using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeveSTSCharacterSharer.Services;

public sealed record Character(
    string Name,
    string Hash,
    long Size,
    IReadOnlyList<string> Files,
    string? Version,
    string? Author,
    string? Description,
    DateTimeOffset UploadedAt,
    string? Source,
    long PackageSize,
    string PackagePath,
    string TarballPath,
    string? PortraitPath,
    string ModId,
    IReadOnlyList<string> Dependencies);

[JsonConverter(typeof(JsonStringEnumConverter<ImportStatus>))]
public enum ImportStatus { Added, Updated, Unchanged }

public sealed record ImportResult(string Name, ImportStatus Status, string? Version, string? PreviousVersion);

/// <summary>
/// Shared characters on disk. Layout of the data directory:
/// <code>
/// characters/&lt;Name&gt;/          the mod folder exactly as players get it
/// packages/&lt;Name&gt;.zip         prebuilt download (contains the &lt;Name&gt;/ folder)
/// packages/&lt;Name&gt;.tar.gz      the same for the Linux/macOS script (GNU tar can't open zips)
/// packages/&lt;Name&gt;.info.json   upload time and original archive name
/// packages/&lt;Name&gt;.portrait.*  the character's portrait, taken from the mod's .pck
/// keys/                       ASP.NET data protection keys (survive container restarts)
/// work/                       scratch space for uploads (same file system, so swapping a new
///                             version in is a rename)
/// </code>
/// </summary>
public sealed class CharacterStore
{
    private sealed record PackageInfo(string? Hash, DateTimeOffset UploadedAt, string? Source);

    private static readonly string[] PortraitExtensions = [".png", ".webp"];

    private readonly string _charactersDir;
    private readonly string _packagesDir;
    private readonly string _workDir;
    private readonly ILogger<CharacterStore> _log;
    private readonly SemaphoreSlim _lock = new(1, 1);

    // Readers never take the lock (an upload can hold it for a while); writers swap in a new
    // dictionary instead of changing the one readers may be enumerating.
    private volatile Dictionary<string, Character> _characters = [];

    public CharacterStore(SharerOptions options, ILogger<CharacterStore> log)
    {
        _charactersDir = Path.Combine(options.DataDir, "characters");
        _packagesDir = Path.Combine(options.DataDir, "packages");
        _workDir = Path.Combine(options.DataDir, "work");
        _log = log;
    }

    /// <summary>Raised with the names of characters that were added, updated or removed.</summary>
    public event Action<IReadOnlyCollection<string>>? Changed;

    public IReadOnlyList<Character> All => _characters.Values.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>Names are unique case-insensitively, like folders on Windows.</summary>
    public Character? Get(string name) =>
        _characters.Values.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public Character? GetByHash(string hash) => _characters.Values.FirstOrDefault(c => c.Hash == hash);

    /// <summary>
    /// Indexes everything on disk and rebuilds packages that are missing or stale, so folders can
    /// also be dropped into characters/ by hand.
    /// </summary>
    public void Load()
    {
        Directory.CreateDirectory(_charactersDir);
        Directory.CreateDirectory(_packagesDir);
        if (Directory.Exists(_workDir))
        {
            Directory.Delete(_workDir, recursive: true);
        }
        Directory.CreateDirectory(_workDir);

        var characters = new Dictionary<string, Character>();
        foreach (var folder in Directory.EnumerateDirectories(_charactersDir).Order(StringComparer.Ordinal))
        {
            if (!Path.GetFileName(folder).StartsWith('.'))
            {
                var character = Index(folder);
                characters[character.Name] = character;
            }
        }
        _characters = characters;

        var known = characters.Keys.SelectMany(PackageFileNames).ToHashSet();
        foreach (var file in Directory.EnumerateFiles(_packagesDir).Where(f => !known.Contains(Path.GetFileName(f))))
        {
            File.Delete(file);
        }
        _log.LogInformation("Loaded {Count} character(s) from {Dir}", characters.Count, _charactersDir);
    }

    public string CreateWorkDirectory() => Directory.CreateDirectory(Path.Combine(_workDir, Guid.NewGuid().ToString("N"))).FullName;

