# Morning Tech Digest

Automatikusan futó .NET 8 Console alkalmazás, amely bejelentkezéskor összegyűjti a Gmail `Develop` label emailjeit, LLM-alapú relevancia-szűréssel kiválasztja a tech tartalmakat, kinyeri a cikkekre mutató linkeket, és HTML digest emailt küld saját Gmail-fiókra.

## Működés

1. **Email lista** — Gmail `Develop` label lekérése az utolsó futás óta (`-subject:"Tech Digest"` önkizárással)
2. **Tartalom beolvasás** — teljes MIME parse, HTML body prioritással
3. **Relevancia szűrés** — Gemini 2.5 Flash LLM értékeli az emaileket (0–10 skála, küszöb: ≥6)
4. **Link kinyerés** — AngleSharp DOM fingerprint-alapú strukturális processzor (4 fázis)
5. **Link értékelés** — Gemini LLM értékeli és rangsorolja a linkeket
6. **Digest küldés** — responsive HTML email, score bar + cikk cím + leírás

## Követelmények

- Windows 10/11 x64
- .NET 8.0 SDK (build) vagy self-contained futtatás (nem szükséges)
- `GEMINI_API_KEY` User szintű environment változó
- `%USERPROFILE%\.gmail-mcp\credentials.json` — OAuth2 token
- `%USERPROFILE%\.gmail-mcp\gcp-oauth.keys.json` — GCP `client_id`, `client_secret`

## Telepítés és futtatás

### Kézi futtatás

```powershell
$env:GEMINI_API_KEY = [System.Environment]::GetEnvironmentVariable("GEMINI_API_KEY", "User")
dotnet run -c Release
```

### Build

```powershell
dotnet build -c Release
```

### Self-contained publish (win-x64)

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -o ./publish
```

### Task Scheduler regisztráció

```powershell
schtasks /create /xml task-scheduler.xml /tn "MorningDigestNet"
```

Bejelentkezéskor automatikusan elindul (30 másodperc késleltetéssel).

## Konfiguráció

`digest-config.json` — minden build mellé kimásolódik:

```json
{
  "gmail_label": "Develop",
  "relevance_threshold": 6,
  "max_emails": 50,
  "model": "gemini-2.5-flash",
  "topics": {
    "primary": [".NET", "C#", "DDD", "CQRS", "Event Sourcing", ...],
    "secondary": ["Prompt Engineering", "MCP", "Agentic", ...],
    "bonus_sources": ["martinfowler.com", "ardalis.com", ...],
    "negative_patterns": ["we're hiring", "introduction to", ...]
  }
}
```

## Projektstruktúra

```
MorningDigest.Net/
├── Program.cs                  # 6 lépéses pipeline
├── MorningDigest.csproj        # net8.0, NuGet függőségek
├── digest-config.json          # Runtime konfiguráció
├── task-scheduler.xml          # Task Scheduler definíció
├── Models/
│   ├── DigestConfig.cs
│   ├── EmailItem.cs
│   ├── LinkItem.cs
│   └── RelevanceScore.cs
├── Services/
│   ├── GmailService.cs         # Gmail OAuth2 + API
│   ├── GeminiService.cs        # Gemini REST API
│   ├── UrlExtractor.cs         # AngleSharp link kinyerő
│   └── DigestBuilder.cs        # HTML digest builder
└── docs/
    ├── specification.md        # IT specifikáció
    └── specification.pdf
```

## NuGet függőségek

| Csomag | Verzió |
|--------|--------|
| `Google.Apis.Gmail.v1` | 1.73.0.4029 |
| `AngleSharp` | 1.4.0 |

## CI/CD

GitHub Actions — minden push-ra build + self-contained win-x64 artifact (`MorningDigest-win-x64`, 30 nap).

## Dokumentáció

Részletes IT specifikáció: [`docs/specification.pdf`](docs/specification.pdf)
