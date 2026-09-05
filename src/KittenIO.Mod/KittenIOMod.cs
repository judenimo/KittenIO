using System.Reflection;
using System.Text.Json;
using KittenIO.Mod.Configuration;
using KittenIO.Mod.Control;
using KittenIO.Mod.Diagnostics;
using KittenIO.Mod.Game;
using KittenIO.Mod.Networking;
using KittenIO.Mod.Patching;
using KittenIO.Mod.Runtime;
using KittenIO.Protocol;
using StarMap.API;

namespace KittenIO.Mod;

[StarMapMod]
public sealed class KittenIOMod
{
    private readonly SideEffectRegistry _sideEffects = new();
    private readonly CatalogPublicationTracker _catalogPublication = new();
    private readonly GameInstanceId _gameInstanceId = GameInstanceId.New();
    private BridgeDiagnostics? _diagnostics;
    private KittenIOConfig? _config;
    private IKsaGameAdapter? _game;
    private ControlCoordinator? _control;
    private ActiveVehicleSwitchCoordinator? _vehicleSwitch;
    private TimeWarpCoordinator? _timeWarp;
    private FlightComputerPatch? _controlPatch;
    private KittenIOWebSocketHost? _network;
    private CapabilitySet? _capabilities;
    private long _frameNumber;
    private long _lastTelemetryMs;
    private long _lastCatalogCheckMs;
    private string? _lastVehicleId;
    private ProtocolPeerContext? _sessionContext;
    private int _catalogRequested;
    private int _catalogResetRequested;
    private int _telemetryRequested;
    private int _switchResetRequested;
    private int _timeWarpResetRequested;
    private int _landingResetRequested;
    private bool _loaded;

    [StarMapImmediateLoad]
    public void Load(KSA.Mod definingMod)
    {
        _ = definingMod;
        if (_loaded) return;

        try
        {
            _diagnostics = new BridgeDiagnostics();
            _config = KittenIOConfigStore.LoadOrCreate();
            _game = new KsaGameAdapter();
            _control = new ControlCoordinator(_config.CommandDeadmanMs, _diagnostics, _gameInstanceId);
            _vehicleSwitch = new ActiveVehicleSwitchCoordinator(_game, _control, _gameInstanceId);
            _timeWarp = new TimeWarpCoordinator(_game);
            _controlPatch = new FlightComputerPatch();

            var compatibility = _controlPatch.Install(_game.GameVersion, _control);
            _control.SetControlAvailability(compatibility.Available);
            _diagnostics.SetCompatibility(compatibility.Description);

            _capabilities = CreateCapabilities(
                _game.TelemetryContractVerified,
                compatibility.Available,
                _config.CommandDeadmanMs,
                _game.TimeWarpContractVerified,
                _game.AvailableTimeWarpRates,
                _game.TimeWarpContractFailureReason);

            var hello = new HelloPayload
            {
                ModVersion = GetAssemblyVersion(typeof(KittenIOMod).Assembly),
                GameVersion = _game.GameVersion,
                StarMapVersion = GetAssemblyVersion(typeof(StarMapModAttribute).Assembly),
                Capabilities = _capabilities,
            };

            _network = new KittenIOWebSocketHost(_config, _diagnostics, hello, _control, _gameInstanceId);
            _network.SessionStarted += OnSessionStarted;
            _network.Disconnected += OnDisconnected;
            _network.Start();
            _loaded = true;

            Console.WriteLine($"[KittenIO] Loaded. Loopback host: {_config.Endpoint}");
            Console.WriteLine($"[KittenIO] Pairing config: {KittenIOConfigStore.ConfigPath}");
        }
        catch (Exception exception)
        {
            _diagnostics?.ReportError("Mod initialization failed safely", exception);
            _control?.ReleaseAll("initialization failed", notifyPeer: false);
            _controlPatch?.Dispose();
        }
    }

