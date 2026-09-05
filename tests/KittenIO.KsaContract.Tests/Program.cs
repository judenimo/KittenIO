using System.Diagnostics;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Loader;
using System.Security.Cryptography;

var repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
var ksaAssemblyPath = ResolveInputFile(
    "KITTENIO_KSA_REFERENCE",
    Path.Combine(repositoryRoot, "Import", "KSA.dll"),
    Path.Combine(repositoryRoot, "..", "KittenIO", "Import", "KSA.dll"));
if (!File.Exists(ksaAssemblyPath))
{
    Console.Error.WriteLine($"FAIL KSA reference assembly is missing: {ksaAssemblyPath}");
    return 1;
}

var installedDirectory = ResolveEnvironmentPath("KITTENIO_KSA_DIR") ?? Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "Programs",
    "Kitten Space Agency");
AssemblyLoadContext.Default.Resolving += (_, assemblyName) =>
{
    var candidate = Path.Combine(installedDirectory, $"{assemblyName.Name}.dll");
    return File.Exists(candidate) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(candidate) : null;
};

var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(ksaAssemblyPath);
var failures = new List<string>();

var productVersion = FileVersionInfo.GetVersionInfo(ksaAssemblyPath).ProductVersion;
Check(
    productVersion == "2026.9.7.5402" || productVersion?.StartsWith("2026.9.7.5402+", StringComparison.Ordinal) == true,
    "KSA.dll is the verified 2026.9.7.5402 build");
using (var stream = File.OpenRead(ksaAssemblyPath))
{
    Check(
        Convert.ToHexString(SHA256.HashData(stream)) == "A8A5164204EED962DF9D9025E99F523D9A23EE2CE6E802B18216A9DA35506D2F",
        "KSA.dll matches the exact verified control-hook binary");
}

var coreSettingsPath = Path.Combine(installedDirectory, "Content", "Core", "mod.toml");
Check(File.Exists(coreSettingsPath), "pinned Core/mod.toml exists");
if (File.Exists(coreSettingsPath))
{
    using var stream = File.OpenRead(coreSettingsPath);
    Check(
        Convert.ToHexString(SHA256.HashData(stream)) == "29B6DEFFBB8B7BA2EF0CB869D703309E6516AB86E962CA4AEEFEBB5F690551B5",
        "Core/mod.toml matches the exact verified simulation-rate contract");
}

var warpUniverseType = RequireType("KSA.Universe");
var warpInputEventsType = RequireType("KSA.InputEvents");
var simSpeedType = RequireType("KSA.SimSpeed");
var simSpeedDataType = RequireType("KSA.InputEvents+SimSpeedData");
var inputActionType = RequireType("KSA.InputAction");
var simSpeedBuffer = warpInputEventsType.GetField(
    "SimSpeedBuffer", BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly);
var simSpeedBufferAdd = simSpeedBuffer?.FieldType.GetMethod(
    "Add", BindingFlags.Instance | BindingFlags.Public, null, [simSpeedDataType], null);
var simSpeedApply = simSpeedDataType.GetMethod(
    "Apply", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly,
    null, Type.EmptyTypes, null);
