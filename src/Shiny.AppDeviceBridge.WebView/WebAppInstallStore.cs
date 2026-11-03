using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Shiny.AppDeviceBridge.WebView;

/// <summary>
/// The downloaded builds on disk.
/// <code>
/// {root}/
///   current.json            which build is installed
///   host-update.json        a native app update the server last required, for launches that cannot reach it
///   releases/{sha256}.zip   the build itself, named by its hash
///   pending/*.download      in-flight downloads, cleared at startup
/// </code>
/// <para>
/// Named by hash so a file name never comes from the network, and so a half-written file can never
/// be mistaken for a finished one: a download only reaches <c>releases/</c> after it has been hashed.
/// <c>current.json</c> is replaced atomically, so a crash mid-install leaves the previous build in
/// place rather than none.
/// </para>
/// </summary>
sealed class WebAppInstallStore(string rootDirectory, ILogger logger)
{
    const string StateFileName = "current.json";
    const string HostRequirementFileName = "host-update.json";

    readonly string root = Path.GetFullPath(rootDirectory);

    string ReleasesDirectory => Path.Combine(this.root, "releases");
    string PendingDirectory => Path.Combine(this.root, "pending");
    string StatePath => Path.Combine(this.root, StateFileName);
    string HostRequirementPath => Path.Combine(this.root, HostRequirementFileName);

    public string RootDirectory => this.root;

    /// <summary>The installed build, if there is one and its file is intact.</summary>
    public WebAppPackage? ReadInstalled()
    {
        var state = this.ReadState();
        if (state is null)
            return null;

        if (!WebAppVersion.TryParse(state.Version, out var version) || state.Sha256 is not { Length: 64 } sha || !sha.All(Char.IsAsciiHexDigit))
        {
            logger.LogWarning("Ignoring unreadable install state in {Path}", this.StatePath);
            return null;
        }

        var path = Path.Combine(this.ReleasesDirectory, sha.ToLowerInvariant() + ".zip");
        var info = new FileInfo(path);

        // The hash was checked at install. Checking the length on every launch is free and catches
        // the common corruption — a file truncated by a full disk or a sync tool.
        if (!info.Exists || info.Length != state.Size)
        {
            logger.LogWarning("Installed web app {Version} is missing or truncated at {Path}", state.Version, path);
            return null;
        }

        return new WebAppPackage(version, WebAppPackageOrigin.Installed, path, null);
    }

    public string CreatePendingPath()
    {
        Directory.CreateDirectory(this.PendingDirectory);
        return Path.Combine(this.PendingDirectory, Guid.NewGuid().ToString("n") + ".download");
    }

    readonly Lock commitLock = new();

    /// <summary>
    /// Moves a verified download into place and makes it the installed build. One at a time: two installs committing at once
    /// — a background update and one the page asked for — would each move a file onto the same state file, and Windows
    /// refuses the second move while the first holds it.
    /// </summary>
    public WebAppPackage Commit(string pendingPath, WebAppVersion version, string sha256, long size)
    {
        lock (this.commitLock)
            return this.CommitCore(pendingPath, version, sha256, size);
    }

    WebAppPackage CommitCore(string pendingPath, WebAppVersion version, string sha256, long size)
    {
        Directory.CreateDirectory(this.ReleasesDirectory);

        var sha = sha256.ToLowerInvariant();
        var target = Path.Combine(this.ReleasesDirectory, sha + ".zip");

        // Named by hash, so an intact file already there holds exactly these verified bytes — a new
        // version with unchanged content — and may well be the one being served, which Windows will
        // not let a move replace.
        if (new FileInfo(target) is { Exists: true } existing && existing.Length == size)
            File.Delete(pendingPath);
        else
            File.Move(pendingPath, target, overwrite: true);

        var state = new InstallState(version.ToString(), sha, size, DateTimeOffset.UtcNow);
        // A temp file of its own, so a crash mid-write never leaves a half-written state file behind. The last to commit wins.
        var temp = $"{this.StatePath}.{Guid.NewGuid():n}.tmp";

        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(state, ClientJsonContext.Default.InstallState));
        File.Move(temp, this.StatePath, overwrite: true);

