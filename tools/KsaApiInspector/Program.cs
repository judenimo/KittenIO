using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using System.Text.RegularExpressions;

if (args.Length is < 1 or > 4)
{
    Console.Error.WriteLine("Usage: KsaApiInspector <assembly.dll> [type-regex] [member-regex] [refs|il]");
    return 2;
}

var assemblyPath = Path.GetFullPath(args[0]);
var installationDirectory = Path.GetDirectoryName(assemblyPath)!;
var typeFilter = new Regex(
    args.Length >= 2 ? args[1] : ".*",
    RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
    TimeSpan.FromSeconds(1));
var memberFilter = new Regex(
    args.Length >= 3 ? args[2] : ".*",
    RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
    TimeSpan.FromSeconds(1));
var showReferences = args.Length == 4 && string.Equals(args[3], "refs", StringComparison.Ordinal);
var showIl = args.Length == 4 && string.Equals(args[3], "il", StringComparison.Ordinal);

AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    var candidate = Path.Combine(installationDirectory, $"{name.Name}.dll");
    return File.Exists(candidate) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate) : null;
};

var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
foreach (var type in SafeTypes(assembly).OrderBy(type => type.FullName, StringComparer.Ordinal))
{
    if (!typeFilter.IsMatch(type.FullName ?? type.Name)) continue;
    var members = type
        .GetMembers(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
        .Select(FormatMember)
        .Where(signature => memberFilter.IsMatch(signature))
        .OrderBy(signature => signature, StringComparer.Ordinal)
        .ToArray();
    if (members.Length == 0) continue;
    Console.WriteLine($"TYPE {FormatType(type)} KIND {(type.IsValueType ? "value" : "reference")} BASE {FormatType(type.BaseType)}");
    foreach (var member in members) Console.WriteLine($"  {member}");
    if (showIl)
    {
        foreach (var method in DeclaredMethods(type).Where(method => memberFilter.IsMatch(FormatMember(method))))
        {
            Console.WriteLine($"  IL {FormatMember(method)}");
            foreach (var instruction in Disassemble(method)) Console.WriteLine($"    {instruction}");
        }
    }
    if (!showReferences) continue;
    foreach (var method in DeclaredMethods(type).Where(method => memberFilter.IsMatch(FormatMember(method))))
    {
        Console.WriteLine($"  REFS {method.Name}");
        foreach (var reference in ReferencedMembers(method).OrderBy(value => value, StringComparer.Ordinal))
        {
            Console.WriteLine($"    {reference}");
        }
    }
}

return 0;

static IEnumerable<Type> SafeTypes(Assembly assembly)
{
    try
    {
        return assembly.GetTypes();
    }
    catch (ReflectionTypeLoadException exception)
    {
        return exception.Types.OfType<Type>();
    }
}

static IEnumerable<MethodBase> DeclaredMethods(Type type) =>
    type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
        .Cast<MethodBase>()
        .Concat(type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly));

static string FormatMember(MemberInfo member) => member switch
{
    FieldInfo field => $"field {FieldVisibility(field)} {(field.IsStatic ? "static " : "")}{FormatType(field.FieldType)} {field.Name}{FormatLiteral(field)}",
    PropertyInfo property => $"property {PropertyVisibility(property)} {(IsStatic(property) ? "static " : "")}{FormatType(property.PropertyType)} {property.Name}",
    MethodInfo method when !method.IsSpecialName =>
        $"method {MethodVisibility(method)} {(method.IsStatic ? "static " : "")}{FormatType(method.ReturnType)} {method.Name}({string.Join(", ", method.GetParameters().Select(FormatParameter))})",
    ConstructorInfo constructor =>
        $"constructor {MethodVisibility(constructor)} {constructor.DeclaringType?.Name}({string.Join(", ", constructor.GetParameters().Select(FormatParameter))})",
    EventInfo eventInfo => $"event {FormatType(eventInfo.EventHandlerType)} {eventInfo.Name}",
    _ => $"{member.MemberType.ToString().ToLowerInvariant()} {member.Name}",
};

static string FormatParameter(ParameterInfo parameter) =>
    $"{(parameter.IsOut ? "out " : parameter.ParameterType.IsByRef ? "ref " : string.Empty)}{FormatType(parameter.ParameterType)} {parameter.Name}";

static string FormatType(Type? type)
{
    if (type is null) return "<null>";
    if (type.IsByRef) return FormatType(type.GetElementType());
    if (type.IsArray) return $"{FormatType(type.GetElementType())}[]";
    if (!type.IsGenericType) return type.FullName ?? type.Name;
    var name = (type.GetGenericTypeDefinition().FullName ?? type.Name).Split('`')[0];
    return $"{name}<{string.Join(",", type.GetGenericArguments().Select(FormatType))}>";
}

static string FieldVisibility(FieldInfo field) => field.IsPublic ? "public" : field.IsFamily ? "protected" : "private";
static string FormatLiteral(FieldInfo field) => field.IsLiteral ? $" = {field.GetRawConstantValue()}" : string.Empty;
static string MethodVisibility(MethodBase method) => method.IsPublic ? "public" : method.IsFamily ? "protected" : "private";
static string PropertyVisibility(PropertyInfo property) => MethodVisibility(property.GetMethod ?? property.SetMethod!);
static bool IsStatic(PropertyInfo property) => (property.GetMethod ?? property.SetMethod)?.IsStatic == true;

