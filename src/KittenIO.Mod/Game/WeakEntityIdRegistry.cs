using System.Runtime.CompilerServices;

namespace KittenIO.Mod.Game;

/// <summary>
/// Creates stable, collision-free IDs without retaining game objects. A registry
/// belongs to one adapter/game instance, so fallback IDs never imply persistence.
/// </summary>
internal sealed class WeakEntityIdRegistry
{
    private readonly ConditionalWeakTable<object, Holder> _ids = new();
    private long _nextId;

    public string GetId(object instance)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return _ids.GetValue(instance, Create).Value;
    }

    private Holder Create(object instance)
    {
        var sequence = checked(Interlocked.Increment(ref _nextId));
        var typeName = instance.GetType().Name.ToLowerInvariant();
        return new($"{typeName}-{sequence:x16}");
    }

    private sealed record Holder(string Value);
}
