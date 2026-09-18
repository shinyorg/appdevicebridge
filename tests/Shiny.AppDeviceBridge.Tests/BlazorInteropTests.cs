using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime.Interop;
using Microsoft.JSInterop;
using Shiny.AppDeviceBridge.Blazor;

namespace Shiny.AppDeviceBridge.Tests;

/// <summary>
/// WebAppEvents against the real appdevicebridge.js, run under Jint behind an IJSRuntime that marshals the way Blazor
/// does. That last part is the point: Blazor turns a DotNetObjectReference into a <em>new</em> JavaScript object on
/// every interop call, so a script that tells .NET listeners apart by object identity can never find one again.
/// </summary>
public class BlazorInteropTests
{
    [Fact]
    public async Task A_page_visited_again_does_not_receive_each_event_again()
    {
        var js = new JintJSRuntime();
        await using var events = new WebAppEvents(js);
        var received = 0;

        // A page that listens, visited three times, left each time before the next.
        for (var visit = 0; visit < 3; visit++)
        {
            await using var _ = await events.OnAsync("tray.click", _ => received++);
        }

        // The fourth visit stays.
        await using (await events.OnAsync("tray.click", _ => received++))
        {
            js.Emit("tray.click", "{}");

            Assert.Equal(1, received);
            Assert.Equal(1, js.DotNetCalls);
        }
    }

    [Fact]
    public async Task Nothing_reaches_net_once_the_last_subscription_is_gone()
    {
        var js = new JintJSRuntime();
        await using var events = new WebAppEvents(js);

        await (await events.OnAsync("wifi.changed", _ => { })).DisposeAsync();

        js.Emit("wifi.changed", "{}");
        Assert.Equal(0, js.DotNetCalls);
    }

    [Fact]
    public async Task Disposing_the_client_releases_every_listener()
    {
        var js = new JintJSRuntime();
        var events = new WebAppEvents(js);

        await events.OnAsync("gps.reading", _ => { });
        await events.OnAsync("wifi.changed", _ => { });
        await events.DisposeAsync();

        js.Emit("gps.reading", "{}");
        js.Emit("wifi.changed", "{}");
        Assert.Equal(0, js.DotNetCalls);
    }

    [Fact]
    public async Task Two_subscriptions_to_one_event_each_get_it_once()
    {
        var js = new JintJSRuntime();
        await using var events = new WebAppEvents(js);
        var first = 0;
        var second = 0;

        await using var a = await events.OnAsync("tray.menu", _ => first++);
        await using var b = await events.OnAsync("tray.menu", _ => second++);

        js.Emit("tray.menu", "{}");

        Assert.Equal((1, 1), (first, second));
        Assert.Equal(1, js.DotNetCalls);
    }

    [Fact]
    public async Task The_stream_carries_exactly_the_events_something_listens_to()
    {
        var js = new JintJSRuntime();
        await using var events = new WebAppEvents(js);

        var gps = await events.OnAsync("gps.reading", _ => { });
        Assert.Equal("/_bridge/events?topics=gps.reading", js.Opened);
        Assert.Equal("gps.reading", js.Topics);

        await using var wifi = await events.OnAsync("wifi.changed", _ => { });
        Assert.Equal("gps.reading,wifi.changed", js.Topics);

        // A second listener for an event already carried changes nothing; the last one leaving drops it.
        var again = await events.OnAsync("gps.reading", _ => { });
        await gps.DisposeAsync();
        Assert.Equal("gps.reading,wifi.changed", js.Topics);

        await again.DisposeAsync();
        Assert.Equal("wifi.changed", js.Topics);
    }

    /// <summary>appdevicebridge.js under Jint, with just enough browser to load: fetch, URL, document and EventSource.</summary>
    sealed class JintJSRuntime : IJSRuntime
    {
        const string Browser = """
            globalThis.document = { baseURI: "http://127.0.0.1:5780/" };
            globalThis.URL = class { constructor(path, base) { this.href = base + path; } toString() { return this.href; } };
            // Never fires: a subscription settles through the stream's open event and the PUT that follows it.
            globalThis.setTimeout = () => 0;

            // The topics the host was last told the stream carries, and the URL it was opened with.
            globalThis.__topics = null;
            globalThis.__opened = null;
            globalThis.fetch = (url, init) => {
                if (init?.method === "PUT") {
                    globalThis.__topics = JSON.parse(init.body).topics.join(",");
                    return Promise.resolve({ ok: true, status: 204 });
                }

                return Promise.resolve({ ok: true, json: () => Promise.resolve({ base: "/", bridge: "/_bridge/" }) });
            };

            const sources = [];
            globalThis.EventSource = class {
                constructor(url) {
                    this.url = url;
                    this.listeners = {};
                    sources.push(this);
                    globalThis.__opened = url;

                    // As the host does: the stream's id first, once the page has had a chance to listen for it.
                    Promise.resolve().then(() => __emit("bridge.stream", JSON.stringify({ id: "s1" })));
                }
                addEventListener(name, listener) { (this.listeners[name] ??= []).push(listener); }
            };
            globalThis.__emit = (name, data) => {
                for (const source of sources)
                    for (const listener of source.listeners[name] ?? [])
                        listener({ data });
            };
            """;

        readonly Engine engine = new();
        readonly ObjectInstance module;

        public JintJSRuntime()
        {
            this.engine.Execute(Browser);
            this.engine.Modules.Add("appdevicebridge", File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "appdevicebridge.js")));
            this.module = this.engine.Modules.Import("appdevicebridge");
        }

        /// <summary>The topics the script last told the host, comma separated; null before it told it any.</summary>
        public string? Topics => this.engine.GetValue("__topics").IsNull() ? null : this.engine.GetValue("__topics").AsString();

        /// <summary>The URL the script opened its event stream with.</summary>
        public string? Opened => this.engine.GetValue("__opened").IsNull() ? null : this.engine.GetValue("__opened").AsString();

        /// <summary>How many times the script called into .NET — once per listener it believes it has.</summary>
        public int DotNetCalls { get; private set; }

        public void Emit(string name, string data)
            => this.engine.Invoke(this.engine.GetValue("__emit"), name, data);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            => identifier == "import"
                ? ValueTask.FromResult((TValue)(object)new Module(this))
                : throw new NotSupportedException(identifier);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
            => this.InvokeAsync<TValue>(identifier, args);

        JsValue Call(string function, object?[]? args)
        {
            var arguments = (args ?? []).Select(this.ToJs).ToArray();
            return this.engine.Invoke(this.module.Get(function), arguments).UnwrapIfPromise();
        }

        /// <summary>Marshals as Blazor does — including a fresh object for a DotNetObjectReference on every call.</summary>
        JsValue ToJs(object? value)
        {
            if (value is DotNetObjectReference<WebAppEvents> reference)
            {
                var wrapper = new JsObject(this.engine);
                wrapper.Set("invokeMethodAsync", new ClrFunction(this.engine, "invokeMethodAsync", (_, a) =>
                {
                    this.DotNetCalls++;

                    if (a[0].AsString() == nameof(WebAppEvents.OnEvent))
                        reference.Value.OnEvent(a[1].AsString(), a[2].AsString()).GetAwaiter().GetResult();

                    return JsValue.Undefined;
                }));

                return wrapper;
            }

            return JsValue.FromObject(this.engine, value);
        }

        sealed class Module(JintJSRuntime runtime) : IJSObjectReference
        {
            public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
            {
                runtime.Call(identifier, args);
                return ValueTask.FromResult(default(TValue)!);
            }

            public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
                => this.InvokeAsync<TValue>(identifier, args);

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }
}
