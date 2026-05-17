---
title: "feat: Per-request debug collection and emission"
status: completed
type: feat
date: 2026-05-17
owner: Artur
origin: docs/brainstorms/2026-05-17-debug-console-output-requirements.md
---

# feat: Per-request debug collection and emission

## Summary

Today `--debug` only surfaces client-side debug entries. The daemon's debug, when produced, writes to its own stderr — which the client drains and discards — so users never see it. The envelope already carries `DaemonRequest.Debug` and `DaemonResponse.Debug`, but `response.Debug` is never populated and the client's existing flush (`FlushResponseDebugToStderr` → `DebugLog.WriteResponseDebug`) currently JSON-serializes the payload instead of treating it as preformatted lines.

This plan wires the brainstorm's per-request opt-in end-to-end: the daemon captures debug entries scoped to a single request via an `AsyncLocal<List<string>>` sink, returns them as `string[]` on `response.Debug`, and the client emits each line 1:1 on stderr before the result lands on stdout. Manual `dotnet aicraft server start` with `DOTNET_AICRAFT_DEBUG=1` continues to write daemon-global debug to its own stderr unchanged. The client stops propagating `DOTNET_AICRAFT_DEBUG=1` to spawned daemons — per-request transport makes the env var redundant for spawned daemons.

---

## Problem Frame

- **Symptom**: `--debug` shows only the client's own log lines. Daemon-side trace is invisible to the user.
- **Root cause**: Daemon writes its debug to its own stderr; client drains that stderr via `DrainProcessPipeAsync` and discards it. The wire field exists (`response.Debug`) but is never populated.
- **Constraint**: Sequencing rule from `docs/brainstorms/stdout-format-for-ai-requirements.md` — debug only on stderr, only before stdout, no interleaving. Per-request transport is what makes this synchronizable; streaming daemon stderr cannot provide a "debug for this request is done" marker.

---

## Requirements

Carried from `docs/brainstorms/2026-05-17-debug-console-output-requirements.md`:

- `--debug` produces both client and daemon debug entries on stderr, in order, before the stdout result.
- Default mode produces empty stderr on both sides during normal request handling.
- Two concurrent requests with different `--debug` flags are independent (per-request scoping, not global).
- Client no longer propagates `DOTNET_AICRAFT_DEBUG=1` to spawned daemons.
- Manual `server start` with `DOTNET_AICRAFT_DEBUG=1` continues to write daemon-side debug to its own stderr.
- `response.Debug` wire shape is `string[]` of preformatted lines matching the existing `DebugLog.Write` format.

---

## Key Technical Decisions

