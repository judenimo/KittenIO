using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using KittenIO.Mod.Configuration;
using KittenIO.Mod.Control;
using KittenIO.Mod.Diagnostics;
using KittenIO.Mod.Patching;
using KittenIO.Mod.Runtime;
using KittenIO.Protocol;

namespace KittenIO.Mod.Networking;

/// <summary>
/// Authenticated, loopback-only WebSocket host owned by the in-game mod. The
/// listener and peer tasks never access KSA objects; they exchange immutable
/// protocol messages with the game thread through bounded queues.
/// </summary>
internal sealed class KittenIOWebSocketHost : IAsyncDisposable
{
    private const int ListenBacklog = 8;
    private const int TelemetryStreamCapacity = 64;
    private const int RecentSessionCapacity = 64;
    private static readonly TimeSpan HttpUpgradeTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PeerTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(2);

    private readonly KittenIOConfig _config;
    private readonly BridgeDiagnostics _diagnostics;
    private readonly HelloPayload _hello;
    private readonly GameInstanceId _gameInstanceId;
    private readonly ControlCoordinator _control;
    private readonly LatestByKeyFairScheduler<TelemetryStream, OutboundMessage> _outbound;
    private readonly LatestInboundControlSlot _controls = new();
    private readonly Channel<MainThreadMessage> _commands;
    private readonly SemaphoreSlim _outboundSignal = new(0, 1);
    private readonly ConcurrentDictionary<string, long> _outboundSequences = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _networkGate = new();
    private readonly object _sessionGate = new();
    private readonly HashSet<Guid> _recentSessionIds = [];
    private readonly Queue<Guid> _recentSessionOrder = [];
    private Task? _runTask;
    private TcpListener? _listener;
    private ActivePeer? _activePeer;
    private Guid? _sessionId;
    private EphemeralControllerId? _controllerId;
    private long _lastReceivedMs;
    private int _ready;
    private int _negotiatedTelemetryHz;
    private int _listeningPort;
    private int _disposed;

    public KittenIOWebSocketHost(
        KittenIOConfig config,
        BridgeDiagnostics diagnostics,
        HelloPayload hello,
        ControlCoordinator control,
        GameInstanceId? gameInstanceId = null)
    {
        _config = config;
        _diagnostics = diagnostics;
        _hello = hello;
        _control = control;
        _gameInstanceId = gameInstanceId ?? KittenIO.Mod.Runtime.GameInstanceId.New();
        _negotiatedTelemetryHz = config.TelemetryHz;

        _outbound = new LatestByKeyFairScheduler<TelemetryStream, OutboundMessage>(
            latestCapacity: TelemetryStreamCapacity,
            reliableCapacity: ProtocolConstants.ReliableQueueCapacity);
        _commands = Channel.CreateBounded<MainThreadMessage>(new BoundedChannelOptions(ProtocolConstants.ReliableQueueCapacity)
        {
            // The game thread consumes commands while session cleanup may
            // drain them after the receive task has stopped.
            SingleReader = false,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.Wait,
            AllowSynchronousContinuations = false,
        });
    }

    public Guid GameInstanceId => _gameInstanceId.Value;

    public Guid? SessionId
    {
        get
        {
            lock (_sessionGate) return _sessionId;
        }
    }

    public bool IsReady => Volatile.Read(ref _ready) != 0;

    public int NegotiatedTelemetryHz => Volatile.Read(ref _negotiatedTelemetryHz);

    /// <summary>The actual bound port, or zero until the listener has started.</summary>
    public int ListeningPort => Volatile.Read(ref _listeningPort);

    internal bool HasClaimedPeer
    {
        get
        {
            lock (_networkGate) return _activePeer is not null;
        }
    }

    internal ListenerEndpointSnapshot? ListeningEndpoint
    {
        get
        {
            lock (_networkGate)
            {
                return _listener is null ? null : ReadListenerEndpoint(_listener);
            }
        }
    }

    public event Action<ProtocolPeerContext?, string>? Disconnected;

