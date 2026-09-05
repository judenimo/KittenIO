using System.Reflection;

namespace KittenIO.Mod.Game;

/// <summary>Bounded cache of reflection metadata only; it never stores target instances or results.</summary>
internal sealed class ReflectionMetadataCache
{
    public const int MaximumTypes = 128;

    private readonly object _gate = new();
    private readonly int _capacity;
    private readonly Dictionary<Type, ReflectionTypeMetadata> _entries = [];
    private readonly Queue<Type> _insertionOrder = [];
    private CacheHit? _last;

    public ReflectionMetadataCache(int capacity = MaximumTypes)
    {
        if (capacity is <= 0 or > MaximumTypes) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public int Count
    {
        get
        {
            lock (_gate) return _entries.Count;
        }
    }

    public ReflectionTypeMetadata Get(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var last = Volatile.Read(ref _last);
        if (last is not null && ReferenceEquals(type, last.Type))
        {
            return last.Metadata;
        }
        lock (_gate)
        {
            if (_entries.TryGetValue(type, out var existing))
            {
                PublishLast(type, existing);
                return existing;
            }
            while (_entries.Count >= _capacity && _insertionOrder.TryDequeue(out var oldest))
            {
                _entries.Remove(oldest);
            }

            var created = new ReflectionTypeMetadata(type);
            _entries.Add(type, created);
            _insertionOrder.Enqueue(type);
            PublishLast(type, created);
            return created;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            Volatile.Write(ref _last, null);
            _entries.Clear();
            _insertionOrder.Clear();
        }
    }

    private void PublishLast(Type type, ReflectionTypeMetadata metadata)
    {
        Volatile.Write(ref _last, new CacheHit(type, metadata));
    }

    private sealed record CacheHit(Type Type, ReflectionTypeMetadata Metadata);
}

internal sealed class ReflectionTypeMetadata
{
    private const BindingFlags AllFlags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
    private readonly Dictionary<MemberKey, PropertyInfo> _properties = [];
    private readonly Dictionary<MemberKey, PropertyInfo> _writableProperties = [];
    private readonly Dictionary<MemberKey, FieldInfo> _fields = [];
    private readonly Dictionary<MemberKey, FieldInfo> _writableFields = [];
    private readonly Dictionary<string, MemberInfo> _instanceReadableMembers =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, MemberInfo> _staticReadableMembers =
        new(StringComparer.Ordinal);
    private readonly Dictionary<MethodKey, MethodCandidate[]> _methods = [];
    private readonly PropertyInfo? _integerIndexer;

    public ReflectionTypeMetadata(Type type)
    {
        var properties = type.GetProperties(AllFlags);
        foreach (var property in properties)
        {
            var isStatic = IsStatic(property);
            if (property.GetIndexParameters().Length == 0)
            {
                AddPreferred(_properties, new MemberKey(property.Name, isStatic), property);
                if (property.SetMethod is not null)
                {
                    AddPreferred(_writableProperties, new MemberKey(property.Name, isStatic), property);
                }
            }
            else if (_integerIndexer is null &&
                     !isStatic &&
                     property.GetIndexParameters() is [{ ParameterType: var parameterType }] &&
                     parameterType == typeof(int))
            {
                _integerIndexer = property;
            }
        }

        foreach (var field in type.GetFields(AllFlags))
        {
            var key = new MemberKey(field.Name, field.IsStatic);
            AddPreferred(_fields, key, field);
            if (!field.IsInitOnly) AddPreferred(_writableFields, key, field);
        }

        foreach (var (key, property) in _properties)
        {
            (key.IsStatic ? _staticReadableMembers : _instanceReadableMembers)
                .Add(key.Name, property);
        }
        foreach (var (key, field) in _fields)
        {
            (key.IsStatic ? _staticReadableMembers : _instanceReadableMembers)
                .TryAdd(key.Name, field);
        }

        var methodGroups = new Dictionary<MethodKey, List<MethodCandidate>>();
        foreach (var method in type.GetMethods(AllFlags))
        {
            // An open generic method cannot be invoked without explicit type arguments.
            // Keeping it in an otherwise compatible group can make the exact concrete
            // overload appear ambiguous (KSA.LookupCollection<T>.GetIndex is one such API).
            if (method.ContainsGenericParameters) continue;
            var parameters = method.GetParameters();
            var key = new MethodKey(method.Name, method.IsStatic, parameters.Length);
            if (!methodGroups.TryGetValue(key, out var group))
            {
                group = [];
                methodGroups.Add(key, group);
            }
            group.Add(new MethodCandidate(method, parameters));
        }

        foreach (var (key, group) in methodGroups) _methods.Add(key, [.. group]);
    }

