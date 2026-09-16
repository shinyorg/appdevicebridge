using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Shiny.AppDeviceBridge;

/// <summary>
/// The signature that lets a host trust a release it did not build.
/// <para>
/// ECDSA over P-256 with SHA-256. The server signs with a private key that never leaves it; the app
/// carries the public half. TLS already protects the connection, and that is not what this is for:
/// it protects against the thing serving the zip — a CDN, a storage bucket, a misconfigured proxy —
/// being able to put arbitrary code in front of every user.
/// </para>
/// <para>
/// A replayed signature is harmless as long as the host refuses to go backwards, which it does: an
/// old, validly signed release is never newer than the one installed.
/// </para>
/// </summary>
public static class WebAppReleaseSignature
{
    /// <summary>Prefixed so a signature made for anything else can never verify as a release.</summary>
    const string Scheme = "shiny-webapp-release-v1";

    /// <summary>The exact bytes that are signed. One field per line, in a fixed order.</summary>
    public static byte[] GetPayload(WebAppRelease release)
    {
        ArgumentNullException.ThrowIfNull(release);

        if (!WebAppProtocol.IsValidAppId(release.AppId))
            throw new ArgumentException($"'{release.AppId}' is not a valid app id.", nameof(release));

        if (!WebAppVersion.TryParse(release.Version, out _))
            throw new ArgumentException($"'{release.Version}' is not a valid version.", nameof(release));

        if (release.MinimumHostVersion is not null && !WebAppVersion.TryParse(release.MinimumHostVersion, out _))
            throw new ArgumentException($"'{release.MinimumHostVersion}' is not a valid host version.", nameof(release));

        if (!IsSha256Hex(release.Sha256))
            throw new ArgumentException("Sha256 must be 64 hex characters.", nameof(release));

        // Every field is validated above to contain no newline, so the line-per-field framing is
        // unambiguous — no field can end early and borrow the start of the next.
        var text = String.Join(
            '\n',
            Scheme,
            release.AppId,
            release.Version.Trim(),
            release.Sha256.ToLowerInvariant(),
            release.Size.ToString(CultureInfo.InvariantCulture),
            release.MinimumHostVersion?.Trim() ?? String.Empty
        );

        return Encoding.UTF8.GetBytes(text);
    }

    public static string Sign(WebAppRelease release, ECDsa privateKey)
    {
        ArgumentNullException.ThrowIfNull(privateKey);

        var signature = privateKey.SignData(
            GetPayload(release),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation
        );

        return Convert.ToBase64String(signature);
    }

    /// <summary>False for a missing, malformed or non-matching signature. Never throws for bad input.</summary>
    public static bool Verify(WebAppRelease release, string? signature, ECDsa publicKey)
    {
        ArgumentNullException.ThrowIfNull(publicKey);

        if (release is null || String.IsNullOrWhiteSpace(signature))
            return false;

        byte[] bytes;
        byte[] payload;

        try
        {
            bytes = Convert.FromBase64String(signature);
            payload = GetPayload(release);
        }
        catch (FormatException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }

        return publicKey.VerifyData(payload, bytes, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    /// <summary>Reads a public key as PEM (<c>-----BEGIN PUBLIC KEY-----</c>) or bare base64 SubjectPublicKeyInfo.</summary>
    public static ECDsa ImportPublicKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var ecdsa = ECDsa.Create();
        try
        {
            if (key.Contains("-----BEGIN", StringComparison.Ordinal))
                ecdsa.ImportFromPem(key);
            else
                ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(key.Trim()), out _);

            return ecdsa;
        }
        catch
        {
            ecdsa.Dispose();
            throw;
        }
    }

    /// <summary>Reads a private key as PEM (PKCS#8 or EC) or bare base64 PKCS#8.</summary>
    public static ECDsa ImportPrivateKey(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        var ecdsa = ECDsa.Create();
        try
        {
            if (key.Contains("-----BEGIN", StringComparison.Ordinal))
                ecdsa.ImportFromPem(key);
            else
                ecdsa.ImportPkcs8PrivateKey(Convert.FromBase64String(key.Trim()), out _);

            return ecdsa;
        }
        catch
        {
            ecdsa.Dispose();
            throw;
        }
    }

    /// <summary>A new P-256 key pair as PEM. The private half belongs in the server's secret store, never in the app.</summary>
    public static (string PublicKeyPem, string PrivateKeyPem) CreateKeyPair()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return (ecdsa.ExportSubjectPublicKeyInfoPem(), ecdsa.ExportPkcs8PrivateKeyPem());
    }

    /// <summary>SHA-256 of a stream as lowercase hex, the form <see cref="WebAppRelease.Sha256"/> uses.</summary>
    public static async Task<string> ComputeSha256Async(Stream stream, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    static bool IsSha256Hex(string? value)
        => value is { Length: 64 } && value.All(Char.IsAsciiHexDigit);
}
