using System.Buffers.Binary;
using System.Text;
using System.Text.RegularExpressions;

namespace DeveSTSCharacterSharer.Services;

public sealed record PortraitImage(byte[] Data, string Extension);

/// <summary>
/// Finds the character's portrait inside a mod's .pck. Character mods keep their art at
/// images/packed/portraits/&lt;id&gt;_&lt;slot&gt;.png: portrait (the square head shot), hud_icon,
/// char_select, map_marker, creature (full body), bg, ...
/// </summary>
public static partial class ModPortrait
{
    // Best first. The portrait is usually the same picture as the HUD icon and map marker.
    private static readonly string[] Slots = ["portrait", "hud_icon", "char_select", "map_marker"];

    public static PortraitImage? Find(string modFolder, string name)
    {
        var packs = Directory.EnumerateFiles(modFolder, "*.pck")
            .OrderBy(p => Path.GetFileNameWithoutExtension(p).Equals(name, StringComparison.OrdinalIgnoreCase) ? 0 : 1);
        foreach (var path in packs)
        {
            using var pck = GodotPck.TryOpen(path);
            if (pck == null)
            {
                continue;
            }
            foreach (var slot in Slots)
            {
                var sources = pck.Paths
                    .Select(p => p.EndsWith(".import", StringComparison.Ordinal) ? p[..^7] : p)
                    .Where(p => p.StartsWith("images/packed/portraits/", StringComparison.Ordinal) && p.EndsWith($"_{slot}.png", StringComparison.Ordinal))
                    .Distinct()
                    // A pack can hold several characters; prefer the one named like the mod.
                    .OrderBy(p => Path.GetFileName(p).Equals($"{name}_{slot}.png", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ThenBy(p => p, StringComparer.Ordinal);
                foreach (var source in sources)
                {
                    if (ReadImage(pck, source) is { } image)
                    {
                        return image;
                    }
                }
            }
        }
        return null;
    }

    private static PortraitImage? ReadImage(GodotPck pck, string source)
    {
        // Some packers keep the original PNG next to the imported texture.
        if (pck.Read(source) is { } original && Recognize(original) is { } extension)
        {
            return new PortraitImage(original, extension);
        }
        // Otherwise the .import file says where the compiled texture is.
        if (pck.Read(source + ".import", maxSize: 64 * 1024) is { } import
            && ImportPathRegex().Match(Encoding.UTF8.GetString(import)) is { Success: true } match
            && pck.Read(match.Groups[1].Value) is { } ctex)
        {
            return FromCompressedTexture(ctex);
        }
        return null;
    }

    /// <summary>
    /// A Godot 4 CompressedTexture2D (.ctex) starts with "GST2". At byte 36 is how the image is stored
    /// (1 = PNG, 2 = WebP; others are GPU formats a browser can't show), at byte 52 the size of the
    /// full-size image, which follows right after.
    /// </summary>
    private static PortraitImage? FromCompressedTexture(byte[] ctex)
    {
        if (ctex.Length < 56 || !ctex.AsSpan(0, 4).SequenceEqual("GST2"u8))
        {
            return null;
        }
        var dataFormat = BinaryPrimitives.ReadUInt32LittleEndian(ctex.AsSpan(36));
        var size = BinaryPrimitives.ReadUInt32LittleEndian(ctex.AsSpan(52));
        if (dataFormat is not (1 or 2) || size > ctex.Length - 56)
        {
            return null;
        }
        var image = ctex.AsSpan(56, (int)size).ToArray();
        return Recognize(image) is { } extension ? new PortraitImage(image, extension) : null;
    }

    private static string? Recognize(byte[] data) => data switch
    {
        [0x89, (byte)'P', (byte)'N', (byte)'G', ..] => ".png",
        [(byte)'R', (byte)'I', (byte)'F', (byte)'F', _, _, _, _, (byte)'W', (byte)'E', (byte)'B', (byte)'P', ..] => ".webp",
        _ => null,
    };

    // path="res://.godot/imported/x.png-<hash>.ctex" (GPU variants use path.s3tc=, path.etc2=, ...).
    [GeneratedRegex("^path=\"res://([^\"]+)\"", RegexOptions.Multiline)]
    private static partial Regex ImportPathRegex();
}
