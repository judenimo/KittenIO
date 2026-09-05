using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;

namespace KittenIO.Mod.Game;

/// <summary>Single source of truth for release-pinned game and loader inputs.</summary>
internal static class PinnedKsaContract
{
    public const string GameVersion = "2026.9.7.5402";
    public const string KsaSha256 =
        "A8A5164204EED962DF9D9025E99F523D" + "9A23EE2CE6E802B18216A9DA35506D2F";
    public const string CoreSettingsSha256 =
        "29B6DEFFBB8B7BA2EF0CB869D703309E" + "6516AB86E962CA4AEEFEBB5F690551B5";
    public const string StarMapVersion = "0.4.6";
    public const string StarMapCommit = "57846b2971f95f48eb90a3483acacce832dd8751";
    public const string StarMapApiSha256 =
        "E8E321F5EF60D1F1CE53598616148A77" + "9F863879F7B9B303BE4512895F27C956";

    public static bool SupportsGameVersion(string? version) =>
        StringComparer.Ordinal.Equals(version, GameVersion) ||
        version?.StartsWith(GameVersion + "+", StringComparison.Ordinal) == true;

    public static bool HasPinnedKsaBinary(Assembly assembly)
    {
        if (string.IsNullOrWhiteSpace(assembly.Location) || !File.Exists(assembly.Location)) return false;
        if (!SupportsGameVersion(FileVersionInfo.GetVersionInfo(assembly.Location).ProductVersion)) return false;
        return HashMatches(assembly.Location, KsaSha256);
    }

    public static bool HashMatches(string path, string expectedSha256)
    {
        using var stream = File.OpenRead(path);
        return StringComparer.Ordinal.Equals(Convert.ToHexString(SHA256.HashData(stream)), expectedSha256);
    }
}
