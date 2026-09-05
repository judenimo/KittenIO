using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using KittenIO.Mod.Configuration;
using KittenIO.Mod.Control;
using KittenIO.Mod.Diagnostics;
using KittenIO.Mod.Networking;
using KittenIO.Protocol;

internal static class TransportHostTests
{
    private const string Token = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFG";

    public static void RunAll() => RunAllAsync().GetAwaiter().GetResult();

    private static async Task RunAllAsync()
    {
        TestUpgradePolicy();
        await TestBoundedUpgradeReaderAsync().ConfigureAwait(false);
        await TestStalledUpgradeDoesNotBlockAuthenticatedPeerAsync().ConfigureAwait(false);
        await TestCanonicalVersionBindingAsync().ConfigureAwait(false);
        await TestHostHandshakeBusyAndReconnectAsync().ConfigureAwait(false);
    }

    private static async Task TestCanonicalVersionBindingAsync()
    {
        var diagnostics = new BridgeDiagnostics();
        var config = new KittenIOConfig
        {
            Endpoint = $"ws://{ProtocolConstants.DefaultHost}:0{ProtocolConstants.Path}",
            PairingToken = Token,
        };
        await using var host = new KittenIOWebSocketHost(
            config,
            diagnostics,
            new HelloPayload
            {
                ModVersion = "test",
                GameVersion = "test",
                StarMapVersion = "test",
                Capabilities = CanonicalCapabilities(),
            },
            new ControlCoordinator(750, diagnostics));
        host.Start();
        await WaitUntilAsync(() => host.ListeningPort != 0, "canonical host did not bind").ConfigureAwait(false);
        using var socket = await ConnectAsync(host.ListeningPort).ConfigureAwait(false);
        var hello = await ReceiveEnvelopeAsync(socket).ConfigureAwait(false);
        Assert(hello.Version == ProtocolConstants.Version, "hello must use canonical protocol version 1");
        Assert(WireCodec.TryReadPayload<HelloPayload>(hello, out var helloPayload, out _) &&
               helloPayload!.SupportedVersions.SequenceEqual([1]),
            "hello must advertise only protocol version 1");

        var session = Guid.NewGuid();
        await SendWelcomeAsync(socket, host.GameInstanceId, session).ConfigureAwait(false);
        await WaitUntilAsync(() => host.IsReady, "welcome did not establish a session").ConfigureAwait(false);
        await SendLeaseAcquireAsync(socket, host.GameInstanceId, session, "canonical-route")
            .ConfigureAwait(false);
        MainThreadMessage? command = null;
        await WaitUntilAsync(
            () => command is not null || host.TryReadCommand(out command),
            "canonical command was not admitted").ConfigureAwait(false);
        Assert(command!.Context.ProtocolVersion == ProtocolConstants.Version,
            "the main-thread command lost its canonical protocol identity");

        var canonicalPing = WireCodec.Serialize(
            ProtocolConstants.MessageTypes.Ping,
            host.GameInstanceId,
            session,
            0,
            1,
            new HeartbeatPayload { Nonce = "wrong-version" });
        var wrongVersion = Encoding.UTF8.GetBytes(
            Encoding.UTF8.GetString(canonicalPing).Replace("\"version\":1", "\"version\":2", StringComparison.Ordinal));
        await socket.SendAsync(wrongVersion.AsMemory(), WebSocketMessageType.Text, true, CancellationToken.None)
            .ConfigureAwait(false);
        Assert(await ReceiveCloseStatusAsync(socket).ConfigureAwait(false) == 1008,
            "post-handshake traffic from another protocol version must close the session");
    }

