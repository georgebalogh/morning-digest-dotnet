---
title: "Morning Tech Digest — IT Specifikáció"
pdf_options:
  format: A4
  margin: "20mm 18mm"
  printBackground: true
---

# Morning Tech Digest — IT Specifikáció

**Projekt:** MorningDigest.Net  
**Verzió:** 1.0  
**Dátum:** 2026-04-08  
**Repository:** https://github.com/georgebalogh/morning-digest-dotnet  
**Platform:** Windows 10/11 x64  
**Runtime:** .NET 8.0 LTS (self-contained)

---

## 1. Áttekintés

A **Morning Tech Digest** egy automatikusan futó .NET 8 Console alkalmazás, amely:

1. Bejelentkezéskor automatikusan elindul (Windows Task Scheduler)
2. Beolvassa a Gmail `Develop` label emailjeit az utolsó futás óta
3. LLM-alapú relevancia-szűréssel kiválasztja a tech szempontból releváns emaileket
4. Strukturális HTML-feldolgozóval kinyeri a cikkekre mutató linkeket
5. A linkeket ismét LLM-mel értékeli és rangsorolja
6. HTML digest emailt állít össze és elküld saját Gmail-fiókra

---

## 2. Rendszer-architektúra

```
┌─────────────────────────────────────────────────────────┐
│                    Program.cs (Pipeline)                 │
│                                                          │
│  [1] ListEmailsAsync                                     │
│  [2] ReadEmailAsync (parallel)                           │
│  [3] EvaluateRelevanceAsync  ──► GeminiService           │
│  [4] ExtractLinksAsync       ──► UrlExtractor (AngleSharp│
│  [5] EvaluateLinksAsync      ──► GeminiService           │
│  [6] BuildHtml + SendDigestAsync                         │
└───────────┬───────────────────────────┬─────────────────┘
            │                           │
    ┌───────▼──────┐           ┌────────▼────────┐
    │  GmailService│           │  GeminiService  │
    │  (Gmail API) │           │  (HTTP REST)    │
    └───────┬──────┘           └────────┬────────┘
            │                           │
    Gmail API v1               Gemini 2.5 Flash API
    (OAuth2 headless)          (generativelanguage.googleapis.com)
```

---

## 3. Projektstruktúra

```
MorningDigest.Net/
├── Program.cs                    # 6 lépéses pipeline, belépési pont
├── MorningDigest.csproj          # net8.0, NuGet függőségek
├── digest-config.json            # Runtime konfiguráció
├── task-scheduler.xml            # Windows Task Scheduler definíció
├── .gitignore
├── .github/
│   └── workflows/
│       └── build.yml             # CI/CD: build + self-contained publish
├── Models/
│   ├── DigestConfig.cs           # Konfiguráció modell
│   ├── EmailItem.cs              # Email adatmodell
│   ├── LinkItem.cs               # Kinyert link modell
│   └── RelevanceScore.cs        # LLM értékelési modellek
└── Services/
    ├── GmailService.cs           # Gmail OAuth2 auth + API műveletek
    ├── GeminiService.cs          # Gemini API integráció (HTTP REST)
    ├── UrlExtractor.cs           # AngleSharp strukturális link-kinyerő
    └── DigestBuilder.cs          # HTML digest összeállító
```

---

## 4. Futási pipeline részletesen

### 4.1 Lépés 1 — Email lista lekérése

- **Osztály:** `GmailService.ListEmailsAsync()`
- **Gmail query:** `label:Develop after:YYYY/MM/DD -subject:"Tech Digest"`
- **Max:** 50 email / futás (`max_emails` konfigból)
- **Önfeldolgozás megelőzése:** `-subject:"Tech Digest"` kizárja a saját digest emaileket

### 4.2 Lépés 2 — Email tartalmak beolvasása

- **Osztály:** `GmailService.ReadEmailAsync()`
- **Format:** `Full` (teljes MIME struktúra)
- **Body prioritás:** `text/html` > `text/plain` > nested parts
- **Dekódolás:** Base64URL → UTF-8 string

### 4.3 Lépés 3 — LLM relevancia értékelés

- **Osztály:** `GeminiService.EvaluateRelevanceAsync()`
- **Model:** `gemini-2.5-flash`
- **Input:** email tárgy + feladó + snippet
- **Output:** JSON tömb `[{index, score, reason}]` — 0–10 skála
- **Küszöb:** `relevance_threshold = 6` (konfigból)
- **Érdeklődési profil:** `digest-config.json` → `topics.primary/secondary/negative_patterns/bonus_sources`

### 4.4 Lépés 4 — URL kinyerés (AngleSharp strukturális processzor)

- **Osztály:** `UrlExtractor.ExtractLinksAsync()`
- **Futás:** `Task.WhenAll` — párhuzamos, emailenként

**4 fázis:**

