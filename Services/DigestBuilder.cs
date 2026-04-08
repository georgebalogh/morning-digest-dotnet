using System.Globalization;
using MorningDigest.Models;

namespace MorningDigest.Services;

public static class DigestBuilder
{
    private static readonly CultureInfo HuCulture = new("hu-HU");

    public static string BuildHtml(
        DateTime date,
        int emailCount,
        int linkCount,
        List<ScoredLink> items,
        DateTime lastRun,
        string gmailLabel)
    {
        var formattedDate = date.ToString("yyyy. MMMM d., dddd", HuCulture);
        var lastRunStr = lastRun.ToLocalTime().ToString("yyyy. MMMM d. HH:mm", HuCulture);

        var itemsHtml = items.Count == 0
            ? "<p style=\"color:#64748b;text-align:center;padding:32px 0;\">Nincs releváns link a vizsgált időszakban.</p>"
            : string.Concat(items.Select(BuildCard));

        return $"""
            <!DOCTYPE html>
            <html lang="hu">
            <head>
              <meta charset="UTF-8">
              <meta name="viewport" content="width=device-width,initial-scale=1">
              <title>Tech Digest</title>
            </head>
            <body style="margin:0;padding:0;background:#f8fafc;font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,sans-serif;">
              <table width="100%" cellpadding="0" cellspacing="0" style="background:#f8fafc;">
                <tr><td align="center" style="padding:32px 16px;">

                  <table width="620" cellpadding="0" cellspacing="0" style="max-width:620px;width:100%;">

                    <!-- Fejléc -->
                    <tr>
                      <td style="background:#0f172a;border-radius:12px 12px 0 0;padding:32px 40px;">
                        <div style="font-size:28px;margin-bottom:8px;">🌅</div>
                        <h1 style="margin:0 0 6px 0;font-size:24px;font-weight:800;color:#f8fafc;letter-spacing:-0.5px;">
                          Tech Digest
                        </h1>
                        <div style="font-size:14px;color:#94a3b8;">{formattedDate}</div>
                        <div style="margin-top:16px;">
                          <span style="background:#1e293b;color:#94a3b8;font-size:12px;padding:4px 12px;border-radius:20px;">
                            📨 {emailCount} email feldolgozva
                          </span>
                          &nbsp;
                          <span style="background:#1e293b;color:#94a3b8;font-size:12px;padding:4px 12px;border-radius:20px;">
                            🔗 {linkCount} releváns link
                          </span>
                        </div>
                      </td>
                    </tr>

                    <!-- Tartalom -->
                    <tr>
                      <td style="background:#ffffff;padding:40px;border-radius:0 0 12px 12px;box-shadow:0 1px 3px rgba(0,0,0,0.08);">

                        {itemsHtml}

                        <!-- Footer -->
                        <table width="100%" cellpadding="0" cellspacing="0" style="margin-top:8px;padding-top:20px;border-top:1px solid #e2e8f0;">
                          <tr>
                            <td style="font-size:11px;color:#94a3b8;text-align:center;">
                              Digest elkészítve: {DateTime.Now.ToString("yyyy. MM. dd. HH:mm", HuCulture)}<br>
                              Vizsgált időszak: {lastRunStr} óta · Label: <strong>{gmailLabel}</strong><br>
                              <span style="color:#cbd5e1;">Morning Digest · .NET 8 + Gemini</span>
                            </td>
                          </tr>
                        </table>

                      </td>
                    </tr>

                  </table>
                </td></tr>
              </table>
            </body>
            </html>
            """;
    }

    private static string BuildCard(ScoredLink item)
    {
        var color = ScoreColor(item.Score);
        var bar = ScoreBar(item.Score);
        var escapedTitle = System.Net.WebUtility.HtmlEncode(item.Title);
        var escapedSource = System.Net.WebUtility.HtmlEncode(item.Source);
        var escapedDescription = System.Net.WebUtility.HtmlEncode(item.Description);

        return $"""
            <table width="100%" cellpadding="0" cellspacing="0" style="margin-bottom:28px;border-bottom:1px solid #e2e8f0;padding-bottom:28px;">
              <tr>
                <td>
                  <div style="margin-bottom:8px;font-family:monospace;font-size:13px;">
                    {bar}
                    <span style="color:{color};font-weight:700;margin-left:8px;">{item.Score}/10</span>
                  </div>
                  <h2 style="margin:0 0 6px 0;font-size:18px;font-weight:700;line-height:1.3;">
                    <a href="{item.Url}" style="color:#0f172a;text-decoration:none;">{escapedTitle}</a>
                  </h2>
                  <div style="margin-bottom:10px;font-size:12px;color:#64748b;">
                    📧 {escapedSource}
                  </div>
                  <p style="margin:0;font-size:14px;color:#475569;line-height:1.6;border-left:3px solid {color};padding-left:12px;">
                    {escapedDescription}
                  </p>
                  <div style="margin-top:10px;">
                    <a href="{item.Url}" style="display:inline-block;font-size:12px;color:#3b82f6;text-decoration:none;">
                      Megnyitás →
                    </a>
                  </div>
                </td>
              </tr>
            </table>
            """;
    }

    private static string ScoreColor(int score) => score switch
    {
        >= 8 => "#22c55e",
        >= 6 => "#f59e0b",
        _ => "#94a3b8"
    };

    private static string ScoreBar(int score)
    {
        var filled = Math.Clamp(score, 0, 10);
        var empty = 10 - filled;
        var color = ScoreColor(score);
        return $"<span style=\"color:{color};letter-spacing:1px;font-family:monospace;\">" +
               $"{new string('█', filled)}{new string('░', empty)}" +
               $"</span>";
    }
}
