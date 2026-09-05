using System.Reflection;
using System.Security.Cryptography;

namespace KittenIO.Mod.Game;

internal enum TimeWarpCommandKind
{
    SetRate,
    Increase,
    Decrease,
    Cancel,
}

internal sealed record TimeWarpObservation(
    bool SimulationAvailable,
    bool MenuPauseActive,
    bool AutoWarpActive,
    double? CurrentRate,
    object? SimulationIdentity = null);

internal sealed record TimeWarpCommandResult(
    bool Ok,
    string Code,
    string Message,
    double? PreviousRate = null,
    double? TargetRate = null,
    object? SimulationIdentity = null)
{
    public static TimeWarpCommandResult Success(double previousRate, double targetRate, object simulationIdentity) =>
        new(true, "ok", "Time-warp request entered KSA's input-event queue.", previousRate, targetRate, simulationIdentity);

    public static TimeWarpCommandResult Failure(string code, string message) =>
        new(false, code, message);
}

/// <summary>
/// Exact build-pinned adapter for the same buffered simulation-speed path used
/// by KSA's keyboard and Simulation menu. It never assigns Universe speed state.
/// </summary>
internal sealed class PinnedKsaTimeWarpController
{
    private const double RateTolerance = 0.0001;

    internal static IReadOnlyList<double> PinnedRates { get; } =
    [
        0d,
        0.1d,
        1d,
        2d,
        4d,
        10d,
        30d,
        120d,
        600d,
        1_200d,
        3_600d,
        14_400d,
        86_400d,
        604_800d,
        2_592_000d,
        7_776_000d,
    ];

    private readonly PropertyInfo _currentSystem;
    private readonly PropertyInfo _autoWarpActive;
    private readonly FieldInfo _editor;
    private readonly FieldInfo _menuPauseActive;

    private PinnedKsaTimeWarpController(
        PropertyInfo currentSystem,
        PropertyInfo autoWarpActive,
        FieldInfo editor,
        FieldInfo menuPauseActive)
    {
        _currentSystem = currentSystem;
        _autoWarpActive = autoWarpActive;
        _editor = editor;
        _menuPauseActive = menuPauseActive;
    }

