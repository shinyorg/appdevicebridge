using Android.App;
using Android.Content;
using Android.Content.PM;

namespace Sample.Maui;

// Name is explicit so the Health Connect activity-alias in AndroidManifest.xml can target it. SingleTop so an app
// link reaches this activity through OnNewIntent instead of stacking a second WebView and host.
[Activity(
    Name = "org.shinylib.appdevicebridge.sample.MainActivity",
    Theme = "@style/Maui.MainTheme.NoActionBar",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density
)]
// App links bridge: sample://…
[IntentFilter(
    [Intent.ActionView],
    Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable],
    DataScheme = "sample"
)]
// Health bridge: Health Connect shows the app's privacy rationale through this, and grants nothing without it.
[IntentFilter(["androidx.health.ACTION_SHOW_PERMISSIONS_RATIONALE"])]
public class MainActivity : MauiAppCompatActivity;
