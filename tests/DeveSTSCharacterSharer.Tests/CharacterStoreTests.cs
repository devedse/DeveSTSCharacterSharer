using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using DeveSTSCharacterSharer.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace DeveSTSCharacterSharer.Tests;

public sealed class CharacterStoreTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("sts-tests-").FullName;
    private readonly string _dataDir;
    private readonly CharacterStore _store;

    public CharacterStoreTests()
    {
        _dataDir = Path.Combine(_root, "data");
        _store = CreateStore();
        _store.Load();
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private CharacterStore CreateStore() => new(new SharerOptions { DataDir = _dataDir }, NullLogger<CharacterStore>.Instance);

    private static Dictionary<string, byte[]> ModFiles(string name, string version = "1.0.0", string pck = "PCK-DATA", string prefix = "") => new()
    {
        [$"{prefix}{name}.dll"] = Encoding.UTF8.GetBytes("MZ-fake-dll-" + name),
        [$"{prefix}{name}.json"] = JsonSerializer.SerializeToUtf8Bytes(new { id = name, name, version, author = "Tester" }),
        [$"{prefix}{name}.pck"] = Encoding.UTF8.GetBytes(pck),
    };

    private string MakeZip(IEnumerable<KeyValuePair<string, byte[]>> entries, string fileName = "upload.zip")
    {
        var path = Path.Combine(_root, $"{Guid.NewGuid():N}-{fileName}");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (name, data) in entries)
        {
            using var stream = zip.CreateEntry(name).Open();
            stream.Write(data);
        }
        return path;
    }

    private async Task<IReadOnlyList<ImportResult>> Upload(string archive, string? originalName = null)
    {
        var target = Path.Combine(_store.CreateWorkDirectory(), "upload");
        File.Copy(archive, target);
        return await _store.ImportAsync(target, originalName ?? Path.GetFileName(archive));
    }

    private string[] StoredFiles(string name) =>
        ModFolder.EnumerateFiles(Path.Combine(_dataDir, "characters", name)).ToArray();

    [Fact]
    public async Task YugiLayout_KeepsOnlyTheModFolder()
    {
        var entries = ModFiles("Yugi", "0.3.0", prefix: "Yugi/")
            .Concat(new Dictionary<string, byte[]>
            {
                ["README.txt"] = "x"u8.ToArray(),
                ["Yugi.source.zip"] = "not really a zip"u8.ToArray(),
                ["Yugi.project.txt"] = "x"u8.ToArray(),
            });

        var result = Assert.Single(await Upload(MakeZip(entries), "Yugi-0.3.0.zip"));

        Assert.Equal(new ImportResult("Yugi", ImportStatus.Added, "0.3.0", null), result);
        Assert.Equal(["Yugi.dll", "Yugi.json", "Yugi.pck"], StoredFiles("Yugi"));
        var character = _store.Get("Yugi")!;
        Assert.Equal("Yugi-0.3.0.zip", character.Source);
        Assert.Equal("Tester", character.Author);
        Assert.Equal("Yugi", character.ModId);
        Assert.Empty(character.Dependencies);
        using var package = ZipFile.OpenRead(character.PackagePath);
        Assert.Equal(["Yugi/", "Yugi/Yugi.dll", "Yugi/Yugi.json", "Yugi/Yugi.pck"], package.Entries.Select(e => e.FullName).Order());

        // The same folder as .tar.gz, for the Linux/macOS script.
        var tarball = new Dictionary<string, byte[]>();
        using (var tar = new TarReader(new GZipStream(File.OpenRead(character.TarballPath), CompressionMode.Decompress)))
        {
            while (tar.GetNextEntry() is { } entry)
            {
                using var data = new MemoryStream();
                entry.DataStream?.CopyTo(data);
                tarball[entry.Name] = data.ToArray();
            }
        }
        Assert.Equal(["Yugi/", "Yugi/Yugi.dll", "Yugi/Yugi.json", "Yugi/Yugi.pck"], tarball.Keys.Order());
        Assert.Equal(File.ReadAllBytes(Path.Combine(_dataDir, "characters", "Yugi", "Yugi.pck")), tarball["Yugi/Yugi.pck"]);
    }

    [Fact]
    public async Task ModIdAndDependencies_ComeFromTheManifest()
    {
        var files = ModFiles("Frost", prefix: "Frost/");
        files["Frost/Frost.json"] = Encoding.UTF8.GetBytes("""
            {
              "id": "FrostMage",
              "version": "0.1.1",
              "dependencies": [ { "id": "BaseLib", "min_version": "3.4.1" }, "STS2-RitsuLib", { "min_version": "1" } ]
            }
            """);

        await Upload(MakeZip(files));

        var character = _store.Get("Frost")!;
        Assert.Equal("FrostMage", character.ModId);
        Assert.Equal(["BaseLib", "STS2-RitsuLib"], character.Dependencies);
    }

    [Fact]
    public async Task SameUploadIsUnchanged_NewVersionReplacesTheOldOne()
    {
        var archive = MakeZip(ModFiles("Yugi", "0.3.0", prefix: "Yugi/"));
        await Upload(archive);
        var firstHash = _store.Get("Yugi")!.Hash;

        var again = Assert.Single(await Upload(archive));
        Assert.Equal(ImportStatus.Unchanged, again.Status);
        Assert.Equal(firstHash, _store.Get("Yugi")!.Hash);

        // A release-style folder name, and an extra file in the new version.
        var entries = ModFiles("Yugi", "0.4.0", pck: "NEW", prefix: "Yugi-0.4.0/")
            .Append(new("Yugi-0.4.0/INSTALL.txt", "copy me"u8.ToArray()));
        var updated = Assert.Single(await Upload(MakeZip(entries)));

        Assert.Equal(new ImportResult("Yugi", ImportStatus.Updated, "0.4.0", "0.3.0"), updated);
        Assert.NotEqual(firstHash, _store.Get("Yugi")!.Hash);
        Assert.Equal(["INSTALL.txt", "Yugi.dll", "Yugi.json", "Yugi.pck"], StoredFiles("Yugi"));
        Assert.Single(_store.All);
        Assert.Equal(["Yugi.info.json", "Yugi.tar.gz", "Yugi.zip"], Directory.GetFiles(Path.Combine(_dataDir, "packages")).Select(Path.GetFileName).Order());
    }

    [Fact]
    public async Task ReplacingIsCaseInsensitive()
    {
        await Upload(MakeZip(ModFiles("Yugi", prefix: "Yugi/")));
        var result = Assert.Single(await Upload(MakeZip(ModFiles("yugi", "2.0", prefix: "yugi/"))));

        Assert.Equal(ImportStatus.Updated, result.Status);
        Assert.Equal(["yugi"], _store.All.Select(c => c.Name));
    }

    [Fact]
    public async Task LooseFilesAtTheArchiveRoot_KeepOnlyTheModFiles()
    {
        var entries = ModFiles("Bimbi")
            .Append(new("README.txt", "junk"u8.ToArray()))
            .Append(new("screenshots/a.png", "png"u8.ToArray()));

        var result = Assert.Single(await Upload(MakeZip(entries)));

        Assert.Equal("Bimbi", result.Name);
        Assert.Equal(["Bimbi.dll", "Bimbi.json", "Bimbi.pck"], StoredFiles("Bimbi"));
    }

    [Fact]
    public async Task SolidSevenZip()
    {
        var result = Assert.Single(await Upload(Path.Combine(AppContext.BaseDirectory, "Fixtures", "TheJester-1.0.7z")));

        Assert.Equal(new ImportResult("TheJester", ImportStatus.Added, "1.0.0", null), result);
        Assert.Equal(["TheJester.dll", "TheJester.json", "TheJester.pck"], StoredFiles("TheJester"));
        Assert.Equal(8192, new FileInfo(Path.Combine(_dataDir, "characters", "TheJester", "TheJester.pck")).Length);
    }

    [Fact]
    public async Task TarGz()
    {
        var path = Path.Combine(_root, "TaurenChieftain.tar.gz");
        await using (var file = File.Create(path))
        await using (var gzip = new GZipStream(file, CompressionLevel.Optimal))
        await using (var tar = new TarWriter(gzip))
        {
            foreach (var (name, data) in ModFiles("TaurenChieftain", prefix: "release/TaurenChieftain/"))
            {
                await tar.WriteEntryAsync(new PaxTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(data) });
            }
        }

        var result = Assert.Single(await Upload(path));

        Assert.Equal(("TaurenChieftain", ImportStatus.Added), (result.Name, result.Status));
    }

    [Fact]
    public async Task DroppedModsFolder_ImportsEveryMod_WithoutSyncMarkersOrOsJunk()
    {
        // What the browser sends for a dropped folder: a GNU tar, long paths as LongLink entries.
        await Upload(MakeZip(ModFiles("Yugi", "0.3.0", prefix: "Yugi/")));
        var longFolder = "mods/" + new string('x', 120) + "/";
        var entries = ModFiles("Yugi", "0.3.0", prefix: "mods/Yugi/")
            .Concat(ModFiles("BaseLib", prefix: longFolder + "BaseLib/"))
            .Append(new("mods/Yugi/.character-sync", "1734cba28f12"u8.ToArray()))
            .Append(new("mods/Yugi/desktop.ini", "[.ShellClassInfo]"u8.ToArray()));
        var path = Path.Combine(_root, "mods (folder)");
        await using (var file = File.Create(path))
        await using (var tar = new TarWriter(file, TarEntryFormat.Gnu))
        {
            foreach (var (name, data) in entries)
            {
                await tar.WriteEntryAsync(new GnuTarEntry(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(data) });
            }
        }

        var results = await Upload(path);

        Assert.Equal([("BaseLib", ImportStatus.Added), ("Yugi", ImportStatus.Unchanged)], results.Select(r => (r.Name, r.Status)).Order());
        Assert.Equal(["Yugi.dll", "Yugi.json", "Yugi.pck"], StoredFiles("Yugi"));
    }

    private static readonly byte[] Png = [0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
    private static readonly byte[] WebP = [.. "RIFF"u8, 4, 0, 0, 0, .. "WEBPVP8L"u8, 9, 9];

    private async Task<Character> UploadWithPck(string name, byte[] pck, string version = "1.0.0")
    {
        var files = ModFiles(name, version, prefix: $"{name}/");
        files[$"{name}/{name}.pck"] = pck;
        await Upload(MakeZip(files));
        return _store.Get(name)!;
    }

    [Fact]
    public async Task Portrait_IsTheOriginalPngInThePck()
    {
        // Like Yugi's pck (format 3): the original PNG is packed next to its .import file.
        var pck = PckBuilder.Build(3, new Dictionary<string, byte[]>
        {
            ["images/packed/card_portraits/yugi/kuriboh.png"] = [.. Png, 7],
            ["images/packed/portraits/aaa_portrait.png"] = [.. Png, 6], // another character in the same pack
            ["images/packed/portraits/yugi_hud_icon.png"] = [.. Png, 5],
            ["images/packed/portraits/yugi_portrait.png"] = Png,
            ["images/packed/portraits/yugi_portrait.png.import"] = Encoding.UTF8.GetBytes(PckBuilder.Import(".godot/imported/yugi_portrait.png-1.ctex")),
        });

        var character = await UploadWithPck("Yugi", pck);

        Assert.Equal(".png", Path.GetExtension(character.PortraitPath));
        Assert.Equal(Png, await File.ReadAllBytesAsync(character.PortraitPath!));
    }

    [Fact]
    public async Task Portrait_ComesFromTheCompressedTexture_WhenThePckHasNoOriginal()
    {
        // Like Frost Mage's pck (format 2, "res://" names): only the .import and the compiled .ctex.
        var pck = PckBuilder.Build(2, new Dictionary<string, byte[]>
        {
            ["res://images/packed/portraits/frostmage_portrait.png.import"] = Encoding.UTF8.GetBytes(PckBuilder.Import(".godot/imported/frostmage_portrait.png-a0.ctex")),
            ["res://.godot/imported/frostmage_portrait.png-a0.ctex"] = PckBuilder.Ctex(2, WebP),
        });

        var character = await UploadWithPck("FrostMage", pck);

        Assert.Equal(".webp", Path.GetExtension(character.PortraitPath));
        Assert.Equal(WebP, await File.ReadAllBytesAsync(character.PortraitPath!));
    }

    [Theory]
    [InlineData(1u, "PNG ", ".png")]
    [InlineData(2u, "WEBP", ".webp")]
    public async Task Portrait_FromATextureWrittenByGodotItself(uint dataFormat, string tag, string extension)
    {
        // Like TheJester's pck: Godot puts a "PNG " or "WEBP" tag in front of the image in a .ctex.
        var image = dataFormat == 1 ? Png : WebP;
        var pck = PckBuilder.Build(2, new Dictionary<string, byte[]>
        {
            ["res://images/packed/portraits/thejester_portrait.png.import"] = Encoding.UTF8.GetBytes(PckBuilder.Import(".godot/imported/thejester_portrait.png-cf.ctex")),
            ["res://.godot/imported/thejester_portrait.png-cf.ctex"] = PckBuilder.Ctex(dataFormat, [.. Encoding.ASCII.GetBytes(tag), .. image]),
        });

        var character = await UploadWithPck("TheJester", pck);

        Assert.Equal(extension, Path.GetExtension(character.PortraitPath));
        Assert.Equal(image, await File.ReadAllBytesAsync(character.PortraitPath!));
    }

    [Fact]
    public async Task Portrait_IsTheCharacterIcon_InModsMadeFromTheBaseLibTemplate()
    {
        // Like DentataGuttur and TaurenChieftain: <Mod>/images/charui/, only .import + .ctex.
        var files = new Dictionary<string, byte[]>();
        void AddTexture(string path, byte[] image)
        {
            var ctex = $".godot/imported/{Path.GetFileName(path)}-{files.Count}.ctex";
            files[path + ".import"] = Encoding.UTF8.GetBytes(PckBuilder.Import(ctex));
            files[ctex] = PckBuilder.Ctex(2, [.. "WEBP"u8, .. image]);
        }
        AddTexture("DentataGuttur/images/charui/character_icon_outline.png", [.. WebP, 1]);
        AddTexture("DentataGuttur/images/charui/map_marker.png", [.. WebP, 2]);
        AddTexture("DentataGuttur/images/charui/character_icon.png", WebP);

        var character = await UploadWithPck("DentataGuttur", PckBuilder.Build(3, files));

        Assert.Equal(WebP, await File.ReadAllBytesAsync(character.PortraitPath!));
    }

    [Fact]
    public async Task Portrait_FallsBackToTheHudIcon_AndSkipsGpuTextures()
    {
        var pck = PckBuilder.Build(3, new Dictionary<string, byte[]>
        {
            // A GPU-compressed (S3TC) portrait can't be shown in a browser.
            ["images/packed/portraits/bimbi_portrait.png.import"] = Encoding.UTF8.GetBytes(PckBuilder.Import(".godot/imported/bimbi_portrait.ctex")),
            [".godot/imported/bimbi_portrait.ctex"] = PckBuilder.Ctex(3, [1, 2, 3]),
            ["images/packed/portraits/bimbi_hud_icon.png.import"] = Encoding.UTF8.GetBytes(PckBuilder.Import(".godot/imported/bimbi_hud_icon.ctex")),
            [".godot/imported/bimbi_hud_icon.ctex"] = PckBuilder.Ctex(1, Png),
        });

        var character = await UploadWithPck("Bimbi", pck);

        Assert.Equal(Png, await File.ReadAllBytesAsync(character.PortraitPath!));
    }

    [Fact]
    public async Task Portrait_IsAddedOnRestart_AndRemovedWhenANewVersionHasNone()
    {
        var withPortrait = PckBuilder.Build(3, new Dictionary<string, byte[]> { ["images/packed/portraits/yugi_portrait.png"] = Png });
        var first = await UploadWithPck("Yugi", withPortrait);

        // Characters stored before portraits existed get one when the server starts.
        File.Delete(first.PortraitPath!);
        var reloaded = CreateStore();
        reloaded.Load();
        Assert.Equal(first.PortraitPath, reloaded.Get("Yugi")!.PortraitPath);
        Assert.True(File.Exists(first.PortraitPath));

        // "PCK-DATA" isn't a Godot pack at all; that's fine, there's just no portrait.
        await Upload(MakeZip(ModFiles("Yugi", "2.0.0", prefix: "Yugi/")));
        Assert.Null(_store.Get("Yugi")!.PortraitPath);
        Assert.False(File.Exists(first.PortraitPath));
    }

    [Fact]
    public async Task ZipInsideZip()
    {
        var inner = MakeZip(ModFiles("BrewmasterPanda", prefix: "BrewmasterPanda/"));
        var outer = MakeZip(new Dictionary<string, byte[]> { ["BrewmasterPanda.zip"] = File.ReadAllBytes(inner), ["readme.txt"] = "x"u8.ToArray() });

        var result = Assert.Single(await Upload(outer));

        Assert.Equal("BrewmasterPanda", result.Name);
    }

    [Fact]
    public async Task ArchiveWithTwoMods_ImportsBoth()
    {
        var entries = ModFiles("Yugi", prefix: "mods/Yugi/").Concat(ModFiles("BaseLib", prefix: "mods/BaseLib/"));

        var results = await Upload(MakeZip(entries));

        Assert.Equal(["BaseLib", "Yugi"], results.Select(r => r.Name).Order());
    }

    [Fact]
    public async Task ArchiveWithoutMod_IsRejected()
    {
        var noMod = MakeZip(new Dictionary<string, byte[]> { ["readme.txt"] = "hi"u8.ToArray(), ["stuff/thing.dll"] = "x"u8.ToArray() });
        var error = await Assert.ThrowsAsync<UploadException>(() => Upload(noMod));
        Assert.Equal(ModArchive.NoModFoundMessage, error.Message);

        var notAnArchive = Path.Combine(_root, "notes.zip");
        await File.WriteAllTextAsync(notAnArchive, "definitely not an archive");
        error = await Assert.ThrowsAsync<UploadException>(() => Upload(notAnArchive));
        Assert.StartsWith("Could not read this file as an archive", error.Message);

        Assert.Empty(_store.All);
        Assert.Empty(Directory.GetFileSystemEntries(Path.Combine(_dataDir, "work")));
    }

    [Fact]
    public async Task UnsafeName_IsRejected()
    {
        var error = await Assert.ThrowsAsync<UploadException>(() => Upload(MakeZip(ModFiles("Bad&Name", prefix: "Bad&Name/"))));

        Assert.Contains("can't be used as a folder name", error.Message);
    }

    [Fact]
    public async Task PathsEscapingTheArchive_AreIgnored()
    {
        var entries = ModFiles("Evil", prefix: "Evil/")
            .Append(new("../../escaped.txt", "nope"u8.ToArray()))
            .Append(new("Evil/../../escaped2.txt", "nope"u8.ToArray()));

        await Upload(MakeZip(entries));

        Assert.Equal(["Evil.dll", "Evil.json", "Evil.pck"], StoredFiles("Evil"));
        Assert.Empty(Directory.GetFiles(_root, "escaped*", SearchOption.AllDirectories));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(_root)!, "escaped*"));
    }

    [Fact]
    public async Task ReloadFromDisk_PicksUpManualFolders_AndDeleteRemovesEverything()
    {
        await Upload(MakeZip(ModFiles("Yugi", "0.3.0", prefix: "Yugi/")));
        var before = _store.Get("Yugi")!;

        // Folders dropped in by hand are picked up too.
        var manual = Directory.CreateDirectory(Path.Combine(_dataDir, "characters", "Manual")).FullName;
        foreach (var (name, data) in ModFiles("Manual"))
        {
            await File.WriteAllBytesAsync(Path.Combine(manual, name), data);
        }

        // Data from before there were .tar.gz packages gets them on start.
        File.Delete(before.TarballPath);

        var reloaded = CreateStore();
        reloaded.Load();
        Assert.True(File.Exists(before.TarballPath));
        Assert.Equal(before.Hash, reloaded.Get("Yugi")!.Hash);
        Assert.Equal(before.UploadedAt, reloaded.Get("Yugi")!.UploadedAt);
        Assert.True(File.Exists(reloaded.Get("Manual")!.PackagePath));

        string[]? changed = null;
        reloaded.Changed += names => changed = names.ToArray();
        Assert.True(await reloaded.DeleteAsync("yugi"));
        Assert.NotNull(changed);
        Assert.Equal(["Yugi"], changed);
        Assert.Null(reloaded.Get("Yugi"));
        Assert.False(Directory.Exists(Path.Combine(_dataDir, "characters", "Yugi")));
        Assert.False(File.Exists(before.PackagePath));
        Assert.False(File.Exists(before.TarballPath));
    }
}
