using System.Diagnostics;
using System.Reflection;
using KittenIO.Mod.Runtime;
using KittenIO.Protocol;

namespace KittenIO.Mod.Game;

internal sealed class KsaGameAdapter : IKsaGameAdapter
{
    private readonly Assembly _ksaAssembly = typeof(KSA.Mod).Assembly;
    private readonly Type? _programType;
    private readonly Type? _universeType;
    private readonly Type? _vehicleType;
    private readonly Type? _sequenceListType;
    private readonly PinnedKsaVehicleSwitcher? _vehicleSwitcher;
    private readonly PinnedKsaPropulsionTelemetry? _propulsionTelemetry;
    private readonly PinnedKsaTelemetry? _telemetry;
    private readonly PinnedKsaTimeWarpController? _timeWarp;
    private readonly Action<string> _diagnosticSink;
    private int _mainThreadId;
    private readonly WeakEntityIdRegistry _fallbackIds = new();
    private readonly CatalogTargetRegistry _targets = new();
    private object? _activeVehicle;
    private object? _activeFlightComputer;
    private AuthorityEpoch _authorityEpoch = new(0);
    private int _lastOmittedVehicleCount;
    private LandingSiteDefinition? _landingSite;

    public KsaGameAdapter(Action<string>? diagnosticSink = null)
    {
        _programType = _ksaAssembly.GetType("KSA.Program");
        _universeType = _ksaAssembly.GetType("KSA.Universe");
        _vehicleType = _ksaAssembly.GetType("KSA.Vehicle");
        _sequenceListType = _ksaAssembly.GetType("KSA.SequenceList");
        GameVersion = GetFileVersion(_ksaAssembly);
        _diagnosticSink = diagnosticSink ?? Console.Error.WriteLine;
        if (!PinnedKsaVehicleSwitcher.TryCreate(_ksaAssembly, out _vehicleSwitcher, out var switchReason))
        {
            ActiveVehicleSwitchContractFailureReason = switchReason;
        }
        PinnedKsaPropulsionTelemetry.TryCreate(
            _ksaAssembly,
            out _propulsionTelemetry,
            out _);
        if (!PinnedKsaTelemetry.TryCreate(_ksaAssembly, out _telemetry, out var telemetryReason))
        {
            TelemetryContractFailureReason = telemetryReason;
        }
        if (!PinnedKsaTimeWarpController.TryCreate(_ksaAssembly, out _timeWarp, out var timeWarpReason))
        {
            TimeWarpContractFailureReason = timeWarpReason;
        }
    }

    public string GameVersion { get; }

    public bool ActiveVehicleSwitchContractVerified => _vehicleSwitcher is not null;

    public string ActiveVehicleSwitchContractFailureReason { get; } = string.Empty;

    public bool TelemetryContractVerified => _telemetry is not null;

    public string TelemetryContractFailureReason { get; } = string.Empty;

    public bool TimeWarpContractVerified => _timeWarp is not null;

    public string TimeWarpContractFailureReason { get; } = string.Empty;

    public IReadOnlyList<double> AvailableTimeWarpRates =>
        _timeWarp is null ? [] : PinnedKsaTimeWarpController.PinnedRates;

    public ActiveVehicleObservation ActiveVehicle { get; private set; } = new(null, null, false, null, null, null, new AuthorityEpoch(0));

