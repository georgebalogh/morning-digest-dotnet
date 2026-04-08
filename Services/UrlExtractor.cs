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

    private static readonly Regex UrlRegex =
        new(@"https?://[^\s<>""')\]]+", RegexOptions.Compiled);

    private const int MaxLinksPerEmail = 10;
    private const int MinUrlLength = 20;

    public static List<LinkItem> ExtractLinks(EmailItem email)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<LinkItem>();

        foreach (Match match in UrlRegex.Matches(email.Body))
        {
            var url = match.Value.TrimEnd('.', ',', ';', '!', '?', ')', '>', ']');

            if (url.Length < MinUrlLength) continue;
            if (Blacklist.Any(b => url.Contains(b, StringComparison.OrdinalIgnoreCase))) continue;
            if (!seen.Add(url)) continue;

            // Szövegkörnyezet kinyerése
            var idx = email.Body.IndexOf(url, StringComparison.Ordinal);
            var start = Math.Max(0, idx - 80);
            var end = Math.Min(email.Body.Length, idx + url.Length + 80);
            var context = email.Body[start..end]
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
}
