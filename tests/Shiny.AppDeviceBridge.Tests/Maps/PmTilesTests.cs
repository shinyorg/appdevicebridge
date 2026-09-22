using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Shiny.AppDeviceBridge.Maps;

namespace Shiny.AppDeviceBridge.Tests.Maps;

/// <summary>
/// The PMTiles v3 reader against a real archive — a corner of Boulder cut from the Protomaps planet with the pmtiles CLI —
/// and against archives written here to reach leaf directories and runs, which a small real cut never has.
/// </summary>
public class PmTilesTests
{
    internal static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Maps", "Fixtures", name);

    [Theory]
    [InlineData(0, 0, 0, 0UL)]
    [InlineData(1, 0, 0, 1UL)]
    [InlineData(1, 0, 1, 2UL)]
    [InlineData(1, 1, 1, 3UL)]
    [InlineData(1, 1, 0, 4UL)]
    [InlineData(2, 0, 0, 5UL)]
    [InlineData(12, 3423, 1763, 19078479UL)]
    [InlineData(13, 1700, 3100, 35758751UL)]
    [InlineData(20, 123456, 654321, 652812043350UL)]
    // Expected values from pmtiles' own zxyToTileId.
    public void Tile_ids_follow_the_specifications_hilbert_curve(int z, int x, int y, ulong expected)
        => Assert.Equal(expected, PmTilesArchive.ZxyToTileId(z, x, y));

    [Fact]
    public async Task Reads_the_header_of_a_real_archive()
    {
        using var archive = await PmTilesArchive.OpenFileAsync(Fixture("boulder.pmtiles"));

        Assert.Equal(12, archive.Header.MinZoom);
        Assert.Equal(13, archive.Header.MaxZoom);
        Assert.Equal(PmTilesHeader.MvtTileType, archive.Header.TileType);
        Assert.Equal(PmTilesCompression.Gzip, archive.Header.TileCompression);
        Assert.Equal(-105.285, archive.Header.MinLongitude, 5);
        Assert.Equal(40.02, archive.Header.MaxLatitude, 5);
    }

    [Fact]
    public async Task Returns_a_tiles_bytes_exactly_as_the_pmtiles_cli_does()
    {
        using var archive = await PmTilesArchive.OpenFileAsync(Fixture("boulder.pmtiles"));

        var tile = await archive.GetTileAsync(13, 1700, 3100);

        Assert.NotNull(tile);
        Assert.Equal(PmTilesCompression.Gzip, tile.Compression);

        // `pmtiles tile boulder.pmtiles 13 1700 3100 | shasum -a 256`
        Assert.Equal("804ebda264e45d8593e6d74690790c55292d8b90fdb18b2a71f875083641cdf9", Convert.ToHexStringLower(SHA256.HashData(tile.Data)));
    }

    [Fact]
    public async Task A_tile_outside_the_archive_is_null_and_not_covered()
    {
        using var archive = await PmTilesArchive.OpenFileAsync(Fixture("boulder.pmtiles"));

        Assert.True(archive.Covers(13, 1700, 3100));
        Assert.False(archive.Covers(13, 100, 100));
        Assert.False(archive.Covers(10, 212, 387));   // Boulder, but a zoom the archive does not have
        Assert.Null(await archive.GetTileAsync(13, 1700, 3200));
        Assert.Null(await archive.GetTileAsync(13, 9000, 3100));   // off the map at that zoom
    }

    [Fact]
    public async Task Refuses_a_file_that_is_not_a_pmtiles_archive()
    {
        var path = Path.GetTempFileName();
        await File.WriteAllBytesAsync(path, new byte[200]);

        await Assert.ThrowsAsync<InvalidDataException>(() => PmTilesArchive.OpenFileAsync(path));
    }

