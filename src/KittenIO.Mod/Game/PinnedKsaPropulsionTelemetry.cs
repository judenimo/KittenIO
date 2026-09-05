using System.Reflection;
using System.Security.Cryptography;
using Brutal.Numerics;

namespace KittenIO.Mod.Game;

/// <summary>
/// Exact, build-pinned reader for KSA's active-vehicle propulsion state. The
/// reader is invoked only by <see cref="KsaGameAdapter"/> on the game thread
/// and returns scalar values rather than retaining or publishing KSA objects.
/// </summary>
internal sealed class PinnedKsaPropulsionTelemetry
{
    private const double PascalsPerKsaPhysicsPressureUnit = 1d;
    private const double AmbientPressureAbsoluteTolerancePa = 0.125d;
    private const double AmbientPressureRelativeTolerance = 0.001d;

    private readonly FieldInfo _flightAmbientPressure;
    private readonly MethodInfo _accumulateCoreThrustAtPressure;

    private PinnedKsaPropulsionTelemetry(
        FieldInfo flightAmbientPressure,
        MethodInfo accumulateCoreThrustAtPressure)
    {
        _flightAmbientPressure = flightAmbientPressure;
        _accumulateCoreThrustAtPressure = accumulateCoreThrustAtPressure;
    }

    public static bool TryCreate(
        Assembly assembly,
        out PinnedKsaPropulsionTelemetry? telemetry,
        out string unavailableReason)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        telemetry = null;
        unavailableReason = "The pinned KSA propulsion telemetry contract is unavailable.";
        try
        {
            if (!HasPinnedBinaryIdentity(assembly))
            {
                unavailableReason = $"Propulsion telemetry is pinned to KSA {PinnedKsaContract.GameVersion}.";
                return false;
            }

            var performanceList = assembly.GetType("KSA.SequencePerformanceList", throwOnError: false);
            var engineController = assembly.GetType("KSA.EngineController", throwOnError: false);
            var rocketCore = assembly.GetType("KSA.RocketCore", throwOnError: false);
            var float3Type = typeof(float3);
            if (performanceList is null || engineController is null || rocketCore is null)
            {
                return false;
            }

            const BindingFlags exactInstance = BindingFlags.Instance |
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
            var flightAmbientPressure = performanceList.GetField("_flightAmbientPressure", exactInstance);
            var accumulate = performanceList.GetMethod(
                "AccumulateCoreThrustAtPressure",
                exactInstance,
                binder: null,
                types:
                [
                    rocketCore,
                    typeof(float),
                    float3Type.MakeByRefType(),
                    typeof(float).MakeByRefType(),
                ],
                modifiers: null);

            if (flightAmbientPressure is not { IsPrivate: true, IsStatic: false } ||
                flightAmbientPressure.FieldType != typeof(float) ||
                accumulate is not { IsPrivate: true, IsStatic: false } ||
                accumulate.ReturnType != typeof(void))
            {
                return false;
            }

            telemetry = new PinnedKsaPropulsionTelemetry(flightAmbientPressure, accumulate);
            unavailableReason = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            CryptographicException or ArgumentException or MethodAccessException or InvalidOperationException)
        {
            unavailableReason = "The exact pinned KSA propulsion telemetry contract could not be verified.";
            return false;
        }
    }

    public PropulsionTelemetrySnapshot Capture(KSA.Vehicle vehicle)
    {
        ArgumentNullException.ThrowIfNull(vehicle);

        var ambientPressure = TryReadAmbientPressure(vehicle, out var rawAmbientPressure)
            ? ConvertPressureToPascals(rawAmbientPressure)
            : null;
        double? currentThrust = TryReadCurrentThrust(vehicle, out var current)
            ? current
            : null;
        float3? maximumForceVehicleAssembly = ambientPressure is not null &&
            TryReadMaximumAvailableThrust(vehicle, rawAmbientPressure, out var maximumForce)
                ? maximumForce
                : null;
        var maximumThrust = maximumForceVehicleAssembly is { } force
            ? MagnitudeOrNull(force)
            : null;

        return new(currentThrust, maximumThrust, maximumForceVehicleAssembly, ambientPressure);
    }

    internal static double? ConvertPressureToPascals(float pressure)
    {
        var pascals = (double)pressure * PascalsPerKsaPhysicsPressureUnit;
        return double.IsFinite(pascals) && pascals >= 0 ? pascals : null;
    }

    internal static double? MagnitudeOrNull(float3 force)
    {
        var magnitude = (double)force.Length();
        return double.IsFinite(magnitude) && magnitude >= 0 ? magnitude : null;
    }

    internal static bool IsUsableEngine(bool isActive, bool isPropellantAvailable) =>
        isActive && isPropellantAvailable;

    internal static bool AmbientPressuresMatch(float cachedPressure, float capturedPressure)
    {
        if (!float.IsFinite(cachedPressure) || cachedPressure < 0 ||
            !float.IsFinite(capturedPressure) || capturedPressure < 0)
        {
            return false;
        }

        var scale = Math.Max(Math.Abs((double)cachedPressure), Math.Abs((double)capturedPressure));
        var tolerance = Math.Max(
            AmbientPressureAbsoluteTolerancePa,
            scale * AmbientPressureRelativeTolerance);
        return Math.Abs((double)cachedPressure - capturedPressure) <= tolerance;
    }

    private static bool TryReadAmbientPressure(KSA.Vehicle vehicle, out float pressure)
    {
        try
        {
            pressure = vehicle.PhysicsEnvironment.AtmosphericPressure;
            return float.IsFinite(pressure) && pressure >= 0;
        }
        catch (Exception exception) when (IsUnavailableKsaAccess(exception))
        {
            pressure = default;
            return false;
        }
    }

    private static bool TryReadCurrentThrust(KSA.Vehicle vehicle, out double thrust)
    {
        try
        {
            var force = default(float3);
            foreach (var nozzle in vehicle.Parts.RocketNozzles.ModulesAndStates)
            {
                var performance = nozzle.State.Performance;
                var massFlowRate = performance.MassFlowRate;
                if (!float.IsFinite(massFlowRate) || massFlowRate < 0)
                {
                    thrust = default;
                    return false;
                }
                if (massFlowRate == 0) continue;

                var nozzleThrust = performance.TotalThrust;
                if (!float.IsFinite(nozzleThrust) || nozzleThrust < 0)
                {
                    thrust = default;
                    return false;
                }
                var direction = nozzle.State.ThrustDirectionVehicleAsmb;
                if (!IsFiniteVector(direction))
                {
                    thrust = default;
                    return false;
                }
                force += nozzleThrust * direction;
                if (!IsFiniteVector(force))
                {
                    thrust = default;
                    return false;
                }
            }

            var magnitude = MagnitudeOrNull(force);
            thrust = magnitude.GetValueOrDefault();
            return magnitude is not null;
        }
        catch (Exception exception) when (IsUnavailableKsaAccess(exception))
        {
            thrust = default;
            return false;
        }
    }

    private bool TryReadMaximumAvailableThrust(
        KSA.Vehicle vehicle,
        float rawAmbientPressure,
        out float3 force)
    {
        try
        {
            var performanceList = vehicle.Parts.PerformanceSequences;
            if (_flightAmbientPressure.GetValue(performanceList) is not float flightAmbientPressure ||
                !AmbientPressuresMatch(flightAmbientPressure, rawAmbientPressure) ||
                !KSA.ModuleStateful<
                    KSA.EngineController,
                    KSA.EngineControllerState,
                    KSA.EngineControllerGlobalState,
                    KSA.EmptyStruct>.TryGetFrom(vehicle.Parts.States, out var engines))
            {
                force = default;
                return false;
            }

            force = default;
            var massFlowRate = 0f;
            object?[] arguments = [null, rawAmbientPressure, force, massFlowRate];
            foreach (var engine in engines.ModulesAndStates)
            {
                if (!IsUsableEngine(engine.Module.IsActive, engine.State.IsPropellantAvailable))
                {
                    continue;
                }

                foreach (var core in engine.Module.Cores)
                {
                    if (core is null)
                    {
                        force = default;
                        return false;
                    }

                    arguments[0] = core;
                    arguments[1] = rawAmbientPressure;
                    arguments[2] = force;
                    arguments[3] = massFlowRate;
                    _accumulateCoreThrustAtPressure.Invoke(performanceList, arguments);
                    if (arguments[2] is not float3 nextForce || arguments[3] is not float nextMassFlowRate ||
                        !IsFiniteVector(nextForce) ||
                        !float.IsFinite(nextMassFlowRate) || nextMassFlowRate < 0)
                    {
                        force = default;
                        return false;
                    }
                    force = nextForce;
                    massFlowRate = nextMassFlowRate;
                }
            }

            return MagnitudeOrNull(force) is not null;
        }
        catch (Exception exception) when (IsUnavailableKsaAccess(exception))
        {
            force = default;
            return false;
        }
    }

    internal static bool HasPinnedBinaryIdentity(Assembly assembly) =>
        PinnedKsaContract.HasPinnedKsaBinary(assembly);

    private static bool IsUnavailableKsaAccess(Exception exception) =>
        exception is TargetInvocationException or TargetException or ArgumentException or
            MemberAccessException or InvalidOperationException or NotSupportedException or
            ObjectDisposedException or TypeLoadException or NullReferenceException or
            IndexOutOfRangeException;

    internal static bool IsFiniteVector(float3 vector) =>
        float.IsFinite(vector.X) && float.IsFinite(vector.Y) && float.IsFinite(vector.Z);
}

internal readonly record struct PropulsionTelemetrySnapshot(
    double? CurrentThrustN,
    double? MaximumAvailableThrustN,
    float3? MaximumAvailableThrustVehicleAssemblyN,
    double? AmbientPressurePa);
