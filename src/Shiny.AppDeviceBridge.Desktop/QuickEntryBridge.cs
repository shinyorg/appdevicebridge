using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Shiny.AppDeviceBridge.Client;
using Shiny.AppDeviceBridge.Desktop.Client;
using Shiny.Maui.Controls.Desktop.QuickEntry;
using Shiny.Maui.Controls.QuickEntry;
using Shiny.Net.HttpServer;
using Contracts = Shiny.AppDeviceBridge.Desktop.Client;
using ControlEntry = Shiny.Maui.Controls.QuickEntry;

namespace Shiny.AppDeviceBridge.Desktop;

/// <summary>Limits on what the page may ask quick entry for, and the hotkey it starts with.</summary>
public sealed class QuickEntryBridgeOptions
{
    /// <summary>
    /// The global shortcut that toggles the window from launch — <c>Cmd+Opt+Space</c>, <c>Ctrl+Alt+Space</c>. The page can
    /// change it with <c>PUT /_bridge/quickentry/options</c>. Null registers none until the page asks.
    /// </summary>
    public string? HotKey { get; set; }

    /// <summary>How many suggestions the page may show at once.</summary>
    public int MaxSuggestions { get; set; } = 50;

    /// <summary>The longest text the page may put in the entry, placeholder or response.</summary>
    public int MaxTextLength { get; set; } = 64 * 1024;
}

public static class QuickEntryBridgeExtensions
{
    /// <summary>
    /// Adds <c>/_bridge/quickentry</c>: Shiny's quick entry prompt as a window that opens over other applications, driven by
    /// the page.
    /// <code>
    /// builder.AddQuickEntryBridge(
    ///     o => o.HotKey = OperatingSystem.IsMacOS() ? "Cmd+Opt+Space" : "Ctrl+Alt+Space",
    ///     quickEntry => quickEntry.ScreenGlow = ScreenGlowTrigger.WhileBusy
    /// );
    /// </code>
    /// <para>
    /// Registers Shiny.Maui.Controls and its desktop quick entry when the app has not, with the window presented on the
    /// desktop rather than inside the app. Desktop only: macOS (AppKit and Catalyst), Windows and Linux. Everywhere else
    /// <c>GET /_bridge/quickentry</c> answers <c>{ "supported": false }</c> and the rest answer 501.
    /// </para>
    /// </summary>
    /// <param name="configure">The bridge's own options: the starting hotkey and the page's limits.</param>
    /// <param name="quickEntry">The quick entry control's options — size, placement, dismissal, glow — as the app starts.</param>
    public static MauiAppBuilder AddQuickEntryBridge(
        this MauiAppBuilder builder,
        Action<QuickEntryBridgeOptions>? configure = null,
        Action<QuickEntryOptions>? quickEntry = null
    )
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new QuickEntryBridgeOptions();
        configure?.Invoke(options);

        if (!builder.Services.Any(x => x.ServiceType == typeof(IQuickEntryService)))
            builder.UseShinyControls();

        if (!builder.Services.Any(x => x.ServiceType == typeof(IGlobalHotKeyService)))
            builder.UseDesktopQuickEntry();

        if (builder.Services.FirstOrDefault(x => x.ServiceType == typeof(QuickEntryOptions))?.ImplementationInstance is QuickEntryOptions controlOptions)
        {
            controlOptions.Presentation = ControlEntry.QuickEntryPresentation.Desktop;
            quickEntry?.Invoke(controlOptions);

            // The bridge owns the hotkey, so the page can change it; left on the control's options it would be registered a
            // second time, by a registration nothing could take back.
            if (!String.IsNullOrWhiteSpace(controlOptions.HotKey))
                options.HotKey ??= controlOptions.HotKey;

            controlOptions.HotKey = null;
        }

