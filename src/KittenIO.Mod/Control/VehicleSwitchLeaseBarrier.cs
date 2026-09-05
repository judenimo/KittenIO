using KittenIO.Protocol;

namespace KittenIO.Mod.Control;

internal interface IVehicleSwitchLeaseBarrier
{
    bool TryEnterVehicleSwitch(out ProtocolError? error);

    void ExitVehicleSwitch();
}
