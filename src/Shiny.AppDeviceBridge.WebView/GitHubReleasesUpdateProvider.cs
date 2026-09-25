using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Shiny.AppDeviceBridge.WebView;

/// <summary>
/// Releases from a GitHub repository's releases: each release is a version of the web app, its tag the version
/// (<c>v1.2.0</c> or <c>1.2.0</c>), its notes the what's new, and one of its assets the zip.
/// <code>
/// webApp.UpdateProvider = new GitHubReleasesUpdateProvider("https://github.com/acme/field-app");
/// </code>
/// <para>
/// Drafts are never offered, prereleases only with <see cref="IncludePrereleases"/>, and a release without a matching
/// asset is skipped. The asset's SHA-256 is checked when GitHub reports one (<c>digest</c>, on every asset uploaded since
/// mid-2025). Releases carry no signature: trust rests on HTTPS to GitHub and on who can publish to the repository.
/// </para>
/// <para>
/// Only the newest 100 releases are read. Unauthenticated calls to the GitHub API are limited to 60 an hour per IP
/// address — plenty for a check per launch, but set <see cref="Token"/> for a private repository or a shared network.
/// GitHub Enterprise Server is reached at <c>https://{host}/api/v3/</c>.
/// </para>
/// </summary>
public sealed class GitHubReleasesUpdateProvider : IUpdateProvider, IDisposable
{
    readonly Lazy<HttpClient> http;

    /// <param name="repositoryUrl">The repository's address, such as <c>https://github.com/acme/field-app</c>.</param>
    /// <param name="httpMessageHandlerFactory">Supplies the handler for API calls and downloads — for a proxy, or tests.</param>
    public GitHubReleasesUpdateProvider(string repositoryUrl, Func<HttpMessageHandler>? httpMessageHandlerFactory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryUrl);

        if (!Uri.TryCreate(repositoryUrl.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http"))
            throw new ArgumentException($"'{repositoryUrl}' is not a repository URL such as https://github.com/owner/repo.", nameof(repositoryUrl));

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length < 2)
            throw new ArgumentException($"'{repositoryUrl}' does not name an owner and a repository.", nameof(repositoryUrl));

        this.Owner = Uri.UnescapeDataString(segments[0]);
        this.Repository = Uri.UnescapeDataString(segments[1]);
        if (this.Repository.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            this.Repository = this.Repository[..^4];

        this.ApiBaseAddress = uri.Host is "github.com" or "www.github.com"
            ? new Uri("https://api.github.com/")
            : new Uri($"{uri.Scheme}://{uri.Authority}/api/v3/");

        // Created on first use, so a handler factory can depend on something not built yet when the provider is.
        this.http = new(() =>
        {
            var client = httpMessageHandlerFactory is null ? new HttpClient() : new HttpClient(httpMessageHandlerFactory(), disposeHandler: true);

            // Downloads can legitimately take minutes; the host times out the check and cancels the rest.
            client.Timeout = Timeout.InfiniteTimeSpan;
            return client;
        });
    }

    public string Owner { get; }

    public string Repository { get; }

    /// <summary><c>https://api.github.com/</c>, or <c>https://{host}/api/v3/</c> for GitHub Enterprise Server.</summary>
    public Uri ApiBaseAddress { get; }

    /// <summary>
    /// The asset holding the zip: an exact file name, or a pattern where <c>*</c> matches anything, such as
    /// <c>webapp-*.zip</c>. Null takes the first asset ending in <c>.zip</c>.
    /// </summary>
    public string? AssetName { get; set; }

    /// <summary>Offer releases GitHub marks as prereleases. Off by default.</summary>
    public bool IncludePrereleases { get; set; }

    /// <summary>
    /// A prefix every web app tag starts with, for a repository that tags other things too — <c>webapp-</c> for
    /// <c>webapp-v1.2.0</c>. Tags without it are skipped. A <c>v</c> after the prefix is always allowed.
    /// </summary>
    public string? TagPrefix { get; set; }

    /// <summary>A token for private repositories and the higher rate limit: a fine-grained token with read access to contents is enough.</summary>
    public string? Token { get; set; }

    /// <summary>
    /// Decides which releases must be installed before the app is shown. The update is required when any release newer than
    /// the running one is. Null makes every update optional.
    /// <code>
    /// provider.RequiredWhen = release => release.WhatsNew?.Contains("[required]") == true;
    /// </code>
    /// </summary>
    public Func<GitHubUpdateInfo, bool>? RequiredWhen { get; set; }

    public async Task<UpdateInfo?> GetUpdateInfoAsync(Version currentHostVersion, WebAppVersion? currentAppVersion, CancellationToken cancellationToken)
    {
        var uri = new Uri(
            this.ApiBaseAddress,
            $"repos/{Uri.EscapeDataString(this.Owner)}/{Uri.EscapeDataString(this.Repository)}/releases?per_page=100"
        );

        using var request = this.CreateRequest(uri, "application/vnd.github+json");
        using var response = await this.http.Value.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var releases = await response.Content
            .ReadFromJsonAsync(GitHubJsonContext.Default.ListGitHubRelease, cancellationToken)
            .ConfigureAwait(false) ?? [];

        var newer = releases
            .Select(this.ToUpdateInfo)
            .OfType<GitHubUpdateInfo>()
            .Where(x => currentAppVersion is not { } current || x.Version > current)
            .OrderByDescending(x => x.Version)
            .ToList();

        if (newer.Count == 0)
            return null;

        var latest = newer[0];
        latest.IsOptional = this.RequiredWhen is not { } required || !newer.Any(required);
        return latest;
    }

    GitHubUpdateInfo? ToUpdateInfo(GitHubRelease release)
    {
        if (release.Draft || (release.Prerelease && !this.IncludePrereleases) || String.IsNullOrWhiteSpace(release.TagName))
            return null;

        var tag = release.TagName.Trim();
        if (this.TagPrefix is { Length: > 0 } prefix)
        {
            if (!tag.StartsWith(prefix, StringComparison.Ordinal))
                return null;

            tag = tag[prefix.Length..];
        }

        if (tag.StartsWith('v') || tag.StartsWith('V'))
            tag = tag[1..];

        if (!WebAppVersion.TryParse(tag, out var version))
            return null;

        var asset = release.Assets?.FirstOrDefault(this.Matches);
        if (asset is null || String.IsNullOrWhiteSpace(asset.Url))
            return null;

        return new GitHubUpdateInfo
        {
            Version = version,
            TagName = release.TagName,
            ReleaseUrl = release.HtmlUrl,
            AssetName = asset.Name!,
            AssetUrl = new Uri(asset.Url),
            WhatsNew = release.Body,
            ReleaseDate = release.PublishedAt,
            FileSize = asset.Size > 0 ? asset.Size : null,
            Sha256 = asset.Digest is { } digest && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..] : null,
            IsOptional = true
        };
    }

