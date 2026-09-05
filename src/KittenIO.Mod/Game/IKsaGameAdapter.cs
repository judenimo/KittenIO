using KittenIO.Protocol;

namespace KittenIO.Mod.Game;

internal interface ITimeWarpAdapter
{
    TimeWarpObservation ObserveTimeWarp();

    TimeWarpCommandResult RequestTimeWarp(TimeWarpCommandKind kind, double? rate);
}

internal interface IKsaGameAdapter : IActiveVehicleSwitchAdapter, ITimeWarpAdapter
{
    string GameVersion { get; }

    bool TimeWarpContractVerified { get; }

    string TimeWarpContractFailureReason { get; }

    IReadOnlyList<double> AvailableTimeWarpRates { get; }

    void Refresh();

    VehicleState? CaptureActiveVehicle();

    TelemetrySnapshot CaptureTelemetry(double simulationTimeS);

    bool TelemetryContractVerified { get; }

    string TelemetryContractFailureReason { get; }

    ManualControlState? CaptureManualControl();

    CatalogPayload CaptureCatalog();

    void ResetCatalogTargets();

    void ResetLandingReference();

    double? GetSimulationTime();

    double? GetSimulationSpeed();

    long? GetFrameNumber();

    GameCommandResult ActivateNextSequence();

    GameCommandResult SetTarget(string targetId);

    GameCommandResult SetLandingSite(LandingSiteDefinition site);
}

internal sealed record GameCommandResult(bool Ok, string Code, string Message, object? Result = null)
{
    public static GameCommandResult Success(object? result = null) => new(true, "ok", "Command completed.", result);

    public static GameCommandResult Failure(string code, string message) => new(false, code, message);
}
