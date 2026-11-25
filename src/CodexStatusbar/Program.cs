using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace CodexStatusbar;

/// <summary>Parsed command line. Nothing here touches the UI, so <c>--no-overlay</c> stays headless.</summary>
internal sealed record CommandLineOptions(
    string SessionRoot,
    string? SettingsPath,
    string? ForcedThreadId,
    bool Debug,
    bool NoOverlay,
    bool Background,
    bool RestartWait,
    bool PositionFastDebug,
    int? DurationSeconds)
{
    public static CommandLineOptions Parse(IReadOnlyList<string> args)
    {
        string? settingsPath = null;
        string? threadId = null;
        var debug = false;
        var noOverlay = false;
        var background = false;
        var restartWait = false;
        var positionFastDebug = false;
        int? durationSeconds = null;

        for (var index = 0; index < args.Count; index++)
        {
            var argument = args[index];
            if (argument.Equals("--debug", StringComparison.OrdinalIgnoreCase))
            {
                debug = true;
            }
            else if (argument.Equals("--no-overlay", StringComparison.OrdinalIgnoreCase))
            {
                noOverlay = true;
            }
            else if (argument.Equals("--background", StringComparison.OrdinalIgnoreCase)
                || argument.Equals("--watch-codex", StringComparison.OrdinalIgnoreCase))
            {
                // What the HKCU Run value launches: no visible window, tray only, and the overlay
                // appears by itself when Codex does.
                background = true;
            }
            else if (argument.Equals("--restart-wait", StringComparison.OrdinalIgnoreCase))
            {
                // Internal: the tray's "Restart Statusbar" relaunches this exe while the old process
                // is still releasing the single-instance mutex.
                restartWait = true;
            }
            else if (argument.Equals("--position-fast-debug", StringComparison.OrdinalIgnoreCase))
            {
                // The manual counterpart of the tray's "Position diagnostics": summarise the position
                // pipeline every second instead of only when something changes. It implies --debug,
                // because the block is written to the debug log.
                positionFastDebug = true;
                debug = true;
            }
            else if (argument.Equals("--thread", StringComparison.OrdinalIgnoreCase))
            {
                threadId = TakeValue(args, ref index, "--thread");
            }
            else if (argument.Equals("--duration", StringComparison.OrdinalIgnoreCase))
            {
                var raw = TakeValue(args, ref index, "--duration");
                durationSeconds = int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds)
                    && seconds > 0
                        ? seconds
                        : throw new ArgumentException("--duration needs a positive number of seconds.", nameof(args));
            }
            else if (argument.Equals("--settings", StringComparison.OrdinalIgnoreCase))
            {
                var raw = TakeValue(args, ref index, "--settings");
                if (!Path.IsPathFullyQualified(raw))
                {
                    throw new ArgumentException("--settings only accepts an absolute settings file path.", nameof(args));
                }

                settingsPath = Path.GetFullPath(raw);
            }
        }

        return new CommandLineOptions(
            SessionPathResolver.Resolve(args),
            settingsPath,
            threadId,
            debug,
            noOverlay,
            background,
            restartWait,
            positionFastDebug,
            durationSeconds);
    }

    private static string TakeValue(IReadOnlyList<string> args, ref int index, string name)
    {
        if (index + 1 >= args.Count || string.IsNullOrWhiteSpace(args[index + 1]))
        {
            throw new ArgumentException($"{name} requires a value.", nameof(args));
        }

        index++;
        return args[index];
    }
}

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        CommandLineOptions options;
        try
        {
            options = CommandLineOptions.Parse(args);
        }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine(exception.Message);
            return 2;
        }

        if (ProbeRunner.TryRun(args, options.SessionRoot))
        {
            return ProbeRunner.LastExitCode;
        }

        // Startup registration is a plain CLI action: it needs no overlay, no tray and no mutex, and
        // it must stay usable from a script or a support session.
        if (StartupCommandLine.TryRun(args, options.SettingsPath))
        {
            return StartupCommandLine.LastExitCode;
        }

        if (options.NoOverlay)
        {
            return HeadlessRunner.Run(options);
        }

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        // Single instance. A logon launch, a double-click and start-monitor.bat must never produce two
        // overlays, two trays or two IPC consumers. The second process exits quietly — except when it
        // is a deliberate restart, which waits for the outgoing process to release the name.
        using var singleInstanceMutex = AcquireSingleInstance(options.RestartWait);
        if (singleInstanceMutex is null)
        {
            return 0;
        }

        Application.Run(new OverlayContext(
            options.SessionRoot,
            options.SettingsPath,
            new DebugDiagnostics(options.Debug),
            options.ForcedThreadId,
            options.Background,
            options.PositionFastDebug));
        GC.KeepAlive(singleInstanceMutex);
        return 0;
    }

    /// <summary>Name of the single-instance object. <c>Local\</c>: one per logon session, no admin.</summary>
    internal const string SingleInstanceMutexName = "Local\\CodexStatusbar.SingleInstance";

    private static Mutex? AcquireSingleInstance(bool waitForRestart)
    {
        var deadline = DateTime.UtcNow.AddSeconds(waitForRestart ? 15 : 0);

        while (true)
        {
            var mutex = new Mutex(
                initiallyOwned: true,
                name: SingleInstanceMutexName,
                createdNew: out var createdNew);
            if (createdNew)
            {
                return mutex;
            }

            mutex.Dispose();
            if (DateTime.UtcNow >= deadline)
            {
                // Another instance owns the name. Nothing to do: it is already watching Codex.
                return null;
            }

            Thread.Sleep(200);
        }
    }
}

