using System.Runtime.CompilerServices;
using KittenIO.Mod.Runtime;

namespace KittenIO.Mod.Game;

/// <summary>
/// Produces collision-free opaque tokens for exact KSA flight-computer
/// references without retaining the KSA instances.
/// </summary>
internal static class FlightComputerInstanceIdentities
{
    private static readonly ConditionalWeakTable<object, FlightComputerInstanceIdentity> Identities = new();

    public static FlightComputerInstanceIdentity For(object instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return Identities.GetValue(instance, static _ => new FlightComputerInstanceIdentity());
    }
}
