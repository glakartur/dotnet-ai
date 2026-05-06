using System.Net.Sockets;
using System.Text;
using DotnetAICraft.Models;
using DotnetAICraft.Output;
using DotnetAICraft.Roslyn;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;

namespace DotnetAICraft.Daemon;

public sealed class DaemonServer : IAsyncDisposable
{
    private readonly string _solutionPath;
    private readonly CancellationTokenSource _cts = new();
    private MSBuildWorkspace? _workspace;
    private Solution? _solution;
    private DateTime _loadedAt;
    private FileSystemWatcher? _watcher;
    private readonly SemaphoreSlim _solutionLock = new(1, 1);
    private readonly object _idleLock = new();
    private DaemonLifecycleState _lifecycleState = DaemonLifecycleState.Running;
    private DaemonIdleTimeoutSetting _idleTimeout = DaemonIdleTimeoutSetting.Default;
    private DateTime _idleDeadlineUtc;
    private CancellationTokenSource? _idleWatcherCts;
    private int _activeRequests;

    private const string DiagnosticsSeverityAcceptedValues = "all | error | warning | info | hidden";

    public const int SymbolsDefaultLimit = 200;
    public const int SymbolsDefaultOffset = 0;
    public const int SymbolsMaxLimit = 2000;

    public DaemonServer(string solutionPath)
    {
        _solutionPath = Path.GetFullPath(solutionPath);
    }

    public DaemonServer(string solutionPath, DaemonIdleTimeoutSetting? idleTimeout)
        : this(solutionPath)
    {
        if (idleTimeout is not null)
            _idleTimeout = idleTimeout;
    }

    public async Task RunAsync()
    {
        Log($"Loading solution: {_solutionPath}");
        await LoadSolutionAsync();

        var socketPath = DaemonClient.GetSocketPath(_solutionPath);

        // Clean up stale socket from previous crash
        if (File.Exists(socketPath)) File.Delete(socketPath);

        var endpoint = new UnixDomainSocketEndPoint(socketPath);
        using var listener = new Socket(
            AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

        listener.Bind(endpoint);
        listener.Listen(backlog: 16);

        // Make socket accessible to current user only
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(socketPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);

        StartFileWatcher();

        Log($"Daemon ready. Socket: {socketPath}");
        Log($"Projects: {_solution!.Projects.Count()}, " +
            $"Documents: {_solution.Projects.Sum(p => p.Documents.Count())}");

        ResetIdleDeadline();

        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptAsync(_cts.Token);
                _ = HandleClientAsync(client, _cts.Token);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Log($"Accept error: {ex.Message}"); }
        }