internal static class SessionPathResolver
{
    public static string Resolve(IReadOnlyList<string>? arguments = null)
    {
        if (arguments is not null)
        {
            for (var index = 0; index < arguments.Count - 1; index++)
            {
                if (arguments[index].Equals("--sessions", StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(arguments[index + 1]))
                {
                    return Normalize(arguments[index + 1]);
                }
            }
        }

        var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (string.IsNullOrWhiteSpace(codexHome))
        {
            codexHome = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".codex");
        }

        return Path.Combine(Normalize(codexHome), "sessions");
    }

    public static string Normalize(string path)
    {
        var expanded = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"'));
        if (expanded.Equals("~", StringComparison.Ordinal)
            || expanded.StartsWith($"~{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            expanded = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                expanded.Length == 1 ? string.Empty : expanded[2..]);
        }

        return Path.GetFullPath(expanded);
    }
}

/// <summary>
/// One immutable view of the active conversation. Every numeric member is official Codex data;
/// nothing is estimated. Optional members default so existing probe JSON keeps deserialising.
/// </summary>
internal sealed record TokenSnapshot(
    string ThreadId,
    string LogPath,
    long TotalTokens,
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    long ReasoningOutputTokens,
    long ContextUsedTokens,
    long ContextWindowTokens,
    DateTime UpdatedAtUtc,
    string Title = "",
    string? TurnId = null,
    bool Streaming = false,
    long TurnGenerationMs = 0,
    long TurnOutputTokens = 0,
    long? FirstItemStartMs = null,
    long? LastItemEndMs = null,
    double? Tps = null,
    double? LastTurnTps = null,
    double? TtftMs = null,
    bool TpsHeld = false,
    string BaselineSource = "none",
    string Source = "none",
    long RolloutOffset = 0,
    IReadOnlyList<string>? Warnings = null,
    int CodexProcessId = 0,
    string CodexVersion = "unknown",
    string TpsState = "idle",
    long MergedModelElapsedMs = 0,
    IReadOnlyList<string>? ModelIntervals = null,
    string Carrier = "none",
    int DedupIgnored = 0, string Project = "", DisplayField KnownUsageFields = DisplayFieldRules.SupportedMask, long? LastTurnTokens = null)
{
    public bool Has(DisplayField fields) => (KnownUsageFields & fields) == fields;

    public long? UsedTokens => Has(DisplayField.Input | DisplayField.CacheHit | DisplayField.Output)
        && UncachedInputTokens <= long.MaxValue - OutputTokens ? UncachedInputTokens + OutputTokens : null;

    public double? CacheHitPercent => Has(DisplayField.Input | DisplayField.CacheHit)
        ? MetricFormat.CacheHitPercent(InputTokens, CachedInputTokens) : null;

    /// <summary>Cache-hit rate as a plain number for the layout code (0 when undefined).</summary>
    public double CacheHitPercentValue => CacheHitPercent ?? 0d;

    public int ContextPercent => 100 - ContextPercentRemaining(ContextUsedTokens, ContextWindowTokens);

    internal static int ContextPercentRemaining(long used, long window)
    {
        const long baseline = 12_000;
        if (window <= baseline) return 0;
        var effectiveWindow = window - baseline;
        var effectiveUsed = Math.Max(used, baseline) - baseline;
        var percent = Math.Max(0, effectiveWindow - effectiveUsed) / (double)effectiveWindow * 100;
        return (int)Math.Round(Math.Clamp(percent, 0, 100), MidpointRounding.AwayFromZero);
    }

    public long UncachedInputTokens => Math.Max(0, InputTokens - CachedInputTokens);

    public bool HasUsage => TotalTokens > 0 || InputTokens > 0 || OutputTokens > 0;
}

internal sealed record ActiveThreadRouteStatus(
    string? ThreadId,
    int ActiveWindowCount,
    bool IsConnected,
    long Version,
    string? LastError);

/// <summary>
/// Connects read-only to the Windows named pipe <c>\\.\pipe\codex-ipc</c> and follows
/// <c>thread-stream-following-changed</c> broadcasts to learn which conversation Codex Desktop has
/// open. Framing is a 4-byte little-endian length prefix followed by UTF-8 JSON.
/// </summary>
internal sealed class CodexIpcActiveThreadMonitor : IDisposable
{
    private const int MaximumWireFrameBytes = 256 * 1024 * 1024;
    private const int MaximumJsonFrameBytes = 4 * 1024 * 1024;
    private const string SourceClientId = "codex-statusbar";

    private readonly object _sync = new();
    private readonly Dictionary<string, string> _activeByWindow = new(StringComparer.Ordinal);
    private CancellationTokenSource? _cancellation;
    private Task? _runner;
    private string? _activeThreadId;
    private string? _lastError;
    private bool _isConnected;
    private long _version;

    /// <summary>
    /// Constructs the monitor without connecting. The pipe is only worth opening while Codex is
    /// running — an idle reconnect loop against a pipe that does not exist is exactly the background
    /// cost this project refuses to pay when Codex is closed.
    /// </summary>
    public CodexIpcActiveThreadMonitor()
    {
    }

    /// <summary>True while the reader task is running.</summary>
    public bool IsRunning
    {
        get
        {
            lock (_sync)
            {
                return _runner is not null;
            }
        }
    }

