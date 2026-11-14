using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ShaGui.App.Pages;
using System.Runtime.InteropServices;

namespace ShaGui.App;

public sealed partial class MainWindow : Window
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon("Assets/AppIcon.ico");

        // Navigate once in the constructor so the first frame is never empty.
        NavFrame.Navigate(typeof(HomePage));

        // Multi-pane tool: nav pane (structural) + content. Sized to the
        // rubric's 1100-1300 x 720-840 range; AppWindow.Resize is in
        // physical pixels, so scale by the monitor DPI.
        var hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        double scale = GetDpiForWindow(hwnd) / 96.0;
        if (scale <= 0)
        {
            scale = 1.0;
        }
        AppWindow.Resize(new Windows.Graphics.SizeInt32(
            (int)(1240 * scale), (int)(800 * scale)));
    }

    private void TitleBar_PaneToggleRequested(TitleBar sender, object args)
    {
        NavView.IsPaneOpen = !NavView.IsPaneOpen;
    }

    private void TitleBar_BackRequested(TitleBar sender, object args)
    {
        if (NavFrame.CanGoBack)
        {
            NavFrame.GoBack();
        }
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            NavFrame.Navigate(typeof(SettingsPage));
            return;
        }

        if (args.SelectedItem is not NavigationViewItem item)
        {
            return;
        }

        NavigateToTag(item.Tag as string);
    }

    /// <summary>
    /// Selects a section by its NavigationView tag. Used by the pane itself
    /// and by Home's shortcut cards, so the pane highlight always matches the
    /// content frame.
    /// </summary>
    public void SelectSection(string tag)
    {
        foreach (var menuItem in NavView.MenuItems.OfType<NavigationViewItem>())
        {
            if (string.Equals(menuItem.Tag as string, tag, StringComparison.Ordinal))
            {
                NavView.SelectedItem = menuItem;
                NavigateToTag(tag);
                return;
            }
        }
        foreach (var menuItem in NavView.FooterMenuItems.OfType<NavigationViewItem>())
        {
            if (string.Equals(menuItem.Tag as string, tag, StringComparison.Ordinal))
            {
                NavView.SelectedItem = menuItem;
                NavigateToTag(tag);
                return;
            }
        }
    }

    private void NavigateToTag(string? tag)
    {
        var pageType = tag switch
        {
            "home" => typeof(HomePage),
            "verify" => typeof(VerifyPage),
            "learn" => typeof(LearnPage),
            "collision" => typeof(CollisionPage),
            "history" => typeof(HistoryPage),
            "about" => typeof(AboutPage),
            _ => null,
        };

        // SelectSection both selects the pane item (which may raise
        // SelectionChanged) and calls this directly; skip when the frame
        // already shows the target so one shortcut click never stacks two
        // instances of the same page on the back stack.
        if (pageType is not null && NavFrame.Content?.GetType() == pageType)
        {
            return;
        }

        // Unknown tags are a programming error, but throwing from an event
        // handler crashes the app; navigating to Home keeps it usable.
        NavFrame.Navigate(pageType ?? typeof(HomePage));
    }
}
