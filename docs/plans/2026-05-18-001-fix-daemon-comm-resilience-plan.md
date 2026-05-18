---
title: "fix: Daemon communication resilience and broken-pipe regression"
status: completed
type: fix
created: 2026-05-18
origin: docs/brainstorms/daemon-comm-resilience-requirements.md
---

# fix: Daemon communication resilience and broken-pipe regression

## Summary

Eliminate the `IOException: Broken pipe` regression that fires on the second `dotnet aicraft` invocation when `--idle-timeout` is passed against an already-running daemon, and harden the client transport so no daemon-comm failure ever escapes as an unhandled exception to the user.

The bug is a protocol-shape violation: `DaemonStartupCoordinator.ConnectOrStartAsync` issues a `setIdleTimeout` round-trip on a fresh `DaemonClient` before the command's own `SendAsync`, but the server is one-request-per-connection by design (`DaemonServer.HandleClientAsync` disposes the `NetworkStream` after one response). The second send hits a half-closed socket. The `IdleTimeoutMinutes` field on `DaemonRequest` already carries the timeout per-request and the server applies it via `ApplyRequestIdleTimeoutIfPresent` — so the pre-send is redundant and can be deleted (see origin: `docs/brainstorms/daemon-comm-resilience-requirements.md`).

Around that primary fix, layer transport-failure handling so future protocol mistakes or daemon crashes degrade to a clean retry + structured error envelope rather than a stack trace.

---

## Problem Frame

- **Symptom:** `Unhandled exception: System.IO.IOException: Unable to write data to the transport connection: Broken pipe.` thrown from `DaemonClient.SendAsync` at the `WriteLineAsync` call when the user runs two consecutive commands with `--idle-timeout`.
- **Trigger:** `--idle-timeout` is passed AND a daemon is already running for the solution. First invocation starts the daemon and passes `--idle-timeout` via CLI args (no pre-send needed). Second invocation reuses the existing daemon and the coordinator issues an `ApplyIdleTimeoutAsync` round-trip before the actual command.
- **Root location:** `src/DotnetAICraft/Daemon/DaemonStartupCoordinator.cs:95-96`.
- **Why it surfaces as a crash:** there is no top-level firewall around daemon-comm exceptions in the command entry points (`CommandHelpers.SendOrWriteValidationErrorAsync` only catches `DaemonClientValidationException`, not `IOException` / `SocketException`).
- **Platform note:** reproduced on WSL2; not Windows-specific despite the original user guess. The bug is in protocol shape, not OS plumbing.

## Requirements Traceability (origin → plan)

| Origin FR | Covered by |
|---|---|
| FR-1: Connection liveness check / transport-failure handling | U2 |
| FR-2: Transparent restart-and-retry on transport failure | U3 |
| FR-3: Stale-socket detection on connect | U4 |
| FR-4: Top-level exception firewall in CLI entrypoint | U5 |
| FR-5: Fix the `--idle-timeout` two-send bug at its source | U1 |

Success criteria from origin doc map to test scenarios in U1 (repro fix), U3 (restart-after-kill), U4 (restart-after-socket-deleted), and U5 (no unhandled exceptions).

---

## Key Technical Decisions