    private static void TestUpgradePolicy()
    {
        var valid = ValidHeaders();
        var accepted = LoopbackWebSocketUpgrade.Evaluate(
            new HttpUpgradeRequest(ProtocolConstants.Path, valid),
            ProtocolConstants.Path,
            Token);
        Assert(accepted.IsAccepted, "a valid authenticated upgrade should be accepted");
        Assert(
            accepted.AcceptKey == "s3pPLMBiTxaQ9kYGzzhZRbK+xOo=",
            "the RFC 6455 accept key changed");

        var wrongToken = ValidHeaders();
        wrongToken["Authorization"] = "Bearer wrong";
        Assert(
            LoopbackWebSocketUpgrade.Evaluate(
                new HttpUpgradeRequest(ProtocolConstants.Path, wrongToken),
                ProtocolConstants.Path,
                Token).StatusCode == 401,
            "wrong authentication should return HTTP 401");

        var wrongProtocol = ValidHeaders();
        wrongProtocol["X-KittenIO-Protocol"] = "2";
        Assert(
            LoopbackWebSocketUpgrade.Evaluate(
                new HttpUpgradeRequest(ProtocolConstants.Path, wrongProtocol),
                ProtocolConstants.Path,
                Token).StatusCode == 426,
            "wrong KittenIO protocol should return HTTP 426");

        var browser = ValidHeaders();
        browser["Origin"] = "https://example.invalid";
        Assert(
            LoopbackWebSocketUpgrade.Evaluate(
                new HttpUpgradeRequest(ProtocolConstants.Path, browser),
                ProtocolConstants.Path,
                Token).StatusCode == 403,
            "browser origins should return HTTP 403");
    }

    private static async Task TestBoundedUpgradeReaderAsync()
    {
        var duplicate = Encoding.ASCII.GetBytes(
            "GET /kittenio/v1 HTTP/1.1\r\nHost: 127.0.0.1\r\nHost: duplicate\r\n\r\n");
        await ThrowsAsync<HttpUpgradeException>(
            async () => _ = await LoopbackWebSocketUpgrade.ReadRequestAsync(
                new MemoryStream(duplicate),
                CancellationToken.None).ConfigureAwait(false),
            "duplicate HTTP properties should be rejected").ConfigureAwait(false);

        var oversized = Encoding.ASCII.GetBytes(
            "GET /kittenio/v1 HTTP/1.1\r\nX-Fill: " +
            new string('a', LoopbackWebSocketUpgrade.MaximumHeaderBytes) +
            "\r\n\r\n");
        var exception = await CaptureAsync<HttpUpgradeException>(
            async () => _ = await LoopbackWebSocketUpgrade.ReadRequestAsync(
                new MemoryStream(oversized),
                CancellationToken.None).ConfigureAwait(false)).ConfigureAwait(false);
        Assert(exception.Decision.StatusCode == 431, "oversized HTTP headers should return HTTP 431");
    }

