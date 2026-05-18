# Daemon Communication Resilience — Requirements

**Date:** 2026-05-18
**Status:** Draft (brainstorm output, awaiting confirmation)
**Scope tier:** Standard

## Problem

The CLI throws an unhandled `System.IO.IOException: Broken pipe` at `src/DotnetAICraft/Daemon/DaemonClient.cs:218` (the `await _writer.WriteLineAsync(...)` inside `SendAsync`) on the **second** invocation of a `dotnet aicraft` command against the same solution. The first invocation completes normally (in the reported case, returning a clean `INVALID_PARAMS` for an unknown symbol); the second invocation fails before any response is read.

Two requirements emerge from this:

1. **Eliminate the crash.** The second-call broken-pipe condition must not occur in normal use.
2. **Eliminate the stack trace.** *No* daemon-communication failure — broken pipe, premature EOF, stale socket, daemon process gone, timeout — should escape to the user as an unhandled exception. The CLI must transparently recover (most likely by restarting the daemon) and either complete the request or return a structured error.

## Why this matters

`aicraft` is the mandated tool for .NET symbol lookups in this user's workflow (per global CLAUDE.md). A single stack trace breaks scripted pipelines (`| head`, `2>&1 | grep`), looks broken to other agents that shell out to it, and forces manual cleanup (kill the daemon, delete the socket). Reliability of the CLI surface is load-bearing for the tool's role.

## Observed evidence

- Repro host: WSL2 (`argawryl@LUBN8461`), not Windows as the user initially guessed.
- First call: `dotnet aicraft refs ... --idle-timeout 30m` → returns `error INVALID_PARAMS` envelope (clean exit).
- Second call (identical args): unhandled `IOException: Broken pipe` thrown from `StreamWriter.WriteLineAsync` → `DaemonClient.SendAsync` at line 223 in the stack (line 218 in current source).
- Idle timeout was set to 30m, so the daemon should not have idled out between back-to-back commands.

## Root cause (confirmed by code reading)

The server's `HandleClientAsync` (`src/DotnetAICraft/Daemon/DaemonServer.cs:167`) processes exactly **one** request per accepted socket and then closes the connection — that is by design.

When `--idle-timeout` is passed AND a daemon is already running, `DaemonStartupCoordinator.ConnectOrStartAsync` (`src/DotnetAICraft/Daemon/DaemonStartupCoordinator.cs:95-96`) calls `DaemonClient.ApplyIdleTimeoutAsync(existing, idleTimeout)`, which sends a `setIdleTimeout` request on the new `DaemonClient`. The server processes it and closes the socket. Control returns to `CommandHelpers.SendOrWriteValidationErrorAsync`, which then calls `client.SendAsync(command, ...)` — the *actual* command — on **the same already-closed connection**. The write at `DaemonClient.cs:218` hits a half-closed socket and throws `Broken pipe`.

This explains the exact reproducer:

- Without `--idle-timeout`: one `SendAsync` per client, no second write → fine.
- With `--idle-timeout` + no existing daemon (first call): the timeout is passed via CLI args to the new daemon process (`DaemonClient.cs:82-93`), no `setIdleTimeout` round-trip is issued → fine.
- With `--idle-timeout` + existing daemon (second call): two `SendAsync` calls on one client → second write hits a closed socket → broken pipe.

Not WSL2-specific. Not Windows-specific. Pure protocol-design fault that only manifests when a pre-command `setIdleTimeout` is sent.

## Goals

1. The second consecutive `dotnet aicraft` command against the same solution succeeds (no broken pipe).
2. The user-facing CLI never emits an unhandled exception/stack trace for daemon-comm failures. The exit channel is always either (a) the requested result or (b) a structured `error` envelope on stdout/stderr matching the existing protocol.
3. When the daemon is missing, dead, unresponsive, or returns a transport-level failure, the client transparently restarts it once and retries the request.

## Non-goals

- Changing the one-request-per-connection server design.
- Persistent client-side socket pooling or pipelining of requests.
- Making the daemon multi-tenant or shared across solutions beyond its current model.
- Reworking the JSON wire protocol.

## Functional requirements

### FR-1: Connection liveness check before `SendAsync`

Before writing the request, the client must confirm the socket is still writable, OR be prepared to treat a transport-level failure (`IOException`, `SocketException`, `ObjectDisposedException`) on the write/read path as a recoverable condition rather than a fatal exception.

### FR-2: Transparent restart-and-retry on transport failure

When the client catches a transport-level failure during `SendAsync` (write, flush, or read), it must:

