---
title: "feat: Add daemon idle auto-shutdown"
type: feat
status: active
date: 2026-05-02
origin: docs/brainstorms/daemon-idle-auto-shutdown-requirements.md
---

# feat: Add daemon idle auto-shutdown

## Summary

This plan adds session-scoped idle auto-shutdown for the daemon with a default of 60 minutes, a CLI override, and `off` to disable automatic shutdown. The implementation keeps the current daemon lifecycle model (one daemon per solution), adds race-safe idle timer management inside the daemon process, and preserves graceful restart behavior via existing `ConnectOrStartAsync` flow.

---

## Problem Frame

Today the daemon lives until manual stop or machine restart, which keeps an unnecessary background process during long idle periods (see origin: docs/brainstorms/daemon-idle-auto-shutdown-requirements.md). We want to keep warm-start performance while automatically reclaiming resources when the daemon is unused.

---

## Requirements

### Idle lifecycle behavior
- R1. Auto-shutdown daemon after effective idle timeout from the later of daemon startup completion or last handled request completion.
- R2. Reset idle deadline after each handled request completion.
- R3. Default timeout is 60 minutes.
- R8. Auto-shutdown is graceful and next command can start a fresh daemon session.

### Session-scoped configuration
- R4. User can pass timeout via CLI parameter as either `off` or a positive duration in the documented CLI duration format.
- R5. Parameter value applies to current daemon session only.
- R6. `off` disables auto-shutdown for current daemon session.

### Validation and safety
- R7. Invalid timeout returns clear error and does not mutate timeout state (and if no daemon session exists yet, does not start one).

**Origin acceptance examples:**
- AE1 (R1, R3), AE2 (R2), AE3 (R4, R5), AE4 (R6), AE5 (R7), AE6 (R8)

---

## Scope Boundaries

- No persistent/global/project-level timeout config storage.
- No change to multi-daemon topology beyond current one-daemon-per-solution behavior.
- No advanced policy/authorization/rate-limit system for timeout updates.

### Deferred to Follow-Up Work

- Add daemon lifecycle telemetry metrics (idle shutdown counts, timeout source distribution) in a separate observability-focused change.

---

## Context & Research

### Relevant Code and Patterns

- Daemon loop and request dispatch: `src/DotnetAi/Daemon/DaemonServer.cs`
- Auto-start and reconnect behavior: `src/DotnetAi/Daemon/DaemonClient.cs`
- Server command surface: `src/DotnetAi/Commands/ServerCommand.cs`
- Existing CLI option/validation style: `src/DotnetAi/Commands/RefsCommand.cs`, `src/DotnetAi/Commands/RenameCommand.cs`, `src/DotnetAi/Commands/CallersCommand.cs`
- Request/response models: `src/DotnetAi/Models/Models.cs`
- JSON output/error behavior: `src/DotnetAi/Output/JsonOutput.cs`

### Institutional Learnings

- No `docs/solutions/` entries exist in this repository yet; this plan uses local code patterns + external docs and should produce a follow-up learning doc after implementation.

### External References

- .NET cancellation model: https://learn.microsoft.com/en-us/dotnet/standard/threading/cancellation-in-managed-threads
- `CancellationTokenSource` and linked tokens: https://learn.microsoft.com/en-us/dotnet/api/system.threading.cancellationtokensource?view=net-9.0
- Cancellable `Task.Delay`: https://learn.microsoft.com/en-us/dotnet/api/system.threading.tasks.task.delay?view=net-9.0
- Cancellable socket accept: https://learn.microsoft.com/en-us/dotnet/api/system.net.sockets.socket.acceptasync?view=net-9.0
- Generic host shutdown timeout semantics: https://learn.microsoft.com/en-us/dotnet/core/extensions/generic-host

---

## Key Technical Decisions

- Keep idle-timeout state fully inside daemon session memory: satisfies R5 and keeps behavior aligned with existing ephemeral daemon architecture.
- Use explicit string sentinel `off` (not numeric `0`) for disabling auto-shutdown: matches origin decision and avoids ambiguous numeric semantics.
- Track inactivity from end of request handling: aligns with origin assumption and avoids counting in-flight work as idle.
- Update timeout only after successful validation and keep prior value on parse/validation failures: enforces R7 with no partial state mutation.
- Implement idle scheduling with cancellation-driven single-owner state transitions in `DaemonServer`: minimizes race risk between request completion and pending shutdown trigger.
- Use an explicit daemon protocol command to update timeout on active sessions (`setIdleTimeout`) before dispatching the feature command: avoids hidden side effects and keeps session mutation observable.

---

## Open Questions

### Resolved During Planning

- Should timeout be persisted across restarts? No; session-only (matches scope boundary).
- Should timer reset at request start or completion? Completion (origin dependency/assumption).

### Deferred to Implementation

- Exact internal helper/type names for timeout parsing and state transitions.

---

