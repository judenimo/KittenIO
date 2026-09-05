using System.Reflection;
using System.Security.Cryptography;
using KittenIO.Mod.Runtime;

namespace KittenIO.Mod.Game;

/// <summary>
/// Exact, build-pinned adapter for KSA's console/UI vehicle-control path. This
/// class deliberately has no reflection aliases and never assigns KSA control
/// fields directly.
/// </summary>
internal sealed class PinnedKsaVehicleSwitcher
{

    private readonly PropertyInfo _currentSystem;
    private readonly MethodInfo _getById;
    private readonly PropertyInfo _all;
    private readonly MemberInfo _allCount;
    private readonly MethodInfo _allGetIndex;
    private readonly PropertyInfo _vehicleId;
    private readonly PropertyInfo _isDisposed;
    private readonly PropertyInfo _isControllable;
    private readonly PropertyInfo _flightComputer;
    private readonly PropertyInfo _controlledVehicle;
    private readonly FieldInfo _controlledVehicleActive;
    private readonly Type _vehicleType;
    private readonly Action<string> _controlVehicle;

    private PinnedKsaVehicleSwitcher(
        PropertyInfo currentSystem,
        MethodInfo getById,
        PropertyInfo all,
        MemberInfo allCount,
        MethodInfo allGetIndex,
        PropertyInfo vehicleId,
        PropertyInfo isDisposed,
        PropertyInfo isControllable,
        PropertyInfo flightComputer,
        PropertyInfo controlledVehicle,
        FieldInfo controlledVehicleActive,
        Type vehicleType,
        Action<string> controlVehicle)
    {
        _currentSystem = currentSystem;
        _getById = getById;
        _all = all;
        _allCount = allCount;
        _allGetIndex = allGetIndex;
        _vehicleId = vehicleId;
        _isDisposed = isDisposed;
        _isControllable = isControllable;
        _flightComputer = flightComputer;
        _controlledVehicle = controlledVehicle;
        _controlledVehicleActive = controlledVehicleActive;
        _vehicleType = vehicleType;
        _controlVehicle = controlVehicle;
    }

