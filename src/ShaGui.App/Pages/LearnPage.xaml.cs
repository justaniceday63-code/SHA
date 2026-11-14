using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using ShaGui.Core.Hashing;
using ShaGui.Core.Native;
using System.Numerics;
using System.Text;
using Windows.UI;
using Microsoft.UI.Text;

namespace ShaGui.App.Pages;

/// <summary>One SHA-1 round: the registers A–E after the round (bound by x:Bind in the overview table)
/// plus the raw operands the detail panel substitutes into the equation.</summary>
public sealed class RoundRow
{
    public int Round { get; init; }
    public string A { get; init; } = "";
    public string B { get; init; } = "";
    public string C { get; init; } = "";
    public string D { get; init; } = "";
    public string E { get; init; } = "";
    /// <summary>0..3 — which 20-round function range this round belongs to.</summary>
    public int Phase { get; init; }
    public Brush Accent { get; init; } = null!;
    public uint[] Before { get; init; } = Array.Empty<uint>();
    public uint[] After { get; init; } = Array.Empty<uint>();
    public uint RawRotl5 { get; init; }
    public uint RawF { get; init; }
    public uint RawK { get; init; }
    public uint RawW { get; init; }
    public uint RawTemp { get; init; }
}

/// <summary>One SHA-256 round: the registers a–h after the round (bound by x:Bind in the overview table)
/// plus raw operands for the T1/T2 detail panel.</summary>
public sealed class RoundRow256
{
    public int Round { get; init; }
    public string A { get; init; } = "";
    public string B { get; init; } = "";
    public string C { get; init; } = "";
    public string D { get; init; } = "";
    public string E { get; init; } = "";
    public string F { get; init; } = "";
    public string G { get; init; } = "";
    public string H { get; init; } = "";
    /// <summary>0 = W[i] read from the block, 1 = W[i] computed by the schedule.</summary>
    public int Group { get; init; }
    public Brush Accent { get; init; } = null!;
    public uint[] Before { get; init; } = Array.Empty<uint>();
    public uint[] After { get; init; } = Array.Empty<uint>();
    public uint RawT1 { get; init; }
    public uint RawT2 { get; init; }
    public uint RawS1e { get; init; }
    public uint RawCh { get; init; }
    public uint RawS0a { get; init; }
    public uint RawMaj { get; init; }
    public uint RawK { get; init; }
    public uint RawW { get; init; }
}

/// <summary>
/// 80-round (SHA-1) / 64-round (SHA-256) explorer. The table is built from
/// the native trace: COMPRESSION rows carry the operands (temp/f/K/W or
/// T1/T2/…/K/W in aux[]) and the registers that ENTER the round in
/// words_before; STATE_UPDATE rows carry the registers that LEAVE it in
/// words. Both rows share the same round index, which is how each table row
/// pairs them. Hashing logic itself is untouched — this page only renders.
/// </summary>
public sealed partial class LearnPage : Page
{
    private const int Sha1Rounds = 80;
    private const int Sha256Rounds = 64;

    /// <summary>
    /// Page width (DIP) at which the round table and the detail card sit side
    /// by side; below it they stack. Derived from the widest table (SHA-256:
    /// ~605px) + page padding + gap + the 420px detail card + slack for the
    /// page scrollbar.
    /// </summary>
    // Page-width thresholds for the side-by-side layout: the table is wider for
    // SHA-256 (8 registers), so it needs a wider page before the detail card
    // fits beside it without overflow.
    private const double SideBySideMinWidthSha1 = 920;
    private const double SideBySideMinWidthSha256 = 1120;

    private static readonly FontFamily Mono = new("Consolas");

    /// <summary>Phase colors: SHA-1's four 20-round ranges; SHA-256 reuses the first two as block/computed W[i] markers.</summary>
    private static readonly Brush[] PhaseBrushes = CreatePhaseBrushes();

    private static readonly string[] Sha1PhaseTexts =
    {
        "Rounds 1–20 · function f1: Ch(B,C,D) = (B & C) | (~B & D) · K = 5A827999",
        "Rounds 21–40 · function f2: Parity(B,C,D) = B ^ C ^ D · K = 6ED9EBA1",
        "Rounds 41–60 · function f3: Maj(B,C,D) = (B & C) | (B & D) | (C & D) · K = 8F1BBCDC",
        "Rounds 61–80 · function f4: Parity(B,C,D) = B ^ C ^ D · K = CA62C1D6",
    };