    public PropertyInfo? FindProperty(string name, bool isStatic, bool requireSetter = false)
    {
        var source = requireSetter ? _writableProperties : _properties;
        return source.GetValueOrDefault(new MemberKey(name, isStatic));
    }

    public FieldInfo? FindField(string name, bool isStatic, bool requireWritable = false)
    {
        var source = requireWritable ? _writableFields : _fields;
        return source.GetValueOrDefault(new MemberKey(name, isStatic));
    }

    public MemberInfo? FindReadableMember(string name, bool isStatic) =>
        (isStatic ? _staticReadableMembers : _instanceReadableMembers)
        .GetValueOrDefault(name);

    public PropertyInfo? FindIntegerIndexer() => _integerIndexer;

    public MethodInfo? FindCompatibleMethod(
        string name,
        bool isStatic,
        IReadOnlyList<object?> arguments)
    {
        if (!_methods.TryGetValue(
                new MethodKey(name, isStatic, arguments.Count),
                out var candidates))
        {
            return null;
        }

        MethodInfo? best = null;
        var bestScore = -1;
        var ambiguous = false;
        foreach (var candidate in candidates)
        {
            var score = CompatibilityScore(candidate.Parameters, arguments);
            if (score < 0) continue;
            if (score > bestScore)
            {
                best = candidate.Method;
                bestScore = score;
                ambiguous = false;
            }
            else if (score == bestScore)
            {
                ambiguous = true;
            }
        }

        return ambiguous ? null : best;
    }

    private static int CompatibilityScore(
        ParameterInfo[] parameters,
        IReadOnlyList<object?> arguments)
    {
        var score = 0;
        for (var index = 0; index < parameters.Length; index++)
        {
            var parameterType = parameters[index].ParameterType;
            if (parameterType.IsByRef) parameterType = parameterType.GetElementType()!;
            var value = arguments[index];
            if (value is null)
            {
                if (parameterType.IsValueType && Nullable.GetUnderlyingType(parameterType) is null) return -1;
                score += 1;
                continue;
            }

            var valueType = value.GetType();
            if (parameterType == valueType)
            {
                score += 4;
            }
            else if (parameterType.IsInstanceOfType(value))
            {
                score += 2;
            }
            else
            {
                return -1;
            }
        }
        return score;
    }

    private static bool IsStatic(PropertyInfo property) =>
        (property.GetMethod ?? property.SetMethod)?.IsStatic == true;

    private static void AddPreferred<TMember>(
        IDictionary<MemberKey, TMember> destination,
        MemberKey key,
        TMember member)
        where TMember : MemberInfo
    {
        if (!destination.TryGetValue(key, out var existing) ||
            InheritanceDepth(member.DeclaringType) > InheritanceDepth(existing.DeclaringType))
        {
            destination[key] = member;
        }
    }

    private static int InheritanceDepth(Type? type)
    {
        var depth = 0;
        for (var current = type; current is not null; current = current.BaseType) depth++;
        return depth;
    }

    private readonly record struct MemberKey(string Name, bool IsStatic);

    private readonly record struct MethodKey(string Name, bool IsStatic, int ParameterCount);

    private readonly record struct MethodCandidate(MethodInfo Method, ParameterInfo[] Parameters);
}
