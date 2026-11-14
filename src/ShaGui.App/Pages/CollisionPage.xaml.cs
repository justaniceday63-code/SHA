using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ShaGui.Core.Hashing;
using ShaGui.Core.Native;

namespace ShaGui.App.Pages;

/// <summary>
/// SHAttered demonstration: the two bundled PDFs must show identical SHA-1
/// and different SHA-256. Bundled as assets and hashed at runtime — no
/// digests are hard-coded, so the page proves the backend rather than
/// reciting a constant.
/// </summary>
public sealed partial class CollisionPage : Page
{
    public const string ExpectedSha1 = "38762cf7f55934b34d179ae6a4c80cadccbb7f0a";

    private string? _leftPath;
    private string? _rightPath;

    // SHA-1 digest each side produced once it was hashed successfully
    // (null until then, and after a failed hash). The verdict claims
    // something about BOTH files, so these plus both revealed sections
    // are what unlock DemoStatus — see UpdateCollisionBanner.
    private string? _leftSha1Hex;
    private string? _rightSha1Hex;

    public CollisionPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _leftPath = await ResolveAssetAsync("shattered-1.pdf");
        _rightPath = await ResolveAssetAsync("shattered-2.pdf");

        if (_leftPath is null || _rightPath is null)
        {
            DemoStatus.Severity = InfoBarSeverity.Warning;
            DemoStatus.Title = "Demo files not bundled";
            DemoStatus.Message = "Expected Assets/Collision/shattered-1.pdf and shattered-2.pdf. "
                + "Run scripts/fetch-shattered.ps1 to download them.";
            DemoStatus.IsOpen = true;
            return;
        }