    [Fact]
    public async Task Follows_leaf_directories_and_runs_of_identical_tiles()
    {
        // Zoom 3 has 64 tiles; the writer puts ids 21..84 in two leaves, with 30..39 one run of the same bytes.
        var tiles = new Dictionary<ulong, byte[]>();
        for (var z = 3; z == 3; z++)
        {
            for (var x = 0; x < 8; x++)
            {
                for (var y = 0; y < 8; y++)
                {
                    var id = PmTilesArchive.ZxyToTileId(z, x, y);
                    tiles[id] = id is >= 30 and < 40 ? "ocean"u8.ToArray() : System.Text.Encoding.UTF8.GetBytes($"tile {z}/{x}/{y}");
                }
            }
        }

        var path = Path.GetTempFileName();
        await File.WriteAllBytesAsync(path, PmTilesWriter.Write(tiles, leafSize: 32));

        using var archive = await PmTilesArchive.OpenFileAsync(path);

        Assert.Equal("tile 3/5/2", System.Text.Encoding.UTF8.GetString((await archive.GetTileAsync(3, 5, 2))!.Data));
        Assert.Equal("tile 3/0/0", System.Text.Encoding.UTF8.GetString((await archive.GetTileAsync(3, 0, 0))!.Data));

        for (var id = 30UL; id < 40; id++)
        {
            var (z, x, y) = Find(id);
            Assert.Equal("ocean", System.Text.Encoding.UTF8.GetString((await archive.GetTileAsync(z, x, y))!.Data));
        }

        Assert.Null(await archive.GetTileAsync(2, 1, 1));

        static (int, int, int) Find(ulong id)
        {
            for (var x = 0; x < 8; x++)
                for (var y = 0; y < 8; y++)
                    if (PmTilesArchive.ZxyToTileId(3, x, y) == id)
                        return (3, x, y);
            throw new InvalidOperationException();
        }
    }

    [Fact]
    public async Task Reads_an_online_archive_with_range_requests()
    {
        var bytes = await File.ReadAllBytesAsync(Fixture("boulder.pmtiles"));
        var handler = new RangeHandler(bytes);
        using var http = new HttpClient(handler);

        using var archive = await PmTilesArchive.OpenAsync(new HttpRangeSource(http, new Uri("https://tiles.example.com/planet.pmtiles"), r => r.Headers.Add("X-Key", "k")));
        var tile = await archive.GetTileAsync(13, 1700, 3100);

        Assert.Equal("804ebda264e45d8593e6d74690790c55292d8b90fdb18b2a71f875083641cdf9", Convert.ToHexStringLower(SHA256.HashData(tile!.Data)));
        Assert.All(handler.Requests, r => Assert.NotNull(r.Headers.Range));
        Assert.All(handler.Requests, r => Assert.Equal("k", r.Headers.GetValues("X-Key").Single()));
    }

    [Fact]
    public async Task An_online_server_that_ignores_range_is_refused()
    {
        var bytes = await File.ReadAllBytesAsync(Fixture("boulder.pmtiles"));
        using var http = new HttpClient(new RangeHandler(bytes) { IgnoreRange = true });

        await Assert.ThrowsAsync<HttpRequestException>(() => PmTilesArchive.OpenAsync(new HttpRangeSource(http, new Uri("https://tiles.example.com/p.pmtiles"), null)));
    }

    /// <summary>Serves a byte array the way a static file server does, Range included.</summary>
    internal sealed class RangeHandler(byte[] bytes) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public bool IgnoreRange { get; init; }
        public bool Offline { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (this.Offline)
                throw new HttpRequestException("offline");

            lock (this.Requests)
                this.Requests.Add(request);

            if (request.Headers.Range?.Ranges.FirstOrDefault() is { } range && !this.IgnoreRange)
            {
                var from = range.From ?? 0;
                var to = Math.Min(range.To ?? bytes.Length - 1, bytes.Length - 1);
                var content = new ByteArrayContent(bytes, (int)from, (int)(to - from + 1));
                content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, bytes.Length);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        }
    }
}

