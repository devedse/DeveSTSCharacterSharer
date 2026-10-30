using SharpCompress.Archives;
using SharpCompress.Common;
using SharpCompress.Readers;

namespace DeveSTSCharacterSharer.Services;

/// <summary>
/// Finds the mod folder(s) inside an uploaded archive, e.g. Yugi/ holding Yugi.dll, Yugi.json and
/// Yugi.pck, and extracts only those. Everything else (readmes, sources, ...) is thrown away.
/// </summary>
public static class ModArchive
{
    public const string NoModFoundMessage =
        "No mod folder found. Expected a folder with e.g. Yugi.json next to Yugi.dll and/or Yugi.pck.";

    private const int MaxNestingDepth = 2;
    private static readonly string[] ArchiveExtensions = [".zip", ".7z", ".rar", ".tar", ".gz", ".tgz", ".xz", ".bz2"];
    private static readonly string[] ModFileExtensions = [".dll", ".pck"];

    // Never stored: they'd change the content hash without changing the mod, making everyone download
    // it again. Uploading a folder from someone's mods/ includes the sync script's marker files.
    private static readonly HashSet<string> IgnoredFileNames =
        new([SyncScript.MarkerFileName, "desktop.ini", "Thumbs.db", ".DS_Store"], StringComparer.OrdinalIgnoreCase);

    private sealed record FoundMod(string Name, string Directory, bool IsRoot);

    /// <summary>Extracts every mod in the archive to stagingDir/&lt;Name&gt;/ and returns their names.</summary>
    public static IReadOnlyList<string> ExtractMods(string archivePath, string stagingDir, ILogger log)
    {
        Directory.CreateDirectory(stagingDir);
        try
        {
            return ExtractMods(archivePath, stagingDir, depth: 0);
        }
        catch (Exception e) when (e is not UploadException)
        {
            log.LogWarning(e, "Could not read {Archive}", archivePath);
            throw new UploadException("Could not read this file as an archive. Upload a .zip, .7z or .rar (not password protected).", e);
        }
    }