        builder.Services.TryAddSingleton(options);
        builder.Services.TryAddSingleton<QuickEntryBridge>();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<IWebAppBridge, QuickEntryBridge>(sp => sp.GetRequiredService<QuickEntryBridge>()));
        builder.Services.AddSingleton<IMauiInitializeService, QuickEntryBridgeStartup>();
        return builder;
    }
}

/// <summary>
/// <c>/_bridge/quickentry</c> over <see cref="IQuickEntryService"/> and its <see cref="PromptView"/>.
/// <code>
/// GET  /_bridge/quickentry                 { "supported": true, "presentation": "Desktop", "isOpen": false, … }
/// PUT  /_bridge/quickentry/options         { "hotKey": "Cmd+Opt+Space", "placement": "TopCenter", "glow": "WhileBusy" }
/// POST /_bridge/quickentry/show | hide | toggle
/// GET  /_bridge/quickentry/prompt
/// PUT  /_bridge/quickentry/prompt          { "placeholder": "Ask…", "suggestions": [ … ], "isBusy": true, "response": "…" }
/// POST /_bridge/quickentry/prompt/reset
/// POST /_bridge/quickentry/glow/show | hide
/// POST /_bridge/quickentry/glow/pulse      { "durationMs": 1500 }
///
/// events: quickentry.submitted, quickentry.suggestion, quickentry.cancelled, quickentry.microphone,
///         quickentry.opened, quickentry.closed
/// </code>
/// <para>
/// The window is summoned from a hotkey with the app in the background — often with its own window closed — so what the
/// user does in it goes out as events <em>and</em> as calls the web app handles in the page when it is open and in
/// <c>background.js</c> when it is not. A handler answers by setting the prompt's response.
/// </para>
/// </summary>
public sealed class QuickEntryBridge : IWebAppBridge, IDisposable
{
    readonly IQuickEntryService? service;
    readonly IGlobalHotKeyService? hotKeys;
    readonly QuickEntryBridgeOptions options;
    readonly WebAppInvoker? invoker;
    readonly ILogger? logger;
    readonly WebAppEventSource<QuickEntryStatus> opened = new();
    readonly WebAppEventSource<QuickEntryStatus> closed = new();
    readonly WebAppEventSource<QuickEntrySubmission> submitted = new();
    readonly WebAppEventSource<QuickEntrySubmission> suggestions = new();
    readonly WebAppEventSource<QuickEntryPromptEvent> cancelled = new();
    readonly WebAppEventSource<QuickEntryPromptEvent> microphone = new();
    readonly SemaphoreSlim gate = new(1, 1);

    // What the page asked the prompt to be, re-applied to a prompt the control builds afresh on every open.
    PromptModel model = new();
    PromptView? wired;
    IDisposable? hotKeyRegistration;
    string? hotKey;
    bool started;
    bool disposed;

    public QuickEntryBridge(IServiceProvider services)
    {
        this.service = services.GetOptionalService<IQuickEntryService>();
        this.hotKeys = services.GetOptionalService<IGlobalHotKeyService>();
        this.options = services.GetOptionalService<QuickEntryBridgeOptions>() ?? new QuickEntryBridgeOptions();
        this.invoker = services.GetOptionalService<WebAppInvoker>();
        this.logger = services.GetOptionalService<ILogger<QuickEntryBridge>>();
        this.hotKey = this.options.HotKey;
    }

    const string OpenedEvent = "quickentry.opened";
    const string ClosedEvent = "quickentry.closed";
    const string SubmittedEvent = "quickentry.submitted";
    const string SuggestionEvent = "quickentry.suggestion";
    const string CancelledEvent = "quickentry.cancelled";
    const string MicrophoneEvent = "quickentry.microphone";

    public string Name => "quickentry";

    public bool IsSupported => this.service is not null && PlatformSupported;

    static bool PlatformSupported
        => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() || OperatingSystem.IsMacCatalyst() || OperatingSystem.IsLinux();