    /// <summary>
    /// Extracts the mod(s) from an uploaded archive and replaces the stored versions. The archive must
    /// live in its own directory from <see cref="CreateWorkDirectory"/>; that directory is deleted.
    /// </summary>
    public Task<IReadOnlyList<ImportResult>> ImportAsync(string archivePath, string originalName, CancellationToken cancellationToken = default) =>
        Task.Run(async () =>
        {
            var scratch = Path.GetDirectoryName(archivePath)!;
            List<ImportResult> results;
            try
            {
                var staging = Path.Combine(scratch, "staged");
                var names = ModArchive.ExtractMods(archivePath, staging, _log);
                if (names.Count == 0)
                {
                    throw new UploadException(ModArchive.NoModFoundMessage);
                }

                await _lock.WaitAsync(cancellationToken);
                try
                {
                    results = names.Select(name => Replace(Path.Combine(staging, name), originalName, scratch)).ToList();
                }
                finally
                {
                    _lock.Release();
                }
            }
            finally
            {
                TryDeleteDirectory(scratch);
            }

            foreach (var r in results)
            {
                _log.LogInformation("Upload {File}: {Status} {Name} ({Version})", originalName, r.Status, r.Name, r.Version ?? "no version");
            }
            var changed = results.Where(r => r.Status != ImportStatus.Unchanged).Select(r => r.Name).ToList();
            if (changed.Count > 0)
            {
                Changed?.Invoke(changed);
            }
            return (IReadOnlyList<ImportResult>)results;
        }, cancellationToken);

    public async Task<bool> DeleteAsync(string name)
    {
        Character? character;
        await _lock.WaitAsync();
        try
        {
            character = Get(name);
            if (character == null)
            {
                return false;
            }
            var trash = CreateWorkDirectory();
            Directory.Move(Path.Combine(_charactersDir, character.Name), Path.Combine(trash, character.Name));
            DeletePackage(character.Name);
            _characters = _characters.Where(kv => kv.Key != character.Name).ToDictionary();
            TryDeleteDirectory(trash);
        }
        finally
        {
            _lock.Release();
        }
        _log.LogInformation("Deleted {Name}", character.Name);
        Changed?.Invoke([character.Name]);
        return true;
    }

    /// <summary>Swaps in a newly extracted folder. Callers hold the lock.</summary>
    private ImportResult Replace(string staged, string source, string scratch)
    {
        var name = Path.GetFileName(staged);
        var existing = Get(name);
        if (existing != null && existing.Hash == ModFolder.ComputeContent(staged).Hash)
        {
            return new ImportResult(existing.Name, ImportStatus.Unchanged, existing.Version, existing.Version);
        }

        var characters = new Dictionary<string, Character>(_characters);
        if (existing != null)
        {
            Directory.Move(Path.Combine(_charactersDir, existing.Name), Path.Combine(scratch, "old-" + existing.Name));
            DeletePackage(existing.Name);
            characters.Remove(existing.Name);
        }
        var target = Path.Combine(_charactersDir, name);
        Directory.Move(staged, target);
        WriteInfo(name, new PackageInfo(null, DateTimeOffset.UtcNow, source));

        var character = Index(target);
        characters[character.Name] = character;
        _characters = characters;
        return new ImportResult(character.Name, existing == null ? ImportStatus.Added : ImportStatus.Updated, character.Version, existing?.Version);
    }

    private Character Index(string folder)
    {
        var name = Path.GetFileName(folder);
        var content = ModFolder.ComputeContent(folder);
        var info = ReadInfo(name);
        var package = Path.Combine(_packagesDir, $"{name}.zip");
        var tarball = Path.Combine(_packagesDir, $"{name}.tar.gz");
        var changed = false;
        if (info?.Hash != content.Hash || !File.Exists(package) || !File.Exists(tarball))
        {
            BuildPackage(folder, package);
            BuildTarball(folder, tarball);
            info = new PackageInfo(content.Hash, info?.UploadedAt ?? Directory.GetLastWriteTimeUtc(folder), info?.Source);
            WriteInfo(name, info);
            changed = true;
        }
        var portrait = PortraitExtensions.Select(ext => Path.Combine(_packagesDir, $"{name}.portrait{ext}")).FirstOrDefault(File.Exists);
        // Also looked for when there's none yet, so characters stored before portraits existed get one.
        if (changed || portrait == null)
        {
            portrait = ExtractPortrait(folder, name);
        }
        var manifest = ModFolder.ReadManifest(folder, name);
        return new Character(
            name,
            content.Hash,
            content.Size,
            content.Files,
            manifest.Version,
            manifest.Author,
            manifest.Description,
            info.UploadedAt,
            info.Source,
            new FileInfo(package).Length,
            package,
            tarball,
            portrait,
            manifest.Id ?? name,
            manifest.Dependencies);
    }