        Log("Daemon shutting down.");
        if (File.Exists(socketPath)) File.Delete(socketPath);
    }

    // ── Request handling ──────────────────────────────────────────────────────

    private async Task HandleClientAsync(Socket client, CancellationToken ct)
    {
        await using var stream = new NetworkStream(client, ownsSocket: true);
        using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        await using var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true)
            { AutoFlush = true };
        string? command = null;
        var requestStarted = false;

        try
        {
            var line = await reader.ReadLineAsync(ct);
            if (line is null) return;

            var request = JsonOutput.Deserialize<DaemonRequest>(line)
                ?? throw new InvalidOperationException("Invalid request JSON.");

            if (!CanAcceptRequests() && request.Command != "shutdown")
            {
                var unavailable = new DaemonResponse(
                    request.Id,
                    false,
                    null,
                    new ErrorInfo("DAEMON_DRAINING", "Daemon is shutting down and not accepting new requests."),
                    null);

                await writer.WriteLineAsync(JsonOutput.Serialize(unavailable).AsMemory(), ct);
                return;
            }

            BeginRequest();
            requestStarted = true;
            command = request.Command;
            var response = await DispatchAsync(request, ct);
            await writer.WriteLineAsync(JsonOutput.Serialize(response).AsMemory(), ct);
        }
        catch (Exception ex)
        {
            Log($"Client error: {ex.Message}");
            try
            {
                var errResponse = new DaemonResponse(
                    "", false, null,
                    new ErrorInfo("INTERNAL_ERROR", ex.Message),
                    null);
                await writer.WriteLineAsync(JsonOutput.Serialize(errResponse).AsMemory(), CancellationToken.None);
            }
            catch { /* best-effort; connection may already be broken */ }
        }
        finally
        {
            if (requestStarted)
                EndRequest(command ?? string.Empty);
        }
    }

    private async Task<DaemonResponse> DispatchAsync(DaemonRequest req, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            object? result = req.Command switch
            {
                "refs"     => await HandleRefsAsync(req, ct),
                "definition" => await HandleDefinitionAsync(req, ct),
                "rename"   => await HandleRenameAsync(req, ct),
                "impls"    => await HandleImplsAsync(req, ct),
                "callers"  => await HandleCallersAsync(req, ct),
                "symbols"  => await HandleSymbolsAsync(req, ct),
                "diagnostics" => await HandleDiagnosticsAsync(req, ct),
                "status"   => HandleStatus(),
                "reload"   => await HandleReloadAsync(ct),
                "setIdleTimeout" => HandleSetIdleTimeout(req),
                "shutdown" => HandleShutdown(),
                _ => throw new InvalidOperationException($"Unknown command: {req.Command}")
            };

            return new DaemonResponse(req.Id, true, result, null,
                new ResponseMeta(sw.ElapsedMilliseconds, _loadedAt));
        }
        catch (DaemonValidationException ex)
        {
            return new DaemonResponse(req.Id, false, null,
                ex.Error,
                new ResponseMeta(sw.ElapsedMilliseconds, _loadedAt));
        }
        catch (Exception ex)
        {
            return new DaemonResponse(req.Id, false, null,
                new ErrorInfo(ex.GetType().Name.ToUpperSnakeCase(), ex.Message),
                new ResponseMeta(sw.ElapsedMilliseconds, _loadedAt));
        }
    }

    // ── Command handlers ──────────────────────────────────────────────────────

    private async Task<object> HandleRefsAsync(DaemonRequest req, CancellationToken ct)
    {
        var p = GetParams(req);
        var solution = GetSolution();

        var symbolName = GetOptionalString(p, "symbol");
        ISymbol symbol = symbolName is not null
            ? await SymbolResolver.FromFullNameAsync(solution, symbolName, ct)
            : await SymbolResolver.FromLocationAsync(solution,
                GetRequiredString(p, "file"),
                GetRequiredInt(p, "line"),
                GetRequiredInt(p, "col"), ct);

        var refs = await SymbolFinder.FindReferencesAsync(symbol, solution, ct);

        return refs
            .SelectMany(r => r.Locations)
            .Select(loc =>
            {
                var (file, line, col) = loc.Location.GetFileLineCol();
                return new Models.ReferenceResult(file, line, col,
                    loc.Location.GetContextLine());
            })
            .ToList();
    }

    private async Task<object> HandleDefinitionAsync(DaemonRequest req, CancellationToken ct)
    {
        var p = GetParams(req);
        var solution = GetSolution();

        var symbol = GetOptionalString(p, "symbol");
        var file = GetOptionalString(p, "file");
        var line = GetOptionalInt(p, "line");
        var col = GetOptionalInt(p, "col");

        return await ResolveDefinitionAsync(solution, symbol, file, line, col, ct);
    }

    private async Task<object> HandleRenameAsync(DaemonRequest req, CancellationToken ct)
    {
        var p        = GetParams(req);
        var solution = GetSolution();
        var newName  = GetRequiredString(p, "to");
        var dryRun   = p.TryGetValue("dryRun", out var dr) && dr?.ToString() == "True";

        var symbolName = GetOptionalString(p, "symbol");
        ISymbol symbol = symbolName is not null
            ? await SymbolResolver.FromFullNameAsync(solution, symbolName, ct)
            : await SymbolResolver.FromLocationAsync(solution,
                GetRequiredString(p, "file"),
                GetRequiredInt(p, "line"),
                GetRequiredInt(p, "col"), ct);

        var oldName = symbol.Name;
        var newSolution = await Renamer.RenameSymbolAsync(
            solution, symbol, new SymbolRenameOptions(), newName, ct);

        var solutionChanges = newSolution.GetChanges(solution);
        var changes = new List<Models.RenameChange>();

        foreach (var projectChanges in solutionChanges.GetProjectChanges())
        foreach (var docChange in projectChanges.GetChangedDocuments())
        {
            var oldDoc = solution.GetDocument(docChange)!;
            var newDoc = newSolution.GetDocument(docChange)!;
            var oldText = await oldDoc.GetTextAsync(ct);
            var newText = await newDoc.GetTextAsync(ct);

            foreach (var change in newText.GetTextChanges(oldText))
            {
                var linePos = oldText.Lines.GetLinePosition(change.Span.Start);
                var oldTextSegment = oldText.GetSubText(change.Span).ToString();

                changes.Add(new Models.RenameChange(
                    File:    oldDoc.FilePath ?? "",
                    Line:    linePos.Line + 1,
                    Col:     linePos.Character + 1,
                    OldText: oldTextSegment,
                    NewText: change.NewText ?? string.Empty));
            }
        }

        // Apply changes if not dry-run
        if (!dryRun)
        {
            await _solutionLock.WaitAsync(ct);
            try
            {
                _workspace!.TryApplyChanges(newSolution);
                _solution = _workspace.CurrentSolution;
            }
            finally { _solutionLock.Release(); }
        }

        return new Models.RenameResult(
            Symbol:  symbol.ToDisplayString(),
            NewName: newName,
            Applied: !dryRun,
            DryRun:  dryRun,
            Changes: changes);
    }

    private async Task<object> HandleImplsAsync(DaemonRequest req, CancellationToken ct)
    {
        var p        = GetParams(req);
        var solution = GetSolution();

        var symbol = await SymbolResolver.FromFullNameAsync(
            solution, GetRequiredString(p, "symbol"), ct);

        var impls = symbol is INamedTypeSymbol namedType
            ? await SymbolFinder.FindImplementationsAsync(namedType, solution, transitive: false, projects: null, ct)
            : await SymbolFinder.FindImplementationsAsync(symbol, solution, projects: null, ct);

        return impls.Select(s => SymbolToResult(s)).ToList();
    }

    private async Task<object> HandleCallersAsync(DaemonRequest req, CancellationToken ct)
    {
        var p        = GetParams(req);
        var solution = GetSolution();

        var symbolName = GetOptionalString(p, "symbol");
        var symbol = symbolName is not null
            ? await SymbolResolver.FromFullNameAsync(solution, symbolName, ct)
            : await SymbolResolver.FromLocationAsync(solution,
                GetRequiredString(p, "file"),
                GetRequiredInt(p, "line"),
                GetRequiredInt(p, "col"), ct);

        var callers = await SymbolFinder.FindCallersAsync(symbol, solution, ct);

        return callers.Select(c =>
        {
            var loc = c.Locations.FirstOrDefault();
            var (file, line, col) = loc is not null
                ? loc.GetFileLineCol()
                : ("", 0, 0);

            return new
            {
                callerSymbol = c.CallingSymbol.ToDisplayString(),
                callerKind   = c.CallingSymbol.GetKindName(),
                isDirect     = c.IsDirect,
                file,
                line,
                col,
                context      = loc?.GetContextLine() ?? ""
            };
        }).ToList();
    }

    private async Task<object> HandleSymbolsAsync(DaemonRequest req, CancellationToken ct)
    {
        var p       = GetParams(req);
        var pattern = p.TryGetValue("pattern", out var pat) ? pat?.ToString() ?? "*" : "*";
        var kind    = p.TryGetValue("kind", out var k) ? k?.ToString() ?? "all" : "all";
        var limit   = GetOptionalInt(p, "limit");
        var offset  = GetOptionalInt(p, "offset");

        var filter = kind.ToLower() switch
        {
            "type"   => SymbolFilter.Type,
            "member" => SymbolFilter.Member,
            "namespace" => SymbolFilter.Namespace,
            _ => SymbolFilter.All
        };

        return await CollectSymbolsAsync(GetSolution(), pattern, filter, limit, offset, ct);
    }

    private async Task<object> HandleDiagnosticsAsync(DaemonRequest req, CancellationToken ct)
    {
        var p = GetParams(req);

        var severityRaw = GetOptionalString(p, "severity") ?? "all";
        var projectFilter = GetOptionalString(p, "project");
        var fileFilter = GetOptionalString(p, "file");

        if (!TryParseDiagnosticsSeverity(severityRaw, out var severityFilter, out _))
        {
            throw new DaemonValidationException(new ErrorInfo(
                "INVALID_PARAMS",
                "Invalid 'severity' parameter.",
                new { acceptedValues = DiagnosticsSeverityAcceptedValues }));
        }

        return await CollectDiagnosticsAsync(
            GetSolution(),
            severityFilter,
            projectFilter,
            fileFilter,
            ct);
    }

    private object HandleStatus()
    {
        var s = GetSolution();
        return new Models.DaemonStatus(
            Running:      true,
            SolutionPath: _solutionPath,
            Projects:     s.Projects.Count(),
            Documents:    s.Projects.Sum(p => p.Documents.Count()),
            LoadedAt:     _loadedAt,
            Uptime:       DateTime.UtcNow - _loadedAt);
    }

    private async Task<object> HandleReloadAsync(CancellationToken ct)
    {
        await _solutionLock.WaitAsync(ct);
        try
        {
            await LoadSolutionAsync();
            return new { reloaded = true, loadedAt = _loadedAt };
        }
        finally { _solutionLock.Release(); }
    }

    private object HandleShutdown()
    {
        BeginShutdown();
        return new { shutdownInitiated = true };
    }

    private object HandleSetIdleTimeout(DaemonRequest req)
    {
        var p = GetParams(req);
        if (!p.TryGetValue("value", out var rawObj) || rawObj is null)
            throw new DaemonValidationException(new ErrorInfo(
                "INVALID_IDLE_TIMEOUT",
                "Missing idle timeout value.",
                new { acceptedValues = "off | <positive duration with unit: m|h>" }));

        var raw = rawObj.ToString() ?? string.Empty;
        if (!DaemonIdleTimeoutParser.TryParse(raw, out var parsed, out var error))
            throw new DaemonValidationException(error!);

        return ApplyIdleTimeout(parsed);
    }

    private Models.IdleTimeoutUpdateResult ApplyIdleTimeout(DaemonIdleTimeoutSetting setting)
    {
        lock (_idleLock)
        {
            DateTime nextDeadlineUtc;
            if (setting.Enabled)
            {
                try
                {
                    nextDeadlineUtc = DateTime.UtcNow + setting.Duration;
                }
                catch (ArgumentOutOfRangeException)
                {
                    throw new DaemonValidationException(new ErrorInfo(
                        "INVALID_IDLE_TIMEOUT",
                        "Idle timeout is too large.",
                        new { acceptedValues = "off | <positive duration with unit: m|h>" }));
                }
            }
            else
            {
                nextDeadlineUtc = DateTime.MaxValue;
            }

            var changed = !_idleTimeout.Equals(setting);
            _idleTimeout = setting;
            _idleDeadlineUtc = nextDeadlineUtc;

            CancelIdleWatcherUnsafe();
            if (setting.Enabled)
            {
                StartIdleWatcherUnsafe();
            }

            return new Models.IdleTimeoutUpdateResult(
                Applied: true,
                Mode: setting.Enabled ? "duration" : "off",
                Value: setting.Enabled ? setting.Normalized : null,
                Changed: changed);
        }
    }

    private void ResetIdleDeadline()
    {
        lock (_idleLock)
        {
            if (!_idleTimeout.Enabled)
            {
                _idleDeadlineUtc = DateTime.MaxValue;
                return;
            }

            try
            {
                _idleDeadlineUtc = DateTime.UtcNow + _idleTimeout.Duration;
            }
            catch (ArgumentOutOfRangeException)
            {
                throw new DaemonValidationException(new ErrorInfo(
                    "INVALID_IDLE_TIMEOUT",
                    "Idle timeout is too large.",
                    new { acceptedValues = "off | <positive duration with unit: m|h>" }));
            }

            CancelIdleWatcherUnsafe();
            StartIdleWatcherUnsafe();
        }
    }

    private void StartIdleWatcherUnsafe()
    {
        if (!_idleTimeout.Enabled || _cts.IsCancellationRequested)
            return;

        _idleWatcherCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _ = WatchIdleAsync(_idleWatcherCts.Token);
    }

    private void CancelIdleWatcherUnsafe()
    {
        if (_idleWatcherCts is null)
            return;

        _idleWatcherCts.Cancel();
        _idleWatcherCts.Dispose();
        _idleWatcherCts = null;
    }

    private async Task WatchIdleAsync(CancellationToken ct)
    {
        TimeSpan delay;
        lock (_idleLock)
        {
            if (!_idleTimeout.Enabled)
                return;

            delay = _idleDeadlineUtc - DateTime.UtcNow;
            if (delay < TimeSpan.Zero)
                delay = TimeSpan.Zero;
        }

        try
        {
            await Task.Delay(delay, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        lock (_idleLock)
        {
            if (!_idleTimeout.Enabled)
                return;

            if (_activeRequests > 0)
                return;

            if (DateTime.UtcNow < _idleDeadlineUtc)
                return;
        }

        BeginShutdown();
    }

    private bool CanAcceptRequests()
    {
        lock (_idleLock)
        {
            return _lifecycleState == DaemonLifecycleState.Running;
        }
    }

    private void BeginRequest()
    {
        lock (_idleLock)
        {
            _activeRequests++;
            CancelIdleWatcherUnsafe();
        }
    }

    private void EndRequest(string command)
    {
        lock (_idleLock)
        {
            if (_activeRequests > 0)
                _activeRequests--;
        }

        if (command != "shutdown")
            ResetIdleDeadline();
    }

    private void BeginShutdown()
    {
        lock (_idleLock)
        {
            if (_lifecycleState != DaemonLifecycleState.Running)
                return;

            _lifecycleState = DaemonLifecycleState.Draining;
            CancelIdleWatcherUnsafe();
        }

        _cts.CancelAfter(TimeSpan.FromMilliseconds(500));
    }

    // ── Solution management ───────────────────────────────────────────────────

    private async Task LoadSolutionAsync()
    {
        _workspace?.Dispose();
        var (workspace, solution) = await WorkspaceLoader.LoadAsync(_solutionPath);
        _workspace  = workspace;
        _solution   = solution;
        _loadedAt   = DateTime.UtcNow;

        Log($"Solution loaded: {solution.Projects.Count()} projects, " +
            $"{solution.Projects.Sum(p => p.Documents.Count())} documents");
    }

    private Solution GetSolution() => _solution
        ?? throw new InvalidOperationException("Solution not loaded.");

    // ── File watching ─────────────────────────────────────────────────────────

    private void StartFileWatcher()
    {
        var dir = Path.GetDirectoryName(_solutionPath)!;
        _watcher = new FileSystemWatcher(dir, "*.cs")
        {
            IncludeSubdirectories = true,
            EnableRaisingEvents   = true,
            NotifyFilter          = NotifyFilters.LastWrite | NotifyFilters.FileName
        };

        var debounce = new Dictionary<string, CancellationTokenSource>();
        var debounceLock = new object();

        async void OnChange(object _, FileSystemEventArgs e)
        {
            CancellationTokenSource cts;
            lock (debounceLock)
            {
                if (debounce.TryGetValue(e.FullPath, out var old)) old.Cancel();
                cts = debounce[e.FullPath] = new CancellationTokenSource();
            }
            try
            {
                await Task.Delay(400, cts.Token);
                await ApplyFileChangeAsync(e.FullPath);
            }
            catch (TaskCanceledException) { }
        }

        _watcher.Changed += OnChange;
        _watcher.Created += OnChange;
    }

    private async Task ApplyFileChangeAsync(string filePath)
    {
        await _solutionLock.WaitAsync();
        try
        {
            var solution = GetSolution();
            var docIds   = solution.GetDocumentIdsWithFilePath(filePath);
            if (docIds.IsEmpty) return;

            var newText = SourceText.From(await File.ReadAllTextAsync(filePath), Encoding.UTF8);
            var updated = solution.WithDocumentText(docIds.First(), newText);

            if (_workspace!.TryApplyChanges(updated))
            {
                _solution = _workspace.CurrentSolution;
                Log($"Updated: {Path.GetFileName(filePath)}");
            }
        }
        catch (Exception ex) { Log($"File update error: {ex.Message}"); }
        finally { _solutionLock.Release(); }
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    public static async Task<Models.DefinitionResult> ResolveDefinitionAsync(
        Solution solution,
        string? symbol,
        string? file,
        int? line,
        int? col,
        CancellationToken ct = default)
    {
        var hasSymbol = !string.IsNullOrWhiteSpace(symbol);
        var hasAnyLocation = !string.IsNullOrWhiteSpace(file) || line is not null || col is not null;
        var hasCompleteLocation = !string.IsNullOrWhiteSpace(file) && line is not null && col is not null;

        if (hasSymbol == hasAnyLocation)
        {
            throw new DaemonValidationException(new ErrorInfo(
                "INVALID_PARAMS",
                "Provide exactly one input mode: either 'symbol' OR 'file'+'line'+'col'."));
        }

        if (hasAnyLocation && !hasCompleteLocation)
        {
            throw new DaemonValidationException(new ErrorInfo(
                "INVALID_PARAMS",
                "Location mode requires 'file', 'line', and 'col' parameters."));
        }

        ISymbol resolved = hasSymbol
            ? await SymbolResolver.FromFullNameAsync(solution, symbol!.Trim(), ct)
            : await SymbolResolver.FromLocationAsync(solution, file!, line!.Value, col!.Value, ct);

        return SymbolToDefinitionResult(resolved);
    }

    public static bool TryParseDiagnosticsSeverity(
        string? raw,
        out DiagnosticSeverity? severity,
        out string normalized)
    {
        normalized = string.IsNullOrWhiteSpace(raw)
            ? "all"
            : raw.Trim().ToLowerInvariant();

        switch (normalized)
        {
            case "all":
                severity = null;
                return true;
            case "error":
                severity = DiagnosticSeverity.Error;
                return true;
            case "warning":
                severity = DiagnosticSeverity.Warning;
                return true;
            case "info":
                severity = DiagnosticSeverity.Info;
                return true;
            case "hidden":
                severity = DiagnosticSeverity.Hidden;
                return true;
            default:
                severity = null;
                return false;
        }
    }

    public static bool TryNormalizeSymbolsPagination(
        int? limit,
        int? offset,
        out int normalizedLimit,
        out int normalizedOffset,
        out ErrorInfo? error)
    {
        normalizedLimit = limit ?? SymbolsDefaultLimit;
        normalizedOffset = offset ?? SymbolsDefaultOffset;

        if (normalizedLimit <= 0)
        {
            error = new ErrorInfo(
                "INVALID_PARAMS",
                "Parameter 'limit' must be greater than 0.",
                new { min = 1, max = SymbolsMaxLimit, @default = SymbolsDefaultLimit });
            return false;
        }

        if (normalizedOffset < 0)
        {
            error = new ErrorInfo(
                "INVALID_PARAMS",
                "Parameter 'offset' must be greater than or equal to 0.",
                new { min = 0, @default = SymbolsDefaultOffset });
            return false;
        }

        if (normalizedLimit > SymbolsMaxLimit)
            normalizedLimit = SymbolsMaxLimit;

        error = null;
        return true;
    }

    public static async Task<Models.SymbolsResultPage> CollectSymbolsAsync(
        Solution solution,
        string pattern,
        SymbolFilter filter = SymbolFilter.All,
        int? limit = null,
        int? offset = null,
        CancellationToken ct = default)
    {
        if (!TryNormalizeSymbolsPagination(limit, offset, out var normalizedLimit, out var normalizedOffset, out var error))
            throw new DaemonValidationException(error!);

        var results = new List<Models.SymbolResult>(normalizedLimit);
        var skipped = 0;
        var hasMore = false;

        await foreach (var symbol in SymbolResolver.SearchAsync(solution, pattern, filter, ct))
        {
            if (skipped < normalizedOffset)
            {
                skipped++;
                continue;
            }

            if (results.Count < normalizedLimit)
            {
                results.Add(SymbolToResult(symbol));
                continue;
            }

            hasMore = true;
            break;
        }

        return new Models.SymbolsResultPage(results, hasMore);
    }

    public static async Task<IReadOnlyList<Models.DiagnosticResult>> CollectDiagnosticsAsync(
        Solution solution,
        DiagnosticSeverity? severityFilter = null,
        string? projectFilter = null,
        string? fileFilter = null,
        CancellationToken ct = default)
    {
        var normalizedProject = string.IsNullOrWhiteSpace(projectFilter)
            ? null
            : projectFilter.Trim();

        var normalizedFile = string.IsNullOrWhiteSpace(fileFilter)
            ? null
            : fileFilter.Trim();

        var results = new List<Models.DiagnosticResult>();

        foreach (var project in solution.Projects)
        {
            ct.ThrowIfCancellationRequested();

            if (normalizedProject is not null &&
                !string.Equals(project.Name, normalizedProject, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var compilation = await project.GetCompilationAsync(ct);
            if (compilation is null)
                continue;

            foreach (var diagnostic in compilation.GetDiagnostics(ct))
            {
                if (severityFilter is not null && diagnostic.Severity != severityFilter.Value)
                    continue;

                var mapped = MapDiagnostic(project.Name, diagnostic);
                if (!MatchesFileFilter(mapped.File, normalizedFile))
                    continue;

                results.Add(mapped);
            }
        }

        return results
            .OrderBy(r => r.Project, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.File ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Line ?? int.MaxValue)
            .ThenBy(r => r.Col ?? int.MaxValue)
            .ThenBy(r => r.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static Models.DiagnosticResult MapDiagnostic(string projectName, Diagnostic diagnostic)
    {
        string? file = null;
        int? line = null;
        int? col = null;
        int? endLine = null;
        int? endCol = null;

        if (diagnostic.Location is { IsInSource: true } location)
        {
            var lineSpan = location.GetLineSpan();
            file = lineSpan.Path;
            line = lineSpan.StartLinePosition.Line + 1;
            col = lineSpan.StartLinePosition.Character + 1;
            endLine = lineSpan.EndLinePosition.Line + 1;
            endCol = lineSpan.EndLinePosition.Character + 1;
        }

        return new Models.DiagnosticResult(
            Project: projectName,
            Id: diagnostic.Id,
            Severity: diagnostic.Severity.ToString().ToLowerInvariant(),
            Message: diagnostic.GetMessage(),
            File: file,
            Line: line,
            Col: col,
            EndLine: endLine,
            EndCol: endCol);
    }

    private static bool MatchesFileFilter(string? diagnosticPath, string? fileFilter)
    {
        if (fileFilter is null)
            return true;

        if (string.IsNullOrWhiteSpace(diagnosticPath))
            return false;

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        var normalizedDiagnosticPath = diagnosticPath.Replace('\\', '/');
        var normalizedFileFilter = fileFilter.Replace('\\', '/');

        if (Path.IsPathRooted(fileFilter))
        {
            try
            {
                var fullDiagnosticPath = Path.GetFullPath(normalizedDiagnosticPath);
                var fullFilterPath = Path.GetFullPath(normalizedFileFilter);
                return string.Equals(fullDiagnosticPath, fullFilterPath, comparison);
            }
            catch
            {
                return false;
            }
        }

        return normalizedDiagnosticPath.EndsWith(normalizedFileFilter, comparison);
    }

    private static Models.DefinitionResult SymbolToDefinitionResult(ISymbol symbol)
    {
        var sourceLocation = symbol.Locations.FirstOrDefault(l => l.IsInSource);

        string? file = null;
        int? line = null;
        int? col = null;

        if (sourceLocation is not null)
        {
            var sourcePosition = sourceLocation.GetFileLineCol();
            file = sourcePosition.File;
            line = sourcePosition.Line;
            col = sourcePosition.Col;
        }

        return new Models.DefinitionResult(
            FullName:            symbol.ToDisplayString(),
            Kind:                symbol.GetKindName(),
            File:                file,
            Line:                line,
            Col:                 col,
            ContainingType:      symbol.ContainingType?.ToDisplayString(),
            ContainingNamespace: symbol.ContainingNamespace?.ToDisplayString());
    }

    private static Models.SymbolResult SymbolToResult(ISymbol symbol)
    {
        var loc  = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        var (file, line, col) = loc is not null ? loc.GetFileLineCol() : ("", 0, 0);

        return new Models.SymbolResult(
            Name:                symbol.Name,
            FullName:            symbol.ToDisplayString(),
            Kind:                symbol.GetKindName(),
            File:                file,
            Line:                line,
            Col:                 col,
            ContainingType:      symbol.ContainingType?.ToDisplayString(),
            ContainingNamespace: symbol.ContainingNamespace?.ToDisplayString());
    }

    private static Dictionary<string, object?> GetParams(DaemonRequest req)
    {
        if (req.Params is null)
            return new Dictionary<string, object?>();

        // Params arrive as JsonElement — convert to plain dict
        var json = JsonOutput.Serialize(req.Params);
        return JsonOutput.Deserialize<Dictionary<string, object?>>(json)
               ?? new Dictionary<string, object?>();
    }

    private static string? GetOptionalString(Dictionary<string, object?> parameters, string key)
    {
        return parameters.TryGetValue(key, out var value)
            ? value?.ToString()
            : null;
    }

    private static int? GetOptionalInt(Dictionary<string, object?> parameters, string key)
    {
        var raw = GetOptionalString(parameters, key);
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        if (int.TryParse(raw, out var parsed))
            return parsed;

        throw new DaemonValidationException(new ErrorInfo(
            "INVALID_PARAMS",
            $"Parameter '{key}' must be an integer."));
    }

    private static string GetRequiredString(Dictionary<string, object?> parameters, string key)
    {
        var value = GetOptionalString(parameters, key);
        if (!string.IsNullOrWhiteSpace(value))
            return value;

        throw new DaemonValidationException(new ErrorInfo(
            "INVALID_PARAMS",
            $"Missing or invalid '{key}' parameter."));
    }

    private static int GetRequiredInt(Dictionary<string, object?> parameters, string key)
    {
        var raw = GetRequiredString(parameters, key);
        if (int.TryParse(raw, out var parsed))
            return parsed;

        throw new DaemonValidationException(new ErrorInfo(
            "INVALID_PARAMS",
            $"Parameter '{key}' must be an integer."));
    }

    private void Log(string message)
        => Console.Error.WriteLine($"[daemon {DateTime.Now:HH:mm:ss}] {message}");

    public async ValueTask DisposeAsync()
    {
        lock (_idleLock)
        {
            _lifecycleState = DaemonLifecycleState.Stopped;
            CancelIdleWatcherUnsafe();
        }

        _watcher?.Dispose();
        _cts.Dispose();
        _workspace?.Dispose();
        _solutionLock.Dispose();
    }
}

public enum DaemonLifecycleState
{
    Running,
    Draining,
    Stopped
}

public sealed class DaemonValidationException : Exception
{
    public ErrorInfo Error { get; }

    public DaemonValidationException(ErrorInfo error)
        : base(error.Message)
    {
        Error = error;
    }
}
