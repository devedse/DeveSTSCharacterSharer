using System.Text;

namespace DeveSTSCharacterSharer.Services;

/// <summary>The Windows .cmd that downloads every changed character into the folder it runs in.</summary>
public sealed class SyncScript
{
    public const string FileName = "sts-character-sync.cmd";

    /// <summary>Hidden file the script keeps in each character folder, holding the hash it downloaded.</summary>
    public const string MarkerFileName = ".character-sync";

    private readonly string _template;

    public SyncScript()
    {
        using var stream = typeof(SyncScript).Assembly.GetManifestResourceStream(FileName + ".template")
            ?? throw new InvalidOperationException("Sync script template is missing from the assembly");
        using var reader = new StreamReader(stream, Encoding.ASCII);
        _template = reader.ReadToEnd();
    }

    public byte[] Render(string serverUrl)
    {
        if (!SharerOptions.IsSafeUrl(serverUrl))
        {
            throw new InvalidOperationException($"Can't build a sync script for '{serverUrl}', set PUBLIC_URL");
        }
        // cmd.exe misparses labels and gotos in files with bare LF line endings.
        var script = _template
            .Replace("__SERVER_URL__", serverUrl)
            .Replace("__MARKER__", MarkerFileName)
            .Replace("__SCRIPT_NAME__", FileName)
            .ReplaceLineEndings("\r\n");
        return Encoding.ASCII.GetBytes(script);
    }
}
