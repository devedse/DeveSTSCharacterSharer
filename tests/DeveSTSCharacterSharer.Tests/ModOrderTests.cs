using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DeveSTSCharacterSharer.Services;

namespace DeveSTSCharacterSharer.Tests;

public class ModOrderTests
{
    private static readonly IReadOnlyList<string> Libraries = SharerOptions.DefaultModLibraries;

    private static readonly SharedMod[] Shared =
    [
        new("Ash", ["BaseLib"], IsCharacter: true),
        new("Foreman", ["BaseLib"], IsCharacter: true),
        new("Yugi", ["BaseLib"], IsCharacter: true),
        new("BaseLib", [], IsCharacter: false),
    ];

    /// <summary>A settings.save the way the game writes it (2 spaces, LF), around the given mod_list entries.</summary>
    private static string Settings(IEnumerable<string> entries, string indent = "  ", string newLine = "\n")
    {
        var i = (int level) => string.Concat(Enumerable.Repeat(indent, level));
        var lines = new List<string> { "{", $"{i(1)}\"fps_limit\": 60,", $"{i(1)}\"mod_settings\": {{", $"{i(2)}\"mod_list\": [" };
        var list = entries.ToList();
        for (var n = 0; n < list.Count; n++)
        {
            var fields = list[n].Split('|');
            lines.Add($"{i(3)}{{");
            lines.Add($"{i(4)}\"id\": \"{fields[0]}\",");
            lines.Add($"{i(4)}\"is_enabled\": {fields[1]},");
            lines.Add($"{i(4)}\"source\": \"{fields[2]}\"");
            lines.Add($"{i(3)}}}" + (n < list.Count - 1 ? "," : ""));
        }
        lines.AddRange([$"{i(2)}],", $"{i(2)}\"play_with_mods\": true", $"{i(1)}}},", $"{i(1)}\"volume\": 0.5", "}"]);
        return string.Join(newLine, lines);
    }

    private static string Sort(string settings, IReadOnlyCollection<SharedMod>? shared = null) =>
        Encoding.UTF8.GetString(ModOrder.Sort(Encoding.UTF8.GetBytes(settings), shared ?? Shared, Libraries));

    private static List<string?> Ids(string settings) =>
        JsonNode.Parse(settings)!["mod_settings"]!["mod_list"]!.AsArray().Select(e => (string?)e?["id"]).ToList();

    [Fact]
    public void LibrariesFirst_ThenOtherMods_ThenCharacters_AndNewCharactersAreAdded()
    {
        var settings = Settings([
            "Yugi|true|mods_directory",
            "SavesMerger|true|workshop",
            "Ash|false|mods_directory",
            "STS2-RitsuLib|true|workshop",
            "BaseLib|true|workshop",
            "BetterModMenu|true|workshop",
        ]);

        var sorted = Sort(settings);

        // Foreman was just downloaded: added like the other shared characters, and enabled. Ash stays disabled.
        Assert.Equal(Settings([
            "BaseLib|true|workshop",
            "STS2-RitsuLib|true|workshop",
            "BetterModMenu|true|workshop",
            "SavesMerger|true|workshop",
            "Ash|false|mods_directory",
            "Foreman|true|mods_directory",
            "Yugi|true|mods_directory",
        ]), sorted);
    }

    [Fact]
    public void AnAlreadySortedFile_IsReturnedAsIs()
    {
        var settings = Encoding.UTF8.GetBytes(Settings(["BaseLib|true|workshop", "Ash|true|mods_directory", "foreman|true|mods_directory", "Yugi|true|mods_directory"]));

        Assert.Same(settings, ModOrder.Sort(settings, Shared, Libraries));
    }

    [Fact]
    public void TheRestOfTheFile_KeepsItsBomLineEndingsAndIndentation()
    {
        var input = "﻿" + Settings(["Yugi|true|mods_directory", "Ash|true|mods_directory", "Foreman|true|mods_directory"], indent: "\t", newLine: "\r\n");

        var sorted = Sort(input, Shared[..3]);

        Assert.Equal("﻿" + Settings(["Ash|true|mods_directory", "Foreman|true|mods_directory", "Yugi|true|mods_directory"], indent: "\t", newLine: "\r\n"), sorted);
    }