    public static bool TryCreate(
        Assembly assembly,
        out PinnedKsaTimeWarpController? controller,
        out string unavailableReason)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        controller = null;
        unavailableReason = "The pinned KSA time-warp command contract is unavailable.";
        try
        {
            if (!HasPinnedBinaryIdentity(assembly))
            {
                unavailableReason = $"Time-warp control is pinned to KSA {PinnedKsaContract.GameVersion}.";
                return false;
            }

            var universe = assembly.GetType("KSA.Universe", throwOnError: false);
            var program = assembly.GetType("KSA.Program", throwOnError: false);
            var inputEvents = assembly.GetType("KSA.InputEvents", throwOnError: false);
            var simSpeed = assembly.GetType("KSA.SimSpeed", throwOnError: false);
            var simSpeedData = assembly.GetType("KSA.InputEvents+SimSpeedData", throwOnError: false);
            var inputAction = assembly.GetType("KSA.InputAction", throwOnError: false);
            if (universe is null || program is null || inputEvents is null || simSpeed is null ||
                simSpeedData is null || inputAction is null)
            {
                return false;
            }

            const BindingFlags exactStatic = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            const BindingFlags exactInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var currentSystem = universe.GetProperty("CurrentSystem", exactStatic);
            var autoWarpActive = universe.GetProperty("IsAutoWarpActive", exactStatic);
            var getSimulationSpeed = universe.GetMethod(
                "GetSimulationSpeed",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null);
            var getSimulationSpeeds = universe.GetMethod(
                "GetSimulationSpeeds",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null);
            var editor = program.GetField("Editor", exactStatic);
            var menuPauseActive = program.GetField("_menuPauseActive", exactStatic);
            var speedValue = simSpeed.GetField("Value", exactInstance);
            var speedConstructor = simSpeed.GetConstructor([typeof(double)]);
            var speedField = simSpeedData.GetField("Speed", exactInstance);
            var actionField = simSpeedData.GetField("Action", exactInstance);
            var apply = simSpeedData.GetMethod(
                "Apply",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null);
            var buffer = inputEvents.GetField("SimSpeedBuffer", exactStatic);
            var bufferAdd = buffer?.FieldType.GetMethod(
                "Add",
                BindingFlags.Instance | BindingFlags.Public,
                binder: null,
                types: [simSpeedData],
                modifiers: null);
            var applyInputEvents = inputEvents.GetMethod(
                "ApplyInputEvents",
                BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly,
                binder: null,
                types: Type.EmptyTypes,
                modifiers: null);

            var requiredActions = new[] { "SimSpeedIncrease", "SimSpeedDecrease", "SimSpeedDefault" };
            if (currentSystem?.GetMethod is not { IsStatic: true } ||
                autoWarpActive?.GetMethod is not { IsStatic: true } || autoWarpActive.PropertyType != typeof(bool) ||
                getSimulationSpeed?.ReturnType != typeof(double) ||
                getSimulationSpeeds?.ReturnType != typeof(List<KSA.SimSpeed>) ||
                editor is not { IsPublic: true, IsStatic: true } ||
                menuPauseActive is not { IsPrivate: true, IsStatic: true } || menuPauseActive.FieldType != typeof(bool) ||
                speedValue is not { IsPublic: true } || speedValue.FieldType != typeof(double) ||
                speedConstructor is not { IsPublic: true } ||
                speedField is not { IsPublic: true } || speedField.FieldType != typeof(KSA.SimSpeed?) ||
                actionField is not { IsPublic: true } || actionField.FieldType != inputAction ||
                apply?.ReturnType != typeof(void) ||
                buffer is not { IsPublic: true, IsStatic: true } || bufferAdd?.ReturnType != typeof(void) ||
                applyInputEvents?.ReturnType != typeof(void) ||
                requiredActions.Any(name => !Enum.IsDefined(inputAction, name)))
            {
                return false;
            }

            if (!HasPinnedCoreSettings(assembly))
            {
                unavailableReason = "KSA's Core simulation-rate settings do not match the pinned contract.";
                return false;
            }

            var liveRates = KSA.Universe.GetSimulationSpeeds().Select(item => item.Value).ToArray();
            if (liveRates.Length > 0 && !RatesMatch(liveRates, PinnedRates))
            {
                unavailableReason = "KSA's loaded simulation-rate table does not match the pinned Core contract.";
                return false;
            }

            controller = new PinnedKsaTimeWarpController(currentSystem, autoWarpActive, editor, menuPauseActive);
            unavailableReason = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            CryptographicException or ArgumentException or MethodAccessException or InvalidOperationException or
            TargetInvocationException or TypeInitializationException)
        {
            unavailableReason = "The exact pinned KSA time-warp command contract could not be verified.";
            return false;
        }
    }

    public TimeWarpObservation Observe()
    {
        try
        {
            var simulationIdentity = _currentSystem.GetValue(null);
            var simulationAvailable = simulationIdentity is not null && _editor.GetValue(null) is null;
            var currentRate = KSA.Universe.GetSimulationSpeed();
            return new TimeWarpObservation(
                simulationAvailable,
                Equals(_menuPauseActive.GetValue(null), true),
                Equals(_autoWarpActive.GetValue(null), true),
                double.IsFinite(currentRate) ? currentRate : null,
                simulationIdentity);
        }
        catch (Exception exception) when (exception is TargetInvocationException or TargetException or
            ArgumentException or MethodAccessException or InvalidOperationException)
        {
            return new TimeWarpObservation(false, false, false, null);
        }
    }

    public TimeWarpCommandResult Request(TimeWarpCommandKind kind, double? requestedRate)
    {
        var observation = Observe();
        if (!observation.SimulationAvailable || observation.CurrentRate is not { } currentRate ||
            observation.SimulationIdentity is null)
        {
            return TimeWarpCommandResult.Failure(
                "simulation_unavailable",
                "KSA does not currently expose an active flight simulation.");
        }
        if (observation.MenuPauseActive)
        {
            return TimeWarpCommandResult.Failure(
                "simulation_paused",
                "KSA's menu pause owns simulation speed; close the pause/save/settings UI before changing time warp.");
        }
        if (!TryGetLiveRates(out var rates))
        {
            return TimeWarpCommandResult.Failure(
                "time_warp_unavailable",
                "KSA's loaded simulation-rate table no longer matches the pinned contract.");
        }

        double targetRate;
        switch (kind)
        {
            case TimeWarpCommandKind.SetRate:
                if (requestedRate is not { } rate || !double.IsFinite(rate) || !ContainsRate(rates, rate))
                {
                    return TimeWarpCommandResult.Failure(
                        "unsupported_warp_rate",
                        "rate must exactly match one of KSA's advertised simulation rates.");
                }
                if (observation.AutoWarpActive)
                {
                    return TimeWarpCommandResult.Failure(
                        "auto_warp_active",
                        "KSA auto-warp currently owns simulation speed; cancel it before selecting an exact rate.");
                }
                targetRate = rate;
                break;
            case TimeWarpCommandKind.Increase:
                targetRate = AdjacentRate(rates, currentRate, increase: true);
                break;
            case TimeWarpCommandKind.Decrease:
                targetRate = AdjacentRate(rates, currentRate, increase: false);
                break;
            case TimeWarpCommandKind.Cancel:
                targetRate = 1d;
                break;
            default:
                return TimeWarpCommandResult.Failure("invalid_params", "Unknown time-warp operation.");
        }

        try
        {
            var command = kind switch
            {
                TimeWarpCommandKind.SetRate => new KSA.InputEvents.SimSpeedData
                {
                    Speed = new KSA.SimSpeed(targetRate),
                },
                TimeWarpCommandKind.Increase => new KSA.InputEvents.SimSpeedData
                {
                    Action = KSA.InputAction.SimSpeedIncrease,
                },
                TimeWarpCommandKind.Decrease => new KSA.InputEvents.SimSpeedData
                {
                    Action = KSA.InputAction.SimSpeedDecrease,
                },
                TimeWarpCommandKind.Cancel => new KSA.InputEvents.SimSpeedData
                {
                    Action = KSA.InputAction.SimSpeedDefault,
                },
                _ => throw new InvalidOperationException("Unknown time-warp operation."),
            };
            KSA.InputEvents.SimSpeedBuffer.Add(command);
            return TimeWarpCommandResult.Success(currentRate, targetRate, observation.SimulationIdentity);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or
            IndexOutOfRangeException or NullReferenceException)
        {
            return TimeWarpCommandResult.Failure(
                "time_warp_refused",
                "KSA refused to enqueue the requested simulation-speed transition.");
        }
    }

    internal static bool Approximately(double left, double right) => Math.Abs(left - right) < RateTolerance;

    private static double AdjacentRate(IReadOnlyList<double> rates, double currentRate, bool increase)
    {
        var index = 0;
        while (index + 1 < rates.Count && rates[index] < currentRate) index++;
        if (increase && index + 1 < rates.Count) index++;
        if (!increase && index > 0) index--;
        return rates[index];
    }

    private static bool ContainsRate(IReadOnlyList<double> rates, double requestedRate) =>
        rates.Any(rate => Approximately(rate, requestedRate));

    private static bool TryGetLiveRates(out IReadOnlyList<double> rates)
    {
        try
        {
            var liveRates = KSA.Universe.GetSimulationSpeeds().Select(item => item.Value).ToArray();
            if (RatesMatch(liveRates, PinnedRates))
            {
                rates = liveRates;
                return true;
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or NullReferenceException)
        {
        }
        rates = [];
        return false;
    }

    private static bool RatesMatch(IReadOnlyList<double> actual, IReadOnlyList<double> expected) =>
        actual.Count == expected.Count && actual.Zip(expected).All(pair => Approximately(pair.First, pair.Second));

    private static bool HasPinnedBinaryIdentity(Assembly assembly) =>
        PinnedKsaContract.HasPinnedKsaBinary(assembly);

    private static bool HasPinnedCoreSettings(Assembly assembly)
    {
        var installationDirectory = Path.GetDirectoryName(assembly.Location);
        var configuredDirectory = Environment.GetEnvironmentVariable("KITTENIO_KSA_DIR");
        return new[] { installationDirectory, configuredDirectory }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => Path.Combine(path!, "Content", "Core", "mod.toml"))
            .Where(File.Exists)
            .Any(settingsPath =>
            {
                return PinnedKsaContract.HashMatches(settingsPath, PinnedKsaContract.CoreSettingsSha256);
            });
    }
}
