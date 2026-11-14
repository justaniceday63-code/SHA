using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Text;
using ShaGui.Core.Hashing;
using ShaGui.Core.History;
using ShaGui.Core.Native;
using ShaGui.Core.Storage;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

namespace ShaGui.App.Pages;

/// <summary>One row in the folder-manifest list. Display-only, hence a plain record.</summary>
public sealed record ManifestRow(string Path, string SizeText, string HashText);

/// <summary>
/// Mode A — Integrity Verification. Text, single file and folder modes share
/// one hashing core; the UI differences are only which input control is
/// visible and how progress is reported.
/// </summary>
public sealed partial class VerifyPage : Page
{
    private ShaAlgo _algo = ShaAlgo.Sha256;
    private string _mode = "text";
    private CancellationTokenSource? _cts;
    private bool _busy;

    public VerifyPage()
    {
        InitializeComponent();

        // The default algorithm comes from persisted settings; selecting the
        // matching chip routes through OnAlgorithmChanged, which keeps _algo
        // and the comparison bar in sync.
        _algo = App.Settings.DefaultAlgorithm;
        AlgoChoice.SelectedIndex = _algo == ShaAlgo.Sha1 ? 0 : 1;

        // SelectionChanged skips its update while the page is still parsing
        // (DigestBox is null), so stamp the result caption here as well.
        ResultLabel.Text = ResultCaption();
    }

    private ShaAlgo SelectedAlgo =>
        // SelectedIndex is reliable inside SelectionChanged; SelectedItem is
        // still null there in this WinUI version (it syncs only after the
        // event dispatches), which silently pinned every switch to SHA-256.
        AlgoChoice.SelectedIndex == 0 ? ShaAlgo.Sha1 : ShaAlgo.Sha256;

    /// <summary>Caption above the digest box, e.g. "SHA-256 hash result".</summary>
    private string ResultCaption() => $"{ShaHasher.AlgorithmName(_algo)} hash result";

    private void OnAlgorithmChanged(object sender, SelectionChangedEventArgs e)
    {
        // RadioButtons raises a transient deselect (SelectedIndex -1) before
        // the real select event when switching; the follow-up event carries
        // the new selection and does the work.
        if (AlgoChoice.SelectedIndex < 0)
        {
            return;
        }

        _algo = SelectedAlgo;

        // SelectionChanged can be raised while the XAML is still parsing
        // (SelectedIndex is set in markup before the later controls exist).
        if (DigestBox is null)
        {
            return;
        }

        // The caption names the algorithm that produces the digest below it.
        ResultLabel.Text = ResultCaption();

        // A digest computed with the previous algorithm must not linger: the
        // page used to keep showing it (and its Match/Mismatch bar) after the
        // radio switched, so SHA-1 and SHA-256 appeared to give the same
        // result. Clear it, then make the page follow the selection:
        //   - text is cheap, so it is re-hashed right away;
        //   - file/folder runs are never re-read silently — a hint asks the
        //     user to press Hash.
        DigestBox.Text = string.Empty;
        ElapsedText.Text = string.Empty;
        ManifestList.Visibility = Visibility.Collapsed;
        UpdateComparison();

        if (_busy)
        {
            return;
        }

        if (_mode == "text" && !string.IsNullOrWhiteSpace(InputText.Text))
        {
            OnHashClicked(this, new RoutedEventArgs());
        }
        else if (_mode is "file" or "folder" && !string.IsNullOrWhiteSpace(
                     _mode == "file" ? FilePathBox.Text : FolderPathBox.Text))
        {
            ShowResult(InfoBarSeverity.Informational, "Algorithm changed",
                $"Now using {ShaHasher.AlgorithmName(_algo)}. Press Hash to recompute.");
        }
    }

