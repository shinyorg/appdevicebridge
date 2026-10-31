using Shiny.BluetoothLE;
using Shiny.BluetoothLE.Hosting;

namespace Shiny.AppDeviceBridge.Hubs;

/// <summary>
/// What the hubs see on a head where the app registered no Bluetooth LE hosting — Windows, macOS or Linux without
/// <c>AddBluetoothLeHosting()</c>. Universal Hubs needs a hosting manager even to run over Wi-Fi alone; this one reports
/// hosting as unsupported, so starting a hub skips BLE and runs on the transports that can.
/// </summary>
sealed class UnavailableBleHosting : IBleHostingManager
{
    public AccessState AdvertisingAccessStatus => AccessState.NotSupported;

    public AccessState GattAccessStatus => AccessState.NotSupported;

    public bool IsAdvertising => false;

    public IReadOnlyList<IGattService> Services => [];

    public Task<AccessState> RequestAccess(bool advertise = true, bool connect = true) => Task.FromResult(AccessState.NotSupported);

    public Task StartAdvertising(AdvertisementOptions? options = null) => throw NotSupported();

    public void StopAdvertising() { }

    public Task<L2CapInstance> OpenL2Cap(bool secure, Action<L2CapChannel> onOpen) => throw NotSupported();

    public Task AdvertiseBeacon(Guid uuid, ushort major, ushort minor, sbyte? txpower = null) => throw NotSupported();

    public Task<IGattService> AddService(string uuid, bool primary, Action<IGattServiceBuilder> serviceBuilder) => throw NotSupported();

    public void RemoveService(string serviceUuid) { }

    public void ClearServices() { }

    static PlatformNotSupportedException NotSupported()
        => new("Bluetooth LE hosting is not registered on this platform - call services.AddBluetoothLeHosting() to host hubs over BLE.");
}