    private static async Task TestStalledUpgradeDoesNotBlockAuthenticatedPeerAsync()
    {
        var diagnostics = new BridgeDiagnostics();
        var hello = new HelloPayload
        {
            ModVersion = "test",
            GameVersion = "test",
            StarMapVersion = "test",
            Capabilities = CanonicalCapabilities(),
        };
        var config = new KittenIOConfig
        {
            Endpoint = $"ws://{ProtocolConstants.DefaultHost}:0{ProtocolConstants.Path}",
            PairingToken = Token,
        };
        var host = new KittenIOWebSocketHost(
            config,
            diagnostics,
            hello,
            new ControlCoordinator(750, diagnostics));
        using var stalled = new TcpClient();
        ClientWebSocket? authenticated = null;
        try
        {
            host.Start();
            await WaitUntilAsync(() => host.ListeningPort != 0, "concurrent-upgrade host did not bind")
                .ConfigureAwait(false);

            await stalled.ConnectAsync(IPAddress.Loopback, host.ListeningPort)
                .WaitAsync(TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);
            var partialRequest = Encoding.ASCII.GetBytes(
                $"GET {ProtocolConstants.Path} HTTP/1.1\r\nHost: {ProtocolConstants.DefaultHost}:{host.ListeningPort}\r\n");
            await stalled.GetStream().WriteAsync(partialRequest).ConfigureAwait(false);
            await stalled.GetStream().FlushAsync().ConfigureAwait(false);

            authenticated = await ConnectAsync(host.ListeningPort).ConfigureAwait(false);
            var authenticatedHello = await ReceiveEnvelopeAsync(authenticated).ConfigureAwait(false);
            Assert(
                authenticatedHello.Type == ProtocolConstants.MessageTypes.Hello,
                "one stalled HTTP upgrade must not block a valid authenticated peer");

            // Leave the partial request open while stopping. Shutdown must
            // cancel and observe its setup task rather than waiting for the
            // five-second upgrade timeout or abandoning the task.
            await host.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        finally
        {
            authenticated?.Dispose();
            await host.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task TestHostHandshakeBusyAndReconnectAsync()
    {
        var diagnostics = new BridgeDiagnostics();
        var hello = new HelloPayload
        {
            ModVersion = "test",
            GameVersion = "test",
            StarMapVersion = "test",
            Capabilities = CanonicalCapabilities(),
        };
        var config = new KittenIOConfig
        {
            Endpoint = $"ws://{ProtocolConstants.DefaultHost}:0{ProtocolConstants.Path}",
            PairingToken = Token,
        };
        await using var host = new KittenIOWebSocketHost(
            config,
            diagnostics,
            hello,
            new ControlCoordinator(750, diagnostics));
        var sessionResetStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowSessionReset = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateSessionReset = 1;
        host.SessionStarted += _ =>
        {
            if (Interlocked.Exchange(ref gateSessionReset, 0) != 1) return;
            sessionResetStarted.TrySetResult();
            allowSessionReset.Task.GetAwaiter().GetResult();
        };
        host.Start();
        await WaitUntilAsync(() => host.ListeningPort != 0, "host did not bind loopback").ConfigureAwait(false);
        Assert(
            StringComparer.Ordinal.Equals(host.ListeningEndpoint?.Address, "127.0.0.1"),
            "the live listener must bind the literal IPv4 loopback address");

        Assert(
            await RawStatusAsync(host.ListeningPort, "Bearer wrong", "1", origin: null).ConfigureAwait(false) == 401,
            "the live listener should reject wrong authentication with HTTP 401");
        Assert(
            await RawStatusAsync(host.ListeningPort, $"Bearer {Token}", "2", origin: null).ConfigureAwait(false) == 426,
            "the live listener should reject protocol mismatch with HTTP 426");
        Assert(
            await RawStatusAsync(
                host.ListeningPort,
                $"Bearer {Token}",
                "1",
                origin: "https://example.invalid").ConfigureAwait(false) == 403,
            "the live listener should reject browser origins with HTTP 403");

        using var first = await ConnectAsync(host.ListeningPort).ConfigureAwait(false);
        var firstHello = await ReceiveEnvelopeAsync(first).ConfigureAwait(false);
        Assert(firstHello.Type == ProtocolConstants.MessageTypes.Hello, "the mod host must send hello first");
        Assert(firstHello.SessionId is null, "hello must remain sessionless");
        Assert(firstHello.GameInstanceId == host.GameInstanceId, "hello must use the mod's game-instance ID");

        var firstSession = Guid.NewGuid();
        await SendWelcomeAsync(first, host.GameInstanceId, firstSession).ConfigureAwait(false);
        await sessionResetStarted.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        await SendLeaseAcquireAsync(
            first,
            host.GameInstanceId,
            firstSession,
            "request-during-reset").ConfigureAwait(false);
        await Task.Delay(50).ConfigureAwait(false);
        Assert(
            !host.IsReady && !host.TryReadCommand(out _),
            "post-welcome commands must wait for exact session reset and readiness");
        allowSessionReset.TrySetResult();
        await WaitUntilAsync(
            () => host.IsReady && host.SessionId == firstSession,
            "first welcome did not establish the protocol session").ConfigureAwait(false);
        MainThreadMessage? firstCommand = null;
        await WaitUntilAsync(
            () => firstCommand is not null || host.TryReadCommand(out firstCommand),
            "command sent during session reset was not admitted afterward").ConfigureAwait(false);
        Assert(firstCommand is not null, "first session command context was not captured");

        var disconnectCleanupStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowDisconnectCleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateDisconnectCleanup = 1;
        host.Disconnected += (_, _) =>
        {
            if (Interlocked.Exchange(ref gateDisconnectCleanup, 0) != 1) return;
            disconnectCleanupStarted.TrySetResult();
            allowDisconnectCleanup.Task.GetAwaiter().GetResult();
        };

        // Busy state is checked only after the second connector has supplied the
        // correct token and completed its HTTP upgrade.
        using (var second = await ConnectAsync(host.ListeningPort).ConfigureAwait(false))
        {
            Assert(
                await ReceiveCloseStatusAsync(second).ConfigureAwait(false) == 1013,
                "a second authenticated peer must receive close code 1013");
        }

        Assert(
            await RawStatusAsync(host.ListeningPort, "Bearer wrong", "1", origin: null).ConfigureAwait(false) == 401,
            "busy state must not be disclosed to an unauthenticated connector");

        await first.CloseAsync(
            WebSocketCloseStatus.NormalClosure,
            "test reconnect",
            CancellationToken.None).ConfigureAwait(false);
        await disconnectCleanupStarted.Task.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        try
        {
            using var duringCleanup = await ConnectAsync(host.ListeningPort).ConfigureAwait(false);
            Assert(
                await ReceiveCloseStatusAsync(duringCleanup).ConfigureAwait(false) == 1013,
                "a new peer must not overlap old-session disconnect cleanup");
        }
        finally
        {
            allowDisconnectCleanup.TrySetResult();
        }
        await WaitUntilAsync(
            () => !host.IsReady && host.SessionId is null && !host.HasClaimedPeer,
            "first session cleanup did not release its peer claim").ConfigureAwait(false);

        using (var reused = await ConnectAsync(host.ListeningPort).ConfigureAwait(false))
        {
            _ = await ReceiveEnvelopeAsync(reused).ConfigureAwait(false);
            await SendWelcomeAsync(reused, host.GameInstanceId, firstSession).ConfigureAwait(false);
            Assert(
                await ReceiveCloseStatusAsync(reused).ConfigureAwait(false) == 1008,
                "a reconnect must not reuse a recent protocol session UUID");
        }
        await WaitUntilAsync(
            () => !host.HasClaimedPeer,
            "reused-session rejection did not finish peer cleanup").ConfigureAwait(false);

        using var replacement = await ConnectAsync(host.ListeningPort).ConfigureAwait(false);
        var replacementHello = await ReceiveEnvelopeAsync(replacement).ConfigureAwait(false);
        Assert(replacementHello.Type == ProtocolConstants.MessageTypes.Hello, "listener did not survive peer loss");
        var replacementSession = Guid.NewGuid();
        await SendWelcomeAsync(replacement, host.GameInstanceId, replacementSession).ConfigureAwait(false);
        await WaitUntilAsync(
            () => host.IsReady && host.SessionId == replacementSession,
            "replacement client did not establish a fresh session").ConfigureAwait(false);
        Assert(replacementSession != firstSession, "reconnect must use a fresh session ID");

        await SendLeaseAcquireAsync(
            replacement,
            host.GameInstanceId,
            replacementSession,
            "replacement-route").ConfigureAwait(false);
        MainThreadMessage? replacementCommand = null;
        await WaitUntilAsync(
            () => replacementCommand is not null || host.TryReadCommand(out replacementCommand),
            "replacement command context was not admitted").ConfigureAwait(false);
        Assert(replacementCommand is not null, "replacement command context was not captured");
        Assert(
            firstCommand!.Context.Controller != replacementCommand!.Context.Controller,
            "reconnect must assign a fresh ephemeral controller identity");

        var routedResponse = new ResponsePayload
        {
            Ok = true,
            Result = new { routed = true },
        };
        Assert(
            !host.SendResponse(routedResponse, firstCommand.Context),
            "an old session/controller response must not route to the replacement peer");
        var staleControllerContext = replacementCommand.Context with
        {
            Controller = firstCommand.Context.Controller,
        };
        Assert(
            !host.SendResponse(routedResponse, staleControllerContext),
            "an old controller response must not route even when its session ID is current");
        Assert(
            host.SendResponse(routedResponse, replacementCommand.Context),
            "the exact replacement context should route its response");
        var exactResponse = await ReceiveEnvelopeAsync(replacement).ConfigureAwait(false);
        Assert(exactResponse.Type == ProtocolConstants.MessageTypes.Response,
            "the exact-context outbound message should be a response");
        Assert(exactResponse.RequestId == "replacement-route",
            "stale outbound messages must not precede the exact-context response");

        await replacement.SendAsync(
            Encoding.UTF8.GetBytes("{").AsMemory(),
            WebSocketMessageType.Text,
            true,
            CancellationToken.None).ConfigureAwait(false);
        Assert(
            await ReceiveCloseStatusAsync(replacement).ConfigureAwait(false) == 1008,
            "malformed authenticated traffic must receive close code 1008");
        await WaitUntilAsync(() => !host.IsReady && host.SessionId is null, "malformed session was not cleared")
            .ConfigureAwait(false);

        using var malformedTyped = await ConnectAsync(host.ListeningPort).ConfigureAwait(false);
        _ = await ReceiveEnvelopeAsync(malformedTyped).ConfigureAwait(false);
        var malformedTypedSession = Guid.NewGuid();
        await SendWelcomeAsync(malformedTyped, host.GameInstanceId, malformedTypedSession).ConfigureAwait(false);
        await WaitUntilAsync(
            () => host.IsReady && host.SessionId == malformedTypedSession,
            "typed-malformed session did not become ready").ConfigureAwait(false);
        var malformedTypedBytes = WireCodec.Serialize(
            ProtocolConstants.MessageTypes.LeaseAcquire,
            host.GameInstanceId,
            malformedTypedSession,
            0,
            0,
            new { unexpected = true },
            "typed-malformed");
        await malformedTyped.SendAsync(
            malformedTypedBytes.AsMemory(),
            WebSocketMessageType.Text,
            true,
            CancellationToken.None).ConfigureAwait(false);
        Assert(
            await ReceiveCloseStatusAsync(malformedTyped).ConfigureAwait(false) == 1008,
            "malformed typed payload must close with policy code 1008 without a concurrent error send");
        await WaitUntilAsync(
            () => !host.HasClaimedPeer,
            "typed-malformed peer cleanup did not complete").ConfigureAwait(false);

        using var oversized = await ConnectAsync(host.ListeningPort).ConfigureAwait(false);
        _ = await ReceiveEnvelopeAsync(oversized).ConfigureAwait(false);
        var oversizedSession = Guid.NewGuid();
        await SendWelcomeAsync(oversized, host.GameInstanceId, oversizedSession).ConfigureAwait(false);
        await WaitUntilAsync(
            () => host.IsReady && host.SessionId == oversizedSession,
            "oversized-message session did not become ready").ConfigureAwait(false);
        var tooLarge = new byte[ProtocolConstants.MaximumMessageBytes + 1];
        Array.Fill(tooLarge, (byte)'x');
        await oversized.SendAsync(
            tooLarge.AsMemory(),
            WebSocketMessageType.Text,
            true,
            CancellationToken.None).ConfigureAwait(false);
        Assert(
            await ReceiveCloseStatusAsync(oversized).ConfigureAwait(false) == 1009,
            "oversized authenticated traffic must receive close code 1009");
    }

    private static Dictionary<string, string> ValidHeaders() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["Host"] = "127.0.0.1:8765",
        ["Connection"] = "keep-alive, Upgrade",
        ["Upgrade"] = "websocket",
        ["Sec-WebSocket-Version"] = "13",
        ["Sec-WebSocket-Key"] = "dGhlIHNhbXBsZSBub25jZQ==",
        ["X-KittenIO-Protocol"] = "1",
        ["Authorization"] = $"Bearer {Token}",
    };

    private static async Task<ClientWebSocket> ConnectAsync(int port)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {Token}");
        socket.Options.SetRequestHeader("X-KittenIO-Protocol", "1");
        try
        {
            await socket.ConnectAsync(
                new Uri($"ws://{ProtocolConstants.DefaultHost}:{port}{ProtocolConstants.Path}"),
                CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static async Task<int> RawStatusAsync(
        int port,
        string authorization,
        string protocol,
        string? origin)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        var stream = client.GetStream();
        var request = new StringBuilder()
            .Append("GET ").Append(ProtocolConstants.Path).Append(" HTTP/1.1\r\n")
            .Append("Host: 127.0.0.1:").Append(port).Append("\r\n")
            .Append("Connection: Upgrade\r\n")
            .Append("Upgrade: websocket\r\n")
            .Append("Sec-WebSocket-Version: 13\r\n")
            .Append("Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\n")
            .Append("Authorization: ").Append(authorization).Append("\r\n")
            .Append("X-KittenIO-Protocol: ").Append(protocol).Append("\r\n");
        if (origin is not null) request.Append("Origin: ").Append(origin).Append("\r\n");
        request.Append("\r\n");
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request.ToString())).ConfigureAwait(false);
        await stream.FlushAsync().ConfigureAwait(false);

        using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);
        var statusLine = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        var parts = statusLine?.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        return parts is { Length: 3 } && int.TryParse(parts[1], out var status)
            ? status
            : throw new InvalidDataException("Host returned an invalid HTTP status line.");
    }