- **Phase 1 — DOM parse:** AngleSharp beolvassa a nyers HTML-t → teljes DOM fa
- **Phase 2 — Fingerprinting:** Rekurzív DOM bejárás; minden elem fingerprint-je = `"TAG:CHILD1|CHILD2|..."` (közvetlen element gyerekek, text node-ok kihagyva)
- **Phase 3 — Sorozatdetektálás:** Leghosszabb ≥3 elemű, azonos fingerprint-ű sorozat keresése (max 1 zaj-elem tolerancia); ha nincs találat → email eldobva
- **Phase 4 — URL kinyerés:** A winning konténerekből: `&` decode → redirect unwrap → query-string strip → blacklist → article-filter → max 2 link/konténer

**Article URL szűrő szabályok (`IsArticleUrl()`):**

- Legalább 5 karakter hosszú path szegmens szükséges
- Legalább 2 path szegmens szükséges (domain homepagek kizárva)
- `/@username` (1 szegmens) → ❌ profil oldal
- `/@username/article-slug` (2 szegmens) → ✓ cikk
- Blokkolt prefixek: `/tag/`, `/topic/`, `/category/`, `/membership`, `/subscribe`, `/about`, `/search`, `/newsletter`, `/author/`, `/page/`, `/feed`, `/rss`, `/login`, `/signup`, `/register`, `/profile/`

**Opaque redirect feloldás (HTTP HEAD):**

Beehiiv, HubSpot, SendGrid, Mailerlite stb. redirect domaineket feloldja a valódi cél URL-re.

**Blacklist:** tracking pixelek, unsubscribe linkek, social share gombok, CDN asset URL-ek.

### 4.5 Lépés 5 — Link értékelés

- **Osztály:** `GeminiService.EvaluateLinksAsync()`
- **Batch méret:** 25 link / Gemini kérés
- **Input:** URL + anchor text + container kontextus (200 char)
- **Output:** `[{index, score, title, description}]`
- **Küszöb:** `relevance_threshold = 6`
- **Rendezés:** score szerint csökkenő

### 4.6 Lépés 6 — Digest összeállítás és küldés

- **Osztály:** `DigestBuilder.BuildHtml()` + `GmailService.SendDigestAsync()`
- **Formátum:** responsive HTML email (table layout, inline CSS)
- **Kártya elemei:** score bar (█░ vizuális), cikk cím (link), leírás (magyarázat)
- **Tárgy:** `🌅 Tech Digest — YYYY-MM-DD (N link)`
- **Küldés:** MIME base64url, `gmail.send` scope

---

## 5. Konfiguráció

### 5.1 `digest-config.json`

```json
{
  "gmail_label": "Develop",
  "relevance_threshold": 6,
  "max_emails": 50,
  "last_run_file": "last-run.txt",
  "model": "gemini-2.5-flash",
  "topics": {
    "primary": [".NET", "C#", "DDD", "CQRS", "Event Sourcing", ...],
    "secondary": ["Prompt Engineering", "MCP", "Agentic", ...],
    "bonus_sources": ["martinfowler.com", "ardalis.com", ...],
    "negative_patterns": ["we're hiring", "introduction to", ...]
  }
}
```

`CopyToOutputDirectory: Always` — minden build mellé kimásolódik.

### 5.2 Környezeti változók

| Változó | Szint | Leírás |
|---------|-------|--------|
| `GEMINI_API_KEY` | User | Google AI Studio API kulcs |

### 5.3 Fájlrendszer

| Útvonal | Tartalom |
|---------|----------|
| `%USERPROFILE%\.gmail-mcp\credentials.json` | OAuth2 token (`access_token`, `refresh_token`) |
| `%USERPROFILE%\.gmail-mcp\gcp-oauth.keys.json` | GCP OAuth2 app credentials (`client_id`, `client_secret`) |
| `%USERPROFILE%\.gmail-mcp\last-run.txt` | Utolsó futás ISO 8601 UTC timestamp |

---

## 6. Gmail OAuth2 autentikáció

**Headless flow** — nem nyit böngészőt, nem igényel interakciót:

```
credentials.json         gcp-oauth.keys.json
     │                         │
  refresh_token          client_id
  access_token           client_secret
     │                         │
     └──────────┬──────────────┘
                ▼
  GoogleAuthorizationCodeFlow
  + TokenResponse + UserCredential
                ▼
       GmailService (Gmail API v1)
```

- `GoogleWebAuthorizationBroker` **nem** használatos (böngészőt nyitna)
- `client_id`/`client_secret` fallback: ha hiányzik a `credentials.json`-ból, automatikusan betölti a `gcp-oauth.keys.json`-ból (`installed.client_id`)
- Scope: `gmail.readonly` + `gmail.send`

---

## 7. Gemini API integráció

**Direkt HTTP REST** — NEM Google.GenerativeAI NuGet csomag:

```
POST https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:generateContent?key={API_KEY}
Content-Type: application/json

{
  "contents": [{ "parts": [{ "text": "..." }] }]
}
```

- Timeout: 120 másodperc
- Válasz parse: regex `\[[\s\S]*\]` → `JsonSerializer.Deserialize`
- Hiba esetén fallback: minden elem score=5, folytatás