    /// <summary>Starts the reader if it is not already running. Idempotent.</summary>
    public void Start()
    {
        lock (_sync)
        {
            if (_runner is not null)
            {
                return;
            }

            _cancellation = new CancellationTokenSource();
            var token = _cancellation.Token;
            _runner = Task.Run(() => RunAsync(token));
        }
    }

    /// <summary>
    /// Stops the reader and clears every conversation it had resolved, so a later Codex start can
    /// never inherit a stale thread id or connection state.
    /// </summary>
    public void Stop()
    {
        CancellationTokenSource? cancellation;
        Task? runner;
        lock (_sync)
        {
            cancellation = _cancellation;
            runner = _runner;
            _cancellation = null;
            _runner = null;
        }

        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Already torn down.
        }

        try
        {
            runner?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Cancelling the background reader during a detach is normal.
        }

        cancellation?.Dispose();

        // Reset the observable state and drop the resolved conversation.
        lock (_sync)
        {
            _lastError = null;
        }

        MarkDisconnected(null);
    }

    /// <summary>
    /// Diagnostic tap: receives one line for every IPC lifecycle event and frame. Used by
    /// <c>--ipc-probe</c> to show exactly what Codex sends, which is what a future Codex update
    /// needs to be diagnosed against. Null in normal operation.
    /// </summary>
    public static Action<string>? FrameObserver { get; set; }