- **Delete the pre-send rather than keep-and-reconnect.** `DaemonRequest.IdleTimeoutMinutes` + `DaemonServer.ApplyRequestIdleTimeoutIfPresent` already carry the timeout end-to-end on every per-command request. Removing `ApplyIdleTimeoutAsync` from the existing-daemon path eliminates a round-trip and a protocol-shape footgun. (Rejected alternative: dispose-and-reconnect after the pre-send. Functionally correct, strictly worse.)
- **Keep `setIdleTimeout` as a server-side command.** The standalone `dotnet aicraft server set-idle-timeout` CLI surface still needs it; only the implicit pre-send is removed.
- **Single-shot retry semantics.** Retry once on transport failure, then surface a structured `DAEMON_TRANSPORT_FAILED` envelope. All current commands are read-only, so single retry is safe; this assumption is recorded for the future `rename` mutation path.
- **Restart-and-retry is opt-in to transport-class faults only.** Validation errors (`DaemonClientValidationException`), protocol contract violations, and `DAEMON_RESPONSE_TIMEOUT` are NOT retried — they indicate a server-side decision, not a transport failure, and retrying would mask real bugs.
- **Top-level firewall in `Program.cs`, not per-command.** A single `try/catch` wrapping `root.Parse(args).InvokeAsync()` is simpler than ten per-command wrappers and guarantees coverage.
- **Restart notice on stderr.** When auto-restart fires, emit `[dotnet-aicraft] Daemon connection lost. Restarting...` to match the existing startup notice tone at `DaemonClient.cs:74`.

---

## Implementation Units

### U1. Remove the redundant `setIdleTimeout` pre-send on existing-daemon attach

**Goal:** Eliminate the protocol-shape bug at its source. After this unit, two consecutive `dotnet aicraft <cmd> --idle-timeout 30m` calls succeed.

**Requirements:** FR-5 (origin). Closes the primary user-reported regression.

**Dependencies:** none — this is the smallest, safest change and should land first.

**Files:**
- `src/DotnetAICraft/Daemon/DaemonStartupCoordinator.cs` — delete the `if (idleTimeout is not null) await DaemonClient.ApplyIdleTimeoutAsync(...)` branch at lines 95-96 and the surrounding try/dispose wrapper that exists only for that call.
- `src/DotnetAICraft/Daemon/DaemonClient.cs` — `ApplyIdleTimeoutAsync` (lines 466-484) becomes unused by the coordinator. Keep it: the standalone `server set-idle-timeout` command path may still depend on it. Verify via aicraft refs and remove only if there are zero callers after U1.
- `tests/DotnetAICraft.Tests/Daemon/DaemonStartupCoordinatorTests.cs` — add coverage.

**Approach:**

The `idleTimeout` value still arrives at the server on every request via `DaemonRequest.IdleTimeoutMinutes` (already wired through `CommandHelpers.TryParseIdleTimeoutMinutes` → `DaemonClient.SendAsync` → `DaemonServer.ApplyRequestIdleTimeoutIfPresent`). The pre-send was double-applying the same value. Delete it. The first-run path that passes `--idle-timeout` via CLI args to the new daemon process (`DaemonClient.StartDaemonProcessAsync`, lines 82-93) is independent and unchanged.

**Patterns to follow:**
- Existing test conventions in `DaemonStartupCoordinatorTests.cs` for fixture setup of a real Unix-socket daemon.

**Test scenarios:**
- *Repro fix:* start a daemon, connect a second client with a non-null `DaemonIdleTimeoutSetting`, call `ConnectOrStartAsync`, then issue a normal command (e.g., `symbols`) on the returned client. The command completes without throwing.
- *Per-request timeout still applied:* after U1, send a command with `idleTimeoutMinutes` set and verify the server applies it (existing test in `DaemonIdleTimeoutTests.cs` should still pass; add an assertion that the value reaches `ApplyRequestIdleTimeoutIfPresent` via the request envelope, not via a separate `setIdleTimeout` call).
- *No `setIdleTimeout` call on attach:* assert via debug log or a test seam that `ConnectOrStartAsync` against an existing daemon issues zero `setIdleTimeout` requests.
- *First-run path unchanged:* when no daemon exists, `--idle-timeout` is still propagated via process args (existing test in `DaemonClientProcessStartTests.cs` should remain green).

**Verification:** The repro from the brainstorm — two back-to-back `dotnet aicraft refs ... --idle-timeout 30m` calls — produces clean responses on both invocations. New unit test passes; no existing test regresses.

---

### U2. Classify transport failures distinctly from validation failures

