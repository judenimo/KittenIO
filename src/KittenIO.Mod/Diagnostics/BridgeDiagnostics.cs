using System.Diagnostics;

namespace KittenIO.Mod.Diagnostics;

internal sealed class BridgeDiagnostics
{
    private readonly object _gate = new();
    private string _connection = "starting";
    private string _compatibility = "checking";
    private string? _vehicle;
    private string? _vehicleName;
    private string? _vehicleId;
    private int _vehicleFormatCount;
    private string _lease = "inactive";
    private string? _lastError;
    private long _lastReceivedMs;
    private long _lastTelemetryMs;

    public void SetConnection(string value)
    {
        lock (_gate) _connection = value;
    }

    public void SetCompatibility(string value)
    {
        lock (_gate) _compatibility = value;
    }

    public void SetVehicle(string? value)
    {
        lock (_gate)
        {
            if (StringComparer.Ordinal.Equals(_vehicle, value)) return;
            _vehicle = value;
            _vehicleName = null;
            _vehicleId = null;
            _vehicleFormatCount++;
        }
    }

    public void SetVehicle(string? name, string? id)
    {
        lock (_gate)
        {
            if (StringComparer.Ordinal.Equals(_vehicleName, name) &&
                StringComparer.Ordinal.Equals(_vehicleId, id))
            {
                return;
            }

            _vehicleName = name;
            _vehicleId = id;
            _vehicle = name is null ? id : id is null ? name : $"{name} ({id})";
            _vehicleFormatCount++;
        }
    }

    internal int VehicleFormatCount
    {
        get
        {
            lock (_gate) return _vehicleFormatCount;
        }
    }

    public void SetLease(string value)
    {
        lock (_gate) _lease = value;
    }

    public void MarkReceived(long nowMs)
    {
        lock (_gate) _lastReceivedMs = nowMs;
    }

    public void MarkTelemetry(long nowMs)
    {
        lock (_gate) _lastTelemetryMs = nowMs;
    }

    public void ReportError(string message, Exception? exception = null)
    {
        var detail = exception is null ? message : $"{message}: {exception.Message}";
        lock (_gate) _lastError = detail;
        Console.Error.WriteLine($"[KittenIO] {detail}");
    }

    public DiagnosticsSnapshot Snapshot()
    {
        lock (_gate)
        {
            var now = Stopwatch.GetTimestamp();
            var nowMs = (long)(now * 1000d / Stopwatch.Frequency);
            return new(
                _connection,
                _compatibility,
                _vehicle ?? "none",
                _lease,
                _lastReceivedMs == 0 ? null : Math.Max(0, nowMs - _lastReceivedMs),
                _lastTelemetryMs == 0 ? null : Math.Max(0, nowMs - _lastTelemetryMs),
                _lastError);
        }
    }
}

internal sealed record DiagnosticsSnapshot(
    string Connection,
    string Compatibility,
    string Vehicle,
    string Lease,
    long? LastMessageAgeMs,
    long? LastTelemetryAgeMs,
    string? LastError);
