using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Net.Wifi;
using Android.OS;

namespace AuraDrop.AndroidApp;

[Activity(Theme = "@style/Maui.SplashTheme", MainLauncher = true, LaunchMode = LaunchMode.SingleTop, ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public class MainActivity : MauiAppCompatActivity
{
    private WifiManager.MulticastLock? _multicastLock;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        try
        {
            var wifi = (WifiManager?)GetSystemService(WifiService);
            if (wifi != null)
            {
                _multicastLock = wifi.CreateMulticastLock("AuraDropMulticastLock");
                _multicastLock.SetReferenceCounted(true);
                _multicastLock.Acquire();
            }
        }
        catch { }
    }

    protected override void OnDestroy()
    {
        base.OnDestroy();
        try
        {
            if (_multicastLock != null && _multicastLock.IsHeld)
            {
                _multicastLock.Release();
            }
        }
        catch { }
    }
}
