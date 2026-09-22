using System.Buffers.Binary;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using Microsoft.Win32.SafeHandles;

namespace Shiny.AppDeviceBridge.Maps;

/// <summary>How a PMTiles archive compresses its tiles or directories. The numbers are the specification's.</summary>
enum PmTilesCompression : byte
{
    Unknown = 0,
    None = 1,
    Gzip = 2,
    Brotli = 3,
    Zstd = 4
}

/// <summary>
/// The fixed 127-byte header of a PMTiles v3 archive.
/// See https://github.com/protomaps/PMTiles/blob/main/spec/v3/spec.md.
/// </summary>
sealed record PmTilesHeader(
    ulong RootDirectoryOffset,
    ulong RootDirectoryLength,
    ulong LeafDirectoriesOffset,
    ulong TileDataOffset,
    PmTilesCompression InternalCompression,
    PmTilesCompression TileCompression,
    byte TileType,
    byte MinZoom,
    byte MaxZoom,
    double MinLongitude,
    double MinLatitude,
    double MaxLongitude,
    double MaxLatitude
)
{
    public const int Length = 127;

    /// <summary>Vector tiles (MVT) — the only kind a vector map can draw.</summary>
    public const byte MvtTileType = 1;

    public static PmTilesHeader Parse(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < Length || !bytes[..7].SequenceEqual("PMTiles"u8))
            throw new InvalidDataException("Not a PMTiles archive.");

        if (bytes[7] != 3)
            throw new InvalidDataException($"PMTiles version {bytes[7]} is not supported; version 3 is.");

        static double Degrees(ReadOnlySpan<byte> b, int at) => BinaryPrimitives.ReadInt32LittleEndian(b[at..]) / 10_000_000d;

        return new PmTilesHeader(
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[16..]),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[40..]),
            BinaryPrimitives.ReadUInt64LittleEndian(bytes[56..]),
            (PmTilesCompression)bytes[97],
            (PmTilesCompression)bytes[98],
            bytes[99],
            bytes[100],
            bytes[101],
            Degrees(bytes, 102),
            Degrees(bytes, 106),
            Degrees(bytes, 110),
            Degrees(bytes, 114)
        );
    }
}

/// <summary>One directory entry: a run of tiles, or — with a zero run length — a pointer to a leaf directory.</summary>
readonly record struct PmTilesEntry(ulong TileId, ulong Offset, uint Length, uint RunLength);

/// <summary>A tile's bytes as the archive stores them, and how they are compressed.</summary>
sealed record MapTile(byte[] Data, PmTilesCompression Compression);

/// <summary>Reads byte ranges of an archive: a file on the device, or a file on a server that answers Range requests.</summary>
interface IRangeSource : IDisposable
{
    Task<byte[]> ReadAsync(ulong offset, int length, CancellationToken cancellationToken);
}

sealed class FileRangeSource(string path) : IRangeSource
{
    readonly SafeFileHandle handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, FileOptions.RandomAccess);

    public async Task<byte[]> ReadAsync(ulong offset, int length, CancellationToken cancellationToken)
    {
        var buffer = new byte[length];
        var read = 0;

        while (read < length)
        {
            var n = await RandomAccess.ReadAsync(this.handle, buffer.AsMemory(read), (long)offset + read, cancellationToken).ConfigureAwait(false);
            if (n == 0)
                throw new InvalidDataException("The archive ended early.");

            read += n;
        }

        return buffer;
    }

    public void Dispose() => this.handle.Dispose();
}

sealed class HttpRangeSource(HttpClient http, Uri url, Action<HttpRequestMessage>? configure) : IRangeSource
{
    public async Task<byte[]> ReadAsync(ulong offset, int length, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue((long)offset, (long)offset + length - 1);
        configure?.Invoke(request);

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        // A server that ignores Range sends the whole file with 200; reading all of a planet to get one tile is never what anyone wants.
        if (response.StatusCode != HttpStatusCode.PartialContent)
            throw new HttpRequestException($"{url.Host} answered a range request with {(int)response.StatusCode}; the online archive needs a server that supports Range.", null, response.StatusCode);

        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        if (bytes.Length != length)
            throw new InvalidDataException("The server sent a different number of bytes than asked for.");

        return bytes;
    }

    public void Dispose() { }
}

