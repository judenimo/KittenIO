using System.Reflection;
using Brutal.Numerics;
using KittenIO.Protocol;

namespace KittenIO.Mod.Game;

/// <summary>
/// Exact-build, game-thread-only canonical telemetry projection. Every KSA value used
/// here is guarded by the reflection contract suite and the pinned DLL hash.
/// Derived vectors are computed only from authoritative values captured in the
/// same callback; unavailable values remain null.
/// </summary>
internal sealed class PinnedKsaTelemetry
{
    private const double StandardGravityMps2 = 9.80665d;
    private string? _contactVehicleId;
    private bool _hadGroundContact;
    private long _touchdownSequence;
    private double? _touchdownVerticalSpeedMps;

    private PinnedKsaTelemetry()
    {
    }

    public static bool TryCreate(
        Assembly assembly,
        out PinnedKsaTelemetry? telemetry,
        out string unavailableReason)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        if (!PinnedKsaPropulsionTelemetry.HasPinnedBinaryIdentity(assembly))
        {
            telemetry = null;
            unavailableReason = $"Extended telemetry is pinned to the exact KSA {PinnedKsaContract.GameVersion} assembly.";
            return false;
        }

        telemetry = new PinnedKsaTelemetry();
        unavailableReason = string.Empty;
        return true;
    }

    public TelemetrySnapshot Capture(
        KSA.Vehicle vehicle,
        string vehicleId,
        string vehicleName,
        string? parentId,
        bool controllable,
        double simulationTimeS,
        LandingSiteDefinition? landingSite,
        PropulsionTelemetrySnapshot aggregateLimits)
    {
        ArgumentNullException.ThrowIfNull(vehicle);
        try
        {
            var positionCci = vehicle.GetPositionCci();
            var positionCce = vehicle.GetPositionCce();
            var velocityCci = vehicle.GetVelocityCci();
            var velocityCce = vehicle.GetVelocityCce();
            var surfaceVelocityCci = vehicle.GetSurfaceVelocityCci();
            var bodyToCci = vehicle.GetBody2Cci();
            var totalAccelerationBody = vehicle.AccelerationBody;
            var gravityBub = vehicle.PhysicsEnvironment.GravitationBub;
            var gravityCci = Rotate(gravityBub, vehicle.BubbleOrigin.GetBub2Cci());
            var gravityCce = Rotate(gravityCci, vehicle.Parent.GetCci2Cce());
            var gravityBody = RotateInverse(gravityCci, bodyToCci);
            var nonGravityBody = totalAccelerationBody - gravityBody;
            var nonGravityCci = Rotate(nonGravityBody, bodyToCci);

            var vehicleDynamics = new VehicleState
            {
                Id = vehicleId,
                Name = vehicleName,
                ParentId = parentId,
                Situation = vehicle.Situation.ToString(),
                Controllable = controllable,
                PositionCciM = Vector(positionCci),
                PositionCceM = Vector(positionCce),
                VelocityCciMps = Vector(velocityCci),
                VelocityCceMps = Vector(velocityCce),
                SurfaceVelocityCciMps = Vector(surfaceVelocityCci),
                BodyToCci = Quaternion(bodyToCci),
                BodyRatesRadps = Vector(vehicle.BodyRates),
                AngularAccelerationBodyRadps2 = Vector(vehicle.AngularAccelerationBody),
                TotalAccelerationBodyMps2 = Vector(totalAccelerationBody),
                TotalAccelerationCciMps2 = Vector(Rotate(totalAccelerationBody, bodyToCci)),
                GravityCciMps2 = Vector(gravityCci),
                GravityCceMps2 = Vector(gravityCce),
                NonGravitationalAccelerationBodyMps2 = Vector(nonGravityBody),
                NonGravitationalAccelerationCciMps2 = Vector(nonGravityCci),
                BarometricAltitudeM = FiniteNonnegative(vehicle.GetBarometricAltitude()),
                RadarAltitudeM = FiniteNonnegative(vehicle.GetRadarAltitude()),
                TotalMassKg = FiniteNonnegative(vehicle.TotalMass),
                InertMassKg = FiniteNonnegative(vehicle.InertMass),
                PropellantMassKg = FiniteNonnegative(vehicle.PropellantMass),
                ParentBody = parentId is null ? null : new CelestialBodyDynamics
                {
                    Id = parentId,
                    GravitationalParameterM3Ps2 = FiniteNonnegative(vehicle.Parent.Mu),
                    MeanRadiusM = FiniteNonnegative(vehicle.Parent.GetNearSurfaceRadius()),
                    RotationRateRadps = FiniteNonnegative(vehicle.Parent.GetAngularVelocity()),
                    RotationAxisCce = UnitOrNull(vehicle.Parent.GetAngularVelocityCce()),
                },
            };

            var propulsion = CapturePropulsion(vehicle, bodyToCci, aggregateLimits);
            var rigidBody = CaptureRigidBody(vehicle, bodyToCci, totalAccelerationBody, propulsion);
            var atmosphere = CaptureAtmosphere(vehicle, bodyToCci);
            var surface = CaptureSurface(vehicle, vehicleId, positionCci, surfaceVelocityCci, bodyToCci, propulsion);
            var landing = landingSite is null || !StringComparer.Ordinal.Equals(parentId, landingSite.BodyId)
                ? null
                : CaptureLandingReference(vehicle, landingSite, simulationTimeS, gravityCci, surface);
            return new(vehicleDynamics, propulsion.Telemetry, rigidBody, atmosphere, surface, landing);
        }
        catch (Exception exception) when (IsUnavailableKsaAccess(exception))
        {
            return default;
        }
    }

    private static PropulsionCapture CapturePropulsion(
        KSA.Vehicle vehicle,
        doubleQuat bodyToCci,
        PropulsionTelemetrySnapshot aggregateLimits)
    {
        var nozzleCaptures = new List<NozzleCapture>();
        foreach (var pair in vehicle.Parts.RocketNozzles.ModulesAndStates)
        {
            var performance = pair.State.Performance;
            if (!IsFiniteNonnegative(performance.TotalThrust) ||
                !IsFiniteNonnegative(performance.MassFlowRate) ||
                !IsFinite(pair.State.ThrustDirectionVehicleAsmb) ||
                !IsFinite(pair.State.ThrustLocationVehicleAsmb))
            {
                continue;
            }

            var forceAsmb = pair.State.ThrustDirectionVehicleAsmb * performance.TotalThrust;
            var forceCci = Rotate(ToDouble(forceAsmb), vehicle.GetAsmb2Cci());
            var forceBody = RotateInverse(forceCci, bodyToCci);
            var locationBody = ToDouble(vehicle.PosAsmbToBody(pair.State.ThrustLocationVehicleAsmb));
            nozzleCaptures.Add(new(
                pair.Module.Rocket.Core,
                forceBody,
                forceCci,
                locationBody,
                performance.MassFlowRate,
                performance.EffectiveExhaustVelocity));
        }

        var engines = new List<EngineTelemetry>();
        if (KSA.ModuleStateful<
                KSA.EngineController,
                KSA.EngineControllerState,
                KSA.EngineControllerGlobalState,
                KSA.EmptyStruct>.TryGetFrom(vehicle.Parts.States, out var engineStates))
        {
            foreach (var pair in engineStates.ModulesAndStates)
            {
                var cores = pair.Module.Cores;
                var engineNozzles = nozzleCaptures
                    .Where(nozzle => cores.Contains(nozzle.Core, ReferenceEqualityComparer.Instance))
                    .ToArray();
                var forceBody = Sum(engineNozzles.Select(item => item.ForceBody));
                var forceCci = Sum(engineNozzles.Select(item => item.ForceCci));
                var massFlow = engineNozzles.Sum(item => item.MassFlowRateKgps);
                var thrust = Length(forceBody);
                var appliedThrottles = cores
                    .Select(core => (double)vehicle.Parts.RocketCores.GetState(core).Throttle)
                    .Where(double.IsFinite)
                    .ToArray();
                var weightedPosition = WeightedPosition(engineNozzles);
                var effectiveExhaustVelocity = massFlow > 0 ? thrust / massFlow : (double?)null;
                engines.Add(new EngineTelemetry
                {
                    Id = pair.Module.InstanceId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Name = pair.Module.TemplateId,
                    StagedEnabled = pair.Module.IsActive,
                    Ignited = massFlow > 0 && thrust > 0,
                    PropellantAvailable = pair.State.IsPropellantAvailable,
                    RequestedThrottle = FiniteUnit(pair.State.CommandThrottle),
                    AppliedThrottle = appliedThrottles.Length == 0 ? null : appliedThrottles.Average(),
                    MinimumThrottle = FiniteUnit(pair.Module.MinimumThrottle),
                    MaximumThrottle = 1d,
                    CurrentThrustN = FiniteNonnegative(thrust),
                    MaximumAvailableThrustN = null,
                    CurrentThrustBodyN = Vector(forceBody),
                    CurrentThrustCciN = Vector(forceCci),
                    ThrustPositionBodyM = weightedPosition is { } position ? Vector(position) : null,
                    NominalThrustDirectionBody = null,
                    GimbalAnglesRad = null,
                    GimbalLimitsRad = null,
                    SpecificImpulseS = effectiveExhaustVelocity is { } ve ? ve / StandardGravityMps2 : null,
                    MassFlowRateKgps = FiniteNonnegative(massFlow),
                    RestartAvailable = null,
                    RemainingIgnitions = null,
                    GimbalRateLimitsRadps = null,
                    IgnitionDelayS = null,
                    ShutdownDelayS = null,
                    ResponseTimeConstantS = null,
                    AmbientPressurePa = aggregateLimits.AmbientPressurePa,
                });
            }
        }

        var gimbals = new List<GimbalActuatorTelemetry>();
        foreach (var pair in vehicle.Parts.Gimbals.ModulesAndStates)
        {
            if (!float.IsFinite(pair.State.AngleY) || !float.IsFinite(pair.State.AngleZ) ||
                !float.IsFinite(pair.Module.AxisY.MaxAngle) || !float.IsFinite(pair.Module.AxisZ.MaxAngle))
            {
                continue;
            }
            gimbals.Add(new GimbalActuatorTelemetry
            {
                Id = pair.Module.InstanceId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ParentPartId = pair.Module.Parent.InstanceId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                AngleYRad = pair.State.AngleY,
                AngleZRad = pair.State.AngleZ,
                LimitYRad = Math.Abs(pair.Module.AxisY.MaxAngle),
                LimitZRad = Math.Abs(pair.Module.AxisZ.MaxAngle),
                RateLimitYRadps = null,
                RateLimitZRadps = null,
            });
        }

        var totalForceBody = Sum(nozzleCaptures.Select(item => item.ForceBody));
        var totalForceCci = Sum(nozzleCaptures.Select(item => item.ForceCci));
        var totalMassFlow = nozzleCaptures.Sum(item => item.MassFlowRateKgps);
        var totalThrust = Length(totalForceBody);
        var maximumForceCci = aggregateLimits.MaximumAvailableThrustVehicleAssemblyN is { } maximumForceAsmb
            ? Rotate(ToDouble(maximumForceAsmb), vehicle.GetAsmb2Cci())
            : (double3?)null;
        var maximumForceBody = maximumForceCci is { } maximumCci
            ? RotateInverse(maximumCci, bodyToCci)
            : (double3?)null;
        var telemetry = new PropulsionTelemetry
        {
            RequestedThrottle = engines.Count == 0 ? null : Average(engines, static engine => engine.RequestedThrottle),
            AppliedThrottle = engines.Count == 0 ? null : Average(engines, static engine => engine.AppliedThrottle),
            MinimumThrottle = FiniteUnit(vehicle.GetMinThrottle()),
            MaximumThrottle = 1d,
            CurrentThrustBodyN = Vector(totalForceBody),
            CurrentThrustCciN = Vector(totalForceCci),
            MaximumAvailableThrustN = aggregateLimits.MaximumAvailableThrustN,
            MaximumAvailableThrustBodyN = maximumForceBody is { } maximumBody ? Vector(maximumBody) : null,
            MaximumAvailableThrustCciN = maximumForceCci is { } maximumInertial ? Vector(maximumInertial) : null,
            MassFlowRateKgps = FiniteNonnegative(totalMassFlow),
            EffectiveSpecificImpulseS = totalMassFlow > 0 ? totalThrust / (totalMassFlow * StandardGravityMps2) : null,
            Engines = engines,
            Gimbals = gimbals,
        };
        return new(telemetry, nozzleCaptures, totalForceBody);
    }

    private static RigidBodyTelemetry CaptureRigidBody(
        KSA.Vehicle vehicle,
        doubleQuat bodyToCci,
        double3 totalAccelerationBody,
        PropulsionCapture propulsion)
    {
        var mass = vehicle.TotalMassPropsBody;
        var inertia = mass.Inertia;
        var centerAsmb = vehicle.CenterOfMassAsmb;
        var centerBody = vehicle.PosAsmbToBody(centerAsmb);
        var momentBody = Sum(propulsion.Nozzles.Select(item => Cross(item.LocationBody - centerBody, item.ForceBody)));
        var totalForceBody = totalAccelerationBody * vehicle.TotalMass;
        var totalForceCci = Rotate(totalForceBody, bodyToCci);
        var angularMomentumBody = Multiply(inertia, vehicle.BodyRates);
        var angularAccelerationMoment = Multiply(inertia, vehicle.AngularAccelerationBody);
        var totalMomentBody = angularAccelerationMoment + Cross(vehicle.BodyRates, angularMomentumBody);
        return new RigidBodyTelemetry
        {
            CenterOfMassVehicleAssemblyM = Vector(centerAsmb),
            CenterOfMassBodyM = Vector(centerBody),
            InertiaBodyKgM2 = IsFinite(inertia.XX) && IsFinite(inertia.YX) && IsFinite(inertia.YY) &&
                IsFinite(inertia.ZX) && IsFinite(inertia.ZY) && IsFinite(inertia.ZZ)
                ? new Matrix3SymmetricValue
                {
                    Xx = inertia.XX,
                    Xy = inertia.YX,
                    Xz = inertia.ZX,
                    Yy = inertia.YY,
                    Yz = inertia.ZY,
                    Zz = inertia.ZZ,
                }
                : null,
            TotalForceBodyN = Vector(totalForceBody),
            TotalForceCciN = Vector(totalForceCci),
            TotalMomentBodyNm = Vector(totalMomentBody),
            PropulsiveForceBodyN = Vector(propulsion.TotalForceBody),
            PropulsiveMomentBodyNm = Vector(momentBody),
            AerodynamicForceBodyN = null,
            AerodynamicMomentBodyNm = null,
            RcsForceBodyN = null,
            RcsMomentBodyNm = null,
            PositiveTorqueAuthorityBodyNm = null,
            NegativeTorqueAuthorityBodyNm = null,
            TvcTorqueAuthorityBodyNm = null,
            RcsTorqueAuthorityBodyNm = null,
        };
    }

    private static double3 Multiply(BepuUtilities.Symmetric3x3 matrix, double3 vector) => new(
        matrix.XX * vector.X + matrix.YX * vector.Y + matrix.ZX * vector.Z,
        matrix.YX * vector.X + matrix.YY * vector.Y + matrix.ZY * vector.Z,
        matrix.ZX * vector.X + matrix.ZY * vector.Y + matrix.ZZ * vector.Z);

    private static AtmosphereTelemetry CaptureAtmosphere(KSA.Vehicle vehicle, doubleQuat bodyToCci)
    {
        var physics = vehicle.GetPhysicsStates();
        var airBody = ToDouble(physics.ComputeAirVelocityBody());
        var airCci = Rotate(airBody, bodyToCci);
        var airCce = Rotate(airCci, vehicle.Parent.GetCci2Cce());
        return new AtmosphereTelemetry
        {
            PressurePa = FiniteNonnegative(vehicle.PhysicsEnvironment.AtmosphericPressure),
            DensityKgPm3 = FiniteNonnegative(vehicle.PhysicsEnvironment.AtmosphericDensity),
            DynamicPressurePa = FiniteNonnegative(KSA.PhysicalAtmosphereReference.GetDynamicPressure(vehicle)),
            AirRelativeVelocityBodyMps = Vector(airBody),
            AirRelativeVelocityCciMps = Vector(airCci),
            AirRelativeVelocityCceMps = Vector(airCce),
            TemperatureK = null,
            SpeedOfSoundMps = null,
            Mach = null,
            WindVelocityCciMps = null,
            AngleOfAttackRad = null,
            SideslipAngleRad = null,
        };
    }

    private SurfaceTelemetry CaptureSurface(
        KSA.Vehicle vehicle,
        string vehicleId,
        double3 positionCci,
        double3 surfaceVelocityCci,
        doubleQuat bodyToCci,
        PropulsionCapture propulsion)
    {
        var contact = KSA.SituationEx.HasAnyContact(vehicle.Situation);
        var verticalSpeed = VerticalSpeed(positionCci, surfaceVelocityCci);
        if (!StringComparer.Ordinal.Equals(_contactVehicleId, vehicleId))
        {
            _contactVehicleId = vehicleId;
            _hadGroundContact = contact;
            _touchdownVerticalSpeedMps = null;
        }
        else if (contact && !_hadGroundContact)
        {
            _touchdownSequence = checked(_touchdownSequence + 1);
            _touchdownVerticalSpeedMps = verticalSpeed;
        }
        _hadGroundContact = contact;

        Vector3Value? groundNormal = null;
        if (vehicle.TryGetTerrainContactNormalBody(out var normal) && IsFinite(normal))
        {
            groundNormal = UnitOrNull(ToDouble(normal));
        }

        var hasEngines = propulsion.Telemetry.Engines.Count > 0;
        var exhausted = hasEngines && propulsion.Telemetry.Engines.All(engine => engine.PropellantAvailable == false);
        return new SurfaceTelemetry
        {
            GroundContact = contact,
            Landed = vehicle.Situation == KSA.Situation.Landed,
            GroundNormalBody = groundNormal,
            ContactCount = null,
            TouchdownEventSequence = _touchdownSequence,
            TouchdownVerticalSpeedMps = _touchdownVerticalSpeedMps,
            CollisionEventSequence = null,
            StructuralFailureEventSequence = null,
            EngineOutEventSequence = null,
            PropellantExhausted = exhausted,
            Controllable = vehicle.IsControllable,
        };
    }

    private static LandingReferenceTelemetry? CaptureLandingReference(
        KSA.Vehicle vehicle,
        LandingSiteDefinition site,
        double simulationTimeS,
        double3 gravityCci,
        SurfaceTelemetry surface)
    {
        var siteCcf = ToDouble(site.PositionCcfM);
        if (!IsFinite(siteCcf)) return null;
        var siteCci = Rotate(siteCcf, vehicle.Parent.GetCcf2Cci());
        var siteCce = Rotate(siteCcf, vehicle.Parent.GetCcf2Cce());
        var enuToCci = KSA.VehicleReferenceFrameEx.GetEnu2Cci(siteCci);
        if (enuToCci is null) return null;
        var positionEnu = RotateInverse(vehicle.GetPositionCci() - siteCci, enuToCci.Value);
        var siteVelocityCci = double3.Cross(vehicle.Parent.GetAngularVelocityCci(), siteCci);
        var velocityEnu = RotateInverse(vehicle.GetVelocityCci() - siteVelocityCci, enuToCci.Value);
        var gravityEnu = RotateInverse(gravityCci, enuToCci.Value);
        var groundNormalEnu = surface.GroundNormalBody is { } normalBody
            ? Vector(RotateInverse(Rotate(ToDouble(normalBody), vehicle.GetBody2Cci()), enuToCci.Value))
            : null;
        double? slope = null;
        if (groundNormalEnu is { } normal)
        {
            slope = Math.Acos(Math.Clamp(normal.Z, -1d, 1d));
        }

        return new LandingReferenceTelemetry
        {
            Site = site,
            SitePositionCciM = Vector(siteCci),
            SitePositionCceM = Vector(siteCce),
            SitePositionCcfM = Vector(siteCcf),
            EastCci = Vector(Rotate(double3.UnitX, enuToCci.Value)),
            NorthCci = Vector(Rotate(double3.UnitY, enuToCci.Value)),
            UpCci = Vector(Rotate(double3.UnitZ, enuToCci.Value)),
            PositionEnuM = Vector(positionEnu),
            VelocityEnuMps = Vector(velocityEnu),
            VerticalVelocityMps = Finite(velocityEnu.Z),
            HorizontalSpeedMps = Finite(Math.Sqrt(velocityEnu.X * velocityEnu.X + velocityEnu.Y * velocityEnu.Y)),
            DistanceM = FiniteNonnegative(Length(positionEnu)),
            GravityEnuMps2 = Vector(gravityEnu),
            GroundNormalEnu = groundNormalEnu,
            TerrainHeightM = null,
            RadarAltitudeM = FiniteNonnegative(vehicle.GetRadarAltitude()),
            SurfaceSlopeRad = slope,
            SimulationTimeS = simulationTimeS,
        };
    }

    private static double? VerticalSpeed(double3 positionCci, double3 surfaceVelocityCci)
    {
        var local = KSA.VehicleReferenceFrameEx.GetEnu2Cci(positionCci);
        return local is null ? null : Finite(RotateInverse(surfaceVelocityCci, local.Value).Z);
    }

    private static double? Average(
        IReadOnlyList<EngineTelemetry> engines,
        Func<EngineTelemetry, double?> selector)
    {
        var values = engines.Select(selector).Where(value => value is not null).Select(value => value!.Value).ToArray();
        return values.Length == 0 ? null : values.Average();
    }

    private static double3? WeightedPosition(IReadOnlyList<NozzleCapture> nozzles)
    {
        if (nozzles.Count == 0) return null;
        var total = nozzles.Sum(item => Length(item.ForceBody));
        if (total <= 0)
        {
            return Sum(nozzles.Select(item => item.LocationBody)) / nozzles.Count;
        }
        var result = double3.Zero;
        foreach (var nozzle in nozzles) result += nozzle.LocationBody * (Length(nozzle.ForceBody) / total);
        return result;
    }

    private static double3 Sum(IEnumerable<double3> vectors)
    {
        var result = double3.Zero;
        foreach (var vector in vectors) result += vector;
        return result;
    }

    private static double3 Cross(double3 left, double3 right) => double3.Cross(left, right);
    private static double Length(double3 vector) => vector.Length();

    private static Vector3Value? Vector(double3 value) =>
        IsFinite(value) ? new(value.X, value.Y, value.Z) : null;

    private static Vector3Value? Vector(float3 value) => Vector(ToDouble(value));

    private static QuaternionValue? Quaternion(doubleQuat value)
    {
        var norm = Math.Sqrt(value.X * value.X + value.Y * value.Y + value.Z * value.Z + value.W * value.W);
        return double.IsFinite(norm) && norm > 1e-12
            ? new(value.X / norm, value.Y / norm, value.Z / norm, value.W / norm)
            : null;
    }

    private static Vector3Value? UnitOrNull(double3 value)
    {
        var length = Length(value);
        return double.IsFinite(length) && length > 1e-12 ? Vector(value / length) : null;
    }

    private static double3 Rotate(double3 vector, doubleQuat rotation)
    {
        var q = new QuaternionValue(rotation.X, rotation.Y, rotation.Z, rotation.W);
        return ToDouble(Rotate(new Vector3Value(vector.X, vector.Y, vector.Z), q));
    }

    private static double3 RotateInverse(double3 vector, doubleQuat rotation) =>
        Rotate(vector, new doubleQuat(-rotation.X, -rotation.Y, -rotation.Z, rotation.W));

    private static Vector3Value Rotate(Vector3Value vector, QuaternionValue rotation)
    {
        var dotUv = rotation.X * vector.X + rotation.Y * vector.Y + rotation.Z * vector.Z;
        var dotUu = rotation.X * rotation.X + rotation.Y * rotation.Y + rotation.Z * rotation.Z;
        var crossX = rotation.Y * vector.Z - rotation.Z * vector.Y;
        var crossY = rotation.Z * vector.X - rotation.X * vector.Z;
        var crossZ = rotation.X * vector.Y - rotation.Y * vector.X;
        var scale = rotation.W * rotation.W - dotUu;
        return new(
            2 * dotUv * rotation.X + scale * vector.X + 2 * rotation.W * crossX,
            2 * dotUv * rotation.Y + scale * vector.Y + 2 * rotation.W * crossY,
            2 * dotUv * rotation.Z + scale * vector.Z + 2 * rotation.W * crossZ);
    }

    private static double3 ToDouble(float3 value) => new(value.X, value.Y, value.Z);
    private static double3 ToDouble(Vector3Value value) => new(value.X, value.Y, value.Z);
    private static bool IsFinite(double value) => double.IsFinite(value);
    private static bool IsFinite(double3 value) => IsFinite(value.X) && IsFinite(value.Y) && IsFinite(value.Z);
    private static bool IsFinite(float3 value) => float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
    private static bool IsFiniteNonnegative(double value) => double.IsFinite(value) && value >= 0;
    private static double? Finite(double value) => double.IsFinite(value) ? value : null;
    private static double? FiniteNonnegative(double value) => IsFiniteNonnegative(value) ? value : null;
    private static double? FiniteUnit(double value) => double.IsFinite(value) && value is >= 0 and <= 1 ? value : null;

    private static bool IsUnavailableKsaAccess(Exception exception) =>
        exception is TargetInvocationException or TargetException or ArgumentException or
            MemberAccessException or InvalidOperationException or NotSupportedException or
            ObjectDisposedException or TypeLoadException or NullReferenceException or
            IndexOutOfRangeException;

    private sealed record NozzleCapture(
        KSA.RocketCore Core,
        double3 ForceBody,
        double3 ForceCci,
        double3 LocationBody,
        double MassFlowRateKgps,
        double EffectiveExhaustVelocityMps);

    private sealed record PropulsionCapture(
        PropulsionTelemetry Telemetry,
        IReadOnlyList<NozzleCapture> Nozzles,
        double3 TotalForceBody);
}

internal readonly record struct TelemetrySnapshot(
    VehicleState? Vehicle,
    PropulsionTelemetry? Propulsion,
    RigidBodyTelemetry? RigidBody,
    AtmosphereTelemetry? Atmosphere,
    SurfaceTelemetry? Surface,
    LandingReferenceTelemetry? LandingReference);
