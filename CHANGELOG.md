# Changelog

Minden jelentős változás ebben a fájlban kerül dokumentálásra.
Formátum: [Keep a Changelog](https://keepachangelog.com/en/1.0.0/)

---

## [Unreleased]

---

## [1.1.0] — 2026-04-14

### Fixed
- **Duplikált linkek** (`2ecad10`) — `DistinctBy(l => l.Url)` hozzáadva a `Program.cs`-ben a `SelectMany` után, hogy ugyanaz a cikk ne szerepeljen többször ha több newsletter is megosztotta

---

## [1.0.3] — 2026-04-08

### Fixed
- **Article URL filter — edge case** (`15a63e3`) — minimum 2 path szegmens szabály hozzáadva; egyetlen szegmenses publication homepagek (pl. `medium.com/lets-code-future`) kizárva
- **Medium `/@username/article-slug` URL-ek** (`e4e7298`) — a `/@` prefix túl agresszív volt; `/@username` (1 szegmens) = profil (kizárva), `/@username/slug` (2 szegmens) = cikk (átengedve)

### Style
- **Source mező eltávolítva** (`c84b7d4`) — a digest kártyákról eltávolítva a `📧 subject (from)` sor, mert zavaró volt

---

## [1.0.2] — 2026-04-08

### Added
- **IT Specifikáció** (`c36c190`) — `docs/specification.md` + `docs/specification.pdf` (7 oldal), teljes rendszerleírás architektúrával, pipeline részletekkel, konfigurációval

### Fixed
- **Docs layout** (`df0a7e5`) — 4 fázis táblázat bullet listára cserélve, `&amp;` encoding javítva

### Changed
- **Non-article URL szűrő** (`9536c6c`, closes #12) — profil oldalak (`/@username`), root domain URL-ek, tag/category/marketing oldalak kizárva az article-filter segítségével

---

## [1.0.1] — 2026-04-08

### Fixed
- **Önfeldolgozás megelőzése** (`7744525`) — Gmail query kiegészítve `-subject:"Tech Digest"` kizárással, hogy a saját digest emaileket ne dolgozza fel
- **Headless OAuth2 client credentials** (`814e018`) — `client_id`/`client_secret` automatikus betöltése `gcp-oauth.keys.json`-ból ha hiányzik a `credentials.json`-ból
- **UTC→Local dátum konverzió** (`9e5aa41`) — Gmail query dátuma helyesen lokál időzónában generálódik

---

## [1.0.0] — 2026-04-08

### Added
- **Projekt scaffold** — `MorningDigest.csproj` (net8.0), `digest-config.json`, `.gitignore`, `task-scheduler.xml`
- **CI/CD** — GitHub Actions: build + self-contained win-x64 publish minden push-ra
- **Models** — `DigestConfig`, `EmailItem`, `LinkItem`, `RelevanceScore`, `ScoredLink`, `LinkScore`
- **GmailService** — headless OAuth2 (`GoogleAuthorizationCodeFlow` + `UserCredential`), `ListEmailsAsync`, `ReadEmailAsync`, `SendDigestAsync`; HTML body prioritás; Base64URL decode
- **GeminiService** — direkt HTTP REST hívás (nem NuGet), email relevancia scoring + link scoring, batch feldolgozás
- **UrlExtractor** — AngleSharp alapú 4 fázisú strukturális DOM processzor: fingerprinting, leghosszabb sorozat detektálás, URL kinyerés redirect unwrappel, blacklisttel, article-filterrel
- **DigestBuilder** — responsive HTML email builder (table layout, inline CSS, score bar)
- **Program.cs** — 6 lépéses pipeline, `last-run.txt` kezelés, `Task.WhenAll` párhuzamos link kinyerés
- **Task Scheduler** — `MorningDigestNet` task, LogonTrigger +30s, regisztrálva PACA gépen

### Issues lezárva
#1 Project scaffold, #2 Models, #3 OAuth2, #4 Gmail API, #5 URL extraction (superseded), #6 Gemini relevancia, #7 Gemini link scoring, #8 DigestBuilder, #9 Program.cs orchestration, #10 Task Scheduler regisztráció, #11 Strukturális HTML processzor, #12 Article URL filter, #13 Article URL filter edge cases