    public ActiveThreadRouteStatus GetStatus()
    {
        lock (_sync)
        {
            return new ActiveThreadRouteStatus(
                _activeThreadId,
                _activeByWindow.Count,
                _isConnected,
                _version,
                _lastError);
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var retryDelay = TimeSpan.FromMilliseconds(350);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var pipe = new NamedPipeClientStream(
                    ".",
                    "codex-ipc",
                    PipeDirection.InOut,
                    PipeOptions.Asynchronous);

                await pipe.ConnectAsync(2500, cancellationToken).ConfigureAwait(false);
                FrameObserver?.Invoke("connected to \\\\.\\pipe\\codex-ipc");
                MarkConnected();
                retryDelay = TimeSpan.FromMilliseconds(350);
                await SendInitializeAsync(pipe, cancellationToken).ConfigureAwait(false);
                FrameObserver?.Invoke("sent initialize");

                while (!cancellationToken.IsCancellationRequested && pipe.IsConnected)
                {
                    var prefix = new byte[sizeof(uint)];
                    if (!await ReadExactlyAsync(pipe, prefix, cancellationToken).ConfigureAwait(false))
                    {
                        break;
                    }

                    var frameLength = BinaryPrimitives.ReadUInt32LittleEndian(prefix);
                    if (frameLength == 0 || frameLength > MaximumWireFrameBytes)
                    {
                        throw new InvalidDataException($"Invalid Codex IPC frame length: {frameLength}");
                    }

                    if (frameLength > MaximumJsonFrameBytes)
                    {
                        await DrainExactlyAsync(pipe, frameLength, cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    var payload = new byte[(int)frameLength];
                    if (!await ReadExactlyAsync(pipe, payload, cancellationToken).ConfigureAwait(false))
                    {
                        break;
                    }

                    try
                    {
                        ProcessFrame(payload);
                    }
                    catch (Exception exception) when (exception is JsonException or InvalidOperationException)
                    {
                        // A single unknown or incomplete message must not kill the whole IPC listener.
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                or InvalidDataException or JsonException or InvalidOperationException or TimeoutException)
            {
                FrameObserver?.Invoke("error " + exception.GetType().Name + ": " + exception.Message);
                MarkDisconnected(exception.Message);
            }

            FrameObserver?.Invoke("disconnected");

            MarkDisconnected(null);
            try
            {
                await Task.Delay(retryDelay, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            retryDelay = TimeSpan.FromMilliseconds(Math.Min(5000, retryDelay.TotalMilliseconds * 2));
        }
    }

    private static async Task SendInitializeAsync(Stream pipe, CancellationToken cancellationToken)
    {
        var request = new
        {
            type = "request",
            requestId = Guid.NewGuid().ToString(),
            sourceClientId = SourceClientId,
            version = 0,
            method = "initialize",
            @params = new { clientType = SourceClientId }
        };
        var payload = JsonSerializer.SerializeToUtf8Bytes(request);
        var prefix = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(prefix, (uint)payload.Length);
        await pipe.WriteAsync(prefix.AsMemory(), cancellationToken).ConfigureAwait(false);
        await pipe.WriteAsync(payload.AsMemory(), cancellationToken).ConfigureAwait(false);
        await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private void ProcessFrame(byte[] payload)
    {
        FrameObserver?.Invoke("recv " + Encoding.UTF8.GetString(payload));
        using var document = JsonDocument.Parse(payload);
        var root = document.RootElement;
        if (!root.TryGetProperty("type", out var type)
            || type.ValueKind != JsonValueKind.String
            || type.GetString() != "broadcast"
            || !root.TryGetProperty("method", out var method)
            || method.ValueKind != JsonValueKind.String
            || !root.TryGetProperty("params", out var parameters)
            || parameters.ValueKind != JsonValueKind.Object)
        {
            // client-discovery-request and initialize responses are intentionally ignored.
            return;
        }

        var methodName = method.GetString();
        if (methodName == "client-status-changed")
        {
            ProcessClientStatusChanged(parameters);
            return;
        }

        if (methodName != "thread-stream-following-changed"
            || !parameters.TryGetProperty("conversationId", out var conversationIdElement)
            || !parameters.TryGetProperty("hostId", out var hostIdElement)
            || !parameters.TryGetProperty("following", out var followingElement)
            || conversationIdElement.ValueKind != JsonValueKind.String
            || hostIdElement.ValueKind != JsonValueKind.String
            || followingElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return;
        }

        var conversationId = conversationIdElement.GetString();
        var hostId = hostIdElement.GetString();
        var sourceClientId = root.TryGetProperty("sourceClientId", out var sourceElement)
            && sourceElement.ValueKind == JsonValueKind.String
            ? sourceElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(conversationId) || string.IsNullOrWhiteSpace(hostId) || string.IsNullOrWhiteSpace(sourceClientId))
        {
            return;
        }

        // A broadcast must identify the window.
        var key = $"{sourceClientId}\u001f{hostId}";
        lock (_sync)
        {
            if (followingElement.GetBoolean())
            {
                _activeByWindow[key] = conversationId;
            }
            else if (_activeByWindow.TryGetValue(key, out var active)
                && active.Equals(conversationId, StringComparison.OrdinalIgnoreCase))
            {
                _activeByWindow.Remove(key);
            }

            RecomputeActiveThread();
        }
    }

    private void ProcessClientStatusChanged(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("status", out var statusElement)
            || statusElement.ValueKind != JsonValueKind.String
            || statusElement.GetString() != "disconnected"
            || !parameters.TryGetProperty("clientId", out var clientIdElement)
            || clientIdElement.ValueKind != JsonValueKind.String)
        {
            return;
        }

        var clientId = clientIdElement.GetString();
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return;
        }

        var keyPrefix = $"{clientId}\u001f";
        lock (_sync)
        {
            foreach (var key in _activeByWindow.Keys
                .Where(key => key.StartsWith(keyPrefix, StringComparison.Ordinal))
                .ToArray())
            {
                _activeByWindow.Remove(key);
            }

            RecomputeActiveThread();
        }
    }

    private void MarkConnected()
    {
        lock (_sync)
        {
            _activeByWindow.Clear();
            _activeThreadId = null;
            _lastError = null;
            _isConnected = true;
            _version++;
        }
    }

    private void MarkDisconnected(string? error)
    {
        lock (_sync)
        {
            var changed = _isConnected || _activeByWindow.Count > 0 || _activeThreadId is not null;
            _isConnected = false;
            _activeByWindow.Clear();
            _activeThreadId = null;
            if (!string.IsNullOrWhiteSpace(error))
            {
                _lastError = error;
            }

            if (changed)
            {
                _version++;
            }
        }
    }

    private void RecomputeActiveThread()
    {
        var nextThreadId = _activeByWindow.Count == 1 ? _activeByWindow.Values.First() : null;
        if (!string.Equals(nextThreadId, _activeThreadId, StringComparison.OrdinalIgnoreCase))
        {
            _activeThreadId = nextThreadId;
            _version++;
        }
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                return false;
            }

            offset += read;
        }

        return true;
    }

    private static async Task DrainExactlyAsync(Stream stream, uint bytesToDrain, CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        var remaining = (long)bytesToDrain;
        while (remaining > 0)
        {
            var requested = (int)Math.Min(buffer.Length, remaining);
            var read = await stream.ReadAsync(buffer.AsMemory(0, requested), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("Codex IPC closed before a complete frame arrived.");
            }

            remaining -= read;
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Stop();
    }

    private int _disposed;
}

/// <summary>
/// Resolves the active conversation to a rollout file and keeps an incrementally parsed view of it.
///
/// A foreground document route, attributed IPC event or explicit pin supplies the identity.
/// The reader recovers cumulative usage from a bounded tail and then follows appends.
/// </summary>
internal sealed class TokenLogMonitor : IDisposable
{
    private string? _lastCandidate;
    private DateTime _retryAfter;

    private readonly string _sessionRoot;
    private readonly string _stateDatabasePath;
    private readonly FileSystemWatcher? _watcher;
    private readonly ConcurrentQueue<string> _changedPaths = new();

    private RolloutReader? _reader;
    private StateThreadRecord? _stateRecord;
    private string? _readerPath;
    private string? _selectedThreadId;
    private string _selectionSource = "none";
    private string _baselineSource = "none";
    private string? _cachedSignature;
    private TokenSnapshot? _lastSnapshot;


    // Resolved once: enumerating processes on every poll would be wasteful, and the client's PID
    // and package version do not change while it runs.
    private CodexDesktopInfo _codexInfo = CodexDesktopInfo.Unknown;
    private bool _codexInfoResolved;

    public TokenLogMonitor(string? sessionRoot = null, string? stateDatabasePath = null)
    {
        _sessionRoot = sessionRoot ?? SessionPathResolver.Resolve();
        _stateDatabasePath = stateDatabasePath ?? StateDatabase.ResolvePathForSessionsRoot(_sessionRoot);

        if (!Directory.Exists(_sessionRoot))
        {
            return;
        }

        _watcher = new FileSystemWatcher(_sessionRoot, "*.jsonl")
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName
                | NotifyFilters.CreationTime | NotifyFilters.Size,
            EnableRaisingEvents = true
        };
        _watcher.Changed += OnLogChanged;
        _watcher.Created += OnLogChanged;
        _watcher.Renamed += (_, eventArgs) => _changedPaths.Enqueue(eventArgs.FullPath);
    }

    public long ActiveSessionVersion { get; private set; }

    public string? ActiveThreadId => _selectedThreadId;

    public string? PreferredThreadId { get; set; }

    public bool PinActiveSession { get; set; }

    /// <summary>
    /// True when <c>--thread</c> pinned this monitor to one conversation. An explicit pin is trusted
    /// even for a session that is not a Codex Desktop root session (for example a <c>codex exec</c>
    /// rollout), which is what makes headless live tracing and troubleshooting possible.
    /// </summary>
    public bool IsForcedPin { get; set; }

    /// <summary>Bytes parsed from the current rollout file (for the debug log).</summary>
    public long RolloutOffset => _reader?.Offset ?? 0;

    /// <summary>Diagnostic when automatic selection has no confirmed route.</summary>
    public string MissingRouteReason { get; private set; } = "waiting-for-desktop-route";

    private string _routeSource = "ipc";
    private ChatBinding _chatBinding;

    public void ConfigureBinding(ChatBinding binding)
    {
        if (_chatBinding == binding) return;
        _chatBinding = binding;
        _manualFailure = null;
        IsForcedPin = binding.FromCommandLine;
        PinActiveSession = binding.Mode == ChatBindingMode.Manual;
        PreferredThreadId = PinActiveSession ? binding.ThreadId : null;
        _lastCandidate = null;
        SwitchActiveLog(null, null, "waiting-for-chat");
    }

    private void SelectManualChat(string? threadId)
    {
        if (_readerPath is { } existingPath && !File.Exists(existingPath))
        {
            SwitchActiveLog(null, null, "manual-session-unavailable");
            _lastCandidate = null;
        }
        if (threadId is null || !IsPlausibleThreadId(threadId))
        {
            _manualFailure = "invalid-manual-thread-id";
            if (_selectionSource != "manual-session-unavailable")
                SwitchActiveLog(null, null, "manual-session-unavailable");
            return;
        }
        if (_selectedThreadId is not null) return;
        if (_lastCandidate == threadId && DateTime.UtcNow < _retryAfter) return;
        _lastCandidate = threadId;
        _retryAfter = DateTime.UtcNow.AddSeconds(2);
        var record = StateDatabase.TryReadSelectableThread(_stateDatabasePath, threadId);
        var reason = ResolveIndexedRollout(record, threadId, true, out var path);
        if (reason is null)
        {
            _manualFailure = null;
            SwitchActiveLog(path, threadId, "manual", record);
        }
        else
        {
            _manualFailure = reason;
            if (_selectionSource != "manual-session-unavailable")
                SwitchActiveLog(null, null, "manual-session-unavailable");
        }
    }

    private string? _manualFailure;

    private string? ResolveIndexedRollout(StateThreadRecord? record, string id, bool allowArchive, out string? path)
    {
        path = null;
        if (record is null)
        {
            if (!File.Exists(_stateDatabasePath)) return "session-index-missing-or-inaccessible";
            return StateDatabase.TryReadThread(_stateDatabasePath, id) is null
                ? "thread-missing-or-index-unreadable" : "thread-excluded-by-metadata";
        }
        if (string.IsNullOrWhiteSpace(record.RolloutPath)) return "missing-indexed-rollout-path";
        try
        {
            path = NormalizeSessionPath(record.RolloutPath);
            var sessions = NormalizeSessionPath(_sessionRoot);
            if (path is null || sessions is null) return "invalid-rollout-path";
            var home = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(sessions));
            if (!IsBelow(path, sessions) && !(allowArchive && home is not null
                && IsBelow(path, Path.Combine(home, "archived_sessions"))))
                return "rollout-outside-session-root";
            if (!string.Equals(ExtractThreadId(path), id, StringComparison.OrdinalIgnoreCase)
                || !path.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
                return "rollout-filename-id-mismatch";
            return File.Exists(path) ? null : "rollout-missing-or-inaccessible";
        }
        catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException)
        {
            return "invalid-rollout-path";
        }
    }