    public void Map(WebAppBridgeRoutes routes) => routes
        .MapEvent(OpenedEvent, ct => this.opened.ListenAsync(ct), QuickEntryJsonContext.Default.QuickEntryStatus)
        .MapEvent(ClosedEvent, ct => this.closed.ListenAsync(ct), QuickEntryJsonContext.Default.QuickEntryStatus)
        .MapEvent(SubmittedEvent, ct => this.submitted.ListenAsync(ct), QuickEntryJsonContext.Default.QuickEntrySubmission)
        .MapEvent(SuggestionEvent, ct => this.suggestions.ListenAsync(ct), QuickEntryJsonContext.Default.QuickEntrySubmission)
        .MapEvent(CancelledEvent, ct => this.cancelled.ListenAsync(ct), QuickEntryJsonContext.Default.QuickEntryPromptEvent)
        .MapEvent(MicrophoneEvent, ct => this.microphone.ListenAsync(ct), QuickEntryJsonContext.Default.QuickEntryPromptEvent)
        .MapGet("", this.StatusAsync)
        .MapPut("/options", this.ConfigureAsync)
        .MapPost("/show", ctx => this.VisibilityAsync(ctx, x => x.Show()))
        .MapPost("/hide", ctx => this.VisibilityAsync(ctx, x => x.Hide()))
        .MapPost("/toggle", ctx => this.VisibilityAsync(ctx, x => x.Toggle()))
        .MapGet("/prompt", this.GetPromptAsync)
        .MapPut("/prompt", this.SetPromptAsync)
        .MapPost("/prompt/reset", this.ResetPromptAsync)
        .MapPost("/glow/show", ctx => this.GlowAsync(ctx, x => x.ShowGlow()))
        .MapPost("/glow/hide", ctx => this.GlowAsync(ctx, x => x.HideGlow()))
        .MapPost("/glow/pulse", this.PulseAsync);

    /// <summary>Registers the starting hotkey and listens for the window opening. Called once the app starts.</summary>
    internal async Task StartAsync()
    {
        if (!this.IsSupported)
            return;

        await this.gate.WaitAsync();
        try
        {
            if (this.started || this.disposed)
                return;

            this.started = true;
            this.service!.Opened += this.OnOpened;
            this.service.Closed += this.OnClosed;

            await MainAsync(() => this.RegisterHotKey(this.hotKey));
        }
        finally
        {
            this.gate.Release();
        }
    }

    async ValueTask StatusAsync(HttpContext context)
    {
        if (!this.IsSupported)
        {
            await WebAppBridgeResults.Json(
                context,
                new QuickEntryStatus(false, Contracts.QuickEntryPresentation.InApp, false, false, false, null, false, false),
                QuickEntryJsonContext.Default.QuickEntryStatus
            );
            return;
        }

        await this.StartAsync();
        await this.RespondStatusAsync(context);
    }

    async ValueTask ConfigureAsync(HttpContext context)
    {
        if (!await this.EnsureSupportedAsync(context))
            return;

        var body = await WebAppBridgeResults.ReadBodyAsync(context, QuickEntryJsonContext.Default.QuickEntryOptionsInput);
        if (body is null)
        {
            await WebAppBridgeResults.BadRequest(context, "Expected the options to change.");
            return;
        }

        if (Validate(body) is { } problem)
        {
            await WebAppBridgeResults.BadRequest(context, problem);
            return;
        }

        await this.gate.WaitAsync(context.RequestAborted);
        try
        {
            await MainAsync(() =>
            {
                var o = this.service!.Options;

                if (body.Presentation is { } presentation)
                    o.Presentation = BridgeEnum.Convert<Contracts.QuickEntryPresentation, ControlEntry.QuickEntryPresentation>(presentation);

                if (body.Placement is { } placement)
                    o.Placement = BridgeEnum.Convert<Contracts.QuickEntryPlacement, ControlEntry.QuickEntryPlacement>(placement);

                if (body.Glow is { } glow)
                    o.ScreenGlow = BridgeEnum.Convert<QuickEntryGlowTrigger, ScreenGlowTrigger>(glow);

                o.Width = body.Width ?? o.Width;
                o.CollapsedHeight = body.CollapsedHeight ?? o.CollapsedHeight;
                o.MaxHeight = body.MaxHeight ?? o.MaxHeight;
                o.TopMarginRatio = body.TopMarginRatio ?? o.TopMarginRatio;
                o.BottomMarginRatio = body.BottomMarginRatio ?? o.BottomMarginRatio;
                o.X = body.X ?? o.X;
                o.Y = body.Y ?? o.Y;
                o.DismissOnFocusLost = body.DismissOnFocusLost ?? o.DismissOnFocusLost;
                o.DismissOnEscape = body.DismissOnEscape ?? o.DismissOnEscape;
                o.ActivateOnShow = body.ActivateOnShow ?? o.ActivateOnShow;

                if (body.HotKey is { } key)
                    this.RegisterHotKey(key.Length == 0 ? null : key);
            });
        }
        finally
        {
            this.gate.Release();
        }

        await this.RespondStatusAsync(context);
    }

