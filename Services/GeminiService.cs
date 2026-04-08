using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MorningDigest.Models;

namespace MorningDigest.Services;

public class GeminiService
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _model;
    private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta/models";

    public GeminiService(string apiKey, string model = "gemini-2.5-flash")
    {
        _apiKey = apiKey;
        _model = model;
        _http = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(120)
        };
    }

    // ─── Email relevancia értékelés ───────────────────────────────────────────

    public async Task<List<RelevanceScore>> EvaluateRelevanceAsync(
        List<EmailItem> emails,
        TopicsConfig topics)
    {
        Console.WriteLine($"[3/6] LLM relevancia értékelés: {emails.Count} email...");

        var profileDesc = $"""
            Érdeklődési profil (senior .NET/C# szoftverarchitektúra fejlesztő):

            ELSŐDLEGES témák (9-10 pont):
            {string.Join(", ", topics.Primary)}

            MÁSODLAGOS témák (6-8 pont):
            {string.Join(", ", topics.Secondary)}

            NEGATÍV szűrők (-3 pont vagy kizárás):
            {string.Join(", ", topics.NegativePatterns)}

            BONUS FORRÁS (+1 pont ha az URL tartalmazza):
            {string.Join(", ", topics.BonusSources)}
            """;

        var emailSummaries = string.Join("\n\n", emails.Select((e, i) =>
            $"[{i}] Subject: \"{e.Subject}\" | From: {e.From}\nSnippet: {e.Snippet}"));

        var prompt = profileDesc + "\n\n" +
            "Az alábbi emaileket kell értékelned. Minden emailhez adj egy 0-10 relevanciascore-t.\n\n" +
            emailSummaries + "\n\n" +
            $"Válaszolj KIZÁRÓLAG JSON tömbként, pontosan {emails.Count} elemmel:\n" +
            "[{\"index\":0,\"score\":8,\"reason\":\"1 mondatos indok\"},...]\n\n" +
            "Ne írj semmi mást, csak a JSON tömböt.";

        try
        {
            var text = await CallGeminiAsync(prompt);
            var match = Regex.Match(text, @"\[[\s\S]*\]");
            if (!match.Success) throw new InvalidOperationException("Nem JSON válasz");

            var scores = JsonSerializer.Deserialize<List<RelevanceScore>>(match.Value)
                ?? throw new InvalidOperationException("JSON deszializáció sikertelen");
            return scores;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[3/6] WARN: LLM válasz parse hiba, minden email bekerül: {ex.Message}");
            return emails.Select((_, i) => new RelevanceScore(i, 5,
                "Parse hiba, manuális ellenőrzés szükséges")).ToList();
        }
    }

    // ─── Link értékelés ───────────────────────────────────────────────────────

    public async Task<List<(LinkItem Link, LinkScore Score)>> EvaluateLinksAsync(
        List<LinkItem> links)
    {
        if (links.Count == 0) return [];

        const int batchSize = 25;
        var allResults = new List<(LinkItem Link, LinkScore Score)>();

        for (int offset = 0; offset < links.Count; offset += batchSize)
        {
            var batch = links.Skip(offset).Take(batchSize).ToList();
            Console.WriteLine($"[5/6] Link értékelés: {batch.Count} link (offset: {offset})...");

            var batchResults = await EvaluateLinkBatchAsync(batch, offset);
            allResults.AddRange(batchResults);
        }

        return allResults;
    }

    private async Task<List<(LinkItem Link, LinkScore Score)>> EvaluateLinkBatchAsync(
        List<LinkItem> batch,
        int offset)
    {
        var profileDesc = "Senior .NET/C# szoftverarchitektúra fejlesztő, aki érdeklődik: DDD, CQRS, " +
            "Event Sourcing, Microservices, Vertical Slice, MediatR, MassTransit, gRPC, " +
            "SignalR, Prompt Engineering, MCP, agentic workflows, Technical Debt, Refactoring iránt.";

        var linkList = string.Join("\n\n", batch.Select((l, i) =>
            $"[{i}] URL: {l.Url}\nAnchor: \"{l.Anchor}\"\nKontextus: \"{l.Context}\""));

        var prompt = profileDesc + "\n\n" +
            "Értékeld az alábbi linkeket 0-10 skálán relevancia szerint.\n" +
            "Ha az URL-ből nem derül ki a tartalom, az anchor text és kontextus alapján becsüld.\n\n" +
            linkList + "\n\n" +
            $"Válaszolj KIZÁRÓLAG JSON tömbként ({batch.Count} elem):\n" +
            "[{\"index\":0,\"score\":8,\"title\":\"Cikk rövid címe\",\"description\":\"1-2 mondatos leírás magyarul: mit tartalmaz és miért érdemes elolvasni\"}]\n\n" +
            "Csak a JSON tömb, semmi más.";

        try
        {
            var text = await CallGeminiAsync(prompt);
            var match = Regex.Match(text, @"\[[\s\S]*\]");
            if (!match.Success) throw new InvalidOperationException("Nem JSON válasz");

            var scores = JsonSerializer.Deserialize<List<LinkScore>>(match.Value)
                ?? throw new InvalidOperationException("JSON deszializáció sikertelen");

            return scores.Select(s => (
                Link: batch[s.Index],
                Score: new LinkScore(s.Index + offset, s.Score, s.Title, s.Description)
            )).ToList();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[5/6] WARN: Link értékelés parse hiba: {ex.Message}");
            return batch.Select((l, i) => (
                Link: l,
                Score: new LinkScore(
                    Index: i + offset,
                    Score: 5,
                    Title: l.Url.Length > 60 ? l.Url[..60] : l.Url,
                    Description: "Automatikus értékelés sikertelen, manuális ellenőrzés szükséges.")
            )).ToList();
        }
    }

    // ─── HTTP hívás ───────────────────────────────────────────────────────────

    private async Task<string> CallGeminiAsync(string prompt)
    {
        var endpoint = $"{BaseUrl}/{_model}:generateContent?key={_apiKey}";

        var requestBody = new
        {
            contents = new[]
            {
                new { parts = new[] { new { text = prompt } } }
            }
        };

        var json = JsonSerializer.Serialize(requestBody);
        var content = new StringContent(json, Encoding.UTF8, "application/json");

        var response = await _http.PostAsync(endpoint, content);
        var responseBody = await response.Content.ReadAsStringAsync();

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Gemini API hiba {(int)response.StatusCode}: {responseBody[..Math.Min(200, responseBody.Length)]}");

        // Válasz struktúra: { "candidates": [{ "content": { "parts": [{ "text": "..." }] } }] }
        using var doc = JsonDocument.Parse(responseBody);
        var text = doc.RootElement
            .GetProperty("candidates")[0]
            .GetProperty("content")
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString() ?? string.Empty;

        return text.Trim();
    }
}
