using Android.App;
using Android.Runtime;

namespace Sample.Maui;

// The network security config allows cleartext to loopback — where the web app is served — and to
// the emulator's alias for the development machine, where the sample release server runs.
[Application(NetworkSecurityConfig = "@xml/network_security_config")]
public class MainApplication(IntPtr handle, JniHandleOwnership ownership) : MauiApplication(handle, ownership)
{
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
}
