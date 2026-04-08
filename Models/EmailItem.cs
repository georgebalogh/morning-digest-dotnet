namespace MorningDigest.Models;

public record EmailItem(
    string Id,
    string Subject,
    string From,
    string Date,
    string Snippet,
    string Body
);