    public void Refresh()
    {
        AssertMainThread();
        var nextVehicle = ReflectionAccess.GetStatic(_programType, "ControlledVehicle");
        var nextVehicleIdText = GetId(nextVehicle);
        VehicleId? nextVehicleId = nextVehicleIdText is null ? null : new VehicleId(nextVehicleIdText);
        var nextFlightComputer = ReflectionAccess.GetOrInvoke(nextVehicle, "FlightComputer", "GetFlightComputer");
        var vehicleControllable = ReflectionAccess.ToBoolean(ReflectionAccess.GetOrInvoke(nextVehicle, "IsControllable"));
        var nextControllable = nextVehicle is not null &&
            nextFlightComputer is not null &&
            HasVerifiedControlAuthority(vehicleControllable);
        var nextVehicleInstance = nextVehicle is null ? null : VehicleInstanceIdentities.For(nextVehicle);

        if (!ReferenceEquals(_activeVehicle, nextVehicle) ||
            !ReferenceEquals(_activeFlightComputer, nextFlightComputer) ||
            !Nullable.Equals(ActiveVehicle.VehicleId, nextVehicleId) ||
            ActiveVehicle.Controllable != nextControllable)
        {
            _authorityEpoch = _authorityEpoch.Next();
        }

        _activeVehicle = nextVehicle;
        _activeFlightComputer = nextFlightComputer;
        ActiveVehicle = new ActiveVehicleObservation(
            nextVehicleId,
            GetName(nextVehicle),
            nextControllable,
            nextControllable ? AuthorityIdentity.KsaLocalControlledInput : null,
            nextVehicleInstance,
            nextFlightComputer is null ? null : FlightComputerInstanceIdentities.For(nextFlightComputer),
            _authorityEpoch);
    }

    internal static bool HasVerifiedControlAuthority(bool? vehicleControllable) =>
        vehicleControllable == true;

    public VehicleState? CaptureActiveVehicle()
    {
        AssertMainThread();
        var vehicle = _activeVehicle;
        var activeVehicleId = ActiveVehicle.VehicleId?.Value;
        if (vehicle is null || activeVehicleId is null) return null;

        var parent = ReflectionAccess.GetOrInvoke(vehicle, "Parent", "GetParent");
        var target = ReflectionAccess.GetOrInvoke(vehicle, "Target", "TargetOrbiter", "GetTarget");
        var orbit = ReflectionAccess.GetOrInvoke(vehicle, "Orbit", "GetOrbit");
        var navBall = ReflectionAccess.GetOrInvoke(vehicle, "NavBallData", "GetNavBallData");
        var bodyToCci = ReadQuaternion(ReflectionAccess.GetOrInvoke(vehicle, "GetBody2Cci", "Body2Cci", "BodyToCci"));
        var accelerationBody = ReadVector(ReflectionAccess.GetOrInvoke(vehicle, "AccelerationBody"));
        return new VehicleState
        {
            Id = activeVehicleId,
            Name = ActiveVehicle.DisplayName ?? activeVehicleId,
            Situation = ReflectionAccess.ToText(ReflectionAccess.GetOrInvoke(vehicle, "Situation", "GetSituation")),
            Controllable = ActiveVehicle.Controllable,
            ParentId = GetId(parent),
            TargetId = GetId(target),
            Target = CaptureTarget(target),
            PositionCciM = ReadVector(ReflectionAccess.GetOrInvoke(vehicle, "GetPositionCci", "PositionCci")),
            PositionCceM = ReadVector(ReflectionAccess.GetOrInvoke(vehicle, "GetPositionCce", "PositionCce")),
            PositionOrbM = ReadVector(ReflectionAccess.GetOrInvoke(vehicle, "GetPositionOrb", "PositionOrb")),
            VelocityCciMps = ReadVector(ReflectionAccess.GetOrInvoke(vehicle, "GetVelocityCci", "VelocityCci")),
            VelocityCceMps = ReadVector(ReflectionAccess.GetOrInvoke(vehicle, "GetVelocityCce", "VelocityCce")),
            VelocityOrbMps = ReadVector(ReflectionAccess.GetOrInvoke(vehicle, "GetVelocityOrb", "VelocityOrb")),
            TotalAccelerationCciMps2 = accelerationBody is { } acceleration && bodyToCci is { } rotation
                ? Rotate(acceleration, rotation)
                : null,
            TotalAccelerationCceMps2 = ReadVector(ReflectionAccess.GetOrInvoke(vehicle, "GetAccelerationCce", "AccelerationCce")),
            TotalAccelerationOrbMps2 = ReadVector(ReflectionAccess.GetOrInvoke(vehicle, "GetAccelerationOrb", "AccelerationOrb")),
            BodyToCci = bodyToCci,
            BodyRatesRadps = ReadVector(ReflectionAccess.GetOrInvoke(vehicle, "BodyRates", "AngularVelocity", "AttitudeRates") ?? ReflectionAccess.Get(navBall, "AttitudeRates")),
            InertialSpeedMps = ReadDouble(vehicle, "GetInertialSpeed", "InertialSpeed"),
            SurfaceSpeedMps = ReadDouble(vehicle, "GetSurfaceSpeed", "SurfaceSpeed"),
            BarometricAltitudeM = ReadDouble(vehicle, "GetBarometricAltitude", "BarometricAltitude"),
            RadarAltitudeM = ReadDouble(vehicle, "GetRadarAltitude", "RadarAltitude"),
            TotalMassKg = ReadDouble(vehicle, "TotalMass", "GetTotalMass"),
            InertMassKg = ReadDouble(vehicle, "InertMass", "GetInertMass"),
            PropellantMassKg = ReadDouble(vehicle, "PropellantMass", "GetPropellantMass"),
            Orbit = ReadOrbit(orbit),
        };
    }

