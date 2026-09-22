using Shiny.AppDeviceBridge.AspNetCore;

var builder = WebApplication.CreateBuilder(args);

// Every interface, so a phone or emulator on the same machine or network can reach it.
builder.WebHost.UseUrls("http://0.0.0.0:5199");

builder.Services.AddWebAppReleases(o =>
{
    // The committed development key. A real server reads this from its secret store.
    o.SigningKey = File.ReadAllText(Path.Combine(builder.Environment.ContentRootPath, "..", "keys", "dev-private.pem"));

    // releases/{appId}/{version}.zip — publish with ../publish-release.sh
    o.ReleasesDirectory = Path.Combine(builder.Environment.ContentRootPath, "releases");
});

// Map regions for the maps bridge, signed with the same key: maps/regions.json and the files beside it, written by
// shiny-map-packs (see maps/README.md). maps/ is not committed; a region is hundreds of megabytes.
var maps = Path.Combine(builder.Environment.ContentRootPath, "maps");
Directory.CreateDirectory(maps);
builder.Services.AddMapPacks(o =>
{
    o.SigningKey = File.ReadAllText(Path.Combine(builder.Environment.ContentRootPath, "..", "keys", "dev-private.pem"));
    o.PacksDirectory = maps;
});

var app = builder.Build();

app.MapGet("/", () => "Shiny.AppDeviceBridge sample release server. Checks: /webapps/sample/check?platform=ios&version=1.0.0");
app.MapWebAppReleases("/webapps");
app.MapMapPacks("/maps");

app.Run();