    [Fact]
    public void ACompactFile_StaysCompact()
    {
        const string input = """{"a":1,"mod_settings":{"mod_list":[{"id":"Yugi","is_enabled":true},{"id":"Ash","is_enabled":true}]},"b":[1, 2]}""";

        Assert.Equal("""{"a":1,"mod_settings":{"mod_list":[{"id":"Ash","is_enabled":true},{"id":"Foreman","is_enabled":true},{"id":"Yugi","is_enabled":true}]},"b":[1, 2]}""",
            Sort(input, Shared[..3]));
    }

    [Fact]
    public void SharedModsGetTheSameOrder_WhateverElsePlayersHave()
    {
        var alice = Sort(Settings(["Yugi|true|m", "Ash|true|m", "BaseLib|true|w", "Foreman|true|m"]));
        var bob = Sort(Settings(["Zeta|true|w", "Foreman|true|m", "aardvark|true|m", "Ash|true|m", "Yugi|false|m", "BaseLib|true|m", "Map Tools|true|w"]));

        var shared = Shared.Select(m => m.Id).ToHashSet();
        Assert.Equal(["BaseLib", "Ash", "Foreman", "Yugi"], Ids(alice));
        Assert.Equal(["BaseLib", "aardvark", "Map Tools", "Zeta", "Ash", "Foreman", "Yugi"], Ids(bob));
        Assert.Equal(Ids(alice), Ids(bob).Where(id => shared.Contains(id!)));
    }

    [Fact]
    public void NothingIsAdded_WithoutASharedModInTheListToCopy()
    {
        var sorted = Sort(Settings(["Zeta|true|workshop", "BetterModMenu|true|workshop"]));

        Assert.Equal(["BetterModMenu", "Zeta"], Ids(sorted));
    }

    [Fact]
    public void TheSameModFromTheModsFolderAndTheWorkshop_EndsUpInTheSameOrder()
    {
        var one = Sort(Settings(["BaseLib|true|workshop", "Ash|true|mods_directory", "BaseLib|false|mods_directory"]), Shared[..1]);
        var other = Sort(Settings(["BaseLib|false|mods_directory", "Ash|true|mods_directory", "BaseLib|true|workshop"]), Shared[..1]);

        Assert.Equal(one, other);
        Assert.Equal(["BaseLib", "BaseLib", "Ash"], Ids(one));
    }

    [Fact]
    public void KnownDependencies_ComeBeforeTheModsThatNeedThem()
    {
        SharedMod[] shared = [new("Hero", ["AlphaLib"], true), new("AlphaLib", ["ZetaCore"], false), new("ZetaCore", [], false)];

        var sorted = Sort(Settings(["Hero|true|m", "AlphaLib|true|m", "ZetaCore|true|m"]), shared);

        Assert.Equal(["ZetaCore", "AlphaLib", "Hero"], Ids(sorted));
    }

    [Fact]
    public void EntriesWithoutAnId_GoLast()
    {
        const string input = """{"mod_settings":{"mod_list":[{"is_enabled":true},{"id":"Ash","is_enabled":true},42]}}""";

        Assert.Equal("""{"mod_settings":{"mod_list":[{"id":"Ash","is_enabled":true},{"id":"Foreman","is_enabled":true},{"id":"Yugi","is_enabled":true},{"is_enabled":true},42]}}""",
            Sort(input, Shared[..3]));
    }

    [Theory]
    [InlineData("""{"fps_limit": 60}""")]
    [InlineData("""{"mod_settings": {"play_with_mods": true}}""")]
    [InlineData("""{"mod_settings": {"mod_list": []}}""")]
    public void FilesWithoutModsAreLeftAlone(string input)
    {
        var settings = Encoding.UTF8.GetBytes(input);

        Assert.Same(settings, ModOrder.Sort(settings, Shared, Libraries));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"mod_settings": {"mod_list": [""")]
    public void NotJson_Throws(string input) =>
        Assert.ThrowsAny<JsonException>(() => ModOrder.Sort(Encoding.UTF8.GetBytes(input), Shared, Libraries));
}
