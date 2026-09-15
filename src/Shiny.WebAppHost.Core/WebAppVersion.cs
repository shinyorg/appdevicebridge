using System.Globalization;

namespace Shiny.WebAppHost;

/// <summary>
/// A release version: up to four numeric components, an optional prerelease label, and build
/// metadata that is ignored.
/// <para>
/// Not <see cref="Version"/>, because web builds are versioned the way packages are —
/// <c>2.1.0-beta.4</c> — and <see cref="Version"/> refuses the label. Not a full SemVer library
/// either: the only question ever asked of a version here is "is this one newer", and prerelease
/// ordering is the only part of SemVer that answer depends on.
/// </para>
/// </summary>
public readonly struct WebAppVersion : IComparable<WebAppVersion>, IEquatable<WebAppVersion>
{
    readonly int[]? numbers;
    readonly string[]? prerelease;
    readonly string? original;

    WebAppVersion(string original, int[] numbers, string[] prerelease)
    {
        this.original = original;
        this.numbers = numbers;
        this.prerelease = prerelease;
    }

    public bool IsPrerelease => this.prerelease is { Length: > 0 };

    public static WebAppVersion Parse(string value)
        => TryParse(value, out var version)
            ? version
            : throw new FormatException($"'{value}' is not a version. Expected something like 1.2.3 or 1.2.3-beta.1.");

    public static bool TryParse(string? value, out WebAppVersion version)
    {
        version = default;

        if (String.IsNullOrWhiteSpace(value))
            return false;

        var text = value.Trim();

        // Build metadata never takes part in ordering: 1.0.0+abc and 1.0.0+def are the same release.
        var plus = text.IndexOf('+');
        if (plus >= 0)
            text = text[..plus];

        string[] label = [];
        var dash = text.IndexOf('-');
        if (dash >= 0)
        {
            label = text[(dash + 1)..].Split('.');
            text = text[..dash];

            if (label.Any(x => x.Length == 0))
                return false;
        }

        var parts = text.Split('.');
        if (parts.Length is < 1 or > 4)
            return false;

        var parsed = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!Int32.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out parsed[i]))
                return false;
        }

        version = new WebAppVersion(value.Trim(), parsed, label);
        return true;
    }

    public int CompareTo(WebAppVersion other)
    {
        var a = this.numbers ?? [];
        var b = other.numbers ?? [];

        // Missing components are zero, so 1.2 and 1.2.0 are the same release.
        for (var i = 0; i < Math.Max(a.Length, b.Length); i++)
        {
            var result = (i < a.Length ? a[i] : 0).CompareTo(i < b.Length ? b[i] : 0);
            if (result != 0)
                return result;
        }

        var pa = this.prerelease ?? [];
        var pb = other.prerelease ?? [];

        // A release outranks every prerelease of the same numbers.
        if (pa.Length == 0 || pb.Length == 0)
            return pb.Length.CompareTo(pa.Length);

        for (var i = 0; i < Math.Min(pa.Length, pb.Length); i++)
        {
            var result = CompareIdentifier(pa[i], pb[i]);
            if (result != 0)
                return result;
        }

        return pa.Length.CompareTo(pb.Length);
    }

    /// <summary>SemVer §11: numeric identifiers compare numerically and sort below alphanumeric ones.</summary>
    static int CompareIdentifier(string a, string b)
    {
        var aNumeric = Int64.TryParse(a, NumberStyles.None, CultureInfo.InvariantCulture, out var an);
        var bNumeric = Int64.TryParse(b, NumberStyles.None, CultureInfo.InvariantCulture, out var bn);

        return (aNumeric, bNumeric) switch
        {
            (true, true) => an.CompareTo(bn),
            (true, false) => -1,
            (false, true) => 1,
            _ => String.CompareOrdinal(a, b)
        };
    }

    public bool Equals(WebAppVersion other) => this.CompareTo(other) == 0;
    public override bool Equals(object? obj) => obj is WebAppVersion other && this.Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        var values = this.numbers ?? [];

        // Trailing zeros are dropped so that versions CompareTo calls equal also hash equal.
        var length = values.Length;
        while (length > 0 && values[length - 1] == 0)
            length--;

        for (var i = 0; i < length; i++)
            hash.Add(values[i]);

        foreach (var identifier in this.prerelease ?? [])
            hash.Add(identifier, StringComparer.Ordinal);

        return hash.ToHashCode();
    }

    public override string ToString() => this.original ?? "0";

    public static bool operator ==(WebAppVersion left, WebAppVersion right) => left.Equals(right);
    public static bool operator !=(WebAppVersion left, WebAppVersion right) => !left.Equals(right);
    public static bool operator <(WebAppVersion left, WebAppVersion right) => left.CompareTo(right) < 0;
    public static bool operator >(WebAppVersion left, WebAppVersion right) => left.CompareTo(right) > 0;
    public static bool operator <=(WebAppVersion left, WebAppVersion right) => left.CompareTo(right) <= 0;
    public static bool operator >=(WebAppVersion left, WebAppVersion right) => left.CompareTo(right) >= 0;
}