    async ValueTask VisibilityAsync(HttpContext context, Action<IQuickEntryService> action)
    {
        if (!await this.EnsureSupportedAsync(context))
            return;

        // Wired before opening, so the prompt the user sees already carries what the page configured.
        await this.WirePromptAsync();
        await MainAsync(() => action(this.service!));
        await this.RespondStatusAsync(context);
    }

    async ValueTask GetPromptAsync(HttpContext context)
    {
        if (!await this.EnsureSupportedAsync(context))
            return;

        if (await this.WirePromptAsync() is not { } prompt)
        {
            await CustomContentAsync(context);
            return;
        }

        await WebAppBridgeResults.Json(context, await MainAsync(() => this.Describe(prompt)), QuickEntryJsonContext.Default.QuickEntryPrompt);
    }

    async ValueTask SetPromptAsync(HttpContext context)
    {
        if (!await this.EnsureSupportedAsync(context))
            return;

        var body = await WebAppBridgeResults.ReadBodyAsync(context, QuickEntryJsonContext.Default.QuickEntryPromptInput);
        if (body is null)
        {
            await WebAppBridgeResults.BadRequest(context, "Expected the prompt properties to change.");
            return;
        }

        if (this.Validate(body) is { } problem)
        {
            await WebAppBridgeResults.BadRequest(context, problem);
            return;
        }

        if (await this.WirePromptAsync() is not { } prompt)
        {
            await CustomContentAsync(context);
            return;
        }

        var described = await MainAsync(() =>
        {
            this.model = this.model.With(body);
            this.model.ApplyTo(prompt);

            if (body.Text is { } text)
                prompt.Text = text;

            return this.Describe(prompt);
        });

        await WebAppBridgeResults.Json(context, described, QuickEntryJsonContext.Default.QuickEntryPrompt);
    }

    async ValueTask ResetPromptAsync(HttpContext context)
    {
        if (!await this.EnsureSupportedAsync(context))
            return;

        if (await this.WirePromptAsync() is not { } prompt)
        {
            await CustomContentAsync(context);
            return;
        }

        var described = await MainAsync(() =>
        {
            prompt.Reset();
            this.model = this.model with { IsBusy = false, Response = null };
            this.model.ApplyTo(prompt);
            return this.Describe(prompt);
        });

        await WebAppBridgeResults.Json(context, described, QuickEntryJsonContext.Default.QuickEntryPrompt);
    }

    async ValueTask GlowAsync(HttpContext context, Action<IQuickEntryService> action)
    {
        if (!await this.EnsureSupportedAsync(context))
            return;

        await MainAsync(() => action(this.service!));
        await WebAppBridgeResults.NoContent(context);
    }

