namespace DotnetAICraft.Models;

public record ReferenceResult(
    string File,
    int Line,
    int Col,
    string Context);

public record RenameChange(
    string File,
    int Line,
    int Col,
    string OldText,
    string NewText);

public record RenameResult(
    string Symbol,
    string NewName,
    bool Applied,
    bool DryRun,
    IReadOnlyList<RenameChange> Changes);

public record SymbolResult(
    string Name,
    string FullName,
    string Kind,
    string File,
    int Line,
    int Col,
    string? ContainingType,
    string? ContainingNamespace);

public record DaemonStatus(
    bool Running,
    string SolutionPath,
    int Projects,
    int Documents,
    DateTime LoadedAt,
    TimeSpan Uptime);

public record ErrorInfo(
    string Code,
    string Message,
    object? Details = null);

public record DaemonRequest(
    string Id,
    string Command,
    object? Params);

public record DaemonResponse(
    string Id,
    bool Ok,
    object? Result,
    ErrorInfo? Error,
    ResponseMeta? Meta);

public record ResponseMeta(
    long DurationMs,
    DateTime SolutionLoadedAt);

public record IdleTimeoutUpdateResult(
    bool Applied,
    string Mode,
    string? Value,
    bool Changed);