var speedField = simSpeedDataType.GetField(
    "Speed", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
var actionField = simSpeedDataType.GetField(
    "Action", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
var setSimSpeed = warpUniverseType.GetMethod(
    "SetSimulationSpeed", BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly,
    null, [simSpeedType], null);
var increaseSimSpeed = warpUniverseType.GetMethod(
    "IncreaseSimulationSpeed", BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly,
    null, Type.EmptyTypes, null);
var decreaseSimSpeed = warpUniverseType.GetMethod(
    "DecreaseSimulationSpeed", BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly,
    null, Type.EmptyTypes, null);
var resetSimSpeed = warpUniverseType.GetMethod(
    "ResetSimulationSpeed", BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly,
    null, Type.EmptyTypes, null);
Check(
    simSpeedBuffer is { IsStatic: true, IsPublic: true } && simSpeedBufferAdd?.ReturnType == typeof(void) &&
    speedField?.FieldType == typeof(Nullable<>).MakeGenericType(simSpeedType) &&
    actionField?.FieldType == inputActionType,
    "InputEvents.SimSpeedBuffer accepts the exact public SimSpeedData command struct");
var simSpeedApplyReferences = simSpeedApply is null ? [] : ReferencedMembers(simSpeedApply);
Check(
    simSpeedApply is { IsPublic: true } && setSimSpeed is not null && increaseSimSpeed is not null &&
    decreaseSimSpeed is not null && resetSimSpeed is not null &&
    References(simSpeedApplyReferences, setSimSpeed) &&
    References(simSpeedApplyReferences, increaseSimSpeed) &&
    References(simSpeedApplyReferences, decreaseSimSpeed) &&
    References(simSpeedApplyReferences, resetSimSpeed),
    "SimSpeedData.Apply dispatches set/increase/decrease/default through KSA's native Universe commands");
var exactSetReferences = setSimSpeed is null ? [] : ReferencedMembers(setSimSpeed);
Check(
    exactSetReferences.All(member => member.DeclaringType?.FullName is "KSA.SimSpeed" or "KSA.Universe"),
    "native exact-rate selection contains no vehicle, thrust, atmosphere, or SOI veto path");
Check(
    Enum.GetNames(inputActionType).Contains("SimSpeedIncrease") &&
    Enum.GetNames(inputActionType).Contains("SimSpeedDecrease") &&
    Enum.GetNames(inputActionType).Contains("SimSpeedDefault"),
    "KSA exposes the exact native increase/decrease/default simulation-speed actions");
var warpApplyInputEvents = warpInputEventsType.GetMethod(
    "ApplyInputEvents", BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly,
    null, Type.EmptyTypes, null);
var prepareFrame = RequireType("KSA.Program").GetMethod(
    "PrepareFrame", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
Check(
    warpApplyInputEvents is not null && prepareFrame is not null &&
    References(ReferencedMembers(prepareFrame), warpApplyInputEvents),
    "Program.PrepareFrame drains InputEvents on KSA's frame thread");
var updateMenuPause = RequireType("KSA.Program").GetMethod(
    "UpdateMenuPause", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
var autoWarpStop = warpUniverseType.GetMethod(
    "AutoWarpStop", BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly,
    null, [typeof(bool)], null);
Check(
    updateMenuPause is not null && setSimSpeed is not null && autoWarpStop is not null &&
    References(ReferencedMembers(updateMenuPause), autoWarpStop) &&
    ReferencedMembers(updateMenuPause).Any(member => member.Name == "_menuPauseActive") &&
    ReferencedMembers(updateMenuPause).Any(member => member.Name == "SetSimulationSpeed"),
    "menu pause explicitly owns speed zero/restoration and stops auto-warp");
Check(
    increaseSimSpeed is not null && decreaseSimSpeed is not null && resetSimSpeed is not null && autoWarpStop is not null &&
    References(ReferencedMembers(increaseSimSpeed), autoWarpStop) &&
    References(ReferencedMembers(decreaseSimSpeed), autoWarpStop) &&
    References(ReferencedMembers(resetSimSpeed), autoWarpStop),
    "native increase/decrease/default actions cancel KSA auto-warp before changing speed");
var lockoutType = RequireType("KSA.Vehicle+ControlsLockout");
var createLockout = lockoutType.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly)
    .Single(method => method.Name == "Create");
Check(
    createLockout.GetParameters().First().ParameterType == typeof(double) &&
    ContainsDoubleConstant(createLockout, 30d),
    "KSA locks manual engine/rotation/translation controls above 30x; it does not veto the warp command");

var programType = RequireType("KSA.Program");
RequireProperty(programType, "ControlledVehicle", isStatic: true);
RequireProperty(programType, "IsControlledVehicleActive", isStatic: true);
var activeBindingField = programType.GetField(
    "IsControlledVehicleActive",
    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
Check(
    activeBindingField is { IsPublic: true, IsStatic: true } &&
    activeBindingField.FieldType == typeof(bool),
    "KSA.Program.IsControlledVehicleActive is the verified public static Boolean binding flag");
RequireMethod(programType, "GetPlayerTime", parameterCount: 0, isStatic: true);
RequireMethod(programType, "GetPlayerDeltaTime", parameterCount: 0, isStatic: true);

var vehicleType = RequireType("KSA.Vehicle");
var vehicleUpdateStateType = RequireType("KSA.VehicleUpdateState");
var controlledVehicleProperty = programType.GetProperty(
    "ControlledVehicle",
    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
Check(
    controlledVehicleProperty?.GetMethod is { IsPublic: true, IsStatic: true } &&
    controlledVehicleProperty.SetMethod is { IsPublic: true, IsStatic: true } &&
    controlledVehicleProperty.PropertyType == vehicleType,
    "KSA.Program.ControlledVehicle is the verified public static KSA.Vehicle property");
var controlledVehicleSetter = controlledVehicleProperty?.SetMethod;
foreach (var methodName in new[]
         {
             "GetPositionCci",
             "GetPositionCce",
             "GetPositionOrb",
             "GetVelocityCci",
             "GetVelocityCce",
             "GetVelocityOrb",
             "GetInertialSpeed",
             "GetSurfaceSpeed",
             "GetBarometricAltitude",
             "GetRadarAltitude",
         })
{
    RequireMethod(vehicleType, methodName, parameterCount: 0, isStatic: false);
}

RequireProperty(vehicleType, "FlightComputer", isStatic: false);
RequireProperty(vehicleType, "Parts", isStatic: false);
RequireProperty(vehicleType, "Orbit", isStatic: false);
RequireProperty(vehicleType, "_manualControlInputs", isStatic: false);
RequireMethod(vehicleType, "SetTarget", parameterCount: 2, isStatic: false);
Check(
    vehicleType.GetMember("IsControllable", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Length > 0,
    "KSA.Vehicle.IsControllable exists");

var physicsEnvironmentType = RequireType("KSA.PhysicsEnvironment");
var physicsEnvironmentProperty = vehicleType.GetProperty(
    "PhysicsEnvironment",
    BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
Check(
    physicsEnvironmentProperty?.GetMethod is { IsPublic: true, IsStatic: false } physicsEnvironmentGetter &&
    physicsEnvironmentProperty.PropertyType.IsByRef &&
    physicsEnvironmentProperty.PropertyType.GetElementType() == physicsEnvironmentType &&
    physicsEnvironmentGetter.ReturnParameter.GetRequiredCustomModifiers()
        .Any(type => type.FullName == "System.Runtime.InteropServices.InAttribute"),
    "KSA.Vehicle.PhysicsEnvironment is the exact public ref-readonly PhysicsEnvironment getter");
var atmosphericPressureField = physicsEnvironmentType.GetField(
    "AtmosphericPressure",
    BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
Check(
    atmosphericPressureField is { IsPublic: true, IsStatic: false } &&
    atmosphericPressureField.FieldType == typeof(float),
    "KSA.PhysicsEnvironment.AtmosphericPressure is the exact public Single field stored in pascals");

var physicalAtmosphereType = RequireType("KSA.PhysicalAtmosphereReference");
var pressureAtAltitude = physicalAtmosphereType.GetMethod(
    "GetAtmosphericPressureAtAltitude",
    BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly,
    binder: null,
    types: [typeof(double)],
    modifiers: null);
Check(
    pressureAtAltitude is { IsPublic: true, IsStatic: false } && pressureAtAltitude.ReturnType == typeof(double),
    "PhysicalAtmosphereReference.GetAtmosphericPressureAtAltitude(Double) is public instance Double (Pa)");
var cameraType = RequireType("KSA.Camera");
var pressureForCamera = physicalAtmosphereType.GetMethod(
    "GetAtmosphericPressure",
    BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly,
    binder: null,
    types: [cameraType],
    modifiers: null);
Check(
    pressureForCamera is { IsPublic: true, IsStatic: true } &&
    pressureForCamera.ReturnType == typeof(double) &&
    ContainsApproximateDoubleConstant(pressureForCamera, 9.86923266716013E-06, 1E-20),
    "PhysicalAtmosphereReference.GetAtmosphericPressure(Camera) is the public atmosphere-unit helper and applies Pa-to-atm");
var recomputePositionalValues = physicsEnvironmentType.GetMethod(
    "RecomputePositionalValues",
    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
Check(
    recomputePositionalValues is not null && pressureAtAltitude is not null && atmosphericPressureField is not null &&
    References(ReferencedMembers(recomputePositionalValues), pressureAtAltitude) &&
    References(ReferencedMembers(recomputePositionalValues), atmosphericPressureField),
    "PhysicsEnvironment stores GetAtmosphericPressureAtAltitude directly in AtmosphericPressure (Pa)");

var pressureReferenceType = RequireType("KSA.PressureReference");
var pressureSetValue = pressureReferenceType.GetMethods(
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
    .SingleOrDefault(method => method.Name == "SetValue");
Check(
    pressureSetValue is not null &&
    ContainsDoubleConstant(pressureSetValue, 100d) &&
    ContainsDoubleConstant(pressureSetValue, 100_000d) &&
    ContainsDoubleConstant(pressureSetValue, 101_325d),
    "PressureReference proves 1 mbar = 100 Pa, 1 bar = 100000 Pa, and 1 atm = 101325 Pa");

var rocketPerformanceType = RequireType("KSA.RocketPerformance");
var rocketMassFlowRate = rocketPerformanceType.GetField(
    "MassFlowRate",
    BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
var rocketTotalThrust = rocketPerformanceType.GetProperty(
    "TotalThrust",
    BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
Check(
    rocketMassFlowRate is { IsPublic: true, IsStatic: false } &&
    rocketMassFlowRate.FieldType == typeof(float),
    "KSA.RocketPerformance.MassFlowRate is the exact public current Single gate");
Check(
    rocketTotalThrust?.GetMethod is { IsPublic: true, IsStatic: false } &&
    rocketTotalThrust.PropertyType == typeof(float),
    "KSA.RocketPerformance.TotalThrust is the exact public Single getter in newtons");
var rocketNozzleStateType = RequireType("KSA.RocketNozzleState");
var nozzleStatePerformance = rocketNozzleStateType.GetField(
    "Performance",
    BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
var nozzleStateDirection = rocketNozzleStateType.GetField(
    "ThrustDirectionVehicleAsmb",
    BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
Check(
    nozzleStatePerformance is { IsPublic: true, IsStatic: false } &&
    nozzleStatePerformance.FieldType == rocketPerformanceType,
    "KSA.RocketNozzleState.Performance is the exact public current RocketPerformance field");
Check(
    nozzleStateDirection is { IsPublic: true, IsStatic: false } &&
    nozzleStateDirection.FieldType.FullName == "Brutal.Numerics.float3",
    "KSA.RocketNozzleState.ThrustDirectionVehicleAsmb is the exact public float3 direction field");
var float3Type = nozzleStateDirection?.FieldType;
var float3Fields = float3Type is null
    ? []
    : new[] { "X", "Y", "Z" }
        .Select(name => float3Type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly))
        .ToArray();
var float3Length = float3Type?.GetMethod(
    "Length",
    BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly,
    binder: null,
    types: Type.EmptyTypes,
    modifiers: null);
var float3Add = float3Type?.GetMethod(
    "op_Addition",
    BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly,
    binder: null,
    types: float3Type is null ? Type.EmptyTypes : [float3Type, float3Type],
    modifiers: null);
var float3Scale = float3Type?.GetMethod(
    "op_Multiply",
    BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly,
    binder: null,
    types: float3Type is null ? Type.EmptyTypes : [typeof(float), float3Type],
    modifiers: null);
Check(
    float3Fields.Length == 3 && float3Fields.All(field => field is { IsPublic: true } && field.FieldType == typeof(float)) &&
    float3Length?.ReturnType == typeof(float) && float3Add?.ReturnType == float3Type && float3Scale?.ReturnType == float3Type,
    "Brutal.Numerics.float3 exposes exact public X/Y/Z, Length(), addition, and Single scaling used for validated force sums");

var nozzlePerformanceType = RequireType("KSA.NozzlePerformance");
var momentumThrust = nozzlePerformanceType.GetMethod(
    "GetMomentumThrust",
    BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly,
    binder: null,
    types: Type.EmptyTypes,
    modifiers: null);
var pressureThrust = nozzlePerformanceType.GetMethod(
    "GetPressureThrust",
    BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly,
    binder: null,
    types: Type.EmptyTypes,
    modifiers: null);
var totalThrust = nozzlePerformanceType.GetMethod(
    "GetTotalThrust",
    BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly,
    binder: null,
    types: Type.EmptyTypes,
    modifiers: null);
Check(
    momentumThrust?.ReturnType == typeof(float) && pressureThrust?.ReturnType == typeof(float) &&
    totalThrust?.ReturnType == typeof(float) && totalThrust is not null &&
    momentumThrust is not null && pressureThrust is not null &&
    References(ReferencedMembers(totalThrust), momentumThrust) &&
    References(ReferencedMembers(totalThrust), pressureThrust),
    "NozzlePerformance.GetTotalThrust() is public Single and exactly combines momentum plus pressure thrust");
var engineDesignerType = RequireType("KSA.EngineDesigner");
var designerExhaustPressure = engineDesignerType.GetField(
    "_exhaustPressureMBar",
    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
Check(
    designerExhaustPressure is { IsPrivate: true, IsStatic: true } &&
    designerExhaustPressure.FieldType == typeof(float),
    "EngineDesigner._exhaustPressureMBar is an exact private static Single UI value and is not a telemetry input");
var deLavalConfigType = RequireType("KSA.DeLavalNozzleConfig");
var exitAreaField = deLavalConfigType.GetField(
    "ExitArea",
    BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
Check(
    exitAreaField is { IsPublic: true, IsStatic: false } && exitAreaField.FieldType == typeof(float),
    "DeLavalNozzleConfig.ExitArea is the exact public Single internal nozzle-area term and is not reconstructed by telemetry");

var engineControllerType = RequireType("KSA.EngineController");
var engineControllerStateType = RequireType("KSA.EngineControllerState");
var engineControllerGlobalStateType = RequireType("KSA.EngineControllerGlobalState");
var emptyStructType = RequireType("KSA.EmptyStruct");
var engineActiveProperty = engineControllerType.GetProperty(
    "IsActive",
    BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
var propellantAvailableField = engineControllerStateType.GetField(
    "IsPropellantAvailable",
    BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
Check(
    engineActiveProperty?.GetMethod is { IsPublic: true, IsStatic: false } &&
    engineActiveProperty.PropertyType == typeof(bool),
    "KSA.EngineController.IsActive is the exact public Boolean staging/enabled getter");
Check(
    propellantAvailableField is { IsPublic: true, IsStatic: false } &&
    propellantAvailableField.FieldType == typeof(bool),
    "KSA.EngineControllerState.IsPropellantAvailable is the exact public Boolean field");

var partsProperty = vehicleType.GetProperty("Parts", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
var partsField = vehicleType.GetField("Parts", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
var partsType = partsProperty?.PropertyType ?? partsField?.FieldType;
var rocketNozzlesProperty = partsType?.GetProperty("RocketNozzles", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
var rocketNozzlesField = partsType?.GetField("RocketNozzles", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
var rocketNozzlesType = rocketNozzlesProperty?.PropertyType ?? rocketNozzlesField?.FieldType;
var nozzleModulesAndStates = rocketNozzlesType?.GetProperty(
    "ModulesAndStates",
    BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
var statesProperty = partsType?.GetProperty("States", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
var statesField = partsType?.GetField("States", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
var nozzlePairType = GetForeachElementType(nozzleModulesAndStates?.PropertyType);
var nozzlePairStateField = nozzlePairType?.GetField("State", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
var nozzlePairStateProperty = nozzlePairType?.GetProperty("State", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
Check(
    (partsProperty?.GetMethod is { IsPublic: true } || partsField is { IsPublic: true }) &&
    (rocketNozzlesProperty?.GetMethod is { IsPublic: true } || rocketNozzlesField is { IsPublic: true }) &&
    nozzleModulesAndStates?.GetMethod is { IsPublic: true } &&
    (statesProperty?.GetMethod is { IsPublic: true } || statesField is { IsPublic: true }) &&
    ((nozzlePairStateField is { IsPublic: true } &&
      Dereference(nozzlePairStateField.FieldType) == rocketNozzleStateType) ||
     (nozzlePairStateProperty?.GetMethod is { IsPublic: true } &&
      Dereference(nozzlePairStateProperty.PropertyType) == rocketNozzleStateType)),
    "Vehicle.Parts exposes the exact public RocketNozzles/ModulesAndStates and States accessors used for current state");

var moduleStatefulOpenType = RequireType("KSA.ModuleStateful`4");
var engineModuleStatefulType = moduleStatefulOpenType.MakeGenericType(
    engineControllerType,
    engineControllerStateType,
    engineControllerGlobalStateType,
    emptyStructType);
var tryGetEngines = engineModuleStatefulType.GetMethods(BindingFlags.Static | BindingFlags.Public)
    .SingleOrDefault(method => method.Name == "TryGetFrom" && method.GetParameters().Length == 2);
var engineStateListType = tryGetEngines?.GetParameters()[1].ParameterType.GetElementType();
var engineModulesAndStates = engineStateListType?.GetProperty(
    "ModulesAndStates",
    BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
var enginePairType = GetForeachElementType(engineModulesAndStates?.PropertyType);
var enginePairModuleField = enginePairType?.GetField("Module", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
var enginePairModuleProperty = enginePairType?.GetProperty("Module", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
var enginePairStateField = enginePairType?.GetField("State", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
var enginePairStateProperty = enginePairType?.GetProperty("State", BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
Check(
    tryGetEngines is { IsPublic: true, IsStatic: true } &&
    tryGetEngines.ReturnType == typeof(bool) &&
    tryGetEngines.GetParameters()[1].ParameterType.IsByRef &&
    engineModulesAndStates?.GetMethod is { IsPublic: true } &&
    ((enginePairModuleField is { IsPublic: true } && Dereference(enginePairModuleField.FieldType) == engineControllerType) ||
     (enginePairModuleProperty?.GetMethod is { IsPublic: true } && Dereference(enginePairModuleProperty.PropertyType) == engineControllerType)) &&
    ((enginePairStateField is { IsPublic: true } && Dereference(enginePairStateField.FieldType) == engineControllerStateType) ||
     (enginePairStateProperty?.GetMethod is { IsPublic: true } && Dereference(enginePairStateProperty.PropertyType) == engineControllerStateType)),
    "ModuleStateful<EngineController,...>.TryGetFrom(ModuleStateList, out StateList) and ModulesAndStates are exact public accessors");

var sequencePerformanceListType = RequireType("KSA.SequencePerformanceList");
var performanceSequencesProperty = partsType?.GetProperty(
    "PerformanceSequences",
    BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
var performanceSequencesField = partsType?.GetField(
    "PerformanceSequences",
    BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
Check(
    (performanceSequencesProperty?.GetMethod is { IsPublic: true } &&
     performanceSequencesProperty.PropertyType == sequencePerformanceListType) ||
    (performanceSequencesField is { IsPublic: true } &&
     performanceSequencesField.FieldType == sequencePerformanceListType),
    "Vehicle.Parts.PerformanceSequences is the exact public SequencePerformanceList accessor");
var flightAmbientPressureField = sequencePerformanceListType.GetField(
    "_flightAmbientPressure",
    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
var rocketCoreType = RequireType("KSA.RocketCore");
var accumulateMaximum = sequencePerformanceListType.GetMethod(
    "AccumulateCoreThrustAtPressure",
    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
    binder: null,
    types:
    [
        rocketCoreType,
        typeof(float),
        nozzleStateDirection?.FieldType.MakeByRefType() ?? typeof(object).MakeByRefType(),
        typeof(float).MakeByRefType(),
    ],
    modifiers: null);
Check(
    flightAmbientPressureField is { IsPrivate: true, IsStatic: false } &&
    flightAmbientPressureField.FieldType == typeof(float),
    "SequencePerformanceList._flightAmbientPressure is the exact private instance Single field");
Check(
    accumulateMaximum is { IsPrivate: true, IsStatic: false } &&
    accumulateMaximum.ReturnType == typeof(void),
    "SequencePerformanceList.AccumulateCoreThrustAtPressure(RocketCore, Single, ref float3, ref Single) is exact private void");
var maximumThrustReferences = accumulateMaximum is null
    ? []
    : ReferencedMembers(accumulateMaximum);
Check(
    nozzleStateDirection is not null &&
    rocketTotalThrust?.GetMethod is not null && float3Scale is not null && float3Add is not null &&
    References(maximumThrustReferences, nozzleStateDirection) &&
    References(maximumThrustReferences, rocketTotalThrust.GetMethod) &&
    ReferencesMethod(maximumThrustReferences, "Brutal.Numerics.float3", float3Scale.Name) &&
    ReferencesMethod(maximumThrustReferences, "Brutal.Numerics.float3", float3Add.Name),
    "SequencePerformanceList maximum thrust accumulates vehicle-assembly direction times full-power nozzle thrust");
var recomputeForFlight = sequencePerformanceListType.GetMethod(
    "RecomputeForFlight",
    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
    binder: null,
    types: [typeof(float)],
    modifiers: null);
Check(
    recomputeForFlight is not null && flightAmbientPressureField is not null &&
    References(ReferencedMembers(recomputeForFlight), flightAmbientPressureField),
    "SequencePerformanceList.RecomputeForFlight refreshes the cached flight ambient pressure");

var pressureRefreshCaller = assembly.GetTypes()
    .SelectMany(type => type.GetMethods(BindingFlags.Instance | BindingFlags.Static |
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
    .FirstOrDefault(method =>
    {
        var references = ReferencedMembers(method);
        return recomputeForFlight is not null && atmosphericPressureField is not null &&
            References(references, recomputeForFlight) &&
            References(references, atmosphericPressureField);
    });
Check(
    pressureRefreshCaller is not null,
    "KSA passes PhysicsEnvironment.AtmosphericPressure to SequencePerformanceList.RecomputeForFlight");

var updateActiveNozzles = vehicleUpdateStateType.GetMethod(
    "UpdateActiveNozzles",
    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
    binder: null,
    types: [typeof(double)],
    modifiers: null);
Check(
    updateActiveNozzles is not null && nozzleStatePerformance is not null && nozzleStateDirection is not null &&
    rocketTotalThrust?.GetMethod is not null &&
    References(ReferencedMembers(updateActiveNozzles), nozzleStatePerformance) &&
    References(ReferencedMembers(updateActiveNozzles), nozzleStateDirection) &&
    References(ReferencedMembers(updateActiveNozzles), rocketTotalThrust.GetMethod),
    "VehicleUpdateState.UpdateActiveNozzles uses current RocketPerformance.TotalThrust times vehicle-assembly direction");

var flightComputerType = RequireType("KSA.FlightComputer");
var manualInputsType = RequireType("KSA.ManualControlInputs");
var commandEngineThrottles = flightComputerType.GetMethods(
        BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
    .SingleOrDefault(method => method.Name == "CommandEngineThrottles");
var commandEngineReferences = commandEngineThrottles is null
    ? []
    : ReferencedMembers(commandEngineThrottles);
Check(
    commandEngineThrottles is not null,
    "FlightComputer.CommandEngineThrottles is the exact engine-command routine");
Check(
    engineActiveProperty?.GetMethod is not null &&
    ReferencesMethod(commandEngineReferences, "KSA.EngineController", engineActiveProperty.GetMethod.Name),
    "FlightComputer engine commands gate on EngineController.IsActive");
Check(
    propellantAvailableField is not null && References(commandEngineReferences, propellantAvailableField),
    "FlightComputer engine commands gate on EngineControllerState.IsPropellantAvailable");
Check(
    engineActiveProperty?.SetMethod is null || !References(commandEngineReferences, engineActiveProperty.SetMethod),
    "manual engine-off and zero-throttle commands do not clear EngineController.IsActive");
var computeControl = flightComputerType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
    .SingleOrDefault(method =>
    {
        var parameters = method.GetParameters();
        return method.Name == "ComputeControl" &&
               parameters.Length == 3 &&
               parameters[1].ParameterType.IsByRef &&
               parameters[1].ParameterType.GetElementType() == manualInputsType;
    });
Check(computeControl is not null, "FlightComputer.ComputeControl has the verified three-argument manual-input signature");
if (computeControl is not null)
{
    var inputsParameter = computeControl.GetParameters()[1];
    Check(manualInputsType.IsValueType, "ManualControlInputs remains a value type");
    Check(inputsParameter.ParameterType.IsByRef && inputsParameter.IsIn,
        "ComputeControl remains an in/ref consumer of prepared manual inputs");
}

var prepareFromVehicle = vehicleUpdateStateType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
    .SingleOrDefault(method =>
    {
        var parameters = method.GetParameters();
        return method.Name == "PrepareFromVehicle" &&
               parameters.Length == 2 &&
               parameters[0].ParameterType == typeof(bool) &&
               parameters[1].ParameterType == manualInputsType;
    });
Check(prepareFromVehicle is not null,
    "VehicleUpdateState.PrepareFromVehicle has the verified manual-input handoff signature");
RequireProperty(vehicleUpdateStateType, "IsControlled", isStatic: false);
RequireProperty(vehicleUpdateStateType, "ManualControlInputs", isStatic: false);
var readOnlyVehicle = vehicleUpdateStateType.GetField(
    "ReadOnlyVehicle",
    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
Check(
    readOnlyVehicle is { IsPublic: true, IsStatic: false, IsInitOnly: true } &&
    readOnlyVehicle.FieldType == vehicleType,
    "VehicleUpdateState.ReadOnlyVehicle is the verified public init-only KSA.Vehicle identity field");

foreach (var memberName in new[] { "EngineOn", "EngineThrottle", "ThrusterCommandFlags" })
{
    var field = manualInputsType.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    var property = manualInputsType.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    Check(field is { IsInitOnly: false } || property?.SetMethod is not null, $"ManualControlInputs.{memberName} is writable");
}

// Canonical telemetry/control members. These checks intentionally name every
// KSA surface read by PinnedKsaTelemetry or the thrust-vector patch so an
// assembly drift fails closed before the mod advertises the capability.
foreach (var memberName in new[]
         {
             "AccelerationBody", "AngularAccelerationBody", "BodyRates", "BubbleOrigin",
             "CenterOfMassAsmb", "InertMass", "Parent", "PropellantMass", "Situation",
             "TotalMass", "TotalMassPropsBody",
         })
{
    RequirePublicInstanceMember(vehicleType, memberName);
}
foreach (var methodName in new[]
         {
             "GetAsmb2Cci", "GetBody2Cci", "GetMinThrottle", "GetPhysicsStates",
             "GetSurfaceVelocityCci", "PosAsmbToBody", "TryGetTerrainContactNormalBody",
         })
{
    RequirePublicInstanceMethod(vehicleType, methodName);
}
foreach (var fieldName in new[] { "AtmosphericDensity", "AtmosphericPressure", "GravitationBub", "TerrainRadius" })
{
    RequirePublicInstanceMember(physicsEnvironmentType, fieldName);
}

var bubbleOriginType = RequireType("KSA.BubbleOrigin");
RequirePublicInstanceMethod(bubbleOriginType, "GetBub2Cci");
var parentBodyType = RequireType("KSA.IParentBody");
foreach (var memberName in new[] { "Mu" }) RequirePublicInstanceMember(parentBodyType, memberName);
foreach (var methodName in new[]
         {
             "GetAngularVelocity", "GetAngularVelocityCce", "GetAngularVelocityCci",
             "GetCcf2Cce", "GetCcf2Cci", "GetCci2Cce", "GetNearSurfaceRadius",
         })
{
    RequirePublicInstanceMethod(parentBodyType, methodName);
}

var readOnlyPhysicsType = RequireType("KSA.ReadOnlyPhysicsStates");
RequirePublicInstanceMethod(readOnlyPhysicsType, "ComputeAirVelocityBody");
var dynamicPressure = physicalAtmosphereType.GetMethod(
    "GetDynamicPressure",
    BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly,
    binder: null,
    types: [vehicleType],
    modifiers: null);
Check(dynamicPressure?.ReturnType == typeof(double),
    "PhysicalAtmosphereReference.GetDynamicPressure(Vehicle) is exact public static Double");

foreach (var memberName in new[] { "Cores", "InstanceId", "MinimumThrottle", "Parent", "TemplateId" })
{
    RequirePublicInstanceMember(engineControllerType, memberName);
}
foreach (var memberName in new[] { "CommandThrottle", "CommandTime", "CommandBurnTime", "IsPropellantAvailable" })
{
    RequirePublicInstanceMember(engineControllerStateType, memberName);
}
var rocketCoreStateType = RequireType("KSA.RocketCoreState");
foreach (var memberName in new[] { "MassFlowRate", "Throttle" }) RequirePublicInstanceMember(rocketCoreStateType, memberName);
foreach (var memberName in new[] { "EffectiveExhaustVelocity", "MassFlowRate", "TotalThrust" })
{
    RequirePublicInstanceMember(rocketPerformanceType, memberName);
}
foreach (var memberName in new[] { "Performance", "ThrustDirectionVehicleAsmb", "ThrustLocationVehicleAsmb" })
{
    RequirePublicInstanceMember(rocketNozzleStateType, memberName);
}

var gimbalType = RequireType("KSA.Gimbal");
var gimbalStateType = RequireType("KSA.GimbalState");
var gimbalAxisType = RequireType("KSA.GimbalAxis");
foreach (var memberName in new[] { "AxisY", "AxisZ", "InstanceId", "Parent" }) RequirePublicInstanceMember(gimbalType, memberName);
foreach (var memberName in new[] { "AngleY", "AngleZ" }) RequirePublicInstanceMember(gimbalStateType, memberName);
RequirePublicInstanceMember(gimbalAxisType, "MaxAngle");

var massPropertiesType = RequireType("KSA.MassProperties");
RequirePublicInstanceMember(massPropertiesType, "Inertia");
var inertiaType = massPropertiesType.GetField("Inertia", BindingFlags.Instance | BindingFlags.Public)?.FieldType;
Check(inertiaType?.FullName == "BepuUtilities.Symmetric3x3", "MassProperties.Inertia uses exact BepuUtilities.Symmetric3x3");
if (inertiaType is not null)
{
    foreach (var fieldName in new[] { "XX", "YX", "YY", "ZX", "ZY", "ZZ" }) RequirePublicInstanceMember(inertiaType, fieldName);
}

var situationExType = RequireType("KSA.SituationEx");
var situationType = RequireType("KSA.Situation");
var hasAnyContact = situationExType.GetMethod(
    "HasAnyContact", BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly,
    binder: null, types: [situationType], modifiers: null);
Check(hasAnyContact?.ReturnType == typeof(bool), "SituationEx.HasAnyContact(Situation) is exact public static Boolean");
var referenceFrameExType = RequireType("KSA.VehicleReferenceFrameEx");
Check(referenceFrameExType.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly)
        .Any(method => method.Name == "GetEnu2Cci" && method.GetParameters().Length == 1),
    "VehicleReferenceFrameEx.GetEnu2Cci(double3) is exact public static local-frame source");

var vehicleUpdateDataType = RequireType("KSA.VehicleUpdateData");
RequirePublicInstanceMember(vehicleUpdateDataType, "NewFlightComputer");
var updateAttitudeTarget = flightComputerType.GetMethod(
    "UpdateAttitudeTarget", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
Check(updateAttitudeTarget is { IsPrivate: true } && updateAttitudeTarget.GetParameters() is [{ ParameterType.IsByRef: true }],
    "FlightComputer.UpdateAttitudeTarget(ref FlightComputerNavigation) is the exact private target hook");
var attitudeTargetType = RequireType("KSA.AttitudeTarget");
foreach (var memberName in new[] { "RatesCci", "Target2Cci" }) RequirePublicInstanceMember(attitudeTargetType, memberName);
RequirePublicInstanceMember(flightComputerType, "AttitudeTarget");
foreach (var enumName in new[] { "Auto" })
{
    Check(Enum.GetNames(RequireType("KSA.FlightComputerAttitudeMode")).Contains(enumName), $"FlightComputerAttitudeMode contains {enumName}");
}
Check(Enum.GetNames(RequireType("KSA.FlightComputerAttitudeTrackTarget")).Contains("Custom"),
    "FlightComputerAttitudeTrackTarget contains Custom");
Check(Enum.GetNames(RequireType("KSA.FlightComputerRollMode")).Contains("Decoupled"),
    "FlightComputerRollMode contains Decoupled");
var quaternionExType = RequireType("KSA.QuaternionEx");
Check(quaternionExType.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly)
        .Any(method => method.Name == "FromToRotation" && method.GetParameters().Length == 4),
    "QuaternionEx.FromToRotation(double3,double3,fallback,fallback) is the exact public direction mapper");

var thrusterFlagsType = RequireType("KSA.ThrusterMapFlags");
foreach (var flag in new[]
         {
             "RollRight", "RollLeft", "PitchUp", "PitchDown", "YawRight", "YawLeft",
             "TranslateForward", "TranslateBackward", "TranslateRight", "TranslateLeft", "TranslateUp", "TranslateDown",
         })
{
    Check(Enum.GetNames(thrusterFlagsType).Contains(flag, StringComparer.Ordinal), $"ThrusterMapFlags contains {flag}");
}

var sequenceListType = RequireType("KSA.SequenceList");
var stageMethod = sequenceListType.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
    .FirstOrDefault(method => method.Name == "ActivateNextSequence" && method.GetParameters().Length is 0 or 1);
Check(stageMethod is not null, "SequenceList exposes ActivateNextSequence");

var astronomicalType = RequireType("KSA.Astronomical");
var keyHashType = RequireType("KSA.KeyHash");
var celestialSystemType = RequireType("KSA.CelestialSystem");
var allProperty = celestialSystemType.GetProperty("All", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
var allField = celestialSystemType.GetField("All", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
var celestialLookupType = allProperty?.PropertyType ?? allField?.FieldType;
Check(
    allProperty?.GetMethod is { IsPublic: true, IsStatic: false } &&
    allField is null &&
    celestialLookupType is { IsGenericType: true } &&
    celestialLookupType.GetGenericTypeDefinition().FullName == "KSA.LookupCollection`1" &&
    celestialLookupType.GetGenericArguments() is [{ } lookupElementType] &&
    lookupElementType == astronomicalType,
    "KSA.CelestialSystem.All is the exact public LookupCollection<Astronomical> inventory");
if (celestialLookupType is not null)
{
    var lookupCount = celestialLookupType.GetProperty(
        "Count",
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    Check(
        lookupCount?.GetMethod is { IsPublic: true, IsStatic: false } &&
        lookupCount.PropertyType == typeof(int),
        "CelestialSystem.All exposes the exact public Int32 Count");

    var integerGetIndexMethods = celestialLookupType
        .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
        .Where(method =>
            method.Name == "GetIndex" &&
            !method.IsStatic &&
            method.GetParameters() is [{ ParameterType: var parameterType }] &&
            parameterType == typeof(int))
        .ToArray();
    var exactGetIndex = integerGetIndexMethods.SingleOrDefault(method =>
        !method.IsGenericMethod &&
        method.ReturnType == astronomicalType);
    var collidingGenericGetIndex = integerGetIndexMethods.SingleOrDefault(method =>
        method.IsGenericMethodDefinition &&
        method.GetGenericArguments().Length == 1 &&
        method.ReturnType.IsGenericParameter);
    Check(
        exactGetIndex is { IsPublic: true },
        "CelestialSystem.All exposes the exact non-generic Astronomical GetIndex(Int32)");
    Check(
        collidingGenericGetIndex is { IsPublic: true },
        "CelestialSystem.All retains the colliding open-generic GetIndex<T>(Int32) overload");
    Check(
        !typeof(System.Collections.IEnumerable).IsAssignableFrom(celestialLookupType),
        "CelestialSystem.All requires Count/GetIndex enumeration rather than IEnumerable");

    var lookupDictionaryField = celestialLookupType.GetField(
        "_collection",
        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
    var lookupListField = celestialLookupType.GetField(
        "_all",
        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
    Check(
        lookupDictionaryField?.FieldType == typeof(Dictionary<,>).MakeGenericType(keyHashType, astronomicalType) &&
        lookupListField?.FieldType == typeof(List<>).MakeGenericType(astronomicalType),
        "CelestialSystem.All couples the KeyHash lookup to the indexed Astronomical list");
}

var universeType = RequireType("KSA.Universe");
var getElapsedSeconds = universeType.GetMethod(
    "GetElapsedSeconds",
    BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly,
    binder: null,
    types: Type.EmptyTypes,
    modifiers: null);
Check(
    getElapsedSeconds?.ReturnType == typeof(double),
    "Universe.GetElapsedSeconds() is the exact public static simulation clock");
var currentSystemProperty = universeType.GetProperty(
    "CurrentSystem",
    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
Check(
    currentSystemProperty?.GetMethod is { IsStatic: true } &&
    currentSystemProperty.PropertyType == celestialSystemType,
    "Universe.CurrentSystem is the exact static current-system source");
var vehiclesInFrameProperty = programType.GetProperty(
    "VehiclesInFrame",
    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
var refreshVehiclesInFrame = programType.GetMethod(
    "RefreshVehiclesInFrame",
    BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly,
    binder: null,
    types: Type.EmptyTypes,
    modifiers: null);
Check(
    vehiclesInFrameProperty?.GetMethod is { IsPublic: true, IsStatic: true } &&
    vehiclesInFrameProperty.PropertyType == typeof(ReadOnlySpan<>).MakeGenericType(vehicleType) &&
    refreshVehiclesInFrame?.ReturnType == typeof(void),
    "Program.VehiclesInFrame is the exact derived ReadOnlySpan<Vehicle> frame inventory");
if (refreshVehiclesInFrame is not null &&
    currentSystemProperty?.GetMethod is not null &&
    allProperty?.GetMethod is not null)
{
    var references = ReferencedMembers(refreshVehiclesInFrame);
    Check(
        References(references, currentSystemProperty.GetMethod) &&
        References(references, allProperty.GetMethod),
        "Program.RefreshVehiclesInFrame derives its vehicle span from CurrentSystem.All");
}
var getAstronomicalById = celestialSystemType
    .GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
    .SingleOrDefault(method =>
        method.Name == "Get" &&
        !method.IsStatic &&
        !method.IsGenericMethod &&
        method.ReturnType == astronomicalType &&
        method.GetParameters() is [{ ParameterType: var parameterType }] &&
        parameterType == typeof(string));
Check(getAstronomicalById is not null,
    "CelestialSystem.Get(string) is the exact non-generic opaque-ID resolver");

var keyHashCode = keyHashType.GetProperty(
    "Code",
    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
var keyHashMake = keyHashType.GetMethod(
    "Make",
    BindingFlags.Static | BindingFlags.Public | BindingFlags.DeclaredOnly,
    binder: null,
    types: [typeof(ReadOnlySpan<char>)],
    modifiers: null);
var keyHashMakeInternal = keyHashType.GetMethods(
        BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
    .SingleOrDefault(method =>
        method.Name == "MakeInternal" &&
        method.ReturnType == keyHashType &&
        method.GetParameters().Length == 2);
Check(
    keyHashCode?.PropertyType == typeof(uint) &&
    keyHashMake?.ReturnType == keyHashType &&
    keyHashMakeInternal is { IsPrivate: true },
    "KeyHash is the exact UInt32 opaque-ID hash contract");
if (keyHashMakeInternal is not null)
{
    var references = ReferencedMembers(keyHashMakeInternal);
    Check(
        ReferencesMethod(references, "System.MemoryExtensions", "ToLowerInvariant") &&
        ReferencesMethod(references, "Brutal.Strings.Utf8", "GetBytes") &&
        ReferencesMethod(references, "System.IO.Hashing.Crc32", "HashToUInt32"),
        "KeyHash lowercases invariantly, UTF-8 encodes, and computes a 32-bit CRC32");
}

if (celestialLookupType is not null && keyHashMake is not null)
{
    var declaredLookupMethods = celestialLookupType.GetMethods(
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly);
    var lookupGetByString = declaredLookupMethods.SingleOrDefault(method =>
        method.Name == "Get" &&
        !method.IsGenericMethod &&
        method.ReturnType == astronomicalType &&
        method.GetParameters() is [{ ParameterType: var parameterType }] &&
        parameterType == typeof(string));
    var lookupGetByHash = declaredLookupMethods.SingleOrDefault(method =>
        method.Name == "Get" &&
        !method.IsGenericMethod &&
        method.ReturnType == astronomicalType &&
        method.GetParameters() is [{ ParameterType: var parameterType }] &&
        parameterType == keyHashType);
    Check(
        lookupGetByString is { IsGenericMethod: false } &&
        lookupGetByString.ReturnType == astronomicalType &&
        lookupGetByHash is { IsGenericMethod: false } &&
        lookupGetByHash.ReturnType == astronomicalType,
        "LookupCollection<Astronomical> has exact string and KeyHash resolvers");
    if (lookupGetByString is not null && lookupGetByHash is not null)
    {
        var references = ReferencedMembers(lookupGetByString);
        Check(
            References(references, keyHashMake) &&
            References(references, lookupGetByHash),
            "LookupCollection string resolution hashes the ID before the KeyHash lookup");
    }
}

var astronomicalId = astronomicalType.GetProperty(
    "Id",
    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
var astronomicalHash = astronomicalType.GetProperty(
    "Hash",
    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
var astronomicalTemplateType = RequireType("KSA.AstronomicalTemplate");
var astronomicalConstructor = astronomicalType
    .GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
    .SingleOrDefault(constructor =>
        constructor.GetParameters() is
        [
            { ParameterType: var systemParameter },
            { ParameterType: var templateParameter },
            { ParameterType: var idParameter },
            { ParameterType: var registerParameter },
        ] &&
        systemParameter == celestialSystemType &&
        templateParameter == astronomicalTemplateType &&
        idParameter == typeof(string) &&
        registerParameter == typeof(bool));
var registerAstronomical = celestialSystemType.GetMethod(
    "Register",
    BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly,
    binder: null,
    types: [astronomicalType],
    modifiers: null);
Check(
    astronomicalId?.PropertyType == typeof(string) &&
    astronomicalHash?.PropertyType == keyHashType &&
    astronomicalConstructor is not null &&
    registerAstronomical?.ReturnType == typeof(void),
    "Astronomical binds its string Id and KeyHash to current-system registration");
if (astronomicalConstructor is not null && keyHashMake is not null && registerAstronomical is not null)
{
    var references = ReferencedMembers(astronomicalConstructor);
    Check(
        References(references, keyHashMake) &&
        References(references, registerAstronomical),
        "Astronomical construction hashes the exact ID and registers the same object in its system");
}

var isDisposed = vehicleType.GetProperty(
    "IsDisposed",
    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
Check(isDisposed?.PropertyType == typeof(bool) && isDisposed.GetMethod is not null,
    "Vehicle.IsDisposed is the exact loaded-inventory exclusion gate");
var disposeVehicle = vehicleType.GetMethod(
    "Dispose",
    BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly,
    binder: null,
    types: [typeof(bool)],
    modifiers: null);
var deregisterAstronomical = celestialSystemType.GetMethod(
    "Deregister",
    BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly,
    binder: null,
    types: [astronomicalType],
    modifiers: null);
Check(
    disposeVehicle?.ReturnType == typeof(void) &&
    isDisposed?.SetMethod is not null &&
    deregisterAstronomical?.ReturnType == typeof(void),
    "Vehicle disposal exposes the exact disposed-state and current-system removal path");
if (disposeVehicle is not null && isDisposed?.SetMethod is not null && deregisterAstronomical is not null)
{
    var references = ReferencedMembers(disposeVehicle);
    Check(
        References(references, isDisposed.SetMethod) &&
        References(references, deregisterAstronomical),
        "Vehicle.Dispose(Boolean) marks the object disposed and deregisters it from the current system");
}

var controlVehicle = universeType.GetMethods(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.DeclaredOnly)
    .SingleOrDefault(method =>
        method.Name == "ControlVehicle" &&
        method.IsPrivate &&
        method.ReturnType == typeof(void) &&
        method.GetParameters() is [{ ParameterType: var parameterType }] &&
        parameterType == typeof(string));
Check(controlVehicle is not null,
    "Universe.ControlVehicle(string) is the exact private static switch entrypoint");
var terminalActions = controlVehicle?.CustomAttributes
    .Where(attribute =>
        attribute.AttributeType.FullName == "Brutal.ImGuiApi.Abstractions.TerminalActionAttribute")
    .ToArray() ?? [];
var terminalActionArguments = terminalActions.Length == 1
    ? terminalActions[0].ConstructorArguments
    : [];
Check(
    terminalActions.Length == 1 &&
    terminalActionArguments.Count == 3 &&
    terminalActionArguments[0].ArgumentType == typeof(string) &&
    Equals(terminalActionArguments[0].Value, "control") &&
    terminalActionArguments[1].ArgumentType == typeof(string) &&
    Equals(terminalActionArguments[1].Value, "Set what vehicle you are controlling.") &&
    terminalActionArguments[2].ArgumentType.FullName == "Brutal.ImGuiApi.Abstractions.ArgParseMode" &&
    Convert.ToInt32(terminalActionArguments[2].Value) == 0,
    "Universe.ControlVehicle is the exact registered control console command");

var inputEventsType = RequireType("KSA.InputEvents");
var vehicleResourcesChangeType = RequireType("KSA.InputEvents+VehicleResourcesChangeData");
var resourcesVehicle = vehicleResourcesChangeType.GetField(
    "Vehicle",
    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
var resourcesRefill = vehicleResourcesChangeType.GetField(
    "Refill",
    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
var resourcesEmpty = vehicleResourcesChangeType.GetField(
    "Empty",
    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
var resourcesControl = vehicleResourcesChangeType.GetField(
    "Control",
    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
var resourcesApply = vehicleResourcesChangeType.GetMethod(
    "Apply",
    BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly,
    binder: null,
    types: Type.EmptyTypes,
    modifiers: null);
var resourcesBuffer = inputEventsType.GetField(
    "VehicleResourcesChangeBuffer",
    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
var resourcesBufferAdd = resourcesBuffer?.FieldType.GetMethod(
    "Add",
    BindingFlags.Instance | BindingFlags.Public,
    binder: null,
    types: [vehicleResourcesChangeType],
    modifiers: null);
var applyInputEvents = inputEventsType.GetMethod(
    "ApplyInputEvents",
    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
    binder: null,
    types: Type.EmptyTypes,
    modifiers: null);
Check(
    resourcesVehicle?.FieldType == vehicleType &&
    resourcesRefill?.FieldType == typeof(bool) &&
    resourcesEmpty?.FieldType == typeof(bool) &&
    resourcesControl?.FieldType == typeof(bool) &&
    resourcesApply?.ReturnType == typeof(void) &&
    resourcesBuffer is { IsStatic: true } &&
    resourcesBufferAdd?.ReturnType == typeof(void) &&
    applyInputEvents?.ReturnType == typeof(void),
    "vehicle-control switching uses the exact queued resources-change event contract");

if (controlVehicle is not null && currentSystemProperty?.GetMethod is not null &&
    getAstronomicalById is not null && resourcesVehicle is not null &&
    resourcesRefill is not null && resourcesEmpty is not null && resourcesControl is not null &&
    resourcesBuffer is not null && resourcesBufferAdd is not null && controlledVehicleSetter is not null)
{
    var references = ReferencedMembers(controlVehicle);
    Check(
        References(references, currentSystemProperty.GetMethod) &&
        References(references, getAstronomicalById) &&
        References(references, resourcesVehicle) &&
        References(references, resourcesRefill) &&
        References(references, resourcesEmpty) &&
        References(references, resourcesControl) &&
        References(references, resourcesBuffer) &&
        References(references, resourcesBufferAdd),
        "Universe.ControlVehicle resolves the current-system ID and enqueues Control=true without direct binding assignment");
    Check(!References(references, controlledVehicleSetter),
        "Universe.ControlVehicle does not directly assign Program.ControlledVehicle");
}

if (resourcesApply is not null && resourcesControl is not null && controlledVehicleSetter is not null &&
    activeBindingField is not null)
{
    var references = ReferencedMembers(resourcesApply);
    Check(
        References(references, resourcesControl) &&
        References(references, controlledVehicleSetter) &&
        !References(references, activeBindingField),
        "VehicleResourcesChangeData.Apply changes the controlled vehicle but not the active-authority flag atomically");
}

if (applyInputEvents is not null && resourcesBuffer is not null)
{
    Check(References(ReferencedMembers(applyInputEvents), resourcesBuffer),
        "InputEvents.ApplyInputEvents consumes the vehicle resources-change buffer");
}

Console.WriteLine($"{(failures.Count == 0 ? "PASS" : "FAIL")} KSA compatibility contract: {failures.Count} failure(s)");
foreach (var failure in failures)
{
    Console.Error.WriteLine($"  {failure}");
}
return failures.Count == 0 ? 0 : 1;

Type RequireType(string fullName)
{
    var type = assembly.GetType(fullName, throwOnError: false);
    Check(type is not null, $"Type {fullName} exists");
    return type ?? typeof(object);
}

void RequireProperty(Type type, string name, bool isStatic)
{
    var flags = BindingFlags.Public | BindingFlags.NonPublic | (isStatic ? BindingFlags.Static : BindingFlags.Instance);
    Check(type.GetProperty(name, flags) is not null || type.GetField(name, flags) is not null, $"{type.FullName}.{name} exists");
}

void RequireMethod(Type type, string name, int parameterCount, bool isStatic)
{
    var flags = BindingFlags.Public | BindingFlags.NonPublic | (isStatic ? BindingFlags.Static : BindingFlags.Instance);
    Check(type.GetMethods(flags).Any(method => method.Name == name && method.GetParameters().Length == parameterCount), $"{type.FullName}.{name}({parameterCount}) exists");
}

void RequirePublicInstanceMember(Type type, string name)
{
    const BindingFlags flags = BindingFlags.Instance | BindingFlags.Public;
    var property = type.GetProperty(name, flags);
    var field = type.GetField(name, flags);
    Check(property?.GetMethod is { IsPublic: true, IsStatic: false } || field is { IsPublic: true, IsStatic: false },
        $"{type.FullName}.{name} is public instance data");
}

void RequirePublicInstanceMethod(Type type, string name)
{
    Check(type.GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Any(method => method.Name == name && !method.IsStatic),
        $"{type.FullName}.{name} is a public instance method");
}

void Check(bool condition, string description)
{
    if (condition)
    {
        Console.WriteLine($"PASS {description}");
    }
    else
    {
        failures.Add(description);
    }
}

static IReadOnlyList<MemberInfo> ReferencedMembers(MethodBase method)
{
    var body = method.GetMethodBody();
    var il = body?.GetILAsByteArray();
    if (il is null) return [];
    var opCodes = typeof(OpCodes)
        .GetFields(BindingFlags.Public | BindingFlags.Static)
        .Where(field => field.FieldType == typeof(OpCode))
        .Select(field => (OpCode)field.GetValue(null)!)
        .ToDictionary(opCode => unchecked((ushort)opCode.Value));
    var references = new List<MemberInfo>();
    var index = 0;
    while (index < il.Length)
    {
        ushort value = il[index++];
        if (value == 0xfe)
        {
            if (index >= il.Length) break;
            value = (ushort)(0xfe00 | il[index++]);
        }
        if (!opCodes.TryGetValue(value, out var opCode))
        {
            throw new InvalidOperationException($"Unknown IL opcode 0x{value:x4} in {method}.");
        }

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
                try
                {
                    var member = method.Module.ResolveMember(
                        token,
                        method.DeclaringType?.GetGenericArguments(),
                        method is MethodInfo methodInfo ? methodInfo.GetGenericArguments() : null);
                    if (member is not null) references.Add(member);
                }
                catch (Exception exception) when (exception is ArgumentException or BadImageFormatException or TypeLoadException)
                {
                    // A probe below will fail if a required reference cannot be resolved.
                }
                break;
            }
            case OperandType.InlineSwitch:
            {
                var count = BitConverter.ToInt32(il, index);
                index += 4 + checked(count * 4);
                break;
            }
            default:
                throw new InvalidOperationException($"Unsupported IL operand {opCode.OperandType} in {method}.");
        }
    }
    return references;
}

static bool References(IEnumerable<MemberInfo> references, MemberInfo member) =>
    references.Any(reference => reference.Module == member.Module && reference.MetadataToken == member.MetadataToken);

static bool ReferencesMethod(
    IEnumerable<MemberInfo> references,
    string declaringTypeFullName,
    string methodName) =>
    references.Any(reference =>
        reference is MethodBase method &&
        method.DeclaringType?.FullName == declaringTypeFullName &&
        method.Name == methodName);

static Type? GetForeachElementType(Type? enumerableType)
{
    var getEnumerator = enumerableType?.GetMethod(
        "GetEnumerator",
        BindingFlags.Instance | BindingFlags.Public,
        binder: null,
        types: Type.EmptyTypes,
        modifiers: null);
    var current = getEnumerator?.ReturnType.GetProperty(
        "Current",
        BindingFlags.Instance | BindingFlags.Public);
    var currentType = current?.PropertyType;
    return currentType?.IsByRef == true ? currentType.GetElementType() : currentType;
}

static Type? Dereference(Type? type) => type?.IsByRef == true ? type.GetElementType() : type;

static bool ContainsDoubleConstant(MethodBase method, double expected)
{
    var il = method.GetMethodBody()?.GetILAsByteArray();
    if (il is null) return false;
    for (var index = 0; index <= il.Length - 9; index++)
    {
        if (il[index] == unchecked((byte)OpCodes.Ldc_R8.Value) &&
            BitConverter.ToDouble(il, index + 1) == expected) return true;
    }
    return false;
}

static bool ContainsApproximateDoubleConstant(MethodBase method, double expected, double tolerance)
{
    var il = method.GetMethodBody()?.GetILAsByteArray();
    if (il is null) return false;
    for (var index = 0; index <= il.Length - 9; index++)
    {
        if (il[index] == unchecked((byte)OpCodes.Ldc_R8.Value) &&
            Math.Abs(BitConverter.ToDouble(il, index + 1) - expected) <= tolerance) return true;
    }
    return false;
}

static string ResolveInputFile(string environmentName, params string[] fallbacks)
{
    var configured = ResolveEnvironmentPath(environmentName);
    if (configured is not null) return configured;
    return fallbacks.Select(Path.GetFullPath).FirstOrDefault(File.Exists) ?? Path.GetFullPath(fallbacks[0]);
}

static string? ResolveEnvironmentPath(string name)
{
    var value = Environment.GetEnvironmentVariable(name);
    if (string.IsNullOrWhiteSpace(value)) return null;
    if (OperatingSystem.IsWindows() && value.Length >= 3 && value[0] == '/' && char.IsAsciiLetter(value[1]) && value[2] == '/')
    {
        value = $"{char.ToUpperInvariant(value[1])}:\\{value[3..].Replace('/', '\\')}";
    }
    return Path.GetFullPath(value);
}
