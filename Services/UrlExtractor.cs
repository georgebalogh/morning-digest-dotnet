using System.Net;
using System.Text.RegularExpressions;
using System.Web;
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

    // ─── Blacklist (domain/path based — NO query-param entries) ──────────────

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
        // Specific click-tracker domains (not the blunt "click." prefix)
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
        "rs6.net",          // Constant Contact
        "ct.sendo.io",
    ];

    // ─── Query params that contain the real destination URL ──────────────────

    private static readonly string[] RedirectParams =
    [
        "redirectUrl", "redirecturl", "redirect_url",
        "url", "u", "q", "link", "lp", "p", "dest", "destination", "to"
    ];

    // ─── Regexes ──────────────────────────────────────────────────────────────

    // Primary: parse <a href="...">anchor</a>
    private static readonly Regex AnchorRegex = new(
        @"<a\s[^>]*\bhref=[""'](https?://[^""'\s>]+)[""'][^>]*>(.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    // Strip inner HTML tags from anchor text
    private static readonly Regex StripTagsRegex =
        new(@"<[^>]+>", RegexOptions.Compiled);

    // Fallback: raw URL regex for plain-text emails
    private static readonly Regex UrlRegex =
        new(@"https?://[^\s<>""')\]]+", RegexOptions.Compiled);

    private const int MaxLinksPerEmail = 10;
    private const int MinUrlLength = 20;

    // ─── Public entry point ───────────────────────────────────────────────────

    public static async Task<List<LinkItem>> ExtractLinksAsync(EmailItem email)
    {
        var body = email.Body;
        bool isHtml = body.Contains("<a ", StringComparison.OrdinalIgnoreCase)
                   || body.Contains("<A ", StringComparison.Ordinal);

        return isHtml
            ? await ExtractFromHtmlAsync(body, email)
            : ExtractFromPlainText(body, email);
    }

    // ─── HTML path: <a href> parsing ──────────────────────────────────────────

    private static async Task<List<LinkItem>> ExtractFromHtmlAsync(string html, EmailItem email)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new List<(string url, string anchor, string context)>();

        foreach (Match m in AnchorRegex.Matches(html))
        {
            // Step 1: decode &amp; in href
            var rawUrl = m.Groups[1].Value
                .Replace("&amp;", "&")
                .Replace("&lt;", "<")
                .Replace("&gt;", ">")
                .TrimEnd('.', ',', ';', '!', ')', '>', ']');

            // Step 2: unwrap redirect query param (e.g. ?redirectUrl=, ?url=)
            var unwrapped = TryUnwrapRedirectUrl(rawUrl);

            // Step 3: strip query string → clean article URL
            var url = StripQueryString(unwrapped);

            if (url.Length < MinUrlLength) continue;

            // Step 4: blacklist check (domain/path only, no query params)
            if (IsBlacklisted(url)) continue;

            if (!seen.Add(url)) continue;

            // Anchor text
            var rawAnchor = m.Groups[2].Value;
            var anchor = StripTagsRegex.Replace(rawAnchor, " ")
                .Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">")
                .Replace("&nbsp;", " ").Replace("&#39;", "'").Replace("&quot;", "\"")
                .Replace('\n', ' ').Replace('\r', ' ').Trim();
            anchor = Regex.Replace(anchor, @"\s{2,}", " ");

            // Skip pure-image links (no meaningful anchor text)
            if (string.IsNullOrWhiteSpace(anchor)) continue;

            // Context from surrounding HTML
            var idx = m.Index;
            var start = Math.Max(0, idx - 80);
            var end = Math.Min(html.Length, idx + m.Length + 80);
            var rawContext = html[start..end];
            var context = StripTagsRegex.Replace(rawContext, " ")
                .Replace("&amp;", "&").Replace("&nbsp;", " ")
                .Replace('\n', ' ').Replace('\r', ' ').Trim();
            context = Regex.Replace(context, @"\s{2,}", " ");
            if (context.Length > 200) context = context[..200];

            candidates.Add((url, anchor, context));

            if (candidates.Count >= MaxLinksPerEmail) break;
        }

        // Step 5: resolve opaque redirect domains via HTTP HEAD (parallel)
        var resolvedTasks = candidates.Select(async c =>
        {
            var resolvedUrl = IsOpaqueRedirect(c.url)
                ? await ResolveOpaqueRedirectAsync(c.url)
                : c.url;
            return (url: resolvedUrl, c.anchor, c.context);
        });
        var resolved = await Task.WhenAll(resolvedTasks);

        // Deduplicate again after resolution (two different tracker URLs may point to same article)
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

    // ─── Plain text fallback ──────────────────────────────────────────────────

    private static List<LinkItem> ExtractFromPlainText(string text, EmailItem email)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<LinkItem>();

        foreach (Match match in UrlRegex.Matches(text))
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

            if (results.Count >= MaxLinksPerEmail) break;
        }

        return results;
    }

    // ─── URL pipeline helpers ─────────────────────────────────────────────────

    /// Unwrap redirect wrapper URLs by extracting the destination from known query params.
    /// e.g. https://medium.com/m/global-identity-2?redirectUrl=https%3A%2F%2Fmedium.com%2F...
    ///   or https://click.convertkit.com/xyz?url=https%3A%2F%2Farticle.com%2F...
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
                    // Decode percent-encoding (may be double-encoded)
                    var decoded = Uri.UnescapeDataString(candidate);
                    // Double-encoded case
                    if (decoded.Contains('%'))
                        decoded = Uri.UnescapeDataString(decoded);

                    if (decoded.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        return decoded;
                }
            }
        }
        catch { /* malformed URL — return as-is */ }
        return url;
    }

    /// Strip the query string from a URL, keeping only scheme+host+path.
    private static string StripQueryString(string url)
    {
        var qi = url.IndexOf('?');
        if (qi < 0) return url;
        var clean = url[..qi].TrimEnd('/');
        return clean.Length < MinUrlLength ? url : clean;
    }

    /// Check if the URL is an opaque redirect that needs HTTP resolution.
    private static bool IsOpaqueRedirect(string url) =>
        OpaqueRedirectDomains.Any(d => url.Contains(d, StringComparison.OrdinalIgnoreCase));

    /// Follow a single HTTP redirect and return the Location URL.
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
                var resolved = loc.IsAbsoluteUri ? loc.AbsoluteUri : new Uri(new Uri(url), loc).AbsoluteUri;
                // Strip query string from resolved URL too
                return StripQueryString(resolved);
            }
        }
        catch { /* timeout or network error — fall through */ }
        return url;
    }

    /// Check if URL contains any blacklisted substring.
    private static bool IsBlacklisted(string url) =>
        Blacklist.Any(b => url.Contains(b, StringComparison.OrdinalIgnoreCase));
}
