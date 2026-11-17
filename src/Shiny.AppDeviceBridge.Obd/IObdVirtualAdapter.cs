using Shiny.Obd;

namespace Shiny.AppDeviceBridge.Obd;

/// <summary>
/// An OBD adapter that isn't hardware: the Shiny.Obd emulator, a demo vehicle for an app store reviewer who has no adapter,
/// a test double. Every scan lists it, whatever the transport asked for, and the page connects to it by passing its
/// <see cref="Id"/> as <c>peripheralUuid</c>. Everything after that — commands, raw requests, trouble codes, the monitor —
/// runs exactly as it does against hardware. Register one with <c>bridge.AddObdVirtualAdapter(adapter)</c>.
/// </summary>
public interface IObdVirtualAdapter
{
    /// <summary>What the page connects with. Stable, so a page that remembers its adapter finds it again.</summary>
    string Id { get; }

    string Name { get; }

    /// <summary>False keeps it out of scans and refuses connections to it — an emulator with no vehicle loaded.</summary>
    bool IsAvailable { get; }

    /// <summary>A new transport to the adapter for each connection. The bridge disposes it with the connection.</summary>
    IObdTransport CreateTransport();
}
