using System.Text.Json.Serialization;

namespace MorningDigest.Models;

public record RelevanceScore(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("score")] int Score,
    [property: JsonPropertyName("reason")] string Reason
);

public record LinkScore(
    [property: JsonPropertyName("index")] int Index,
    [property: JsonPropertyName("score")] int Score,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("description")] string Description
);

public record ScoredLink(
    string Url,
    string Anchor,
    string Context,
    string Source,
    int Score,
    string Title,
    string Description
);