    [StarMapAfterOnFrame]
    public void AfterOnFrame(double currentPlayerTime, double dtPlayer)
    {
        if (!_loaded || _game is null || _network is null || _control is null || _capabilities is null || _diagnostics is null)
        {
            return;
        }

        try
        {
            var nowMs = MonotonicClock.NowMilliseconds;
            _frameNumber++;
            _game.Refresh();
            var activeVehicle = _game.ActiveVehicle;
            if (Interlocked.Exchange(ref _switchResetRequested, 0) != 0)
            {
                _vehicleSwitch?.CancelAll();
            }
            if (Interlocked.Exchange(ref _timeWarpResetRequested, 0) != 0)
            {
                _timeWarp?.CancelAll();
            }
            if (Interlocked.Exchange(ref _catalogResetRequested, 0) != 0)
            {
                _catalogPublication.Reset();
                _game.ResetCatalogTargets();
            }
            if (Interlocked.Exchange(ref _landingResetRequested, 0) != 0)
            {
                _game.ResetLandingReference();
            }
            _control.UpdateActiveVehicle(activeVehicle);
            _control.ReleaseForPendingPatchFault();
            _control.ExpireIfStale(nowMs);
            _diagnostics.SetVehicle(activeVehicle.DisplayName, activeVehicle.VehicleId?.Value);

            DetectVehicleChange(activeVehicle.VehicleId?.Value);
            ProcessInboundControl();
            ProcessMainThreadMessages(nowMs);
            AdvanceActiveVehicleSwitch(nowMs);
            AdvanceTimeWarp(nowMs);
            FlushLeaseReleaseNotices();

            if (!_network.IsReady) return;

            var forceCatalog = Interlocked.Exchange(ref _catalogRequested, 0) != 0;
            if (forceCatalog || nowMs - _lastCatalogCheckMs >= 1_000)
            {
                _lastCatalogCheckMs = nowMs;
                PublishCatalogIfChanged(forceCatalog);
            }

            var forceTelemetry = Interlocked.Exchange(ref _telemetryRequested, 0) != 0;
            var telemetryIntervalMs = 1_000d / Math.Clamp(_network.NegotiatedTelemetryHz, 1, 60);
            if (!forceTelemetry && nowMs - _lastTelemetryMs < telemetryIntervalMs) return;
            _lastTelemetryMs = nowMs;

            var simulationTime = _game.GetSimulationTime();
            if (simulationTime is null || !double.IsFinite(dtPlayer))
            {
                _diagnostics.ReportError("KSA supplied unavailable or non-finite simulation timing; this telemetry sample was omitted.");
                return;
            }

            var capturedFrame = _game.GetFrameNumber() ?? _frameNumber;
            var telemetry = _game.CaptureTelemetry(simulationTime.Value);
            _network.PublishTelemetry(new FlightStatePayload
            {
                FrameNumber = capturedFrame,
                PlayerTimeS = simulationTime.Value,
                DeltaTimeS = dtPlayer,
                SimulationSpeed = _game.GetSimulationSpeed(),
                CapturedMonotonicMs = nowMs,
                Capabilities = _capabilities,
                Vehicle = telemetry.Vehicle,
                Propulsion = telemetry.Propulsion,
                RigidBody = telemetry.RigidBody,
                Atmosphere = telemetry.Atmosphere,
                Surface = telemetry.Surface,
                LandingReference = telemetry.LandingReference,
                AppliedControl = _control.CaptureAppliedControl(),
                ManualControl = _game.CaptureManualControl(),
            });
            _diagnostics.MarkTelemetry(nowMs);
        }
        catch (Exception exception)
        {
            _control.ReleaseAll("frame exception");
            _diagnostics.ReportError("AfterOnFrame failed safely", exception);
        }
    }

    [StarMapUnload]
    public void Unload()
    {
        if (!_loaded) return;
        _loaded = false;

        var network = _network;
        FailureIsolatedCleanup.Run(
            _diagnostics,
            new CleanupStep("Control release during unload", () => _control?.ReleaseAll("mod unload", notifyPeer: false)),
            new CleanupStep("Vehicle switch cancellation during unload", () => _vehicleSwitch?.CancelAll()),
            new CleanupStep("Time-warp request tracking cancellation during unload", () => _timeWarp?.CancelAll()),
            new CleanupStep("Harmony patch removal during unload", () => _controlPatch?.Dispose()),
            new CleanupStep("WebSocket shutdown during unload", () =>
            {
                if (network is null) return;
                network.SessionStarted -= OnSessionStarted;
                network.Disconnected -= OnDisconnected;
                var diagnostics = _diagnostics;
                _ = network.DisposeAsync().AsTask().ContinueWith(
                    static (task, state) => ((BridgeDiagnostics?)state)?.ReportError("WebSocket shutdown failed", task.Exception),
                    diagnostics,
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted,
                    TaskScheduler.Default);
            }));

        Console.WriteLine("[KittenIO] Unloaded; external control was released.");
    }