    private const string Sha256PhaseNote =
        "Rounds 1–16 read W[i] straight from the block (blue marker); rounds 17–64 "
        + "compute it (teal). Every round applies Ch(e,f,g) and Maj(a,b,c); only K[i] "
        + "and W[i] change.";

    private ShaAlgo _algo = ShaAlgo.Sha1;
    private ShaTraceSession? _session;
    private bool _suppressTrace;
    /// <summary>Guards programmatic selection updates against event re-entry (slider ↔ table ↔ progress).</summary>
    private bool _syncing;
    private int _selectedRound;
    private int _selectedBlock;
    private int _totalBlocks = 1;
    private List<RoundRow> _rows1 = new();
    private List<RoundRow256> _rows2 = new();

    /// <summary>Last applied layout mode (null until the first SizeChanged).</summary>
    private bool? _sideBySide;

    private readonly Brush _accentBrush;
    private readonly Brush _secondaryBrush;
    private readonly Brush _cardStrokeBrush;
    private readonly Border[] _phaseSegs;
    private readonly Storyboard _detailFade;

    public LearnPage()
    {
        InitializeComponent();

        _accentBrush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        _secondaryBrush = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        _cardStrokeBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];
        _phaseSegs = new[] { PhaseSeg0, PhaseSeg1, PhaseSeg2, PhaseSeg3 };

        // Subtle transition when the selected round changes; kept short and
        // skipped during slider drags so nothing flickers while scrubbing.
        var fade = new DoubleAnimation
        {
            From = 0.55,
            To = 1.0,
            Duration = new Duration(TimeSpan.FromMilliseconds(160)),
        };
        Storyboard.SetTarget(fade, DetailHost);
        Storyboard.SetTargetProperty(fade, "Opacity");
        _detailFade = new Storyboard();
        _detailFade.Children.Add(fade);

        // The native context behind _session is only reclaimed by the
        // SafeHandle finalizer if the page is dropped without Unloaded, so
        // release it explicitly as soon as the page leaves the frame.
        Unloaded += (_, _) => DisposeSession();

        // Side-by-side on wide pages, stacked on narrow ones; the page's own
        // width already excludes the nav pane, so no window math is needed.
        SizeChanged += OnPageSizeChanged;