    public TelemetrySnapshot CaptureTelemetry(double simulationTimeS)
    {
        AssertMainThread();
        if (_telemetry is null || _activeVehicle is not KSA.Vehicle vehicle ||
            ActiveVehicle.VehicleId?.Value is not { } vehicleId)
        {
            return default;
        }

        var aggregateLimits = _propulsionTelemetry?.Capture(vehicle) ?? default;
        var snapshot = _telemetry.Capture(
            vehicle,
            vehicleId,
            ActiveVehicle.DisplayName ?? vehicleId,
            GetId(vehicle.Parent),
            ActiveVehicle.Controllable,
            simulationTimeS,
            _landingSite,
            aggregateLimits);
        var navigation = CaptureActiveVehicle();
        if (snapshot.Vehicle is not { } dynamics || navigation is null) return snapshot;
        return snapshot with
        {
            Vehicle = dynamics with
            {
                TargetId = navigation.TargetId,
                Target = navigation.Target,
                PositionOrbM = navigation.PositionOrbM,
                VelocityOrbMps = navigation.VelocityOrbMps,
                TotalAccelerationCceMps2 = navigation.TotalAccelerationCceMps2,
                TotalAccelerationOrbMps2 = navigation.TotalAccelerationOrbMps2,
                InertialSpeedMps = navigation.InertialSpeedMps,
                SurfaceSpeedMps = navigation.SurfaceSpeedMps,
                Orbit = navigation.Orbit,
            },
        };
    }

    public ManualControlState? CaptureManualControl()
    {
        AssertMainThread();
        var inputs = ReflectionAccess.GetOrInvoke(
                         _activeVehicle,
                         "_manualControlInputs",
                         "ManualControlInputs",
                         "ManualInputs",
                         "CurrentManualInputs",
                         "ControlInputs") ??
                     ReflectionAccess.GetOrInvoke(
                         _activeFlightComputer,
                         "ManualControlInputs",
                         "ManualInputs",
                         "CurrentManualInputs",
                         "ControlInputs");
        if (inputs is null) return null;

        var engineOn = ReflectionAccess.ToBoolean(ReflectionAccess.Get(inputs, "EngineOn"));
        var throttle = ReflectionAccess.ToFiniteDouble(ReflectionAccess.Get(inputs, "EngineThrottle", "Throttle"));
        if (throttle is < 0 or > 1) throttle = null;
        var flags = ReflectionAccess.Get(inputs, "ThrusterCommandFlags");
        var pitch = ReadAxis(flags, "PitchUp", "PitchDown");
        var yaw = ReadAxis(flags, "YawRight", "YawLeft");
        var roll = ReadAxis(flags, "RollRight", "RollLeft");
        var forward = ReadAxis(flags, "TranslateForward", "TranslateBackward");
        var right = ReadAxis(flags, "TranslateRight", "TranslateLeft");
        var up = ReadAxis(flags, "TranslateUp", "TranslateDown");

        if (engineOn is null && throttle is null && pitch is null && yaw is null && roll is null &&
            forward is null && right is null && up is null)
        {
            return null;
        }

        return new ManualControlState
        {
            EngineOn = engineOn,
            Throttle = throttle,
            Pitch = pitch,
            Yaw = yaw,
            Roll = roll,
            TranslateForward = forward,
            TranslateRight = right,
            TranslateUp = up,
        };
    }

