namespace KittenIO.Protocol;

public static class ProtocolConstants
{
    public const string Name = "kittenio";
    public const int Version = 1;
    public static IReadOnlyList<int> SupportedVersions { get; } = [Version];
    public const int DefaultPort = 8765;
    public const string DefaultHost = "127.0.0.1";
    public const string Path = "/kittenio/v1";
    public const string DefaultUri = "ws://127.0.0.1:8765/kittenio/v1";
    public const int MaximumMessageBytes = 256 * 1024;
    public const int DefaultTelemetryHz = 20;
    public const int DefaultControlTimeoutMs = 750;
    public const int ReliableQueueCapacity = 64;

    public static class MessageTypes
    {
        public const string Hello = "hello";
        public const string Welcome = "welcome";
        public const string Telemetry = "telemetry";
        public const string Catalog = "catalog";
        public const string Event = "event";
        public const string LeaseAcquire = "lease.acquire";
        public const string LeaseGranted = "lease.granted";
        public const string LeaseRelease = "lease.release";
        public const string Control = "control";
        public const string Request = "request";
        public const string Response = "response";
        public const string Ping = "ping";
        public const string Pong = "pong";
        public const string Error = "error";
    }
}
