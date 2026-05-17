---
title: Debug console output — per-request collection and emission
status: draft
date: 2026-05-17
owner: Artur
---

# Debug console output — per-request collection and emission

## Problem

`--debug` to dziś półgotowy mechanizm:

- Klient i daemon używają wspólnej klasy `DebugLog.Write(...)`, która pisze na **własny stderr** procesu, w którym zostanie wywołana.
- Gdy klient sam uruchamia daemona, ustawia w jego środowisku `DOTNET_AICRAFT_DEBUG=1`. Daemon zaczyna pisać debug na **swój** stderr — ale stderr daemona jest po stronie klienta **silently drained** przez `DrainProcessPipeAsync` i wyrzucany. Efekt: w `--debug` użytkownik widzi wyłącznie logi z procesu klienta. Daemon-side debug nie dociera do konsoli mimo że jest produkowany.
- W envelope'ie protokołu istnieją już pola `DaemonRequest.Debug` i `DaemonResponse.Debug`, ale `response.Debug` nigdy nie jest wypełniane przez daemona ani emitowane przez klienta. `DebugLog.WriteResponseDebug(...)` istnieje, ale nie jest podpięte do client receive path.
- W trybie domyślnym (`--debug` nieustawione) `DebugLog.IsEnabled` jest `false`, więc klient nie emituje własnych logów. To jest OK. Natomiast daemon, jeżeli został wcześniej uruchomiony ręcznie z `DOTNET_AICRAFT_DEBUG=1`, pisze swoje logi na swój własny stderr niezależnie od trybu wywołania klienta — to działa poprawnie dla manualnych uruchomień daemona, ale powinno być wyraźnie oddzielone od mechanizmu per-request.

Poprzedni brainstorm ([stdout-format-for-ai](stdout-format-for-ai-requirements.md)) twardo wymaga, by debug pojawiał się **na stderr**, **przed** wynikiem na stdout, i nigdy nie przeplatał się ze stdoutem. Ten dokument domyka mechanizm tak, by ten kontrakt był realizowalny dla obu źródeł debugu (klient + daemon).

## Cel

`--debug` jako **per-request opt-in** dla zbierania i przekazywania debugu z daemona, plus czyste „zero output" w trybie domyślnym.

## Decyzje produktowe

### Tryb `--debug`

- Klient emituje na **stderr** swoje własne wpisy debug (już działa: `DebugLog.Write(...)` → stderr).
- Klient w `DaemonRequest.Debug = true` sygnalizuje daemonowi, że dla tego konkretnego requestu ma zbierać debug.
- Daemon w obrębie obsługi tego requestu **zbiera** wpisy debug do bufora w pamięci scoped per-request (nie globalnie).
- Daemon dołącza zebrane wpisy do `DaemonResponse.Debug` i odsyła socketem.
- Klient po otrzymaniu response'a emituje `response.Debug` na **stderr**, linia po linii, **przed** wypisaniem wyniku na stdout.

### Tryb domyślny (bez `--debug`)

- Klient: `request.Debug` jest `null` / `false`. Nie czyta `response.Debug` nawet gdyby się tam coś znalazło.
- Daemon: nie alokuje per-request bufora, `response.Debug` pozostaje `null`.
- Brak jakiegokolwiek outputu na stderr po obu stronach. Jedyne wyjście: wynik na stdout klienta.

### Per-request, nie globalnie

- Zbieranie debugu w daemonie sterowane jest **wyłącznie** flagą `request.Debug` w danym requeście. Dwa równoległe requesty od dwóch klientów (jeden z `--debug`, drugi bez) muszą zachować się niezależnie.
- Klient nie propaguje już `DOTNET_AICRAFT_DEBUG=1` do środowiska spawnowanego daemona — debug wraca per-request, więc env var jest zbędny dla daemona uruchamianego przez klienta. (`DaemonClient.StartDaemonAsync` przestaje ustawiać tę zmienną.)

### Zachowanie dla manualnie uruchamianego daemona

- Komenda `dotnet aicraft server start` (uruchamiana ręcznie) zachowuje obecne wsparcie dla `DOTNET_AICRAFT_DEBUG` jako daemon-global verbose mode: jeśli ustawione, daemon pisze swój debug na **swój własny stderr** (czyli na konsolę, w której uruchomiono server start) — w taki sam sposób jak dziś.
- To nie koliduje z mechanizmem per-request: globalny verbose nie wymusza zbierania do bufora ani odsyłania w response. Bufor per-request działa niezależnie. Jeśli oba są włączone naraz, daemon: (a) zapisuje wpis do per-request bufora gdy w trakcie obsługi requestu z `Debug=true`, i (b) dodatkowo emituje go na swój stderr jeżeli `DOTNET_AICRAFT_DEBUG` jest globalnie włączone.

### Sekwencjonowanie i kanał

- Wszystkie wpisy debug (klient + daemon) → **stderr**.
- Klient gwarantuje kolejność: najpierw swoje wpisy debug, potem `response.Debug` z daemona, na końcu wynik na **stdout**. Stdout nigdy nie zawiera debugu, stderr nigdy nie zawiera wyniku. Spójne z [stdout-format-for-ai](stdout-format-for-ai-requirements.md).

### Kształt `response.Debug` na drucie

- `response.Debug` to `string[]` (lista preformatowanych linii w stylu obecnego `DebugLog.Write` → `"[dotnet-aicraft debug <ISO-UTC>] [<component>] <message>"`).
- Bez strukturyzowanego schematu (timestamp/level/component jako osobne pola) w pierwszej iteracji. Linie są self-describing dla człowieka i dla LLM.
- Klient nie reinterpretuje treści — emituje 1:1 na stderr.

