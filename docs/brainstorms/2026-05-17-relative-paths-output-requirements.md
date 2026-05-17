# Requirements: Relative file paths in command output

**Date:** 2026-05-17
**Status:** Ready for planning
**Scope:** Lightweight

## Problem

`dotnet aicraft` commands return absolute file paths in their output (text and JSON modes). The solution-root prefix is repeated in every result line, inflating token usage for AI agent consumers without adding information they don't already have.

## Goal

Reduce output size by emitting file paths **relative to the directory of the `.sln` file**, while keeping the absolute solution root available once per response so consumers can re-resolve when needed.

## Measured impact

Pomiar wykonany 2026-05-17 na repo `dotnet-aicraft` (prefiks 30 znaków: `C:\code\others\dotnet-aicraft\`):

| Command | Total chars | Prefix occurrences | Savings |
|---|---|---|---|
| `symbols --pattern Server*` | 1 165 | 7 | 17,4% |
| `refs ServerCommand` | 3 073 | 17 | 16,0% |
| `diagnostics` (whole solution) | 10 342 | 58 | 16,3% |

Dla typowych enterprise ścieżek (50-70 znaków prefiksu) oszczędność rośnie do 25-35%.

## Decisions

1. **Paths in output bodies → relative to `.sln` directory.** Forward-slash separators (`/`) for cross-platform spójność, niezależnie od OS.
2. **Solution root surfaced once per response:**
   - Text mode: pierwsza linia nagłówka, np. `SolutionRoot: C:\code\others\dotnet-aicraft`.
   - JSON mode: pole `solutionRoot` na poziomie root obiektu odpowiedzi.
3. **Komendy w zakresie:** `refs`, `impls`, `callers`, `symbols`, `diagnostics`, `definition`, `unused`. `rename` zwraca summary — minimalna zmiana, wyrównanie kosmetyczne.
4. **Linia/kolumna bez zmian** — pozostają w formacie `:line:col`.
5. **Brak flagi opt-out w v1.** Zmiana traktowana jako poprawa formatu; jeśli pojawi się realny konsument wymagający absolutnych ścieżek, dołożymy flagę później.

## Success criteria

- Wszystkie 7 komend w zakresie zwracają ścieżki relatywne w obu formatach (Text, Json).
- Każda odpowiedź zawiera pojedynczy `solutionRoot` (text header / json field).
- Powtórny pomiar na tych samych 3 próbkach pokazuje ≥15% redukcję char count.
- Istniejące testy snapshot-owe zaktualizowane, suite zielony.

## Out of scope

- Zmiana formatu numerów linii/kolumn.
- Flaga `--paths=absolute|relative` (rozważyć dopiero przy zgłoszonej potrzebie).
- Zmiana formatu wynikow `rename` poza zwykłym pass-through nowych ścieżek.

## Open questions for planning

- Czy `solutionRoot` powinien być absolutny, czy znormalizowany (np. zawsze backslash na Windows)?
- Czy istnieją wewnętrzne testy/skrypty parsujące output, które wymagają migracji?
- Json schema — czy istnieje wersjonowanie, które trzeba bumpnąć?

## Next step

`/ce-plan` — rozpisać kroki implementacji (helper `PathFormatter`, wstrzyknięcie do każdego use case'a, aktualizacja testów).