    public event Action<ProtocolPeerContext>? SessionStarted;

    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        lock (_networkGate)
        {
            if (_runTask is not null) return;
            _runTask = Task.Run(() => RunListenerAsync(_lifetime.Token));
            _ = _runTask.ContinueWith(
                static (task, state) =>
                {
                    var diagnostics = (BridgeDiagnostics)state!;
                    diagnostics.ReportError("WebSocket listener task faulted", task.Exception);
                },
                _diagnostics,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    public bool TryReadLatestControl(out InboundControl? inbound) => _controls.TryRead(out inbound);

    public bool TryReadCommand(out MainThreadMessage? command) => _commands.Reader.TryRead(out command);

    public bool IsCurrentContext(CommandContext context)
        => IsCurrentRoute(OutboundRoute.From(context));

    private bool IsCurrentRoute(OutboundRoute route)
    {
        var (sessionId, controllerId) = GetProtocolContext();
        return route.GameInstance == _gameInstanceId &&
            sessionId == route.Session.Value &&
            controllerId == route.Controller;
    }

    private OutboundRoute? GetCurrentRoute()
    {
        var (sessionId, controllerId) = GetProtocolContext();
        return sessionId is { } session && controllerId is { } controller
            ? new(_gameInstanceId, controller, new ProtocolSessionId(session))
            : null;
    }

    public void PublishTelemetry(FlightStatePayload payload)
    {
        var (sessionId, controllerId) = GetProtocolContext();
        if (!IsReady || sessionId is null || controllerId is null) return;
        var session = new ProtocolSessionId(sessionId.Value);
        var route = new OutboundRoute(_gameInstanceId, controllerId.Value, session);
        var stream = TelemetryStream.ActiveFlight(_gameInstanceId, controllerId.Value, session);
        if (!_outbound.TryEnqueueLatest(
                stream,
                new OutboundMessage(ProtocolConstants.MessageTypes.Telemetry, payload, null, route)))
        {
            _diagnostics.ReportError("Telemetry stream scheduler reached its bounded key capacity; sample omitted.");
            return;
        }
        SignalOutbound();
    }

    public void PublishCatalog(CatalogPayload payload)
    {
        if (!IsReady) return;
        QueueReliableCurrent(ProtocolConstants.MessageTypes.Catalog, payload, null);
    }

    public bool SendLeaseGranted(LeaseGrantedPayload payload, CommandContext context) =>
        QueueReliable(ProtocolConstants.MessageTypes.LeaseGranted, payload, context.RequestId, context);

    public bool SendLeaseReleased(LeaseReleasePayload payload, CommandContext context) =>
        QueueReliable(ProtocolConstants.MessageTypes.LeaseRelease, payload, context.RequestId, context);

    public bool SendResponse(ResponsePayload payload, CommandContext context) =>
        QueueReliable(ProtocolConstants.MessageTypes.Response, payload, context.RequestId, context);

    public bool SendEvent(EventPayload payload) =>
        QueueReliableCurrent(ProtocolConstants.MessageTypes.Event, payload, null);

    public bool SendError(string code, string message, CommandContext context) =>
        QueueReliable(
            ProtocolConstants.MessageTypes.Error,
            new ErrorPayload { Code = code, Message = message },
            context.RequestId,
            context);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _lifetime.Cancel();
        Task? runTask;
        ActivePeer? peer;
        lock (_networkGate)
        {
            runTask = _runTask;
            peer = _activePeer;
            _listener?.Stop();
        }
        peer?.Socket.Abort();

        if (runTask is not null)
        {
            try
            {
                await runTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected during mod unload.
            }
        }

        _lifetime.Dispose();
        _outboundSignal.Dispose();
    }

    private async Task RunListenerAsync(CancellationToken cancellationToken)
    {
        var endpoint = ValidateEndpoint(_config.Endpoint);
        var listener = CreateLoopbackListener(endpoint.Port);
        var setupTasks = new List<Task>(ListenBacklog);
        listener.Server.ExclusiveAddressUse = true;
        lock (_networkGate) _listener = listener;

        try
        {
            listener.Start(ListenBacklog);
            var boundEndpoint = ReadListenerEndpoint(listener);
            var boundPort = boundEndpoint.Port;
            Volatile.Write(ref _listeningPort, boundPort);
            _diagnostics.SetConnection($"listening on {ProtocolConstants.DefaultHost}:{boundPort}");

            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (SocketException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                await ObserveCompletedSetupTasksAsync(setupTasks).ConfigureAwait(false);
                if (setupTasks.Count >= ListenBacklog)
                {
                    // The kernel backlog and the in-process HTTP-upgrade work
                    // have the same bound. Do not let an excess connector add
                    // memory, timeout, or task pressure while the existing
                    // bounded setup slots are occupied.
                    client.Dispose();
                    continue;
                }

                setupTasks.Add(HandleAcceptedClientAsync(
                    client,
                    endpoint.AbsolutePath,
                    cancellationToken));
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Expected during mod unload.
        }
        catch (Exception exception)
        {
            _diagnostics.ReportError("WebSocket listener stopped unexpectedly", exception);
        }
        finally
        {
            listener.Stop();
            // Every accepted connector remains owned by this listener until
            // its setup task has completed. Lifetime cancellation interrupts
            // pending reads, and observing all tasks prevents upgrade work
            // from escaping mod shutdown.
            foreach (var setupTask in setupTasks)
            {
                await AwaitQuietly(setupTask).ConfigureAwait(false);
            }
            setupTasks.Clear();

            ActivePeer? peer;
            lock (_networkGate)
            {
                if (ReferenceEquals(_listener, listener)) _listener = null;
                peer = _activePeer;
            }
            peer?.Socket.Abort();
            if (peer is not null)
            {
                await peer.Completion.Task.ConfigureAwait(false);
            }
            Volatile.Write(ref _listeningPort, 0);
            _diagnostics.SetConnection("stopped");
        }
    }

    private static async Task ObserveCompletedSetupTasksAsync(List<Task> setupTasks)
    {
        for (var index = setupTasks.Count - 1; index >= 0; index--)
        {
            var setupTask = setupTasks[index];
            if (!setupTask.IsCompleted) continue;
            setupTasks.RemoveAt(index);
            await AwaitQuietly(setupTask).ConfigureAwait(false);
        }
    }

    private async Task HandleAcceptedClientAsync(
        TcpClient client,
        string expectedPath,
        CancellationToken cancellationToken)
    {
        var claimed = false;
        ActivePeer? upgradedPeer = null;
        try
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            using var upgradeCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            upgradeCancellation.CancelAfter(HttpUpgradeTimeout);

            HttpUpgradeRequest request;
            try
            {
                request = await LoopbackWebSocketUpgrade.ReadRequestAsync(
                    stream,
                    upgradeCancellation.Token).ConfigureAwait(false);
            }
            catch (HttpUpgradeException exception)
            {
                await TryWriteHttpResponseAsync(stream, exception.Decision, cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                var timeout = WebSocketUpgradeDecision.Reject(
                    HttpStatusCode.RequestTimeout,
                    "Request Timeout",
                    "HTTP upgrade timed out\n");
                await TryWriteHttpResponseAsync(stream, timeout, cancellationToken).ConfigureAwait(false);
                return;
            }

            var decision = LoopbackWebSocketUpgrade.Evaluate(request, expectedPath, _config.PairingToken);
            await LoopbackWebSocketUpgrade.WriteResponseAsync(
                stream,
                decision,
                upgradeCancellation.Token).ConfigureAwait(false);
            if (!decision.IsAccepted) return;

            var socket = WebSocket.CreateFromStream(
                stream,
                isServer: true,
                subProtocol: null,
                keepAliveInterval: Timeout.InfiniteTimeSpan);
            var peer = new ActivePeer(client, socket, EphemeralControllerId.New());
            upgradedPeer = peer;

            if (cancellationToken.IsCancellationRequested)
            {
                await CloseOutputQuietlyAsync(
                    peer.Socket,
                    WebSocketCloseStatus.EndpointUnavailable,
                    "KittenIO host stopping",
                    CancellationToken.None).ConfigureAwait(false);
                return;
            }

            if (!TryClaim(peer))
            {
                await CloseOutputQuietlyAsync(
                    peer.Socket,
                    (WebSocketCloseStatus)1013,
                    "A Python peer is already connected",
                    upgradeCancellation.Token).ConfigureAwait(false);
                return;
            }

            claimed = true;
            _ = RunClaimedPeerAsync(peer, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Listener shutdown owns cleanup below.
        }
        catch (OperationCanceledException)
        {
            // A slow or stalled connector exhausted its bounded upgrade budget.
        }
        catch (IOException)
        {
            // A half-open or malformed local connection is not a mod fault.
        }
        catch (SocketException)
        {
            // The local connector disappeared during upgrade.
        }
        catch (Exception exception)
        {
            // Isolate one connector from the long-lived listener. This message
            // intentionally contains no headers or credentials.
            _diagnostics.ReportError("WebSocket connection setup failed", exception);
        }
        finally
        {
            if (!claimed)
            {
                if (upgradedPeer is not null) upgradedPeer.Dispose();
                else client.Dispose();
            }
        }
    }

    private bool TryClaim(ActivePeer peer)
    {
        lock (_networkGate)
        {
            if (_activePeer is not null || _lifetime.IsCancellationRequested) return false;
            _activePeer = peer;
            return true;
        }
    }

    private async Task RunClaimedPeerAsync(ActivePeer peer, CancellationToken cancellationToken)
    {
        var hadSession = false;
        try
        {
            DrainSessionState();
            SetProtocolContext(null, peer.ControllerId);
            _lastReceivedMs = MonotonicClock.NowMilliseconds;
            _diagnostics.SetConnection("Python client connected; handshaking");

            await RunPeerSessionAsync(peer, cancellationToken).ConfigureAwait(false);
            hadSession = SessionId is not null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            hadSession = SessionId is not null;
        }
        catch (Exception exception)
        {
            hadSession = SessionId is not null;
            _diagnostics.ReportError("WebSocket peer ended", exception);
        }
        finally
        {
            Volatile.Write(ref _ready, 0);
            var endedSession = SessionId;
            SetProtocolContext(null, null);
            DrainSessionState();
            try
            {
                using var closeCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await CloseOutputQuietlyAsync(
                    peer.Socket,
                    WebSocketCloseStatus.NormalClosure,
                    "KittenIO session ended",
                    closeCancellation.Token).ConfigureAwait(false);
                peer.Dispose();
            }
            catch (Exception exception)
            {
                _diagnostics.ReportError("WebSocket peer cleanup failed", exception);
            }
            finally
            {
                // Keep the peer claim until its exact disconnect cleanup has
                // run. Otherwise a freshly authenticated peer could become
                // active between clearing _activePeer and invoking the old
                // callback, allowing old-session cleanup to affect the new
                // v1 session. A connector arriving in this short interval is
                // rejected with retryable 1013 and can reconnect safely.
                var endedContext = endedSession is { } session
                    ? new ProtocolPeerContext(
                        _gameInstanceId,
                        peer.ControllerId,
                        new ProtocolSessionId(session))
                    : (ProtocolPeerContext?)null;
                InvokeDisconnected(
                    endedContext,
                    endedSession is null && !hadSession ? "connection failed" : "socket disconnected");
                lock (_networkGate)
                {
                    if (ReferenceEquals(_activePeer, peer)) _activePeer = null;
                }

                if (!cancellationToken.IsCancellationRequested)
                {
                    var port = ListeningPort;
                    _diagnostics.SetConnection(port == 0
                        ? "stopped"
                        : $"listening on {ProtocolConstants.DefaultHost}:{port}");
                }
                peer.Completion.TrySetResult();
            }
        }
    }

    private async Task RunPeerSessionAsync(ActivePeer peer, CancellationToken cancellationToken)
    {
        var handshake = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sessionActivated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await SendNowAsync(
            peer.Socket,
            ProtocolConstants.MessageTypes.Hello,
            _hello,
            null,
            null,
            cancellationToken).ConfigureAwait(false);

        using var connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var sendTask = SendLoopAsync(peer.Socket, connectionCancellation.Token);
        var receiveTask = ReceiveLoopAsync(
            peer.Socket,
            peer.ControllerId,
            handshake,
            sessionActivated,
            connectionCancellation.Token);
        var heartbeatTask = HeartbeatLoopAsync(connectionCancellation.Token);
        var handshakeTask = handshake.Task.WaitAsync(HandshakeTimeout, connectionCancellation.Token);
        try
        {
            var first = await Task.WhenAny(handshakeTask, sendTask, receiveTask, heartbeatTask).ConfigureAwait(false);
            if (!ReferenceEquals(first, handshakeTask))
            {
                await first.ConfigureAwait(false);
                throw new InvalidDataException("The Python peer disconnected before completing the welcome handshake.");
            }

            var negotiatedSession = await handshakeTask.ConfigureAwait(false);
            InvokeSessionStarted(new ProtocolPeerContext(
                _gameInstanceId,
                peer.ControllerId,
                new ProtocolSessionId(negotiatedSession)));
            Volatile.Write(ref _ready, 1);
            _diagnostics.SetConnection("ready");
            sessionActivated.TrySetResult();

            var completed = await Task.WhenAny(sendTask, receiveTask, heartbeatTask).ConfigureAwait(false);
            await completed.ConfigureAwait(false);
        }
        finally
        {
            sessionActivated.TrySetCanceled(cancellationToken);
            connectionCancellation.Cancel();
            await AwaitQuietly(handshakeTask).ConfigureAwait(false);
            await AwaitQuietly(sendTask).ConfigureAwait(false);
            await AwaitQuietly(receiveTask).ConfigureAwait(false);
            await AwaitQuietly(heartbeatTask).ConfigureAwait(false);
        }
    }

    private async Task ReceiveLoopAsync(
        WebSocket socket,
        EphemeralControllerId controllerId,
        TaskCompletionSource<Guid> handshake,
        TaskCompletionSource sessionActivated,
        CancellationToken cancellationToken)
    {
        var inboundSequences = new InboundSequenceTracker();
        while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            var bytes = await ReceiveOneAsync(socket, cancellationToken).ConfigureAwait(false);
            if (bytes is null) return;

            var now = MonotonicClock.NowMilliseconds;
            Volatile.Write(ref _lastReceivedMs, now);
            _diagnostics.MarkReceived(now);

            if (!WireCodec.TryDeserialize(bytes, out var envelope, out var decodeError) || envelope is null)
            {
                await CloseForPolicyViolationAsync(
                    socket,
                    decodeError?.Code ?? "invalid_envelope",
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!InboundProtocolPolicy.IsAllowed(envelope.Type))
            {
                await CloseForPolicyViolationAsync(socket, "invalid_direction", cancellationToken).ConfigureAwait(false);
                return;
            }

            if (envelope.GameInstanceId != _gameInstanceId.Value)
            {
                await CloseForPolicyViolationAsync(socket, "wrong_game_instance", cancellationToken).ConfigureAwait(false);
                return;
            }

            var activeSession = SessionId;
            if (handshake.Task.IsCompleted && envelope.SessionId != activeSession)
            {
                await CloseForPolicyViolationAsync(socket, "stale_session", cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!inboundSequences.TryAdvance(envelope.Type, envelope.Sequence, out var sequenceError))
            {
                await CloseForPolicyViolationAsync(
                    socket,
                    sequenceError?.Code ?? "stale_sequence",
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            if (!handshake.Task.IsCompleted && envelope.Type != ProtocolConstants.MessageTypes.Welcome)
            {
                await CloseForPolicyViolationAsync(socket, "handshake_required", cancellationToken).ConfigureAwait(false);
                return;
            }

            if (handshake.Task.IsCompleted && envelope.Type == ProtocolConstants.MessageTypes.Welcome)
            {
                await CloseForPolicyViolationAsync(socket, "duplicate_welcome", cancellationToken).ConfigureAwait(false);
                return;
            }

            switch (envelope.Type)
            {
                case ProtocolConstants.MessageTypes.Welcome:
                    if (!HandleWelcome(envelope, handshake))
                    {
                        await CloseForPolicyViolationAsync(socket, "invalid_welcome", cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    // The session ID is selected by welcome, but no later
                    // message may be admitted until the mod's exact
                    // session-reset callback has completed and readiness is
                    // published.
                    await sessionActivated.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
                    break;
                case ProtocolConstants.MessageTypes.Control:
                    if (TryPayload(envelope, out ControlFramePayload? control) && control is not null)
                    {
                        if (!WireCodec.TryValidateControl(control, out _))
                        {
                            await CloseForPolicyViolationAsync(socket, "invalid_control", cancellationToken).ConfigureAwait(false);
                            return;
                        }
                        var context = CreateCommandContext(envelope, controllerId, activeSession!.Value, now);
                        if (!_control.TryValidateInbound(context, control, now, out var controlError))
                        {
                            QueueProtocolError(
                                controlError ?? new("invalid_control", "Control frame was rejected."),
                                context);
                            break;
                        }

                        if (!_controls.TryWrite(new(context, NormalizedControlFrame.FromPayload(control), now), out controlError))
                        {
                            QueueProtocolError(
                                controlError ?? new("stale_control", "Control frame was superseded."),
                                context);
                        }
                    }
                    else
                    {
                        await CloseForPolicyViolationAsync(socket, "invalid_payload", cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    break;
                case ProtocolConstants.MessageTypes.LeaseAcquire:
                    if (TryPayload(envelope, out LeaseAcquirePayload? acquire) && acquire is not null)
                    {
                        var context = CreateCommandContext(envelope, controllerId, activeSession!.Value, now);
                        QueueMainThread(new AcquireLeaseMessage(context, acquire));
                    }
                    else
                    {
                        await CloseForPolicyViolationAsync(socket, "invalid_payload", cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    break;
                case ProtocolConstants.MessageTypes.LeaseRelease:
                    if (TryPayload(envelope, out LeaseReleasePayload? release) && release is not null)
                    {
                        var context = CreateCommandContext(envelope, controllerId, activeSession!.Value, now);
                        QueueMainThread(new ReleaseLeaseMessage(context, release));
                    }
                    else
                    {
                        await CloseForPolicyViolationAsync(socket, "invalid_payload", cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    break;
                case ProtocolConstants.MessageTypes.Request:
                    if (TryPayload(envelope, out RequestPayload? request) && request is not null)
                    {
                        var context = CreateCommandContext(envelope, controllerId, activeSession!.Value, now);
                        QueueMainThread(new GameRequestMessage(context, request));
                    }
                    else
                    {
                        await CloseForPolicyViolationAsync(socket, "invalid_payload", cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    break;
                case ProtocolConstants.MessageTypes.Ping:
                    if (TryPayload(envelope, out HeartbeatPayload? ping) && ping is not null)
                    {
                        var context = CreateCommandContext(envelope, controllerId, activeSession!.Value, now);
                        QueueReliable(ProtocolConstants.MessageTypes.Pong, ping, context.RequestId, context);
                    }
                    else
                    {
                        await CloseForPolicyViolationAsync(socket, "invalid_payload", cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    break;
                case ProtocolConstants.MessageTypes.Pong:
                    if (!TryPayload(envelope, out HeartbeatPayload? _))
                    {
                        await CloseForPolicyViolationAsync(socket, "invalid_payload", cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    break;
                case ProtocolConstants.MessageTypes.Error:
                    if (TryPayload(envelope, out ErrorPayload? peerError) && peerError is not null)
                    {
                        _diagnostics.ReportError($"Python peer error {peerError.Code}: {peerError.Message}");
                    }
                    else
                    {
                        await CloseForPolicyViolationAsync(socket, "invalid_payload", cancellationToken).ConfigureAwait(false);
                        return;
                    }
                    break;
                default:
                    await CloseForPolicyViolationAsync(socket, "invalid_direction", cancellationToken).ConfigureAwait(false);
                    return;
            }
        }
    }

    private bool HandleWelcome(WireEnvelope envelope, TaskCompletionSource<Guid> handshake)
    {
        if (handshake.Task.IsCompleted) return false;
        if (!TryPayload(envelope, out WelcomePayload? welcome) || welcome is null) return false;
        if (welcome.SelectedVersion != ProtocolConstants.Version ||
            envelope.Version != ProtocolConstants.Version ||
            welcome.SessionId == Guid.Empty ||
            envelope.SessionId != welcome.SessionId)
        {
            return false;
        }

        if (!TrySetFreshSession(welcome.SessionId))
        {
            return false;
        }
        _outboundSequences.Clear();
        Volatile.Write(ref _negotiatedTelemetryHz, Math.Clamp(welcome.TelemetryHz, 1, 60));
        handshake.TrySetResult(welcome.SessionId);
        return true;
    }

    private async Task SendLoopAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
        {
            await _outboundSignal.WaitAsync(cancellationToken).ConfigureAwait(false);
            while (_outbound.TryDequeue(out var outbound, out _))
            {
                if (outbound is null || !IsCurrentRoute(outbound.Route)) continue;
                await SendNowAsync(
                    socket,
                    outbound.Type,
                    outbound.Payload,
                    outbound.RequestId,
                    outbound.Route.Session.Value,
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(HeartbeatInterval);
        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            var age = MonotonicClock.NowMilliseconds - Volatile.Read(ref _lastReceivedMs);
            if (age > PeerTimeout.TotalMilliseconds)
            {
                throw new TimeoutException("The Python peer stopped responding to WebSocket traffic.");
            }

            QueueReliable(
                ProtocolConstants.MessageTypes.Ping,
                new HeartbeatPayload { Nonce = Guid.NewGuid().ToString("N") },
                null,
                GetCurrentRoute());
        }
    }

    private async Task SendNowAsync(
        WebSocket socket,
        string type,
        object payload,
        string? requestId,
        Guid? sessionId,
        CancellationToken cancellationToken)
    {
        var sequence = _outboundSequences.AddOrUpdate(type, 0, static (_, prior) => checked(prior + 1));
        var bytes = WireCodec.Serialize(
            type,
            _gameInstanceId.Value,
            sessionId,
            sequence,
            MonotonicClock.NowMilliseconds,
            payload,
            requestId);
        await socket.SendAsync(bytes.AsMemory(), WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
    }

    private bool TryPayload<T>(WireEnvelope envelope, out T? payload)
    {
        if (WireCodec.TryReadPayload(envelope, out payload, out var error)) return true;
        _ = error;
        return false;
    }

    private bool QueueReliableCurrent(string type, object payload, string? requestId)
    {
        var route = GetCurrentRoute();
        return route is { } current && QueueReliable(type, payload, requestId, current);
    }

    private bool QueueReliable(
        string type,
        object payload,
        string? requestId,
        CommandContext context) =>
        QueueReliable(type, payload, requestId, OutboundRoute.From(context));

    private bool QueueReliable(
        string type,
        object payload,
        string? requestId,
        OutboundRoute? expectedRoute)
    {
        if (!IsReady && type is not ProtocolConstants.MessageTypes.Error and not ProtocolConstants.MessageTypes.Pong)
        {
            return false;
        }

        if (expectedRoute is not { } route || !IsCurrentRoute(route)) return false;

        if (!_outbound.TryEnqueueReliable(new(type, payload, requestId, route)))
        {
            _diagnostics.ReportError(
                "Reliable outbound queue reached its bounded capacity; closing the session instead of dropping a message.");
            ActivePeer? peer;
            lock (_networkGate) peer = _activePeer;
            peer?.Socket.Abort();
            return false;
        }
        SignalOutbound();
        return true;
    }

    private void QueueMainThread(MainThreadMessage command)
    {
        if (_commands.Writer.TryWrite(command)) return;
        QueueProtocolError(
            new("busy", "The game command queue is full; retry this acknowledged request."),
            command.Context);
    }

    private void QueueProtocolError(ProtocolError error, CommandContext context) =>
        QueueReliable(
            ProtocolConstants.MessageTypes.Error,
            new ErrorPayload { Code = error.Code, Message = error.Message },
            context.RequestId,
            context);

    private void SignalOutbound()
    {
        try
        {
            _outboundSignal.Release();
        }
        catch (SemaphoreFullException)
        {
            // The sender is already awake.
        }
    }

    private void DrainSessionState()
    {
        _controls.Clear();
        while (_commands.Reader.TryRead(out _)) { }
        _outbound.Clear();
        _outboundSequences.Clear();
    }

    private void SetSession(Guid? sessionId)
    {
        lock (_sessionGate) _sessionId = sessionId;
    }

    private bool TrySetFreshSession(Guid sessionId)
    {
        lock (_sessionGate)
        {
            if (_recentSessionIds.Contains(sessionId)) return false;
            _recentSessionIds.Add(sessionId);
            _recentSessionOrder.Enqueue(sessionId);
            while (_recentSessionOrder.Count > RecentSessionCapacity)
            {
                _recentSessionIds.Remove(_recentSessionOrder.Dequeue());
            }
            _sessionId = sessionId;
            return true;
        }
    }

    private void SetProtocolContext(Guid? sessionId, EphemeralControllerId? controllerId)
    {
        lock (_sessionGate)
        {
            _sessionId = sessionId;
            _controllerId = controllerId;
        }
    }

    private (Guid? SessionId, EphemeralControllerId? ControllerId) GetProtocolContext()
    {
        lock (_sessionGate) return (_sessionId, _controllerId);
    }

    private CommandContext CreateCommandContext(
        WireEnvelope envelope,
        EphemeralControllerId controllerId,
        Guid sessionId,
        long receivedMonotonicMs) => new(
        _gameInstanceId,
        controllerId,
        new ProtocolSessionId(sessionId),
        envelope.Type,
        envelope.Sequence,
        receivedMonotonicMs,
        envelope.RequestId)
        {
            ProtocolVersion = envelope.Version,
        };

    private void InvokeSessionStarted(ProtocolPeerContext context)
    {
        try
        {
            SessionStarted?.Invoke(context);
        }
        catch (Exception exception)
        {
            _diagnostics.ReportError("Session-start callback failed", exception);
        }
    }

    private void InvokeDisconnected(ProtocolPeerContext? context, string reason)
    {
        try
        {
            Disconnected?.Invoke(context, reason);
        }
        catch (Exception exception)
        {
            _diagnostics.ReportError("Disconnect callback failed", exception);
        }
    }

    private static Uri ValidateEndpoint(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeWs ||
            !string.Equals(uri.Host, ProtocolConstants.DefaultHost, StringComparison.Ordinal) ||
            !string.Equals(uri.AbsolutePath, ProtocolConstants.Path, StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment) ||
            uri.Port is < 0 or > ushort.MaxValue)
        {
            throw new InvalidDataException(
                $"endpoint must be ws://{ProtocolConstants.DefaultHost}:<port>{ProtocolConstants.Path} without credentials, query, or fragment.");
        }
        return uri;
    }

    private static TcpListener CreateLoopbackListener(int port)
    {
        // StarMap isolates game and mod assemblies in separate load contexts.
        // On the pinned KSA runtime a direct TcpListener(IPAddress, int) call
        // can bind the TcpListener token to the game context while the
        // IPAddress token comes from the loader context, which fails during
        // JIT resolution with MissingMethodException even though the runtime
        // constructor is present. Resolve the one exact framework signature
        // from TcpListener's own metadata so both parameter and declaring
        // types come from the same context. This runs once per mod load.
        var constructor = typeof(TcpListener)
            .GetConstructors(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .SingleOrDefault(static candidate =>
            {
                var parameters = candidate.GetParameters();
                return parameters.Length == 2 &&
                    parameters[0].ParameterType.FullName == "System.Net.IPAddress" &&
                    parameters[1].ParameterType.FullName == "System.Int32";
            }) ?? throw new MissingMethodException(
                "The runtime does not expose TcpListener(System.Net.IPAddress, System.Int32).");

        var addressType = constructor.GetParameters()[0].ParameterType;
        var loopback = addressType
            .GetField("Loopback", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            ?.GetValue(null) ?? throw new MissingMemberException(
                "The runtime System.Net.IPAddress type does not expose Loopback.");

        try
        {
            return (TcpListener)constructor.Invoke([loopback, port]);
        }
        catch (System.Reflection.TargetInvocationException exception) when (exception.InnerException is not null)
        {
            throw new InvalidOperationException(
                "The runtime rejected creation of the loopback TCP listener.",
                exception.InnerException);
        }
    }

    private static ListenerEndpointSnapshot ReadListenerEndpoint(TcpListener listener)
    {
        // LocalEndpoint's return type lives in System.Net.Primitives and can
        // have a different load-context identity from the mod's compile-time
        // EndPoint token. Read it through TcpListener's own metadata for the
        // same reason CreateLoopbackListener resolves its constructor there.
        var localEndpoint = typeof(TcpListener)
            .GetProperty(
                "LocalEndpoint",
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            ?.GetValue(listener) ?? throw new MissingMemberException(
                "The runtime TcpListener type does not expose LocalEndpoint.");
        var endpointType = localEndpoint.GetType();
        var address = endpointType
            .GetProperty("Address", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            ?.GetValue(localEndpoint)
            ?.ToString() ?? throw new MissingMemberException(
                "The runtime listener endpoint does not expose Address.");
        var portValue = endpointType
            .GetProperty("Port", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            ?.GetValue(localEndpoint);
        if (portValue is not int port)
        {
            throw new MissingMemberException("The runtime listener endpoint does not expose an integer Port.");
        }

        return new(address, port);
    }

    private static async ValueTask TryWriteHttpResponseAsync(
        Stream stream,
        WebSocketUpgradeDecision decision,
        CancellationToken cancellationToken)
    {
        try
        {
            await LoopbackWebSocketUpgrade.WriteResponseAsync(stream, decision, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or SocketException or OperationCanceledException)
        {
            // The rejected connector may have already gone away.
        }
    }

    private static async Task CloseForPolicyViolationAsync(
        WebSocket socket,
        string code,
        CancellationToken cancellationToken)
    {
        await CloseOutputQuietlyAsync(
            socket,
            WebSocketCloseStatus.PolicyViolation,
            $"KittenIO protocol policy: {code}",
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task CloseOutputQuietlyAsync(
        WebSocket socket,
        WebSocketCloseStatus closeStatus,
        string reason,
        CancellationToken cancellationToken)
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) return;
        try
        {
            await socket.CloseOutputAsync(closeStatus, reason, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            socket.Abort();
        }
    }

    private static async Task<byte[]?> ReceiveOneAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var rented = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            using var message = new MemoryStream();
            while (true)
            {
                var result = await socket.ReceiveAsync(rented.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) return null;
                if (result.MessageType != WebSocketMessageType.Text)
                {
                    await CloseOutputQuietlyAsync(
                        socket,
                        WebSocketCloseStatus.InvalidMessageType,
                        "KittenIO accepts JSON text messages only.",
                        cancellationToken).ConfigureAwait(false);
                    return null;
                }

                if (message.Length + result.Count > ProtocolConstants.MaximumMessageBytes)
                {
                    await CloseOutputQuietlyAsync(
                        socket,
                        WebSocketCloseStatus.MessageTooBig,
                        "KittenIO message exceeds 256 KiB.",
                        cancellationToken).ConfigureAwait(false);
                    return null;
                }

                message.Write(rented, 0, result.Count);
                if (result.EndOfMessage) return message.ToArray();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static async Task AwaitQuietly(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Expected when another connection task completes.
        }
        catch (Exception)
        {
            // The primary task is observed by RunPeerSessionAsync.
        }
    }

    private readonly record struct OutboundRoute(
        GameInstanceId GameInstance,
        EphemeralControllerId Controller,
        ProtocolSessionId Session)
    {
        public static OutboundRoute From(CommandContext context) => new(
            context.GameInstance,
            context.Controller,
            context.Session);
    }

    internal readonly record struct ListenerEndpointSnapshot(string Address, int Port);

    private sealed record OutboundMessage(
        string Type,
        object Payload,
        string? RequestId,
        OutboundRoute Route);

    private sealed class ActivePeer(
        TcpClient client,
        WebSocket socket,
        EphemeralControllerId controllerId) : IDisposable
    {
        public WebSocket Socket { get; } = socket;

        public EphemeralControllerId ControllerId { get; } = controllerId;

        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Dispose()
        {
            Socket.Dispose();
            client.Dispose();
        }
    }
}
