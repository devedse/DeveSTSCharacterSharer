using DeveSTSCharacterSharer;
using DeveSTSCharacterSharer.Components;
using DeveSTSCharacterSharer.Services;
using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);

var options = SharerOptions.FromConfiguration(builder.Configuration, builder.Environment);
builder.Services.AddSingleton(options);
builder.Services.AddSingleton<CharacterStore>();
builder.Services.AddSingleton<SyncScript>();
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(options.DataDir, "keys")));

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

var app = builder.Build();

app.Services.GetRequiredService<CharacterStore>().Load();
app.Logger.LogInformation("Sync script will use {Url}", options.PublicUrl ?? "the address the browser uses (PUBLIC_URL not set)");

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseAntiforgery();

app.MapStaticAssets();
app.MapSharerApi();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