    private void ProcessInboundControl()
    {
        if (_network is null || _control is null || !_network.TryReadLatestControl(out var inbound) || inbound is null) return;
        if (!_network.IsCurrentContext(inbound.Context))
        {
            _network.SendError("stale_session", "Control frame belongs to a previous connection.", inbound.Context);
            return;
        }

        if (!_control.TryAccept(inbound.Context, inbound.Payload, inbound.ReceivedMonotonicMs, out var error))
        {
            _network.SendError(error?.Code ?? "invalid_control", error?.Message ?? "Control frame was rejected.", inbound.Context);
        }
    }

    private void ProcessMainThreadMessages(long nowMs)
    {
        if (_network is null || _control is null || _game is null) return;
        for (var processed = 0; processed < ProtocolConstants.ReliableQueueCapacity && _network.TryReadCommand(out var message); processed++)
        {
            if (message is null) continue;
            if (!_network.IsCurrentContext(message.Context))
            {
                _network.SendError("stale_session", "Command belongs to a previous connection.", message.Context);
                continue;
            }

            switch (message)
            {
                case AcquireLeaseMessage acquire:
                    if (string.IsNullOrWhiteSpace(acquire.RequestId))
                    {
                        _network.SendError("request_id_required", "lease.acquire requires request_id.", acquire.Context);
                    }
                    else if (!IsControlCadenceSafe(_network.NegotiatedTelemetryHz, _control.TimeoutMs))
                    {
                        _network.SendError(
                            "unsafe_control_cadence",
                            "The negotiated telemetry rate is too slow to refresh control safely; increase telemetry_hz before acquiring a lease.",
                            acquire.Context);
                    }
                    else if (_control.TryAcquire(acquire.Context, acquire.Payload.VehicleId, nowMs, out var granted, out var acquireError) && granted is not null)
                    {
                        _network.SendLeaseGranted(granted, acquire.Context);
                    }
                    else
                    {
                        _network.SendError(acquireError?.Code ?? "lease_denied", acquireError?.Message ?? "Control lease was denied.", acquire.Context);
                    }
                    break;

                case ReleaseLeaseMessage release:
                    if (string.IsNullOrWhiteSpace(release.RequestId))
                    {
                        _network.SendError("request_id_required", "lease.release requires request_id.", release.Context);
                    }
                    else if (_control.Release(release.Context, release.Payload.LeaseId, release.Payload.Reason ?? "peer request"))
                    {
                        _network.SendLeaseReleased(release.Payload, release.Context);
                    }
                    else
                    {
                        _network.SendError("invalid_lease", "The lease is not active.", release.Context);
                    }
                    break;

                case GameRequestMessage request:
                    HandleRequest(request, nowMs);
                    break;
            }
        }
    }

    private void HandleRequest(GameRequestMessage request, long nowMs)
    {
        if (_network is null || _game is null || _capabilities is null) return;
        if (string.IsNullOrWhiteSpace(request.RequestId))
        {
            _network.SendError("request_id_required", "request messages require request_id.", request.Context);
            return;
        }
        if (request.Payload.CommandId == Guid.Empty)
        {
            _network.SendResponse(Failure("invalid_command_id", "command_id must be a non-empty UUID."), request.Context);
            return;
        }
        var isVehicleSwitch = request.Payload.Method == "set_active_vehicle";
        var isTimeWarp = IsTimeWarpMethod(request.Payload.Method);
        var isSideEffect = isVehicleSwitch || isTimeWarp || request.Payload.Method is "activate_next_sequence" or "set_target";
        var executionContext = request.Context;
        if (isSideEffect)
        {
            if (!isVehicleSwitch && !isTimeWarp && !TryBindSideEffectContext(request, out executionContext, out var bindingError))
            {
                _network.SendResponse(bindingError!, request.Context);
                return;
            }

            switch (_sideEffects.TryBegin(executionContext, request.Payload.CommandId, out var cached))
            {
                case SideEffectAdmission.Replay:
                    _network.SendResponse(cached!, request.Context);
                    return;
                case SideEffectAdmission.InProgress:
                    _network.SendResponse(Failure("busy", "This side-effect command is still in progress; retry shortly."), request.Context);
                    return;
                case SideEffectAdmission.Full:
                    _network.SendResponse(Failure("busy", "The session side-effect registry is full; reconnect before issuing a new command ID."), request.Context);
                    return;
            }

            if (isVehicleSwitch)
            {
                var switchResponse = BeginActiveVehicleSwitch(request, nowMs);
                if (switchResponse is null) return;
                _sideEffects.Complete(executionContext, request.Payload.CommandId, switchResponse);
                _network.SendResponse(switchResponse, request.Context);
                return;
            }
            if (isTimeWarp)
            {
                var timeWarpResponse = BeginTimeWarp(request, nowMs);
                if (timeWarpResponse is null) return;
                _sideEffects.Complete(executionContext, request.Payload.CommandId, timeWarpResponse);
                _network.SendResponse(timeWarpResponse, request.Context);
                return;
            }
        }

        ResponsePayload response;
        try
        {
            response = request.Payload.Method switch
            {
                "get_catalog" => HandleGetCatalog(),
                "activate_next_sequence" => HandleVehicleCommand(executionContext, _game.ActivateNextSequence),
                "set_target" => HandleSetTarget(executionContext, request.Payload.Parameters),
                "set_landing_site" => HandleSetLandingSite(request),
                _ => Failure("unknown_method", $"Unknown request method '{request.Payload.Method}'."),
            };
        }
        catch (Exception exception)
        {
            response = Failure("game_command_failed", exception.Message);
        }

        if (isSideEffect)
        {
            _sideEffects.Complete(executionContext, request.Payload.CommandId, response);
        }
        _network.SendResponse(response, request.Context);
    }

