using System.Runtime.CompilerServices;
using KittenIO.Mod.Runtime;

namespace KittenIO.Mod.Game;

/// <summary>
/// Produces collision-free opaque tokens using reference identity. Values do
/// not retain their weak keys, so disposed KSA vehicles remain collectible.
/// </summary>
internal static class VehicleInstanceIdentities
{
    private static readonly ConditionalWeakTable<object, VehicleInstanceIdentity> Identities = new();

    public static VehicleInstanceIdentity For(object instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return Identities.GetValue(instance, static _ => new VehicleInstanceIdentity());
    }

    /// <summary>
    /// Read-only hot-path lookup. Only the adapter creates identities; an
    /// unseen KSA object fails closed until a later game-thread refresh.
    /// </summary>
    public static bool TryGet(object instance, out VehicleInstanceIdentity? identity)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return Identities.TryGetValue(instance, out identity);
    }
}