/// <summary>
/// A PMTiles v3 archive: finds a tile's bytes by walking the root directory and at most three levels of leaf
/// directories. Directories are cached, so panning around one area reads each once.
/// </summary>
sealed class PmTilesArchive : IDisposable
{
    const int MaxDepth = 4;
    const int CachedDirectories = 64;

    readonly IRangeSource source;
    readonly Dictionary<ulong, (PmTilesEntry[] Entries, long Used)> directories = [];
    readonly Lock gate = new();
    long clock;

    PmTilesArchive(IRangeSource source, PmTilesHeader header)
    {
        this.source = source;
        this.Header = header;
    }

    public PmTilesHeader Header { get; }

    public static async Task<PmTilesArchive> OpenAsync(IRangeSource source, CancellationToken cancellationToken = default)
    {
        try
        {
            // The specification keeps the root directory inside the first 16 KiB, so one read usually brings both.
            var header = PmTilesHeader.Parse(await source.ReadAsync(0, PmTilesHeader.Length, cancellationToken).ConfigureAwait(false));

            if (header.InternalCompression is not (PmTilesCompression.None or PmTilesCompression.Gzip or PmTilesCompression.Brotli))
                throw new InvalidDataException($"PMTiles directories compressed with {header.InternalCompression} are not supported.");

            return new PmTilesArchive(source, header);
        }
        catch
        {
            source.Dispose();
            throw;
        }
    }

    public static Task<PmTilesArchive> OpenFileAsync(string path, CancellationToken cancellationToken = default)
        => OpenAsync(new FileRangeSource(path), cancellationToken);

    /// <summary>Whether the archive's bounds and zooms could hold the tile. A cheap test before any directory is read.</summary>
    public bool Covers(int z, int x, int y)
    {
        if (z < this.Header.MinZoom || z > this.Header.MaxZoom)
            return false;

        var (west, south, east, north) = TileBounds(z, x, y);
        return east > this.Header.MinLongitude
               && west < this.Header.MaxLongitude
               && north > this.Header.MinLatitude
               && south < this.Header.MaxLatitude;
    }

    /// <summary>The tile's bytes, or null when the archive does not have it.</summary>
    public async Task<MapTile?> GetTileAsync(int z, int x, int y, CancellationToken cancellationToken = default)
    {
        if (!IsValidTile(z, x, y))
            return null;

        var tileId = ZxyToTileId(z, x, y);
        var offset = this.Header.RootDirectoryOffset;
        var length = this.Header.RootDirectoryLength;

        for (var depth = 0; depth < MaxDepth; depth++)
        {
            var entries = await this.GetDirectoryAsync(offset, length, cancellationToken).ConfigureAwait(false);

            if (FindTile(entries, tileId) is not { } entry)
                return null;

            if (entry.RunLength > 0)
            {
                var data = await this.source.ReadAsync(this.Header.TileDataOffset + entry.Offset, (int)entry.Length, cancellationToken).ConfigureAwait(false);
                return new MapTile(data, this.Header.TileCompression);
            }

            offset = this.Header.LeafDirectoriesOffset + entry.Offset;
            length = entry.Length;
        }

        return null;
    }

    async Task<PmTilesEntry[]> GetDirectoryAsync(ulong offset, ulong length, CancellationToken cancellationToken)
    {
        lock (this.gate)
        {
            if (this.directories.TryGetValue(offset, out var cached))
            {
                this.directories[offset] = (cached.Entries, ++this.clock);
                return cached.Entries;
            }
        }

        var bytes = await this.source.ReadAsync(offset, checked((int)length), cancellationToken).ConfigureAwait(false);
        var entries = ParseDirectory(Decompress(bytes, this.Header.InternalCompression));

        lock (this.gate)
        {
            if (this.directories.Count >= CachedDirectories)
                this.directories.Remove(this.directories.MinBy(x => x.Value.Used).Key);

            this.directories[offset] = (entries, ++this.clock);
        }

        return entries;
    }

    public void Dispose() => this.source.Dispose();

    internal static bool IsValidTile(int z, int x, int y)
        => z is >= 0 and <= 26 && x >= 0 && y >= 0 && x < 1 << z && y < 1 << z;

