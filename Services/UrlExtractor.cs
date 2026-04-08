using System.Text.RegularExpressions;
using MorningDigest.Models;

namespace MorningDigest.Services;

public static class UrlExtractor
{
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
        "utm_", "ref=", "?s=", "?p=email", "?mc_", "?pk_",
        "go.redirectingat.com", "redirect.", "r.mail.", "click."
    ];

    // HTML <a href="...">anchor text</a> — primary source
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

    public static List<LinkItem> ExtractLinks(EmailItem email)
    {
        var body = email.Body;
        bool isHtml = body.Contains("<a ", StringComparison.OrdinalIgnoreCase)
                   || body.Contains("<A ", StringComparison.Ordinal);

        return isHtml
            ? ExtractFromHtml(body, email)
            : ExtractFromPlainText(body, email);
    }

    // ─── HTML: parse <a href> tags ────────────────────────────────────────────

    private static List<LinkItem> ExtractFromHtml(string html, EmailItem email)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<LinkItem>();

        foreach (Match m in AnchorRegex.Matches(html))
        {
            var url = m.Groups[1].Value.TrimEnd('.', ',', ';', '!', '?', ')', '>', ']');
            var rawAnchor = m.Groups[2].Value;

            if (url.Length < MinUrlLength) continue;
            if (IsBlacklisted(url)) continue;
            if (!seen.Add(url)) continue;

            // Clean anchor: strip inner tags, decode entities, collapse whitespace
            var anchor = StripTagsRegex.Replace(rawAnchor, " ")
                .Replace("&amp;", "&").Replace("&lt;", "<").Replace("&gt;", ">")
                .Replace("&nbsp;", " ").Replace("&#39;", "'").Replace("&quot;", "\"")
                .Replace('\n', ' ').Replace('\r', ' ')
                .Trim();
            anchor = Regex.Replace(anchor, @"\s{2,}", " ");

            // Skip pure-image links (no meaningful anchor text, url is not descriptive)
            if (string.IsNullOrWhiteSpace(anchor)) continue;

            // Context: 120 chars around the full match in the HTML
            var idx = m.Index;
            var start = Math.Max(0, idx - 80);
            var end = Math.Min(html.Length, idx + m.Length + 80);
            var rawContext = html[start..end];
            var context = StripTagsRegex.Replace(rawContext, " ")
                .Replace('\n', ' ').Replace('\r', ' ')
                .Trim();
            context = Regex.Replace(context, @"\s{2,}", " ");
            if (context.Length > 200) context = context[..200];

            results.Add(new LinkItem(
                Url: url,
                Anchor: anchor,
                Context: context,
                Source: $"{email.Subject} ({email.From})"
            ));

            if (results.Count >= MaxLinksPerEmail) break;
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
            var url = match.Value.TrimEnd('.', ',', ';', '!', '?', ')', '>', ']');

            if (url.Length < MinUrlLength) continue;
            if (IsBlacklisted(url)) continue;
            if (!seen.Add(url)) continue;

            var idx = text.IndexOf(url, StringComparison.Ordinal);
            var start = Math.Max(0, idx - 80);
            var end = Math.Min(text.Length, idx + url.Length + 80);
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

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static bool IsBlacklisted(string url) =>
        Blacklist.Any(b => url.Contains(b, StringComparison.OrdinalIgnoreCase));
}