    private static async Task<WireEnvelope> ReceiveEnvelopeAsync(ClientWebSocket socket)
    {
        var buffer = new byte[16 * 1024];
        using var payload = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(), CancellationToken.None)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(2))
                .ConfigureAwait(false);
            Assert(result.MessageType == WebSocketMessageType.Text, "expected a JSON text envelope");
            payload.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) break;
        }

        Assert(
            WireCodec.TryDeserialize(payload.ToArray(), out var envelope, out var error) && envelope is not null,
            error?.Message ?? "host emitted an invalid envelope");
        return envelope!;
    }

    private static async Task<int?> ReceiveCloseStatusAsync(ClientWebSocket socket)
    {
        var buffer = new byte[128];
        var close = await socket.ReceiveAsync(buffer.AsMemory(), CancellationToken.None)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2))
            .ConfigureAwait(false);
        Assert(close.MessageType == WebSocketMessageType.Close, "expected a WebSocket close frame");
        return (int?)socket.CloseStatus;
    }

    private static async Task SendWelcomeAsync(
        ClientWebSocket socket,
        Guid gameInstanceId,
        Guid sessionId)
    {
        var payload = new WelcomePayload
        {
            SelectedVersion = ProtocolConstants.Version,
            SessionId = sessionId,
            SdkVersion = "test",
            TelemetryHz = ProtocolConstants.DefaultTelemetryHz,
        };
        var bytes = WireCodec.Serialize(
            ProtocolConstants.MessageTypes.Welcome,
            gameInstanceId,
            sessionId,
            0,
            0,
            payload);
        await socket.SendAsync(
            bytes.AsMemory(),
            WebSocketMessageType.Text,
            true,
            CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task SendLeaseAcquireAsync(
        ClientWebSocket socket,
        Guid gameInstanceId,
        Guid sessionId,
        string requestId)
    {
        var bytes = WireCodec.Serialize(
            ProtocolConstants.MessageTypes.LeaseAcquire,
            gameInstanceId,
            sessionId,
            0,
            0,
            new LeaseAcquirePayload { VehicleId = "test-vehicle" },
            requestId);
        await socket.SendAsync(
            bytes.AsMemory(),
            WebSocketMessageType.Text,
            true,
            CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, string failure)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (!predicate() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10).ConfigureAwait(false);
        }
        Assert(predicate(), failure);
    }

    private static CapabilitySet CanonicalCapabilities() => new()
    {
        Control = new ControlCapabilities
        {
            ExactThrottle = true,
            EngineState = true,
            Stage = true,
            SetTarget = true,
            ThrustVectorTarget = true,
            DeadmanTimeoutMs = 750,
        },
        Dynamics = new DynamicsCapabilities
        {
            GravityCci = true, GravityCce = true, GravityLocal = true,
            TotalAcceleration = true, NonGravitationalAcceleration = true, BodyParameters = true,
        },
        Propulsion = new PropulsionCapabilities
        {
            Aggregate = true, Engines = true, EnginePositions = true, GimbalState = true,
            GimbalRateLimits = false, RestartCounts = false, ResponseParameters = false,
        },
        RigidBody = new RigidBodyCapabilities
        {
            CenterOfMass = true, InertiaTensor = true, AngularAcceleration = true,
            TotalForceMoment = true, PropulsiveForceMoment = true,
            AerodynamicForceMoment = false, RcsForceMoment = false, TorqueAuthority = false,
        },
        Atmosphere = new AtmosphereCapabilities
        {
            Pressure = true, Density = true, Temperature = false, SpeedOfSound = false,
            Mach = false, DynamicPressure = true, Wind = false,
            AirRelativeVelocity = true, Angles = false,
        },
        Landing = new LandingCapabilities
        {
            SiteDefinition = true, LocalFrame = true, TerrainHeight = false,
            GroundNormal = true, SurfaceSlope = true,
        },
        Surface = new SurfaceCapabilities
        {
            GroundContact = true, TouchdownEvents = true, CollisionEvents = false,
            StructuralFailureEvents = false, EngineOutEvents = false,
            PropellantExhaustion = true, Controllability = true,
            ContactPoints = false, LandingLegState = false,
        },
        AppliedControl = true,
        RecordingReplay = true,
        TimeWarp = new TimeWarpCapabilities
        {
            Control = true,
            AvailableRates = [0, 1, 2, 4],
            Ownership = "game_global_persistent",
            UnavailableReason = null,
        },
    };

    private static async Task ThrowsAsync<TException>(Func<Task> action, string failure)
        where TException : Exception => _ = await CaptureAsync<TException>(action, failure).ConfigureAwait(false);

    private static async Task<TException> CaptureAsync<TException>(Func<Task> action, string? failure = null)
        where TException : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (TException exception)
        {
            return exception;
        }
        throw new InvalidOperationException(failure ?? $"Expected {typeof(TException).Name}.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