**Goal:** Introduce a typed exception (or a discriminated error category) the upper layers can catch to trigger restart-and-retry, without conflating it with `DaemonClientValidationException`.

**Requirements:** FR-1 (origin).

**Dependencies:** U1 (lands first to make the repro test passable in isolation).

**Files:**
- `src/DotnetAICraft/Daemon/DaemonClient.cs` — wrap the `WriteLineAsync` / `FlushAsync` / `ReadLineAsync` calls inside `SendAsync` (and `ReadResponseLineOrThrowAsync`) to translate `IOException`, `SocketException`, and `ObjectDisposedException` into a new `DaemonTransportException` carrying an `ErrorInfo` with code `DAEMON_TRANSPORT_FAILED`.
- `src/DotnetAICraft/Daemon/DaemonClient.cs` — new sealed class `DaemonTransportException : Exception` (parallel to `DaemonClientValidationException` at lines 487-496).

**Approach:**

The current code lets raw `IOException` escape from `SendAsync`. Translate it at the daemon-comm boundary so the layer above sees a single, named exception type with structured detail (request command, inner exception type, socket-path hash for diagnostics — not the full path). Do *not* catch `DaemonClientValidationException` here; that's a separate, intentional surface.

Keep the existing `DAEMON_RESPONSE_TIMEOUT` and `DAEMON_RESPONSE_INCOMPLETE` paths in `ReadResponseLineOrThrowAsync` (lines 227-284) untouched — those are already structured as `DaemonClientValidationException` and represent server-decision states, not transport faults.

**Patterns to follow:**
- `DaemonClientValidationException` shape at `DaemonClient.cs:487-496` for the new exception type.
- Error code convention: existing codes follow `DAEMON_*` pattern (`DAEMON_DRAINING`, `DAEMON_STARTUP_FAILED`, `DAEMON_RESPONSE_TIMEOUT`).

**Test scenarios:**
- *Write to closed socket throws DaemonTransportException:* unit test that creates a `DaemonClient` against a socket whose listener has been shut down between connect and send. Assert the type is `DaemonTransportException` and `ErrorInfo.Code` is `DAEMON_TRANSPORT_FAILED`.
- *Read EOF mid-response throws DaemonTransportException:* fixture where the server accepts, reads the request, then closes without writing. (Distinguish from `DAEMON_RESPONSE_INCOMPLETE` if needed — incomplete-then-EOF is the boundary case to confirm.)
- *Validation errors not reclassified:* a server-side `INVALID_PARAMS` response still surfaces as `DaemonClientValidationException`, not `DaemonTransportException`.

**Verification:** New exception type covers the transport failure cases; existing validation-error tests still pass unchanged.

---

### U3. Restart-and-retry layer in the daemon-comm boundary

**Goal:** When `DaemonTransportException` fires on a `SendAsync`, dispose the dead client, restart the daemon via the existing coordinator path, and re-issue the request exactly once. If the retry also fails, surface a structured error envelope; never propagate the raw exception.

**Requirements:** FR-2 (origin).

**Dependencies:** U2 (needs the typed exception).

**Files:**
- `src/DotnetAICraft/Commands/Shared/CommandHelpers.cs` — replace the current `SendOrWriteValidationErrorAsync` flow with a wrapper that owns the client lifetime, catches `DaemonTransportException` on the first attempt, restarts via `DaemonClient.ConnectOrStartAsync`, retries the send once, and emits an envelope on the second failure.
- `src/DotnetAICraft/Commands/Shared/CommandHelpers.cs` — change `ConnectOrWriteValidationErrorAsync` callers so the client is reachable to the retry layer (or fold connect + send into a single retrying call; either shape works — pick whichever requires fewer call-site edits).
- `src/DotnetAICraft/Daemon/DaemonClient.cs` — emit `[dotnet-aicraft] Daemon connection lost. Restarting...` on stderr when the retry path fires (mirror the existing `Starting analysis daemon...` notice at line 74).
- `tests/DotnetAICraft.Tests/Daemon/` — new file `DaemonRetryOnTransportFailureTests.cs`.

