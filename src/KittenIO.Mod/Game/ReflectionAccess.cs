using System.Collections;
using System.Globalization;
using System.Reflection;

namespace KittenIO.Mod.Game;

internal static class ReflectionAccess
{
    private static readonly ReflectionMetadataCache MetadataCache = new();

    internal static int CachedTypeCount => MetadataCache.Count;

    internal static void ClearMetadataCache() => MetadataCache.Clear();

    public static object? Get(object? instance, params string[] names)
    {
        if (instance is null) return null;
        var type = instance.GetType();
        var metadata = MetadataCache.Get(type);
        foreach (var name in names)
        {
            try
            {
                var member = metadata.FindReadableMember(name, isStatic: false);
                if (member is PropertyInfo property) return property.GetValue(instance);
                if (member is FieldInfo field) return field.GetValue(instance);
            }
            catch (Exception exception) when (IsUnavailableReflectionAccess(exception))
            {
                // A game getter is allowed to be unavailable in menus or during scene transitions.
            }
        }

        return null;
    }

    public static object? GetStatic(Type? type, params string[] names)
    {
        if (type is null) return null;
        var metadata = MetadataCache.Get(type);
        foreach (var name in names)
        {
            try
            {
                var member = metadata.FindReadableMember(name, isStatic: true);
                if (member is PropertyInfo property) return property.GetValue(null);
                if (member is FieldInfo field) return field.GetValue(null);
            }
            catch (Exception exception) when (IsUnavailableReflectionAccess(exception))
            {
                // Treat transient game state as absent.
            }
        }

        return null;
    }

    public static object? Invoke(object? instance, string name, params object?[] arguments)
    {
        if (instance is null) return null;
        var method = MetadataCache.Get(instance.GetType()).FindCompatibleMethod(name, false, arguments);
        return method?.Invoke(instance, arguments);
    }

    public static object? InvokeStatic(Type? type, string name, params object?[] arguments)
    {
        var method = type is null ? null : MetadataCache.Get(type).FindCompatibleMethod(name, true, arguments);
        return method?.Invoke(null, arguments);
    }

    public static object? GetOrInvokeStatic(Type? type, params string[] names)
    {
        foreach (var name in names)
        {
            var value = GetStatic(type, name);
            if (value is not null) return value;
            try
            {
                if (TryInvokeStatic(type, name, [], out value)) return value;
            }
            catch (Exception exception) when (IsUnavailableReflectionAccess(exception))
            {
                // Treat transient game state as absent.
            }
        }

        return null;
    }

    public static bool TryInvoke(object? instance, string name, object?[] arguments, out object? result)
    {
        result = null;
        if (instance is null) return false;
        var method = MetadataCache.Get(instance.GetType()).FindCompatibleMethod(name, false, arguments);
        if (method is null) return false;
        result = method.Invoke(instance, arguments);
        return true;
    }

    public static bool TryInvokeStatic(Type? type, string name, object?[] arguments, out object? result)
    {
        result = null;
        var method = type is null ? null : MetadataCache.Get(type).FindCompatibleMethod(name, true, arguments);
        if (method is null) return false;
        result = method.Invoke(null, arguments);
        return true;
    }

    public static object? GetOrInvoke(object? instance, params string[] names)
    {
        foreach (var name in names)
        {
            var value = Get(instance, name);
            if (value is not null) return value;
            try
            {
                if (TryInvoke(instance, name, [], out value)) return value;
            }
            catch (Exception exception) when (IsUnavailableReflectionAccess(exception))
            {
                // Optional telemetry values are omitted when KSA cannot compute them.
            }
        }

        return null;
    }

    public static string? ToText(object? value)
    {
        if (value is null) return null;
        if (value is string text) return string.IsNullOrWhiteSpace(text) ? null : text;
        return Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    public static double? ToFiniteDouble(object? value)
    {
        if (value is null) return null;
        try
        {
            var number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return double.IsFinite(number) ? number : null;
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException or OverflowException)
        {
            return null;
        }
    }

    public static bool? ToBoolean(object? value)
    {
        if (value is null) return null;
        try
        {
            return Convert.ToBoolean(value, CultureInfo.InvariantCulture);
        }
        catch (Exception exception) when (exception is FormatException or InvalidCastException)
        {
            return null;
        }
    }

    public static IEnumerable<object> Enumerate(object? source)
    {
        if (source is null) yield break;
        if (source is string) yield break;

        if (source is IEnumerable enumerable)
        {
            foreach (var value in enumerable)
            {
                if (value is not null) yield return value;
            }
            yield break;
        }

        var values = Get(source, "Values", "Items", "All");
        if (values is IEnumerable valuesEnumerable)
        {
            foreach (var value in valuesEnumerable)
            {
                if (value is not null) yield return value;
            }
            yield break;
        }

        var count = ToFiniteDouble(Get(source, "Count"));
        if (count is not { } finiteCount) yield break;
        var boundedCount = Math.Clamp((int)finiteCount, 0, 100_000);
        var indexer = MetadataCache.Get(source.GetType()).FindIntegerIndexer();
        for (var index = 0; index < boundedCount; index++)
        {
            object? value = null;
            if (indexer is not null)
            {
                value = indexer.GetValue(source, [index]);
            }
            else if (!TryInvoke(source, "GetIndex", [index], out value) && !TryInvoke(source, "Get", [index], out value))
            {
                yield break;
            }

            if (value is not null) yield return value;
        }
    }

    public static bool Set(object instance, string name, object? value)
    {
        var type = instance.GetType();
        var metadata = MetadataCache.Get(type);
        var property = metadata.FindProperty(name, isStatic: false, requireSetter: true);
        if (property is not null)
        {
            property.SetValue(instance, ConvertFor(value, property.PropertyType));
            return true;
        }

        var field = metadata.FindField(name, isStatic: false, requireWritable: true);
        if (field is not null)
        {
            field.SetValue(instance, ConvertFor(value, field.FieldType));
            return true;
        }

        return false;
    }

    private static object? ConvertFor(object? value, Type targetType)
    {
        if (value is null || targetType.IsInstanceOfType(value)) return value;
        if (targetType.IsEnum)
        {
            if (value is string text) return Enum.Parse(targetType, text, true);
            return Enum.ToObject(targetType, value);
        }

        return Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
    }

    private static bool IsUnavailableReflectionAccess(Exception exception) =>
        exception is TargetInvocationException or
            NotSupportedException or
            ArgumentException or
            TargetException or
            MethodAccessException;
}