    public CatalogPayload CaptureCatalog()
    {
        AssertMainThread();
        var targets = new Dictionary<string, object>(StringComparer.Ordinal);

        var bodies = new List<EntityReference>();
        var currentSystem = ReflectionAccess.GetOrInvokeStatic(_universeType, "CurrentSystem", "GetCurrentSystem");
        var celestialAll = ReflectionAccess.GetOrInvoke(currentSystem, "All");
        var currentEntities = ReflectionAccess.Enumerate(celestialAll).ToArray();
        foreach (var body in currentEntities)
        {
            if (_vehicleType?.IsInstanceOfType(body) == true) continue;
            AddEntity(body, "body", bodies, targets);
        }

        var candidateVehicles = new List<EntityReference>();
        var vehicleTargets = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var vehicle in currentEntities)
        {
            if (_vehicleType?.IsInstanceOfType(vehicle) != true ||
                ReflectionAccess.ToBoolean(ReflectionAccess.Get(vehicle, "IsDisposed")) != false)
            {
                continue;
            }

            var id = GetId(vehicle);
            if (id is null) continue;
            if (!vehicleTargets.TryAdd(id, vehicle))
            {
                throw new InvalidOperationException(
                    $"The current KSA system exposed duplicate vehicle ID '{id}'.");
            }
            candidateVehicles.Add(new EntityReference
            {
                Id = id,
                Name = GetName(vehicle) ?? id,
                Kind = "vehicle",
            });
        }

        var activeVehicleId = ActiveVehicle.VehicleId?.Value;
        var selection = VehicleCatalogPolicy.Select(activeVehicleId, candidateVehicles);
        if (selection.OmittedCount != _lastOmittedVehicleCount)
        {
            _lastOmittedVehicleCount = selection.OmittedCount;
            if (selection.OmittedCount > 0)
            {
                try
                {
                    _diagnosticSink(
                        $"KittenIO omitted {selection.OmittedCount} loaded KSA vehicle(s) from the bounded 64-entry v1 catalog.");
                }
                catch (Exception)
                {
                    // A diagnostic sink must never make authoritative catalog capture fail.
                }
            }
        }
        foreach (var vehicle in selection.Vehicles)
        {
            if (targets.ContainsKey(vehicle.Id))
            {
                throw new InvalidOperationException(
                    $"The current KSA system exposed duplicate catalog ID '{vehicle.Id}'.");
            }
            targets.Add(vehicle.Id, vehicleTargets[vehicle.Id]);
        }

        _targets.Replace(targets);

