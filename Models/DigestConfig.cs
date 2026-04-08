using System.Text.Json.Serialization;

namespace MorningDigest.Models;

public class DigestConfig
{
    [JsonPropertyName("gmail_label")]
    public string GmailLabel { get; set; } = "Develop";

    [JsonPropertyName("relevance_threshold")]
    public int RelevanceThreshold { get; set; } = 6;

    [JsonPropertyName("max_emails")]
    public int MaxEmails { get; set; } = 50;

    [JsonPropertyName("last_run_file")]
    public string LastRunFile { get; set; } = "last-run.txt";

    [JsonPropertyName("model")]
    public string Model { get; set; } = "gemini-2.5-flash";

    [JsonPropertyName("topics")]
    public TopicsConfig Topics { get; set; } = new();
}

public class TopicsConfig
{
    [JsonPropertyName("primary")]
    public List<string> Primary { get; set; } = [];

    [JsonPropertyName("secondary")]
    public List<string> Secondary { get; set; } = [];

    [JsonPropertyName("bonus_sources")]
    public List<string> BonusSources { get; set; } = [];

    [JsonPropertyName("negative_patterns")]
    public List<string> NegativePatterns { get; set; } = [];
}
