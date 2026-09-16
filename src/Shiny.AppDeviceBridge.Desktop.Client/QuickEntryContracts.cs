using System.Text.Json.Serialization;

namespace Shiny.AppDeviceBridge.Desktop.Client;

public enum QuickEntryPresentation
{
    /// <summary>A desktop window where one is available, an overlay on the app's own window everywhere else.</summary>
    Auto,

    /// <summary>An overlay drawn over the app's own window.</summary>
    InApp,

    /// <summary>A borderless, always-on-top window that opens over other applications. The bridge's default.</summary>
    Desktop
}

public enum QuickEntryPlacement
{
    /// <summary>Centred, in the upper part of the screen with the pointer — where Spotlight sits.</summary>
    TopCenter,

    /// <summary>Centred, near the bottom of the screen.</summary>
    BottomCenter,

    Center,

    /// <summary>Just below and right of the pointer.</summary>
    NearCursor,

    /// <summary>At <see cref="QuickEntryOptionsInput.X"/> and <see cref="QuickEntryOptionsInput.Y"/>.</summary>
    Manual
}

public enum QuickEntryGlowTrigger
{
    /// <summary>The glow only lights when asked.</summary>
    None,

    /// <summary>The glow lights while the window is open.</summary>
    WhileOpen,

    /// <summary>The glow lights while the prompt is busy.</summary>
    WhileBusy
}

/// <summary>What quick entry can do here and what it is doing.</summary>
/// <param name="Supported">False on Android and iOS, where every other call fails with 501.</param>
/// <param name="Presentation">How the window will actually open, with <see cref="QuickEntryPresentation.Auto"/> resolved.</param>
/// <param name="HotKey">The global shortcut that toggles the window, when one is set.</param>
/// <param name="HotKeyRegistered">False when the shortcut is set but another application already owns it.</param>
/// <param name="HasPrompt">False when the app replaced the prompt with content of its own, which the prompt calls cannot drive.</param>
public sealed record QuickEntryStatus(
    bool Supported,
    QuickEntryPresentation Presentation,
    bool IsOpen,
    bool GlowSupported,
    bool GlowVisible,
    string? HotKey,
    bool HotKeyRegistered,
    bool HasPrompt
);

/// <summary>Changes to how the window opens. Properties left null are left as they are.</summary>
/// <param name="HotKey">A global shortcut such as <c>Cmd+Opt+Space</c> or <c>Ctrl+Alt+Space</c>; an empty string removes it.</param>
/// <param name="Width">Device-independent pixels, 200 to 4000.</param>
/// <param name="CollapsedHeight">The height with nothing below the entry row.</param>
/// <param name="MaxHeight">The window grows to fit suggestions and a response, up to this.</param>
/// <param name="TopMarginRatio">For <see cref="QuickEntryPlacement.TopCenter"/>: the top edge as a fraction of the screen, 0 to 1.</param>
/// <param name="BottomMarginRatio">For <see cref="QuickEntryPlacement.BottomCenter"/>: the gap below as a fraction of the screen, 0 to 1.</param>
/// <param name="X">For <see cref="QuickEntryPlacement.Manual"/>: screen coordinates from the top left.</param>
/// <param name="DismissOnFocusLost">Close when another application takes focus.</param>
/// <param name="ActivateOnShow">Take keyboard focus when opening. Off for a window that should not steal typing.</param>
/// <param name="Glow">What lights the glow around the screen's edge.</param>
public sealed record QuickEntryOptionsInput(
    QuickEntryPresentation? Presentation = null,
    string? HotKey = null,
    double? Width = null,
    double? CollapsedHeight = null,
    double? MaxHeight = null,
    QuickEntryPlacement? Placement = null,
    double? TopMarginRatio = null,
    double? BottomMarginRatio = null,
    double? X = null,
    double? Y = null,
    bool? DismissOnFocusLost = null,
    bool? DismissOnEscape = null,
    bool? ActivateOnShow = null,
    QuickEntryGlowTrigger? Glow = null
);

/// <summary>A row under the entry.</summary>
/// <param name="Text">What is written into the entry when it is chosen.</param>
/// <param name="Description">A second line.</param>
/// <param name="Glyph">A leading emoji or icon-font character.</param>
/// <param name="Value">Anything the page wants back when the suggestion is chosen.</param>
public sealed record QuickEntrySuggestion(string Text, string? Description = null, string? Glyph = null, string? Value = null);

/// <summary>The prompt in the window.</summary>
/// <param name="Text">What is typed in the entry right now.</param>
/// <param name="IsBusy">Shows <see cref="BusyText"/> and an animated orb in place of the entry, while the page works.</param>
/// <param name="Response">Text shown under the entry — the answer to what was submitted.</param>
/// <param name="ShowMicrophone">Shows a microphone button, which raises <c>quickentry.microphone</c>.</param>
/// <param name="ClearOnSubmit">Empty the entry once a prompt is submitted.</param>
public sealed record QuickEntryPrompt(
    string Text,
    string Placeholder,
    bool IsBusy,
    string BusyText,
    string? Response,
    IReadOnlyList<QuickEntrySuggestion> Suggestions,
    bool ShowMicrophone,
    bool ShowSubmitButton,
    bool ClearOnSubmit
);

/// <summary>
/// Changes to the prompt. Properties left null are left as they are; an empty <see cref="Response"/> removes the response
/// and an empty <see cref="Suggestions"/> list removes the suggestions. Everything but <see cref="Text"/> is kept across a
/// window that rebuilds its content on every open.
/// </summary>
public sealed record QuickEntryPromptInput(
    string? Text = null,
    string? Placeholder = null,
    bool? IsBusy = null,
    string? BusyText = null,
    string? Response = null,
    IReadOnlyList<QuickEntrySuggestion>? Suggestions = null,
    bool? ShowMicrophone = null,
    bool? ShowSubmitButton = null,
    bool? ClearOnSubmit = null
);

/// <summary>A prompt the user submitted.</summary>
/// <param name="Suggestion">The suggestion chosen to submit it; null when it was typed.</param>
public sealed record QuickEntrySubmission(string Text, QuickEntrySuggestion? Suggestion);

/// <summary>The entry's text when the user cancelled or pressed the microphone.</summary>
public sealed record QuickEntryPromptEvent(string Text);

/// <param name="DurationMs">How long the glow stays lit: 100 ms to 30 s.</param>
public sealed record QuickEntryPulse(int DurationMs);

/// <summary>Serialization for every quick entry contract, shared by the page's client and the native bridge.</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(QuickEntryStatus))]
[JsonSerializable(typeof(QuickEntryOptionsInput))]
[JsonSerializable(typeof(QuickEntryPrompt))]
[JsonSerializable(typeof(QuickEntryPromptInput))]
[JsonSerializable(typeof(QuickEntrySubmission))]
[JsonSerializable(typeof(QuickEntryPromptEvent))]
[JsonSerializable(typeof(QuickEntryPulse))]
public partial class QuickEntryJsonContext : JsonSerializerContext;
