using System.Text;

namespace DeveSTSCharacterSharer.Services;

/// <summary>
/// The scripts that download every changed character into the mods folder and sort the game's mod
/// list: a .cmd for Windows and a .sh for Linux and macOS.
/// </summary>
public sealed class SyncScript
{
    public const string FileName = "sts-character-sync.cmd";

    public const string ShellFileName = "sts-character-sync.sh";

    /// <summary>Hidden file the script keeps in each character folder, holding the hash it downloaded.</summary>
    public const string MarkerFileName = ".character-sync";

    private readonly string _cmdTemplate = ReadTemplate(FileName);
    private readonly string _shellTemplate = ReadTemplate(ShellFileName);

    // cmd.exe misparses labels and gotos in files with bare LF line endings.
    public byte[] RenderCmd(string serverUrl) => Render(_cmdTemplate, serverUrl, FileName, "\r\n");

    public byte[] RenderShell(string serverUrl) => Render(_shellTemplate, serverUrl, ShellFileName, "\n");

    private static byte[] Render(string template, string serverUrl, string scriptName, string newLine)
    {
        if (!SharerOptions.IsSafeUrl(serverUrl))
        {
            throw new InvalidOperationException($"Can't build a sync script for '{serverUrl}', set PUBLIC_URL");
        }
        var script = template
            .Replace("__SERVER_URL__", serverUrl)
            .Replace("__MARKER__", MarkerFileName)
            .Replace("__SCRIPT_NAME__", scriptName)
            .ReplaceLineEndings(newLine);
        return Encoding.ASCII.GetBytes(script);
    }

    private static string ReadTemplate(string scriptName)
    {
        using var stream = typeof(SyncScript).Assembly.GetManifestResourceStream(scriptName + ".template")
            ?? throw new InvalidOperationException($"{scriptName}.template is missing from the assembly");
        using var reader = new StreamReader(stream, Encoding.ASCII);
        return reader.ReadToEnd();
    }
}