static IEnumerable<string> ReferencedMembers(MethodBase method)
{
    var il = method.GetMethodBody()?.GetILAsByteArray();
    if (il is null) yield break;
    var opCodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode))
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(opCode => unchecked((ushort)opCode.Value));
    var index = 0;
    while (index < il.Length)
    {
        ushort value = il[index++];
        if (value == 0xfe) value = (ushort)(0xfe00 | il[index++]);
        var opCode = opCodes[value];
        switch (opCode.OperandType)
        {
            case OperandType.InlineNone:
                break;
            case OperandType.ShortInlineBrTarget:
            case OperandType.ShortInlineI:
            case OperandType.ShortInlineVar:
                index += 1;
                break;
            case OperandType.InlineVar:
                index += 2;
                break;
            case OperandType.InlineBrTarget:
            case OperandType.InlineI:
            case OperandType.ShortInlineR:
            case OperandType.InlineSig:
            case OperandType.InlineString:
                index += 4;
                break;
            case OperandType.InlineI8:
            case OperandType.InlineR:
                index += 8;
                break;
            case OperandType.InlineField:
            case OperandType.InlineMethod:
            case OperandType.InlineTok:
            case OperandType.InlineType:
            {
                var token = BitConverter.ToInt32(il, index);
                index += 4;
                MemberInfo? member = null;
                try
                {
                    member = method.Module.ResolveMember(token, method.DeclaringType?.GetGenericArguments(),
                        method is MethodInfo methodInfo ? methodInfo.GetGenericArguments() : null);
                }
                catch (Exception exception) when (exception is ArgumentException or BadImageFormatException)
                {
                }
                if (member is not null) yield return $"{member.DeclaringType?.FullName}.{member.Name}";
                break;
            }
            case OperandType.InlineSwitch:
                var count = BitConverter.ToInt32(il, index);
                index += 4 + checked(count * 4);
                break;
            default:
                throw new InvalidOperationException($"Unsupported IL operand {opCode.OperandType}.");
        }
    }
}

static IEnumerable<string> Disassemble(MethodBase method)
{
    var body = method.GetMethodBody();
    var il = body?.GetILAsByteArray();
    if (body is null || il is null) yield break;
    var opCodes = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode))
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(opCode => unchecked((ushort)opCode.Value));
    var index = 0;
    while (index < il.Length)
    {
        var offset = index;
        ushort value = il[index++];
        if (value == 0xfe) value = (ushort)(0xfe00 | il[index++]);
        var opCode = opCodes[value];
        object? operand = null;
        switch (opCode.OperandType)
        {
            case OperandType.InlineNone:
                break;
            case OperandType.ShortInlineBrTarget:
                operand = $"IL_{index + 1 + unchecked((sbyte)il[index]):X4}";
                index += 1;
                break;
            case OperandType.InlineBrTarget:
                operand = $"IL_{index + 4 + BitConverter.ToInt32(il, index):X4}";
                index += 4;
                break;
            case OperandType.ShortInlineI:
                operand = opCode == OpCodes.Ldc_I4_S ? unchecked((sbyte)il[index]) : il[index];
                index += 1;
                break;
            case OperandType.InlineI:
                operand = BitConverter.ToInt32(il, index);
                index += 4;
                break;
            case OperandType.InlineI8:
                operand = BitConverter.ToInt64(il, index);
                index += 8;
                break;
            case OperandType.ShortInlineR:
                operand = BitConverter.ToSingle(il, index).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                index += 4;
                break;
            case OperandType.InlineR:
                operand = BitConverter.ToDouble(il, index).ToString("R", System.Globalization.CultureInfo.InvariantCulture);
                index += 8;
                break;
            case OperandType.ShortInlineVar:
                operand = il[index];
                index += 1;
                break;
            case OperandType.InlineVar:
                operand = BitConverter.ToUInt16(il, index);
                index += 2;
                break;
            case OperandType.InlineSwitch:
            {
                var count = BitConverter.ToInt32(il, index);
                index += 4;
                var baseOffset = index + checked(count * 4);
                var targets = new string[count];
                for (var targetIndex = 0; targetIndex < count; targetIndex++)
                {
                    targets[targetIndex] = $"IL_{baseOffset + BitConverter.ToInt32(il, index):X4}";
                    index += 4;
                }
                operand = string.Join(", ", targets);
                break;
            }
            case OperandType.InlineString:
            {
                var token = BitConverter.ToInt32(il, index);
                index += 4;
                try { operand = $"\"{method.Module.ResolveString(token)}\""; }
                catch (ArgumentException) { operand = $"string 0x{token:X8}"; }
                break;
            }
            case OperandType.InlineField:
            case OperandType.InlineMethod:
            case OperandType.InlineTok:
            case OperandType.InlineType:
            {
                var token = BitConverter.ToInt32(il, index);
                index += 4;
                try
                {
                    var member = method.Module.ResolveMember(token, method.DeclaringType?.GetGenericArguments(),
                        method is MethodInfo methodInfo ? methodInfo.GetGenericArguments() : null);
                    operand = member is null ? $"token 0x{token:X8}" : $"{member.DeclaringType?.FullName}.{member.Name}";
                }
                catch (Exception exception) when (exception is ArgumentException or BadImageFormatException)
                {
                    operand = $"token 0x{token:X8}";
                }
                break;
            }
            case OperandType.InlineSig:
                operand = $"signature 0x{BitConverter.ToInt32(il, index):X8}";
                index += 4;
                break;
            default:
                throw new InvalidOperationException($"Unsupported IL operand {opCode.OperandType}.");
        }
        yield return $"IL_{offset:X4}: {opCode.Name}{(operand is null ? string.Empty : $" {operand}")}";
    }
}
