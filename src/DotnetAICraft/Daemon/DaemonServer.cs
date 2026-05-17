using System.Net.Sockets;
using System.Text;
using DotnetAICraft.Diagnostics;
using DotnetAICraft.Models;
using DotnetAICraft.Output;
using DotnetAICraft.Roslyn;
using DotnetAICraft.Commands.Definition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Rename;
using Microsoft.CodeAnalysis.Text;

namespace DotnetAICraft.Daemon;

public sealed class DaemonServer : IAsyncDisposable
{
    private const int UnloadedIdleTimeoutMinutes = 5;

    private readonly string _solutionPath;
    private readonly DaemonStartupLock? _startupLock;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _metadataReloadLock = new();
    private MSBuildWorkspace? _workspace;
    private Solution? _solution;
    private DateTime _loadedAt;
    private readonly DateTime _startedAt = DateTime.UtcNow;
    private DateTime? _lastLoadAttemptAt;
    private ErrorInfo? _lastLoadError;
    private DaemonLoadState _loadState = DaemonLoadState.Unloaded;
    private FileSystemWatcher? _watcher;
    private FileSystemWatcher? _metadataWatcher;
    private CancellationTokenSource? _metadataReloadDebounceCts;
    private readonly SemaphoreSlim _solutionLock = new(1, 1);
    private readonly SemaphoreSlim _metadataReloadSingleFlight = new(1, 1);
    private readonly object _idleLock = new();
    private DaemonLifecycleState _lifecycleState = DaemonLifecycleState.Running;
    private DaemonIdleTimeoutSetting _idleTimeout = DaemonIdleTimeoutSetting.Default;
    private bool _hasExplicitIdleTimeoutOverride;
    private DateTime _idleDeadlineUtc;
    private CancellationTokenSource? _idleWatcherCts;
    private int _activeRequests;

    private const string DiagnosticsSeverityAcceptedValues = "all | error | warning | info | hidden";
    public const string SymbolsKindAcceptedValues = "all | type | member | namespace | class | interface | struct | enum | delegate | method | constructor | property | field | event";
    public const string UnusedKindAcceptedValues = SymbolsKindAcceptedValues;
    public const string CallGraphDirectionAcceptedValues = "incoming | outgoing | both";
    public const string CallGraphDefaultDirection = "incoming";
    public const int CallGraphDefaultDepth = 1;

    private static readonly SymbolDisplayFormat CallGraphSymbolDisplayFormat = SymbolDisplayFormat.CSharpErrorMessageFormat;

    private static readonly Func<ISymbol, bool> AnySymbolPredicate = static _ => true;
    private static readonly Func<ISymbol, IEnumerable<ISymbol>> IdentitySymbolExpander = static symbol => [symbol];

    private readonly record struct SymbolsKindDefinition(
        SymbolFilter Filter,
        Func<ISymbol, IEnumerable<ISymbol>> Expand,
        Func<ISymbol, bool> Predicate);

    private enum CallGraphDirection
    {
        Incoming,
        Outgoing,
        Both
    }

    private static readonly IReadOnlyDictionary<string, SymbolsKindDefinition> SymbolsKindDefinitions =
        new Dictionary<string, SymbolsKindDefinition>(StringComparer.Ordinal)
        {
            ["all"] = new(SymbolFilter.All, IdentitySymbolExpander, AnySymbolPredicate),
            ["type"] = new(SymbolFilter.Type, IdentitySymbolExpander, AnySymbolPredicate),
            ["member"] = new(SymbolFilter.Member, IdentitySymbolExpander, AnySymbolPredicate),
            ["namespace"] = new(SymbolFilter.Namespace, IdentitySymbolExpander, AnySymbolPredicate),
            ["class"] = new(SymbolFilter.Type, IdentitySymbolExpander, static symbol => symbol is INamedTypeSymbol { TypeKind: TypeKind.Class }),
            ["interface"] = new(SymbolFilter.Type, IdentitySymbolExpander, static symbol => symbol is INamedTypeSymbol { TypeKind: TypeKind.Interface }),
            ["struct"] = new(SymbolFilter.Type, IdentitySymbolExpander, static symbol => symbol is INamedTypeSymbol { TypeKind: TypeKind.Struct }),
            ["enum"] = new(SymbolFilter.Type, IdentitySymbolExpander, static symbol => symbol is INamedTypeSymbol { TypeKind: TypeKind.Enum }),
            ["delegate"] = new(SymbolFilter.Type, IdentitySymbolExpander, static symbol => symbol is INamedTypeSymbol { TypeKind: TypeKind.Delegate }),
            ["method"] = new(SymbolFilter.Member, IdentitySymbolExpander, static symbol =>
                symbol is IMethodSymbol method &&
                method.MethodKind is not MethodKind.Constructor and not MethodKind.StaticConstructor),
            ["constructor"] = new(SymbolFilter.Type, ExpandConstructors, static symbol =>
                symbol is IMethodSymbol method &&
                method.MethodKind is MethodKind.Constructor or MethodKind.StaticConstructor),
            ["property"] = new(SymbolFilter.Member, IdentitySymbolExpander, static symbol => symbol is IPropertySymbol),
            ["field"] = new(SymbolFilter.Member, IdentitySymbolExpander, static symbol => symbol is IFieldSymbol),
            ["event"] = new(SymbolFilter.Member, IdentitySymbolExpander, static symbol => symbol is IEventSymbol)
        };

    public const int SymbolsDefaultLimit = 200;
    public const int SymbolsDefaultOffset = 0;
    public const int SymbolsMaxLimit = 2000;

    public DaemonServer(string solutionPath)
    {
        _solutionPath = Path.GetFullPath(solutionPath);
        _loadedAt = _startedAt;
    }

    public DaemonServer(string solutionPath, DaemonIdleTimeoutSetting? idleTimeout)
        : this(solutionPath)
    {
        if (idleTimeout is not null)
        {
            _idleTimeout = idleTimeout;
            _hasExplicitIdleTimeoutOverride = true;
        }
    }

    public DaemonServer(string solutionPath, DaemonIdleTimeoutSetting? idleTimeout, DaemonStartupLock? startupLock)
        : this(solutionPath, idleTimeout)
    {
        _startupLock = startupLock;
    }

