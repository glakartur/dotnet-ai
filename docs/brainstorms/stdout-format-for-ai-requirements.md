---
title: Stdout output format optimized for AI consumption
status: draft
date: 2026-05-16
owner: Artur
---

# Stdout format optimized for AI

## Problem

`dotnet aicraft` jest narzędziem konsumowanym przede wszystkim przez agenty AI (Claude Code). Aktualnie klient emituje na stdout **pretty-printed JSON** (`WriteIndented = true`, camelCase). JSON jest dobrze parsowalny, ale:

- Tokenowo drogi dla dominującego kształtu danych (listy lokalizacji: `refs`, `symbols`, `callers`, `impls`, `unused`) — quote'y, klucze, wcięcia, separatory.
- Nie wykorzystuje konwencji devtools (`grep`/`ripgrep`/`MSBuild`/`gcc`), na których LLM-y zostały masowo wytrenowane i które rozumieją natywnie.
- Domyślne wyjście traktuje konsumenta-AI tak samo jak konsumenta-skrypt, mimo że ich potrzeby są przeciwstawne.

Kanał klient↔daemon (JSON-RPC) pozostaje **bez zmian** — zmiana dotyczy wyłącznie warstwy renderowania w kliencie.

## Cel

Zoptymalizować domyślny stdout pod **rozumienie przez LLM**, zachowując JSON jako opcjonalny format dla użycia skryptowego.

## Decyzje produktowe

### Default: text-hybryda (compiler/ripgrep-style)

Dla **list lokalizacji** (`refs`, `symbols`, `callers`, `impls`, `unused`):

```
12 references to MyNamespace.Service.DoWork in MySolution.sln

src/Foo/Bar.cs:42:17: var x = service.DoWork();
src/Foo/Baz.cs:88:9: await DoWork(token);
...
```

Dla **`definition`** (pojedynczy bogaty obiekt):

```
MyNamespace.Service

Kind: class
Location: src/Service.cs:12:14
Signature: public sealed class Service : IService

Documentation:
  Provides core business logic.
  Thread-safe.
```

Dla **`diagnostics`** (konwencja MSBuild/Roslyn):

```
3 errors, 17 warnings

warning src/Foo.cs:42:5 [CS0168]: The variable 'x' is declared but never used
error src/Bar.cs:88:1 [CS0103]: The name 'foo' does not exist in the current context
...
```

Dla **błędów wykonania klienta**:

```
error SOLUTION_UNAVAILABLE: Solution is currently unavailable.
hint: Run 'server reload' or fix the solution/project files and retry.
```

### Flaga `--format <text|json>`

- `text` — **default**, powyższy format.
- `json` — obecny pretty-printed JSON, bez zmian schematu (kompat dla użycia skryptowego).
- Brak `yaml`, `jsonl`, `md` w pierwszej iteracji — odrzucone świadomie (zobacz "Odrzucone alternatywy").

### Konwencje renderowania (text)

- **Linie body raw** — bez paddingu/wyrównywania kolumn (jak ripgrep). Padding kosztuje tokeny i nie poprawia rozumienia LLM.
- **Nagłówek meta to zwykła linia**, nie komentarz `#` (uniknięcie kolizji z `#` w PowerShell/bash przy pipeline'ach).
- **Treść wieloliniowa wcięta 2 spacjami** bez ramek/cudzysłowów (np. `Documentation` w `definition`).
- **Stdout = wyłącznie wynik komendy.** Linie statusu daemona (np. `Starting analysis daemon...`, `Ready.`) nie idą na stdout.

### Sekwencjonowanie wyjścia debug

Gdy daemon zwraca informacje debug (przy `--debug`), klient **musi je wyemitować PRZED wynikiem** komendy, w osobnym kanale (stderr) lub w sposób ustalony w planie. Wynik na stdout pozostaje czysty i przewidywalny dla parsera AI/skryptu — debug nie przeplata się z wynikiem.

## Sukces

- Default stdout dla typowego `refs`/`symbols`/`callers` jest **istotnie krótszy tokenowo** niż obecny pretty-printed JSON (oczekiwany rząd: ~3× redukcja dla list).
- LLM (Claude Code) potrafi poprawnie ekstraktować lokalizacje (`file:line:col`) i identyfikatory bez dodatkowych instrukcji w prompcie.
- `--format json` zwraca wyjście **identyczne** z obecnym (zero regresji dla użycia skryptowego, gdyby się pojawiło).
- Debug z daemona pojawia się **przed** wynikiem, nigdy nie przeplata stdout.

## Poza zakresem

- Zmiany w protokole JSON klient↔daemon.
- Zmiana zachowania flagi `--debug` jako takiej (porządki osobno — patrz "Zależności").
- Internacjonalizacja komunikatów.
- Kolorowanie ANSI / TTY-aware output.
- Formaty `yaml`, `jsonl`, `md`.
- Schemat i kontrakt `json` — pozostaje 1:1 z obecnym.

## Odrzucone alternatywy

- **YAML jako default** — w dominującym kształcie (listy lokalizacji) zużywa ~3× więcej linii/tokenów niż text-hybryda przy braku przewagi w rozumieniu LLM. Dla `definition` remisuje, dla `diagnostics` przegrywa z konwencją branżową.
- **Markdown z tabelami jako default** — tabele MD są tokenowo drogie (`|` separatory, header underline), słabo skalują się dla wielu wierszy.
- **`jsonl` (NDJSON)** — wynik z daemona przychodzi w całości, nie ma realnego case'u streamingu na poziomie klienta.
- **Custom flag values poza `text`/`json`** — wstrzymane do czasu, aż pojawi się konkretne uzasadnienie.

## Zależności i założenia

- **Brak zewnętrznych klientów skryptowych** zależnych od obecnego defaultu — potwierdzone przez właściciela; zmiana defaultu z JSON na text nie wymaga okresu deprecation.
- Telemetria/`--debug` jest porządkowana **osobno** (poza tym dokumentem). Wymaganie sekwencjonowania ("debug przed wynikiem") jest twardym kontraktem dla tej zmiany — implementacja musi to zapewnić niezależnie od stanu prac nad `--debug`.
- Kontrakt JSON klient↔daemon nie zmienia się.

## Otwarte pytania do planu (`/ce-plan`)

- Czy renderery per-komenda dostają osobne klasy (np. `TextRenderer` per command), czy jeden wspólny komponent z dispatcherem po typie?
- Czy `--format` jest globalną opcją root command (przed sub-command), czy duplikowaną w każdej sub-command?
- Gdzie technicznie spina się "debug przed wynikiem" — bufor w kliencie, flush daemona przed final response, czy oba?
