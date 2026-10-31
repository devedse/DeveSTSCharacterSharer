using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DeveSTSCharacterSharer.Services;

/// <summary>A mod shared on this server, as far as sorting is concerned.</summary>
public sealed record SharedMod(string Id, IReadOnlyList<string> Dependencies, bool IsCharacter);

/// <summary>
/// Sorts the game's mod list (mod_settings.mod_list in settings.save) the same way on every PC. The
/// game loads mods in that order and adds new ones at the end, and co-op only works when everyone
/// loads the same mods in the same order. The order:
/// <list type="number">
/// <item>libraries: the configured ones (BaseLib, ...) and whatever a shared mod depends on;</item>
/// <item>all other mods that aren't characters: Workshop mods, a player's own mods, shared mods
/// without character art;</item>
/// <item>the characters shared here;</item>
/// </list>
/// each A-Z by id (ignoring case, the same on every OS), with a mod's known dependencies before it.
/// Being a plain sort, mods that only some players have don't change the order of the others.
/// Shared mods that aren't in the list yet (just downloaded, game not started since) are added, so
/// the first launch already has the right order. Only the mod_list array is rewritten; the rest of
/// the file stays byte for byte the same.
/// </summary>
public static class ModOrder
{
    private static readonly JsonDocumentOptions DocumentOptions = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };

    private sealed record Entry(JsonNode? Node, string? Id, int Index)
    {
        /// <summary>Everything but the id and enabled flag: tells the mods folder and Workshop copies of a mod apart.</summary>
        public string Shape { get; } = Node is JsonObject o
            ? string.Join(",", o.Where(p => p.Key is not ("id" or "is_enabled")).Select(p => $"{p.Key}:{p.Value?.ToJsonString()}"))
            : "";
    }

    /// <summary>Returns the sorted file, or <paramref name="settings"/> itself when nothing changes.</summary>
    /// <exception cref="JsonException">The file isn't JSON.</exception>
    public static byte[] Sort(byte[] settings, IReadOnlyCollection<SharedMod> shared, IReadOnlyCollection<string> libraries)
    {
        var offset = settings.AsSpan().StartsWith("﻿"u8) ? 3 : 0;
        var json = settings.AsMemory(offset);
        using (JsonDocument.Parse(json, DocumentOptions))
        {
            // Only checks that the whole file is valid.
        }
        if (FindModList(json.Span) is not { } range)
        {
            return settings;
        }
        var (start, end) = range;

        var list = JsonNode.Parse(json.Span[start..end], documentOptions: DocumentOptions)!.AsArray();
        var entries = list.Select((node, index) => new Entry(node, IdOf(node), index)).ToList();
        var original = entries.ToList();

        var sharedById = shared.GroupBy(m => m.Id, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        var libraryIds = libraries.Concat(shared.SelectMany(m => m.Dependencies)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int Group(Entry e) =>
            e.Id == null ? 3
            : libraryIds.Contains(e.Id) ? 0
            : sharedById.TryGetValue(e.Id, out var mod) && mod.IsCharacter ? 2
            : 1;

        var present = entries.Where(e => e.Id != null).Select(e => e.Id!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = sharedById.Keys.Where(id => !present.Contains(id)).ToList();
        if (missing.Count > 0 && FindTemplate(entries, sharedById) is { } template)
        {
            foreach (var id in missing)
            {
                var added = template.DeepClone().AsObject();
                added["id"] = id;
                if (added.ContainsKey("is_enabled"))
                {
                    added["is_enabled"] = true;
                }
                entries.Add(new Entry(added, id, entries.Count));
            }
        }

        var sorted = entries
            .OrderBy(Group)
            .ThenBy(e => e.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Id, StringComparer.Ordinal)
            .ThenBy(e => e.Shape, StringComparer.Ordinal)
            .ThenBy(e => e.Index)
            .ToList();

        // Known dependencies first (a shared library that needs another one).
        var result = new List<Entry>(sorted.Count);
        var placed = new HashSet<Entry>(ReferenceEqualityComparer.Instance);
        void Place(Entry entry)
        {
            if (!placed.Add(entry))
            {
                return;
            }
            if (entry.Id != null && sharedById.TryGetValue(entry.Id, out var mod))
            {
                foreach (var dependency in sorted.Where(e => mod.Dependencies.Contains(e.Id, StringComparer.OrdinalIgnoreCase)))
                {
                    Place(dependency);
                }
            }
            result.Add(entry);
        }
        sorted.ForEach(Place);

        if (result.SequenceEqual(original))
        {
            return settings;
        }
        var text = Format(result.Select(e => e.Node), json.Span, start, end);
        return [.. settings.AsSpan(0, offset + start), .. text, .. settings.AsSpan(offset + end)];
    }

    /// <summary>Byte range of the mod_settings.mod_list array, if the file has one.</summary>
    private static (int Start, int End)? FindModList(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
        {
            return null;
        }
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var isModSettings = reader.ValueTextEquals("mod_settings");
            reader.Read();
            if (isModSettings && reader.TokenType == JsonTokenType.StartObject)
            {
                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    var isModList = reader.ValueTextEquals("mod_list");
                    reader.Read();
                    if (isModList && reader.TokenType == JsonTokenType.StartArray)
                    {
                        var start = (int)reader.TokenStartIndex;
                        reader.Skip();
                        return (start, (int)reader.TokenStartIndex + 1);
                    }
                    reader.Skip();
                }
                return null;
            }
            reader.Skip();
        }
        return null;
    }

    private static string? IdOf(JsonNode? node) =>
        node is JsonObject o && o["id"] is JsonValue value && value.TryGetValue<string>(out var id) && !string.IsNullOrWhiteSpace(id) ? id : null;

    /// <summary>
    /// What a new entry looks like is copied from the shared mods already in the list: those came from
    /// the mods folder through the sync script too. (The fields besides id and is_enabled, like where
    /// the mod was found, are the game's business.) Characters are preferred, because a library like
    /// BaseLib may come from the Workshop instead. Without any, nothing is added and the game adds the
    /// new mods itself.
    /// </summary>
    private static JsonObject? FindTemplate(List<Entry> entries, Dictionary<string, SharedMod> sharedById) =>
        entries
            .Where(e => e.Node is JsonObject && e.Id != null && sharedById.ContainsKey(e.Id))
            .GroupBy(e => e.Shape)
            .OrderByDescending(g => g.Any(e => sharedById[e.Id!].IsCharacter))
            .ThenByDescending(g => g.Count())
            .ThenBy(g => g.Min(e => e.Index))
            .Select(g => (JsonObject)g.First().Node!)
            .FirstOrDefault();

    /// <summary>Writes the array the way the file is written: indented or not, same indent and line endings.</summary>
    private static byte[] Format(IEnumerable<JsonNode?> nodes, ReadOnlySpan<byte> json, int start, int end)
    {
        var newLine = json.IndexOf("\r\n"u8) >= 0 ? "\r\n" : "\n";
        var lineStart = json[..start].LastIndexOf((byte)'\n') + 1;
        var baseIndent = LeadingWhitespace(json[lineStart..]);
        var indented = json[start..end].IndexOf((byte)'\n') >= 0;
        var indent = "  ";
        if (indented)
        {
            var firstLine = json[start..end];
            var firstEntry = LeadingWhitespace(firstLine[(firstLine.IndexOf((byte)'\n') + 1)..]);
            if (firstEntry.Length > baseIndent.Length && firstEntry.StartsWith(baseIndent, StringComparison.Ordinal))
            {
                indent = firstEntry[baseIndent.Length..];
            }
        }
        if (indent.Distinct().Count() != 1 || indent[0] is not (' ' or '\t'))
        {
            indent = "  ";
        }

        var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = indented,
            IndentCharacter = indent[0],
            IndentSize = indent.Length,
            NewLine = newLine,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }))
        {
            writer.WriteStartArray();
            foreach (var node in nodes)
            {
                if (node == null)
                {
                    writer.WriteNullValue();
                }
                else
                {
                    node.WriteTo(writer);
                }
            }
            writer.WriteEndArray();
        }
        var text = Encoding.UTF8.GetString(buffer.ToArray());
        return Encoding.UTF8.GetBytes(indented ? text.Replace(newLine, newLine + baseIndent) : text);
    }

    private static string LeadingWhitespace(ReadOnlySpan<byte> line)
    {
        var length = line.IndexOfAnyExcept(" \t"u8);
        return Encoding.ASCII.GetString(length < 0 ? line : line[..length]);
    }
}