    public async Task RunAsync()
    {
        var socketPath = DaemonClient.GetSocketPath(_solutionPath);

        var endpoint = new UnixDomainSocketEndPoint(socketPath);
        using var listener = new Socket(
            AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

        listener.Bind(endpoint);
        listener.Listen(backlog: 16);

        if (_startupLock is not null)
        {
            await _startupLock.DisposeAsync();
        }

        // Make socket accessible to current user only
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(socketPath,
                UnixFileMode.UserRead | UnixFileMode.UserWrite);

        StartFileWatcher();
        StartMetadataWatcher();

        Log($"Attempting initial solution load: {_solutionPath}");
        await TryLoadSolutionAsync();

        Log($"Daemon ready. Socket: {socketPath}");
        Log($"Load state: {_loadState.ToString().ToLowerInvariant()}");

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

            ValidateRequestMetadata(request);
            ApplyRequestIdleTimeoutIfPresent(request);

            DebugLog.Write("server", $"HandleClientAsync request received command={request.Command} id={request.Id}");

            if (!CanAcceptRequests() && request.Command != "shutdown")
            {
                var unavailable = CreateErrorResponse(
                    request.Id,
                    new ErrorInfo("DAEMON_DRAINING", "Daemon is shutting down and not accepting new requests."),
                    null);

                await writer.WriteLineAsync(JsonOutput.Serialize(unavailable).AsMemory(), ct);
                return;
            }

            BeginRequest();
            requestStarted = true;
            command = request.Command;
            var response = await DispatchAsync(request, ct);
            DebugLog.Write("server", $"HandleClientAsync response ready command={request.Command} id={request.Id}");
            await writer.WriteLineAsync(JsonOutput.Serialize(response).AsMemory(), ct);
            DebugLog.Write("server", $"HandleClientAsync response written command={request.Command} id={request.Id}");
        }
        catch (Exception ex)
        {
            Log($"Client error: {ex.Message}");
            try
            {
                var errResponse = CreateErrorResponse(
                    "",
                    new ErrorInfo("INTERNAL_ERROR", "Request processing failed.", new { hint = "Check daemon logs for details." }),
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
        DebugLog.Write("server", $"DispatchAsync begin command={req.Command} id={req.Id}");
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
                "unused"   => await HandleUnusedAsync(req, ct),
                "status"   => HandleStatus(),
                "reload"   => await HandleReloadAsync(ct),
                "setIdleTimeout" => HandleSetIdleTimeout(req),
                "shutdown" => HandleShutdown(),
                _ => throw new InvalidOperationException($"Unknown command: {req.Command}")
            };

            if (req.Command == "symbols" && result is Models.SymbolsResultPage symbolsPage)
            {
                return CreateSuccessResponse(
                    req.Id,
                    symbolsPage.Items,
                    new Models.PageResponse(
                        Offset: GetSymbolsOffset(req),
                        Limit: GetSymbolsLimit(req),
                        HasMore: symbolsPage.HasMore),
                    new ResponseMeta(sw.ElapsedMilliseconds, _loadedAt));
            }

            return CreateSuccessResponse(
                req.Id,
                result,
                new ResponseMeta(sw.ElapsedMilliseconds, _loadedAt));
        }
        catch (DaemonValidationException ex)
        {
            return CreateProblemResponse(
                req.Id,
                ex.Error,
                new ResponseMeta(sw.ElapsedMilliseconds, _loadedAt));
        }
        catch (ArgumentException ex)
        {
            return CreateProblemResponse(
                req.Id,
                new ErrorInfo("INVALID_PARAMS", ex.Message),
                new ResponseMeta(sw.ElapsedMilliseconds, _loadedAt));
        }
        catch (Exception ex)
        {
            Log($"Dispatch error ({req.Command}): {ex.Message}");
            return CreateErrorResponse(
                req.Id,
                new ErrorInfo("INTERNAL_ERROR", "Unexpected daemon error.", new { hint = "Check daemon logs for details." }),
                new ResponseMeta(sw.ElapsedMilliseconds, _loadedAt));
        }
        finally
        {
            DebugLog.Write("server", $"DispatchAsync end command={req.Command} id={req.Id} durationMs={sw.ElapsedMilliseconds}");
        }
    }

    private static DaemonResponse CreateSuccessResponse(string id, object? data, ResponseMeta? meta)
        => CreateSuccessResponse(id, data, page: null, meta);

    private static DaemonResponse CreateSuccessResponse(string id, object? data, Models.PageResponse? page, ResponseMeta? meta)
        => new(
            Id: id,
            Status: DaemonResponseStatus.Ok,
            Result: data,
            Error: null,
            Debug: null,
            Page: page,
            Meta: meta);

    private static DaemonResponse CreateProblemResponse(string id, ErrorInfo error, ResponseMeta? meta)
        => new(
            Id: id,
            Status: DaemonResponseStatus.Problem,
            Result: null,
            Error: error,
            Debug: null,
            Page: null,
            Meta: meta);

    private static DaemonResponse CreateErrorResponse(string id, ErrorInfo error, ResponseMeta? meta)
        => new(
            Id: id,
            Status: DaemonResponseStatus.Error,
            Result: null,
            Error: error,
            Debug: null,
            Page: null,
            Meta: meta);

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
        var solutionDir = GetSolutionDir(solution);

        return refs
            .SelectMany(r => r.Locations)
            .Select(loc =>
            {
                var (file, line, col) = loc.Location.GetFileLineColRelative(solutionDir);
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
        var p = GetParams(req);
        var solution = GetSolution();
        var newName = GetRequiredString(p, "to");
        var dryRun = p.TryGetValue("dryRun", out var dr) && dr?.ToString() == "True";
        var symbol = GetOptionalString(p, "symbol");
        var file = symbol is null ? GetRequiredString(p, "file") : GetOptionalString(p, "file");
        var line = symbol is null ? GetRequiredInt(p, "line") : GetOptionalInt(p, "line");
        var col = symbol is null ? GetRequiredInt(p, "col") : GetOptionalInt(p, "col");

        var result = await DotnetAICraft.Commands.Rename.UseCase.ResolveAsync(
            solution,
            newName,
            dryRun,
            symbol,
            file,
            line,
            col,
            ct);

        if (result.Applied)
        {
            await _solutionLock.WaitAsync(ct);
            try
            {
                var symbolName = symbol;
                ISymbol resolved = symbolName is not null
                    ? await SymbolResolver.FromFullNameAsync(solution, symbolName, ct)
                    : await SymbolResolver.FromLocationAsync(solution,
                        GetRequiredString(p, "file"),
                        GetRequiredInt(p, "line"),
                        GetRequiredInt(p, "col"), ct);

                var appliedSolution = await Renamer.RenameSymbolAsync(
                    solution, resolved, new SymbolRenameOptions(), newName, ct);

                _workspace!.TryApplyChanges(appliedSolution);
                _solution = _workspace.CurrentSolution;
            }
            finally { _solutionLock.Release(); }
        }

        return result;
    }

    private async Task<object> HandleImplsAsync(DaemonRequest req, CancellationToken ct)
    {
        var p        = GetParams(req);
        var solution = GetSolution();

        return await DotnetAICraft.Commands.Impls.UseCase.ResolveAsync(
            solution,
            GetRequiredString(p, "symbol"),
            ct);
    }

    private async Task<object> HandleCallersAsync(DaemonRequest req, CancellationToken ct)
    {
        var p = GetParams(req);
        var solution = GetSolution();

        return await DotnetAICraft.Commands.Callers.UseCase.ResolveAsync(
            solution,
            GetOptionalString(p, "symbol"),
            GetOptionalString(p, "file"),
            GetOptionalInt(p, "line"),
            GetOptionalInt(p, "col"),
            GetOptionalString(p, "direction"),
            GetOptionalInt(p, "depth"),
            ct);
    }


    private async Task<object> HandleSymbolsAsync(DaemonRequest req, CancellationToken ct)
    {
        var p       = GetParams(req);
        var pattern = p.TryGetValue("pattern", out var pat) ? pat?.ToString() ?? "*" : "*";
        var kind    = p.TryGetValue("kind", out var k) ? k?.ToString() ?? "all" : "all";
        var limit   = req.Page?.Limit ?? GetOptionalInt(p, "limit");
        var offset  = req.Page?.Offset ?? GetOptionalInt(p, "offset");

        return await DotnetAICraft.Commands.Symbols.UseCase.ResolveAsync(
            GetSolution(),
            pattern,
            kind,
            limit,
            offset,
            ct);
    }

    private async Task<object> HandleDiagnosticsAsync(DaemonRequest req, CancellationToken ct)
    {
        var p = GetParams(req);

        return await DotnetAICraft.Commands.Diagnostics.UseCase.ResolveAsync(
            GetSolution(),
            GetOptionalString(p, "severity") ?? "all",
            GetOptionalString(p, "project"),
            GetOptionalString(p, "file"),
            ct);
    }

    private async Task<object> HandleUnusedAsync(DaemonRequest req, CancellationToken ct)
    {
        var p = GetParams(req);

        return await DotnetAICraft.Commands.Unused.UseCase.ResolveAsync(
            GetSolution(),
            GetOptionalString(p, "kind") ?? "all",
            GetOptionalString(p, "project"),
            GetOptionalBool(p, "publicOnly") ?? false,
            GetOptionalBool(p, "includeGenerated") ?? false,
            ct);
    }

    private object HandleStatus()
    {
        var loadState = _loadState;
        var lastLoadAttemptAt = _lastLoadAttemptAt;
        var lastLoadError = _lastLoadError;
        var loadedAt = _loadedAt;
        var uptimeBase = loadState == DaemonLoadState.Loaded ? loadedAt : _startedAt;

        var projects = 0;
        var documents = 0;

        if (_solution is Solution solution)
        {
            projects = solution.Projects.Count();
            documents = solution.Projects.Sum(p => p.Documents.Count());
        }

        return new Models.DaemonStatus(
            Running:      true,
            SolutionPath: _solutionPath,
            Projects:     projects,
            Documents:    documents,
            LoadedAt:     loadedAt,
            Uptime:       DateTime.UtcNow - uptimeBase,
            LoadState:    loadState.ToString().ToLowerInvariant(),
            LastLoadAttemptAt: lastLoadAttemptAt,
            LastLoadErrorCode: lastLoadError?.Code,
            LastLoadErrorMessage: lastLoadError?.Message);
    }

    private async Task<object> HandleReloadAsync(CancellationToken ct)
    {
        var reloaded = await TryLoadSolutionAsync(ct);
        return new
        {
            reloaded,
            loadState = _loadState.ToString().ToLowerInvariant(),
            loadedAt = _loadedAt,
            lastLoadAttemptAt = _lastLoadAttemptAt,
            lastLoadErrorCode = _lastLoadError?.Code,
            lastLoadErrorMessage = _lastLoadError?.Message
        };
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
            if (_hasExplicitIdleTimeoutOverride && _idleTimeout.Equals(setting))
            {
                return new Models.IdleTimeoutUpdateResult(
                    Applied: true,
                    Mode: setting.Enabled ? "duration" : "off",
                    Value: setting.Enabled ? setting.Normalized : null,
                    Changed: false);
            }

            DateTime nextDeadlineUtc;
            var effectiveSetting = GetEffectiveIdleTimeoutSetting(setting);
            if (effectiveSetting.Enabled)
            {
                try
                {
                    nextDeadlineUtc = DateTime.UtcNow + effectiveSetting.Duration;
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
            _hasExplicitIdleTimeoutOverride = true;
            _idleDeadlineUtc = nextDeadlineUtc;

            CancelIdleWatcherUnsafe();
            if (effectiveSetting.Enabled)
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
            var effectiveSetting = GetEffectiveIdleTimeoutSetting(_idleTimeout);
            if (!effectiveSetting.Enabled)
            {
                _idleDeadlineUtc = DateTime.MaxValue;
                return;
            }

            try
            {
                _idleDeadlineUtc = DateTime.UtcNow + effectiveSetting.Duration;
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
        if (!GetEffectiveIdleTimeoutSetting(_idleTimeout).Enabled || _cts.IsCancellationRequested)
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
            if (!GetEffectiveIdleTimeoutSetting(_idleTimeout).Enabled)
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
            if (!GetEffectiveIdleTimeoutSetting(_idleTimeout).Enabled)
                return;

            if (_activeRequests > 0)
                return;

            if (DateTime.UtcNow < _idleDeadlineUtc)
                return;
        }

        BeginShutdown();
    }

    private DaemonIdleTimeoutSetting GetEffectiveIdleTimeoutSetting(DaemonIdleTimeoutSetting configured)
    {
        if (_hasExplicitIdleTimeoutOverride)
            return configured;

        if (_loadState == DaemonLoadState.Unloaded)
            return new DaemonIdleTimeoutSetting(true, TimeSpan.FromMinutes(UnloadedIdleTimeoutMinutes), $"{UnloadedIdleTimeoutMinutes}m");

        return configured;
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

    private async Task<bool> TryLoadSolutionAsync(CancellationToken ct = default)
    {
        await _solutionLock.WaitAsync(ct);
        try
        {
            _lastLoadAttemptAt = DateTime.UtcNow;

            _workspace?.Dispose();
            _workspace = null;
            _solution = null;

            var (workspace, solution) = await WorkspaceLoader.LoadAsync(_solutionPath);
            _workspace = workspace;
            _solution = solution;
            _loadedAt = DateTime.UtcNow;
            _loadState = DaemonLoadState.Loaded;
            _lastLoadError = null;

            Log($"Solution loaded: {solution.Projects.Count()} projects, " +
                $"{solution.Projects.Sum(p => p.Documents.Count())} documents");

            return true;
        }
        catch (Exception ex)
        {
            _loadState = DaemonLoadState.Unloaded;
            _lastLoadError = new ErrorInfo("SOLUTION_LOAD_FAILED", "Failed to load solution.", new { exceptionType = ex.GetType().Name });
            Log($"Solution load failed: {ex.Message}");
            return false;
        }
        finally
        {
            _solutionLock.Release();
            ResetIdleDeadline();
        }
    }

    private Solution GetSolution()
    {
        if (_solution is not null)
            return _solution;

        throw new DaemonValidationException(new ErrorInfo(
            "SOLUTION_UNAVAILABLE",
            "Solution is currently unavailable.",
            new
            {
                solutionPath = _solutionPath,
                loadState = _loadState.ToString().ToLowerInvariant(),
                lastLoadErrorCode = _lastLoadError?.Code,
                hint = "Run 'server reload' or fix the solution/project files and retry."
            }));
    }

    private static string GetSolutionDir(Solution solution)
        => Path.GetDirectoryName(solution.FilePath) ?? string.Empty;

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
            if (IsBuildArtifactPath(e.FullPath)) return;

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

    private static bool IsBuildArtifactPath(string fullPath)
    {
        // Skip build outputs (obj/, bin/) and auto-generated XAML codebehind (*.g.cs).
        // These regenerate during MSBuild design-time build (which Roslyn runs on solution load)
        // and don't represent user edits — propagating them as WithDocumentText updates produces
        // a noisy cascade and treats build artifacts as source-of-truth.
        var sep = Path.DirectorySeparatorChar;
        var alt = Path.AltDirectorySeparatorChar;
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        if (fullPath.Contains($"{sep}obj{sep}", cmp) || fullPath.Contains($"{alt}obj{alt}", cmp) ||
            fullPath.Contains($"{sep}bin{sep}", cmp) || fullPath.Contains($"{alt}bin{alt}", cmp))
            return true;

        var name = Path.GetFileName(fullPath);
        return name.EndsWith(".g.cs", cmp) || name.EndsWith(".g.i.cs", cmp);
    }

    private void StartMetadataWatcher()
    {
        var dir = Path.GetDirectoryName(_solutionPath)!;
        _metadataWatcher = new FileSystemWatcher(dir)
        {
            IncludeSubdirectories = true,
            EnableRaisingEvents = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName
        };

        void OnMetadataChange(object _, FileSystemEventArgs e)
        {
            if (!IsMetadataReloadCandidate(e.FullPath))
                return;

            ScheduleMetadataReload();
        }

        _metadataWatcher.Changed += OnMetadataChange;
        _metadataWatcher.Created += OnMetadataChange;
        _metadataWatcher.Renamed += (_, e) =>
        {
            if (IsMetadataReloadCandidate(e.FullPath) || IsMetadataReloadCandidate(e.OldFullPath))
                ScheduleMetadataReload();
        };
    }

    private static bool IsMetadataReloadCandidate(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        var fileName = Path.GetFileName(path);
        if (string.Equals(fileName, "Directory.Build.props", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(fileName, "Directory.Build.targets", StringComparison.OrdinalIgnoreCase))
            return true;

        var ext = Path.GetExtension(path);
        return ext.Equals(".sln", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".csproj", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".vbproj", StringComparison.OrdinalIgnoreCase) ||
               ext.Equals(".fsproj", StringComparison.OrdinalIgnoreCase);
    }

    private void ScheduleMetadataReload()
    {
        CancellationTokenSource cts;
        lock (_metadataReloadLock)
        {
            _metadataReloadDebounceCts?.Cancel();
            _metadataReloadDebounceCts?.Dispose();
            _metadataReloadDebounceCts = new CancellationTokenSource();
            cts = _metadataReloadDebounceCts;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), cts.Token);
                await TriggerMetadataReloadAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
            }
        }, CancellationToken.None);
    }

    private async Task TriggerMetadataReloadAsync(CancellationToken ct)
    {
        if (!await _metadataReloadSingleFlight.WaitAsync(0, ct))
            return;

        try
        {
            await TryLoadSolutionAsync(ct);
        }
        finally
        {
            _metadataReloadSingleFlight.Release();
        }
    }

    private async Task ApplyFileChangeAsync(string filePath)
    {
        await _solutionLock.WaitAsync();
        try
        {
            var solution = GetSolution();
            var docIds   = solution.GetDocumentIdsWithFilePath(filePath);
            if (docIds.IsEmpty) return;

            var content = await File.ReadAllTextAsync(filePath);
            // Guard against empty reads during concurrent writes (e.g. build tools flushing)
            if (string.IsNullOrEmpty(content)) return;

            var newText = SourceText.From(content, Encoding.UTF8);
            // Update in-memory solution only — do NOT call TryApplyChanges, which writes back to disk
            _solution = solution.WithDocumentText(docIds.First(), newText);
            Log($"Updated: {Path.GetFileName(filePath)}");
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
        try
        {
            return await UseCase.ResolveAsync(solution, symbol, file, line, col, ct);
        }
        catch (ArgumentException ex)
        {
            throw new DaemonValidationException(new ErrorInfo("INVALID_PARAMS", ex.Message));
        }
    }

    public static bool TryParseCallGraphDirection(string? raw, out string normalized)
        => TryParseCallGraphDirection(raw, out _, out normalized);

    private static bool TryParseCallGraphDirection(
        string? raw,
        out CallGraphDirection direction,
        out string normalized)
    {
        normalized = string.IsNullOrWhiteSpace(raw)
            ? CallGraphDefaultDirection
            : raw.Trim().ToLowerInvariant();

        switch (normalized)
        {
            case "incoming":
                direction = CallGraphDirection.Incoming;
                return true;
            case "outgoing":
                direction = CallGraphDirection.Outgoing;
                return true;
            case "both":
                direction = CallGraphDirection.Both;
                return true;
            default:
                direction = CallGraphDirection.Incoming;
                return false;
        }
    }

    public static bool TryNormalizeCallGraphDepth(
        int? depth,
        out int normalizedDepth,
        out ErrorInfo? error)
    {
        normalizedDepth = depth ?? CallGraphDefaultDepth;

        if (normalizedDepth < 1)
        {
            error = new ErrorInfo(
                "INVALID_PARAMS",
                "Parameter 'depth' must be greater than or equal to 1.",
                new { min = 1, @default = CallGraphDefaultDepth });
            return false;
        }

        error = null;
        return true;
    }

    public static async Task<IReadOnlyList<Models.CallerResult>> CollectIncomingCallersAsync(
        Solution solution,
        ISymbol symbol,
        CancellationToken ct = default)
    {
        var callers = await SymbolFinder.FindCallersAsync(symbol, solution, ct);
        var solutionDir = Path.GetDirectoryName(solution.FilePath) ?? string.Empty;

        return callers.Select(c =>
        {
            var loc = c.Locations.FirstOrDefault();
            var (file, line, col) = loc is not null
                ? loc.GetFileLineColRelative(solutionDir)
                : ("", 0, 0);

            return new Models.CallerResult(
                CallerSymbol: c.CallingSymbol.ToDisplayString(),
                CallerKind: c.CallingSymbol.GetKindName(),
                IsDirect: c.IsDirect,
                File: file,
                Line: line,
                Col: col,
                Context: loc?.GetContextLine() ?? "");
        }).ToList();
    }

    public static async Task<Models.CallGraphResult> CollectCallGraphAsync(
        Solution solution,
        ISymbol symbol,
        string? direction = null,
        int? depth = null,
        CancellationToken ct = default)
    {
        if (!TryParseCallGraphDirection(direction, out var parsedDirection, out _))
            throw new DaemonValidationException(BuildInvalidCallGraphDirectionError());

        if (!TryNormalizeCallGraphDepth(depth, out var normalizedDepth, out var depthError))
            throw new DaemonValidationException(depthError!);

        return await CollectCallGraphAsync(solution, symbol, parsedDirection, normalizedDepth, ct);
    }

    private static async Task<Models.CallGraphResult> CollectCallGraphAsync(
        Solution solution,
        ISymbol rootSymbol,
        CallGraphDirection direction,
        int depth,
        CancellationToken ct)
    {
        var solutionDir = Path.GetDirectoryName(solution.FilePath) ?? string.Empty;
        var rootNode = CreateCallGraphNode(rootSymbol, solutionDir);
        var nodes = new Dictionary<string, Models.CallGraphNode>(StringComparer.Ordinal)
        {
            [rootNode.Id] = rootNode
        };

        var edges = new List<Models.CallGraphEdge>();
        var edgeKeys = new HashSet<string>(StringComparer.Ordinal);

        var queue = new Queue<(ISymbol Symbol, int CurrentDepth)>();
        queue.Enqueue((rootSymbol, 0));

        var visited = new HashSet<string>(StringComparer.Ordinal);

        while (queue.Count > 0)
        {
            ct.ThrowIfCancellationRequested();

            var (currentSymbol, currentDepth) = queue.Dequeue();
            var currentNode = CreateCallGraphNode(currentSymbol, solutionDir);
            nodes[currentNode.Id] = currentNode;

            if (currentDepth >= depth)
                continue;

            if (!visited.Add(currentNode.Id))
                continue;

            if (direction is CallGraphDirection.Incoming or CallGraphDirection.Both)
            {
                var incomingCallers = await SymbolFinder.FindCallersAsync(currentSymbol, solution, ct);

                foreach (var callerInfo in incomingCallers)
                {
                    var callerNode = CreateCallGraphNode(callerInfo.CallingSymbol, solutionDir);
                    nodes[callerNode.Id] = callerNode;

                    AddEdge(callerNode.Id, currentNode.Id, "incoming", callerInfo.IsDirect);
                    queue.Enqueue((callerInfo.CallingSymbol, currentDepth + 1));
                }
            }

            if (direction is CallGraphDirection.Outgoing or CallGraphDirection.Both)
            {
                var outgoingCallees = await CollectOutgoingCalleesAsync(solution, currentSymbol, ct);

                foreach (var callee in outgoingCallees)
                {
                    var calleeNode = CreateCallGraphNode(callee, solutionDir);
                    nodes[calleeNode.Id] = calleeNode;

                    AddEdge(currentNode.Id, calleeNode.Id, "outgoing", true);
                    queue.Enqueue((callee, currentDepth + 1));
                }
            }
        }

        return new Models.CallGraphResult(
            RootId: rootNode.Id,
            Direction: direction.ToString().ToLowerInvariant(),
            Depth: depth,
            Nodes: nodes.Values
                .OrderBy(node => node.Id, StringComparer.Ordinal)
                .ToList(),
            Edges: edges
                .OrderBy(edge => edge.From, StringComparer.Ordinal)
                .ThenBy(edge => edge.To, StringComparer.Ordinal)
                .ThenBy(edge => edge.Relation, StringComparer.Ordinal)
                .ToList());

        void AddEdge(string from, string to, string relation, bool isDirect)
        {
            var edgeKey = $"{from}|{to}|{relation}|{isDirect}";
            if (!edgeKeys.Add(edgeKey))
                return;

            edges.Add(new Models.CallGraphEdge(
                From: from,
                To: to,
                Relation: relation,
                IsDirect: isDirect));
        }
    }

    private static async Task<IReadOnlyList<ISymbol>> CollectOutgoingCalleesAsync(
        Solution solution,
        ISymbol symbol,
        CancellationToken ct)
    {
        if (symbol.DeclaringSyntaxReferences.Length == 0)
            return [];

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var callees = new List<ISymbol>();

        foreach (var syntaxReference in symbol.DeclaringSyntaxReferences)
        {
            ct.ThrowIfCancellationRequested();

            var syntax = await syntaxReference.GetSyntaxAsync(ct);
            var document = solution.GetDocument(syntax.SyntaxTree);
            if (document is null)
                continue;

            var semanticModel = await document.GetSemanticModelAsync(ct);
            if (semanticModel is null)
                continue;

            foreach (var invocation in syntax.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                ct.ThrowIfCancellationRequested();

                var symbolInfo = semanticModel.GetSymbolInfo(invocation, ct);
                var invoked = symbolInfo.Symbol ?? symbolInfo.CandidateSymbols.FirstOrDefault();
                if (invoked is not IMethodSymbol methodSymbol)
                    continue;

                var key = GetCallGraphSymbolKey(methodSymbol);
                if (seen.Add(key))
                    callees.Add(methodSymbol);
            }
        }

        return callees;
    }

    private static Models.CallGraphNode CreateCallGraphNode(ISymbol symbol, string solutionDir)
    {
        var sourceLocation = symbol.Locations.FirstOrDefault(location => location.IsInSource);
        var (file, line, col) = sourceLocation is not null
            ? sourceLocation.GetFileLineColRelative(solutionDir)
            : ("", 0, 0);

        return new Models.CallGraphNode(
            Id: GetCallGraphSymbolKey(symbol),
            FullName: symbol.ToDisplayString(),
            Kind: symbol.GetKindName(),
            File: file,
            Line: line,
            Col: col,
            ContainingType: symbol.ContainingType?.ToDisplayString(),
            ContainingNamespace: symbol.ContainingNamespace?.ToDisplayString());
    }

    private static string GetCallGraphSymbolKey(ISymbol symbol)
        => symbol switch
        {
            IMethodSymbol methodSymbol => methodSymbol.OriginalDefinition.ToDisplayString(CallGraphSymbolDisplayFormat),
            INamedTypeSymbol namedTypeSymbol => namedTypeSymbol.OriginalDefinition.ToDisplayString(CallGraphSymbolDisplayFormat),
            IPropertySymbol propertySymbol => propertySymbol.OriginalDefinition.ToDisplayString(CallGraphSymbolDisplayFormat),
            IFieldSymbol fieldSymbol => fieldSymbol.OriginalDefinition.ToDisplayString(CallGraphSymbolDisplayFormat),
            IEventSymbol eventSymbol => eventSymbol.OriginalDefinition.ToDisplayString(CallGraphSymbolDisplayFormat),
            _ => symbol.ToDisplayString(CallGraphSymbolDisplayFormat)
        };

    private static ErrorInfo BuildInvalidCallGraphDirectionError()
        => new(
            "INVALID_PARAMS",
            "Invalid 'direction' parameter.",
            new { acceptedValues = CallGraphDirectionAcceptedValues });

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


    private static bool TryGetSymbolsKindDefinition(
        string? raw,
        out SymbolsKindDefinition definition,
        out string normalized)
    {
        normalized = string.IsNullOrWhiteSpace(raw)
            ? "all"
            : raw.Trim().ToLowerInvariant();

        return SymbolsKindDefinitions.TryGetValue(normalized, out definition);
    }

    private static IEnumerable<ISymbol> ExpandConstructors(ISymbol symbol)
    {
        if (symbol is not INamedTypeSymbol namedType)
            yield break;

        foreach (var constructor in namedType.Constructors)
        {
            if (!constructor.IsImplicitlyDeclared)
                yield return constructor;
        }
    }

    public static bool TryParseSymbolsKind(
        string? raw,
        out SymbolFilter filter,
        out Func<ISymbol, bool> predicate,
        out string normalized)
    {
        if (TryGetSymbolsKindDefinition(raw, out var definition, out normalized))
        {
            filter = definition.Filter;
            predicate = definition.Predicate;
            return true;
        }

        filter = SymbolFilter.All;
        predicate = AnySymbolPredicate;
        return false;
    }

    public static bool TryParseUnusedKind(string? raw, out string normalized)
        => TryGetSymbolsKindDefinition(raw, out _, out normalized);

    private static ErrorInfo BuildInvalidSymbolsKindError()
        => new(
            "INVALID_PARAMS",
            "Invalid 'kind' parameter.",
            new { acceptedValues = SymbolsKindAcceptedValues });

    private static ErrorInfo BuildInvalidUnusedKindError()
        => new(
            "INVALID_PARAMS",
            "Invalid 'kind' parameter.",
            new { acceptedValues = UnusedKindAcceptedValues });

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
        string? kind,
        int? limit = null,
        int? offset = null,
        CancellationToken ct = default)
    {
        if (!TryGetSymbolsKindDefinition(kind, out var definition, out _))
            throw new DaemonValidationException(BuildInvalidSymbolsKindError());

        return await CollectSymbolsAsyncCore(
            solution,
            pattern,
            definition.Filter,
            definition.Expand,
            definition.Predicate,
            limit,
            offset,
            ct);
    }

    public static async Task<Models.SymbolsResultPage> CollectSymbolsAsync(
        Solution solution,
        string pattern,
        SymbolFilter filter = SymbolFilter.All,
        Func<ISymbol, bool>? predicate = null,
        int? limit = null,
        int? offset = null,
        CancellationToken ct = default)
    {
        return await CollectSymbolsAsyncCore(
            solution,
            pattern,
            filter,
            IdentitySymbolExpander,
            predicate,
            limit,
            offset,
            ct);
    }

    private static async Task<Models.SymbolsResultPage> CollectSymbolsAsyncCore(
        Solution solution,
        string pattern,
        SymbolFilter filter,
        Func<ISymbol, IEnumerable<ISymbol>> expand,
        Func<ISymbol, bool>? predicate,
        int? limit,
        int? offset,
        CancellationToken ct)
    {
        if (!TryNormalizeSymbolsPagination(limit, offset, out var normalizedLimit, out var normalizedOffset, out var error))
            throw new DaemonValidationException(error!);

        var matchesKind = predicate ?? AnySymbolPredicate;
        var results = new List<Models.SymbolResult>(normalizedLimit);
        var skipped = 0;
        var hasMore = false;
        var solutionDir = Path.GetDirectoryName(solution.FilePath) ?? string.Empty;

        await foreach (var symbol in SymbolResolver.SearchAsync(solution, pattern, filter, ct))
        {
            foreach (var candidate in expand(symbol))
            {
                if (!matchesKind(candidate))
                    continue;

                if (skipped < normalizedOffset)
                {
                    skipped++;
                    continue;
                }

                if (results.Count < normalizedLimit)
                {
                    results.Add(SymbolToResult(candidate, solutionDir));
                    continue;
                }

                hasMore = true;
                break;
            }

            if (hasMore)
                break;
        }

        return new Models.SymbolsResultPage(results, hasMore);
    }

    public static async Task<Models.UnusedScanSummary> CollectUnusedAsync(
        Solution solution,
        string? kind = null,
        string? projectFilter = null,
        bool publicOnly = false,
        bool includeGenerated = false,
        CancellationToken ct = default)
    {
        if (!TryGetSymbolsKindDefinition(kind, out var definition, out var normalizedKind))
            throw new DaemonValidationException(BuildInvalidUnusedKindError());

        var normalizedProject = string.IsNullOrWhiteSpace(projectFilter)
            ? null
            : projectFilter.Trim();

        var scanned = 0;
        var results = new List<Models.UnusedCandidateResult>();
        var seenSymbols = new HashSet<string>(StringComparer.Ordinal);
        var solutionDir = Path.GetDirectoryName(solution.FilePath) ?? string.Empty;

        await foreach (var symbol in SymbolResolver.SearchAsync(solution, "*", definition.Filter, ct))
        {
            foreach (var candidate in definition.Expand(symbol))
            {
                ct.ThrowIfCancellationRequested();

                if (!definition.Predicate(candidate))
                    continue;

                if (!ShouldAnalyzeForUnused(candidate, publicOnly))
                    continue;

                var symbolKey = GetCallGraphSymbolKey(candidate);
                if (!seenSymbols.Add(symbolKey))
                    continue;

                if (ShouldExcludeUnusedByHeuristics(candidate))
                    continue;

                var sourceLocation = await GetPrimarySourceLocationAsync(candidate, solution, solutionDir, ct);
                if (sourceLocation is null)
                    continue;

                if (normalizedProject is not null &&
                    !string.Equals(sourceLocation.Value.Project, normalizedProject, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!includeGenerated && sourceLocation.Value.IsGenerated)
                    continue;

                scanned++;

                var refs = await SymbolFinder.FindReferencesAsync(candidate, solution, ct);
                var referencesCount = refs.Sum(reference => reference.Locations.Count());

                if (referencesCount > 0)
                    continue;

                var (reason, confidence) = BuildUnusedReasonAndConfidence(candidate);

                results.Add(new Models.UnusedCandidateResult(
                    Symbol: candidate.ToDisplayString(),
                    Kind: candidate.GetKindName(),
                    File: sourceLocation.Value.File,
                    Line: sourceLocation.Value.Line,
                    Col: sourceLocation.Value.Col,
                    Project: sourceLocation.Value.Project,
                    Reason: reason,
                    Confidence: confidence));
            }
        }

        var ordered = results
            .OrderByDescending(r => r.Confidence)
            .ThenBy(r => r.Project, StringComparer.Ordinal)
            .ThenBy(r => r.File, StringComparer.Ordinal)
            .ThenBy(r => r.Line)
            .ThenBy(r => r.Col)
            .ThenBy(r => r.Symbol, StringComparer.Ordinal)
            .ToList();

        return new Models.UnusedScanSummary(
            Kind: normalizedKind,
            Project: normalizedProject,
            PublicOnly: publicOnly,
            IncludeGenerated: includeGenerated,
            Scanned: scanned,
            Items: ordered);
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
        var solutionDir = Path.GetDirectoryName(solution.FilePath) ?? string.Empty;

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

                var absolutePath = diagnostic.Location is { IsInSource: true } location
                    ? location.GetLineSpan().Path
                    : null;
                if (!MatchesFileFilter(absolutePath, normalizedFile))
                    continue;

                results.Add(MapDiagnostic(project.Name, diagnostic, solutionDir));
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

    private static Models.DiagnosticResult MapDiagnostic(string projectName, Diagnostic diagnostic, string solutionDir)
    {
        string? file = null;
        int? line = null;
        int? col = null;
        int? endLine = null;
        int? endCol = null;

        if (diagnostic.Location is { IsInSource: true } location)
        {
            var lineSpan = location.GetLineSpan();
            file = PathFormatter.ToRelative(lineSpan.Path, solutionDir);
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

    private readonly record struct SourceSymbolLocation(
        string File,
        int Line,
        int Col,
        string Project,
        bool IsGenerated);

    private static bool ShouldAnalyzeForUnused(ISymbol symbol, bool publicOnly)
    {
        if (symbol.IsImplicitlyDeclared)
            return false;

        if (symbol.DeclaringSyntaxReferences.Length == 0)
            return false;

        if (publicOnly && symbol.DeclaredAccessibility != Accessibility.Public)
            return false;

        if (symbol.ContainingType?.TypeKind == TypeKind.Interface)
            return false;

        if (symbol is IMethodSymbol method)
        {
            if (method.IsAbstract)
                return false;

            if (method.MethodKind is MethodKind.AnonymousFunction or MethodKind.LocalFunction)
                return false;

            if (method.MethodKind is MethodKind.PropertyGet or MethodKind.PropertySet or MethodKind.EventAdd or MethodKind.EventRemove or MethodKind.EventRaise)
                return false;
        }

        if (symbol is IPropertySymbol { IsAbstract: true } || symbol is IEventSymbol { IsAbstract: true })
            return false;

        return symbol is not INamespaceSymbol and not IAliasSymbol and not ILocalSymbol and not IParameterSymbol and not ITypeParameterSymbol;
    }

    private static bool ShouldExcludeUnusedByHeuristics(ISymbol symbol)
    {
        if (symbol switch
            {
                IMethodSymbol { IsOverride: true } => true,
                IPropertySymbol { IsOverride: true } => true,
                IEventSymbol { IsOverride: true } => true,
                _ => false
            })
        {
            return true;
        }

        if (ImplementsInterfaceMember(symbol))
            return true;

        if (symbol is IMethodSymbol method && IsEntryPointMethod(method))
            return true;

        return false;
    }

    private static bool ImplementsInterfaceMember(ISymbol symbol)
    {
        if (symbol is IMethodSymbol { ExplicitInterfaceImplementations.Length: > 0 })
            return true;

        if (symbol is IPropertySymbol { ExplicitInterfaceImplementations.Length: > 0 })
            return true;

        if (symbol is IEventSymbol { ExplicitInterfaceImplementations.Length: > 0 })
            return true;

        if (symbol is not IMethodSymbol && symbol is not IPropertySymbol && symbol is not IEventSymbol)
            return false;

        var containingType = symbol.ContainingType;
        if (containingType is null || containingType.AllInterfaces.IsDefaultOrEmpty)
            return false;

        var target = symbol.OriginalDefinition;

        foreach (var iface in containingType.AllInterfaces)
        {
            foreach (var interfaceMember in iface.GetMembers())
            {
                var implementation = containingType.FindImplementationForInterfaceMember(interfaceMember);
                if (implementation is null)
                    continue;

                if (SymbolEqualityComparer.Default.Equals(implementation.OriginalDefinition, target))
                    return true;
            }
        }

        return false;
    }

    private static bool IsEntryPointMethod(IMethodSymbol method)
    {
        if (!method.IsStatic || method.MethodKind != MethodKind.Ordinary)
            return false;

        if (!string.Equals(method.Name, "Main", StringComparison.Ordinal))
            return false;

        if (!IsValidEntryPointReturnType(method.ReturnType))
            return false;

        if (method.Parameters.Length == 0)
            return true;

        if (method.Parameters.Length != 1)
            return false;

        return method.Parameters[0].Type is IArrayTypeSymbol
        {
            Rank: 1,
            ElementType.SpecialType: SpecialType.System_String
        };
    }

    private static bool IsValidEntryPointReturnType(ITypeSymbol returnType)
    {
        if (returnType.SpecialType is SpecialType.System_Void or SpecialType.System_Int32)
            return true;

        if (returnType is not INamedTypeSymbol taskType)
            return false;

        if (!string.Equals(taskType.Name, "Task", StringComparison.Ordinal))
            return false;

        if (!string.Equals(taskType.ContainingNamespace?.ToDisplayString(), "System.Threading.Tasks", StringComparison.Ordinal))
            return false;

        if (!taskType.IsGenericType)
            return true;

        return taskType.TypeArguments.Length == 1 &&
               taskType.TypeArguments[0].SpecialType == SpecialType.System_Int32;
    }

    private static async Task<SourceSymbolLocation?> GetPrimarySourceLocationAsync(
        ISymbol symbol,
        Solution solution,
        string solutionDir,
        CancellationToken ct)
    {
        var sourceLocation = symbol.Locations.FirstOrDefault(location => location.IsInSource);
        if (sourceLocation is null)
            return null;

        var (file, line, col) = sourceLocation.GetFileLineCol();

        var document = sourceLocation.SourceTree is not null
            ? solution.GetDocument(sourceLocation.SourceTree)
            : null;

        if (document is null)
        {
            foreach (var syntaxReference in symbol.DeclaringSyntaxReferences)
            {
                document = solution.GetDocument(syntaxReference.SyntaxTree);
                if (document is not null)
                    break;
            }
        }

        if (document?.FilePath is { Length: > 0 } documentPath)
            file = documentPath;

        if (string.IsNullOrWhiteSpace(file))
            return null;

        var project = document?.Project.Name ?? symbol.ContainingAssembly?.Name ?? string.Empty;
        var generated = IsGeneratedFilePath(file);

        if (!generated && document is not null)
            generated = await ContainsAutoGeneratedMarkerAsync(document, ct);

        var relativeFile = PathFormatter.ToRelative(file, solutionDir) ?? string.Empty;
        return new SourceSymbolLocation(relativeFile, line, col, project, generated);
    }

    private static bool IsGeneratedFilePath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return false;

        var normalized = filePath.Replace('\\', '/').ToLowerInvariant();

        if (normalized.Contains("/obj/", StringComparison.Ordinal) ||
            normalized.Contains("/generated/", StringComparison.Ordinal))
        {
            return true;
        }

        return normalized.EndsWith(".g.cs", StringComparison.Ordinal) ||
               normalized.EndsWith(".g.i.cs", StringComparison.Ordinal) ||
               normalized.EndsWith(".designer.cs", StringComparison.Ordinal) ||
               normalized.EndsWith(".generated.cs", StringComparison.Ordinal) ||
               normalized.EndsWith(".assemblyattributes.cs", StringComparison.Ordinal) ||
               normalized.EndsWith(".assemblyinfo.cs", StringComparison.Ordinal) ||
               normalized.EndsWith(".globalusings.g.cs", StringComparison.Ordinal) ||
               normalized.EndsWith(".razor.g.cs", StringComparison.Ordinal);
    }

    private static async Task<bool> ContainsAutoGeneratedMarkerAsync(Document document, CancellationToken ct)
    {
        try
        {
            var text = await document.GetTextAsync(ct);
            var maxLines = Math.Min(8, text.Lines.Count);

            for (var i = 0; i < maxLines; i++)
            {
                var line = text.Lines[i].ToString();
                if (line.Contains("<auto-generated", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("<autogenerated", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static (string Reason, double Confidence) BuildUnusedReasonAndConfidence(ISymbol symbol)
    {
        var confidence = symbol.DeclaredAccessibility switch
        {
            Accessibility.Private => 0.98,
            Accessibility.Internal => 0.90,
            Accessibility.Protected => 0.75,
            Accessibility.ProtectedAndInternal => 0.72,
            Accessibility.ProtectedOrInternal => 0.72,
            Accessibility.Public => 0.60,
            _ => 0.70
        };

        var scope = symbol.DeclaredAccessibility.ToString().ToLowerInvariant();
        var reason = $"No references found for {scope} {symbol.GetKindName()} '{symbol.ToDisplayString()}'.";

        if (symbol.DeclaredAccessibility == Accessibility.Public)
            reason += " Public symbol may still be used outside the analyzed solution.";

        return (reason, confidence);
    }

    private static Models.SymbolResult SymbolToResult(ISymbol symbol, string solutionDir)
    {
        var loc  = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        var (file, line, col) = loc is not null ? loc.GetFileLineColRelative(solutionDir) : ("", 0, 0);

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

    private static bool? GetOptionalBool(Dictionary<string, object?> parameters, string key)
    {
        var raw = GetOptionalString(parameters, key);
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        if (bool.TryParse(raw, out var parsed))
            return parsed;

        throw new DaemonValidationException(new ErrorInfo(
            "INVALID_PARAMS",
            $"Parameter '{key}' must be a boolean."));
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

    private static void ValidateRequestMetadata(DaemonRequest request)
    {
        if (request.IdleTimeoutMinutes is <= 0)
        {
            throw new DaemonValidationException(new ErrorInfo(
                "INVALID_IDLE_TIMEOUT",
                "idleTimeoutMinutes must be greater than zero."));
        }

        if (request.Page is null)
            return;

        if (request.Page.Limit <= 0)
        {
            throw new DaemonValidationException(new ErrorInfo(
                "INVALID_PARAMS",
                "page.limit must be greater than zero."));
        }

        if (request.Page.Offset < 0)
        {
            throw new DaemonValidationException(new ErrorInfo(
                "INVALID_PARAMS",
                "page.offset must be greater than or equal to zero."));
        }
    }

    private void ApplyRequestIdleTimeoutIfPresent(DaemonRequest request)
    {
        if (request.IdleTimeoutMinutes is null)
            return;

        var minutes = request.IdleTimeoutMinutes.Value;
        var setting = new DaemonIdleTimeoutSetting(
            Enabled: true,
            Duration: TimeSpan.FromMinutes(minutes),
            Normalized: $"{minutes}m");
        _ = ApplyIdleTimeout(setting);
    }

    private static int GetSymbolsOffset(DaemonRequest request)
        => request.Page?.Offset ?? SymbolsDefaultOffset;

    private static int GetSymbolsLimit(DaemonRequest request)
        => request.Page?.Limit ?? SymbolsDefaultLimit;

    public async ValueTask DisposeAsync()
    {
        lock (_idleLock)
        {
            _lifecycleState = DaemonLifecycleState.Stopped;
            CancelIdleWatcherUnsafe();
        }

        _watcher?.Dispose();
        _metadataWatcher?.Dispose();
        lock (_metadataReloadLock)
        {
            _metadataReloadDebounceCts?.Cancel();
            _metadataReloadDebounceCts?.Dispose();
            _metadataReloadDebounceCts = null;
        }
        if (_startupLock is not null)
            await _startupLock.DisposeAsync();
        _cts.Dispose();
        _workspace?.Dispose();
        _solutionLock.Dispose();
        _metadataReloadSingleFlight.Dispose();
    }
}

public enum DaemonLifecycleState
{
    Running,
    Draining,
    Stopped
}

public enum DaemonLoadState
{
    Unloaded,
    Loaded
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
