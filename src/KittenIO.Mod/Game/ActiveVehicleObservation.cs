using KittenIO.Mod.Runtime;

namespace KittenIO.Mod.Game;

internal sealed record ActiveVehicleObservation(
    VehicleId? VehicleId,
    string? DisplayName,
    bool Controllable,
    AuthorityIdentity? Authority,
    VehicleInstanceIdentity? VehicleInstance,
    FlightComputerInstanceIdentity? FlightComputerInstance,
    AuthorityEpoch AuthorityEpoch)
{
    public bool HasVerifiedLocalAuthority =>
        Controllable &&
        VehicleId is not null &&
        VehicleInstance is not null &&
        FlightComputerInstance is not null &&
        Authority is { IsVerifiedLocal: true };
}
