using KittenIO.Protocol;

namespace KittenIO.Mod.Game;

/// <summary>
/// Deterministic v1 projection of the loaded current-system vehicle inventory.
/// The active vehicle, when present, is first; remaining entries use opaque-ID
/// ordinal order so catalog ordering never depends on KSA collection order.
/// </summary>
internal static class VehicleCatalogPolicy
{
    public const int MaximumVehicles = 64;

    public static VehicleCatalogSelection Select(
        string? activeVehicleId,
        IEnumerable<EntityReference> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        var byId = new Dictionary<string, EntityReference>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            ArgumentNullException.ThrowIfNull(candidate);
            if (!byId.TryAdd(candidate.Id, candidate))
            {
                throw new InvalidOperationException(
                    $"The current KSA system exposed duplicate vehicle ID '{candidate.Id}'.");
            }
        }
        var totalCount = byId.Count;

        var selected = new List<EntityReference>(Math.Min(MaximumVehicles, byId.Count));
        if (activeVehicleId is not null && byId.Remove(activeVehicleId, out var active))
        {
            selected.Add(active);
        }

        selected.AddRange(byId.Values
            .OrderBy(item => item.Id, StringComparer.Ordinal)
            .Take(MaximumVehicles - selected.Count));
        return new VehicleCatalogSelection(
            selected.ToArray(),
            totalCount - selected.Count);
    }
}

internal sealed record VehicleCatalogSelection(
    IReadOnlyList<EntityReference> Vehicles,
    int OmittedCount);