        return new CatalogPayload
        {
            Bodies = bodies.OrderBy(item => item.Name, StringComparer.OrdinalIgnoreCase).ToArray(),
            Vehicles = selection.Vehicles,
        };
    }

    public void ResetCatalogTargets()
    {
        AssertMainThread();
        _targets.Replace(new Dictionary<string, object>(StringComparer.Ordinal));
    }

    public void ResetLandingReference()
    {
        AssertMainThread();
        _landingSite = null;
    }

    public double? GetSimulationTime()
    {
        AssertMainThread();
        return ReflectionAccess.ToFiniteDouble(
            ReflectionAccess.GetOrInvokeStatic(_universeType, "GetElapsedSeconds"));
    }

    public double? GetSimulationSpeed()
    {
        AssertMainThread();
        var direct = ReflectionAccess.GetOrInvokeStatic(_universeType, "SimulationSpeed");
        var currentSystem = ReflectionAccess.GetOrInvokeStatic(_universeType, "CurrentSystem", "GetCurrentSystem");
        return ReflectionAccess.ToFiniteDouble(direct ?? ReflectionAccess.GetOrInvoke(currentSystem, "SimulationSpeed", "GetSimulationSpeed"));
    }

    public long? GetFrameNumber()
    {
        AssertMainThread();
        var value = ReflectionAccess.ToFiniteDouble(ReflectionAccess.GetOrInvokeStatic(_programType, "FrameNumber", "CurrentFrameNumber"));
        return value is { } number && number >= 0 && number <= long.MaxValue ? (long)number : null;
    }

    public TimeWarpObservation ObserveTimeWarp()
    {
        AssertMainThread();
        return _timeWarp?.Observe() ?? new TimeWarpObservation(false, false, false, null);
    }

    public TimeWarpCommandResult RequestTimeWarp(TimeWarpCommandKind kind, double? rate)
    {
        AssertMainThread();
        return _timeWarp?.Request(kind, rate) ?? TimeWarpCommandResult.Failure(
            "time_warp_unavailable",
            TimeWarpContractFailureReason.Length == 0
                ? "The pinned KSA time-warp command contract is unavailable."
                : TimeWarpContractFailureReason);
    }

    public GameCommandResult ActivateNextSequence()
    {
        AssertMainThread();
        if (_activeVehicle is null) return GameCommandResult.Failure("no_active_vehicle", "No controlled vehicle is active.");

        try
        {
            var parts = ReflectionAccess.GetOrInvoke(_activeVehicle, "Parts", "GetParts");
            var sequenceList = ReflectionAccess.GetOrInvoke(parts, "SequenceList", "Sequences") ??
                               ReflectionAccess.GetOrInvoke(_activeVehicle, "SequenceList", "Sequences");
            if (ReflectionAccess.TryInvoke(sequenceList, "ActivateNextSequence", [_activeVehicle], out _) ||
                ReflectionAccess.TryInvoke(sequenceList, "ActivateNextSequence", [], out _) ||
                ReflectionAccess.TryInvokeStatic(_sequenceListType, "ActivateNextSequence", [_activeVehicle], out _) ||
                ReflectionAccess.TryInvoke(_activeVehicle, "ActivateNextSequence", [], out _))
            {
                return GameCommandResult.Success(new { activated = true, vehicle_id = ActiveVehicle.VehicleId?.Value });
            }

            return GameCommandResult.Failure("stage_unavailable", "This KSA build does not expose a compatible sequence activation API.");
        }
        catch (Exception exception) when (exception is TargetInvocationException or ArgumentException)
        {
            return GameCommandResult.Failure("stage_failed", exception.InnerException?.Message ?? exception.Message);
        }
    }

    public GameCommandResult SetTarget(string targetId)
    {
        AssertMainThread();
        if (_activeVehicle is null) return GameCommandResult.Failure("no_active_vehicle", "No controlled vehicle is active.");
        // Target IDs are session/catalog scoped. Side effects are rare, so
        // rebuild from current KSA state before every lookup rather than ever
        // trusting a retained object from a previous capture or session.
        CaptureCatalog();
        if (!_targets.TryResolve(targetId, out var target) || target is null)
        {
            return GameCommandResult.Failure("target_not_found", $"Target '{targetId}' is not present in the current catalog.");
        }

        try
        {
            if (!ReflectionAccess.TryInvoke(_activeVehicle, "SetTarget", [target, null], out _) &&
                !ReflectionAccess.TryInvoke(_activeVehicle, "SetTarget", [target], out _))
            {
                return GameCommandResult.Failure("target_unavailable", "This KSA build does not expose a compatible SetTarget API.");
            }

            return GameCommandResult.Success(new { target_id = targetId });
        }
        catch (Exception exception) when (exception is TargetInvocationException or ArgumentException)
        {
            return GameCommandResult.Failure("target_failed", exception.InnerException?.Message ?? exception.Message);
        }
    }

    public GameCommandResult SetLandingSite(LandingSiteDefinition site)
    {
        AssertMainThread();
        if (_activeVehicle is not KSA.Vehicle vehicle)
        {
            return GameCommandResult.Failure("no_active_vehicle", "No controlled vehicle is active.");
        }
        if (!StringComparer.Ordinal.Equals(GetId(vehicle.Parent), site.BodyId))
        {
            return GameCommandResult.Failure("wrong_parent_body", "The landing site body_id is not the active vehicle's parent body.");
        }
        if (!IsFinite(site.PositionCcfM) || MagnitudeSquared(site.PositionCcfM) <= 1e-12 ||
            site.LatitudeDeg is { } latitude && (!double.IsFinite(latitude) || latitude is < -90 or > 90) ||
            site.LongitudeDeg is { } longitude && (!double.IsFinite(longitude) || longitude is < -180 or > 180) ||
            site.AltitudeM is { } altitude && !double.IsFinite(altitude))
        {
            return GameCommandResult.Failure("invalid_landing_site", "Landing-site values must be finite and within their documented ranges.");
        }

        _landingSite = site;
        return GameCommandResult.Success(site);
    }

    public GameCommandResult ValidateActiveVehicleSwitchTarget(string vehicleId)
    {
        AssertMainThread();
        return _vehicleSwitcher?.ValidateTarget(vehicleId) ?? GameCommandResult.Failure(
            "switch_unavailable",
            ActiveVehicleSwitchContractFailureReason);
    }

    public VehicleSwitchRequestResult RequestActiveVehicleSwitch(string vehicleId)
    {
        AssertMainThread();
        return _vehicleSwitcher?.Request(vehicleId) ?? VehicleSwitchRequestResult.Failure(
            "switch_unavailable",
            ActiveVehicleSwitchContractFailureReason);
    }

    private void AddEntity(
        object entity,
        string kind,
        ICollection<EntityReference> destination,
        IDictionary<string, object> targets)
    {
        var id = GetId(entity);
        if (id is null || targets.ContainsKey(id)) return;
        targets[id] = entity;
        destination.Add(new EntityReference { Id = id, Name = GetName(entity) ?? id, Kind = kind });
    }

    private string? GetId(object? entity)
    {
        if (entity is null) return null;
        var explicitId = ReflectionAccess.ToText(ReflectionAccess.GetOrInvoke(entity, "Id", "ID", "Identifier", "PersistentId", "Guid"));
        if (!string.IsNullOrWhiteSpace(explicitId)) return explicitId;
        return _fallbackIds.GetId(entity);
    }

    private static string? GetName(object? entity) =>
        ReflectionAccess.ToText(ReflectionAccess.GetOrInvoke(entity, "Name", "DisplayName", "Label"));

    private void AssertMainThread()
    {
        var current = Environment.CurrentManagedThreadId;
        Interlocked.CompareExchange(ref _mainThreadId, current, 0);
        if (current != Volatile.Read(ref _mainThreadId))
        {
            throw new InvalidOperationException("KSA game state was accessed outside the StarMap main-thread callback.");
        }
    }

    private static string GetFileVersion(Assembly assembly)
    {
        var location = assembly.Location;
        if (!string.IsNullOrWhiteSpace(location))
        {
            var version = FileVersionInfo.GetVersionInfo(location).ProductVersion;
            if (!string.IsNullOrWhiteSpace(version)) return version;
        }

        return assembly.GetName().Version?.ToString() ?? "unknown";
    }

    private static double? ReadDouble(object? source, params string[] names) =>
        ReflectionAccess.ToFiniteDouble(ReflectionAccess.GetOrInvoke(source, names));

    private static Vector3Value? ReadVector(object? value)
    {
        if (value is null) return null;
        var x = ReadDouble(value, "X", "x");
        var y = ReadDouble(value, "Y", "y");
        var z = ReadDouble(value, "Z", "z");
        return x is { } xv && y is { } yv && z is { } zv ? new(xv, yv, zv) : null;
    }

    private static bool IsFinite(Vector3Value value) =>
        double.IsFinite(value.X) && double.IsFinite(value.Y) && double.IsFinite(value.Z);

    private static double MagnitudeSquared(Vector3Value value) =>
        value.X * value.X + value.Y * value.Y + value.Z * value.Z;

    private static QuaternionValue? ReadQuaternion(object? value)
    {
        if (value is null) return null;
        var x = ReadDouble(value, "X", "x");
        var y = ReadDouble(value, "Y", "y");
        var z = ReadDouble(value, "Z", "z");
        var w = ReadDouble(value, "W", "w");
        return x is { } xv && y is { } yv && z is { } zv && w is { } wv ? new(xv, yv, zv, wv) : null;
    }

    private TargetState? CaptureTarget(object? target)
    {
        var id = GetId(target);
        if (target is null || id is null) return null;
        var orbit = ReflectionAccess.GetOrInvoke(target, "Orbit", "GetOrbit");
        return new TargetState
        {
            Id = id,
            Name = GetName(target) ?? id,
            Kind = _vehicleType?.IsInstanceOfType(target) == true ? "vehicle" : "body",
            PositionCciM = ReadVector(ReflectionAccess.GetOrInvoke(target, "GetPositionCci", "PositionCci")),
            VelocityCciMps = ReadVector(ReflectionAccess.GetOrInvoke(target, "GetVelocityCci", "VelocityCci")),
            Orbit = ReadOrbit(orbit),
        };
    }

    private static AxisCommand? ReadAxis(object? flagsValue, string positiveName, string negativeName)
    {
        if (flagsValue is null || !flagsValue.GetType().IsEnum) return null;
        var enumType = flagsValue.GetType();
        var flags = Convert.ToUInt64(flagsValue);
        var positive = Enum.TryParse(enumType, positiveName, false, out var positiveValue) && positiveValue is not null
            ? Convert.ToUInt64(positiveValue)
            : 0;
        var negative = Enum.TryParse(enumType, negativeName, false, out var negativeValue) && negativeValue is not null
            ? Convert.ToUInt64(negativeValue)
            : 0;
        if (positive == 0 || negative == 0) return null;
        var positiveSet = (flags & positive) != 0;
        var negativeSet = (flags & negative) != 0;
        if (positiveSet == negativeSet) return positiveSet ? null : AxisCommand.Neutral;
        return positiveSet ? AxisCommand.Positive : AxisCommand.Negative;
    }

    private static Vector3Value Rotate(Vector3Value vector, QuaternionValue rotation)
    {
        var dotUv = rotation.X * vector.X + rotation.Y * vector.Y + rotation.Z * vector.Z;
        var dotUu = rotation.X * rotation.X + rotation.Y * rotation.Y + rotation.Z * rotation.Z;
        var crossX = rotation.Y * vector.Z - rotation.Z * vector.Y;
        var crossY = rotation.Z * vector.X - rotation.X * vector.Z;
        var crossZ = rotation.X * vector.Y - rotation.Y * vector.X;
        var scale = rotation.W * rotation.W - dotUu;
        return new Vector3Value(
            2 * dotUv * rotation.X + scale * vector.X + 2 * rotation.W * crossX,
            2 * dotUv * rotation.Y + scale * vector.Y + 2 * rotation.W * crossY,
            2 * dotUv * rotation.Z + scale * vector.Z + 2 * rotation.W * crossZ);
    }

    private static OrbitState? ReadOrbit(object? orbit)
    {
        if (orbit is null) return null;
        return new OrbitState
        {
            Eccentricity = ReadDouble(orbit, "Eccentricity"),
            SemiMajorAxisM = ReadDouble(orbit, "SemiMajorAxis", "SemiMajorAxisM"),
            InclinationRad = ReadDouble(orbit, "Inclination"),
            LongitudeAscendingNodeRad = ReadDouble(orbit, "LongitudeAscendingNode", "LongitudeOfAscendingNode", "LAN"),
            ArgumentPeriapsisRad = ReadDouble(orbit, "ArgumentPeriapsis", "ArgumentOfPeriapsis"),
            PeriapsisM = ReadDouble(orbit, "Periapsis", "PeriapsisRadius"),
            ApoapsisM = ReadDouble(orbit, "Apoapsis", "ApoapsisRadius"),
            PeriodS = ReadDouble(orbit, "Period", "OrbitalPeriod"),
        };
    }
}