**Approach:**

Per origin, retry is single-shot to avoid loops; all current commands are read-only, so re-sending is safe. The retry call must:

1. Catch `DaemonTransportException` from the first `SendAsync`.
2. `DisposeAsync` the dead `DaemonClient`.
3. Call `DaemonClient.ConnectOrStartAsync` again (which will go through stale-socket detection from U4).
4. Re-issue the exact same `SendAsync(command, params, idleTimeoutMinutes, page)`.
5. On second-attempt success, return the response. On second-attempt failure (transport or otherwise), write a `DAEMON_TRANSPORT_FAILED` envelope to stdout and return null.

The retry layer must NOT catch `DaemonClientValidationException` and retry — that's a server decision and re-sending will produce the same result. Pass-through to the existing validation-error handler.

**Patterns to follow:**
- The existing `Func<Task<DaemonResponse>>` indirection in `CommandHelpers.cs:48-64` is the natural seam — wrap it.
- `JsonOutput.WriteError(code, message, details)` for the envelope output.

**Execution note:** start with the failing test from the U3 first scenario, then make it pass — the retry semantics are easy to mis-implement (e.g., double-disposing, leaking the second client) and a test fixture catches that immediately.

**Test scenarios:**
- *Retry succeeds when daemon is killed mid-flight:* start a daemon, get a `DaemonClient`, kill the daemon process (or close the listener socket), call `SendAsync` via the retry wrapper. Assert: stderr notice emitted, second daemon process started, response returned cleanly, original client disposed.
- *Retry fails cleanly when daemon refuses to restart:* simulate a startup failure (e.g., bad solution path) after the first transport failure. Assert: structured `DAEMON_TRANSPORT_FAILED` envelope on stdout, non-zero exit code, no unhandled exception.
- *Validation errors are not retried:* server returns `INVALID_PARAMS` on the first call. Assert: no restart attempted, single envelope written, exits normally.
- *Single-shot guarantee:* fixture where both attempts fail. Assert: exactly two `SendAsync` invocations occurred (not three or more).
- *Daemon-dispose on transport failure:* the original `DaemonClient` is disposed before the second `ConnectOrStartAsync` runs.

**Verification:** Killing the daemon between two CLI invocations produces a successful second invocation with a stderr restart notice. No stack traces.

---

### U4. Stale-socket detection in `TryConnectAsync`

**Goal:** When the socket file exists but no daemon is actually listening, detect it and clean up before attempting `ConnectAsync`, so the start-new path can run.

**Requirements:** FR-3 (origin).

**Dependencies:** none structurally; sequenced after U3 so the retry layer can rely on the cleanup happening on the retry's `ConnectOrStartAsync` call.

**Files:**
- `src/DotnetAICraft/Daemon/DaemonClient.cs` — extend `TryConnectAsync` (lines 30-50). Today it returns `null` on a `ConnectAsync` failure; on Linux/WSL2 a stale socket file with no listener triggers ECONNREFUSED, and the file remains. The downstream `DaemonStartupCoordinator.PrepareServerStartAsync` already has `TryDeleteStaleSocket` for the server-start side (lines 219-286), but the connect-attempt side currently relies on the `existing is not null` branch and doesn't trigger startup when the socket file lingers from a crashed daemon.
- `src/DotnetAICraft/Daemon/DaemonStartupCoordinator.cs` — `ConnectOrStartAsync` (lines 82-128) checks `existing is not null`; if `null`, it falls through to `StartDaemonProcessAsync`. The new daemon's `PrepareServerStartAsync` already cleans up via `TryDeleteStaleSocket`. **Reuse this path** rather than duplicating cleanup in `TryConnectAsync` — confirm during implementation that the server-start path is reached on connect-failure and that `TryDeleteStaleSocket` handles the stale-file case.
- `tests/DotnetAICraft.Tests/Daemon/DaemonStartupCoordinatorTests.cs` — add coverage for stale-socket-on-connect.

