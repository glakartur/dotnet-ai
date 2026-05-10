namespace DotnetAICraft.Models;

public record ReferenceResult(
    string File,
    int Line,
    int Col,
    string Context);

public record CallerResult(
    string CallerSymbol,
    string CallerKind,
    bool IsDirect,
    string File,
    int Line,
    int Col,
    string Context);

public record CallGraphNode(
    string Id,
    string FullName,
    string Kind,
    string File,
    int Line,
    int Col,
    string? ContainingType,
    string? ContainingNamespace);

public record CallGraphEdge(
    string From,
    string To,
    string Relation,
    bool IsDirect);

public record CallGraphResult(
    string RootId,
    string Direction,
    int Depth,
    IReadOnlyList<CallGraphNode> Nodes,
    IReadOnlyList<CallGraphEdge> Edges);

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

public record SymbolsResultPage(
    IReadOnlyList<SymbolResult> Items,
    bool HasMore);

public record DefinitionResult(
    string FullName,
    string Kind,
    string? File,
    int? Line,
    int? Col,
    string? ContainingType,
    string? ContainingNamespace);

public record DaemonStatus(
    bool Running,
    string SolutionPath,
    int Projects,
    int Documents,
    DateTime LoadedAt,
    TimeSpan Uptime,
    string LoadState,
    DateTime? LastLoadAttemptAt,
    string? LastLoadErrorCode,
    string? LastLoadErrorMessage);

public record DiagnosticResult(
    string Project,
    string Id,
    string Severity,
    string Message,
    string? File,
    int? Line,
    int? Col,
    int? EndLine,
    int? EndCol);

public record UnusedCandidateResult(
    string Symbol,
    string Kind,
    string File,
    int Line,
    int Col,
    string Project,
    string Reason,
    double Confidence);

public record UnusedScanSummary(
    string Kind,
    string? Project,
    bool PublicOnly,
    bool IncludeGenerated,
    int Scanned,
    IReadOnlyList<UnusedCandidateResult> Items);

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
    object? Data,
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
