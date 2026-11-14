using Microsoft.UI.Xaml.Controls;

namespace ShaGui.App.Pages;

public sealed partial class AboutPage : Page
{
    public AboutPage()
    {
        InitializeComponent();

        var health = App.NativeHealth;
        HealthBar.Severity = health.IsAvailable
            ? InfoBarSeverity.Success
            : InfoBarSeverity.Error;
        HealthBar.Title = health.IsAvailable ? "Native backend ready" : "Native backend unavailable";
        HealthBar.Message = health.Detail;
    }
}
