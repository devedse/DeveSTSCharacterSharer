using DeveSTSCharacterSharer.Services;
using Microsoft.AspNetCore.Http.Features;

namespace DeveSTSCharacterSharer;

/// <summary>Plain HTTP endpoints, used by the sync script (and for uploading from a terminal).</summary>
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

        app.MapGet("/api/package/{hash}", (string hash, CharacterStore store) =>
            store.GetByHash(hash) is { } character
                ? Results.File(character.PackagePath, "application/zip", $"{character.Name}.zip")
                : Results.Text("This version is no longer available, run the sync again.", statusCode: 404));

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
            return Results.File(script.Render(ServerUrl(options, context.Request)), "application/octet-stream", SyncScript.FileName);
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