    public static bool TryCreate(
        Assembly assembly,
        out PinnedKsaVehicleSwitcher? switcher,
        out string unavailableReason)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        switcher = null;
        unavailableReason = "The pinned KSA active-vehicle switch contract is unavailable.";
        try
        {
            if (!HasPinnedBinaryIdentity(assembly))
            {
                unavailableReason = $"Active-vehicle switching is pinned to KSA {PinnedKsaContract.GameVersion}.";
                return false;
            }

            var universe = assembly.GetType("KSA.Universe", throwOnError: false);
            var system = assembly.GetType("KSA.CelestialSystem", throwOnError: false);
            var astronomical = assembly.GetType("KSA.Astronomical", throwOnError: false);
            var vehicle = assembly.GetType("KSA.Vehicle", throwOnError: false);
            var program = assembly.GetType("KSA.Program", throwOnError: false);
            var inputEvents = assembly.GetType("KSA.InputEvents", throwOnError: false);
            var changeData = assembly.GetType("KSA.InputEvents+VehicleResourcesChangeData", throwOnError: false);
            if (universe is null || system is null || astronomical is null || vehicle is null ||
                program is null || inputEvents is null || changeData is null)
            {
                return false;
            }

            const BindingFlags exactStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            const BindingFlags exactInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var currentSystem = universe.GetProperty("CurrentSystem", exactStatic);
            var getById = system.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .SingleOrDefault(method =>
                    method.Name == "Get" &&
                    !method.IsGenericMethod &&
                    method.ReturnType == astronomical &&
                    method.GetParameters() is [{ ParameterType: var parameterType }] &&
                    parameterType == typeof(string));
            var all = system.GetProperty("All", exactInstance);
            var allCount = (MemberInfo?)all?.PropertyType.GetProperty("Count", exactInstance) ??
                all?.PropertyType.GetField("Count", exactInstance);
            var allGetIndex = all?.PropertyType.GetMethods(exactInstance)
                .SingleOrDefault(method =>
                    method.Name == "GetIndex" &&
                    !method.IsStatic &&
                    !method.IsGenericMethod &&
                    astronomical.IsAssignableFrom(method.ReturnType) &&
                    method.GetParameters() is [{ ParameterType: var parameterType }] &&
                    parameterType == typeof(int));
            var vehicleId = astronomical.GetProperty("Id", exactInstance);
            var isDisposed = vehicle.GetProperty("IsDisposed", exactInstance);
            var isControllable = vehicle.GetProperty("IsControllable", exactInstance);
            var flightComputer = vehicle.GetProperty("FlightComputer", exactInstance);
            var controlledVehicle = program.GetProperty("ControlledVehicle", exactStatic);
            var controlledVehicleActive = program.GetField("IsControlledVehicleActive", exactStatic);
            var controlVehicle = universe.GetMethod(
                "ControlVehicle",
                BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                binder: null,
                types: [typeof(string)],
                modifiers: null);
            var vehicleField = changeData.GetField("Vehicle", exactInstance);
            var refillField = changeData.GetField("Refill", exactInstance);
            var emptyField = changeData.GetField("Empty", exactInstance);
            var controlField = changeData.GetField("Control", exactInstance);
            var apply = changeData.GetMethod(
                "Apply",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null);
            var buffer = inputEvents.GetField("VehicleResourcesChangeBuffer", exactStatic);
            var bufferAdd = buffer?.FieldType.GetMethod(
                "Add",
                BindingFlags.Instance | BindingFlags.Public,
                binder: null,
                types: [changeData],
                modifiers: null);

            if (currentSystem?.GetMethod?.IsStatic != true || currentSystem.PropertyType != system ||
                getById is null || getById.IsStatic || getById.IsGenericMethod || getById.ReturnType != astronomical ||
                all is null || allCount is null || allGetIndex is null ||
                GetMemberType(allCount) != typeof(int) || vehicleId?.PropertyType != typeof(string) ||
                isDisposed?.PropertyType != typeof(bool) || isControllable?.PropertyType != typeof(bool) ||
                flightComputer is null ||
                controlledVehicle?.GetMethod is not { IsPublic: true, IsStatic: true } ||
                controlledVehicle.PropertyType != vehicle ||
                controlledVehicleActive?.FieldType != typeof(bool) ||
                controlVehicle is null || !controlVehicle.IsPrivate || controlVehicle.ReturnType != typeof(void) ||
                vehicleField?.FieldType != vehicle || refillField?.FieldType != typeof(bool) ||
                emptyField?.FieldType != typeof(bool) || controlField?.FieldType != typeof(bool) ||
                apply?.ReturnType != typeof(void) || buffer is null || bufferAdd?.ReturnType != typeof(void))
            {
                return false;
            }

            switcher = new PinnedKsaVehicleSwitcher(
                currentSystem,
                getById,
                all,
                allCount,
                allGetIndex,
                vehicleId,
                isDisposed,
                isControllable,
                flightComputer,
                controlledVehicle,
                controlledVehicleActive,
                vehicle,
                controlVehicle.CreateDelegate<Action<string>>());
            unavailableReason = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            CryptographicException or ArgumentException or MethodAccessException or InvalidOperationException)
        {
            unavailableReason = "The exact pinned KSA active-vehicle switch contract could not be verified.";
            return false;
        }
    }

    public GameCommandResult ValidateTarget(string vehicleId)
    {
        if (!TryResolveTarget(vehicleId, out _, out _, out var failure)) return failure!;
        return GameCommandResult.Success(new { vehicle_id = vehicleId });
    }

    public VehicleSwitchRequestResult Request(string vehicleId)
    {
        if (!TryResolveTarget(vehicleId, out var target, out var flightComputer, out var failure))
        {
            return VehicleSwitchRequestResult.Failure(failure!.Code, failure.Message);
        }

        try
        {
            var currentSystem = _currentSystem.GetValue(null);
            var reResolved = currentSystem is null ? null : _getById.Invoke(currentSystem, [vehicleId]);
            if (currentSystem is null ||
                !ReferenceEquals(target, reResolved) ||
                !ContainsByReference(currentSystem, target!) ||
                !ReferenceEquals(_flightComputer.GetValue(target), flightComputer))
            {
                return VehicleSwitchRequestResult.Failure(
                    "vehicle_not_found",
                    "The requested vehicle left the current loaded KSA system before switching.");
            }

            var alreadyActive = ReferenceEquals(_controlledVehicle.GetValue(null), target) &&
                Equals(_controlledVehicleActive.GetValue(null), true);
            if (!alreadyActive) _controlVehicle(vehicleId);
            return VehicleSwitchRequestResult.Success(
                new VehicleId(vehicleId),
                alreadyActive,
                VehicleInstanceIdentities.For(target!),
                FlightComputerInstanceIdentities.For(flightComputer!));
        }
        catch (Exception exception) when (exception is TargetInvocationException or TargetException or
            ArgumentException or MethodAccessException or InvalidOperationException)
        {
            return VehicleSwitchRequestResult.Failure(
                "switch_failed",
                "KSA rejected the active-vehicle switch request.");
        }
    }

    private bool TryResolveTarget(
        string vehicleId,
        out object? target,
        out object? flightComputer,
        out GameCommandResult? failure)
    {
        target = null;
        flightComputer = null;
        failure = null;
        if (string.IsNullOrWhiteSpace(vehicleId))
        {
            failure = GameCommandResult.Failure("invalid_params", "vehicle_id must be a non-empty opaque string.");
            return false;
        }

        try
        {
            var currentSystem = _currentSystem.GetValue(null);
            target = currentSystem is null ? null : _getById.Invoke(currentSystem, [vehicleId]);
            if (currentSystem is null || target is null || !_vehicleType.IsInstanceOfType(target) ||
                !StringComparer.Ordinal.Equals(_vehicleId.GetValue(target) as string, vehicleId) ||
                !ContainsByReference(currentSystem, target) || Equals(_isDisposed.GetValue(target), true))
            {
                target = null;
                failure = GameCommandResult.Failure(
                    "vehicle_not_found",
                    "The requested vehicle is not a loaded vehicle in the current KSA system.");
                return false;
            }

            flightComputer = _flightComputer.GetValue(target);
            if (!Equals(_isControllable.GetValue(target), true) || flightComputer is null)
            {
                target = null;
                flightComputer = null;
                failure = GameCommandResult.Failure(
                    "vehicle_uncontrollable",
                    "The requested vehicle is not currently controllable by KSA.");
                return false;
            }

            return true;
        }
        catch (Exception exception) when (exception is TargetInvocationException or TargetException or
            ArgumentException or MethodAccessException or InvalidOperationException)
        {
            target = null;
            flightComputer = null;
            failure = GameCommandResult.Failure(
                "switch_unavailable",
                "KSA's current loaded vehicle inventory is temporarily unavailable.");
            return false;
        }
    }

    private bool ContainsByReference(object currentSystem, object target)
    {
        var all = _all.GetValue(currentSystem);
        if (all is null) return false;
        var count = _allCount switch
        {
            PropertyInfo property => property.GetValue(all),
            FieldInfo field => field.GetValue(all),
            _ => null,
        };
        if (count is not int length || length < 0) return false;
        for (var index = 0; index < length; index++)
        {
            if (ReferenceEquals(_allGetIndex.Invoke(all, [index]), target)) return true;
        }
        return false;
    }

    private static Type? GetMemberType(MemberInfo member) => member switch
    {
        PropertyInfo property => property.PropertyType,
        FieldInfo field => field.FieldType,
        _ => null,
    };

    private static bool HasPinnedBinaryIdentity(Assembly assembly) =>
        PinnedKsaContract.HasPinnedKsaBinary(assembly);
}
