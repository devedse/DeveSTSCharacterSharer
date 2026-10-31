using System.Text.RegularExpressions;

namespace DeveSTSCharacterSharer.Services;

public sealed partial class SharerOptions
{
    /// <summary>Where characters are stored (DATA_DIR, /data in the container).</summary>
    public required string DataDir { get; init; }

    /// <summary>
    /// The address friends use to reach this server (PUBLIC_URL). It is baked into the sync script.
    /// When empty, the address the browser used is taken instead.
    /// </summary>
    public string? PublicUrl { get; init; }

    public long MaxUploadBytes { get; init; }

    /// <summary>
    /// Mod ids that go at the top of everyone's mod list (MOD_LIBRARIES, comma separated), on top of
    /// whatever the shared mods depend on.
    /// </summary>
    public IReadOnlyList<string> ModLibraries { get; init; } = DefaultModLibraries;

    public static readonly IReadOnlyList<string> DefaultModLibraries = ["BaseLib", "STS2-RitsuLib", "RitsuLib", "ModConfig"];

    public static SharerOptions FromConfiguration(IConfiguration config, IHostEnvironment env)
    {
        var publicUrl = NormalizePublicUrl(config["PUBLIC_URL"]);
        if (publicUrl != null && !IsSafeUrl(publicUrl))
        {
            throw new InvalidOperationException($"PUBLIC_URL '{publicUrl}' contains characters that can't be used in a .cmd script");
        }

        return new SharerOptions
        {
            DataDir = config["DATA_DIR"] is { Length: > 0 } dataDir ? dataDir : Path.Combine(env.ContentRootPath, "data"),
            PublicUrl = publicUrl,
            MaxUploadBytes = (long.TryParse(config["MAX_UPLOAD_MB"], out var mb) ? mb : 2048) * 1024 * 1024,
            ModLibraries = config["MOD_LIBRARIES"] is { } libraries
                ? libraries.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                : DefaultModLibraries,
        };
    }

    /// <summary>'sts.example.com' becomes 'https://sts.example.com', '192.168.1.10:8080' becomes 'http://192.168.1.10:8080'.</summary>
    public static string? NormalizePublicUrl(string? raw)
    {
        var url = raw?.Trim().TrimEnd('/');
        if (string.IsNullOrEmpty(url))
        {
            return null;
        }
        if (!url.Contains("://"))
        {
            var host = url.Split(':', '/')[0];
            var plainHttp = host == "localhost" || !host.Contains('.') || IpAddressRegex().IsMatch(host);
            url = (plainHttp ? "http://" : "https://") + url;
        }
        return url;
    }

    /// <summary>The URL ends up inside the .cmd and .sh scripts, so refuse anything cmd.exe or sh would interpret.</summary>
    public static bool IsSafeUrl(string url) => SafeUrlRegex().IsMatch(url);

    [GeneratedRegex(@"^[\d.]+$")]
    private static partial Regex IpAddressRegex();

    [GeneratedRegex(@"^https?://[A-Za-z0-9._~:/?#\[\]@+=,;-]+$")]
    private static partial Regex SafeUrlRegex();
}