    private static bool IsBelow(string path, string root) =>
        path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeSessionPath(string path)
    {
        // The Windows index can store the extended spelling of a DOS drive or UNC path.
        if (path.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            path = @"\\" + path[8..];
        else if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            path = path[4..];
            if (path.Length < 3 || !char.IsAsciiLetter(path[0]) || path[1] != ':' || path[2] != '\\')
                return null;
        }
        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
            return null;
        return Path.GetFullPath(path);
    }

    public void ObserveDesktopRoute(ActiveThreadRouteStatus route)
    {
        var documentId = DesktopDocumentRoute.Read(out var singleForegroundHost);
        ApplyRoute(route, documentId, singleForegroundHost);
    }

    public void ApplyRoute(ActiveThreadRouteStatus route, string? documentThreadId = null,
        bool singleForegroundHost = true)
    {
        _routeSource = documentThreadId is null ? "ipc" : "desktop-document";
        PreferredThreadId = documentThreadId ?? (singleForegroundHost ? route.ThreadId : null);
        MissingRouteReason = !route.IsConnected ? "desktop-pipe-unavailable"
            : !singleForegroundHost && route.ThreadId is not null ? "unverified-foreground-window"
            : route.ActiveWindowCount > 1 ? "ambiguous-desktop-windows"
            : "waiting-for-desktop-route";
    }