---

## 8. NuGet függőségek

| Csomag | Verzió | Felhasználás |
|--------|--------|-------------|
| `Google.Apis.Gmail.v1` | 1.73.0.4029 | Gmail API kliens |
| `AngleSharp` | 1.4.0 | HTML DOM parse és traversal |

Minden más funkció a .NET 8 BCL-ből van megvalósítva (`HttpClient`, `System.Text.Json`, stb.).

---

## 9. CI/CD

**GitHub Actions** — `.github/workflows/build.yml`

| Trigger | `push` — minden branch |
|---------|----------------------|
| Runner | `windows-latest` |
| .NET | 8.0.x |
| Lépések | checkout → restore → build → publish → artifact upload |
| Artifact | `MorningDigest-win-x64` (self-contained, 30 nap megőrzés) |

Self-contained publish parancs:
```
dotnet publish --configuration Release --runtime win-x64 --self-contained true --output ./publish
```

---

## 10. Windows Task Scheduler

**Task neve:** `MorningDigestNet`

| Tulajdonság | Érték |
|-------------|-------|
| Trigger | Bejelentkezéskor (`LogonTrigger`) |
| Felhasználó | `PACA\balog` |
| Késleltetés | 30 másodperc |
| Futtatandó | `C:\Work\Agents\MorningDigest.Net\bin\Release\net8.0\MorningDigest.exe` |
| Munkamappa | `C:\Work\Agents\MorningDigest.Net` |
| Max futási idő | 10 perc |
| Hálózat szükséges | Igen |
| Akkumulátoron | Fut |

**Regisztráció:**
```
schtasks /create /xml task-scheduler.xml /tn "MorningDigestNet"
```

---

## 11. Adatfolyam összefoglalása

```
Gmail (label: Develop)
        │
        │  max 50 email, utolsó futás óta
        │  -subject:"Tech Digest" kizárás
        ▼
   EmailItem[]  (subject, from, snippet, html body)
        │
        │  Gemini: 0–10 relevancia score
        │  küszöb: ≥6
        ▼
   Releváns EmailItem[]
        │
        │  AngleSharp DOM fingerprint → cikk konténerek
        │  Article URL filter (2 szegmens, nem profil/tag)
        │  Redirect unwrap, blacklist
        ▼
   LinkItem[]  (url, anchor, context, source)
        │
        │  Gemini: 0–10 relevancia score, title, description
        │  küszöb: ≥6, rendezés: score desc
        ▼
   ScoredLink[]
        │
        │  DigestBuilder → responsive HTML
        ▼
   Gmail → balogh.gyuri@gmail.com
```

---

## 12. Hibakezelés

| Eset | Viselkedés |
|------|-----------|
| `credentials.json` hiányzik | `Exit(1)`, hibaüzenet |
| `GEMINI_API_KEY` nincs beállítva | `Exit(1)`, hibaüzenet |
| Gemini JSON parse hiba | Fallback: minden elem score=5, futás folytatódik |
| Email olvasási hiba | Skip, warning log, többi email feldolgozódik |
| Nincs releváns email | Üres digest elküldve, `last-run.txt` frissítve |
| Küldési hiba | HTML console-ra kiírva fallbackként |

---

## 13. Biztonsági megfontolások

- API kulcs és OAuth2 credentials **nem** kerülnek a repositoryba (`.gitignore`)
- `credentials.json` és `gcp-oauth.keys.json` a felhasználói profilban tárolt (`%USERPROFILE%\.gmail-mcp\`)
- `GEMINI_API_KEY` User szintű environment változóban (nem System szinten)
- A Task Scheduler task csak az adott felhasználó (`PACA\balog`) bejelentkezésekor fut
- Gmail scope minimális: `readonly` + `send` (nem `modify`, nem `admin`)

---

## 14. GitHub Issues — fejlesztési előzmények

| # | Cím | Státusz |
|---|-----|---------|
| #1 | Project scaffold & CI/CD setup | ✅ Closed |
| #2 | Models & configuration | ✅ Closed |
| #3 | Gmail OAuth2 headless auth | ✅ Closed |
| #4 | Gmail API: list, read, send | ✅ Closed |
| #5 | URL extraction & blacklist filtering (superseded) | ✅ Closed |
| #6 | Gemini API — email relevance scoring | ✅ Closed |
| #7 | Gemini API — link evaluation | ✅ Closed |
| #8 | HTML digest builder & email assembly | ✅ Closed |
| #9 | Program.cs orchestration, logging & Task Scheduler | ✅ Closed |
| #10 | Register Task Scheduler task (MorningDigestNet) | ✅ Closed |
| #11 | Structural HTML processor — DOM fingerprint-based extraction | ✅ Closed |
| #12 | Filter non-article URLs: profiles, root domains, tag/category pages | ✅ Closed |
| #13 | Fix article URL filter edge cases: /@username/slug and single-segment URLs | ✅ Closed |
