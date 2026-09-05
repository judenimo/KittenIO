using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using KittenIO.Protocol;

namespace KittenIO.Mod.Configuration;

internal sealed record KittenIOConfig
{
    [JsonPropertyName("endpoint")]
    public string Endpoint { get; init; } = ProtocolConstants.DefaultUri;

    [JsonPropertyName("pairing_token")]
    public required string PairingToken { get; init; }

    [JsonPropertyName("telemetry_hz")]
    public int TelemetryHz { get; init; } = ProtocolConstants.DefaultTelemetryHz;

    [JsonPropertyName("command_deadman_ms")]
    public int CommandDeadmanMs { get; init; } = ProtocolConstants.DefaultControlTimeoutMs;

}

internal static class KittenIOConfigStore
{
    private const string ConfigFileName = "kittenio.json";

    public static string ConfigPath
    {
        get
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (!string.IsNullOrWhiteSpace(localAppData))
            {
                return Path.Combine(localAppData, "KittenIO", ConfigFileName);
            }

            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return Path.Combine(home, ".config", "KittenIO", ConfigFileName);
        }
    }

    public static KittenIOConfig LoadOrCreate()
    {
        var path = ConfigPath;
        if (File.Exists(path))
        {
            try
            {
                var config = JsonSerializer.Deserialize(File.ReadAllText(path), ConfigJsonContext.Default.KittenIOConfig);
                if (config is not null)
                {
                    var validated = Validate(config);
                    if (validated != config) SaveAtomically(validated);
                    return validated;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                Console.Error.WriteLine($"[KittenIO] Could not read {path}: {exception.Message}. Creating a safe replacement.");
            }
        }

        var created = new KittenIOConfig { PairingToken = GenerateToken() };
        SaveAtomically(created);
        return created;
    }

    private static KittenIOConfig Validate(KittenIOConfig config)
    {
        if (!Uri.TryCreate(config.Endpoint, UriKind.Absolute, out var endpoint) ||
            endpoint.Scheme != Uri.UriSchemeWs ||
            !string.Equals(endpoint.Host, ProtocolConstants.DefaultHost, StringComparison.Ordinal) ||
            !string.Equals(endpoint.AbsolutePath, ProtocolConstants.Path, StringComparison.Ordinal) ||
            !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new JsonException($"endpoint must be ws://{ProtocolConstants.DefaultHost}:<port>{ProtocolConstants.Path} without credentials, query, or fragment.");
        }

        var token = config.PairingToken;
        if (string.IsNullOrWhiteSpace(token) || token.Length < 43)
        {
            token = GenerateToken();
        }

        return config with
        {
            Endpoint = endpoint.ToString(),
            PairingToken = token,
            TelemetryHz = Math.Clamp(config.TelemetryHz, 1, 60),
            CommandDeadmanMs = Math.Clamp(config.CommandDeadmanMs, 250, ProtocolConstants.DefaultControlTimeoutMs),
        };
    }

    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private static void SaveAtomically(KittenIOConfig config)
    {
        var path = ConfigPath;
        var directory = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("Config path has no directory.");
        Directory.CreateDirectory(directory);

        var temporaryPath = Path.Combine(directory, $".{ConfigFileName}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(config, ConfigJsonContext.Default.KittenIOConfig));
            File.Move(temporaryPath, path, true);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(KittenIOConfig))]
internal partial class ConfigJsonContext : JsonSerializerContext;