    private ResponsePayload? BeginActiveVehicleSwitch(GameRequestMessage request, long nowMs)
    {
        if (_vehicleSwitch is null)
        {
            return Failure("switch_unavailable", "The active-vehicle switch coordinator is unavailable.");
        }
        if (!TryExactVehicleSwitchParameters(request.Payload.Parameters, out var vehicleId, out var parameterError))
        {
            return parameterError;
        }
        if (!_vehicleSwitch.TryBegin(
                request.Context,
                request.Payload.CommandId,
                vehicleId!,
                _frameNumber,
                nowMs,
                out var failure))
        {
            return FromGameResult(failure ?? GameCommandResult.Failure(
                "switch_failed",
                "The active-vehicle switch request could not be started."));
        }

        return null;
    }

    private void AdvanceActiveVehicleSwitch(long nowMs)
    {
        if (_vehicleSwitch?.TryAdvance(_frameNumber, nowMs, out var completion) != true || completion is null)
        {
            return;
        }

        var response = FromGameResult(completion.Result);
        _sideEffects.Complete(completion.Context, completion.CommandId, response);
        _network?.SendResponse(response, completion.Context);
        Interlocked.Exchange(ref _catalogRequested, 1);
        Interlocked.Exchange(ref _telemetryRequested, 1);
    }

    private ResponsePayload? BeginTimeWarp(GameRequestMessage request, long nowMs)
    {
        if (_game is null || _timeWarp is null || !_game.TimeWarpContractVerified)
        {
            return Failure(
                "time_warp_unavailable",
                _game?.TimeWarpContractFailureReason.Length > 0
                    ? _game.TimeWarpContractFailureReason
                    : "The pinned KSA time-warp command contract is unavailable.");
        }
        if (!TryParseTimeWarpRequest(request.Payload.Method, request.Payload.Parameters, out var kind, out var rate, out var parameterError))
        {
            return parameterError;
        }
        if (!_timeWarp.TryBegin(
                request.Context,
                request.Payload.CommandId,
                kind,
                rate,
                _frameNumber,
                nowMs,
                out var failure))
        {
            return FromGameResult(failure ?? GameCommandResult.Failure(
                "time_warp_refused",
                "KSA rejected the time-warp request."));
        }
        return null;
    }

    private void AdvanceTimeWarp(long nowMs)
    {
        if (_timeWarp?.TryAdvance(_frameNumber, nowMs, out var completion) != true || completion is null)
        {
            return;
        }
        if (_network?.IsCurrentContext(completion.Context) != true)
        {
            return;
        }
        var response = FromGameResult(completion.Result);
        _sideEffects.Complete(completion.Context, completion.CommandId, response);
        _network?.SendResponse(response, completion.Context);
        Interlocked.Exchange(ref _telemetryRequested, 1);
    }

    private ResponsePayload HandleGetCatalog()
    {
        if (_game is null || _network is null) return Failure("not_loaded", "KittenIO is not loaded.");
        var catalog = _game.CaptureCatalog();
        _network.PublishCatalog(catalog);
        _catalogPublication.Observe(catalog, force: true);
        return Success(catalog);
    }

