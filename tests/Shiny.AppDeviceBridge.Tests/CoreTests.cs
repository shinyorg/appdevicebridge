using System.Security.Cryptography;
using Shiny.AppDeviceBridge.AspNetCore;

namespace Shiny.AppDeviceBridge.Tests;

public class WebAppVersionTests
{
    [Theory]
    [InlineData("1.0.0", "1.0.1")]
    [InlineData("1.9.0", "1.10.0")]
    [InlineData("1.0.0-beta", "1.0.0")]
    [InlineData("1.0.0-alpha", "1.0.0-alpha.1")]
    [InlineData("1.0.0-alpha.2", "1.0.0-alpha.10")]
    [InlineData("1.0.0-1", "1.0.0-alpha")]
    [InlineData("1.0.0-alpha", "1.0.0-beta")]
    [InlineData("2.0", "2.0.0.1")]
    public void Orders(string lower, string higher)
    {
        Assert.True(WebAppVersion.Parse(lower) < WebAppVersion.Parse(higher));
        Assert.True(WebAppVersion.Parse(higher) > WebAppVersion.Parse(lower));
    }

    [Theory]
    [InlineData("1.2", "1.2.0")]
    [InlineData("1.0.0+abc", "1.0.0+def")]
    public void Equal(string a, string b)
    {
        Assert.Equal(WebAppVersion.Parse(a), WebAppVersion.Parse(b));
        Assert.Equal(WebAppVersion.Parse(a).GetHashCode(), WebAppVersion.Parse(b).GetHashCode());
    }

    [Theory]
    [InlineData("")]
    [InlineData("1..0")]
    [InlineData("a.b")]
    [InlineData("1.2.3.4.5")]
    [InlineData("1.0-")]
    [InlineData("1.0.0-beta..1")]
    [InlineData("-1.0")]
    public void RejectsInvalid(string value) => Assert.False(WebAppVersion.TryParse(value, out _));
}

public class WebAppReleaseSignatureTests
{
    static WebAppRelease Release() => new()
    {
        AppId = "demo",
        Version = "1.2.0",
        Sha256 = new string('a', 64),
        Size = 1024,
        MinimumHostVersion = "2.0"
    };

    [Fact]
    public void RoundTrips()
    {
        var (publicPem, privatePem) = WebAppReleaseSignature.CreateKeyPair();
        using var privateKey = WebAppReleaseSignature.ImportPrivateKey(privatePem);
        using var publicKey = WebAppReleaseSignature.ImportPublicKey(publicPem);

        var signature = WebAppReleaseSignature.Sign(Release(), privateKey);

        Assert.True(WebAppReleaseSignature.Verify(Release(), signature, publicKey));
    }

    [Fact]
    public void IgnoresUnsignedFields()
    {
        var (publicPem, privatePem) = WebAppReleaseSignature.CreateKeyPair();
        using var privateKey = WebAppReleaseSignature.ImportPrivateKey(privatePem);
        using var publicKey = WebAppReleaseSignature.ImportPublicKey(publicPem);

        var signature = WebAppReleaseSignature.Sign(Release(), privateKey);

        Assert.True(WebAppReleaseSignature.Verify(Release() with { ReleaseNotes = "changed" }, signature, publicKey));
    }

    public static TheoryData<WebAppRelease> Tampered => new()
    {
        Release() with { Version = "9.9.9" },
        Release() with { Sha256 = new string('b', 64) },
        Release() with { Size = 1025 },
        Release() with { AppId = "other" },
        Release() with { MinimumHostVersion = null },
        Release() with { AppId = "demo\n1.2.0" }
    };

    [Theory]
    [MemberData(nameof(Tampered))]
    public void RejectsTampering(WebAppRelease tampered)
    {
        var (publicPem, privatePem) = WebAppReleaseSignature.CreateKeyPair();
        using var privateKey = WebAppReleaseSignature.ImportPrivateKey(privatePem);
        using var publicKey = WebAppReleaseSignature.ImportPublicKey(publicPem);

        var signature = WebAppReleaseSignature.Sign(Release(), privateKey);

        Assert.False(WebAppReleaseSignature.Verify(tampered, signature, publicKey));
    }

