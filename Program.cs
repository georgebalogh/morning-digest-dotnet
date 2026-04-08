using System.Text.Json;
using MorningDigest.Models;
using MorningDigest.Services;

// ─── Fejléc ───────────────────────────────────────────────────────────────────

Console.WriteLine("═══════════════════════════════════════");
Console.WriteLine("  🌅 Morning Tech Digest");
Console.WriteLine($"  {DateTime.Now:yyyy. MM. dd. HH:mm:ss}");
Console.WriteLine("═══════════════════════════════════════\n");

// ─── Előfeltételek ellenőrzése ────────────────────────────────────────────────

var apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
if (string.IsNullOrWhiteSpace(apiKey))
{
    Console.Error.WriteLine("❌ GEMINI_API_KEY environment változó nincs beállítva.");
    Console.Error.WriteLine("   Állítsd be: [System.Environment]::SetEnvironmentVariable(\"GEMINI_API_KEY\", \"AIza...\", \"User\")");
    Environment.Exit(1);
    return;
}

var credentialsPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    ".gmail-mcp", "credentials.json");

// GmailService ellenőrzi a fájl meglétét és hiba esetén Exit(1)-et hív

// ─── Konfiguráció betöltése ───────────────────────────────────────────────────

var configPath = Path.Combine(AppContext.BaseDirectory, "digest-config.json");
if (!File.Exists(configPath))
{
    Console.Error.WriteLine($"❌ digest-config.json nem található: {configPath}");
    Environment.Exit(1);
    return;
}

var config = JsonSerializer.Deserialize<DigestConfig>(
    File.ReadAllText(configPath),
    new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
    ?? new DigestConfig();

// ─── Last-run logika ──────────────────────────────────────────────────────────

var lastRunPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    ".gmail-mcp",
    config.LastRunFile);

DateTime lastRun = ReadLastRun(lastRunPath);
Console.WriteLine($"⏱  Utolsó futás: {lastRun.ToLocalTime():yyyy. MM. dd. HH:mm:ss}\n");

// ─── Gmail & Gemini inicializálás ─────────────────────────────────────────────

var gmail = GmailService.Create(credentialsPath);
var gemini = new GeminiService(apiKey, config.Model);

var myEmail = await gmail.GetSenderEmailAsync();
Console.WriteLine($"📬 Gmail fiók: {myEmail}\n");

// ─── LÉPÉS 1: Email lista ─────────────────────────────────────────────────────

IList<Google.Apis.Gmail.v1.Data.Message> messages;
try
{
    messages = await gmail.ListEmailsAsync(config.GmailLabel, lastRun, config.MaxEmails);
}
catch (Exception ex)
{
    Console.Error.WriteLine($"❌ [1/6] Email lista hiba: {ex.Message}");
    Environment.Exit(1);
    return;
}

if (messages.Count == 0)
{
    Console.WriteLine("ℹ️  Nincs új email a Develop labelben az utolsó futás óta.");
    WriteLastRun(lastRunPath);
    return;
}

// ─── LÉPÉS 2: Email tartalmak ─────────────────────────────────────────────────

Console.WriteLine($"[2/6] Email tartalmak beolvasása: {messages.Count} db...");
var emails = new List<EmailItem>();
var readOk = 0;

foreach (var msg in messages)
{
    var email = await gmail.ReadEmailAsync(msg.Id);
    if (email != null)
    {
        emails.Add(email);
        readOk++;
    }
}
Console.WriteLine($"[2/6] Beolvasva: {readOk}/{messages.Count}");

// ─── LÉPÉS 3: LLM relevancia értékelés ───────────────────────────────────────

var scores = await gemini.EvaluateRelevanceAsync(emails, config.Topics);
var threshold = config.RelevanceThreshold;

var relevantEmails = emails.Where((_, i) =>
{
    var s = scores.FirstOrDefault(x => x.Index == i);
    return s != null && s.Score >= threshold;
}).ToList();

Console.WriteLine($"[3/6] Releváns emailek (>={threshold} pont): {relevantEmails.Count}/{emails.Count}");