**Approach:**

Read `DaemonStartupCoordinator.TryDeleteStaleSocket` (lines 219-286) and `IsDaemonActiveAsync` (lines 199-217) first. The server-start path already handles this case via liveness probing. The most likely outcome of this unit is: **a documentation/test-coverage change rather than new logic**, confirming the existing path covers the scenario and adding a regression test. If a gap exists (e.g., `TryConnectAsync` returns success for a half-closed socket), close it minimally.

**Patterns to follow:**
- The existing `TryDeleteStaleSocket` outcome counters and `StaleSocketOutcomeRecorded` event surface — do not break them.

**Test scenarios:**
- *Stale socket file from crashed daemon is cleaned up:* place a stale `.sock` file at the expected path, no listener. Call `ConnectOrStartAsync`. Assert: a fresh daemon starts, the stale file is removed, the returned client connects to the new daemon.
- *Real listener is not mistaken for stale:* control case — a live daemon's socket is not deleted.
- *Reparse-point safety policy preserved:* the existing reparse-point safety logic in `EvaluateReparseSafety` is not bypassed.

**Verification:** Stale-socket regression test passes; existing `DaemonStartupCoordinatorTests` remain green; stale-socket outcome counters still increment on the heal path.

---

### U5. Top-level exception firewall in `Program.cs`

**Goal:** No code path can propagate an unhandled exception (transport, parse, MSBuild registration, anything) out of `Main`. All escapees are converted to a structured error envelope on stdout with a non-zero exit code.

**Requirements:** FR-4 (origin).

**Dependencies:** none structurally; should land last because U1–U4 reduce the surface this catches.

**Files:**
- `src/DotnetAICraft/Program.cs` — wrap the body in a `try` with a top-level `catch (Exception ex)` that writes a `JsonOutput.WriteError("INTERNAL_ERROR", ex.Message, new { type = ex.GetType().FullName })` envelope (or `DAEMON_TRANSPORT_FAILED` for transport-typed escapees) and returns a non-zero exit code.
- `tests/DotnetAICraft.Tests/` — integration test that spawns the CLI with a guaranteed-failure scenario and asserts the output is a JSON envelope, not a stack trace.

**Approach:**

The current `Program.cs` is top-level statements with a single `return await root.Parse(args).InvokeAsync();` at line 57. Wrap it in `try`/`catch`. The catch must:

1. Map known exception types to known error codes (`DaemonTransportException` → `DAEMON_TRANSPORT_FAILED`, `DaemonClientValidationException` → its carried code, generic → `INTERNAL_ERROR`).
2. Write the envelope via `JsonOutput.WriteError` to keep formatting consistent.
3. Return a non-zero exit code so shell pipelines correctly detect failure.

Be careful with the MSBuildLocator block at lines 8-19 — its `InvalidOperationException` should also be caught by the firewall and emit a recognizable error code (e.g., `MSBUILD_REGISTRATION_FAILED`).

**Patterns to follow:**
- `JsonOutput.WriteError` everywhere envelopes are emitted today.
- Existing error-code naming (`DAEMON_*`, `INVALID_*`, `INTERNAL_ERROR`).

**Test scenarios:**
- *Unhandled exception in a command is caught:* inject a deliberate throw inside a command (or use a known-failing path) and assert the process exits with a non-zero code AND stdout contains a JSON envelope, not "Unhandled exception:".
- *Transport-typed exception gets the right code:* if `DaemonTransportException` somehow reaches Main (e.g., a path not covered by U3), it is caught and emitted with code `DAEMON_TRANSPORT_FAILED`.
- *Validation exception carries its code:* a `DaemonClientValidationException` reaching Main is emitted with the exception's own `Error.Code`.
- *MSBuild registration failure is structured:* simulate the MSBuildLocator failure path (or unit-test the catch arm directly) and assert envelope output.
- *Normal success path unchanged:* a successful command exits 0 with normal JSON output.

