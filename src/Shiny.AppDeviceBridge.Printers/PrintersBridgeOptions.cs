namespace Shiny.AppDeviceBridge.Printers;

public sealed class PrintersBridgeOptions
{
    /// <summary>
    /// The TCP ports a page may connect to a network printer it names by address: 9100–9102, the raw (JetDirect /
    /// AppSocket) ports, by default. The host must be an IP address on the local network either way. Holding both down
    /// keeps a receipt — whose elements can carry raw bytes — from becoming a socket to any service on the LAN. A printer
    /// found by a scan is connected on the port it advertised.
    /// </summary>
    public ISet<int> NetworkPorts { get; } = new HashSet<int> { 9100, 9101, 9102 };

    internal void Validate()
    {
        if (this.NetworkPorts.Any(x => x is < 1 or > 65_535))
            throw new InvalidOperationException("PrintersBridgeOptions.NetworkPorts are 1 to 65535.");
    }
}