        LeftSize.Text = Describe(_leftPath);
        RightSize.Text = Describe(_rightPath);
        HashBoth();
    }

    // The hash section starts collapsed; pressing this card's button reveals
    // it and runs the same hash path as before, then lets the verdict
    // reconsider whether both sides are on screen now.
    private async void OnHashLeft(object sender, RoutedEventArgs e)
    {
        LeftHashSection.Visibility = Visibility.Visible;
        _leftSha1Hex = await HashFileAsync(_leftPath, LeftSha1, LeftSha256);
        UpdateCollisionBanner();
    }

    private async void OnHashRight(object sender, RoutedEventArgs e)
    {
        RightHashSection.Visibility = Visibility.Visible;
        _rightSha1Hex = await HashFileAsync(_rightPath, RightSha1, RightSha256);
        UpdateCollisionBanner();
    }

    private async void OnViewLeft(object sender, RoutedEventArgs e) => await ViewPdfAsync(_leftPath, "shattered-1.pdf");
    private async void OnViewRight(object sender, RoutedEventArgs e) => await ViewPdfAsync(_rightPath, "shattered-2.pdf");

    /// <summary>
    /// Copies the bundled PDF into app storage (a managed copy under
    /// %LOCALAPPDATA%\ShaGui\Collision) and opens it in the user's default
    /// PDF viewer — "stored first, shown on demand".
    /// </summary>
    private async Task ViewPdfAsync(string? sourcePath, string name)
    {
        if (sourcePath is null)
        {
            DemoStatus.Severity = InfoBarSeverity.Warning;
            DemoStatus.Title = "Demo file not available";
            DemoStatus.Message = "The bundled PDF could not be located on disk.";
            DemoStatus.IsOpen = true;
            return;
        }

        try
        {
            string dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ShaGui", "Collision");
            Directory.CreateDirectory(dir);
            string stored = Path.Combine(dir, name);

            // Copy once; refresh if the stored copy no longer matches the
            // bundled asset's size (i.e. the demo assets were updated).
            var source = new FileInfo(sourcePath);
            if (!File.Exists(stored) || new FileInfo(stored).Length != source.Length)
            {
                File.Copy(sourcePath, stored, overwrite: true);
            }

            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(stored);
            bool opened = await Windows.System.Launcher.LaunchFileAsync(file);

            DemoStatus.Severity = opened ? InfoBarSeverity.Informational : InfoBarSeverity.Warning;
            DemoStatus.Title = opened ? $"Opened {name}" : "No PDF viewer found";
            DemoStatus.Message = opened
                ? $"Stored at {stored} and opened in your default PDF viewer."
                : "Install a PDF viewer (any browser works) to inspect the file.";
            DemoStatus.IsOpen = true;
        }
        catch (Exception ex)
        {
            DemoStatus.Severity = InfoBarSeverity.Error;
            DemoStatus.Title = "Could not open the PDF";
            DemoStatus.Message = ex.Message;
            DemoStatus.IsOpen = true;
        }
    }

    // Pre-fills both digest cells on load. The sections stay collapsed and
    // UpdateCollisionBanner refuses to speak until both are revealed, so
    // this alone never raises the banner.
    private void HashBoth()
    {
        _ = HashLeftAsync();
        _ = HashRightAsync();
    }

    private async Task HashLeftAsync()
    {
        _leftSha1Hex = await HashFileAsync(_leftPath, LeftSha1, LeftSha256);
        UpdateCollisionBanner();
    }

    private async Task HashRightAsync()
    {
        _rightSha1Hex = await HashFileAsync(_rightPath, RightSha1, RightSha256);
        UpdateCollisionBanner();
    }

    /// <summary>
    /// Hashes one file into the two digest cells and returns its SHA-1 hex,
    /// or null when the file could not be hashed — the caller records which
    /// side has a real digest.
    /// </summary>
    private async Task<string?> HashFileAsync(string? path, TextBlock sha1Block, TextBlock sha256Block)
    {
        if (path is null)
        {
            return null;
        }

        var sha1 = await ShaHasher.HashFileAsync(path, ShaAlgo.Sha1);
        var sha256 = await ShaHasher.HashFileAsync(path, ShaAlgo.Sha256);

        // The algorithm labels are static left-column TextBlocks in XAML now,
        // so the value cells carry only the digest (or the error). The
        // ExpectedSha1 comparison lives in UpdateCollisionBanner, which owns
        // the verdict.
        sha1Block.Text = sha1.Success ? sha1.Hex : $"error: {sha1.Status}";
        sha256Block.Text = sha256.Success ? sha256.Hex : $"error: {sha256.Status}";

        return sha1.Success ? sha1.Hex : null;
    }

    /// <summary>
    /// Raises the collision verdict, but only once BOTH files have been
    /// hashed on screen: the message asserts something about two files, so
    /// one revealed section — or the hidden pre-fill — is not enough.
    /// </summary>
    private void UpdateCollisionBanner()
    {
        if (LeftHashSection.Visibility != Visibility.Visible ||
            RightHashSection.Visibility != Visibility.Visible ||
            _leftSha1Hex is null || _rightSha1Hex is null)
        {
            return;
        }

        if (_leftSha1Hex == ExpectedSha1 && _rightSha1Hex == ExpectedSha1)
        {
            DemoStatus.Severity = InfoBarSeverity.Success;
            DemoStatus.Title = "Both files report the published SHA-1 collision digest";
            DemoStatus.Message = $"{ExpectedSha1} — while the SHA-256 digests differ.";
            DemoStatus.IsOpen = true;
        }
        else
        {
            DemoStatus.Severity = InfoBarSeverity.Warning;
            DemoStatus.Title = "Unexpected SHA-1 digest";
            DemoStatus.Message = "The bundled file does not match the published collision, "
                + "so the assets may be corrupted or replaced.";
            DemoStatus.IsOpen = true;
        }
    }

    private static string Describe(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return $"{info.Length:N0} bytes";
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Resolves a bundled asset from the install location first (packaged
    /// apps run from WindowsApps), then from the source tree for unpackaged
    /// development runs.
    /// </summary>
    private static Task<string?> ResolveAssetAsync(string name)
    {
        string install = Path.Combine(AppContext.BaseDirectory, "Assets", "Collision", name);
        if (File.Exists(install))
        {
            return Task.FromResult<string?>(install);
        }

        // Development fallback: walk up to the repository root.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "src", "ShaGui.App", "Assets", "Collision", name);
            if (File.Exists(candidate))
            {
                return Task.FromResult<string?>(candidate);
            }
        }
        return Task.FromResult<string?>(null);
    }
}
