using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ShaGui.Core.Hashing;
using ShaGui.Core.Native;
using ShaGui.Core.Settings;

namespace ShaGui.App.Pages;

/// <summary>
/// Settings. Values are loaded from and saved to the local SQLite database
/// (<see cref="App.Settings"/>); every page reads the in-memory copy so a
/// change applies without a restart.
/// </summary>
public sealed partial class SettingsPage : Page
{
    public SettingsPage()
    {
        InitializeComponent();

        var health = App.NativeHealth;
        HealthBar.Severity = health.IsAvailable ? InfoBarSeverity.Success : InfoBarSeverity.Error;
        HealthBar.Title = health.IsAvailable ? "Native backend ready" : "Native backend unavailable";
        HealthBar.Message = health.Detail;

        LoadFrom(App.Settings);
    }

    private void LoadFrom(AppSettings settings)
    {
        BufferSizeBox.Value = settings.BufferSize;
        IncludeHiddenSwitch.IsOn = settings.IncludeHidden;
        FollowReparseSwitch.IsOn = settings.FollowReparsePoints;
        RecordHistorySwitch.IsOn = settings.RecordHistory;
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        // NumberBox allows NaN when the box is cleared; fall back to the
        // native default rather than persisting a nonsense buffer.
        ulong buffer = double.IsNaN(BufferSizeBox.Value) || BufferSizeBox.Value < 4096
            ? ShaHasher.DefaultBufferSize
            : (ulong)BufferSizeBox.Value;

        var updated = new AppSettings
        {
            DefaultAlgorithm = App.Settings.DefaultAlgorithm,
            BufferSize = buffer,
            IncludeHidden = IncludeHiddenSwitch.IsOn,
            FollowReparsePoints = FollowReparseSwitch.IsOn,
            RecordHistory = RecordHistorySwitch.IsOn,
        };

        App.SaveSettings(updated);
        LoadFrom(updated);

        SaveStatus.Severity = App.DatabaseError is null
            ? InfoBarSeverity.Success
            : InfoBarSeverity.Warning;
        SaveStatus.Title = App.DatabaseError is null ? "Settings saved" : "Settings applied, not stored";
        SaveStatus.Message = App.DatabaseError is null
            ? $"{buffer:N0} B buffer · hidden {(updated.IncludeHidden ? "on" : "off")} · "
              + $"reparse points {(updated.FollowReparsePoints ? "followed" : "skipped")} · "
              + $"history {(updated.RecordHistory ? "recording" : "paused")}"
            : $"The database is unavailable ({App.DatabaseError}); settings apply to this session only.";
        SaveStatus.IsOpen = true;
    }
}