1. **Per-request sink via `AsyncLocal<List<string>>`** on the daemon. Set inside `DaemonServer.DispatchAsync` when `request.Debug == true`, cleared in `finally`. Chosen over a passed-through `IDebugSink` because every existing `DebugLog.Write(...)` callsite stays untouched.
2. **`DebugLog.Write` writes to both stderr (when globally enabled) and the per-request sink (when one is active)**. The two paths are independent: global verbose without per-request, per-request without global verbose, both together, or neither — all valid combinations. No deduplication across them (rejected open question #2 from origin).
3. **`response.Debug` wire shape becomes `string[]`** (preformatted lines exactly as `DebugLog.Write` produces today: `[dotnet-aicraft debug <ISO-UTC>] [<component>] <message>`). The existing `object?` field type in `DaemonResponse` already accepts this; serialization is unchanged.
4. **`DebugLog.WriteResponseDebug` signature changes** from `(object payload)` (JSON-serializes) to `(IEnumerable<string> lines)` (emits 1:1). The client's `FlushResponseDebugToStderr` deserializes `response.Debug` into a string array first.
5. **Unconditional removal of `DOTNET_AICRAFT_DEBUG=1` from spawned daemon env** (open question #3 from origin resolved). No deprecation window — no known external consumer relied on attaching to spawned daemon stderr.
6. **Manual `server start` daemon-global verbose stays via `ConfigureFromEnvironment` on daemon startup** (already wired). This plan does not touch that path beyond verifying it still works.

---

## High-Level Technical Design

```
Client                                Daemon
------                                ------
ConfigureFromArgs(--debug)
  -> DebugLog.IsEnabled = true

SendAsync(cmd, debug=true)
  request.Debug = true
  [client stderr: "[debug] ... SendAsync begin ..."]   <- already happens
                                      HandleClientAsync reads request
                                      DispatchAsync:
                                        if request.Debug == true:
                                          sink = new List<string>()
                                          DebugSink.Current = sink
                                        try:
                                          DebugLog.Write(...)
                                            -> if IsEnabled: stderr (daemon's own)
                                            -> if DebugSink.Current != null: sink.Add(line)
                                          result = handler(req)
                                        finally:
                                          DebugSink.Current = null
                                        response.Debug =
                                          (request.Debug == true && sink.Count > 0) ? sink.ToArray() : null
                                      writer.WriteLine(serialize(response))
  ReadResponseLine -> response
  FlushResponseDebugToStderr(response)
    -> decode response.Debug as string[]
    -> DebugLog.WriteResponseDebug(lines)
      -> foreach line: Console.Error.WriteLine(line)
  [return to command handler]
  -> emit result to stdout
```

*Directional guidance for review. The implementer should treat the sink mechanism as the load-bearing decision; method names and exact placement of the AsyncLocal holder are open to the implementer.*

---

## Implementation Units

### U1. Reshape `DebugLog.WriteResponseDebug` to emit preformatted lines

**Goal:** Change `WriteResponseDebug` from "serialize object to JSON, write each line" to "write each provided line to stderr verbatim". This is the wire-shape contract change.

**Requirements:** Brainstorm "Kształt response.Debug na drucie" — `string[]` of preformatted lines, client emits 1:1.

**Dependencies:** None.

**Files:**
- `src/DotnetAICraft/Diagnostics/DebugLog.cs` (modify `WriteResponseDebug`)
- `tests/DotnetAICraft.Tests/Output/DebugSequencingTests.cs` (rewrite the two existing tests for the new signature)

**Approach:**
- Replace `WriteResponseDebug(object payload)` with `WriteResponseDebug(IEnumerable<string> lines)`. Implementation iterates `lines`, calls `Console.Error.WriteLine` per line, returns.
- Keep the method static on `DebugLog`. Stay gated only on the caller having lines to flush (caller already handles `response.Debug is null`).
- No JSON serialization left in this method.

**Patterns to follow:** Existing `DebugLog.Write` shape (one `Console.Error.WriteLine` per logical line). Keep `using System.IO` import set minimal.

**Test scenarios:**
- Happy path: passing 3 lines emits 3 stderr lines in order, stdout stays empty.
- Edge case: empty `IEnumerable<string>` emits nothing on stderr.
- Edge case: a line containing embedded `\n` is emitted as-is (no extra splitting) — documents that callers are expected to pass already-split lines, matching daemon output format.

**Verification:** Tests pass. No callsite outside `CommandHelpers.FlushResponseDebugToStderr` invokes the old `object` overload.

---

### U2. Add per-request debug sink scoped to daemon request handling

**Goal:** Introduce an `AsyncLocal<List<string>>` debug sink on the daemon side and route `DebugLog.Write` through it when active. The sink is set inside `DispatchAsync` when `request.Debug == true` and cleared on completion.

**Requirements:** Brainstorm "Per-request, nie globalnie" + "Decyzje produktowe / Tryb --debug" (daemon collects to in-memory buffer scoped per-request).

**Dependencies:** None (parallel to U1).

**Files:**
- `src/DotnetAICraft/Diagnostics/DebugLog.cs` (add sink holder + extend `Write`)
- `src/DotnetAICraft/Daemon/DaemonServer.cs` (set/clear sink in `DispatchAsync`)
- `tests/DotnetAICraft.Tests/Diagnostics/DebugLogSinkTests.cs` (new test file)

**Approach:**
- In `DebugLog.cs`, add an internal `AsyncLocal<List<string>?>` (e.g., `_currentSink`) plus a pair of methods or a `using`-disposable scope (`BeginCapture()` returning a disposable that on dispose clears the sink and exposes the collected lines). The scope owns the buffer; `DebugLog.Write` reads from `AsyncLocal.Value` on each call.
- Extend `DebugLog.Write(component, message)`:
  - Existing behavior (write to stderr when `IsEnabled`) is preserved verbatim — does NOT change for global verbose.
  - Additionally: when `_currentSink.Value` is non-null, append the formatted line to it. This second branch is independent of `IsEnabled`. Per request.Debug=true alone, lines collect into the sink regardless of whether the daemon was also launched with the env var.
- In `DaemonServer.HandleClientAsync` (or directly inside `DispatchAsync` — implementer's call, but the cleanest seam is `DispatchAsync` so the existing `DebugLog.Write("server", "DispatchAsync begin ...")` line is captured): wrap the body in `using var scope = request.Debug == true ? DebugLog.BeginCapture() : null;`. After the response is built, read `scope?.GetLines()` and attach to `response.Debug` (U3 covers attachment).

**Technical design (sink shape):**

```
internal sealed class DebugCaptureScope : IDisposable
    constructor: prior = _currentSink.Value; _currentSink.Value = lines = new List<string>()
    GetLines(): return lines.ToArray()
    Dispose(): _currentSink.Value = prior
```

*Directional. Implementer may use a struct or a pair of static methods instead — the load-bearing property is AsyncLocal scoping with deterministic clear.*

**Patterns to follow:** Existing thread-safe primitive usage in `DebugLog.cs` (`Volatile.Read` / `Interlocked.Exchange`). Use `AsyncLocal<T>` not `ThreadLocal<T>` — the daemon's request handler is async and may resume on a different thread.

**Test scenarios:**
- Happy path: `using (var scope = DebugLog.BeginCapture()) { DebugLog.Write("server", "hello"); }` then `scope.GetLines()` returns one line matching the existing format.
- Capture works when `DebugLog.IsEnabled == false` (per-request collection is independent of global verbose).
- Capture AND global verbose together: line appears in both the capture buffer and on `Console.Error` (no dedup).
- Concurrency: two parallel `Task.Run` blocks each open their own capture scope, write 5 distinct lines each, verify each scope returns exactly its own 5 lines (no cross-contamination — this is what `AsyncLocal` guarantees, but the test pins the contract).
- Edge case: no scope active → `DebugLog.Write` behaves exactly as today (stderr if enabled, nothing if not). Existing `DebugSequencingTests` and other tests must continue to pass.
- Nested scopes: if one is opened inside another (unlikely in practice but cheap to guarantee), the outer scope sees writes that happen before/after the inner; inner sees only its own.

**Verification:** New `DebugLogSinkTests` pass. `DispatchAsync` opens/closes the scope correctly (covered indirectly by U3 integration tests).

---

### U3. Daemon populates `response.Debug` from the per-request sink

**Goal:** When the daemon finishes dispatching a request with `request.Debug == true`, attach the collected lines (`string[]`) to `response.Debug`. When `request.Debug` is null/false, `response.Debug` stays `null`.

**Requirements:** Brainstorm "Tryb --debug" (daemon dołącza zebrane wpisy do response.Debug) + "Tryb domyślny" (`response.Debug` pozostaje null).

**Dependencies:** U2.

**Files:**
- `src/DotnetAICraft/Daemon/DaemonServer.cs` (modify `DispatchAsync` and the `CreateSuccessResponse` / `CreateProblemResponse` / `CreateErrorResponse` helpers, or attach after-the-fact)
- `tests/DotnetAICraft.Tests/Daemon/DaemonDebugCaptureTests.cs` (new) — exercises `DispatchAsync` end-to-end with a minimal fake handler or via the public socket path

**Approach:**
- In `DispatchAsync`, open the capture scope at the top (per U2) when `req.Debug == true`. At the bottom (and in each `catch` branch that builds a response), read `scope?.GetLines()` and pass it through.
- Easiest seam: assign to the response right before returning. Either (a) extend the `CreateSuccessResponse` / `CreateProblemResponse` / `CreateErrorResponse` factories to accept an optional `string[]? debug` parameter (cleanest, keeps current call shape), or (b) construct the response, then `response = response with { Debug = capturedLines };` before returning (record `with` already supported by `DaemonResponse`).
- `Debug` is left null when scope is null OR when the collected list is empty (no point shipping an empty array). The brainstorm calls for `null` in the default case — keep it that way to satisfy `Assert.Equal(string.Empty, stderr)` regression.

**Patterns to follow:** Existing `CreateSuccessResponse` / `CreateProblemResponse` / `CreateErrorResponse` helpers — they already centralize all response construction in `DaemonServer`. Extend, don't bypass.

**Test scenarios:**
- Happy path (in-process, via daemon's `HandleClientAsync` over a paired stream pipe or directly via `DispatchAsync` if visible): a request with `Debug = true` for a fast command (`status` is good — no Roslyn needed) produces a response whose `Debug` field contains at least the canonical `DispatchAsync begin ...` and `DispatchAsync end ...` lines.
- Default mode: same request with `Debug = null` produces a response whose `Debug` field is `null`.
- Concurrent independence: two parallel `DispatchAsync` calls, one with `Debug = true` and one without, the first returns a non-empty debug array, the second returns `null`. Both contain only their own lines.
- Error path: a handler that throws still populates `response.Debug` when `Debug = true` (so users can see what led to the failure). Use a request that triggers `DAEMON_VALIDATION` or `INVALID_PARAMS`.
- Edge case: `Debug = true` but the handler logs nothing (hypothetical — `DispatchAsync` always emits begin/end, so this is hard in practice; document as "if zero lines, `response.Debug` is `null`").

**Verification:** New tests pass. Existing daemon and protocol tests untouched.

---

### U4. Client stops setting `DOTNET_AICRAFT_DEBUG=1` on spawned daemon

**Goal:** Remove the env-var propagation in `DaemonClient.StartDaemonAsync`. Spawned daemons no longer need the env var — per-request transport replaces it.

**Requirements:** Brainstorm "Per-request, nie globalnie" — DaemonClient.StartDaemonAsync przestaje ustawiać tę zmienną.

**Dependencies:** Logically depends on U3 (the replacement transport must exist), but the env-var line can be safely removed before U3 lands because no behavior currently observes the spawned daemon's own stderr.

**Files:**
- `src/DotnetAICraft/Daemon/DaemonClient.cs` (delete the `if (DebugLog.IsEnabled) proc.StartInfo.EnvironmentVariables["DOTNET_AICRAFT_DEBUG"] = "1";` block at ~line 97-98)
- `tests/DotnetAICraft.Tests/Daemon/DaemonClientProcessStartTests.cs` (extend with a test pinning that spawned daemon env does NOT contain `DOTNET_AICRAFT_DEBUG`)

**Approach:**
- Delete the two-line conditional that sets the env var. No replacement — manual `server start` users continue to set `DOTNET_AICRAFT_DEBUG=1` themselves before launching the daemon process; client-spawned daemons get debug per-request instead.

**Patterns to follow:** `CreateDaemonStartInfo` already returns the inert default `ProcessStartInfo`. Keep `StartDaemonAsync` minimal.

**Test scenarios:**
- Pin via `CreateDaemonStartInfo` (the conditional sits in `StartDaemonAsync` which calls `CreateDaemonStartInfo`; the env var assignment happens after). The cleanest test is on the wider `StartDaemonAsync` path, but that's process-launching. Acceptable alternative: extract the env-var-set into a small testable helper, or test against `proc.StartInfo.EnvironmentVariables` by constructing the start info via the same factory the production code uses.
- Test: with `DebugLog.IsEnabled = true`, the `ProcessStartInfo` used to spawn the daemon does NOT contain a `DOTNET_AICRAFT_DEBUG` entry (or its value is empty / matches the parent's, which is the default behavior — be explicit).
- Test: with `DebugLog.IsEnabled = false`, same — no env var set.

**Verification:** Test passes. Manual smoke: `--debug refs ...` against a client-spawned daemon still surfaces daemon-side debug lines (because of U3, not because of the env var).

---

### U5. End-to-end regression tests for the four success criteria

**Goal:** Lock in the four success criteria from the brainstorm with executable tests against the public surface.

**Requirements:** Origin "Sukces" section, all four bullets.

**Dependencies:** U1, U2, U3, U4.

**Files:**
- `tests/DotnetAICraft.Tests/Output/DebugSequencingTests.cs` (extend — currently has only the `WriteResponseDebug` unit tests; add scenarios spanning client+daemon flow)
- Or new file: `tests/DotnetAICraft.Tests/EndToEnd/DebugSequencingE2ETests.cs` — implementer's call based on existing test organization

**Approach:**
- Reuse the existing `Collection("Console output")` xUnit collection so stdout/stderr capture doesn't conflict.
- For tests that need a real daemon process, follow the pattern already used by other daemon-spawning tests in `tests/DotnetAICraft.Tests/Daemon/` (locate at execution time — likely a `TestDaemonHarness` or similar; implementer to discover the existing pattern).
- For pure flush-order tests, the unit-level capture (`ConsoleErrorCapture`, `ConsoleOutputCapture`) is sufficient.

**Test scenarios** (one per origin "Sukces" bullet):
- **Sukces 1 — Combined output**: `--debug` against any command produces stderr lines from both `[client]` and `[server]` components, in that order, before stdout receives any byte. Assert: stderr contains at least one `[client]` line and at least one `[server]` line; the first stdout byte arrives after the last stderr byte for this request.
- **Sukces 2 — Default mode is silent**: without `--debug`, run any command end-to-end against a fresh client-spawned daemon. Assert: client stderr is `Empty`. The daemon process was NOT spawned with `DOTNET_AICRAFT_DEBUG=1` (verified at startup), so daemon's own stderr is empty too — but the client drains it, so we cannot inspect it directly; the absence-of-env-var test in U4 is the proxy.
- **Sukces 3 — Per-request scoping under concurrency**: two `Task.Run` blocks issue concurrent requests on the same daemon, one with `request.Debug = true` and one with `request.Debug = null`. The first response's `Debug` is non-null and contains only its own request id in the captured lines; the second's `Debug` is null. (Daemon-level test via `DispatchAsync` is sufficient — does not require two real client processes.)
- **Sukces 4 — Spawn does not leak env var**: covered by U4's test; cross-reference rather than duplicate.
- **Sukces 5 — Manual `server start` with env var still writes daemon-side stderr**: with `DOTNET_AICRAFT_DEBUG=1` set in env when the daemon process starts, `DebugLog.ConfigureFromEnvironment` is invoked at startup and `DebugLog.Write` continues to emit to that process's stderr. Test in-process: set env, call `ConfigureFromEnvironment`, call `Write`, assert stderr contains the line. (Regression for the rejected-removal of global verbose.)

**Verification:** All five scenarios pass. The existing `--debug` smoke flow (manual) shows daemon-side lines appearing in addition to client lines.

---

## Scope Boundaries

Carried verbatim from `docs/brainstorms/2026-05-17-debug-console-output-requirements.md` "Poza zakresem":

### Outside this iteration

- Daemon background activity not tied to a request (startup banner, file-watcher events, idle-timeout shutdown, GC). Not request-scoped — not reported via `response.Debug`. Daemon-global verbose remains the only way to observe these live.
- Structured debug schema (levels, categories, structured fields). Flat preformatted lines as today.
- Component/level filtering (`--debug=server`, `--debug-level=trace`). All-or-nothing.
- Request-id correlation between client and daemon debug entries. Client already logs `requestId`; sufficient.
- File sink, syslog, OpenTelemetry. Stderr only.
- Envelope schema changes (`DaemonRequest.Debug` and `DaemonResponse.Debug` stay; we just start using them).

### Deferred to Follow-Up Work

- Documentation refresh (`README.md`, `CHANGELOG.md`, `dotnet-aicraft` skill docs) reflecting the per-request behavior. Lands in a separate doc-only commit after the behavior commits stabilize.

---

## System-Wide Impact

- **Wire protocol**: `response.Debug` semantics change from "unused / any object" to "string[] of preformatted lines". The field already exists and is typed `object?`, so no client/daemon version mismatch — an older client just sees an object and ignores it; an older daemon never populates it. No breaking change.
- **Manual daemon-launch UX**: unchanged. `dotnet aicraft server start` + `DOTNET_AICRAFT_DEBUG=1` still works as a debugging tool.
- **Client-spawn UX**: improved — `--debug` now surfaces daemon trace too.
- **Concurrent client behavior**: improved — each request's debug is private to that request.

---

## Risks & Mitigations

- **Risk**: `AsyncLocal<T>` flow doesn't survive an unexpected continuation pattern (e.g., a handler that schedules work onto a non-flowing thread pool path).
  **Mitigation**: All daemon handlers are `async Task<object>` reached via `await` from `DispatchAsync` — `AsyncLocal` flows across `await` by design. The U2 concurrency test pins this.
- **Risk**: Large debug payloads bloat the response envelope.
  **Mitigation**: Origin notes the response is already fully buffered server-side before send; adding a few KB of debug lines is irrelevant. No streaming needed.
- **Risk**: Test that asserts "stdout starts after last stderr write" is racy on Windows console buffers.
  **Mitigation**: Capture stdout/stderr via the existing `ConsoleOutputCapture` / `ConsoleErrorCapture` test helpers (already used by `DebugSequencingTests`), which capture at the `Console.Out` / `Console.Error` writer level — deterministic, no buffer race.

---

## Deferred Implementation Notes

- Exact name of the capture-scope type and its method (`BeginCapture` vs `BeginRequestScope` vs `WithDebugCapture`) — implementer's call.
- Exact placement of the `using` scope in `DaemonServer` (inside `DispatchAsync` vs around it in `HandleClientAsync`). Both work; `DispatchAsync` is the cleaner seam because the begin/end log lines should be captured.
- Whether to expose `DebugCaptureScope` as `public` (for tests) or `internal` with `InternalsVisibleTo` (already set in this repo — verify and reuse).

---

## Verification (overall)

- All new and modified tests pass on Windows and Linux runners.
- Manual smoke: `dotnet aicraft --debug refs Foo.Bar` with no daemon running shows interleaved `[client]` and `[server]` debug lines on stderr, then the JSON result on stdout.
- Manual smoke: `dotnet aicraft refs Foo.Bar` (no `--debug`) produces nothing on stderr and the result on stdout.
- Manual smoke: `DOTNET_AICRAFT_DEBUG=1 dotnet aicraft server start` continues to write daemon-side debug to that terminal as before.