    async ValueTask PulseAsync(HttpContext context)
    {
        if (!await this.EnsureSupportedAsync(context))
            return;

        var body = await WebAppBridgeResults.ReadBodyAsync(context, QuickEntryJsonContext.Default.QuickEntryPulse);
        if (body is not { DurationMs: >= 100 and <= 30_000 })
        {
            await WebAppBridgeResults.BadRequest(context, "Expected { \"durationMs\": 100 to 30000 }.");
            return;
        }

        var pulse = await MainAsync(() => this.service!.PulseGlowAsync(TimeSpan.FromMilliseconds(body.DurationMs), context.RequestAborted));
        await pulse;
        await WebAppBridgeResults.NoContent(context);
    }

    async ValueTask<bool> EnsureSupportedAsync(HttpContext context)
    {
        if (!this.IsSupported)
        {
            await WebAppBridgeResults.NotSupported(context, "Quick entry");
            return false;
        }

        await this.StartAsync();
        return true;
    }

    async ValueTask RespondStatusAsync(HttpContext context)
        => await WebAppBridgeResults.Json(context, await MainAsync(this.DescribeStatus), QuickEntryJsonContext.Default.QuickEntryStatus);

    QuickEntryStatus DescribeStatus()
    {
        var quickEntry = this.service!;

        return new QuickEntryStatus(
            true,
            BridgeEnum.Convert<ControlEntry.QuickEntryPresentation, Contracts.QuickEntryPresentation>(quickEntry.ResolvedPresentation),
            quickEntry.IsOpen,
            quickEntry.IsGlowSupported,
            quickEntry.IsGlowVisible,
            this.hotKey,
            this.hotKeyRegistration is not null,
            quickEntry.Content is null or PromptView
        );
    }

    QuickEntryPrompt Describe(PromptView prompt) => new(
        prompt.Text ?? String.Empty,
        prompt.Placeholder ?? String.Empty,
        prompt.IsBusy,
        prompt.BusyText ?? String.Empty,
        prompt.Response,
        this.model.Suggestions,
        prompt.ShowMicrophone,
        prompt.ShowSubmitButton,
        prompt.ClearOnSubmit
    );

    static ValueTask CustomContentAsync(HttpContext context) => WebAppBridgeResults.Error(
        context,
        StatusCodes.Status409Conflict,
        "custom_content",
        "The app replaced the quick entry prompt with content of its own, which the prompt calls cannot drive."
    );

    /// <summary>
    /// The prompt, built if it has not been, with the page's settings applied and its events wired. Null when the app gave
    /// the window content of its own. A window that rebuilds its content on every open gets the new prompt wired here too.
    /// </summary>
    async Task<PromptView?> WirePromptAsync()
    {
        await MainAsync(() => this.service!.PreloadAsync()).Unwrap();

        return await MainAsync(() =>
        {
            if (this.service!.Content is not PromptView prompt)
                return null;

            if (ReferenceEquals(prompt, this.wired))
                return prompt;

            if (this.wired is { } previous)
            {
                previous.Submitted -= this.OnSubmitted;
                previous.SuggestionSelected -= this.OnSuggestionSelected;
                previous.Cancelled -= this.OnCancelled;
            }

            prompt.Submitted += this.OnSubmitted;
            prompt.SuggestionSelected += this.OnSuggestionSelected;
            prompt.Cancelled += this.OnCancelled;
            prompt.MicrophoneCommand = new Command(() => this.Raise(
                this.microphone,
                MicrophoneEvent,
                new QuickEntryPromptEvent(prompt.Text ?? String.Empty),
                QuickEntryJsonContext.Default.QuickEntryPromptEvent
            ));

            this.model.ApplyTo(prompt);
            this.wired = prompt;
            return prompt;
        });
    }

    /// <summary>Must run on the UI thread: the platform hotkey services register there.</summary>
    void RegisterHotKey(string? accelerator)
    {
        this.hotKeyRegistration?.Dispose();
        this.hotKeyRegistration = null;
        this.hotKey = accelerator;

        if (accelerator is null || this.hotKeys is not { IsSupported: true } keys)
            return;

        this.hotKeyRegistration = keys.Register(accelerator, () => this.service!.Toggle());

        if (this.hotKeyRegistration is null)
            this.logger?.LogWarning("The quick entry hotkey '{HotKey}' could not be registered; another application may own it", accelerator);
    }

