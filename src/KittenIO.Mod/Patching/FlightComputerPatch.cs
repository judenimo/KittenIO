using System.Reflection;
using System.Runtime.CompilerServices;
using Brutal.Numerics;
using HarmonyLib;
using KittenIO.Mod.Control;
using KittenIO.Mod.Game;
using KittenIO.Mod.Runtime;
using KittenIO.Protocol;

namespace KittenIO.Mod.Patching;

internal sealed class FlightComputerPatch : IDisposable
{
    private const string HarmonyId = "io.kitten.kittenio.control.v1";
    private readonly Harmony _harmony = new(HarmonyId);
    private bool _installed;

    internal static Action? AtomicCommitFaultInjector { get; set; }

    public PatchCompatibility Install(string gameVersion, ControlCoordinator coordinator)
    {
        if (!PinnedKsaContract.SupportsGameVersion(gameVersion))
        {
            return new(false, $"KSA {gameVersion} is not the verified {PinnedKsaContract.GameVersion} build; telemetry remains available.");
        }

        try
        {
            var assembly = typeof(KSA.Mod).Assembly;
            if (string.IsNullOrWhiteSpace(assembly.Location) ||
                !File.Exists(assembly.Location) ||
                !PinnedKsaContract.HasPinnedKsaBinary(assembly))
            {
                return new(false, $"KSA {gameVersion} does not match the exact verified control-hook binary; telemetry remains available.");
            }
            var flightComputerType = FindType(assembly, "KSA.FlightComputer", "FlightComputer");
            var manualInputsType = FindType(assembly, "KSA.ManualControlInputs", "ManualControlInputs");
            var vehicleType = FindType(assembly, "KSA.Vehicle", "Vehicle");
            var vehicleUpdateStateType = FindType(assembly, "KSA.VehicleUpdateState", "VehicleUpdateState");
            var thrusterFlagsType = FindType(assembly, "KSA.ThrusterMapFlags", "ThrusterMapFlags");
            if (flightComputerType is null || manualInputsType is null || vehicleType is null || vehicleUpdateStateType is null || thrusterFlagsType is null)
            {
                return new(false, "Required FlightComputer, ManualControlInputs, ThrusterMapFlags, or VehicleUpdateState type is missing.");
            }

            var computeControl = flightComputerType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .SingleOrDefault(method =>
                {
                    if (!string.Equals(method.Name, "ComputeControl", StringComparison.Ordinal)) return false;
                    var parameters = method.GetParameters();
                    return parameters.Length == 3 && parameters[1].ParameterType.GetElementType() == manualInputsType;
                });
            if (computeControl is null)
            {
                return new(false, "FlightComputer.ComputeControl signature did not match the verified contract.");
            }

            var updateAttitudeTarget = flightComputerType.GetMethod(
                "UpdateAttitudeTarget",
                BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                types: [assembly.GetType("KSA.FlightComputerNavigation", throwOnError: true)!.MakeByRefType()],
                modifiers: null);

            var prepareFromVehicle = vehicleUpdateStateType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .SingleOrDefault(method =>
                {
                    if (!string.Equals(method.Name, "PrepareFromVehicle", StringComparison.Ordinal)) return false;
                    var parameters = method.GetParameters();
                    return parameters.Length == 2 &&
                           parameters[0].ParameterType == typeof(bool) &&
                           parameters[1].ParameterType == manualInputsType;
                });
            var readOnlyVehicle = vehicleUpdateStateType.GetField(
                "ReadOnlyVehicle",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (prepareFromVehicle is null || updateAttitudeTarget is null ||
                readOnlyVehicle is not { IsPublic: true, IsInitOnly: true } ||
                readOnlyVehicle.FieldType != vehicleType ||
                !HasWritableMember(vehicleUpdateStateType, "IsControlled") ||
                !HasWritableMember(vehicleUpdateStateType, "ManualControlInputs"))
            {
                return new(false, "VehicleUpdateState controlled-input handoff did not match the verified contract.");
            }

            if (!HasWritableMember(flightComputerType, "AttitudeMode") ||
                !HasWritableMember(flightComputerType, "AttitudeTrackTarget") ||
                !HasWritableMember(flightComputerType, "RollMode") ||
                !HasWritableMember(flightComputerType, "AttitudeTarget"))
            {
                return new(false, "FlightComputer thrust-vector target members did not match the verified contract.");
            }

            if (!HasWritableMember(manualInputsType, "EngineOn") ||
                !HasWritableMember(manualInputsType, "EngineThrottle") ||
                !HasWritableMember(manualInputsType, "ThrusterCommandFlags") ||
                GetMemberType(manualInputsType, "ThrusterCommandFlags") != thrusterFlagsType ||
                !HasExpectedThrusterFlags(thrusterFlagsType))
            {
                return new(false, "ManualControlInputs no longer exposes the verified writable members.");
            }

            ControlledStatePostfix.Coordinator = coordinator;
            AttitudeTargetPostfix.Coordinator = coordinator;
            _harmony.Patch(
                prepareFromVehicle,
                postfix: new HarmonyMethod(typeof(ControlledStatePostfix), nameof(ControlledStatePostfix.Apply)));
            _harmony.Patch(
                updateAttitudeTarget,
                postfix: new HarmonyMethod(typeof(AttitudeTargetPostfix), nameof(AttitudeTargetPostfix.Apply)));
            _installed = true;
            return new(true, $"verified for KSA {PinnedKsaContract.GameVersion}");
        }
        catch (Exception exception)
        {
            try
            {
                _harmony.UnpatchAll(HarmonyId);
            }
            catch (Exception)
            {
                // Installation already failed. Keep the original compatibility
                // failure as the primary diagnostic while still clearing state.
            }
            _installed = false;
            ControlledStatePostfix.Coordinator = null;
            AttitudeTargetPostfix.Coordinator = null;
            return new(false, $"Control patch installation failed safely: {exception.Message}");
        }
    }

    public void Dispose()
    {
        try
        {
            if (_installed) _harmony.UnpatchAll(HarmonyId);
        }
        finally
        {
            _installed = false;
            ControlledStatePostfix.Coordinator = null;
            AtomicCommitFaultInjector = null;
        }
    }

    private static Type? FindType(Assembly assembly, string fullName, string simpleName)
    {
        var exact = assembly.GetType(fullName, false);
        if (exact is not null) return exact;
        try
        {
            return assembly.GetTypes().SingleOrDefault(type => type.Name == simpleName);
        }
        catch (ReflectionTypeLoadException exception)
        {
            return exception.Types.Where(type => type is not null).SingleOrDefault(type => type!.Name == simpleName);
        }
    }

    private static bool HasWritableMember(Type type, string name)
    {
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        var field = type.GetField(name, flags);
        if (field is not null) return !field.IsInitOnly;
        return type.GetProperty(name, flags)?.SetMethod is not null;
    }

    private static Type? GetMemberType(Type type, string name)
    {
        var flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
        return type.GetField(name, flags)?.FieldType ?? type.GetProperty(name, flags)?.PropertyType;
    }

    private static bool HasExpectedThrusterFlags(Type type) =>
        type.IsEnum &&
        Enum.IsDefined(type, "RollRight") &&
        Enum.IsDefined(type, "RollLeft") &&
        Enum.IsDefined(type, "PitchUp") &&
        Enum.IsDefined(type, "PitchDown") &&
        Enum.IsDefined(type, "YawRight") &&
        Enum.IsDefined(type, "YawLeft") &&
        Enum.IsDefined(type, "TranslateForward") &&
        Enum.IsDefined(type, "TranslateBackward") &&
        Enum.IsDefined(type, "TranslateRight") &&
        Enum.IsDefined(type, "TranslateLeft") &&
        Enum.IsDefined(type, "TranslateUp") &&
        Enum.IsDefined(type, "TranslateDown");

    private static class ControlledStatePostfix
    {
        internal static ControlCoordinator? Coordinator { private get; set; }

        // KSA computes each vehicle on a cloned FlightComputer, so comparing
        // ComputeControl.__instance with Vehicle.FlightComputer rejects every
        // live invocation. PrepareFromVehicle is the authoritative handoff.
        // ReadOnlyVehicle is the exact object KSA reference-compared with
        // Program.ControlledVehicle. Keep that exact-object route through UI
        // view changes: map mode may clear KSA's transient IsControlled flags
        // even though the selected vehicle, flight computer, and simulation
        // authority remain unchanged. The coordinator's opaque identity and
        // epoch still close same-frame vehicle-switch and stale-route windows.
        public static void Apply(
            bool ___IsControlled,
            KSA.Vehicle ___ReadOnlyVehicle,
            KSA.VehicleUpdateData ___UpdateData,
            ref KSA.ManualControlInputs ___ManualControlInputs)
        {
            try
            {
                var coordinator = Coordinator;
                if (!ReferenceEquals(KSA.Program.ControlledVehicle, ___ReadOnlyVehicle) ||
                    coordinator is null ||
                    ___ReadOnlyVehicle is null ||
                    !VehicleInstanceIdentities.TryGet(___ReadOnlyVehicle, out var vehicleIdentity) ||
                    vehicleIdentity is null ||
                    !coordinator.TryGetFreshOverrideForControlledVehicle(
                        MonotonicClock.NowMilliseconds,
                        vehicleIdentity,
                        out var control) ||
                    control is null)
                {
                    return;
                }

                ApplyControl(ref ___ManualControlInputs, control);
                var flightComputer = ___UpdateData.NewFlightComputer;
                if (StringComparer.Ordinal.Equals(control.Mode, ControlModes.ThrustVectorTarget))
                {
                    if (flightComputer is null)
                    {
                    throw new InvalidOperationException("KSA did not provide the cloned flight computer required for thrust-vector control.");
                    }
                    flightComputer.AttitudeMode = KSA.FlightComputerAttitudeMode.Auto;
                    flightComputer.AttitudeTrackTarget = KSA.FlightComputerAttitudeTrackTarget.Custom;
                    flightComputer.RollMode = KSA.FlightComputerRollMode.Decoupled;
                    FlightComputerRoutes.Bind(flightComputer, vehicleIdentity);
                }
                else
                {
                    if (flightComputer is not null) FlightComputerRoutes.Remove(flightComputer);
                    coordinator.ReportApplied(control, MonotonicClock.NowMilliseconds);
                }
            }
            catch (Exception exception)
            {
                // Harmony must never unwind through KSA. Cleanup is deferred to
                // the game-thread coordinator so this path never takes its lock.
                Coordinator?.ReportPatchFault();
                Console.Error.WriteLine($"[KittenIO] Control override failed closed: {exception.Message}");
            }
        }

        private static void ApplyControl(ref KSA.ManualControlInputs manualInputs, NormalizedControlFrame control)
        {
            // Prepare a complete value-type copy and publish it with one final
            // assignment. Any conversion/fault before that point leaves KSA's
            // original inputs untouched.
            var candidate = manualInputs;
            if (control.EngineOn is { } engineOn)
            {
                candidate.EngineOn = engineOn;
            }
            if (control.Throttle is { } throttle)
            {
                candidate.EngineThrottle = (float)throttle;
            }

            candidate.ThrusterCommandFlags &= ~(KSA.ThrusterMapFlags.RollRight | KSA.ThrusterMapFlags.RollLeft |
                KSA.ThrusterMapFlags.PitchUp | KSA.ThrusterMapFlags.PitchDown |
                KSA.ThrusterMapFlags.YawRight | KSA.ThrusterMapFlags.YawLeft |
                KSA.ThrusterMapFlags.TranslateForward | KSA.ThrusterMapFlags.TranslateBackward |
                KSA.ThrusterMapFlags.TranslateRight | KSA.ThrusterMapFlags.TranslateLeft |
                KSA.ThrusterMapFlags.TranslateUp | KSA.ThrusterMapFlags.TranslateDown);

            AtomicCommitFaultInjector?.Invoke();
            manualInputs = candidate;
        }

    }

    private static class AttitudeTargetPostfix
    {
        internal static ControlCoordinator? Coordinator { private get; set; }

        public static void Apply(KSA.FlightComputer __instance)
        {
            try
            {
                var coordinator = Coordinator;
                if (coordinator is null ||
                    !FlightComputerRoutes.TryGet(__instance, out var vehicleIdentity) ||
                    vehicleIdentity is null ||
                    !coordinator.TryGetFreshOverrideForControlledVehicle(
                        MonotonicClock.NowMilliseconds,
                        vehicleIdentity,
                        out var control) ||
                    control?.ThrustVectorTarget is not { } target)
                {
                    return;
                }

                var direction = target.DirectionCci;
                var targetRotation = KSA.QuaternionEx.FromToRotation(
                    double3.UnitX,
                    new double3(direction.X, direction.Y, direction.Z),
                    KSA.QuaternionEx.QuaternionFallbackAxis.Y,
                    KSA.QuaternionEx.QuaternionFallbackAxis.Z);
                var attitude = __instance.AttitudeTarget;
                attitude.Target2Cci = targetRotation;
                attitude.RatesCci = double3.Zero;
                __instance.AttitudeTarget = attitude;
                coordinator.ReportApplied(control, MonotonicClock.NowMilliseconds);
            }
            catch (Exception exception)
            {
                Coordinator?.ReportPatchFault();
                Console.Error.WriteLine($"[KittenIO] Thrust-vector target failed closed: {exception.Message}");
            }
        }
    }

    private static class FlightComputerRoutes
    {
        private static readonly ConditionalWeakTable<KSA.FlightComputer, VehicleInstanceIdentity> Routes = new();

        public static void Bind(KSA.FlightComputer flightComputer, VehicleInstanceIdentity vehicleIdentity)
        {
            Routes.Remove(flightComputer);
            Routes.Add(flightComputer, vehicleIdentity);
        }

        public static bool TryGet(KSA.FlightComputer flightComputer, out VehicleInstanceIdentity? vehicleIdentity) =>
            Routes.TryGetValue(flightComputer, out vehicleIdentity);

        public static void Remove(KSA.FlightComputer flightComputer) => Routes.Remove(flightComputer);
    }
}

internal readonly record struct PatchCompatibility(bool Available, string Description);

internal static class MonotonicClock
{
    public static long NowMilliseconds => (long)(System.Diagnostics.Stopwatch.GetTimestamp() * 1000d / System.Diagnostics.Stopwatch.Frequency);
}