    bool Matches(GitHubAsset asset)
    {
        if (String.IsNullOrWhiteSpace(asset.Name))
            return false;

        if (this.AssetName is not { Length: > 0 } pattern)
            return asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

        if (!pattern.Contains('*'))
            return String.Equals(asset.Name, pattern, StringComparison.OrdinalIgnoreCase);

        var regex = "^" + String.Join(".*", pattern.Split('*').Select(Regex.Escape)) + "$";
        return Regex.IsMatch(asset.Name, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    public async Task<Stream> DownloadAsync(UpdateInfo update, CancellationToken cancellationToken)
    {
        if (update is not GitHubUpdateInfo { AssetUrl: { } uri })
            throw new ArgumentException("Not a release from this provider.", nameof(update));

        // The API's asset URL answers with a redirect to the file itself, for public and private repositories alike.
        // HttpClient drops the Authorization header when it follows it, so the token never reaches the storage host.
        using var request = this.CreateRequest(uri, "application/octet-stream");
        var response = await this.http.Value.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        try
        {
            response.EnsureSuccessStatusCode();

            // Disposing the content stream releases the response with it.
            return await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    HttpRequestMessage CreateRequest(Uri uri, string accept)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(accept));
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Shiny.AppDeviceBridge", null));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        if (!String.IsNullOrWhiteSpace(this.Token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", this.Token);

        return request;
    }

    public void Dispose()
    {
        if (this.http.IsValueCreated)
            this.http.Value.Dispose();
    }
}

/// <summary>A release offered by <see cref="GitHubReleasesUpdateProvider"/>.</summary>
public sealed class GitHubUpdateInfo : UpdateInfo
{
    public required string TagName { get; init; }

    /// <summary>The release's page on GitHub.</summary>
    public string? ReleaseUrl { get; init; }

    public required string AssetName { get; init; }

    /// <summary>The asset's API address, which <see cref="GitHubReleasesUpdateProvider.DownloadAsync"/> fetches.</summary>
    public required Uri AssetUrl { get; init; }
}

sealed record GitHubRelease(
    string? TagName,
    string? Body,
    string? HtmlUrl,
    bool Draft,
    bool Prerelease,
    DateTimeOffset? PublishedAt,
    List<GitHubAsset>? Assets
);

sealed record GitHubAsset(string? Name, string? Url, long Size, string? Digest);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(List<GitHubRelease>))]
partial class GitHubJsonContext : JsonSerializerContext;