    async void OnOpened(object? sender, EventArgs e)
    {
        try
        {
            await this.WirePromptAsync();
            this.Raise(this.opened, OpenedEvent, await MainAsync(this.DescribeStatus), QuickEntryJsonContext.Default.QuickEntryStatus);
        }
        catch (Exception ex)
        {
            this.logger?.LogWarning(ex, "Quick entry could not report opening");
        }
    }

    async void OnClosed(object? sender, EventArgs e)
    {
        try
        {
            this.Raise(this.closed, ClosedEvent, await MainAsync(this.DescribeStatus), QuickEntryJsonContext.Default.QuickEntryStatus);
        }
        catch (Exception ex)
        {
            this.logger?.LogWarning(ex, "Quick entry could not report closing");
        }
    }

    void OnSubmitted(object? sender, PromptSubmittedEventArgs e)
        => this.Raise(this.submitted, SubmittedEvent, this.ToSubmission(e), QuickEntryJsonContext.Default.QuickEntrySubmission);

    void OnSuggestionSelected(object? sender, PromptSubmittedEventArgs e)
        => this.Raise(this.suggestions, SuggestionEvent, this.ToSubmission(e), QuickEntryJsonContext.Default.QuickEntrySubmission);

    void OnCancelled(object? sender, EventArgs e)
        => this.Raise(this.cancelled, CancelledEvent, new QuickEntryPromptEvent((sender as PromptView)?.Text ?? String.Empty), QuickEntryJsonContext.Default.QuickEntryPromptEvent);

    QuickEntrySubmission ToSubmission(PromptSubmittedEventArgs e) => new(
        e.Text,
        e.Suggestion switch
        {
            PromptSuggestion { Value: QuickEntrySuggestion original } => original,
            PromptSuggestion row => new QuickEntrySuggestion(row.Text, row.Description, row.Glyph),
            null => null,
            var other => new QuickEntrySuggestion(other.ToString() ?? String.Empty)
        }
    );

    /// <summary>
    /// Published to whatever listens for the event, and called on the web app so <c>background.js</c> can answer when the
    /// app's window is closed — which is when a window summoned over other applications earns its keep.
    /// </summary>
    void Raise<T>(WebAppEventSource<T> source, string name, T payload, JsonTypeInfo<T> typeInfo)
    {
        source.Publish(payload);

        if (this.invoker is { } target)
            _ = Forget(target.InvokeAsync(name, payload, typeInfo));
    }

    static async Task Forget(Task<WebAppInvocationResult> call)
    {
        try
        {
            await call;
        }
        catch
        {
            // Raised from a native event handler: nothing here is worth taking the app down for.
        }
    }

    internal static string? Validate(QuickEntryOptionsInput body)
    {
        if (body.Width is { } width && width is < 200 or > 4000)
            return "width must be between 200 and 4000.";

        if (body.CollapsedHeight is { } collapsed && collapsed is < 20 or > 4000)
            return "collapsedHeight must be between 20 and 4000.";

        if (body.MaxHeight is { } max && max is < 20 or > 4000)
            return "maxHeight must be between 20 and 4000.";

        if (body.TopMarginRatio is { } top && top is < 0 or > 1)
            return "topMarginRatio must be between 0 and 1.";

        if (body.BottomMarginRatio is { } bottom && bottom is < 0 or > 1)
            return "bottomMarginRatio must be between 0 and 1.";

        if (body.HotKey is { Length: > 64 })
            return "hotKey is too long.";

        return null;
    }

