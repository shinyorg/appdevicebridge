using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Shiny.AppDeviceBridge.Client;
using Shiny.AppDeviceBridge.Desktop;
using Shiny.AppDeviceBridge.Desktop.Client;

namespace Shiny.AppDeviceBridge.Tests;

/// <summary>
/// The quick entry bridge without a window: what the page is told when quick entry is missing, and the checks every
/// request meets before anything reaches the control.
/// </summary>
public class QuickEntryTests
{
    [Fact]
    public async Task Says_it_is_unsupported_and_answers_501_without_the_control()
    {
        var services = new ServiceCollection().BuildServiceProvider();
        await using var fixture = await BuiltInClientTests.HostFixture.StartAsync(_ => [new QuickEntryBridge(services, new WebAppEventHub())]);
        var quickEntry = new QuickEntryBridgeClient(fixture.Transport);

        var status = await quickEntry.GetStatusAsync();
        Assert.False(status.Supported);
        Assert.False(status.IsOpen);

        foreach (var call in new Func<Task>[]
        {
            () => quickEntry.ShowAsync(),
            () => quickEntry.ToggleAsync(),
            () => quickEntry.SetPromptAsync(new QuickEntryPromptInput(Placeholder: "Ask")),
            () => quickEntry.ConfigureAsync(new QuickEntryOptionsInput(HotKey: "Ctrl+Alt+Space")),
            () => quickEntry.PulseGlowAsync(new QuickEntryPulse(500))
        })
        {
            var refused = await Assert.ThrowsAsync<BridgeException>(call);
            Assert.Equal(HttpStatusCode.NotImplemented, refused.StatusCode);
        }
    }

    [Theory]
    [InlineData(199d, null, null, "width")]
    [InlineData(4001d, null, null, "width")]
    [InlineData(null, -0.1d, null, "topMarginRatio")]
    [InlineData(null, 1.5d, null, "topMarginRatio")]
    [InlineData(null, null, 10d, "maxHeight")]
    public void Refuses_options_it_cannot_lay_out(double? width, double? top, double? maxHeight, string expected)
        => Assert.Contains(expected, QuickEntryBridge.Validate(new QuickEntryOptionsInput(Width: width, TopMarginRatio: top, MaxHeight: maxHeight)));

    [Fact]
    public void Accepts_sensible_options_and_a_hotkey_removal()
    {
        Assert.Null(QuickEntryBridge.Validate(new QuickEntryOptionsInput(Width: 720, MaxHeight: 560, TopMarginRatio: 0.18, HotKey: "")));
        Assert.Contains("hotKey", QuickEntryBridge.Validate(new QuickEntryOptionsInput(HotKey: new string('k', 65))));
    }

    [Fact]
    public void Holds_the_page_to_its_limits_on_the_prompt()
    {
        var services = new ServiceCollection()
            .AddSingleton(new QuickEntryBridgeOptions { MaxSuggestions = 2, MaxTextLength = 10 })
            .BuildServiceProvider();
        var bridge = new QuickEntryBridge(services, new WebAppEventHub());

        Assert.Null(bridge.Validate(new QuickEntryPromptInput(Placeholder: "Ask", Suggestions: [new("a"), new("b")])));
        Assert.Contains("At most 2", bridge.Validate(new QuickEntryPromptInput(Suggestions: [new("a"), new("b"), new("c")])));
        Assert.Contains("needs text", bridge.Validate(new QuickEntryPromptInput(Suggestions: [new(" ")])));
        Assert.Contains("10 characters", bridge.Validate(new QuickEntryPromptInput(Response: "far too long for this")));
    }

    /// <summary>What survives a window that rebuilds its prompt on every open, and how the page clears things.</summary>
    [Fact]
    public void Remembers_what_the_page_set_and_clears_on_empty()
    {
        var model = new QuickEntryBridge.PromptModel()
            .With(new QuickEntryPromptInput(Placeholder: "Ask", Response: "Hello", Suggestions: [new("One", Value: "1")], IsBusy: true))
            .With(new QuickEntryPromptInput(IsBusy: false));

        Assert.Equal("Ask", model.Placeholder);
        Assert.Equal("Hello", model.Response);
        Assert.False(model.IsBusy);
        Assert.Equal("1", Assert.Single(model.Suggestions).Value);

        var cleared = model.With(new QuickEntryPromptInput(Response: "", Suggestions: []));
        Assert.Null(cleared.Response);
        Assert.Empty(cleared.Suggestions);
        Assert.Equal("Ask", cleared.Placeholder);
    }

    [Fact]
    public void Maps_every_contract_enum_onto_the_control()
    {
        foreach (var value in Enum.GetValues<Desktop.Client.QuickEntryPresentation>())
            BridgeEnum.Convert<Desktop.Client.QuickEntryPresentation, Shiny.Maui.Controls.QuickEntry.QuickEntryPresentation>(value);

        foreach (var value in Enum.GetValues<Desktop.Client.QuickEntryPlacement>())
            BridgeEnum.Convert<Desktop.Client.QuickEntryPlacement, Shiny.Maui.Controls.QuickEntry.QuickEntryPlacement>(value);

        foreach (var value in Enum.GetValues<QuickEntryGlowTrigger>())
            BridgeEnum.Convert<QuickEntryGlowTrigger, Shiny.Maui.Controls.QuickEntry.ScreenGlowTrigger>(value);

        // And back, for the presentation the control resolves.
        foreach (var value in Enum.GetValues<Shiny.Maui.Controls.QuickEntry.QuickEntryPresentation>())
            BridgeEnum.Convert<Shiny.Maui.Controls.QuickEntry.QuickEntryPresentation, Desktop.Client.QuickEntryPresentation>(value);
    }
}