## Sukces

- `--debug` dowolnej komendy klienta produkuje na stderr **zarówno** wpisy klienta, **jak i** wpisy daemona, sekwencyjnie i przed wynikiem na stdout. Dziś tylko klient.
- Tryb domyślny: stderr klienta i daemona są puste podczas normalnej obsługi requestu (zweryfikowane testami: `Assert.Equal(string.Empty, stderr)`).
- Dwa równoległe requesty, jeden z `--debug` jeden bez, dają poprawny output dla każdego z osobna (request-scoping zweryfikowany testem).
- Klient przestaje ustawiać `DOTNET_AICRAFT_DEBUG=1` na spawnowanym daemonie — debug per-request działa bez tej zmiennej.
- Manualne `server start` z `DOTNET_AICRAFT_DEBUG=1` nadal pisze daemon-side debug na swój własny stderr (regresja byłaby utratą sposobu debugowania daemona w izolacji).

## Poza zakresem

- **Tła daemona poza obsługą requestu**: startup banner, file-watcher events, idle-timeout shutdown, GC, itp. Nie są przypisane do żadnego requestu, więc w tej iteracji **nie** są raportowane przez `response.Debug`. Daemon-global verbose (`DOTNET_AICRAFT_DEBUG` na procesie daemona) pozostaje jedynym sposobem zobaczenia ich w czasie rzeczywistym.
- **Strukturyzowany schemat wpisu debug** (poziomy log, kategorie, structured fields). Płaska linia jak dziś.
- **Filtrowanie po komponencie / poziomie** (`--debug=server`, `--debug-level=trace`). Wszystko albo nic.
- **Korelacja request-id w debugu klienta vs daemona**. Klient już dziś loguje `requestId`; struktura wystarczająca.
- **Plikowy sink, syslog, OpenTelemetry**. Tylko stderr.
- **Zmiany w schemacie envelope'a** (`DaemonRequest.Debug`, `DaemonResponse.Debug` zostają; tylko zaczynamy je realnie używać).

## Odrzucone alternatywy

- **Streaming daemon stderr do stderr klienta zamiast in-memory + response.Debug.** Łatwiejsze (po prostu nie drainuj stderr daemona, przepuść go), ale łamie kontrakt sekwencjonowania z poprzedniego brainstormu: nie ma synchronicznego punktu „cały debug z daemona dla tego requestu już dotarł" przed wypisaniem wyniku. Per-request collection rozwiązuje to z definicji — debug podróżuje **razem z** response.
- **Daemon-global mode jako jedyny tryb** (klient zawsze ustawia env var, daemon zawsze pisze własny stderr, klient nie drainuje). Zalewa logami przy współbieżnych klientach z innymi trybami debugowymi; brak request-scopingu.
- **Strukturyzowany payload `response.Debug` od początku**. Bez konkretnego konsumenta (LLM też dobrze radzi sobie z liniami) — over-engineering.
- **Połączenie stdout + stderr na jeden strumień, debug jako prefiksowane linie.** Łamie kontrakt „stdout = wyłącznie wynik" z [stdout-format-for-ai](stdout-format-for-ai-requirements.md).

## Zależności i założenia

- Schemat envelope'a (`DaemonRequest.Debug`, `DaemonResponse.Debug`) jest już na miejscu — bez zmian w `Models.cs`.
- `DebugLog.WriteResponseDebug` zostanie podpięte do klient-side response handlera (dziś nie ma callsite'u).
- `DrainProcessPipeAsync` na stderr daemona pozostaje — daemon-side stderr (globalny verbose) nadal nie wycieka na konsolę klienta. Per-request debug jedzie kanałem socketu.
- Decyzja „stderr, przed wynikiem" jest dziedziczona z [stdout-format-for-ai](stdout-format-for-ai-requirements.md) — twardy kontrakt, nie do renegocjacji w tym dokumencie.
- Zakładamy, że debug per-request mieści się w odpowiedzi (response już w całości buforowana po stronie daemona przed wysyłką — dodanie array stringów jest bez znaczenia rozmiarowego dla typowych komend).

## Otwarte pytania do planu (`/ce-plan`)

- Gdzie konkretnie ustawiany jest per-request scope w daemonie? `AsyncLocal<List<string>>` przepinany w `DispatchAsync` przed wywołaniem handlera, plus override `DebugLog.Write` żeby dopisywało do bufora gdy scope aktywny, czy jakaś jawna `IDebugSink` przekazywana w command context?
- Czy w `--debug` daemon-global verbose i per-request collection mają deduplikować (jeden wpis nie powtarza się 2x dla użytkownika, który uruchomił daemona manualnie z env var i potem dzwoni klientem z `--debug`)? — domyślnie **nie** deduplikujemy, ale do potwierdzenia.
- Jak długo trzymać emisję `DOTNET_AICRAFT_DEBUG` env var w spawnie daemona przez klienta — usuwamy bezwarunkowo w tej iteracji, czy zostawiamy z deprecation note dla zewnętrznych skryptów które polegały na obserwacji daemon stderr przez attach? (Brak takich konsumentów AFAIK — analogicznie do ustaleń z poprzedniego brainstormu.)
- Czy testy regresyjne na „stderr pusty w trybie domyślnym" pokrywają też ścieżkę spawnu daemona (gdzie zniknięcie env var jest realnie weryfikowalne)?
