using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Shiny.AppDeviceBridge.Maps.Valhalla;

public static class OnDeviceDirectionsExtensions
{
    /// <summary>
    /// Lets <c>/_bridge/directions</c> compute routes on the device, inside regions whose road network the user downloaded.
    /// Call it after <c>AddMapsBridge</c>. On Android and iOS it registers valhalla-mobile's engine; everywhere else it
    /// registers nothing, and directions go online.
    /// <code>
    /// bridge
    ///     .AddMapsBridge(o => { … })
    ///     .AddOnDeviceDirections();
    /// </code>
    /// </summary>
    public static TBuilder AddOnDeviceDirections<TBuilder>(this TBuilder bridge) where TBuilder : AppDeviceBridgeBuilder
    {
        ArgumentNullException.ThrowIfNull(bridge);

#if ANDROID || IOS
        bridge.Services.TryAddSingleton<IOnDeviceRouterFactory, ValhallaRouterFactory>();
#endif
        return bridge;
    }
}

/// <summary>The Valhalla config for one region: the embedded defaults, pointed at its tile extract.</summary>
static class ValhallaConfig
{
    const string Placeholder = "__TILE_EXTRACT__";

    /// <summary>Writes the config beside the extract — Valhalla reads its config from a file — and returns its path.</summary>
    public static string WriteFor(string tileExtractPath)
    {
        using var stream = typeof(ValhallaConfig).Assembly.GetManifestResourceStream("valhalla-config.json")
                           ?? throw new InvalidOperationException("The embedded Valhalla config is missing.");
        using var reader = new StreamReader(stream);

        // The path goes into a JSON string, so it is escaped as one.
        var escaped = System.Text.Json.JsonEncodedText.Encode(tileExtractPath).ToString();
        var config = reader.ReadToEnd().Replace(Placeholder, escaped, StringComparison.Ordinal);

        var path = Path.ChangeExtension(tileExtractPath, ".valhalla.json");
        File.WriteAllText(path, config);
        return path;
    }
}

#if ANDROID || IOS
sealed class ValhallaRouterFactory : IOnDeviceRouterFactory
{
    public IValhallaRouter Open(string tileExtractPath)
    {
#if IOS
        TimeZoneData.Ensure();
#endif
        return new ValhallaNativeRouter(ValhallaConfig.WriteFor(tileExtractPath));
    }
}

#if IOS
/// <summary>
/// Valhalla's date library reads the time zone database from <c>$HOME/Library/tzdata</c> on iOS, and without it the engine
/// starts but finds no roads near any stop. The package embeds valhalla-mobile's copy; it is unpacked on first use, and
/// again when a newer package brings a different one.
/// </summary>
static class TimeZoneData
{
    const string Resource = "valhalla-tzdata.tar";
    static readonly Lock gate = new();
    static bool ensured;

    public static void Ensure()
    {
        lock (gate)
        {
            if (ensured)
                return;

            using var tar = typeof(TimeZoneData).Assembly.GetManifestResourceStream(Resource)
                            ?? throw new InvalidOperationException("The embedded time zone database is missing.");

            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "tzdata");
            var marker = Path.Combine(directory, ".shiny-tzdata");
            var version = tar.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);

            if (!File.Exists(marker) || File.ReadAllText(marker) != version)
            {
                Directory.CreateDirectory(directory);
                System.Formats.Tar.TarFile.ExtractToDirectory(tar, directory, overwriteFiles: true);
                File.WriteAllText(marker, version);
            }

            ensured = true;
        }
    }
}
#endif

/// <summary>
/// One valhalla-mobile actor. The actor is not safe to use from two threads at once, so calls queue; each runs on the
/// thread pool, since a route blocks for as long as it takes.
/// </summary>
sealed class ValhallaNativeRouter : IValhallaRouter
{
    readonly SemaphoreSlim gate = new(1, 1);
    readonly string configPath;
    nint actor;

    public ValhallaNativeRouter(string configPath)
    {
        this.configPath = configPath;
        this.actor = ValhallaNative.Create(configPath);
    }

    public async Task<string> RouteAsync(string requestJson, CancellationToken cancellationToken)
    {
        await this.gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(this.actor == 0, this);
            var actor = this.actor;
            var answer = await Task.Run(() => ValhallaNative.Route(actor, requestJson), CancellationToken.None).ConfigureAwait(false);

            if (ValhallaException.TryParse(answer, 400) is { } failure)
                throw failure;

            return answer;
        }
        finally
        {
            this.gate.Release();
        }
    }

    public void Dispose()
    {
        // Waits for a route in progress: the actor cannot be freed under it.
        this.gate.Wait();
        try
        {
            if (this.actor != 0)
                ValhallaNative.Delete(this.actor);

            this.actor = 0;

            try
            {
                File.Delete(this.configPath);
            }
            catch (IOException)
            {
            }
        }
        finally
        {
            this.gate.Release();
        }
    }
}
#endif
