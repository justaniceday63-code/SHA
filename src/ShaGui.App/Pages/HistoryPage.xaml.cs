using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ShaGui.Core.History;

namespace ShaGui.App.Pages;

/// <summary>One history row (display-only).</summary>
public sealed record HistoryRow(string When, string Algorithm, string Subject, string Size, string Throughput);

/// <summary>One bar in the throughput chart (display-only).</summary>
public sealed record ChartRow(string Label, double Fraction, string ValueText);

/// <summary>
/// History &amp; benchmarks: every finished run is stored in a local SQLite
/// database (<see cref="ShaGui.Core.Data.AppDatabase"/>). The list and the
/// chart are both projections of the same rows, so they can never disagree.
/// </summary>
public sealed partial class HistoryPage : Page
{
    /// <summary>How many bars the chart draws (the whole list can be much longer).</summary>
    private const int ChartRows = 20;

    public HistoryPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => Refresh();

    private void Refresh()
    {
        if (App.Database is not { } db)
        {
            RunList.ItemsSource = Array.Empty<HistoryRow>();
            ChartList.ItemsSource = Array.Empty<ChartRow>();
            CountText.Text = "history unavailable";
            BenchmarkSummary.Text = string.Empty;
            HistoryStatus.Severity = InfoBarSeverity.Warning;
            HistoryStatus.Title = "History storage unavailable";
            HistoryStatus.Message = App.DatabaseError
                ?? "The local database could not be opened. Hashing still works.";
            HistoryStatus.IsOpen = true;
            return;
        }

        IReadOnlyList<HistoryEntry> runs;
        try
        {
            runs = db.History.Recent(500);
        }
        catch (Exception ex)
        {
            HistoryStatus.Severity = InfoBarSeverity.Error;
            HistoryStatus.Title = "Could not read history";
            HistoryStatus.Message = ex.Message;
            HistoryStatus.IsOpen = true;
            return;
        }

        HistoryStatus.IsOpen = false;
        CountText.Text = runs.Count == 1 ? "1 run recorded" : $"{runs.Count} runs recorded";

        RunList.ItemsSource = runs.Select(r => new HistoryRow(
            r.RecordedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
            r.AlgorithmName,
            r.Subject,
            FormatSize(r.Bytes),
            r.ThroughputMiBPerSecond.ToString("F1"))).ToList();

        BuildChart(runs);
    }

    /// <summary>
    /// Bars are scaled to the fastest run so the shape of throughput over
    /// time is visible regardless of absolute speed.
    /// </summary>
    private void BuildChart(IReadOnlyList<HistoryEntry> runs)
    {
        var recent = runs.Where(r => r.ThroughputMiBPerSecond > 0)
            .Take(ChartRows)
            .ToList();

        if (recent.Count == 0)
        {
            ChartList.ItemsSource = Array.Empty<ChartRow>();
            BenchmarkSummary.Text = runs.Count == 0
                ? "No runs yet — hash something to build a benchmark."
                : "Recorded runs did not report a throughput.";
            return;
        }

        double peak = recent.Max(r => r.ThroughputMiBPerSecond);
        ChartList.ItemsSource = recent.Select(r => new ChartRow(
            r.Subject,
            peak <= 0 ? 0 : r.ThroughputMiBPerSecond / peak,
            $"{r.ThroughputMiBPerSecond:F1}")).ToList();

        double mean = recent.Average(r => r.ThroughputMiBPerSecond);
        BenchmarkSummary.Text =
            $"last {recent.Count} run(s) · mean {mean:F1} MiB/s · peak {peak:F1} MiB/s";
    }

    private async void OnClear(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Clear history?",
            Content = "This deletes every recorded run and benchmark. It cannot be undone.",
            PrimaryButtonText = "Clear history",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            App.Database?.History.Clear();
        }
        catch (Exception ex)
        {
            HistoryStatus.Severity = InfoBarSeverity.Error;
            HistoryStatus.Title = "Could not clear history";
            HistoryStatus.Message = ex.Message;
            HistoryStatus.IsOpen = true;
            return;
        }

        Refresh();
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F1} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):F1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):F2} GB",
    };
}