        return new WebAppPackage(version, WebAppPackageOrigin.Installed, target, null);
    }

    /// <summary>Forgets the installed build, for when it turns out not to open.</summary>
    public void ClearInstalled()
    {
        try
        {
            File.Delete(this.StatePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not clear install state at {Path}", this.StatePath);
        }
    }

    /// <summary>
    /// Deletes interrupted downloads and every build except the one being served. Call before any
    /// download starts, since it clears the pending folder.
    /// </summary>
    public void Prune(WebAppPackage? active)
    {
        DeleteAll(this.PendingDirectory, "*", keep: null);
        DeleteAll(this.ReleasesDirectory, "*.zip", keep: active?.ZipPath);
    }

    void DeleteAll(string directory, string pattern, string? keep)
    {
        if (!Directory.Exists(directory))
            return;

        foreach (var file in Directory.EnumerateFiles(directory, pattern))
        {
            if (keep is not null && String.Equals(Path.GetFullPath(file), Path.GetFullPath(keep), StringComparison.Ordinal))
                continue;

            try
            {
                File.Delete(file);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A file still being read by a response from the previous build. Next launch gets it.
                logger.LogDebug(ex, "Could not delete {File}", file);
            }
        }
    }

    /// <summary>
    /// The native app update the server last required, so a launch with the network off cannot step around it. Null when
    /// there is none, or this native app has since reached the minimum it named — it was updated from the store.
    /// </summary>
    public HostUpdateInfo? ReadHostRequirement(string hostVersion)
    {
        try
        {
            if (!File.Exists(this.HostRequirementPath))
                return null;

            var saved = JsonSerializer.Deserialize(File.ReadAllBytes(this.HostRequirementPath), ClientJsonContext.Default.HostRequirement);

            if (saved is null
                || !WebAppVersion.TryParse(saved.MinimumVersion, out var minimum)
                || !WebAppVersion.TryParse(hostVersion, out var host)
                || host >= minimum)
                return null;

            return new HostUpdateInfo
            {
                IsRequired = true,
                MinimumVersion = saved.MinimumVersion,
                LatestVersion = saved.LatestVersion,
                StoreUri = WebAppProtocol.IsAllowedStoreUrl(saved.StoreUrl) ? new Uri(saved.StoreUrl!) : null,
                WhatsNew = saved.WhatsNew
            };
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not read the native app requirement at {Path}", this.HostRequirementPath);
            return null;
        }
    }

    /// <summary>Records what the server just said about the native app: a requirement to remember, or none to forget.</summary>
    public void SaveHostRequirement(HostUpdateInfo? update)
    {
        try
        {
            if (update is not { IsRequired: true, MinimumVersion: { } minimum })
            {
                if (File.Exists(this.HostRequirementPath))
                    File.Delete(this.HostRequirementPath);

                return;
            }

            Directory.CreateDirectory(this.root);

            var temp = $"{this.HostRequirementPath}.{Guid.NewGuid():n}.tmp";
            var saved = new HostRequirement(minimum, update.LatestVersion, update.StoreUri?.AbsoluteUri, update.WhatsNew);

            File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(saved, ClientJsonContext.Default.HostRequirement));
            File.Move(temp, this.HostRequirementPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not save the native app requirement at {Path}", this.HostRequirementPath);
        }
    }

    InstallState? ReadState()
    {
        try
        {
            if (!File.Exists(this.StatePath))
                return null;

            return JsonSerializer.Deserialize(File.ReadAllBytes(this.StatePath), ClientJsonContext.Default.InstallState);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "Could not read install state at {Path}", this.StatePath);
            return null;
        }
    }
}

sealed record InstallState(string Version, string Sha256, long Size, DateTimeOffset InstalledAt);

sealed record HostRequirement(string MinimumVersion, string? LatestVersion, string? StoreUrl, string? WhatsNew);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(InstallState))]
[JsonSerializable(typeof(HostRequirement))]
partial class ClientJsonContext : JsonSerializerContext;