    private void OnModeChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        _mode = (sender.SelectedItem as SelectorBarItem)?.Tag as string ?? "text";
        TextPanel.Visibility = _mode == "text" ? Visibility.Visible : Visibility.Collapsed;
        FilePanel.Visibility = _mode == "file" ? Visibility.Visible : Visibility.Collapsed;
        FolderPanel.Visibility = _mode == "folder" ? Visibility.Visible : Visibility.Collapsed;
        ManifestList.Visibility = Visibility.Collapsed;
    }

    private void OnInputTextChanged(object sender, TextChangedEventArgs e) => UpdateComparison();

    private void OnExpectedChanged(object sender, TextChangedEventArgs e) => UpdateComparison();

    // ------------------------------------------------------------- browsing

    private async void OnBrowseFile(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        picker.FileTypeFilter.Add("*");

        var file = await picker.PickSingleFileAsync();
        if (file is not null)
        {
            FilePathBox.Text = file.Path;
        }
    }

    private async void OnBrowseFolder(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        picker.FileTypeFilter.Add("*");

        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            FolderPathBox.Text = folder.Path;
        }
    }

    // -------------------------------------------------------------- hashing

    private async void OnHashClicked(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        SetBusy(true);
        _cts = new CancellationTokenSource();
        try
        {
            switch (_mode)
            {
                case "text":
                    await HashTextAsync(_cts.Token);
                    break;
                case "file":
                    await HashSingleFileAsync(_cts.Token);
                    break;
                case "folder":
                    await HashFolderAsync(_cts.Token);
                    break;
            }
        }
        catch (Exception ex)
        {
            ShowResult(InfoBarSeverity.Error, "Hashing failed", ex.Message);
        }
        finally
        {
            SetBusy(false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    private void OnCancelClicked(object sender, RoutedEventArgs e) => _cts?.Cancel();

    private async Task HashTextAsync(CancellationToken token)
    {
        string text = InputText.Text ?? string.Empty;
        ShaAlgo algo = _algo;

        // Encoded and hashed off the UI thread: a large paste must not freeze
        // the window (the file and folder paths do the same).
        var result = await Task.Run(() => ShaHasher.HashText(algo, text), token);

        ShowDigest(result, $"{result.Hash.Length} bytes · {result.Elapsed.TotalMilliseconds:F1} ms");
        RecordResult(result, RunKind.Text, PreviewText(text), Encoding.UTF8.GetByteCount(text));
    }

    private async Task HashSingleFileAsync(CancellationToken token)
    {
        string path = FilePathBox.Text;
        if (string.IsNullOrWhiteSpace(path))
        {
            ShowResult(InfoBarSeverity.Warning, "No file selected", "Pick a file first.");
            return;
        }

        RunProgress.Visibility = Visibility.Visible;
        var progress = new Progress<HashProgress>(p =>
        {
            RunProgress.Value = p.BytesTotal == 0 ? 0 : (double)p.BytesDone / p.BytesTotal;
        });

        var result = await ShaHasher.HashFileAsync(path, _algo, new HashFileOptions
        {
            BufferSize = App.Settings.BufferSize,
            Progress = p => ((IProgress<HashProgress>)progress).Report(p),
            CancellationToken = token,
        }, token);

        RunProgress.Visibility = Visibility.Collapsed;
        HandleFileResult(result, path);
    }

    private async Task HashFolderAsync(CancellationToken token)
    {
        string path = FolderPathBox.Text;
        if (string.IsNullOrWhiteSpace(path))
        {
            ShowResult(InfoBarSeverity.Warning, "No folder selected", "Pick a folder first.");
            return;
        }

        RunProgress.Visibility = Visibility.Visible;
        var progress = new Progress<ManifestProgress>(p =>
        {
            RunProgress.Value = p.Fraction;
        });

        var algorithm = _algo;
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var manifest = await Task.Run(() => FolderManifestBuilder.Build(path,
            new FolderManifestOptions
            {
                Algorithm = algorithm,
                BufferSize = App.Settings.BufferSize,
                IncludeHidden = App.Settings.IncludeHidden,
                FollowReparsePoints = App.Settings.FollowReparsePoints,
                CancellationToken = token,
                Progress = p => ((IProgress<ManifestProgress>)progress).Report(p),
            }), token);
        clock.Stop();

        RunProgress.Visibility = Visibility.Collapsed;

        DigestBox.Text = manifest.ContentHash();
        ElapsedText.Text = $"{manifest.Entries.Count(e => e.Kind == EntryKind.File)} files · "
            + $"manifest algorithm {manifest.Algorithm} · created {manifest.CreatedUtc:u}";
        ManifestList.ItemsSource = manifest.Entries.Select(e => new ManifestRow(
            e.Path,
            e.Kind == EntryKind.Directory ? "—" : FormatSize(e.Size),
            e.Hash ?? "—")).ToList();
        ManifestList.Visibility = Visibility.Visible;

        if (!manifest.IsComplete)
        {
            var skipped = string.Join("\n", manifest.Skipped.Take(10).Select(s => $"• {s.Reason}: {s.Path}"));
            ShowResult(InfoBarSeverity.Warning,
                manifest.WasCancelled ? "Cancelled — not verified" : "Incomplete — not verified",
                $"{manifest.Skipped.Count} item(s) were skipped:\n{skipped}");
        }
        else
        {
            ShowResult(InfoBarSeverity.Success, "Folder hashed",
                "Every file was read successfully. Compare the manifest hash against a trusted copy.");
        }

        // Folder runs are recorded even when incomplete, with Complete=false,
        // so the History page can show that a run was not a full verification.
        long totalBytes = manifest.Entries
            .Where(e => e.Kind == EntryKind.File)
            .Sum(e => e.Size);
        RecordHistoryEntry(new HistoryEntry
        {
            Algorithm = algorithm,
            Kind = RunKind.Folder,
            Subject = path,
            Bytes = totalBytes,
            Elapsed = clock.Elapsed,
            Digest = manifest.ContentHash(),
            Complete = manifest.IsComplete,
            Status = manifest.IsComplete
                ? ShaStatus.Ok
                : manifest.WasCancelled ? ShaStatus.Cancelled : ShaStatus.Io,
        });

        UpdateComparison();
    }

    private void HandleFileResult(HashResult result, string path)
    {
        if (!result.Success)
        {
            ShowResult(InfoBarSeverity.Error, "Could not hash file", Describe(result.Status));
            DigestBox.Text = string.Empty;
            ElapsedText.Text = string.Empty;
            return;
        }

        var size = new FileInfo(path).Length;
        ShowDigest(result, $"{FormatSize(size)} · {result.Elapsed.TotalMilliseconds:F1} ms "
            + $"({Throughput(size, result.Elapsed)} MB/s)");
    }

    private void ShowDigest(HashResult result, string detail)
    {
        DigestBox.Text = result.Hex;
        ElapsedText.Text = detail;
        UpdateComparison();
    }

    private void UpdateComparison()
    {
        string expected = (ExpectedBox.Text ?? string.Empty).Trim();
        if (expected.Length == 0)
        {
            ResultBar.IsOpen = false;
            return;
        }

        string actual = DigestBox.Text ?? string.Empty;
        if (actual.Length == 0)
        {
            ResultBar.IsOpen = false;
            return;
        }

        if (string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            ShowResult(InfoBarSeverity.Success, "Match",
                "The computed digest matches the expected value.");
        }
        else
        {
            ShowResult(InfoBarSeverity.Error, "Mismatch",
                "The computed digest does not match the expected value. "
                + "Check the algorithm, then re-read the source.");
        }
    }

    private void ShowResult(InfoBarSeverity severity, string title, string message)
    {
        ResultBar.Severity = severity;
        ResultBar.Title = title;
        ResultBar.Message = message;
        ResultBar.IsOpen = true;
    }

    // ------------------------------------------------------------- clipboard

    /// <summary>Copies the digest currently shown in the result box.</summary>
    private void OnCopyDigest(object sender, RoutedEventArgs e)
    {
        string digest = DigestBox.Text;
        if (string.IsNullOrWhiteSpace(digest))
        {
            return;
        }

        var package = new DataPackage();
        package.SetText(digest);
        try
        {
            Clipboard.SetContent(package);
            // Release the package so the digest stays on the clipboard after
            // the app closes, the way other Copy buttons on Windows behave.
            Clipboard.Flush();
        }
        catch
        {
            // Another app can hold the clipboard open; a copy convenience
            // must never turn into a crash.
        }
    }

    // ------------------------------------------------------------- history

    /// <summary>Records a successful in-memory or file run, when history is enabled.</summary>
    private void RecordResult(HashResult result, RunKind kind, string subject, long bytes)
    {
        if (!result.Success)
        {
            return;
        }

        RecordHistoryEntry(new HistoryEntry
        {
            Algorithm = _algo,
            Kind = kind,
            Subject = subject,
            Bytes = bytes,
            Elapsed = result.Elapsed,
            Digest = result.Hex,
        });
    }

    /// <summary>
    /// Writes one run to local history. History is a convenience, never a
    /// verifier: a disabled or unavailable store is skipped and a storage
    /// failure is swallowed, so it can never turn a good hash into an error.
    /// </summary>
    private static void RecordHistoryEntry(HistoryEntry entry)
    {
        if (!App.Settings.RecordHistory || App.Database is not { } db)
        {
            return;
        }

        try
        {
            db.History.Add(entry);
        }
        catch
        {
            // The digest is already on screen; losing a history row is not
            // worth surfacing a failure for.
        }
    }

    /// <summary>Short, single-line label for the text that was hashed.</summary>
    private static string PreviewText(string text)
    {
        string flat = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (flat.Length == 0)
        {
            return "(empty text)";
        }
        return flat.Length <= 80 ? flat : flat[..77] + "…";
    }

    private static string Describe(ShaStatus status) => status switch
    {
        ShaStatus.Io => "The file could not be opened for reading.",
        ShaStatus.Access => "Access denied. The file is locked or you lack permission.",
        ShaStatus.Changed => "The file changed while it was being hashed. Re-run to get a stable digest.",
        ShaStatus.Cancelled => "Cancelled. No digest was produced.",
        ShaStatus.PathTooLong => "The path exceeds the platform limit.",
        _ => status.ToString(),
    };

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
    };

    private static double Throughput(long bytes, TimeSpan elapsed)
        => elapsed.TotalSeconds <= 0 ? 0 : bytes / (1024.0 * 1024) / elapsed.TotalSeconds;

    private void SetBusy(bool busy)
    {
        _busy = busy;
        HashButton.IsEnabled = !busy;
        CancelButton.IsEnabled = busy;
        BusyRing.IsActive = busy;
        if (!busy)
        {
            RunProgress.Visibility = Visibility.Collapsed;
        }
    }
}