**Verification:** Grep the codebase for `Unhandled exception` after a deliberately broken run — it should never appear in CLI output. CI passes.

---

## Scope Boundaries

### In scope
- Fix the `--idle-timeout` two-send regression.
- Translate transport-class exceptions into a typed, catchable form.
- Single-shot restart-and-retry on transport failures.
- Stale-socket-on-connect cleanup (likely a coverage/test gap rather than new code).
- Top-level exception firewall in `Program.cs`.

### Deferred to Follow-Up Work
- Removing the standalone `setIdleTimeout` server command if it has no remaining callers after U1. Tracked as an open question in the origin doc; resolve after U1 lands and `aicraft refs` against `setIdleTimeout` confirms the call graph.
- Health-check / heartbeat command for proactive liveness detection.
- Daemon supervisor / watchdog process.
- Telemetry around restart frequency (counters exist for stale-socket healing; mirror that shape if/when we add restart metrics).

### Outside this product's identity
- Persistent client-side socket pooling or request pipelining.
- Multi-tenant daemon shared across solutions.
- Reworking the JSON wire protocol.

---

## Risks and Mitigations

- **Risk:** Single-shot retry semantics become unsafe when `rename` (mutation) ships. **Mitigation:** record the assumption explicitly (U3 approach notes); when `rename` lands, add a per-command idempotency flag that gates the retry.
- **Risk:** `DaemonTransportException` swallows a real bug because it's catch-all over `IOException`. **Mitigation:** keep `DaemonClientValidationException` strictly separate (U2 explicit decision); structured detail in the envelope carries the inner exception type for diagnostics.
- **Risk:** The top-level firewall hides a crash during local dev. **Mitigation:** when `DOTNET_AICRAFT_DEBUG=1`, also write the stack trace to stderr (alongside the envelope on stdout). Existing `DebugLog` plumbing already gates on this env var.
- **Risk:** Stale-socket cleanup races a freshly-starting daemon and deletes its socket. **Mitigation:** the existing `DaemonStartupLock` and `IsDaemonActiveAsync` liveness probe (3 attempts with 100ms backoff) already guard this; U4 reuses that path rather than adding parallel cleanup.

---

## Verification Plan

1. **Reproduce, then fix.** Run the exact brainstorm repro (two `dotnet aicraft refs` calls with `--idle-timeout 30m` against a real solution) before U1, observe the broken pipe. After U1, both calls succeed.
2. **Unit-test each new boundary** as enumerated per-unit.
3. **Integration test for U5** by spawning the CLI in a subprocess and asserting on its stdout/stderr/exit-code.
4. **Run the full daemon test suite** (`dotnet test tests/DotnetAICraft.Tests`) and confirm no regressions in existing `DaemonStartupCoordinatorTests`, `DaemonIdleTimeoutTests`, `DaemonResponseEnvelopeTests`, `DaemonClientProcessStartTests`.
5. **Smoke test on WSL2 and native Linux.** Native Windows can follow if access is available; the bug is not OS-specific so cross-platform smoke is confirmatory.

---

## Sequencing Summary

```
U1 (delete pre-send)  ──► repro fixed, ships independently
        │
U2 (typed transport exception)  ──► no behavior change yet
        │
U3 (restart-and-retry layer)  ──► graceful recovery
        │
U4 (stale-socket-on-connect coverage)  ──► confirms path, adds test
        │
U5 (top-level firewall in Program.cs)  ──► last-line safety net
```

U1 is shippable on its own and closes the user-reported bug. U2–U5 are the hardening layer the origin document asked for as the second requirement.