    internal string? Validate(QuickEntryPromptInput body)
    {
        var max = this.options.MaxTextLength;

        if (new[] { body.Text, body.Placeholder, body.BusyText, body.Response }.Any(x => x is not null && x.Length > max))
            return $"Text is limited to {max} characters.";

        if (body.Suggestions is { } suggestions)
        {
            if (suggestions.Count > this.options.MaxSuggestions)
                return $"At most {this.options.MaxSuggestions} suggestions.";

            if (suggestions.Any(x => x is null || String.IsNullOrWhiteSpace(x.Text) || x.Text.Length > max))
                return "Every suggestion needs text.";
        }

        return null;
    }

    static Task MainAsync(Action action)
    {
        if (Application.Current?.Dispatcher is { } dispatcher)
            return dispatcher.DispatchAsync(action);

        action();
        return Task.CompletedTask;
    }

    static Task<T> MainAsync<T>(Func<T> func)
        => Application.Current?.Dispatcher is { } dispatcher ? dispatcher.DispatchAsync(func) : Task.FromResult(func());

    public void Dispose()
    {
        if (this.disposed)
            return;

        this.disposed = true;

        if (this.service is not null && this.started)
        {
            this.service.Opened -= this.OnOpened;
            this.service.Closed -= this.OnClosed;
        }

        this.hotKeyRegistration?.Dispose();
        this.gate.Dispose();
    }

    /// <summary>The prompt as the page configured it. Everything but the typed text, which belongs to the user.</summary>
    internal sealed record PromptModel
    {
        public string? Placeholder { get; init; }
        public bool? IsBusy { get; init; }
        public string? BusyText { get; init; }
        public string? Response { get; init; }
        public IReadOnlyList<QuickEntrySuggestion> Suggestions { get; init; } = [];
        public bool? ShowMicrophone { get; init; }
        public bool? ShowSubmitButton { get; init; }
        public bool? ClearOnSubmit { get; init; }

        public PromptModel With(QuickEntryPromptInput input) => this with
        {
            Placeholder = input.Placeholder ?? this.Placeholder,
            IsBusy = input.IsBusy ?? this.IsBusy,
            BusyText = input.BusyText ?? this.BusyText,
            Response = input.Response is null ? this.Response : input.Response.Length == 0 ? null : input.Response,
            Suggestions = input.Suggestions ?? this.Suggestions,
            ShowMicrophone = input.ShowMicrophone ?? this.ShowMicrophone,
            ShowSubmitButton = input.ShowSubmitButton ?? this.ShowSubmitButton,
            ClearOnSubmit = input.ClearOnSubmit ?? this.ClearOnSubmit
        };

        /// <summary>Only what the page set: a prompt keeps its own defaults for the rest.</summary>
        public void ApplyTo(PromptView prompt)
        {
            if (this.Placeholder is not null)
                prompt.Placeholder = this.Placeholder;

            if (this.IsBusy is { } busy)
                prompt.IsBusy = busy;

            if (this.BusyText is not null)
                prompt.BusyText = this.BusyText;

            prompt.Response = this.Response;
            prompt.Suggestions = this.Suggestions.Count == 0
                ? null
                : this.Suggestions.Select(x => new PromptSuggestion(x.Text, x.Description, x.Glyph, x)).ToList();

            if (this.ShowMicrophone is { } microphone)
                prompt.ShowMicrophone = microphone;

            if (this.ShowSubmitButton is { } submit)
                prompt.ShowSubmitButton = submit;

            if (this.ClearOnSubmit is { } clear)
                prompt.ClearOnSubmit = clear;
        }
    }
}

/// <summary>Registers the starting hotkey once the app is up, without waiting for a page to call the bridge.</summary>
sealed class QuickEntryBridgeStartup : IMauiInitializeService
{
    public void Initialize(IServiceProvider services)
    {
        if (services.GetOptionalService<QuickEntryBridge>() is { IsSupported: true } bridge)
            _ = Start(bridge, services.GetService<ILogger<QuickEntryBridge>>());
    }

    static async Task Start(QuickEntryBridge bridge, ILogger? logger)
    {
        try
        {
            await bridge.StartAsync();
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Quick entry could not start");
        }
    }
}
