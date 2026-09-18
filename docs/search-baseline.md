# Repere de căutare, măsurate înainte de a scrie motorul

Arborele: `D:\dev\OAN` — **130.523 fișiere, 45 GB**, cu `target/` plin de artefacte de build
Rust, deci amestec realist de text și binar. Cache cald, a doua rulare.
Mașină: Windows 11 Pro 26200, RTX 4080 Super, 32 GB.

ripgrep 15.0.0, cel bundlat cu VS Code:
`%LOCALAPPDATA%\Programs\Microsoft VS Code\645f29cc31\resources\app\node_modules.asar.unpacked\@vscode\ripgrep-universal\bin\win32-x64\rg.exe`

## Nume

| unealtă | timp | rezultate |
|---|---|---|
| `rg --files \| grep -i router` | **0,77 s** | 789 |
| `find -iname "*router*"` | 3,74 s | 62 |

Cifrele nu sunt comparabile direct și de-aia sunt amândouă aici: `grep` se potrivește pe toată
calea, `find` doar pe numele fișierului. Reperul de timp util e **sub o secundă pentru a
enumera 130.000 de căi**, nu raportul dintre cele două.

## Conținut — termenul `NotAttempted`

| unealtă | timp | fișiere |
|---|---|---|
| `rg` (implicit: sare peste binare) | **7,4 s** | 108 |
| `rg -uuu --binary` | 32 s | 14.546 |
| `findstr /S /I /M` | 2 min 39 s | 14.548 |

**Comparat corect** — binare incluse de ambele părți — ripgrep dă practic același răspuns de
**5× mai repede**. Diferența de 2 fișiere rămâne neexplicată și e notată ca atare.

**Ce contează mai mult decât timpul:** 14.438 din cele 14.548 ale lui findstr sunt în `target\`,
adică artefacte de build. Doar **110** sunt fișiere reale. findstr e de cinci ori mai lent ca să
livreze de 130 de ori mai mult zgomot. Răspunsul implicit al lui ripgrep — 108 — e la două
fișiere de semnalul adevărat.

De-aici ies două cerințe pentru motorul nostru, nu una:
1. **viteză**: în clasa 7–32 s pe arborele ăsta;
2. **a sări peste binare implicit**, fiindcă altfel viteza nu ajută pe nimeni.

## Ce NU s-a măsurat aici

Windows Search. Nu poate fi comparat cu o cronometrare pe interfața Explorer — trebuie
interogat prin `Search.CollatorDSO`, ca să fie aceeași unitate de măsură. Rămas pe seama
raportului `search-benchmark.md`.

Everything nu e instalat pe mașina asta, deci reperul pentru căutarea instantă pe nume lipsește.
El citește `$MFT` direct și are nevoie de administrator — vezi de ce, în nota de memorie despre
WizTree. Fără indexul ăla nu-l batem la nume, și nu pretindem că îl batem.