/// <summary>
/// A minimal PMTiles v3 writer: uncompressed directories, a root of leaf pointers when there are more entries than
/// <c>leafSize</c>, and identical consecutive tiles folded into runs. Enough to exercise every path of the reader.
/// </summary>
static class PmTilesWriter
{
    public static byte[] Write(IReadOnlyDictionary<ulong, byte[]> tiles, int leafSize = int.MaxValue, double[]? bounds = null)
    {
        var data = new MemoryStream();
        var entries = new List<(ulong Id, ulong Offset, uint Length, uint Run)>();

        foreach (var (id, bytes) in tiles.OrderBy(x => x.Key))
        {
            if (entries.Count > 0 && entries[^1] is var last && last.Id + last.Run == id
                && tiles[last.Id].AsSpan().SequenceEqual(bytes))
            {
                entries[^1] = last with { Run = last.Run + 1 };
                continue;
            }

            entries.Add((id, (ulong)data.Position, (uint)bytes.Length, 1));
            data.Write(bytes);
        }

        byte[] root;
        var leaves = new MemoryStream();
        if (entries.Count <= leafSize)
        {
            root = Directory(entries);
        }
        else
        {
            var pointers = new List<(ulong, ulong, uint, uint)>();
            foreach (var chunk in entries.Chunk(leafSize))
            {
                var leaf = Directory([.. chunk]);
                pointers.Add((chunk[0].Id, (ulong)leaves.Position, (uint)leaf.Length, 0));
                leaves.Write(leaf);
            }

            root = Directory(pointers);
        }

        const int headerLength = 127;
        var rootOffset = (ulong)headerLength;
        var leavesOffset = rootOffset + (ulong)root.Length;
        var dataOffset = leavesOffset + (ulong)leaves.Length;
        bounds ??= [-180, -85, 180, 85];

        var header = new byte[headerLength];
        "PMTiles"u8.CopyTo(header);
        header[7] = 3;
        void U64(int at, ulong v) => BitConverter.TryWriteBytes(header.AsSpan(at), v);
        void I32(int at, double deg) => BitConverter.TryWriteBytes(header.AsSpan(at), (int)Math.Round(deg * 10_000_000));

        U64(8, rootOffset);
        U64(16, (ulong)root.Length);
        U64(40, leavesOffset);
        U64(48, (ulong)leaves.Length);
        U64(56, dataOffset);
        U64(64, (ulong)data.Length);
        header[97] = 1;   // internal compression: none
        header[98] = 1;   // tile compression: none
        header[99] = 1;   // mvt
        header[100] = (byte)tiles.Keys.Min(TileZoom);
        header[101] = (byte)tiles.Keys.Max(TileZoom);
        I32(102, bounds[0]);
        I32(106, bounds[1]);
        I32(110, bounds[2]);
        I32(114, bounds[3]);

        var output = new MemoryStream();
        output.Write(header);
        output.Write(root);
        output.Write(leaves.ToArray());
        output.Write(data.ToArray());
        return output.ToArray();
    }

    static int TileZoom(ulong id)
    {
        var z = 0;
        ulong acc = 0;
        while (acc + (1UL << (2 * z)) <= id)
        {
            acc += 1UL << (2 * z);
            z++;
        }

        return z;
    }

    static byte[] Directory(List<(ulong Id, ulong Offset, uint Length, uint Run)> entries)
    {
        var s = new MemoryStream();
        Varint(s, (ulong)entries.Count);

        ulong last = 0;
        foreach (var e in entries)
        {
            Varint(s, e.Id - last);
            last = e.Id;
        }

        foreach (var e in entries)
            Varint(s, e.Run);
        foreach (var e in entries)
            Varint(s, e.Length);
        foreach (var e in entries)
            Varint(s, e.Offset + 1);   // never the "follows the previous" zero, so every offset is explicit

        return s.ToArray();
    }

    static void Varint(Stream s, ulong v)
    {
        while (v >= 0x80)
        {
            s.WriteByte((byte)(v | 0x80));
            v >>= 7;
        }

        s.WriteByte((byte)v);
    }
}
