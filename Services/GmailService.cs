using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Auth.OAuth2.Flows;
using Google.Apis.Auth.OAuth2.Responses;
using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
using Google.Apis.Services;
using MorningDigest.Models;

namespace MorningDigest.Services;

public class GmailService
{
    private readonly Google.Apis.Gmail.v1.GmailService _gmail;

    private GmailService(Google.Apis.Gmail.v1.GmailService gmail)
    {
        _gmail = gmail;
    }

    // ─── Auth ────────────────────────────────────────────────────────────────

    public static GmailService Create(string credentialsPath)
    {
        if (!File.Exists(credentialsPath))
        {
            Console.Error.WriteLine($"❌ Gmail credentials nem találhatók: {credentialsPath}");
            Console.Error.WriteLine("   Futtasd a Gmail auth flow-t a credentials.json generálásához.");
            Environment.Exit(1);
        }

        var raw = JsonSerializer.Deserialize<CredentialsFile>(
            File.ReadAllText(credentialsPath),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException("credentials.json parse sikertelen");

        if (string.IsNullOrWhiteSpace(raw.ClientId))
        {
            Console.Error.WriteLine("❌ credentials.json: client_id hiányzik");
            Environment.Exit(1);
        }
        if (string.IsNullOrWhiteSpace(raw.ClientSecret))
        {
            Console.Error.WriteLine("❌ credentials.json: client_secret hiányzik");
            Environment.Exit(1);
        }
        if (string.IsNullOrWhiteSpace(raw.RefreshToken))
        {
            Console.Error.WriteLine("❌ credentials.json: refresh_token hiányzik");
            Environment.Exit(1);
        }

        // Headless OAuth2 — NEM GoogleWebAuthorizationBroker (böngészőt nyitna)
        var flow = new GoogleAuthorizationCodeFlow(
            new GoogleAuthorizationCodeFlow.Initializer
            {
                ClientSecrets = new ClientSecrets
                {
                    ClientId = raw.ClientId,
                    ClientSecret = raw.ClientSecret
                },
                Scopes = new[]
                {
                    Google.Apis.Gmail.v1.GmailService.Scope.GmailReadonly,
                    Google.Apis.Gmail.v1.GmailService.Scope.GmailSend
                }
            });

        var tokenResponse = new TokenResponse
        {
            RefreshToken = raw.RefreshToken,
            AccessToken = raw.AccessToken
        };

        var credential = new UserCredential(flow, "user", tokenResponse);

        var gmail = new Google.Apis.Gmail.v1.GmailService(new BaseClientService.Initializer
        {
            HttpClientInitializer = credential,
            ApplicationName = "MorningDigest"
        });

        return new GmailService(gmail);
    }

    // ─── Sender email ─────────────────────────────────────────────────────────

    public async Task<string> GetSenderEmailAsync()
    {
        var profile = await _gmail.Users.GetProfile("me").ExecuteAsync();
        return profile.EmailAddress;
    }

    // ─── List emails ──────────────────────────────────────────────────────────

    public async Task<IList<Message>> ListEmailsAsync(string labelName, DateTime afterDate, int maxResults)
    {
        var localDate = afterDate.ToLocalTime();
        var afterStr = $"{localDate.Year}/{localDate.Month:D2}/{localDate.Day:D2}";
        var query = $"label:{labelName} after:{afterStr}";
        Console.WriteLine($"[1/6] Email lista lekérése: \"{query}\"");

        var request = _gmail.Users.Messages.List("me");
        request.Q = query;
        request.MaxResults = maxResults;

        var response = await request.ExecuteAsync();
        var messages = response.Messages ?? new List<Message>();

        Console.WriteLine($"[1/6] Találat: {messages.Count} email");
        return messages;
    }

    // ─── Read email ───────────────────────────────────────────────────────────

    public async Task<EmailItem?> ReadEmailAsync(string messageId)
    {
        try
        {
            var request = _gmail.Users.Messages.Get("me", messageId);
            request.Format = UsersResource.MessagesResource.GetRequest.FormatEnum.Full;
            var msg = await request.ExecuteAsync();

            var headers = msg.Payload?.Headers ?? new List<MessagePartHeader>();
            return new EmailItem(
                Id: messageId,
                Subject: GetHeader(headers, "subject") ?? "(nincs tárgy)",
                From: GetHeader(headers, "from") ?? "",
                Date: GetHeader(headers, "date") ?? "",
                Snippet: msg.Snippet ?? "",
                Body: ExtractBody(msg.Payload)
            );
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ⚠️  Email {messageId} skip: {ex.Message}");
            return null;
        }
    }

    // ─── Send digest ──────────────────────────────────────────────────────────

    public async Task SendDigestAsync(string to, string subject, string htmlBody)
    {
        var subjectBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(subject));
        var bodyBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(htmlBody));

        var rawEmail = string.Join("\r\n", new[]
        {
            $"To: {to}",
            $"From: {to}",
            $"Subject: =?UTF-8?B?{subjectBase64}?=",
            "MIME-Version: 1.0",
            "Content-Type: text/html; charset=utf-8",
            "Content-Transfer-Encoding: base64",
            "",
            bodyBase64
        });

        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(rawEmail))
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');

        await _gmail.Users.Messages.Send(
            new Message { Raw = encoded },
            "me"
        ).ExecuteAsync();
    }

    // ─── Helpers ──────────────────────────────────────────────────────────────

    private static string? GetHeader(IList<MessagePartHeader> headers, string name)
        => headers.FirstOrDefault(h =>
            string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase))?.Value;

    private static string ExtractBody(MessagePart? payload)
    {
        if (payload == null) return string.Empty;

        // Közvetlen body
        if (!string.IsNullOrEmpty(payload.Body?.Data))
            return DecodeBase64Url(payload.Body.Data);

        if (payload.Parts == null) return string.Empty;

        // 1. prioritás: text/plain
        foreach (var part in payload.Parts)
        {
            if (part.MimeType == "text/plain" && !string.IsNullOrEmpty(part.Body?.Data))
                return DecodeBase64Url(part.Body.Data);
        }

        // 2. prioritás: text/html, majd nested parts
        foreach (var part in payload.Parts)
        {
            if (part.MimeType == "text/html" && !string.IsNullOrEmpty(part.Body?.Data))
            {
                var html = DecodeBase64Url(part.Body.Data);
                return Regex.Replace(html, "<[^>]+>", " ")
                            .Replace("\n", " ").Replace("\r", " ")
                            .Replace("  ", " ").Trim();
            }

            if (part.Parts != null)
            {
                var nested = ExtractBody(part);
                if (!string.IsNullOrEmpty(nested)) return nested;
            }
        }

        return string.Empty;
    }

    private static string DecodeBase64Url(string data)
    {
        if (string.IsNullOrEmpty(data)) return string.Empty;
        var base64 = data.Replace('-', '+').Replace('_', '/');
        switch (base64.Length % 4)
        {
            case 2: base64 += "=="; break;
            case 3: base64 += "="; break;
        }
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(base64));
        }
        catch
        {
            return string.Empty;
        }
    }

    // ─── Credentials model ────────────────────────────────────────────────────

    private class CredentialsFile
    {
        [JsonPropertyName("client_id")]
        public string? ClientId { get; set; }

        [JsonPropertyName("client_secret")]
        public string? ClientSecret { get; set; }

        [JsonPropertyName("refresh_token")]
        public string? RefreshToken { get; set; }

        [JsonPropertyName("access_token")]
        public string? AccessToken { get; set; }
    }
}
