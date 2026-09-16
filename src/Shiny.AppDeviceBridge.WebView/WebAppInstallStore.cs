using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Shiny.AppDeviceBridge.WebView;

/// <summary>
/// The downloaded builds on disk.
/// <code>
/// {root}/
///   current.json            which build is installed
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

    readonly string root = Path.GetFullPath(rootDirectory);

    string ReleasesDirectory => Path.Combine(this.root, "releases");
    string PendingDirectory => Path.Combine(this.root, "pending");
    string StatePath => Path.Combine(this.root, StateFileName);

    public string RootDirectory => this.root;

    /// <summary>The installed build, if there is one this host can run and its file is intact.</summary>
    public WebAppPackage? ReadInstalled(WebAppVersion hostVersion)
    {
        var state = this.ReadState();
        if (state is null)
            return null;

        if (!WebAppVersion.TryParse(state.Version, out var version) || state.Sha256 is not { Length: 64 } sha || !sha.All(Char.IsAsciiHexDigit))
        {
            logger.LogWarning("Ignoring unreadable install state in {Path}", this.StatePath);
            return null;
        }

        if (state.MinimumHostVersion is { } minimumText
            && (!WebAppVersion.TryParse(minimumText, out var minimum) || hostVersion < minimum))
        {
            logger.LogWarning("Installed web app {Version} needs host {Minimum}; this host is {Host}", state.Version, minimumText, hostVersion);
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

    /// <summary>Moves a verified download into place and makes it the installed build.</summary>
    public WebAppPackage Commit(string pendingPath, WebAppRelease release)
    {
        Directory.CreateDirectory(this.ReleasesDirectory);

        var sha = release.Sha256.ToLowerInvariant();
        var target = Path.Combine(this.ReleasesDirectory, sha + ".zip");

        // Named by hash, so an intact file already there holds exactly these verified bytes — a new
        // version with unchanged content — and may well be the one being served, which Windows will
        // not let a move replace.
        if (new FileInfo(target) is { Exists: true } existing && existing.Length == release.Size)
            File.Delete(pendingPath);
        else
            File.Move(pendingPath, target, overwrite: true);

        var state = new InstallState(release.Version, sha, release.Size, release.MinimumHostVersion, DateTimeOffset.UtcNow);
        var temp = this.StatePath + ".tmp";

        File.WriteAllBytes(temp, JsonSerializer.SerializeToUtf8Bytes(state, ClientJsonContext.Default.InstallState));
        File.Move(temp, this.StatePath, overwrite: true);

        return new WebAppPackage(WebAppVersion.Parse(release.Version), WebAppPackageOrigin.Installed, target, null);
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

sealed record InstallState(string Version, string Sha256, long Size, string? MinimumHostVersion, DateTimeOffset InstalledAt);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(InstallState))]
partial class ClientJsonContext : JsonSerializerContext;