## Deferred to Follow-Up Work (Tracked)

- Add timeout mode/value visibility to `server status` response.
- Add richer daemon lifecycle telemetry and metrics.

---

## Implementation Units

- U1. **Add daemon idle-timeout state and lifecycle control**

**Goal:** Introduce effective timeout state (default/on/off), deterministic idle scheduling, and graceful self-shutdown in daemon runtime.

**Requirements:** R1, R2, R3, R6, R8

**Dependencies:** None

**Files:**
- Modify: `src/DotnetAi/Daemon/DaemonServer.cs`
- Modify: `src/DotnetAi/Models/Models.cs`
- Test: `tests/DotnetAi.Tests/Daemon/DaemonIdleTimeoutTests.cs`

**Approach:**
- Add daemon-session timeout state initialized to default 60 minutes.
- Add parser-friendly effective mode (`enabled + duration` vs disabled).
- Reset idle deadline after each completed request handling path (success and controlled failure).
- Run a cancellable idle watcher that triggers existing graceful shutdown path when idle deadline is reached.
- Ensure shutdown trigger is idempotent and safe when concurrent request activity appears near timeout boundary.
- Introduce explicit runtime lifecycle states (`Running`, `Draining`, `Stopped`) so idle-triggered shutdown stops accepting new work before cancellation and avoids double-shutdown races.

**Patterns to follow:**
- Existing daemon cancellation and accept-loop pattern in `src/DotnetAi/Daemon/DaemonServer.cs`.
- Existing graceful shutdown trigger pattern (`shutdown` command handling) in `src/DotnetAi/Daemon/DaemonServer.cs`.

**Test scenarios:**
- Happy path: Covers AE1. With default timeout and no requests, daemon auto-shuts down after 60-minute effective idle period.
- Happy path: Covers AE2. New handled request before deadline resets idle deadline so shutdown is postponed.
- Happy path: Covers AE4. With timeout mode `off`, daemon remains running past default timeout window.
- Edge case: Rapid consecutive requests do not cause duplicate/shaky shutdown scheduling.
- Edge case: Boundary behavior is deterministic (no shutdown at `timeout - epsilon`, shutdown at `timeout + epsilon`).
- Edge case: Request completion near deadline does not trigger premature shutdown while request is in flight.
- Error path: Internal handler failure still counts as handled request completion for idle reset semantics.
- Integration: Covers AE6. After idle-triggered shutdown, next command starts new daemon and receives valid response.

**Verification:**
- Daemon exits only after configured idle duration without handled requests and restarts cleanly on next client call.

---

- U2. **Expose session timeout parameter on command surface**

**Goal:** Allow user to set per-session daemon idle timeout (or `off`) from CLI invocations that connect/start daemon.

**Requirements:** R4, R5, R6

**Dependencies:** U1

**Files:**
- Modify: `src/DotnetAi/Program.cs`
- Modify: `src/DotnetAi/Commands/RefsCommand.cs`
- Modify: `src/DotnetAi/Commands/RenameCommand.cs`
- Modify: `src/DotnetAi/Commands/ImplsCommand.cs`
- Modify: `src/DotnetAi/Commands/CallersCommand.cs`
- Modify: `src/DotnetAi/Commands/SymbolsCommand.cs`
- Modify: `src/DotnetAi/Commands/ServerCommand.cs`
- Modify: `src/DotnetAi/Daemon/DaemonClient.cs`
- Test: `tests/DotnetAi.Tests/Commands/DaemonTimeoutOptionTests.cs`

**Approach:**
- Add shared CLI option for timeout input (duration value or `off`) and thread it to daemon connect/start path.
- Ensure all daemon-backed commands can pass timeout intent consistently.
- Keep behavior session-scoped: update running daemon timeout when connected via explicit `setIdleTimeout` command, or start daemon with requested timeout when creating new session.
- If multiple valid timeout updates occur in one session, the most recent successful update is authoritative.
- Keep command output contract unchanged (JSON stdout only).

**Patterns to follow:**
- Shared option plumbing pattern in `src/DotnetAi/Program.cs`.
- Existing command handler + `ConnectOrStartAsync` flow in command files.

**Test scenarios:**
- Happy path: Covers AE3. Passing valid timeout value applies to active daemon session and affects shutdown threshold.
- Happy path: Covers AE4. Passing `off` disables idle auto-shutdown for current daemon session.
- Edge case: Repeated calls with different valid timeouts update same running session without restart.
- Integration: First call with timeout starts daemon and applies timeout before first idle window begins.
- Integration: Timeout setting is session-scoped only; after daemon restart timeout returns to default unless re-set.
- Integration: Command surface parity check verifies each daemon-backed command forwards timeout consistently.

**Verification:**
- User-provided timeout is accepted across daemon-backed commands and effective for current daemon session behavior.

---

- U3. **Add strict timeout validation and non-mutating error handling**

**Goal:** Reject invalid timeout values clearly while preserving existing session timeout unchanged.