    public TokenSnapshot? Poll(bool forceFullScan = false)
    {
        var preferred = PreferredThreadId;
        var hasPreferred = !string.IsNullOrWhiteSpace(preferred);

        if (_chatBinding.Mode == ChatBindingMode.Manual && !_chatBinding.FromCommandLine)
        {
            SelectManualChat(preferred);
        }
        else if (IsForcedPin && hasPreferred && _readerPath is { } pinnedPath && !File.Exists(pinnedPath))
        {
            SwitchActiveLog(null, null, "manual-session-unavailable");
        }
        else if (IsForcedPin && hasPreferred && _selectedThreadId is null)
        {
            // --thread: pin immediately instead of waiting for a state-database round trip. Without
            // this the first poll would select an unrelated conversation from the database and the
            // pinned thread would never be read until something else changed.
            SelectPreferredRootSession(preferred!, "forced");
        }
        else if (!PinActiveSession && hasPreferred)
        {
            SelectPreferredRootSession(preferred!, _routeSource);
        }
        else if (PinActiveSession && _selectedThreadId is not null)
        {
            // Pinned to one conversation: never follow another.
        }
        else if (_selectedThreadId is not null || _selectionSource != MissingRouteReason)
        {
            _lastCandidate = null;
            SwitchActiveLog(null, null, MissingRouteReason);
        }

        DrainChangedPaths();
        EnsureReader();
        RefreshReader();

        return BuildSnapshot();
    }