1. Dispose the current connection.
2. Verify the daemon is actually dead (e.g., socket file gone, `ConnectAsync` fails) vs. transient.
3. If dead: clean up the stale socket file, start a fresh daemon via the existing `DaemonStartupCoordinator` path, and re-send the same request **once**.
4. If the retry also fails: emit a structured error envelope (new code, e.g., `DAEMON_TRANSPORT_FAILED`) — never throw to `Main`.

The retry is **single-shot** to avoid infinite loops. Idempotency: all current commands are read-only/query-shaped, so single retry is safe; this assumption is recorded explicitly so future write-shaped commands (e.g., `rename`) revisit it.

### FR-3: Stale-socket detection on connect

`TryConnectAsync` must detect a stale `.sock` file (file exists but no listener) and clean it up before attempting startup. Today the file's mere existence blocks the start-new path.

### FR-4: Top-level exception firewall in CLI entrypoint

`Program.cs` (or the per-command `Entry.ExecuteAsync` wrappers) must wrap all daemon calls in a final catch that converts any escaped exception into the standard error envelope on stdout with a non-zero exit code. No path should produce `Unhandled exception:` text.

### FR-5: Fix the `--idle-timeout` two-send bug at its source

Stop issuing a separate `setIdleTimeout` round-trip when reusing an existing daemon. The request envelope already carries `IdleTimeoutMinutes` (`DaemonRequest.IdleTimeoutMinutes`, applied server-side in `DaemonServer.ApplyRequestIdleTimeoutIfPresent`), so the per-request path covers the case end-to-end.

Concretely:

- Delete the `if (idleTimeout is not null) await DaemonClient.ApplyIdleTimeoutAsync(existing, idleTimeout);` branch at `src/DotnetAICraft/Daemon/DaemonStartupCoordinator.cs:95-96`.
- Keep parsing/validating the `--idle-timeout` value at the client boundary (`DaemonClient.ConnectOrStartAsync`) so bad values still fail fast before any send.
- The `setIdleTimeout` command itself stays on the server (used by the `server set-idle-timeout` CLI path and the existing tests) — only the implicit pre-command send is removed.
- Add a test that reproduces the bug: start a daemon, then run a second client call with `--idle-timeout` set, and assert it succeeds and does not throw.

Alternative (rejected) shape: dispose `existing` after `ApplyIdleTimeoutAsync` and return a fresh client via another `TryConnectAsync`. Works, but keeps the redundant round-trip — strictly worse than removing it.

## Success criteria

- The repro case (two consecutive `refs` calls with an invalid symbol name) completes both times with a clean `INVALID_PARAMS` envelope.
- A test fixture that kills the daemon between two client calls produces a clean second response (after a transparent restart).
- A test fixture that deletes the socket file between two client calls produces the same.
- No code path in `src/DotnetAICraft/**` propagates an exception out of `Main` for daemon-comm failures. (Internal exceptions inside Roslyn analysis still surface as `INTERNAL_ERROR` envelopes — already handled at `DaemonServer.cs:208`; this scope is the client side.)
- All new behavior covered by tests in `tests/DotnetAICraft.Tests/Daemon/` (existing test conventions: see `DaemonStartupCoordinatorTests.cs`, `DaemonResponseEnvelopeTests.cs`).

## Open questions / assumptions to validate in planning

- **Assumption:** all current commands are safely idempotent for single-retry. Confirm against the full command list (`refs`, `definition`, `rename`, `impls`, `symbols`, etc.) — `rename` may need a different treatment if it ever mutates files.
- **Question:** should the user see a one-line `[dotnet-aicraft] Restarting daemon...` notice on stderr when an auto-restart occurs, or stay silent? Recommendation: emit the notice (consistent with the existing `[dotnet-aicraft] Starting analysis daemon...` line at `DaemonClient.cs:74`).
- **Question:** after FR-5 removes the implicit `setIdleTimeout` send, is the explicit `dotnet aicraft server set-idle-timeout` command still wanted as a user-facing surface, or can the standalone `setIdleTimeout` server handler be removed entirely? Recommendation: keep it — it's still useful for "change timeout on a running daemon without issuing a query."

## Out of scope (deferred)

- Health-check / heartbeat command for proactive liveness detection.
- Daemon supervisor / watchdog process.
- Telemetry around restart frequency.

## Suggested next step

`/ce-plan` — translate this into an implementation plan: (1) reproduce + diagnose, (2) client-side retry layer + stale-socket detection, (3) top-level exception firewall, (4) server-side fix for the underlying death, (5) tests at each layer.
