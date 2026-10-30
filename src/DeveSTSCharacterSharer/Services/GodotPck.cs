using System.Text;

namespace DeveSTSCharacterSharer.Services;

/// <summary>
/// Reads files out of a Godot resource pack (.pck, format version 1 to 3 as used by Godot 3 and 4).
/// Uploaded packs are untrusted, so every count, length and offset is checked against the file.
/// </summary>
public sealed class GodotPck : IDisposable
{
    private const uint DirectoryEncrypted = 1;
    private const uint FileEncrypted = 1;
    private const int MaxFiles = 100_000;

    private readonly FileStream _stream;
    private readonly Dictionary<string, (long Offset, long Size)> _files = new(StringComparer.Ordinal);

    private GodotPck(FileStream stream) => _stream = stream;

    /// <summary>Paths inside the pack, without the "res://" prefix.</summary>
    public IReadOnlyCollection<string> Paths => _files.Keys;

    /// <summary>Opens the pack, or returns null when it isn't a (readable) Godot pack.</summary>
    public static GodotPck? TryOpen(string path)
    {
        var pck = new GodotPck(File.OpenRead(path));
        try
        {
            if (pck.ReadDirectory())
            {
                return pck;
            }
        }
        catch (Exception e) when (e is EndOfStreamException or IOException or ArgumentException or DecoderFallbackException)
        {
        }
        pck.Dispose();
        return null;
    }

    public byte[]? Read(string path, long maxSize = 64 * 1024 * 1024)
    {
        if (!_files.TryGetValue(path, out var file) || file.Size > maxSize)
        {
            return null;
        }
        var data = new byte[file.Size];
        _stream.Position = file.Offset;
        _stream.ReadExactly(data);
        return data;
    }

    public void Dispose() => _stream.Dispose();

    private bool ReadDirectory()
    {
        var reader = new BinaryReader(_stream, Encoding.UTF8, leaveOpen: true);
        if (!reader.ReadBytes(4).AsSpan().SequenceEqual("GDPC"u8))
        {
            return false;
        }
        var version = reader.ReadUInt32();
        reader.ReadBytes(12); // Godot version the pack was made with
        uint flags = 0;
        long fileBase = 0;
        long? directoryOffset = null;
        if (version >= 2)
        {
            flags = reader.ReadUInt32();
            fileBase = (long)reader.ReadUInt64();
        }
        if (version >= 3)
        {
            directoryOffset = (long)reader.ReadUInt64();
        }
        if (version is < 1 or > 3 || (flags & DirectoryEncrypted) != 0)
        {
            return false;
        }
        reader.ReadBytes(16 * 4); // reserved
        if (directoryOffset is { } offset)
        {
            _stream.Position = offset;
        }

        var count = reader.ReadUInt32();
        if (count > MaxFiles)
        {
            return false;
        }
        for (var i = 0; i < count; i++)
        {
            var nameLength = reader.ReadInt32();
            if (nameLength is < 0 or > 4096)
            {
                return false;
            }
            var name = Encoding.UTF8.GetString(reader.ReadBytes(nameLength)).TrimEnd('\0');
            var fileOffset = fileBase + (long)reader.ReadUInt64();
            var size = (long)reader.ReadUInt64();
            reader.ReadBytes(16); // md5
            var fileFlags = version >= 2 ? reader.ReadUInt32() : 0;
            if (fileOffset < 0 || size < 0 || fileOffset + size > _stream.Length || (fileFlags & FileEncrypted) != 0)
            {
                continue;
            }
            _files[name.StartsWith("res://", StringComparison.Ordinal) ? name[6..] : name] = (fileOffset, size);
        }
        return true;
    }
}
