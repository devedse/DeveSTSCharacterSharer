using System.Text.Json;
using DeveSTSCharacterSharer.Services;
using Microsoft.AspNetCore.Http.Features;

namespace DeveSTSCharacterSharer;

/// <summary>Plain HTTP endpoints, used by the sync scripts (and for uploading from a terminal).</summary>
public static class SharerApi
{
    public static void MapSharerApi(this WebApplication app)
    {
        // What the sync script reads: one "<hash>|<name>" line per character.
        app.MapGet("/api/manifest.txt", (HttpContext context, CharacterStore store) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Text(string.Concat(store.All.Select(c => $"{c.Hash}|{c.Name}\n")));
        });

        // <hash> is the zip for the .cmd, <hash>.tar.gz the same for the .sh.
        app.MapGet("/api/package/{file}", (string file, CharacterStore store) =>
        {
            var tarball = file.EndsWith(".tar.gz", StringComparison.Ordinal);
            if (store.GetByHash(tarball ? file[..^7] : file) is not { } character)
            {
                return Results.Text("This version is no longer available, run the sync again.", statusCode: 404);
            }
            return tarball
                ? Results.File(character.TarballPath, "application/gzip", $"{character.Name}.tar.gz")
                : Results.File(character.PackagePath, "application/zip", $"{character.Name}.zip");
        });

        app.MapGet("/download/{name}.zip", (string name, HttpContext context, CharacterStore store) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return store.Get(name) is { } character
                ? Results.File(character.PackagePath, "application/zip", $"{character.Name}.zip")
                : Results.Text("Not found", statusCode: 404);
        });

        // The page adds ?v=<hash> to the URL, so a new version is never served from a stale cache.
        app.MapGet("/api/portrait/{name}", (string name, HttpContext context, CharacterStore store) =>
        {
            if (store.Get(name)?.PortraitPath is not { } path)
            {
                return Results.Text("Not found", statusCode: 404);
            }
            context.Response.Headers.CacheControl = "public, max-age=86400";
            context.Response.Headers.XContentTypeOptions = "nosniff";
            return Results.File(path, path.EndsWith(".webp", StringComparison.Ordinal) ? "image/webp" : "image/png");
        });

        app.MapGet("/" + SyncScript.FileName, (HttpContext context, SyncScript script, SharerOptions options) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.File(script.RenderCmd(ServerUrl(options, context.Request)), "application/octet-stream", SyncScript.FileName);
        });

        app.MapGet("/" + SyncScript.ShellFileName, (HttpContext context, SyncScript script, SharerOptions options) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.File(script.RenderShell(ServerUrl(options, context.Request)), "application/octet-stream", SyncScript.ShellFileName);
        });

        // The sync scripts send the game's settings.save and write back what they get: the same file
        // with the mod list sorted (see ModOrder). Nothing is kept.
        app.MapPost("/api/sort-mods", async (HttpContext context, CharacterStore store, SharerOptions options) =>
        {
            context.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = 4 * 1024 * 1024;
            using var body = new MemoryStream();
            await context.Request.Body.CopyToAsync(body, context.RequestAborted);
            // A mod counts as a character when its .pck has character art (what the page shows).
            var shared = store.All.Select(c => new SharedMod(c.ModId, c.Dependencies, IsCharacter: c.PortraitPath != null)).ToList();
            try
            {
                return Results.File(ModOrder.Sort(body.ToArray(), shared, options.ModLibraries), "application/octet-stream");
            }
            catch (JsonException)
            {
                return Results.Text("This is not a Slay the Spire 2 settings.save file.", statusCode: 400);
            }
        });

        // The raw request body is the archive and the URL carries its name:
        //   curl -T Yugi-0.3.0.zip http://host:8080/api/upload/Yugi-0.3.0.zip
        app.MapMethods("/api/upload/{filename}", ["PUT", "POST"], async (string filename, HttpContext context, CharacterStore store, SharerOptions options) =>
        {
            context.Features.Get<IHttpMaxRequestBodySizeFeature>()!.MaxRequestBodySize = options.MaxUploadBytes;
            var archive = Path.Combine(store.CreateWorkDirectory(), "upload");
            try
            {
                await using (var file = File.Create(archive))
                {
                    await context.Request.Body.CopyToAsync(file, context.RequestAborted);
                }
                var results = await store.ImportAsync(archive, Path.GetFileName(filename), context.RequestAborted);
                return Results.Ok(new { results });
            }
            catch (UploadException e)
            {
                return Results.BadRequest(new { error = e.Message });
            }
            finally
            {
                var dir = Path.GetDirectoryName(archive)!;
                if (Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
        });
    }

    public static string ServerUrl(SharerOptions options, HttpRequest request) =>
        options.PublicUrl ?? $"{request.Scheme}://{request.Host}{request.PathBase}".TrimEnd('/');
}