    private bool TryBindSideEffectContext(
        GameRequestMessage request,
        out CommandContext context,
        out ResponsePayload? error)
    {
        context = request.Context;
        error = null;
        if (_game is null || _control is null || _capabilities?.Control.ThrustVectorTarget != true)
        {
            error = Failure("control_unavailable", "This KSA build is operating in telemetry-only mode.");
            return false;
        }

        if (!TryRequiredString(request.Payload.Parameters, "vehicle_id", out var vehicleId, out error)) return false;
        if (!StringComparer.Ordinal.Equals(vehicleId, _game.ActiveVehicle.VehicleId?.Value))
        {
            error = Failure("vehicle_changed", "vehicle_id is not the active controlled vehicle.");
            return false;
        }

        if (!TryRequiredGuid(request.Payload.Parameters, "lease_id", out var leaseId, out error)) return false;
        if (!_control.TryBindAuthorizedRoute(request.Context, vehicleId!, leaseId, out context))
        {
            error = Failure("invalid_lease", "The side-effect request requires the active control lease for this session and vehicle.");
            return false;
        }

        return true;
    }

    private ResponsePayload HandleVehicleCommand(CommandContext context, Func<GameCommandResult> command)
    {
        if (_control is null || context.Route is not { } route || context.LeaseId is not { } leaseId ||
            !_control.IsAuthorized(context, route.Vehicle.Value, leaseId))
        {
            return Failure("invalid_lease", "The side-effect request lost its active control authority before execution.");
        }

        return FromGameResult(command());
    }

    private ResponsePayload HandleSetTarget(CommandContext context, JsonElement parameters)
    {
        if (!TryRequiredString(parameters, "target_id", out var targetId, out var targetError)) return targetError!;
        return HandleVehicleCommand(context, () => _game!.SetTarget(targetId!));
    }

    private ResponsePayload HandleSetLandingSite(GameRequestMessage request)
    {
        if (_game is null || !_game.TelemetryContractVerified)
        {
            return Failure("landing_unavailable", "Landing references are unavailable on this KSA build.");
        }
        try
        {
            var site = JsonSerializer.Deserialize<LandingSiteDefinition>(request.Payload.Parameters, WireCodec.Options);
            return site is null
                ? Failure("invalid_params", "set_landing_site requires a landing-site object.")
                : FromGameResult(_game.SetLandingSite(site));
        }
        catch (JsonException exception)
        {
            return Failure("invalid_params", exception.Message);
        }
    }

    private void DetectVehicleChange(string? vehicleId)
    {
        if (StringComparer.Ordinal.Equals(_lastVehicleId, vehicleId)) return;
        var previous = _lastVehicleId;
        _lastVehicleId = vehicleId;
        Interlocked.Exchange(ref _catalogRequested, 1);

        if (_network?.IsReady == true)
        {
            _network.SendEvent(new EventPayload
            {
                Name = "vehicle.changed",
                Data = new { previous_vehicle_id = previous, vehicle_id = vehicleId },
            });
        }
    }

    private void PublishCatalogIfChanged(bool force)
    {
        if (_game is null || _network is null) return;
        var catalog = _game.CaptureCatalog();
        var decision = _catalogPublication.Observe(catalog, force);
        if (!decision.Publish) return;
        _network.PublishCatalog(catalog);
        if (decision.Changed)
        {
            _network.SendEvent(new EventPayload
            {
                Name = "catalog.changed",
                Data = new
                {
                    body_count = catalog.Bodies.Count,
                    vehicle_count = catalog.Vehicles.Count,
                },
            });
        }
    }

    private void FlushLeaseReleaseNotices()
    {
        if (_control is null || _network?.IsReady != true) return;
        while (_control.TryReadReleaseNotice(out var release) && release is not null)
        {
            _network.SendLeaseReleased(release.Payload, release.Context);
        }
    }

    private void OnSessionStarted(ProtocolPeerContext context)
    {
        if (_sessionContext is { } previous)
        {
            _sideEffects.ClearController(previous.Controller);
            _control?.ReleaseController(
                previous.Controller,
                $"replaced session {previous.Session.Value:N}",
                notifyPeer: false);
        }
        _sessionContext = context;
        _control?.ClearReleaseNotices();
        Interlocked.Exchange(ref _switchResetRequested, 1);
        Interlocked.Exchange(ref _timeWarpResetRequested, 1);
        Interlocked.Exchange(ref _catalogResetRequested, 1);
        Interlocked.Exchange(ref _landingResetRequested, 1);
        Interlocked.Exchange(ref _catalogRequested, 1);
    }

