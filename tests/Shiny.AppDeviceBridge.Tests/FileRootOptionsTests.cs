using Shiny.AppDeviceBridge.Client;
using Shiny.Net.HttpServer;

namespace Shiny.AppDeviceBridge.Tests;

public class FileRootOptionsTests
{
    static readonly string Scratch = Path.Combine(Path.GetTempPath(), "appdevicebridge-tests", "roots");

    [Fact]
    public void DefaultsToDataAndCache()
    {
        var options = new AppDeviceBridgeOptions { AppId = "demo", DataDirectory = Scratch };

        var roots = options.ResolveFileRoots();

        Assert.Equal(["data", "cache"], roots.Select(x => x.Name));
        Assert.Equal(Path.Combine(Scratch, "files"), roots[0].FullPath);
    }

    [Fact]
    public void ConfiguredRootsReplaceTheDefaults()
    {
        var options = new AppDeviceBridgeOptions { AppId = "demo", DataDirectory = Scratch };
        options.FileRoots["media"] = Path.Combine(Scratch, "media") + Path.DirectorySeparatorChar;

        var root = Assert.Single(options.ResolveFileRoots());

        Assert.Equal("media", root.Name);
        Assert.Equal(Path.Combine(Scratch, "media"), root.FullPath);
    }

    [Fact]
    public void Without_the_defaults_an_app_starts_with_no_roots_and_adds_its_own()
    {
        var options = new AppDeviceBridgeOptions { AppId = "demo", DataDirectory = Scratch, DefaultFileRoots = false };

        Assert.Empty(options.ResolveFileRoots());

        var roots = new WebAppFileRoots(options);
        Assert.Empty(roots.All);
        Assert.False(roots.TryGet("data", out _));

        // storage of the app's own, which is not a directory and so could never be configured
        var store = new AppStore("storage");
        roots.Add(store);

        Assert.True(roots.TryGet("storage", out var found));
        Assert.Same(store, found);
        Assert.Same(store, Assert.Single(roots.All));
        Assert.False(roots.IsConfigured("storage"));
        Assert.False(roots.TryResolve("storage", "a.txt", out _));
    }

    [Fact]
    public void Configured_roots_win_whatever_the_defaults_flag_says()
    {
        foreach (var defaults in new[] { true, false })
        {
            var options = new AppDeviceBridgeOptions { AppId = "demo", DataDirectory = Scratch, DefaultFileRoots = defaults };
            options.FileRoots["media"] = Path.Combine(Scratch, "media");

            Assert.Equal("media", Assert.Single(options.ResolveFileRoots()).Name);
        }
    }

    /// <summary>A store with no paths on disk - what an app's own storage looks like to the bridges.</summary>
    sealed class AppStore(string name) : WebAppFileStore(name)
    {
        public override Task<FileEntry?> GetEntryAsync(string path, CancellationToken cancellationToken) => throw new NotSupportedException();
        public override Task<IReadOnlyList<FileEntry>> ListAsync(string path, CancellationToken cancellationToken) => throw new NotSupportedException();
        public override Task<IResult> ReadAsync(string path, string? downloadName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public override Task<FileWriteResult> WriteAsync(string path, Stream content, FileWriteMode mode, long maxBytes, CancellationToken cancellationToken) => throw new NotSupportedException();
        public override Task<FileWriteResult> CreateDirectoryAsync(string path, CancellationToken cancellationToken) => throw new NotSupportedException();
        public override Task DeleteAsync(string path, bool recursive, CancellationToken cancellationToken) => throw new NotSupportedException();
        public override Task<FileEntry> TransferAsync(string from, string to, bool overwrite, bool move, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