    /// <summary>The tile's position on the Hilbert curve of its zoom, after every tile of the zooms above it.</summary>
    internal static ulong ZxyToTileId(int z, int x, int y)
    {
        ulong acc = ((1UL << (2 * z)) - 1) / 3;
        ulong n = 1UL << z;
        ulong d = 0;
        ulong tx = (ulong)x, ty = (ulong)y;

        for (var s = n / 2; s > 0; s /= 2)
        {
            ulong rx = (tx & s) > 0 ? 1UL : 0UL;
            ulong ry = (ty & s) > 0 ? 1UL : 0UL;
            d += s * s * ((3 * rx) ^ ry);

            // Rotate the quadrant so the curve stays continuous.
            if (ry == 0)
            {
                if (rx == 1)
                {
                    tx = s - 1 - tx;
                    ty = s - 1 - ty;
                }

                (tx, ty) = (ty, tx);
            }
        }

        return acc + d;
    }

    /// <summary>The entry whose run covers the tile, or the leaf directory that holds it.</summary>
    internal static PmTilesEntry? FindTile(PmTilesEntry[] entries, ulong tileId)
    {
        int lo = 0, hi = entries.Length - 1;

        while (lo <= hi)
        {
            var mid = (lo + hi) >>> 1;
            var cmp = tileId.CompareTo(entries[mid].TileId);

            if (cmp > 0)
                lo = mid + 1;
            else if (cmp < 0)
                hi = mid - 1;
            else
                return entries[mid];
        }

        // Not an exact match: the entry before is the only one that can hold it.
        if (hi >= 0)
        {
            var entry = entries[hi];
            if (entry.RunLength == 0)
                return entry;

            if (tileId - entry.TileId < entry.RunLength)
                return entry;
        }

        return null;
    }

    internal static PmTilesEntry[] ParseDirectory(ReadOnlySpan<byte> bytes)
    {
        var at = 0;
        var count = checked((int)ReadVarint(bytes, ref at));
        var entries = new PmTilesEntry[count];

        ulong lastId = 0;
        for (var i = 0; i < count; i++)
        {
            lastId += ReadVarint(bytes, ref at);
            entries[i] = entries[i] with { TileId = lastId };
        }

        for (var i = 0; i < count; i++)
            entries[i] = entries[i] with { RunLength = checked((uint)ReadVarint(bytes, ref at)) };

        for (var i = 0; i < count; i++)
            entries[i] = entries[i] with { Length = checked((uint)ReadVarint(bytes, ref at)) };

        for (var i = 0; i < count; i++)
        {
            var value = ReadVarint(bytes, ref at);

            // Zero means "straight after the previous entry", which is how clustered archives stay small.
            entries[i] = entries[i] with
            {
                Offset = value == 0 && i > 0 ? entries[i - 1].Offset + entries[i - 1].Length : value - 1
            };
        }

        return entries;
    }

    static ulong ReadVarint(ReadOnlySpan<byte> bytes, ref int at)
    {
        ulong value = 0;

        for (var shift = 0; shift < 64; shift += 7)
        {
            if (at >= bytes.Length)
                throw new InvalidDataException("A PMTiles directory ended inside a number.");

            var b = bytes[at++];
            value |= (ulong)(b & 0x7F) << shift;

            if ((b & 0x80) == 0)
                return value;
        }

        throw new InvalidDataException("A PMTiles directory has a number too large to read.");
    }

    internal static byte[] Decompress(byte[] bytes, PmTilesCompression compression)
    {
        if (compression is PmTilesCompression.None or PmTilesCompression.Unknown)
            return bytes;

        using var input = new MemoryStream(bytes);
        using Stream decoder = compression switch
        {
            PmTilesCompression.Gzip => new GZipStream(input, CompressionMode.Decompress),
            PmTilesCompression.Brotli => new BrotliStream(input, CompressionMode.Decompress),
            _ => throw new InvalidDataException($"{compression} is not supported.")
        };
        using var output = new MemoryStream();
        decoder.CopyTo(output);
        return output.ToArray();
    }

    /// <summary>A tile's bounds in degrees: west, south, east, north.</summary>
    internal static (double West, double South, double East, double North) TileBounds(int z, int x, int y)
    {
        var n = (double)(1 << z);

        static double Latitude(double yTile, double n) => Math.Atan(Math.Sinh(Math.PI * (1 - 2 * yTile / n))) * 180 / Math.PI;

        return (x / n * 360 - 180, Latitude(y + 1, n), (x + 1) / n * 360 - 180, Latitude(y, n));
    }
}