    private static List<string> ExtractMods(string archivePath, string stagingDir, int depth)
    {
        using var contents = new ArchiveContents(archivePath);
        var files = contents.Files;

        var mods = FindMods(files.Keys);
        if (mods.Count == 0)
        {
            return depth < MaxNestingDepth ? ExtractFromNestedArchives(contents, archivePath, stagingDir, depth) : [];
        }

        var targets = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var mod in mods)
        {
            ModFolder.ValidateName(mod.Name);
            if (Directory.EnumerateDirectories(stagingDir).Any(d => Path.GetFileName(d).Equals(mod.Name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new UploadException($"The archive contains more than one mod called {mod.Name}");
            }
            var modDir = Directory.CreateDirectory(Path.Combine(stagingDir, mod.Name)).FullName;
            foreach (var path in files.Keys)
            {
                var relative = mod.IsRoot
                    // Loose files at the top of the archive: keep only the mod's own files
                    // (Yugi.dll, Yugi.json, Yugi.pck, ...), not readmes or other junk.
                    ? !path.Contains('/') && Path.GetFileNameWithoutExtension(path).Equals(mod.Name, StringComparison.OrdinalIgnoreCase) ? path : null
                    : path.StartsWith(mod.Directory + "/", StringComparison.Ordinal) ? path[(mod.Directory.Length + 1)..] : null;
                if (relative != null)
                {
                    var target = Path.GetFullPath(Path.Combine(modDir, relative));
                    if (!target.StartsWith(modDir + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    {
                        continue;
                    }
                    if (files[path].IsEncrypted)
                    {
                        throw new UploadException("The archive is password protected.");
                    }
                    targets[path] = target;
                }
            }
        }

        contents.Extract(targets);
        return mods.Select(m => m.Name).ToList();
    }

    /// <summary>
    /// Some people zip their zip. Only look inside nested archives when the outer one had no mod at
    /// all (Yugi's own zip also ships a Yugi.source.zip next to the Yugi/ folder).
    /// </summary>
    private static List<string> ExtractFromNestedArchives(ArchiveContents contents, string archivePath, string stagingDir, int depth)
    {
        var found = new List<string>();
        var nested = contents.Files.Keys
            .Where(path => ArchiveExtensions.Any(ext => path.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
            .Order(StringComparer.Ordinal);
        foreach (var path in nested)
        {
            var nestedPath = Path.Combine(Path.GetDirectoryName(archivePath)!, $"nested-{Guid.NewGuid():N}");
            try
            {
                contents.Extract(new Dictionary<string, string> { [path] = nestedPath });
                found.AddRange(ExtractMods(nestedPath, stagingDir, depth + 1));
            }
            catch (Exception e) when (e is not UploadException)
            {
                // Not an archive after all, or a broken one; keep looking.
            }
            finally
            {
                File.Delete(nestedPath);
            }
        }
        return found;
    }

    /// <summary>
    /// Folders holding a mod, outermost first. A folder is a mod when it has &lt;X&gt;.json next to
    /// &lt;X&gt;.dll and/or &lt;X&gt;.pck. The mod's name comes from those files rather than the folder,
    /// so a release folder like "Yugi-0.3.0/" still replaces the existing "Yugi".
    /// </summary>
    private static List<FoundMod> FindMods(IEnumerable<string> paths)
    {
        var filesByDirectory = paths
            .GroupBy(p => p.Contains('/') ? p[..p.LastIndexOf('/')] : "")
            .ToDictionary(g => g.Key, g => g.Select(p => p[(p.LastIndexOf('/') + 1)..]).ToList());

        var mods = new List<FoundMod>();
        var outermostFirst = filesByDirectory.Keys
            .OrderBy(d => d.Length == 0 ? 0 : d.Count(c => c == '/') + 1)
            .ThenBy(d => d, StringComparer.Ordinal);
        foreach (var directory in outermostFirst)
        {
            // Everything below a mod folder belongs to that mod.
            if (mods.Any(m => m.IsRoot || directory.StartsWith(m.Directory + "/", StringComparison.Ordinal)))
            {
                continue;
            }

            var fileNames = filesByDirectory[directory];
            var stems = fileNames
                .Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                .Select(f => Path.GetFileNameWithoutExtension(f))
                .Where(stem => fileNames.Any(f => ModFileExtensions.Any(ext => f.Equals(stem + ext, StringComparison.OrdinalIgnoreCase))))
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (stems.Count == 0)
            {
                continue;
            }

            var isRoot = directory.Length == 0;
            var folderName = directory[(directory.LastIndexOf('/') + 1)..];
            var name = stems.Count == 1
                ? stems[0]
                : stems.FirstOrDefault(s => s.Equals(folderName, StringComparison.OrdinalIgnoreCase)) ?? (isRoot ? stems[0] : folderName);
            mods.Add(new FoundMod(name, directory, isRoot));
        }
        return mods;
    }

    /// <summary>"./Yugi\Yugi.dll" becomes "Yugi/Yugi.dll"; paths escaping the archive ("../x") are dropped.</summary>
    private static string? NormalizePath(string? key)
    {
        if (key == null)
        {
            return null;
        }
        var parts = key.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Where(p => p != ".").ToList();
        return parts.Count == 0 || parts.Contains("..") ? null : string.Join('/', parts);
    }

    /// <summary>
    /// The regular files in an archive (symlinks are never extracted), and a way to extract some of
    /// them. Zip, 7z and rar open with random access; compressed tarballs (.tar.gz, ...) only open
    /// as a stream, so for those the archive is read twice: once to list, once to extract.
    /// </summary>
    private sealed class ArchiveContents : IDisposable
    {
        private readonly string _path;
        private readonly IArchive? _archive;

        public ArchiveContents(string path)
        {
            _path = path;
            try
            {
                _archive = ArchiveFactory.OpenArchive(path, new ReaderOptions());
            }
            catch (Exception)
            {
                using var reader = OpenReader();
                while (reader.MoveToNextEntry())
                {
                    Add(reader.Entry);
                }
                return;
            }
            foreach (var entry in _archive.Entries)
            {
                Add(entry);
            }
        }

        /// <summary>Normalized path -> entry.</summary>
        public Dictionary<string, IEntry> Files { get; } = new(StringComparer.Ordinal);

        /// <summary>Extracts archive path -> target file.</summary>
        public void Extract(Dictionary<string, string> targets)
        {
            if (_archive is { IsSolid: false } && _archive.Type != ArchiveType.SevenZip)
            {
                foreach (var (path, target) in targets)
                {
                    WriteFile(target, stream => ((IArchiveEntry)Files[path]).WriteTo(stream, null));
                }
                return;
            }

            // Solid archives and streams decompress front to back; read them once.
            var remaining = new Dictionary<string, string>(targets);
            using var reader = _archive?.ExtractAllEntries() ?? OpenReader();
            while (remaining.Count > 0 && reader.MoveToNextEntry())
            {
                if (!reader.Entry.IsDirectory && reader.Entry.LinkTarget == null && NormalizePath(reader.Entry.Key) is { } path && remaining.Remove(path, out var target))
                {
                    WriteFile(target, reader.WriteEntryTo);
                }
            }
        }

        public void Dispose() => _archive?.Dispose();

        private IReader OpenReader() => ReaderFactory.OpenReader(File.OpenRead(_path), new ReaderOptions());

        private void Add(IEntry entry)
        {
            if (!entry.IsDirectory && entry.LinkTarget == null && NormalizePath(entry.Key) is { } path && !IgnoredFileNames.Contains(path[(path.LastIndexOf('/') + 1)..]))
            {
                Files.TryAdd(path, entry);
            }
        }
    }

    private static void WriteFile(string path, Action<Stream> write)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        write(stream);
    }
}