**Requirements:** R7

**Dependencies:** U1, U2

**Files:**
- Modify: `src/DotnetAi/Commands/ServerCommand.cs`
- Modify: `src/DotnetAi/Daemon/DaemonServer.cs`
- Modify: `src/DotnetAi/Daemon/DaemonClient.cs`
- Test: `tests/DotnetAi.Tests/Commands/DaemonTimeoutValidationTests.cs`

**Approach:**
- Centralize timeout parsing/validation semantics (documented duration format plus `off`).
- Return explicit validation error payload for malformed, negative, zero, or unsupported values.
- Apply timeout update only after parser success; keep prior daemon timeout state on errors.
- Preserve existing error envelope shape used by command stack.
- Treat sentinel handling as deterministic and documented (case-insensitive, surrounding whitespace trimmed).

**Patterns to follow:**
- Existing argument validation style in `src/DotnetAi/Commands/RefsCommand.cs`.
- Existing error JSON structure in `src/DotnetAi/Output/JsonOutput.cs`.

**Test scenarios:**
- Error path: Covers AE5. Invalid timeout returns validation error and session timeout remains previous value.
- Error path: Invalid value on first startup command does not start daemon with partial/undefined timeout state.
- Edge case: Parser table covers malformed, negative, zero, whitespace-only, and overflow values.
- Edge case: Sentinel handling (`off`, `OFF`, ` off `) follows documented normalization rule.
- Integration: After validation failure, next valid timeout update works and takes effect.

**Verification:**
- Invalid input never mutates active timeout state and always yields clear machine-readable error.

---

- U4. **Document daemon idle-timeout behavior and operational expectations**

**Goal:** Update user-facing docs to describe default timeout, override syntax, `off`, and restart behavior.

**Requirements:** R3, R4, R6, R8

**Dependencies:** U1, U2, U3

**Files:**
- Modify: `README.md`
- Modify: `CHANGELOG.md`
- Test expectation: none -- documentation-only unit.

**Approach:**
- Extend daemon management and daemon lifecycle sections with timeout semantics and examples.
- Document validation outcomes and clarify session-only scope.
- Add changelog entry summarizing new feature and behavior guarantees.

**Patterns to follow:**
- Existing command examples format in `README.md`.
- Existing changelog style in `CHANGELOG.md`.

**Verification:**
- README describes exact timeout behavior without ambiguity and changelog reflects feature scope.

---

## System-Wide Impact

- **Interaction graph:** CLI command handlers -> `DaemonClient.ConnectOrStartAsync` -> daemon request dispatch -> idle lifecycle controller.
- **Error propagation:** Timeout validation errors surface through existing daemon response/error envelope and command JSON output path.
- **State lifecycle risks:** Race between timeout expiry and in-flight request completion; mitigation is single-owner timeout state transition and idempotent shutdown trigger.
- **API surface parity:** All daemon-backed commands should accept/forward timeout parameter consistently to avoid mode drift.
- **Integration coverage:** End-to-end tests must prove auto-shutdown + next-command restart contract, not just isolated parser behavior.
- **Unchanged invariants:** One daemon per solution, JSON-on-stdout contract, explicit `server stop/status/reload` behavior, and no persistent timeout config storage remain unchanged.

---

## Risks & Dependencies

| Risk | Mitigation |
|------|------------|
| Timer race triggers shutdown during active workload | Use completion-based idle updates and cancellation-safe, idempotent shutdown signaling |
| Cross-command option inconsistency | Define shared timeout option wiring in root/command build path and add command-surface tests |
| Flaky timing tests | Use deterministic/fake-time test approach where possible; keep real-time windows buffered and minimal |
| Behavior drift from docs | Ship docs update in same plan (U4) and ensure examples reflect implemented validation semantics |
| Unauthorized local client mutates session timeout | Preserve user-scoped socket endpoint permissions and reject unsafe timeout updates through strict command validation |

---

## Documentation / Operational Notes

- Support should treat idle shutdown as expected behavior, not failure.
- Default timeout and `off` mode should be discoverable in daemon management docs.
- Future observability enhancement can add explicit lifecycle counters once this baseline feature is stable.

---

## Sources & References

- **Origin document:** `docs/brainstorms/daemon-idle-auto-shutdown-requirements.md`
- Related code: `src/DotnetAi/Daemon/DaemonServer.cs`
- Related code: `src/DotnetAi/Daemon/DaemonClient.cs`
- Related code: `src/DotnetAi/Commands/ServerCommand.cs`
- External docs: https://learn.microsoft.com/en-us/dotnet/standard/threading/cancellation-in-managed-threads
- External docs: https://learn.microsoft.com/en-us/dotnet/api/system.threading.cancellationtokensource?view=net-9.0
- External docs: https://learn.microsoft.com/en-us/dotnet/api/system.net.sockets.socket.acceptasync?view=net-9.0
