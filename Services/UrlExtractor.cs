using System.Web;
using System.Text.RegularExpressions;
using AngleSharp;
using AngleSharp.Dom;
using MorningDigest.Models;

namespace MorningDigest.Services;

public static class UrlExtractor
{
    // ─── HTTP client for opaque redirect resolution ───────────────────────────

    private static readonly HttpClient Http = new(
        new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    // ─── Blacklist (domain/path based — no query-param entries) ──────────────

    private static readonly string[] Blacklist =
    [
        "unsubscribe", "tracking", "pixel", "open.php", "click.php",
        "email.mg.", "mailchi.mp/track", ".gif?", ".png?", "mg.mail",
        "list-manage.com", "mandrillapp.com", "sendgrid.net/wf/",
        "r.emaillink.", "links.iterable.com", "mailchimp.com",
        "campaign-archive", "view-this-email", "forward-to-a-friend",
        "account.google.com", "accounts.google.com", "policies.google.com",
        "support.google.com", "mail.google.com", "google.com/intl",
        "twitter.com/intent", "linkedin.com/shareArticle", "facebook.com/sharer",
        "t.co/", "bit.ly/", "ow.ly/", "buff.ly/", "dlvr.it/",
        "/cdn-cgi/", "images.", "img.", "static.", "assets.",
        ".jpg", ".jpeg", ".png", ".gif", ".svg", ".ico", ".css", ".js",
        "go.redirectingat.com", "r.mail.",
        "click.convertkit-mail.com", "click.pstmrk.it", "click.mailersend.com",
        "click.e.hubspot.com", "click.mlsend.com"
    ];

    // ─── Opaque redirect domains — resolved via HTTP HEAD ────────────────────

    private static readonly string[] OpaqueRedirectDomains =
    [
        "link.mail.beehiiv.com",
        "beehiiv.com/ss/",
        "link.hubspot.com",
        "ep.sendgrid.net",
        "app.mailerlite.com",
        "mailchi.mp",
        "rs6.net",
        "ct.sendo.io",
    ];

    // ─── Query params that contain the real destination URL ──────────────────

    private static readonly string[] RedirectParams =
    [
        "redirectUrl", "redirecturl", "redirect_url",
        "url", "u", "q", "link", "lp", "p", "dest", "destination", "to"
    ];

    // ─── Fallback regex for plain-text emails ─────────────────────────────────

    private static readonly Regex PlainUrlRegex =
        new(@"https?://[^\s<>""')\]]+", RegexOptions.Compiled);

    private static readonly Regex StripTagsRegex =
        new(@"<[^>]+>", RegexOptions.Compiled);

    private const int MinSequenceLength = 3;
    private const int MaxLinksPerContainer = 2;
    private const int MinUrlLength = 20;

    // ─── Public entry point ───────────────────────────────────────────────────

    public static async Task<List<LinkItem>> ExtractLinksAsync(EmailItem email)
    {
        var body = email.Body;

        // Plain-text fallback: no anchor tags present
        if (!body.Contains("<a ", StringComparison.OrdinalIgnoreCase) &&
            !body.Contains("<A ", StringComparison.Ordinal))
            return ExtractFromPlainText(body, email);

        return await ExtractFromHtmlAsync(body, email);
    }

    // ─── Phase 1–4: Structural HTML processor ────────────────────────────────

    private static async Task<List<LinkItem>> ExtractFromHtmlAsync(string html, EmailItem email)
    {
        // Phase 1: parse DOM with AngleSharp
        var config = Configuration.Default;
        using var browsingContext = BrowsingContext.New(config);
        using var document = await browsingContext.OpenAsync(req => req.Content(html));

        // Phase 2 & 3: find the longest repeating-fingerprint sequence in the DOM
        var winningContainers = FindArticleContainers(document.Body ?? document.DocumentElement);

        // No repeating structure found → drop this email
        if (winningContainers.Count == 0)
        {
            Console.WriteLine($"  ⚠  Nem található cikk-struktúra: {email.Subject}");
            return [];
        }

        // Phase 4: extract + clean URLs from winning containers
        var candidates = ExtractUrlsFromContainers(winningContainers, email);

        // Resolve opaque redirect domains via HTTP HEAD (parallel)
        var resolvedTasks = candidates.Select(async c =>
        {
            var resolvedUrl = IsOpaqueRedirect(c.url)
                ? await ResolveOpaqueRedirectAsync(c.url)
                : c.url;
            return (url: resolvedUrl, c.anchor, c.context);
        });
        var resolved = await Task.WhenAll(resolvedTasks);

        // Final dedup after resolution
        var finalSeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<LinkItem>();
        foreach (var (url, anchor, context) in resolved)
        {
            if (!finalSeen.Add(url)) continue;
            results.Add(new LinkItem(
                Url: url,
                Anchor: anchor,
                Context: context,
                Source: $"{email.Subject} ({email.From})"
            ));
        }

        return results;
    }

    // ─── Phase 2+3: DOM traversal + fingerprint sequence detection ───────────

    private static List<IElement> FindArticleContainers(IElement root)
    {
        var bestSequence = new List<IElement>();

        // Recursively walk every element in the tree
        WalkNode(root, bestSequence);

        return bestSequence;
    }

    private static void WalkNode(IElement node, List<IElement> bestSequence)
    {
        // Check this node's children for repeating fingerprint sequences
        var children = node.Children.ToList();
        if (children.Count >= MinSequenceLength)
        {
            var candidate = FindBestSequence(children);
            if (candidate.Count > bestSequence.Count)
            {
                bestSequence.Clear();
                bestSequence.AddRange(candidate);
                // Found a good sequence at this level — don't recurse into these children.
                // Recurse only into siblings that are NOT part of the winning sequence.
                var sequenceSet = candidate.ToHashSet();
                foreach (var child in children.Where(c => !sequenceSet.Contains(c)))
                    WalkNode(child, bestSequence);
                return;
            }
        }

        // No sequence found at this level — recurse into all children
        foreach (var child in children)
            WalkNode(child, bestSequence);
    }

    private static List<IElement> FindBestSequence(List<IElement> siblings)
    {
        // Compute fingerprint for each sibling
        var fingerprints = siblings.Select(Fingerprint).ToList();

        var best = new List<IElement>();
        var current = new List<IElement>();
        string? currentFp = null;
        int noiseCount = 0;

        for (int i = 0; i < siblings.Count; i++)
        {
            var fp = fingerprints[i];

            if (currentFp == null)
            {
                // Start new sequence
                currentFp = fp;
                current.Add(siblings[i]);
                noiseCount = 0;
            }
            else if (fp == currentFp)
            {
                // Matching fingerprint — add to sequence, reset noise
                current.Add(siblings[i]);
                noiseCount = 0;
            }
            else if (noiseCount == 0)
            {
                // First non-matching element — treat as noise (spacer), keep going
                noiseCount++;
                // Don't add the noise element to the sequence
            }
            else
            {
                // Second non-matching element — sequence broken
                if (current.Count >= MinSequenceLength && current.Count > best.Count)
                    best = new List<IElement>(current);

                // Start fresh from current element
                currentFp = fp;
                current = [siblings[i]];
                noiseCount = 0;
            }
        }

        // Check final sequence
        if (current.Count >= MinSequenceLength && current.Count > best.Count)
            best = new List<IElement>(current);

        return best;
    }

    /// Fingerprint = "PARENTTAG:CHILD1|CHILD2|..." using direct element children only.
    private static string Fingerprint(IElement el)
    {
        var childTags = el.Children
            .Select(c => c.TagName.ToUpperInvariant())
            .ToList();

        return childTags.Count == 0
            ? el.TagName.ToUpperInvariant()
            : $"{el.TagName.ToUpperInvariant()}:{string.Join("|", childTags)}";
    }

    // ─── Phase 4: extract URLs from winning containers ────────────────────────

    private static List<(string url, string anchor, string context)> ExtractUrlsFromContainers(
        List<IElement> containers, EmailItem email)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<(string url, string anchor, string context)>();

        foreach (var container in containers)
        {
            int addedForContainer = 0;

            foreach (var anchor in container.QuerySelectorAll("a[href]"))
            {
                if (addedForContainer >= MaxLinksPerContainer) break;

                var rawHref = anchor.GetAttribute("href") ?? string.Empty;

                // Decode HTML entities in href
                rawHref = rawHref
                    .Replace("&amp;", "&")
                    .Replace("&lt;", "<")
                    .Replace("&gt;", ">")
                    .TrimEnd('.', ',', ';', '!', ')', '>', ']');

                if (!rawHref.StartsWith("http", StringComparison.OrdinalIgnoreCase)) continue;

                // Unwrap redirect params, strip query string
                var unwrapped = TryUnwrapRedirectUrl(rawHref);
                var url = StripQueryString(unwrapped);

                if (url.Length < MinUrlLength) continue;
                if (IsBlacklisted(url)) continue;
                if (!seen.Add(url)) continue;

                // Anchor text from element
                var anchorText = anchor.TextContent
                    .Replace('\n', ' ').Replace('\r', ' ').Trim();
                anchorText = Regex.Replace(anchorText, @"\s{2,}", " ");

                // Context: text content of the whole container
                var context = container.TextContent
                    .Replace('\n', ' ').Replace('\r', ' ').Trim();
                context = Regex.Replace(context, @"\s{2,}", " ");
                if (context.Length > 200) context = context[..200];

                results.Add((url, anchorText, context));
                addedForContainer++;
            }
        }

        return results;
    }