    private void OnDisconnected(ProtocolPeerContext? context, string reason)
    {
        if (context is { } ended)
        {
            _sideEffects.ClearController(ended.Controller);
            _control?.ReleaseController(ended.Controller, reason, notifyPeer: false);
            if (_sessionContext == ended) _sessionContext = null;
        }
        _control?.ClearReleaseNotices();
        Interlocked.Exchange(ref _switchResetRequested, 1);
        Interlocked.Exchange(ref _timeWarpResetRequested, 1);
        Interlocked.Exchange(ref _catalogResetRequested, 1);
        Interlocked.Exchange(ref _landingResetRequested, 1);
    }

    private static CapabilitySet CreateCapabilities(
        bool telemetryAvailable,
        bool controlAvailable,
        int deadmanTimeoutMs,
        bool timeWarpAvailable,
        IReadOnlyList<double> availableRates,
        string unavailableReason) => new()
    {
        Control = new ControlCapabilities
        {
            ExactThrottle = controlAvailable,
            EngineState = controlAvailable,
            Stage = controlAvailable,
            SetTarget = controlAvailable,
            ThrustVectorTarget = controlAvailable,
            DeadmanTimeoutMs = deadmanTimeoutMs,
        },
        Dynamics = new DynamicsCapabilities
        {
            GravityCci = telemetryAvailable,
            GravityCce = telemetryAvailable,
            GravityLocal = telemetryAvailable,
            TotalAcceleration = telemetryAvailable,
            NonGravitationalAcceleration = telemetryAvailable,
            BodyParameters = telemetryAvailable,
        },
        Propulsion = new PropulsionCapabilities
        {
            Aggregate = telemetryAvailable,
            Engines = telemetryAvailable,
            EnginePositions = telemetryAvailable,
            GimbalState = telemetryAvailable,
            GimbalRateLimits = false,
            RestartCounts = false,
            ResponseParameters = false,
        },
        RigidBody = new RigidBodyCapabilities
        {
            CenterOfMass = telemetryAvailable,
            InertiaTensor = telemetryAvailable,
            AngularAcceleration = telemetryAvailable,
            TotalForceMoment = telemetryAvailable,
            PropulsiveForceMoment = telemetryAvailable,
            AerodynamicForceMoment = false,
            RcsForceMoment = false,
            TorqueAuthority = false,
        },
        Atmosphere = new AtmosphereCapabilities
        {
            Pressure = telemetryAvailable,
            Density = telemetryAvailable,
            Temperature = false,
            SpeedOfSound = false,
            Mach = false,
            DynamicPressure = telemetryAvailable,
            Wind = false,
            AirRelativeVelocity = telemetryAvailable,
            Angles = false,
        },
        Landing = new LandingCapabilities
        {
            SiteDefinition = telemetryAvailable,
            LocalFrame = telemetryAvailable,
            TerrainHeight = false,
            GroundNormal = telemetryAvailable,
            SurfaceSlope = telemetryAvailable,
        },
        Surface = new SurfaceCapabilities
        {
            GroundContact = telemetryAvailable,
            TouchdownEvents = telemetryAvailable,
            CollisionEvents = false,
            StructuralFailureEvents = false,
            EngineOutEvents = false,
            PropellantExhaustion = telemetryAvailable,
            Controllability = telemetryAvailable,
            ContactPoints = false,
            LandingLegState = false,
        },
        AppliedControl = controlAvailable,
        RecordingReplay = true,
        TimeWarp = new TimeWarpCapabilities
        {
            Control = timeWarpAvailable,
            AvailableRates = timeWarpAvailable ? availableRates : [],
            Ownership = "game_global_persistent",
            UnavailableReason = timeWarpAvailable ? null : unavailableReason,
        },
    };

    private static bool IsTimeWarpMethod(string method) => method is
        "time_warp.set_rate" or "time_warp.increase" or "time_warp.decrease" or "time_warp.cancel";

