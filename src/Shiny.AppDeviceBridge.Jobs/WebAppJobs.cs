using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shiny.AppDeviceBridge.Client;
using Shiny.Jobs;

namespace Shiny.AppDeviceBridge.Jobs;

public static class WebAppJobExtensions
{
    /// <summary>
    /// Schedules a background job the web app handles as <c>job:{name}</c> — in the page when it is open
    /// and listening, in <c>background.js</c> otherwise.
    /// <code>
    /// builder
    ///     .AddWebAppJob("sync", job => job.WithInternet(InternetAccess.Any))
    ///     .AddWebAppJob("cleanup", job => job.WithCharging());
    /// </code>
    /// <para>
    /// The OS decides when jobs run — roughly every 15 minutes at best on Android, far less predictably on
    /// iOS — and runs every job with the same charging and network requirements together. Jobs sharing those
    /// requirements therefore share one native job and must agree on <c>WithForeground</c> and
    /// <c>WithBatteryNotLow</c>.
    /// </para>
    /// <para>
    /// iOS needs <c>BGTaskSchedulerPermittedIdentifiers</c> in Info.plist — <c>com.shiny.job</c>,
    /// <c>com.shiny.jobnet</c>, <c>com.shiny.jobpower</c>, <c>com.shiny.jobpowernet</c> — and the
    /// <c>processing</c> background mode.
    /// </para>
    /// </summary>
    public static MauiAppBuilder AddWebAppJob(
        this MauiAppBuilder builder,
        string name,
        Func<JobRegistration, JobRegistration>? configure = null
    )
    {
        ArgumentNullException.ThrowIfNull(builder);

        if (!WebAppJobCatalog.IsValidName(name))
            throw new ArgumentException($"'{name}' is not a valid job name. Use letters, digits, '.', '_' and '-'.", nameof(name));

        var requested = configure?.Invoke(new JobRegistration(typeof(WebAppJob))) ?? new JobRegistration(typeof(WebAppJob));
        var catalog = WebAppJobCatalog.GetOrAdd(builder.Services);

#if ANDROID || IOS || MACCATALYST || WINDOWS
        builder.EnsureShiny();
#endif

        if (catalog.Add(name, requested, out var jobType))
        {
            // The first job in a category registers the native job for it; the rest ride along.
            Func<JobRegistration, JobRegistration> apply = x => x with
            {
                RunOnForeground = requested.RunOnForeground,
                RequiredInternetAccess = requested.RequiredInternetAccess,
                DeviceCharging = requested.DeviceCharging,
                BatteryNotLow = requested.BatteryNotLow
            };

            if (jobType == typeof(WebAppJob))
                builder.Services.AddJob<WebAppJob>(apply);
            else if (jobType == typeof(WebAppNetworkJob))
                builder.Services.AddJob<WebAppNetworkJob>(apply);
            else if (jobType == typeof(WebAppChargingJob))
                builder.Services.AddJob<WebAppChargingJob>(apply);
            else
                builder.Services.AddJob<WebAppChargingNetworkJob>(apply);
        }

        return builder;
    }
}

/// <summary>
/// Which web app jobs belong to which native job. Shiny registers jobs by type and the OS schedules them by
/// charging and network requirements, so there is one native type per combination of the two.
/// </summary>
sealed class WebAppJobCatalog
{
    readonly Dictionary<Type, (JobRegistration First, string FirstName, List<string> Names)> categories = [];

    public static WebAppJobCatalog GetOrAdd(IServiceCollection services)
    {
        if (services.FirstOrDefault(x => x.ServiceType == typeof(WebAppJobCatalog))?.ImplementationInstance is WebAppJobCatalog existing)
            return existing;

        var catalog = new WebAppJobCatalog();
        services.AddSingleton(catalog);
        return catalog;
    }

    public static bool IsValidName(string? name)
        => !String.IsNullOrEmpty(name)
           && name.Length <= 100
           && name.All(c => Char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    /// <summary>Adds a job. True when it is the first in its category, meaning the native job still needs registering.</summary>
    public bool Add(string name, JobRegistration registration, out Type jobType)
    {
        jobType = (registration.DeviceCharging, registration.RequiredInternetAccess != InternetAccess.None) switch
        {
            (false, false) => typeof(WebAppJob),
            (false, true) => typeof(WebAppNetworkJob),
            (true, false) => typeof(WebAppChargingJob),
            (true, true) => typeof(WebAppChargingNetworkJob)
        };

        if (this.categories.Values.Any(x => x.Names.Contains(name, StringComparer.Ordinal)))
            throw new InvalidOperationException($"A web app job named '{name}' is already registered.");

        if (!this.categories.TryGetValue(jobType, out var category))
        {
            this.categories[jobType] = (registration, name, [name]);
            return true;
        }

        if (category.First.RunOnForeground != registration.RunOnForeground || category.First.BatteryNotLow != registration.BatteryNotLow)
            throw new InvalidOperationException(
                $"Web app jobs '{category.FirstName}' and '{name}' have the same charging and network requirements, so the OS runs them " +
                "as one job — they must also agree on WithForeground and WithBatteryNotLow."
            );

        category.Names.Add(name);
        return false;
    }

    public IReadOnlyList<string> NamesFor(Type jobType)
        => this.categories.TryGetValue(jobType, out var category) ? category.Names : [];
}

abstract class WebAppJobBase(WebAppInvoker invoker, WebAppJobCatalog catalog, ILogger logger) : IJob
{
    public async Task Run(CancellationToken cancelToken)
    {
        List<Exception> failures = [];

        // One after another: the OS gave the whole category one slot of background time, and web app jobs
        // commonly touch the same settings and files.
        foreach (var name in catalog.NamesFor(this.GetType()))
        {
            cancelToken.ThrowIfCancellationRequested();

            var result = await invoker.InvokeAsync($"job:{name}", new JobRun(name), AppDeviceBridgeJsonContext.Default.JobRun, cancelToken);

            if (!result.Handled)
                logger.LogWarning("Web app job {Job} ran, but neither the page nor background.js handles 'job:{Job}'", name, name);
            else if (!result.Succeeded)
                failures.Add(new InvalidOperationException($"Web app job '{name}' failed in the {result.Target}: {result.Error}"));
        }

        // Thrown so Shiny records the run as failed; the other jobs in the category still ran.
        if (failures.Count > 0)
            throw new AggregateException(failures);
    }
}

sealed class WebAppJob(WebAppInvoker invoker, WebAppJobCatalog catalog, ILogger<WebAppJob> logger) : WebAppJobBase(invoker, catalog, logger);
sealed class WebAppNetworkJob(WebAppInvoker invoker, WebAppJobCatalog catalog, ILogger<WebAppNetworkJob> logger) : WebAppJobBase(invoker, catalog, logger);
sealed class WebAppChargingJob(WebAppInvoker invoker, WebAppJobCatalog catalog, ILogger<WebAppChargingJob> logger) : WebAppJobBase(invoker, catalog, logger);
sealed class WebAppChargingNetworkJob(WebAppInvoker invoker, WebAppJobCatalog catalog, ILogger<WebAppChargingNetworkJob> logger) : WebAppJobBase(invoker, catalog, logger);
