using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Shiny.AppDeviceBridge.AspNetCore;

public sealed class WebAppReleaseServerOptions
{
    /// <summary>
    /// The ECDSA P-256 private key releases are signed with, as PEM. Required. Load it from a secret
    /// store; the public half goes into the app. <see cref="WebAppReleaseSignature.CreateKeyPair"/>
    /// makes a pair.
    /// </summary>
    public string? SigningKey { get; set; }

    /// <summary>
    /// Serves releases from this directory with <see cref="FileSystemWebAppReleaseStore"/>. Leave
    /// null and register an <see cref="IWebAppReleaseStore"/> to use a store of your own.
    /// </summary>
    public string? ReleasesDirectory { get; set; }
}

/// <summary>Signs releases with one key, safely from concurrent requests.</summary>
public sealed class WebAppReleaseSigner : IDisposable
{
    readonly System.Security.Cryptography.ECDsa key;
    readonly Lock gate = new();

    public WebAppReleaseSigner(string privateKeyPem)
        => this.key = WebAppReleaseSignature.ImportPrivateKey(privateKeyPem);

    public string Sign(WebAppRelease release)
    {
        // ECDsa instances make no promise about concurrent use.
        lock (this.gate)
            return WebAppReleaseSignature.Sign(release, this.key);
    }

    public void Dispose() => this.key.Dispose();
}

public static class WebAppReleaseExtensions
{
    /// <summary>
    /// Registers the release signer and, when <see cref="WebAppReleaseServerOptions.ReleasesDirectory"/>
    /// is set, the file system store.
    /// <code>
    /// builder.Services.AddWebAppReleases(o =>
    /// {
    ///     o.SigningKey = builder.Configuration["WebApps:SigningKey"];
    ///     o.ReleasesDirectory = "/srv/webapps";
    /// });
    /// </code>
    /// </summary>
    public static IServiceCollection AddWebAppReleases(this IServiceCollection services, Action<WebAppReleaseServerOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new WebAppReleaseServerOptions();
        configure(options);

        if (String.IsNullOrWhiteSpace(options.SigningKey))
            throw new InvalidOperationException(
                "WebAppReleaseServerOptions.SigningKey is required. Every release is signed; a host refuses one that is not."
            );

        // Imported now so a bad key fails at startup rather than on the first phone to check in.
        var signer = new WebAppReleaseSigner(options.SigningKey);

        services.TryAddSingleton(options);
        services.TryAddSingleton(signer);

        if (!String.IsNullOrWhiteSpace(options.ReleasesDirectory))
            services.TryAddSingleton<IWebAppReleaseStore>(new FileSystemWebAppReleaseStore(options.ReleasesDirectory));

        return services;
    }

    /// <summary>
    /// Maps <c>GET {prefix}/{appId}/check</c> and <c>GET {prefix}/{appId}/releases/{version}/download</c>.
    /// Returns the group, so authorization, rate limiting or output caching can be applied to both.
    /// </summary>
    public static RouteGroupBuilder MapWebAppReleases(this IEndpointRouteBuilder endpoints, string prefix = "/webapps")
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        if (endpoints.ServiceProvider.GetService<IWebAppReleaseStore>() is null)
            throw new InvalidOperationException(
                "No IWebAppReleaseStore is registered. Set ReleasesDirectory in AddWebAppReleases, or register a store of your own."
            );

        var group = endpoints.MapGroup(prefix);

        // Plain RequestDelegates: no parameter binding to generate or reflect over, so this stays
        // trim-safe in an AOT-published server.
        group.MapGet("/{appId}/check", CheckAsync);
        group.MapGet("/{appId}/releases/{version}/download", DownloadAsync);

        return group;
    }

    static async Task CheckAsync(HttpContext context)
    {
        var ct = context.RequestAborted;
        var appId = context.Request.RouteValues["appId"] as string;

        if (!WebAppProtocol.IsValidAppId(appId))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var query = context.Request.Query;
        var platform = query[WebAppProtocol.PlatformQuery].ToString();

        if (String.IsNullOrWhiteSpace(platform))
        {
            await Results.BadRequest("platform is required.").ExecuteAsync(context);
            return;
        }

        WebAppVersion? current = null;
        var currentText = query[WebAppProtocol.VersionQuery].ToString();
        if (!String.IsNullOrWhiteSpace(currentText))
        {
            if (!WebAppVersion.TryParse(currentText, out var parsed))
            {
                await Results.BadRequest("version is not a valid version.").ExecuteAsync(context);
                return;
            }

            current = parsed;
        }

        WebAppVersion? host = WebAppVersion.TryParse(query[WebAppProtocol.HostVersionQuery].ToString(), out var hostParsed)
            ? hostParsed
            : null;

        var store = context.RequestServices.GetRequiredService<IWebAppReleaseStore>();
        var releases = await store.GetReleasesAsync(appId!, ct);

        if (releases is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var policy = await store.GetPolicyAsync(appId!, ct);
        var plan = WebAppUpdatePlanner.Plan(
            releases,
            policy,
            current,
            platform,
            host,
            query[WebAppProtocol.ChannelQuery].ToString()
        );

        var response = plan.Entry is not { } entry
            ? new WebAppUpdateResponse { Kind = WebAppUpdateKind.None, MinimumVersion = plan.MinimumVersion }
            : new WebAppUpdateResponse
            {
                Kind = plan.Kind,
                Release = entry.Release,
                Signature = context.RequestServices.GetRequiredService<WebAppReleaseSigner>().Sign(entry.Release),

                // Relative, so it resolves against wherever the check was answered from — the same
                // host behind any proxy or path prefix, with nothing to configure.
                DownloadUrl = entry.DownloadUrl ?? $"releases/{Uri.EscapeDataString(entry.Release.Version)}/download",
                MinimumVersion = plan.MinimumVersion
            };

        context.Response.Headers.CacheControl = "no-store";
        await context.Response.WriteAsJsonAsync(response, WebAppJsonContext.Default.WebAppUpdateResponse, cancellationToken: ct);
    }

    static async Task DownloadAsync(HttpContext context)
    {
        var appId = context.Request.RouteValues["appId"] as string;
        var version = context.Request.RouteValues["version"] as string;

        if (!WebAppProtocol.IsValidAppId(appId) || !WebAppVersion.TryParse(version, out _))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var store = context.RequestServices.GetRequiredService<IWebAppReleaseStore>();
        var stream = await store.OpenReleaseAsync(appId!, version!, context.RequestAborted);

        if (stream is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        // Range support lets a host on a flaky connection resume rather than start over.
        await Results
            .Stream(stream, "application/zip", fileDownloadName: $"{appId}-{version}.zip", enableRangeProcessing: stream.CanSeek)
            .ExecuteAsync(context);
    }
}