    internal static bool TryParseTimeWarpRequest(
        string method,
        JsonElement parameters,
        out TimeWarpCommandKind kind,
        out double? rate,
        out ResponsePayload? error)
    {
        kind = default;
        rate = null;
        error = null;
        if (parameters.ValueKind != JsonValueKind.Object)
        {
            error = Failure("invalid_params", "time-warp params must be an object.");
            return false;
        }

        if (method == "time_warp.set_rate")
        {
            using var properties = parameters.EnumerateObject();
            if (!properties.MoveNext())
            {
                error = Failure("invalid_params", "time_warp.set_rate params must contain exactly one numeric rate.");
                return false;
            }
            var property = properties.Current;
            if (properties.MoveNext() || property.Name != "rate" ||
                property.Value.ValueKind != JsonValueKind.Number ||
                !property.Value.TryGetDouble(out var parsedRate) || !double.IsFinite(parsedRate))
            {
                error = Failure("invalid_params", "time_warp.set_rate params must contain exactly one finite numeric rate.");
                return false;
            }
            kind = TimeWarpCommandKind.SetRate;
            rate = parsedRate;
            return true;
        }

        if (parameters.EnumerateObject().MoveNext())
        {
            error = Failure("invalid_params", $"{method} params must be empty.");
            return false;
        }
        kind = method switch
        {
            "time_warp.increase" => TimeWarpCommandKind.Increase,
            "time_warp.decrease" => TimeWarpCommandKind.Decrease,
            "time_warp.cancel" => TimeWarpCommandKind.Cancel,
            _ => throw new InvalidOperationException("Unknown time-warp method."),
        };
        return true;
    }

    internal static bool TryExactVehicleSwitchParameters(
        JsonElement parameters,
        out string? vehicleId,
        out ResponsePayload? error)
    {
        vehicleId = null;
        error = null;
        if (parameters.ValueKind != JsonValueKind.Object)
        {
            error = Failure("invalid_params", "params must contain exactly one vehicle_id string.");
            return false;
        }

        using var properties = parameters.EnumerateObject();
        if (!properties.MoveNext())
        {
            error = Failure("invalid_params", "params must contain exactly one vehicle_id string.");
            return false;
        }
        var property = properties.Current;
        var parsedVehicleId = property.Value.ValueKind == JsonValueKind.String
            ? property.Value.GetString()
            : null;
        if (properties.MoveNext() ||
            property.Name != "vehicle_id" ||
            string.IsNullOrWhiteSpace(parsedVehicleId))
        {
            error = Failure("invalid_params", "params must contain exactly one non-empty vehicle_id string.");
            return false;
        }

        vehicleId = parsedVehicleId;
        return true;
    }

    private static bool TryRequiredString(JsonElement parameters, string name, out string? value, out ResponsePayload? error)
    {
        value = null;
        error = null;
        if (parameters.ValueKind != JsonValueKind.Object ||
            !parameters.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value = property.GetString()))
        {
            error = Failure("invalid_params", $"params.{name} must be a non-empty string.");
            return false;
        }
        return true;
    }

    private static bool TryRequiredGuid(JsonElement parameters, string name, out Guid value, out ResponsePayload? error)
    {
        value = Guid.Empty;
        error = null;
        if (parameters.ValueKind != JsonValueKind.Object ||
            !parameters.TryGetProperty(name, out var property) ||
            property.ValueKind != JsonValueKind.String ||
            property.GetString() is not { } text ||
            !Guid.TryParseExact(text, "D", out value) ||
            !StringComparer.Ordinal.Equals(text, value.ToString("D")) ||
            value == Guid.Empty)
        {
            error = Failure("invalid_params", $"params.{name} must be a lowercase canonical non-empty UUID string.");
            return false;
        }
        return true;
    }

    private static ResponsePayload FromGameResult(GameCommandResult result) => result.Ok
        ? Success(result.Result ?? new { })
        : Failure(result.Code, result.Message);

    private static ResponsePayload Success<T>(T value) => new()
    {
        Ok = true,
        Result = value,
    };

    private static ResponsePayload Failure(string code, string message) => new()
    {
        Ok = false,
        Error = new ErrorPayload { Code = code, Message = message },
    };

    private static string GetAssemblyVersion(Assembly assembly) =>
        assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ??
        assembly.GetName().Version?.ToString() ??
        "unknown";

    internal static bool IsControlCadenceSafe(int telemetryHz, int timeoutMs) =>
        telemetryHz > 0 && timeoutMs > 0 && (1_000d / telemetryHz) * 4d <= timeoutMs;
}