        // TextChanged fires while InitializeComponent sets Text="abc"; the
        // explicit trace below is authoritative, so suppress that one.
        _suppressTrace = true;
        // OnAlgoChanged can fire before RoundSelect exists (and may not fire
        // again), so seed the SHA-1 round picker here.
        SyncRoundSelector(Sha1Rounds);
        OnTraceClicked(this, new RoutedEventArgs());
        _suppressTrace = false;
    }

    private static Brush[] CreatePhaseBrushes()
    {
        // Chosen to stay legible on the dark theme without looking like
        // status colors (they are ranges, not severities).
        byte[] r = { 0x4D, 0x2E, 0xFF, 0xC7 };
        byte[] g = { 0x9F, 0xC4, 0xB8, 0x92 };
        byte[] b = { 0xFF, 0xB6, 0x4D, 0xEA };
        var brushes = new Brush[4];
        for (int i = 0; i < 4; i++)
        {
            brushes[i] = new SolidColorBrush(Color.FromArgb(0xFF, r[i], g[i], b[i]));
        }
        return brushes;
    }

    private static string Hex(uint value) => value.ToString("X8");

    private void OnAlgoChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        // SelectorBar can raise SelectionChanged while InitializeComponent is
        // still building the controls below it; there is nothing to update yet.
        if (RoundTable1 is null || RoundSelect is null)
        {
            return;
        }

        _algo = (sender.SelectedItem as SelectorBarItem)?.Tag as string == "sha256"
            ? ShaAlgo.Sha256
            : ShaAlgo.Sha1;
        bool sha1 = _algo == ShaAlgo.Sha1;

        // The threshold depends on the algorithm, so re-check the layout now.
        ApplyResponsiveLayout();

        PageTitle.Text = sha1 ? "Learn SHA-1" : "Learn SHA-256";
        PageSubtitle.Text = sha1
            ? "Explore the 80 rounds of SHA-1 compression. See how each step updates the "
              + "registers A, B, C, D and E."
            : "Explore the 64 rounds of SHA-256 compression. See how each step updates the "
              + "eight working registers a, b, c, d, e, f, g and h.";
        InfoDigestLabel.Text = sha1 ? "SHA-1 digest" : "SHA-256 digest";
        RoundTable1.Visibility = sha1 ? Visibility.Visible : Visibility.Collapsed;
        RoundTable256.Visibility = sha1 ? Visibility.Collapsed : Visibility.Visible;
        DetailPanel1.Visibility = sha1 ? Visibility.Visible : Visibility.Collapsed;
        DetailPanel2.Visibility = sha1 ? Visibility.Collapsed : Visibility.Visible;
        PhaseStrip.Visibility = sha1 ? Visibility.Visible : Visibility.Collapsed;
        int rounds = sha1 ? Sha1Rounds : Sha256Rounds;
        RoundBar.Maximum = rounds - 1;
        RoundTotal.Text = $"/ {rounds}";
        SyncRoundSelector(rounds);

        OnTraceClicked(sender, new RoutedEventArgs());
    }

    /// <summary>Rebuilds the round picker (0..count-1) for the current algorithm.</summary>
    private void SyncRoundSelector(int count)
    {
        _syncing = true;
        try
        {
            RoundSelect.Items.Clear();
            for (int i = 0; i < count; i++)
            {
                // 1-based labels to match the table's Round column; the
                // index (not the label) still drives selection.
                RoundSelect.Items.Add((i + 1).ToString());
            }
            RoundSelect.SelectedIndex = Math.Clamp(_selectedRound, 0, count - 1);
        }
        finally
        {
            _syncing = false;
        }
    }

    private void OnTraceInputChanged(object sender, TextChangedEventArgs e)
    {
        // Re-tracing per keystroke is cheap: only the viewed block is traced,
        // so the work stays bounded no matter how long the message grows.
        if (!_suppressTrace)
        {
            OnTraceClicked(sender, new RoutedEventArgs());
        }
    }

    private void OnTraceClicked(object sender, RoutedEventArgs e)
    {
        DisposeSession();

        try
        {
            string text = TraceInput.Text ?? string.Empty;

            // Standard padding (message + 0x80 + zero bytes + 64-bit length,
            // rounded to 512-bit blocks). Only the resulting block COUNT is
            // shown — padding/conversion detail is not this page's focus.
            int utf8Bytes = Encoding.UTF8.GetByteCount(text);
            _totalBlocks = Math.Max(1, (utf8Bytes + 72) / 64);
            if (_selectedBlock >= _totalBlocks)
            {
                _selectedBlock = _totalBlocks - 1;
            }

            _session = new ShaTraceSession(_algo, (uint)_selectedBlock, blockCount: 1);
            _session.UpdateText(text);
            var result = _session.Finalize();

            TraceDigest.Text = result.Hex;
            TraceError.IsOpen = false;
            InfoMessage.Text = text.Length == 0 ? "(empty message)" : text;
            InfoBlocks.Text = _totalBlocks.ToString();
            PopulateBlockSelector();

            if (_algo == ShaAlgo.Sha1)
            {
                _rows1 = BuildRows1(_session.Steps);
                RoundTable1.ItemsSource = _rows1;
            }
            else
            {
                _rows2 = BuildRows2(_session.Steps);
                RoundTable256.ItemsSource = _rows2;
            }

            // Scroll: assigning ItemsSource resets the table's scroll
            // position, so re-centering on the selected round keeps the table
            // synchronized with the detail card on every retrace.
            ApplySelectedRound(_selectedRound, scroll: true, animate: false);
        }
        catch (Exception ex)
        {
            TraceError.Severity = InfoBarSeverity.Error;
            TraceError.Title = "Could not trace";
            TraceError.Message = ex.Message;
            TraceError.IsOpen = true;
            TraceDigest.Text = string.Empty;
            _rows1 = new List<RoundRow>();
            _rows2 = new List<RoundRow256>();
            RoundTable1.ItemsSource = null;
            RoundTable256.ItemsSource = null;
            _syncing = true;
            RoundProgress.Text = "Round — / —";
            RoundPercent.Text = "—";
            RoundBar.Value = 0;
            BtnPrevRound.IsEnabled = false;
            BtnNextRound.IsEnabled = false;
            BtnDetailPrev.IsEnabled = false;
            BtnDetailNext.IsEnabled = false;
            RoundSelect.IsEnabled = false;
            _syncing = false;
        }
    }

    private void PopulateBlockSelector()
    {
        _syncing = true;
        try
        {
            BlockSelector.Items.Clear();
            for (int i = 1; i <= _totalBlocks; i++)
            {
                BlockSelector.Items.Add(i.ToString());
            }
            BlockSelector.SelectedIndex = _selectedBlock;
        }
        finally
        {
            _syncing = false;
        }
    }

    private void OnBlockChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing
            || BlockSelector.SelectedItem is not string choice
            || !int.TryParse(choice, out int oneBased))
        {
            return;
        }

        int block = oneBased - 1;
        if (block == _selectedBlock)
        {
            return;
        }
        _selectedBlock = block;
        OnTraceClicked(sender, new RoutedEventArgs());
    }

    // ---------------------------------------------------------------- rows

    private static List<RoundRow> BuildRows1(IReadOnlyList<ShaTraceStep> steps)
    {
        var comp = new Dictionary<uint, ShaTraceStep>();
        var upd = new Dictionary<uint, ShaTraceStep>();
        foreach (var s in steps)
        {
            if (s.Stage == (uint)ShaTraceStage.Compression)
            {
                comp[s.Round] = s;
            }
            else if (s.Stage == (uint)ShaTraceStage.StateUpdate)
            {
                upd[s.Round] = s;
            }
        }

        var rows = new List<RoundRow>(Sha1Rounds);
        for (uint t = 0; t < Sha1Rounds; t++)
        {
            if (!comp.TryGetValue(t, out var c) || !upd.TryGetValue(t, out var u))
            {
                continue;
            }

            var before = new uint[5];
            var after = new uint[5];
            for (int i = 0; i < 5; i++)
            {
                before[i] = c.WordsBefore[i];
                after[i] = u.Words[i];
            }

            uint rotl5 = BitOperations.RotateLeft(before[0], 5);
            int phase = (int)(t / 20);
            rows.Add(new RoundRow
            {
                // 1-based for display; selection and indexing stay 0-based.
                Round = (int)t + 1,
                A = Hex(after[0]),
                B = Hex(after[1]),
                C = Hex(after[2]),
                D = Hex(after[3]),
                E = Hex(after[4]),
                Phase = phase,
                Accent = PhaseBrushes[phase],
                Before = before,
                After = after,
                RawRotl5 = rotl5,
                RawF = c.Aux[1],
                RawK = c.Aux[2],
                RawW = c.Aux[3],
                RawTemp = c.Aux[0],
            });
        }
        return rows;
    }

    private static List<RoundRow256> BuildRows2(IReadOnlyList<ShaTraceStep> steps)
    {
        var comp = new Dictionary<uint, ShaTraceStep>();
        var upd = new Dictionary<uint, ShaTraceStep>();
        foreach (var s in steps)
        {
            if (s.Stage == (uint)ShaTraceStage.Compression)
            {
                comp[s.Round] = s;
            }
            else if (s.Stage == (uint)ShaTraceStage.StateUpdate)
            {
                upd[s.Round] = s;
            }
        }

        var rows = new List<RoundRow256>(Sha256Rounds);
        for (uint t = 0; t < Sha256Rounds; t++)
        {
            if (!comp.TryGetValue(t, out var c) || !upd.TryGetValue(t, out var u))
            {
                continue;
            }

            var before = new uint[8];
            var after = new uint[8];
            for (int i = 0; i < 8; i++)
            {
                before[i] = c.WordsBefore[i];
                after[i] = u.Words[i];
            }

            int group = t < 16 ? 0 : 1;
            rows.Add(new RoundRow256
            {
                // 1-based for display; selection and indexing stay 0-based.
                Round = (int)t + 1,
                A = Hex(after[0]),
                B = Hex(after[1]),
                C = Hex(after[2]),
                D = Hex(after[3]),
                E = Hex(after[4]),
                F = Hex(after[5]),
                G = Hex(after[6]),
                H = Hex(after[7]),
                Group = group,
                Accent = PhaseBrushes[group],
                Before = before,
                After = after,
                RawT1 = c.Aux[0],
                RawT2 = c.Aux[1],
                RawS0a = c.Aux[2],
                RawCh = c.Aux[3],
                RawS1e = c.Aux[4],
                RawMaj = c.Aux[5],
                RawK = c.Aux[6],
                RawW = c.Aux[7],
            });
        }
        return rows;
    }

    // --------------------------------------------------------- navigation

    private int CurrentRoundCount => _algo == ShaAlgo.Sha1 ? _rows1.Count : _rows2.Count;

    private void ApplySelectedRound(int round, bool scroll, bool animate)
    {
        int count = CurrentRoundCount;
        bool sha1 = _algo == ShaAlgo.Sha1;
        var table = sha1 ? RoundTable1 : RoundTable256;

        if (count == 0)
        {
            _syncing = true;
            RoundProgress.Text = "Round — / —";
            RoundPercent.Text = "—";
            RoundBar.Value = 0;
            BtnPrevRound.IsEnabled = false;
            BtnNextRound.IsEnabled = false;
            BtnDetailPrev.IsEnabled = false;
            BtnDetailNext.IsEnabled = false;
            RoundSelect.IsEnabled = false;
            _syncing = false;
            return;
        }

        round = Math.Clamp(round, 0, count - 1);
        _selectedRound = round;

        _syncing = true;
        RoundSelect.IsEnabled = true;
        if (table.SelectedIndex != round)
        {
            table.SelectedIndex = round;
        }
        if (RoundSelect.SelectedIndex != round)
        {
            RoundSelect.SelectedIndex = round;
        }
        RoundBar.Value = round;
        RoundPercent.Text = count > 1 ? $"{round * 100 / (count - 1)}%" : "0%";
        RoundProgress.Text = $"Round {round + 1} / {count}";
        bool canPrev = round > 0;
        bool canNext = round < count - 1;
        BtnPrevRound.IsEnabled = canPrev;
        BtnNextRound.IsEnabled = canNext;
        BtnDetailPrev.IsEnabled = canPrev;
        BtnDetailNext.IsEnabled = canNext;
        _syncing = false;

        if (scroll && round < table.Items.Count)
        {
            ScrollTableTo(table, round);
        }

        if (sha1)
        {
            UpdateDetail1(_rows1[round]);
        }
        else
        {
            UpdateDetail2(_rows2[round]);
        }

        if (animate)
        {
            _detailFade.Begin();
        }
    }

    /// <summary>
    /// Scrolls the round table so the selected row is visible. ScrollIntoView is a
    /// no-op right after an ItemsSource replacement and during rapid successive
    /// scrolls, so retry once on the dispatcher when the selection still matches.
    /// </summary>
    private void ScrollTableTo(ListView table, int round)
    {
        table.ScrollIntoView(table.Items[round]);
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            if (table.SelectedIndex == round && round < table.Items.Count)
            {
                table.ScrollIntoView(table.Items[round]);
            }
        });
    }

    private void OnRoundSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }
        if (sender is ListView { SelectedIndex: >= 0 } table)
        {
            ApplySelectedRound(table.SelectedIndex, scroll: false, animate: true);
        }
    }

    private void OnRoundSelectChanged(object sender, SelectionChangedEventArgs e)
    {
        // Can fire while the picker is being rebuilt (items cleared/re-added)
        // or before the first trace — nothing to select yet.
        if (_syncing || RoundProgress is null || RoundSelect is null)
        {
            return;
        }
        if (RoundSelect.SelectedIndex >= 0)
        {
            ApplySelectedRound(RoundSelect.SelectedIndex, scroll: true, animate: true);
        }
    }

    private void OnPrevRound(object sender, RoutedEventArgs e)
        => ApplySelectedRound(_selectedRound - 1, scroll: true, animate: true);

    private void OnNextRound(object sender, RoutedEventArgs e)
        => ApplySelectedRound(_selectedRound + 1, scroll: true, animate: true);

    // ------------------------------------------------------------- layout

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs e)
        => ApplyResponsiveLayout();

    /// <summary>
    /// Puts the detail card beside the round table on wide pages and below it
    /// on narrow ones. Uses the page width (nav pane already excluded) rather
    /// than the window width, and only reapplies when the mode actually flips.
    /// </summary>
    private void ApplyResponsiveLayout()
    {
        double min = _algo == ShaAlgo.Sha1 ? SideBySideMinWidthSha1 : SideBySideMinWidthSha256;
        bool sideBySide = ActualWidth >= min;
        if (_sideBySide == sideBySide)
        {
            return;
        }
        _sideBySide = sideBySide;

        if (sideBySide)
        {
            Grid.SetRow(DetailHost, 0);
            Grid.SetColumn(DetailHost, 1);
            DetailHost.Width = 420;
            // Reset: RowSpacing would otherwise add a phantom gap above the
            // empty second row.
            MainGrid.RowSpacing = 0;
        }
        else
        {
            Grid.SetRow(DetailHost, 1);
            Grid.SetColumn(DetailHost, 0);
            // NaN = auto: stretch across the full content width when stacked.
            DetailHost.Width = double.NaN;
            MainGrid.RowSpacing = 16;
        }
    }

    // ------------------------------------------------------------- detail

    private void UpdateDetail1(RoundRow row)
    {
        DetailTitle.Text = $"Round {row.Round}";
        DetailKLine.Text = $"K = {Hex(row.RawK)}";
        DetailStateHeader1.Text = $"Input register state (before round {row.Round})";
        SetRegisterChips(
            DetailState1,
            new[] { "A", "B", "C", "D", "E" },
            row.Before,
            columns: 5,
            automationId: "DetailState1");
        SetLabeledValues(
            DetailComponents1,
            new[] { "ROTL5(A)", "f(B,C,D)", "E", "W[t]", "K[t]" },
            new[] { row.RawRotl5, row.RawF, row.Before[4], row.RawW, row.RawK });

        // Symbolic line first, then the same equation with the round's actual
        // hex values substituted, then the wrapped sum — the spec's requirement
        // to show substituted values, not just the general formula.
        EqSym1.Text = "TEMP = ROTL5(A) + f(B,C,D) + E + W[t] + K[t]  (mod 2³²)";
        EqSub1.Text =
            $"     = {Hex(row.RawRotl5)} + {Hex(row.RawF)} + {Hex(row.Before[4])} + {Hex(row.RawW)} + {Hex(row.RawK)}";
        EqRes1.Text = $"     = {Hex(row.RawTemp)}";

        SetRegisterUpdates(
            DetailUpdate1,
            new[] { "A' = TEMP", "B' = A", "C' = ROTL30(B)", "D' = C", "E' = D" },
            row.Before,
            row.After);

        // Phase comes from the row (computed from the 0-based trace index),
        // not from the displayed round number.
        UpdatePhaseStrip(row.Phase);

        FinalStepPanel1.Visibility = row.Round == Sha1Rounds
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (FinalStepPanel1.Visibility == Visibility.Visible)
        {
            BuildFinalStep(FinalStepText1, row.After, _rows1[0].Before);
        }
    }

    private void UpdateDetail2(RoundRow256 row)
    {
        DetailTitle.Text = $"Round {row.Round}";
        DetailKLine.Text = $"K[i] = {Hex(row.RawK)}";
        DetailStateHeader2.Text = $"Input register state (before round {row.Round})";
        SetRegisterChips(
            DetailState2,
            new[] { "a", "b", "c", "d", "e", "f", "g", "h" },
            row.Before,
            columns: 4,
            automationId: "DetailState2");
        SetLabeledValues(
            DetailComponents2,
            new[] { "Σ1(e)", "Ch(e,f,g)", "h", "W[i]", "K[i]", "Σ0(a)", "Maj(a,b,c)" },
            new[] { row.RawS1e, row.RawCh, row.Before[7], row.RawW, row.RawK, row.RawS0a, row.RawMaj });

        EqSymT1.Text = "T1 = h + Σ1(e) + Ch(e,f,g) + W[i] + K[i]  (mod 2³²)";
        EqSubT1.Text =
            $"     = {Hex(row.Before[7])} + {Hex(row.RawS1e)} + {Hex(row.RawCh)} + {Hex(row.RawW)} + {Hex(row.RawK)}";
        EqResT1.Text = $"     = {Hex(row.RawT1)}";
        EqSymT2.Text = "T2 = Σ0(a) + Maj(a,b,c)  (mod 2³²)";
        EqSubT2.Text = $"     = {Hex(row.RawS0a)} + {Hex(row.RawMaj)}";
        EqResT2.Text = $"     = {Hex(row.RawT2)}";

        SetRegisterUpdates(
            DetailUpdate2,
            new[]
            {
                "a' = T1 + T2", "b' = a", "c' = b", "d' = c",
                "e' = d + T1", "f' = e", "g' = f", "h' = g",
            },
            row.Before,
            row.After);

        // SHA-256 has no 20-round function phases; the badge marks whether
        // W[i] is read from the block or computed by the message schedule.
        int group = row.Group;
        string badge = group == 0 ? "W[i] from block" : "W[i] computed";
        PhaseBadgeText.Text = badge;
        PhaseBadge.BorderBrush = PhaseBrushes[group];
        PhaseBadgeText.Foreground = PhaseBrushes[group];
        DetailAccentBar.Background = PhaseBrushes[group];
        PhaseLabel.Text = Sha256PhaseNote;
        PhaseLabel.Foreground = _secondaryBrush;

        FinalStepPanel2.Visibility = row.Round == Sha256Rounds
            ? Visibility.Visible
            : Visibility.Collapsed;
        if (FinalStepPanel2.Visibility == Visibility.Visible)
        {
            BuildFinalStep(FinalStepText2, row.After, _rows2[0].Before);
        }
    }

    /// <summary>Chip per register: label above the value in a bordered box (mockup section A).</summary>
    private void SetRegisterChips(Grid target, string[] labels, uint[] values, int columns, string automationId)
    {
        target.Children.Clear();
        target.ColumnDefinitions.Clear();
        target.RowDefinitions.Clear();
        for (int c = 0; c < columns; c++)
        {
            target.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        int rows = (labels.Length + columns - 1) / columns;
        for (int r = 0; r < rows; r++)
        {
            target.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        for (int i = 0; i < labels.Length && i < values.Length; i++)
        {
            var chip = new Border
            {
                CornerRadius = new CornerRadius(6),
                // Tight padding + 12px value keeps five SHA-1 chips inside the
                // 420px detail card (70.8 DIP per column) without clipping.
                Padding = new Thickness(6, 5, 6, 5),
                Background = _cardStrokeBrush,
                Child = new StackPanel
                {
                    Spacing = 1,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = labels[i],
                            FontSize = 11,
                            Foreground = _secondaryBrush,
                        },
                        new TextBlock
                        {
                            Text = Hex(values[i]),
                            FontFamily = Mono,
                            FontSize = 12,
                        },
                    },
                },
            };
            Grid.SetRow(chip, i / columns);
            Grid.SetColumn(chip, i % columns);
            target.Children.Add(chip);
        }

        // Panels have no UIA peer, so the chips alone are invisible to assistive
        // tech and to the UI tests: mirror the same values as one
        // "A = 1A2B3C4D B = ..." line on an invisible readout spanning the grid.
        // A single NoWrap line never grows the chip rows, and disabling hit
        // testing keeps it from swallowing pointer input over the chips.
        var pairs = new StringBuilder();
        for (int i = 0; i < labels.Length && i < values.Length; i++)
        {
            if (pairs.Length > 0)
            {
                pairs.Append(' ');
            }
            pairs.Append(labels[i]).Append(" = ").Append(Hex(values[i]));
        }
        var readout = new TextBlock
        {
            Text = pairs.ToString(),
            TextWrapping = TextWrapping.NoWrap,
            Opacity = 0,
            IsHitTestVisible = false,
        };
        AutomationProperties.SetAutomationId(readout, automationId);
        Grid.SetRow(readout, 0);
        Grid.SetColumn(readout, 0);
        Grid.SetRowSpan(readout, rows);
        Grid.SetColumnSpan(readout, columns);
        target.Children.Add(readout);
    }

    /// <summary>One "label = 0xvalue" line per entry; values in accent monospace so inputs stand out.</summary>
    private void SetLabeledValues(TextBlock target, string[] labels, uint[] values)
    {
        target.Inlines.Clear();
        for (int i = 0; i < labels.Length && i < values.Length; i++)
        {
            if (i > 0)
            {
                target.Inlines.Add(new LineBreak());
            }
            target.Inlines.Add(new Run { Text = labels[i] + " = ", Foreground = _secondaryBrush });
            target.Inlines.Add(new Run
            {
                Text = Hex(values[i]),
                FontFamily = Mono,
                Foreground = _accentBrush,
            });
        }
    }

    /// <summary>
    /// Final step of the algorithm: every register of the final round is
    /// added to the value it entered the block with (feed-forward), and the
    /// results are concatenated — for the message's last block that string
    /// is the digest shown above the table.
    /// </summary>
    private void BuildFinalStep(TextBlock target, uint[] finalRegs, uint[] inputRegs)
    {
        target.Inlines.Clear();
        var combined = new uint[finalRegs.Length];
        for (int i = 0; i < finalRegs.Length; i++)
        {
            if (i > 0)
            {
                target.Inlines.Add(new LineBreak());
            }
            combined[i] = unchecked(finalRegs[i] + inputRegs[i]);
            target.Inlines.Add(new Run
            {
                Text = $"H{i} = {Hex(inputRegs[i])} + {Hex(finalRegs[i])} = ",
                FontFamily = Mono,
                Foreground = _secondaryBrush,
            });
            target.Inlines.Add(new Run
            {
                Text = Hex(combined[i]),
                FontFamily = Mono,
                FontWeight = FontWeights.SemiBold,
            });
        }

        var concat = new StringBuilder();
        for (int i = 0; i < combined.Length; i++)
        {
            concat.Append(Hex(combined[i]).ToLowerInvariant());
        }
        string value = concat.ToString();
        bool isDigest = string.Equals(value, TraceDigest.Text, StringComparison.OrdinalIgnoreCase);

        target.Inlines.Add(new LineBreak());
        target.Inlines.Add(new Run
        {
            Text = isDigest ? "digest = " : "block output = ",
            FontFamily = Mono,
            Foreground = _secondaryBrush,
        });
        target.Inlines.Add(new Run
        {
            Text = value,
            FontFamily = Mono,
            FontWeight = FontWeights.SemiBold,
        });
        if (!isDigest)
        {
            target.Inlines.Add(new Run
            {
                Text = " (feeds the next block)",
                FontFamily = Mono,
                Foreground = _secondaryBrush,
            });
        }
    }

    /// <summary>One "rule  before → after" line per register: dim before-value, bold after-value, so old and new state never blur together.</summary>
    private void SetRegisterUpdates(TextBlock target, string[] rules, uint[] before, uint[] after)
    {
        target.Inlines.Clear();
        for (int i = 0; i < rules.Length && i < before.Length; i++)
        {
            if (i > 0)
            {
                target.Inlines.Add(new LineBreak());
            }
            target.Inlines.Add(new Run
            {
                Text = rules[i].PadRight(14) + " ",
                Foreground = _secondaryBrush,
            });
            target.Inlines.Add(new Run
            {
                Text = Hex(before[i]) + " ",
                FontFamily = Mono,
                Foreground = _secondaryBrush,
            });
            target.Inlines.Add(new Run { Text = "→ ", Foreground = _secondaryBrush });
            target.Inlines.Add(new Run
            {
                Text = Hex(after[i]),
                FontFamily = Mono,
                FontWeight = FontWeights.SemiBold,
            });
        }
    }

    private void UpdatePhaseStrip(int phase)
    {
        for (int i = 0; i < _phaseSegs.Length; i++)
        {
            bool active = i == phase;
            _phaseSegs[i].Opacity = active ? 1.0 : 0.4;
            _phaseSegs[i].BorderBrush = active ? PhaseBrushes[i] : _cardStrokeBrush;
            if (_phaseSegs[i].Child is TextBlock label)
            {
                label.Foreground = active ? PhaseBrushes[i] : _secondaryBrush;
                label.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;
            }
        }
        // The label recolors with the phase too, and the card header badge +
        // accent bar mark which 20-round function range the round belongs to.
        string[] badges =
        {
            "Function 1 (1–20)", "Function 2 (21–40)",
            "Function 3 (41–60)", "Function 4 (61–80)",
        };
        PhaseBadgeText.Text = badges[phase];
        PhaseBadge.BorderBrush = PhaseBrushes[phase];
        PhaseBadgeText.Foreground = PhaseBrushes[phase];
        DetailAccentBar.Background = PhaseBrushes[phase];
        PhaseLabel.Text = Sha1PhaseTexts[phase];
        PhaseLabel.Foreground = PhaseBrushes[phase];
    }

    private void DisposeSession()
    {
        _session?.Dispose();
        _session = null;
    }
}