    /// <summary>Reads only newly appended bytes; re-seeds from the tail when the file was replaced.</summary>
    private void RefreshReader()
    {
        if (_reader is null)
        {
            return;
        }

        try
        {
            if (_reader.TryUpdate() is RolloutUpdateResult.NeedsReinitialize)
            {
                _reader.Initialize();
                if (_reader.RecoveredFromRollout)
                {
                    _baselineSource = "rollout-tail";
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Codex is rotating the file; the next poll retries.
        }
    }

    private void EnsureReader()
    {
        if (_selectedThreadId is null || _reader is not null)
        {
            return;
        }

        var path = _readerPath ?? FindRolloutPath(_selectedThreadId, requireDesktopRoot: !IsForcedPin);
        if (path is null)
        {
            // Brand-new empty conversation: no rollout file yet. Show "-- tok/s", "-- tok", "Cache --"
            // and pick it up as soon as the file appears.
            return;
        }

        try
        {
            _reader = new RolloutReader(path);
            _reader.Initialize();
            if (_reader.TailWasExtended)
            {
                // The 4 MB fast path found no usage, so the window was widened. Visible in the debug
                // log because it means the bounded read was not enough for this session.
                _reader.State.Warnings.Add($"tail-window-extended:{_reader.TailWindowBytes / (1024 * 1024)}MB");
            }

            _readerPath = path;
            _baselineSource = _reader.RecoveredFromRollout ? "rollout-tail" : "none";
            if (!_reader.RecoveredFromRollout && _stateRecord is not null)
            {
                // Whatever the tail still holds is authoritative; state_5.sqlite only fills a void.
                _reader.State.ApplyStateDatabaseBaseline(_stateRecord.TokensUsed);
                if (_reader.State.HasCumulativeUsage)
                {
                    _baselineSource = "state-db";
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _reader = null;
            _readerPath = null;
            if (_chatBinding.Mode == ChatBindingMode.Manual && !IsForcedPin)
            {
                _manualFailure = exception is UnauthorizedAccessException ? "rollout-access-denied" : "rollout-read-error";
                SwitchActiveLog(null, null, "manual-session-unavailable");
            }
        }
    }

    private void OnLogChanged(object sender, FileSystemEventArgs eventArgs) => _changedPaths.Enqueue(eventArgs.FullPath);

    private void DrainChangedPaths()
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (_changedPaths.TryDequeue(out var path))
        {
            if (!visited.Add(path))
            {
                continue;
            }

            if (_reader is not null && path.Equals(_readerPath, StringComparison.OrdinalIgnoreCase))
            {
                RefreshReader();
            }

        }
    }

    private void SelectPreferredRootSession(string threadId, string source)
    {
        if (_selectedThreadId?.Equals(threadId, StringComparison.OrdinalIgnoreCase) == true)
        {
            _selectionSource = source;
            EnsureReader();
            return;
        }

        if (IsForcedPin)
        {
            var indexed = StateDatabase.TryReadSelectableThread(_stateDatabasePath, threadId);
            var pinnedPath = ResolveIndexedRollout(indexed, threadId, true, out var candidate) is null
                ? candidate : FindRolloutPath(threadId, requireDesktopRoot: false);
            SwitchActiveLog(pinnedPath, threadId, source, pinnedPath is not null && pinnedPath == candidate ? indexed : null);
            return;
        }

        if (_lastCandidate == threadId && DateTime.UtcNow < _retryAfter)
        {
            return;
        }
        _lastCandidate = threadId;
        _retryAfter = DateTime.UtcNow.AddSeconds(2);
        var record = StateDatabase.TryReadDesktopUserThread(_stateDatabasePath, threadId);
        if (ResolveIndexedRollout(record, threadId, false, out var path) is not null
            || path is null || !IsRootDesktopSession(path, threadId))
        {
            SwitchActiveLog(null, null, "unverified-desktop-session");
            return;
        }
        SwitchActiveLog(path, threadId, source, record);
    }

    /// <summary>Maps a thread id to its rollout file by searching ~/.codex/sessions recursively.</summary>
    /// <param name="requireDesktopRoot">
    /// When true (the normal case) only a root Codex Desktop session is accepted: sub-agent rollouts
    /// replay the parent's session, and CLI/VSCode sessions are not what the strip follows. An
    /// explicit <c>--thread</c> pin passes false, because an operator instruction to look at one
    /// specific conversation should be honoured whatever produced it.
    /// </param>
    private string? FindRolloutPath(string threadId, bool requireDesktopRoot = true)
    {
        if (!Directory.Exists(_sessionRoot) || !IsPlausibleThreadId(threadId))
        {
            return null;
        }

        try
        {
            return Directory.EnumerateFiles(_sessionRoot, $"*{threadId}.jsonl", SearchOption.AllDirectories)
                .Where(path => !requireDesktopRoot || IsRootDesktopSession(path, threadId))
                .OrderByDescending(SafeGetLastWriteUtc)
                .FirstOrDefault();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or ArgumentException)
        {
            return null;
        }
    }

    internal static bool IsPlausibleThreadId(string threadId) =>
        threadId.Length is >= 8 and <= 64
        && threadId.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_');

    private static bool IsRootDesktopSession(string path, string threadId)
    {
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(
                stream,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true,
                bufferSize: 64 * 1024);
            var firstLine = reader.ReadLine();
            if (string.IsNullOrWhiteSpace(firstLine))
            {
                // A Created event can land before Codex writes the first line; do not cache the miss.
                return false;
            }

            using var document = JsonDocument.Parse(firstLine);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type)
                || type.ValueKind != JsonValueKind.String || type.GetString() != "session_meta"
                || !root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            // Sub-agent rollouts replay the parent's session, so ONLY the first physical line is
            // authoritative. Desktop root sessions carry originator == "Codex Desktop"; their source
            // field is "vscode" for Desktop too, so nothing else may be required.
            if (!payload.TryGetProperty("originator", out var originator)
                || originator.ValueKind != JsonValueKind.String
                || !string.Equals(originator.GetString(), "Codex Desktop", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (payload.TryGetProperty("thread_source", out var kind)
                && kind.ValueKind != JsonValueKind.Null
                && (kind.ValueKind != JsonValueKind.String || kind.GetString() != "user"))
                return false;
            return payload.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String
                && string.Equals(id.GetString(), threadId, StringComparison.OrdinalIgnoreCase)
                && payload.TryGetProperty("source", out var source) && source.ValueKind == JsonValueKind.String
                && source.GetString() == "vscode";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
            or JsonException)
        {
            return false;
        }
    }

    private void SwitchActiveLog(string? path, string? threadId, string source, StateThreadRecord? record = null)
    {
        _selectedThreadId = threadId;
        _selectionSource = source;
        _stateRecord = record ?? (IsForcedPin && path is null ? null : TryReadStateRecord(threadId));
        _reader = null;
        _readerPath = null;
        _baselineSource = "none";
        _cachedSignature = null;
        _lastSnapshot = null;
        ActiveSessionVersion++;
        if (path is not null)
        {
            _readerPath = path;
            EnsureReader();
        }
    }

    private StateThreadRecord? TryReadStateRecord(string? threadId)
    {
        if (string.IsNullOrWhiteSpace(threadId))
        {
            return null;
        }

        try
        {
            return StateDatabase.TryReadThread(_stateDatabasePath, threadId);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private TokenSnapshot BuildSnapshot()
    {
        var state = _reader?.State;
        var threadId = _selectedThreadId ?? string.Empty;
        var tracker = state?.Tps;

        var baselineSource = _baselineSource;
        if (state is not null && state.HasCumulativeUsage && baselineSource == "none")
        {
            baselineSource = "rollout-tail";
        }

        var total = state?.TotalTokens ?? 0;
        var input = state?.InputTokens ?? 0;
        var cached = state?.CachedInputTokens ?? 0;
        var output = state?.OutputTokens ?? 0;
        if (state is null && _stateRecord is not null && _chatBinding.Mode != ChatBindingMode.Manual)
        {
            // No rollout file yet (or unreadable): state_5.sqlite's official thread total is the only
            // number available, and it is a cumulative (input + output) total.
            total = _stateRecord.TokensUsed;
            baselineSource = _stateRecord.TokensUsed > 0 ? "state-db" : baselineSource;
        }

        var tps = tracker?.DisplayTps;
        var lastTurnTps = tracker?.LastTurnTps;
        var warnings = _selectionSource == "manual-session-unavailable" && _manualFailure is not null
            ? new[] { _manualFailure } : state?.Warnings.ToArray();
        var path = _readerPath ?? string.Empty;

        // The unioned model-output windows, rendered as "[start,end] [start,end]".
        var intervals = tracker is null
            ? null
            : tracker.ModelIntervals
                .Take(12)
                .Select(interval => string.Create(
                    CultureInfo.InvariantCulture,
                    $"[{interval.Start},{interval.End}]"))
                .ToArray();

        var dedupIgnored = (state?.DuplicateUsageReadingsIgnored ?? 0)
            + (tracker?.DuplicateResponseSamplesIgnored ?? 0);

        // Re-detect while Codex is not running, so the debug log starts reporting a PID and version
        // as soon as it starts (and after a Codex restart).
        if (!_codexInfoResolved || _codexInfo.ProcessId == 0)
        {
            _codexInfo = CodexDesktopInfo.Detect();
            _codexInfoResolved = _codexInfo.ProcessId != 0;
        }

        var signature = string.Join(
            '\u001f',
            threadId,
            _selectionSource,
            path,
            baselineSource,
            state?.CurrentTurnId ?? string.Empty,
            state?.Streaming == true ? "1" : "0",
            total.ToString(CultureInfo.InvariantCulture),
            input.ToString(CultureInfo.InvariantCulture),
            cached.ToString(CultureInfo.InvariantCulture),
            output.ToString(CultureInfo.InvariantCulture),
            tracker?.TurnGenerationMs.ToString(CultureInfo.InvariantCulture) ?? "0",
            tracker?.TurnOutputTokens.ToString(CultureInfo.InvariantCulture) ?? "0",
            tracker?.TtftMs?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            tps is null ? string.Empty : tps.Value.ToString("0.000", CultureInfo.InvariantCulture),
            lastTurnTps is null ? string.Empty : lastTurnTps.Value.ToString("0.000", CultureInfo.InvariantCulture),
            tracker?.IsHeld == true ? "1" : "0",
            _reader?.Offset.ToString(CultureInfo.InvariantCulture) ?? "0",
            warnings is null ? string.Empty : string.Join(',', warnings),
            _codexInfo.ProcessId.ToString(CultureInfo.InvariantCulture),
            _codexInfo.Version,
            tracker?.StateDescription ?? "idle",
            tracker?.MergedModelElapsedMs.ToString(CultureInfo.InvariantCulture) ?? "0",
            state?.Carrier ?? "none",
            state?.KnownUsageFields.ToString(), state?.LastTurnTokens?.ToString(CultureInfo.InvariantCulture),
            state?.ContextUsedTokens.ToString(CultureInfo.InvariantCulture),
            state?.ContextWindowTokens.ToString(CultureInfo.InvariantCulture),
            state?.ReasoningOutputTokens.ToString(CultureInfo.InvariantCulture),
            dedupIgnored.ToString(CultureInfo.InvariantCulture));

        if (signature == _cachedSignature && _lastSnapshot is not null)
        {
            return _lastSnapshot;
        }

        _cachedSignature = signature;
        _lastSnapshot = new TokenSnapshot(
            threadId,
            path,
            total,
            input,
            cached,
            output,
            state?.ReasoningOutputTokens ?? 0,
            state?.ContextUsedTokens ?? 0,
            state?.ContextWindowTokens ?? 0,
            SafeGetLastWriteUtc(path),
            _stateRecord?.Title ?? string.Empty,
            state?.CurrentTurnId,
            state?.Streaming == true,
            tracker?.TurnGenerationMs ?? 0,
            tracker?.TurnOutputTokens ?? 0,
            tracker?.FirstItemStartMs,
            tracker?.LastItemEndMs,
            tps,
            lastTurnTps,
            tracker?.TtftMs,
            tracker?.IsHeld == true,
            baselineSource,
            _selectionSource,
            _reader?.Offset ?? 0,
            warnings,
            _codexInfo.ProcessId,
            _codexInfo.Version,
            tracker?.StateDescription ?? "idle",
            tracker?.MergedModelElapsedMs ?? 0,
            intervals,
            state?.Carrier ?? "none",
            dedupIgnored, _stateRecord?.Project ?? string.Empty,
            state?.KnownUsageFields ?? (total > 0 ? DisplayField.Total : DisplayField.None), state?.LastTurnTokens);
        return _lastSnapshot;
    }

    private static DateTime SafeGetLastWriteUtc(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return DateTime.MinValue;
        }

        try
        {
            return File.GetLastWriteTimeUtc(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return DateTime.MinValue;
        }
    }

    internal static string ExtractThreadId(string path)
    {
        var fileName = Path.GetFileNameWithoutExtension(path);
        return fileName.Length >= 36 ? fileName[^36..] : fileName;
    }

    public void Dispose() => _watcher?.Dispose();
}

/// <summary>
/// <c>--no-overlay</c>: runs the monitor with no window so the pipeline can be tested automatically.
/// With <c>--debug</c> it emits the diagnostics block on every state change.
/// </summary>
internal static class HeadlessRunner
{
    public static int Run(CommandLineOptions options)
    {
        using var routeMonitor = new CodexIpcActiveThreadMonitor();
        var binding = ChatBinding.Resolve(OverlaySettings.Load(options.SettingsPath), options.ForcedThreadId);
        if (binding.Mode == ChatBindingMode.Automatic)
            routeMonitor.Start();
        using var monitor = new TokenLogMonitor(options.SessionRoot);
        monitor.ConfigureBinding(binding);
        var diagnostics = new DebugDiagnostics(options.Debug);

        var deadline = options.DurationSeconds is int seconds
            ? DateTime.UtcNow.AddSeconds(seconds)
            : DateTime.MaxValue;

        TokenSnapshot? previous = null;
        var lastBlock = string.Empty;
        while (DateTime.UtcNow < deadline)
        {
            var snapshot = PollAndReport(routeMonitor, monitor, diagnostics, options, previous);
            var block = DebugDiagnostics.Build(snapshot);
            if (block != lastBlock)
            {
                lastBlock = block;
                previous = snapshot;
            }

            Thread.Sleep(500);
        }

        var final = PollAndReport(routeMonitor, monitor, diagnostics, options, previous);
        var finalBlock = DebugDiagnostics.Build(final);
        if (finalBlock != lastBlock)
        {
            diagnostics.Write(final);
        }

        Console.Out.WriteLine(finalBlock);
        Console.Out.Flush();
        return 0;
    }

    private static TokenSnapshot? PollAndReport(
        CodexIpcActiveThreadMonitor routeMonitor,
        TokenLogMonitor monitor,
        DebugDiagnostics diagnostics,
        CommandLineOptions options,
        TokenSnapshot? previous)
    {
        if (!monitor.PinActiveSession)
        {
            var route = routeMonitor.GetStatus();
            monitor.ObserveDesktopRoute(route);
        }

        var snapshot = monitor.Poll();
        if (!ReferenceEquals(snapshot, previous))
        {
            diagnostics.Write(snapshot);
        }

        return snapshot;
    }
}