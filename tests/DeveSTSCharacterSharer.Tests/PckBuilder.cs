using System.Text;

namespace DeveSTSCharacterSharer.Tests;

/// <summary>Writes Godot .pck files the way Godot 4 does: format 2 (directory, then data) or 3 (data, then directory).</summary>
internal static class PckBuilder
{
    public static byte[] Build(int version, IReadOnlyDictionary<string, byte[]> files)
    {
        // Names are NUL padded to a multiple of 4 bytes.
        var names = files.Keys.Select(n => Encoding.UTF8.GetBytes(n).Concat(new byte[4 - Encoding.UTF8.GetByteCount(n) % 4]).ToArray()).ToList();
        var headerSize = 4 + 16 + 12 + (version >= 3 ? 8 : 0) + 64;
        var directorySize = 4 + names.Sum(n => 4 + n.Length + 8 + 8 + 16 + 4);
        var dataSize = files.Values.Sum(f => f.Length);
        var fileBase = version >= 3 ? headerSize : headerSize + directorySize;

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("GDPC"u8);
        writer.Write((uint)version);
        foreach (var part in new uint[] { 4, 5, 1 }) // made with Godot 4.5.1
        {
            writer.Write(part);
        }
        writer.Write(0u); // flags
        writer.Write((ulong)fileBase);
        if (version >= 3)
        {
            writer.Write((ulong)(headerSize + dataSize));
        }
        writer.Write(new byte[64]);

        void WriteDirectory()
        {
            writer.Write((uint)files.Count);
            long offset = 0;
            foreach (var (name, data) in names.Zip(files.Values))
            {
                writer.Write((uint)name.Length);
                writer.Write(name);
                writer.Write((ulong)offset);
                writer.Write((ulong)data.Length);
                writer.Write(new byte[16]);
                writer.Write(0u);
                offset += data.Length;
            }
        }

        if (version < 3)
        {
            WriteDirectory();
        }
        foreach (var data in files.Values)
        {
            writer.Write(data);
        }
        if (version >= 3)
        {
            WriteDirectory();
        }
        return stream.ToArray();
    }

    /// <summary>A Godot 4 compressed texture (.ctex) holding the image as PNG (1) or WebP (2).</summary>
    public static byte[] Ctex(uint dataFormat, byte[] image)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("GST2"u8);
        foreach (var field in new uint[] { 1, 256, 256, 0, 0, 0, 0, 0 }) // version, size, flags, mipmap limit, reserved
        {
            writer.Write(field);
        }
        writer.Write(dataFormat);
        writer.Write((ushort)256);
        writer.Write((ushort)256);
        writer.Write(0u); // mipmaps
        writer.Write(5u); // RGBA8
        writer.Write((uint)image.Length);
        writer.Write(image);
        return stream.ToArray();
    }

    public static string Import(string ctexPath) =>
        $"[remap]\n\nimporter=\"texture\"\ntype=\"CompressedTexture2D\"\npath=\"res://{ctexPath}\"\nmetadata={{\n\"vram_texture\": false\n}}\n";
}
