using System.Text;
using KittenIO.Protocol;

var failures = new List<string>();

void Check(bool condition, string message)
{
    if (!condition)
    {
        failures.Add(message);
    }
}

var fixtureRoot = Path.Combine(AppContext.BaseDirectory, "fixtures", "v1");
var fixtures = Directory.GetFiles(fixtureRoot, "*.json").Order(StringComparer.Ordinal).ToArray();
Check(fixtures.Length == 7, "exactly seven canonical v1 fixtures are packaged");
Check(ProtocolConstants.Version == 1, "canonical protocol version is 1");
Check(ProtocolConstants.SupportedVersions.SequenceEqual([1]), "only protocol version 1 is advertised");

var envelopes = new List<WireEnvelope>();
foreach (var fixture in fixtures)
{
    var bytes = File.ReadAllBytes(fixture);
    Check(WireCodec.TryDeserialize(bytes, out var envelope, out var error),
        $"{Path.GetFileName(fixture)} decodes: {error?.Message}");
    if (envelope is null)
    {
        continue;
    }
    Check(envelope.Version == 1, $"{Path.GetFileName(fixture)} uses protocol 1");
    envelopes.Add(envelope);
}

var hello = envelopes.Single(item => item.Type == ProtocolConstants.MessageTypes.Hello);
Check(WireCodec.TryReadPayload<HelloPayload>(hello, out var helloPayload, out var helloError),
    $"hello uses the canonical nested capability set: {helloError?.Message}");
if (helloPayload is not null)
{
    Check(helloPayload.SupportedVersions.SequenceEqual([1]), "hello advertises only version 1");
    Check(helloPayload.Capabilities.Control.ThrustVectorTarget, "hello advertises thrust-vector control");
    Check(helloPayload.Capabilities.TimeWarp.Control, "hello advertises time-warp control");
}

var telemetry = envelopes.Single(item => item.Type == ProtocolConstants.MessageTypes.Telemetry);
Check(WireCodec.TryReadPayload<FlightStatePayload>(telemetry, out var state, out var stateError),
    $"canonical telemetry decodes: {stateError?.Message}");
if (state is not null)
{
    Check(state.Vehicle?.TargetId == "body:moon:2", "target telemetry is retained");
    Check(state.Vehicle?.PositionOrbM == new Vector3Value(100, 0, 0), "ORB position is retained");
    Check(state.Vehicle?.VelocityOrbMps == new Vector3Value(-12, 0, 0), "ORB velocity is retained");
    Check(state.Vehicle?.TotalAccelerationCceMps2 == new Vector3Value(-9.1, 0, 0), "CCE acceleration is retained");
    Check(state.Vehicle?.TotalAccelerationOrbMps2 == new Vector3Value(-9.1, 0, 0), "ORB acceleration is retained");
    Check(state.Vehicle?.InertialSpeedMps == 12, "inertial speed is retained");
    Check(state.Vehicle?.SurfaceSpeedMps == 12, "surface speed is retained");
    Check(state.Vehicle?.Orbit?.PeriodS == 1800, "orbit elements are retained");
    Check(state.ManualControl?.Yaw == AxisCommand.Positive, "manual player input is retained");
    Check(state.Propulsion?.MaximumAvailableThrustN == 150000, "thrust has one propulsion path");
    Check(state.Atmosphere?.PressurePa == 90000, "pressure has one atmosphere path");
}

var controlEnvelope = envelopes.Single(item => item.Type == ProtocolConstants.MessageTypes.Control);
Check(WireCodec.TryReadPayload<ControlFramePayload>(controlEnvelope, out var control, out var controlReadError),
    $"thrust-vector control decodes: {controlReadError?.Message}");
Check(control is not null && WireCodec.TryValidateControl(control, out _),
    "canonical thrust-vector control validates");
if (control is not null)
{
    var invalid = control with
    {
        ThrustVectorTarget = new ThrustVectorTarget { DirectionCci = new Vector3Value(0, 2, 0) },
    };
    Check(!WireCodec.TryValidateControl(invalid, out var invalidError) && invalidError?.Code == "invalid_thrust_vector",
        "non-unit thrust vectors are rejected");
}

var telemetryJson = File.ReadAllText(Path.Combine(fixtureRoot, "03-telemetry.json"), Encoding.UTF8);
foreach (var retiredVersion in new[] { 2, 3 })
{
    var retired = Encoding.UTF8.GetBytes(telemetryJson.Replace(
        "\"version\": 1", $"\"version\": {retiredVersion}", StringComparison.Ordinal));
    Check(!WireCodec.TryDeserialize(retired, out _, out var error) && error?.Code == "unsupported_version",
        $"protocol version {retiredVersion} is rejected");
}

var unknownPayload = Encoding.UTF8.GetBytes(telemetryJson.Replace(
    "\"frame_number\": 2456,", "\"frame_number\": 2456, \"invented\": true,", StringComparison.Ordinal));
Check(WireCodec.TryDeserialize(unknownPayload, out var unknownEnvelope, out _), "unknown-field test envelope decodes");
Check(unknownEnvelope is not null && !WireCodec.TryReadPayload<FlightStatePayload>(unknownEnvelope, out _, out _),
    "unknown telemetry fields are rejected");

var missingPayload = Encoding.UTF8.GetBytes(telemetryJson.Replace(
    "\"player_time_s\": 123.5,", string.Empty, StringComparison.Ordinal));
Check(WireCodec.TryDeserialize(missingPayload, out var missingEnvelope, out _), "missing-field test envelope decodes");
Check(missingEnvelope is not null && !WireCodec.TryReadPayload<FlightStatePayload>(missingEnvelope, out _, out _),
    "missing required telemetry fields are rejected");

var publicTypes = typeof(FlightStatePayload).Assembly.GetExportedTypes();
Check(publicTypes.All(type => !type.Name.EndsWith("V2", StringComparison.Ordinal) &&
                              !type.Name.EndsWith("V3", StringComparison.Ordinal)),
    "no version-suffixed public DTOs remain");
Check(publicTypes.All(type => type.Name != "ControlOverride"), "digital ControlOverride is absent");
Check(typeof(FlightStatePayload).GetProperty("ActiveVehicle") is null, "active_vehicle projection is absent");
Check(typeof(FlightStatePayload).GetProperty("Control") is null, "control projection is absent");
Check(typeof(VehicleState).GetProperty("CurrentThrustN") is null, "vehicle thrust duplicate is absent");
Check(typeof(VehicleState).GetProperty("AmbientPressurePa") is null, "vehicle pressure duplicate is absent");
Check(typeof(ControlFramePayload).GetProperty("Pitch") is null, "digital attitude axes are absent");
Check(typeof(ControlFramePayload).GetProperty("TranslateForward") is null, "digital translation axes are absent");

if (failures.Count > 0)
{
    foreach (var failure in failures)
    {
        Console.Error.WriteLine("FAIL: " + failure);
    }
    return 1;
}

Console.WriteLine("KittenIO canonical protocol tests passed.");
return 0;
