using System.Net.Sockets;
using System.Text;
using DotnetAi.Models;
using DotnetAi.Output;
using DotnetAi.Roslyn;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;

namespace DotnetAi.Daemon;

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

                await writer.WriteLineAsync(JsonOutput.Serialize(unavailable));
                return;
            }

            BeginRequest();
            requestStarted = true;
            command = request.Command;
            var response = await DispatchAsync(request, ct);
            await writer.WriteLineAsync(JsonOutput.Serialize(response));
        }
        catch (Exception ex)
        {
            Log($"Client error: {ex.Message}");
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
                "rename"   => await HandleRenameAsync(req, ct),
                "impls"    => await HandleImplsAsync(req, ct),
                "callers"  => await HandleCallersAsync(req, ct),
                "symbols"  => await HandleSymbolsAsync(req, ct),
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

        ISymbol symbol = p.TryGetValue("symbol", out var sym) && sym is not null
            ? await SymbolResolver.FromFullNameAsync(solution, sym.ToString()!, ct)
            : await SymbolResolver.FromLocationAsync(solution,
                p["file"].ToString()!,
                int.Parse(p["line"].ToString()!),
                int.Parse(p["col"].ToString()!), ct);

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

    private async Task<object> HandleRenameAsync(DaemonRequest req, CancellationToken ct)
    {
        var p        = GetParams(req);
        var solution = GetSolution();
        var newName  = p["to"].ToString()!;
        var dryRun   = p.TryGetValue("dryRun", out var dr) && dr?.ToString() == "True";

        ISymbol symbol = p.TryGetValue("symbol", out var sym) && sym is not null
            ? await SymbolResolver.FromFullNameAsync(solution, sym.ToString()!, ct)
            : await SymbolResolver.FromLocationAsync(solution,
                p["file"].ToString()!,
                int.Parse(p["line"].ToString()!),
                int.Parse(p["col"].ToString()!), ct);

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
                    NewText: change.NewText));
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
            solution, p["symbol"].ToString()!, ct);

        var impls = symbol is INamedTypeSymbol namedType
            ? await SymbolFinder.FindImplementationsAsync(namedType, solution, transitive: false, projects: null, ct)
            : await SymbolFinder.FindImplementationsAsync(symbol, solution, projects: null, ct);

        return impls.Select(s => SymbolToResult(s)).ToList();
    }

    private async Task<object> HandleCallersAsync(DaemonRequest req, CancellationToken ct)
    {
        var p        = GetParams(req);
        var solution = GetSolution();

        var symbol = p.TryGetValue("symbol", out var sym) && sym is not null
            ? await SymbolResolver.FromFullNameAsync(solution, sym.ToString()!, ct)
            : await SymbolResolver.FromLocationAsync(solution,
                p["file"].ToString()!,
                int.Parse(p["line"].ToString()!),
                int.Parse(p["col"].ToString()!), ct);

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

        var filter = kind.ToLower() switch
        {
            "type"   => SymbolFilter.Type,
            "member" => SymbolFilter.Member,
            "namespace" => SymbolFilter.Namespace,
            _ => SymbolFilter.All
        };

        var results = new List<Models.SymbolResult>();
        await foreach (var symbol in SymbolResolver.SearchAsync(GetSolution(), pattern, filter, ct))
        {
            results.Add(SymbolToResult(symbol));
            if (results.Count >= 200) break; // safety limit
        }

        return results;
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
