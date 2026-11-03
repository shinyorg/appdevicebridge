namespace Shiny.AppDeviceBridge.AspNetCore;

/// <summary>The outcome of planning: what kind of update, and which release.</summary>
public sealed record WebAppUpdatePlan(WebAppUpdateKind Kind, WebAppReleaseEntry? Entry, string? MinimumVersion);

/// <summary>
/// Decides what a host should do. Pure, so the rules can be tested without a store or a request.
/// </summary>
public static class WebAppUpdatePlanner
{
    /// <summary>
    /// Picks the newest release the host is allowed to see and able to run, then decides whether it is
    /// required.
    /// <list type="bullet">
    /// <item>A host with no version at all has nothing to show, so any release is required.</item>
    /// <item>A host below <see cref="WebAppPolicy.MinimumVersion"/> is required to update.</item>
    /// <item>Anything else newer is optional.</item>
    /// </list>
    /// A host below the minimum with no compatible newer release gets <see cref="WebAppUpdateKind.None"/>:
    /// requiring an update it cannot install would lock it out of the version it already has.
    /// </summary>
    public static WebAppUpdatePlan Plan(
        IEnumerable<WebAppReleaseEntry> releases,
        WebAppPolicy? policy,
        WebAppVersion? current,
        string platform,
        WebAppVersion? hostVersion,
        string? channel
    )
    {
        ArgumentNullException.ThrowIfNull(releases);

        WebAppReleaseEntry? latest = null;
        var latestVersion = default(WebAppVersion);

        foreach (var entry in releases)
        {
            if (!WebAppVersion.TryParse(entry.Release.Version, out var version))
                continue;

            if (!MatchesChannel(entry.Channel, channel)
                || !MatchesPlatform(entry.Platforms, platform)
                || !IsHostCompatible(entry.Release.MinimumHostVersion, hostVersion))
                continue;

            if (latest is null || version > latestVersion)
            {
                latest = entry;
                latestVersion = version;
            }
        }

        var minimumText = policy?.MinimumVersion;

        if (latest is null || (current is { } installed && latestVersion <= installed))
            return new WebAppUpdatePlan(WebAppUpdateKind.None, null, minimumText);

        var required = current is not { } running
            || (WebAppVersion.TryParse(minimumText, out var minimum) && running < minimum);

        return new WebAppUpdatePlan(required ? WebAppUpdateKind.Required : WebAppUpdateKind.Optional, latest, minimumText);
    }

    /// <summary>
    /// What to tell a native app about its own version on <paramref name="platform"/>, from <see cref="WebAppPolicy.Hosts"/> —
    /// that platform's rule, or <c>*</c>.
    /// <list type="bullet">
    /// <item>Below <see cref="WebAppHostPolicy.MinimumVersion"/>: required — the host stops at its splash screen.</item>
    /// <item>Below <see cref="WebAppHostPolicy.LatestVersion"/>: optional — the host offers it.</item>
    /// <item>Otherwise, or a host that did not say its version, or no rule: null.</item>
    /// </list>
    /// A version in the policy that does not parse is ignored rather than treated as a minimum nobody meets: a typo in
    /// <c>app.json</c> must not lock every user out.
    /// </summary>
    public static WebAppHostUpdate? PlanHost(WebAppPolicy? policy, string platform, WebAppVersion? hostVersion, string? culture)
    {
        if (hostVersion is not { } host || FindHostPolicy(policy?.Hosts, platform) is not { } rule)
            return null;

        WebAppUpdateKind kind;

        if (WebAppVersion.TryParse(rule.MinimumVersion, out var minimum) && host < minimum)
            kind = WebAppUpdateKind.Required;
        else if (WebAppVersion.TryParse(rule.LatestVersion, out var latest) && host < latest)
            kind = WebAppUpdateKind.Optional;
        else
            return null;

        return new WebAppHostUpdate
        {
            Kind = kind,
            MinimumVersion = rule.MinimumVersion,
            LatestVersion = rule.LatestVersion,
            StoreUrl = WebAppProtocol.IsAllowedStoreUrl(rule.StoreUrl) ? rule.StoreUrl : null,
            ReleaseNotes = rule.ReleaseNotes?.Resolve(culture)
        };
    }

    static WebAppHostPolicy? FindHostPolicy(IReadOnlyDictionary<string, WebAppHostPolicy>? hosts, string platform)
    {
        if (hosts is not { Count: > 0 })
            return null;

        WebAppHostPolicy? any = null;

        foreach (var (name, rule) in hosts)
        {
            if (String.Equals(name, platform, StringComparison.OrdinalIgnoreCase))
                return rule;

            if (name == "*")
                any = rule;
        }

        return any;
    }

    /// <summary>Stable releases are seen by everyone; a named channel only by hosts that ask for it.</summary>
    static bool MatchesChannel(string? releaseChannel, string? hostChannel)
        => String.IsNullOrWhiteSpace(releaseChannel)
           || String.Equals(releaseChannel, hostChannel, StringComparison.OrdinalIgnoreCase);

    static bool MatchesPlatform(IReadOnlyList<string>? platforms, string platform)
        => platforms is not { Count: > 0 }
           || platforms.Contains(platform, StringComparer.OrdinalIgnoreCase);

    /// <summary>An unreadable minimum, or a host that did not say its version, is treated as incompatible.</summary>
    static bool IsHostCompatible(string? minimumHostVersion, WebAppVersion? hostVersion)
    {
        if (String.IsNullOrWhiteSpace(minimumHostVersion))
            return true;

        return WebAppVersion.TryParse(minimumHostVersion, out var minimum)
               && hostVersion is { } host
               && host >= minimum;
    }
}