    private string? ExtractPortrait(string folder, string name)
    {
        foreach (var ext in PortraitExtensions)
        {
            File.Delete(Path.Combine(_packagesDir, $"{name}.portrait{ext}"));
        }
        try
        {
            if (ModPortrait.Find(folder, name) is not { } image)
            {
                return null;
            }
            var path = Path.Combine(_packagesDir, $"{name}.portrait{image.Extension}");
            File.WriteAllBytes(path, image.Data);
            return path;
        }
        catch (IOException e)
        {
            _log.LogWarning(e, "Could not read the portrait of {Name}", name);
            return null;
        }
    }

    private static void BuildPackage(string folder, string package)
    {
        var name = Path.GetFileName(folder);
        var tmp = package + ".tmp";
        File.Delete(tmp);
        using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
        {
            zip.CreateEntry(name + "/");
            foreach (var relative in ModFolder.EnumerateFiles(folder))
            {
                var path = Path.Combine(folder, relative);
                var entry = zip.CreateEntry($"{name}/{relative}", CompressionLevel.Optimal);
                // Zip can't store dates before 1980 (common in tarballs).
                var modified = File.GetLastWriteTimeUtc(path);
                entry.LastWriteTime = modified.Year < 1980 ? new DateTimeOffset(1980, 1, 1, 0, 0, 0, TimeSpan.Zero) : modified;
                using var input = File.OpenRead(path);
                using var output = entry.Open();
                input.CopyTo(output);
            }
        }
        File.Move(tmp, package, overwrite: true);
    }

    private static void BuildTarball(string folder, string tarball)
    {
        const UnixFileMode fileMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        const UnixFileMode directoryMode = fileMode | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
        var name = Path.GetFileName(folder);
        var tmp = tarball + ".tmp";
        using (var file = File.Create(tmp))
        using (var gzip = new GZipStream(file, CompressionLevel.Optimal))
        using (var tar = new TarWriter(gzip, TarEntryFormat.Gnu))
        {
            tar.WriteEntry(new GnuTarEntry(TarEntryType.Directory, name + "/") { Mode = directoryMode, ModificationTime = Directory.GetLastWriteTimeUtc(folder) });
            foreach (var relative in ModFolder.EnumerateFiles(folder))
            {
                var path = Path.Combine(folder, relative);
                using var input = File.OpenRead(path);
                tar.WriteEntry(new GnuTarEntry(TarEntryType.RegularFile, $"{name}/{relative}")
                {
                    Mode = fileMode,
                    ModificationTime = File.GetLastWriteTimeUtc(path),
                    DataStream = input,
                });
            }
        }
        File.Move(tmp, tarball, overwrite: true);
    }

    private static IEnumerable<string> PackageFileNames(string name) =>
        [$"{name}.zip", $"{name}.tar.gz", $"{name}.info.json", .. PortraitExtensions.Select(ext => $"{name}.portrait{ext}")];

    private string InfoPath(string name) => Path.Combine(_packagesDir, $"{name}.info.json");

    private PackageInfo? ReadInfo(string name)
    {
        try
        {
            return JsonSerializer.Deserialize<PackageInfo>(File.ReadAllText(InfoPath(name)), JsonSerializerOptions.Web);
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
            return null;
        }
    }

    private void WriteInfo(string name, PackageInfo info) =>
        File.WriteAllText(InfoPath(name), JsonSerializer.Serialize(info, JsonSerializerOptions.Web));

    private void DeletePackage(string name)
    {
        foreach (var file in PackageFileNames(name))
        {
            File.Delete(Path.Combine(_packagesDir, file));
        }
    }

    private void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException e)
        {
            _log.LogWarning(e, "Could not clean up {Path}", path);
        }
    }
}
