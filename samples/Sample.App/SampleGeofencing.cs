using System.Text.Json.Serialization;
using Shiny.DocumentDb;
using Shiny.DocumentDb.Sqlite;
using Shiny.Locations;
using Shiny.Locations.NorthAmerica;

namespace Sample;

/// <summary>
/// The store behind the region geofencing demo: Shiny.Geofencing.Regions.DocumentDb's US states, Canadian provinces and
/// their cities, seeded into SQLite on first run. The bridge monitors them through <c>AddRegionGeofenceBridge</c>.
/// </summary>
static class SampleGeofencing
{
    public static IServiceCollection AddSampleGeoStore(this IServiceCollection services)
    {
        services.AddDocumentStore(opts =>
        {
            // Next to the bridge's own data. FileSystem.AppDataDirectory is the bare ~/Library on the AppKit head.
            var directory = Directory.CreateDirectory(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "appdevicebridge", "sample"));
            opts.DatabaseProvider = new SqliteDatabaseProvider($"Data Source={Path.Combine(directory.FullName, "geo.db")}");

            // Source-generated, so the store and the bridge (which writes each region into its change events) stay
            // trim/AOT safe — and the page gets camelCase JSON.
            opts.JsonSerializerOptions = SampleGeoJsonContext.Default.Options;
            opts.UseReflectionFallback = false;
            opts.MapNorthAmericaData();
        });

        // Once, when the Shiny host starts; a no-op after the first run, which records the seeder's version in the store.
        services.AddNorthAmericaSeeder();
        return services;
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(GeoRegion))]
[JsonSerializable(typeof(GeoCity))]
[JsonSerializable(typeof(DocumentSeedMarker))]    // the seed runner's record of what it applied
partial class SampleGeoJsonContext : JsonSerializerContext;