    // ─── Plain text fallback ──────────────────────────────────────────────────

    private static List<LinkItem> ExtractFromPlainText(string text, EmailItem email)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<LinkItem>();

        foreach (Match match in PlainUrlRegex.Matches(text))
        {
            var rawUrl = match.Value.TrimEnd('.', ',', ';', '!', '?', ')', '>', ']');
            var unwrapped = TryUnwrapRedirectUrl(rawUrl);
            var url = StripQueryString(unwrapped);

            if (url.Length < MinUrlLength) continue;
            if (IsBlacklisted(url)) continue;
            if (!seen.Add(url)) continue;

            var idx = text.IndexOf(rawUrl, StringComparison.Ordinal);
            var start = Math.Max(0, idx - 80);
            var end = Math.Min(text.Length, idx + rawUrl.Length + 80);
            var context = text[start..end]
                .Replace('\n', ' ').Replace('\r', ' ')
                .Replace("  ", " ").Trim();
            if (context.Length > 200) context = context[..200];

            results.Add(new LinkItem(
                Url: url,
                Anchor: string.Empty,
                Context: context,
                Source: $"{email.Subject} ({email.From})"
            ));
        }

        return results;
    }

    // ─── URL pipeline helpers ─────────────────────────────────────────────────

    private static string TryUnwrapRedirectUrl(string url)
    {
        try
        {
            var qi = url.IndexOf('?');
            if (qi < 0) return url;

            var query = HttpUtility.ParseQueryString(url[(qi + 1)..]);
            foreach (var param in RedirectParams)
            {
                var candidate = query[param];
                if (!string.IsNullOrEmpty(candidate))
                {
                    var decoded = Uri.UnescapeDataString(candidate);
                    if (decoded.Contains('%'))
                        decoded = Uri.UnescapeDataString(decoded);
                    if (decoded.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        return decoded;
                }
            }
        }
        catch { }
        return url;
    }

    private static string StripQueryString(string url)
    {
        var qi = url.IndexOf('?');
        if (qi < 0) return url;
        var clean = url[..qi].TrimEnd('/');
        return clean.Length < MinUrlLength ? url : clean;
    }

    private static bool IsOpaqueRedirect(string url) =>
        OpaqueRedirectDomains.Any(d => url.Contains(d, StringComparison.OrdinalIgnoreCase));

    private static async Task<string> ResolveOpaqueRedirectAsync(string url)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, url);
            req.Headers.TryAddWithoutValidation("User-Agent",
                "Mozilla/5.0 (compatible; MorningDigest/1.0)");
            using var resp = await Http.SendAsync(req);

            if (resp.Headers.Location is { } loc)
            {
                var resolved = loc.IsAbsoluteUri
                    ? loc.AbsoluteUri
                    : new Uri(new Uri(url), loc).AbsoluteUri;
                return StripQueryString(resolved);
            }
        }
        catch { }
        return url;
    }

    private static bool IsBlacklisted(string url) =>
        Blacklist.Any(b => url.Contains(b, StringComparison.OrdinalIgnoreCase));
}