    [Fact]
    public void RejectsOtherKeyAndGarbage()
    {
        var (_, privatePem) = WebAppReleaseSignature.CreateKeyPair();
        var (otherPublicPem, _) = WebAppReleaseSignature.CreateKeyPair();
        using var privateKey = WebAppReleaseSignature.ImportPrivateKey(privatePem);
        using var otherKey = WebAppReleaseSignature.ImportPublicKey(otherPublicPem);

        Assert.False(WebAppReleaseSignature.Verify(Release(), WebAppReleaseSignature.Sign(Release(), privateKey), otherKey));
        Assert.False(WebAppReleaseSignature.Verify(Release(), "not base64!", otherKey));
        Assert.False(WebAppReleaseSignature.Verify(Release(), null, otherKey));
    }

    [Fact]
    public void ImportsBareBase64PublicKey()
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var bare = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());

        using var imported = WebAppReleaseSignature.ImportPublicKey(bare);
        var signature = WebAppReleaseSignature.Sign(Release(), ecdsa);

        Assert.True(WebAppReleaseSignature.Verify(Release(), signature, imported));
    }

    [Fact]
    public void BuildsCheckUri()
    {
        var uri = WebAppProtocol.BuildCheckUri(new Uri("https://api.test/webapps/"), "demo", "1.0.0-beta.1", "ios", "2.0", "beta");
        Assert.Equal("https://api.test/webapps/demo/check?version=1.0.0-beta.1&platform=ios&host=2.0&channel=beta", uri.AbsoluteUri);
    }
}

public class WebAppUpdatePlannerTests
{
    static WebAppReleaseEntry Entry(string version, string? channel = null, string[]? platforms = null, string? minimumHost = null)
        => new(
            new WebAppRelease { AppId = "demo", Version = version, Sha256 = new string('a', 64), Size = 1, MinimumHostVersion = minimumHost },
            channel,
            platforms
        );

    static WebAppUpdatePlan Plan(
        WebAppReleaseEntry[] releases,
        string? current,
        string? minimum = null,
        string platform = "ios",
        string host = "1.0",
        string? channel = null
    ) => WebAppUpdatePlanner.Plan(
        releases,
        new WebAppPolicy { MinimumVersion = minimum },
        current is null ? null : WebAppVersion.Parse(current),
        platform,
        WebAppVersion.Parse(host),
        channel
    );

    [Fact]
    public void NothingInstalledIsRequired()
    {
        var plan = Plan([Entry("1.0.0"), Entry("1.1.0")], current: null);

        Assert.Equal(WebAppUpdateKind.Required, plan.Kind);
        Assert.Equal("1.1.0", plan.Entry!.Release.Version);
    }

    [Fact]
    public void CurrentIsNone() => Assert.Equal(WebAppUpdateKind.None, Plan([Entry("1.1.0")], "1.1.0").Kind);

    [Fact]
    public void NewerIsOptional() => Assert.Equal(WebAppUpdateKind.Optional, Plan([Entry("1.1.0")], "1.0.0").Kind);

    [Fact]
    public void BelowMinimumIsRequired() => Assert.Equal(WebAppUpdateKind.Required, Plan([Entry("1.2.0")], "1.0.0", minimum: "1.1.0").Kind);

    [Fact]
    public void BelowMinimumWithNothingCompatibleIsNone()
        => Assert.Equal(WebAppUpdateKind.None, Plan([Entry("1.2.0", minimumHost: "5.0")], "1.0.0", minimum: "1.1.0").Kind);

    [Fact]
    public void ChannelsAreOptIn()
    {
        WebAppReleaseEntry[] releases = [Entry("1.0.0"), Entry("1.1.0-beta.1", channel: "beta")];

        Assert.Equal(WebAppUpdateKind.None, Plan(releases, "1.0.0").Kind);
        Assert.Equal("1.1.0-beta.1", Plan(releases, "1.0.0", channel: "BETA").Entry!.Release.Version);
    }

    [Fact]
    public void PlatformsFilter()
    {
        WebAppReleaseEntry[] releases = [Entry("1.0.0"), Entry("1.1.0", platforms: ["android"])];

        Assert.Equal(WebAppUpdateKind.None, Plan(releases, "1.0.0", platform: "ios").Kind);
        Assert.Equal(WebAppUpdateKind.Optional, Plan(releases, "1.0.0", platform: "android").Kind);
    }

    [Fact]
    public void TooOldHostFallsBackToCompatibleRelease()
    {
        var plan = Plan([Entry("1.1.0"), Entry("1.2.0", minimumHost: "2.0")], "1.0.0", host: "1.5");

        Assert.Equal("1.1.0", plan.Entry!.Release.Version);
    }
}
