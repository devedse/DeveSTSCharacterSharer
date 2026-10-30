using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DeveSTSCharacterSharer.Services;

public sealed class UploadException(string message, Exception? inner = null) : Exception(message, inner);

public sealed record ModManifest(string? Version, string? Author, string? Description);

public sealed record FolderContent(string Hash, long Size, IReadOnlyList<string> Files);

/// <summary>Helpers for a stored mod folder like Yugi/ (Yugi.dll, Yugi.json, Yugi.pck).</summary>
public static partial class ModFolder
{
    private static readonly HashSet<string> WindowsReservedNames = new(
        ["CON", "PRN", "AUX", "NUL", .. Enumerable.Range(1, 9).SelectMany(i => new[] { $"COM{i}", $"LPT{i}" })],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Folder names end up in a Windows .cmd script and on Windows disks, so keep them boring:
    /// nothing that cmd.exe or the file system treats specially.
    /// </summary>
    public static void ValidateName(string name)
    {
        if (!ValidNameRegex().IsMatch(name) || name.EndsWith('.') || name.EndsWith(' ') || WindowsReservedNames.Contains(name.Split('.')[0]))
        {
            throw new UploadException($"'{name}' can't be used as a folder name; only letters, digits, space, '.', '_' and '-' are allowed");
        }
    }

    public static IEnumerable<string> EnumerateFiles(string folder) =>
        Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(f => !File.GetAttributes(f).HasFlag(FileAttributes.ReparsePoint))
            .Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/'))
            .Order(StringComparer.Ordinal);

    /// <summary>
    /// Hash of a folder's contents (paths + bytes), independent of timestamps, so re-uploading an
    /// identical build keeps the same hash and players don't download it again.
    /// </summary>
    public static FolderContent ComputeContent(string folder)
    {
        var files = EnumerateFiles(folder).ToList();
        using var total = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        long size = 0;
        foreach (var relative in files)
        {
            using var stream = File.OpenRead(Path.Combine(folder, relative));
            var fileHash = Convert.ToHexStringLower(SHA256.HashData(stream));
            size += stream.Length;
            total.AppendData(Encoding.UTF8.GetBytes($"{relative}\0{stream.Length}\0{fileHash}\n"));
        }
        return new FolderContent(Convert.ToHexStringLower(total.GetHashAndReset()), size, files);
    }

    /// <summary>The mod's own &lt;Name&gt;.json (id, version, author, ...), if it's readable.</summary>
    public static ModManifest ReadManifest(string folder, string name)
    {
        var path = Directory.EnumerateFiles(folder, "*.json")
            .FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Equals(name, StringComparison.OrdinalIgnoreCase));
        if (path == null)
        {
            return new ModManifest(null, null, null);
        }
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            return new ModManifest(GetString(doc.RootElement, "version"), GetString(doc.RootElement, "author"), GetString(doc.RootElement, "description"));
        }
        catch (JsonException)
        {
            return new ModManifest(null, null, null);
        }
    }

    private static string? GetString(JsonElement root, string property)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty(property, out var value))
        {
            return null;
        }
        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Number => value.GetRawText(),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9 ._-]{0,63}$")]
    private static partial Regex ValidNameRegex();
}
