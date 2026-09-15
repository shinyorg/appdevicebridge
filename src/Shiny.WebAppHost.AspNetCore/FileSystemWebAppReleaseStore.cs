using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shiny.WebAppHost.AspNetCore;

/// <summary>
/// Releases laid out in a directory, one folder per app:
/// <code>
/// releases/
///   myapp/
///     app.json          { "minimumVersion": "1.2.0" }               optional
///     1.2.0.zip
///     1.3.0-beta.1.zip
///     1.3.0-beta.1.json { "channel": "beta", "minimumHostVersion": "2.0" }   optional
/// </code>
/// <para>
/// Publishing is copying a file. The zip's name is its version; the hash and size are computed on
/// first sight and cached until the file changes, so there is no manifest to keep in step with the
/// files beside it.
/// </para>
/// <para>
/// Sidecar <c>{version}.json</c> fields: <c>channel</c>, <c>platforms</c>, <c>minimumHostVersion</c>,
/// <c>releaseNotes</c>, <c>publishedAt</c>, <c>downloadUrl</c>.
/// </para>
/// </summary>
public sealed class FileSystemWebAppReleaseStore : IWebAppReleaseStore
{
    readonly string root;
    readonly ConcurrentDictionary<string, FileHash> hashes = new(StringComparer.Ordinal);

    public FileSystemWebAppReleaseStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        this.root = Path.GetFullPath(rootDirectory);
    }

    public async Task<IReadOnlyList<WebAppReleaseEntry>?> GetReleasesAsync(string appId, CancellationToken cancellationToken)
    {
        if (this.GetAppDirectory(appId) is not { } directory)
            return null;

        var entries = new List<WebAppReleaseEntry>();

        foreach (var zip in Directory.EnumerateFiles(directory, "*.zip"))
        {
            var version = Path.GetFileNameWithoutExtension(zip);
            if (!WebAppVersion.TryParse(version, out _))
                continue;

            var info = new FileInfo(zip);
            var metadata = await ReadJsonAsync(
                Path.ChangeExtension(zip, ".json"),
                ServerJsonContext.Default.ReleaseMetadata,
                cancellationToken
            ).ConfigureAwait(false);

            var sha256 = await this.GetSha256Async(info, cancellationToken).ConfigureAwait(false);

            entries.Add(new WebAppReleaseEntry(
                new WebAppRelease
                {
                    AppId = appId,
                    Version = version,
                    Sha256 = sha256,
                    Size = info.Length,
                    MinimumHostVersion = metadata?.MinimumHostVersion,
                    ReleaseNotes = metadata?.ReleaseNotes,
                    PublishedAt = metadata?.PublishedAt ?? new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero)
                },
                metadata?.Channel,
                metadata?.Platforms,
                metadata?.DownloadUrl
            ));
        }

        return entries;
    }

    public Task<WebAppPolicy?> GetPolicyAsync(string appId, CancellationToken cancellationToken)
        => this.GetAppDirectory(appId) is { } directory
            ? ReadJsonAsync(Path.Combine(directory, "app.json"), ServerJsonContext.Default.WebAppPolicy, cancellationToken)
            : Task.FromResult<WebAppPolicy?>(null);

    public Task<Stream?> OpenReleaseAsync(string appId, string version, CancellationToken cancellationToken)
    {
        if (this.GetAppDirectory(appId) is not { } directory)
            return Task.FromResult<Stream?>(null);

        // Matched against what is actually in the directory rather than combined into a path, so
        // nothing arriving from the URL is ever handed to the file system.
        var match = Directory
            .EnumerateFiles(directory, "*.zip")
            .FirstOrDefault(x => String.Equals(Path.GetFileNameWithoutExtension(x), version, StringComparison.Ordinal));

        if (match is null)
            return Task.FromResult<Stream?>(null);

        Stream stream = new FileStream(match, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.Read,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan
        });

        return Task.FromResult<Stream?>(stream);
    }

    string? GetAppDirectory(string appId)
    {
        if (!WebAppProtocol.IsValidAppId(appId))
            return null;

        var directory = Path.Combine(this.root, appId);
        return Directory.Exists(directory) ? directory : null;
    }

    async Task<string> GetSha256Async(FileInfo info, CancellationToken cancellationToken)
    {
        if (this.hashes.TryGetValue(info.FullName, out var cached)
            && cached.Length == info.Length
            && cached.LastWriteTicks == info.LastWriteTimeUtc.Ticks)
            return cached.Sha256;

        await using var stream = info.OpenRead();
        var sha256 = await WebAppReleaseSignature.ComputeSha256Async(stream, cancellationToken).ConfigureAwait(false);

        this.hashes[info.FullName] = new FileHash(info.Length, info.LastWriteTimeUtc.Ticks, sha256);
        return sha256;
    }

    static async Task<T?> ReadJsonAsync<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo, CancellationToken cancellationToken)
        where T : class
    {
        if (!File.Exists(path))
            return null;

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync(stream, typeInfo, cancellationToken).ConfigureAwait(false);
    }

    readonly record struct FileHash(long Length, long LastWriteTicks, string Sha256);
}

sealed record ReleaseMetadata
{
    public string? Channel { get; init; }
    public List<string>? Platforms { get; init; }
    public string? MinimumHostVersion { get; init; }
    public string? ReleaseNotes { get; init; }
    public DateTimeOffset? PublishedAt { get; init; }
    public string? DownloadUrl { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true
)]
[JsonSerializable(typeof(ReleaseMetadata))]
[JsonSerializable(typeof(WebAppPolicy))]
partial class ServerJsonContext : JsonSerializerContext;
