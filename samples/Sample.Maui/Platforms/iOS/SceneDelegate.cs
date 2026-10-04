using Foundation;

namespace Sample.Maui;

// Apps built with the iOS 27 SDK refuse to launch without the UIScene life cycle. Info.plist's
// UIApplicationSceneManifest names this class.
[Register("SceneDelegate")]
public class SceneDelegate : MauiUISceneDelegate;
