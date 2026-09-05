namespace KittenIO.Mod.Game;

/// <summary>
/// Main-thread target bindings from exactly one completed catalog capture.
/// Replacing the catalog revokes every object retained by the previous capture.
/// </summary>
internal sealed class CatalogTargetRegistry
{
    private IReadOnlyDictionary<string, object> _targets =
        new Dictionary<string, object>(StringComparer.Ordinal);

    public long Generation { get; private set; }

    public void Replace(IReadOnlyDictionary<string, object> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        _targets = new Dictionary<string, object>(targets, StringComparer.Ordinal);
        Generation++;
    }

    public bool TryResolve(string targetId, out object? target) =>
        _targets.TryGetValue(targetId, out target);
}
