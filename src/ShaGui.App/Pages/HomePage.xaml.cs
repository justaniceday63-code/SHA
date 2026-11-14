using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace ShaGui.App.Pages;

public sealed partial class HomePage : Page
{
    public HomePage()
    {
        InitializeComponent();

        var health = App.NativeHealth;
        NativeHealthText = health.Detail;
        NativeHealthBar.Severity = health.IsAvailable
            ? InfoBarSeverity.Success
            : InfoBarSeverity.Error;
        NativeHealthBar.Title = health.IsAvailable
            ? "Native backend ready"
            : "Native backend unavailable";
    }

    public string NativeHealthText { get; }

    // These must be the NavigationView items' Tag slugs (lowercase, ordinal
    // match in MainWindow.SelectSection) — display labels like "Verify" or
    // "Collision demo" match nothing and the buttons silently do nothing.
    private void OnOpenVerify(object sender, RoutedEventArgs e) => Navigate("verify");
    private void OnOpenLearn(object sender, RoutedEventArgs e) => Navigate("learn");
    private void OnOpenCollision(object sender, RoutedEventArgs e) => Navigate("collision");
    private void OnOpenHistory(object sender, RoutedEventArgs e) => Navigate("history");

    /// <summary>
    /// Navigates through the shell's NavigationView so the pane selection
    /// stays in sync — finding the ancestor is cheaper than re-plumbing
    /// navigation events through every page.
    /// </summary>
    private void Navigate(string tag)
    {
        if (App.MainWindow is { } window)
        {
            window.SelectSection(tag);
        }
    }
}