if (relevantEmails.Count == 0)
{
    Console.WriteLine("ℹ️  Nincs releváns email. Üres digest küldése...");
    var emptyHtml = DigestBuilder.BuildHtml(DateTime.Now, emails.Count, 0, [], lastRun, config.GmailLabel);
    await SendSafe(gmail, myEmail, $"🌅 Tech Digest — {DateTime.Now:yyyy-MM-dd} (üres)", emptyHtml);
    WriteLastRun(lastRunPath);
    Console.WriteLine("[6/6] Üres digest elküldve.");
    PrintSummary(emails.Count, 0);
    return;
}

// ─── LÉPÉS 4: URL kinyerés ────────────────────────────────────────────────────

Console.WriteLine("[4/6] URL kinyerés...");
var allLinks = (await Task.WhenAll(
    relevantEmails.Select(UrlExtractor.ExtractLinksAsync)))
    .SelectMany(x => x)
    .ToList();
Console.WriteLine($"[4/6] Kinyert linkek: {allLinks.Count} db");

if (allLinks.Count == 0)
{
    Console.WriteLine("ℹ️  Nincs kinyerhető link a releváns emailekből.");
    var noLinksHtml = DigestBuilder.BuildHtml(DateTime.Now, emails.Count, 0, [], lastRun, config.GmailLabel);
    await SendSafe(gmail, myEmail, $"🌅 Tech Digest — {DateTime.Now:yyyy-MM-dd}", noLinksHtml);
    WriteLastRun(lastRunPath);
    Console.WriteLine("[6/6] Digest elküldve (linkek nélkül).");
    PrintSummary(emails.Count, 0);
    return;
}

// ─── LÉPÉS 5: Link értékelés ──────────────────────────────────────────────────

var linkResults = await gemini.EvaluateLinksAsync(allLinks);

var relevantLinks = linkResults
    .Where(r => r.Score.Score >= threshold)
    .OrderByDescending(r => r.Score.Score)
    .Select(r => new ScoredLink(
        Url: r.Link.Url,
        Anchor: r.Link.Anchor,
        Context: r.Link.Context,
        Source: r.Link.Source,
        Score: r.Score.Score,
        Title: r.Score.Title,
        Description: r.Score.Description))
    .ToList();

Console.WriteLine($"[5/6] Releváns linkek (>={threshold} pont): {relevantLinks.Count}/{allLinks.Count}");

// ─── LÉPÉS 6: Digest összeállítás és küldés ───────────────────────────────────

Console.WriteLine("[6/6] Digest összeállítása és küldése...");

var html = DigestBuilder.BuildHtml(
    date: DateTime.Now,
    emailCount: emails.Count,
    linkCount: relevantLinks.Count,
    items: relevantLinks,
    lastRun: lastRun,
    gmailLabel: config.GmailLabel);

var subject = $"🌅 Tech Digest — {DateTime.Now:yyyy-MM-dd} ({relevantLinks.Count} link)";

try
{
    await gmail.SendDigestAsync(myEmail, subject, html);
    WriteLastRun(lastRunPath);
    Console.WriteLine($"[6/6] ✅ Digest elküldve → {myEmail}");
}
catch (Exception ex)
{
    Console.Error.WriteLine($"❌ Küldési hiba: {ex.Message}");
    Console.WriteLine("\n─── DIGEST (fallback, helyi megjelenítés) ───\n");
    Console.WriteLine(html);
}

PrintSummary(emails.Count, relevantLinks.Count);

// ─── Segédfüggvények ──────────────────────────────────────────────────────────

static DateTime ReadLastRun(string path)
{
    try
    {
        if (File.Exists(path))
        {
            var ts = File.ReadAllText(path).Trim();
            if (DateTime.TryParse(ts, null, System.Globalization.DateTimeStyles.RoundtripKind, out var dt))
                return dt;
        }
    }
    catch { }
    return DateTime.UtcNow.AddHours(-24);
}

static void WriteLastRun(string path)
{
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.WriteAllText(path, DateTime.UtcNow.ToString("O"));
}

static async Task SendSafe(GmailService gmail, string to, string subject, string html)
{
    try
    {
        await gmail.SendDigestAsync(to, subject, html);
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"❌ Küldési hiba: {ex.Message}");
        Console.WriteLine(html);
    }
}

static void PrintSummary(int emailCount, int linkCount)
{
    Console.WriteLine("\n═══════════════════════════════════════");
    Console.WriteLine($"  ✅ Kész | {emailCount} email → {linkCount} releváns link");
    Console.WriteLine("═══════════════════════════════════════\n");
}
