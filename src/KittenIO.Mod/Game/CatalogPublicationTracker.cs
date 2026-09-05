using KittenIO.Protocol;

namespace KittenIO.Mod.Game;

/// <summary>Game-thread-owned structural catalog comparison and force-publication state.</summary>
internal sealed class CatalogPublicationTracker
{
    private CatalogStructure? _last;

    public CatalogPublicationDecision Observe(CatalogPayload catalog, bool force)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        var current = CatalogStructure.From(catalog);
        var changed = _last is not null && !_last.Equals(current);
        var publish = force || _last is null || changed;
        _last = current;
        return new CatalogPublicationDecision(publish, changed);
    }

    public void Reset() => _last = null;

    private sealed class CatalogStructure : IEquatable<CatalogStructure>
    {
        private CatalogStructure(
            IReadOnlyList<CatalogEntity> bodies,
            IReadOnlyList<CatalogEntity> vehicles)
        {
            Bodies = bodies;
            Vehicles = vehicles;
        }

        private IReadOnlyList<CatalogEntity> Bodies { get; }

        private IReadOnlyList<CatalogEntity> Vehicles { get; }

        public static CatalogStructure From(CatalogPayload catalog) => new(
            Normalize(catalog.Bodies),
            Normalize(catalog.Vehicles));

        public bool Equals(CatalogStructure? other) =>
            other is not null &&
            Bodies.SequenceEqual(other.Bodies) &&
            Vehicles.SequenceEqual(other.Vehicles);

        public override bool Equals(object? obj) => Equals(obj as CatalogStructure);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (var item in Bodies) hash.Add(item);
            hash.Add("vehicles", StringComparer.Ordinal);
            foreach (var item in Vehicles) hash.Add(item);
            return hash.ToHashCode();
        }

        private static IReadOnlyList<CatalogEntity> Normalize(IReadOnlyList<EntityReference> items) =>
            items.Select(item => new CatalogEntity(item.Id, item.Name, item.Kind))
                .OrderBy(item => item.Id, StringComparer.Ordinal)
                .ThenBy(item => item.Name, StringComparer.Ordinal)
                .ThenBy(item => item.Kind, StringComparer.Ordinal)
                .ToArray();
    }

    private sealed record CatalogEntity(string Id, string Name, string Kind);
}

internal readonly record struct CatalogPublicationDecision(bool Publish, bool Changed);
