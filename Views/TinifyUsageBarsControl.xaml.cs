using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using MergeMansionWikiTools.Services;

namespace MergeMansionWikiTools.Views;

/// <summary>
/// Compact TinyPNG compression usage bar(s).
/// Shows Primary only, Fallback only, or both — based on which key still has capacity.
/// </summary>
public partial class TinifyUsageBarsControl : UserControl
{
    private CancellationTokenSource? _cts;

    public TinifyUsageBarsControl()
    {
        InitializeComponent();
    }

    /// <summary>Starts an async validation check and updates the bar(s).</summary>
    public void Initialize(string key1, string key2 = "")
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        bool hasKey2 = !string.IsNullOrWhiteSpace(key2);

        ShowLoading(gridKey1, barKey1, txtKey1Status, txtKey1Count, "TinyPNG Compression limit:");
        gridKey2.Visibility = Visibility.Collapsed;

        _ = Task.Run(() => CheckAndDisplayAsync(key1, key2, hasKey2, token));
    }

    public void Cancel() => _cts?.Cancel();

    private async Task CheckAndDisplayAsync(string key1, string key2, bool hasKey2, CancellationToken token)
    {
        try
        {
            var (count1, err1) = await TinifyChecker.CheckAsync(key1, token);
            if (token.IsCancellationRequested) return;

            int count2 = 0;
            string? err2 = null;
            if (hasKey2)
            {
                (count2, err2) = await TinifyChecker.CheckAsync(key2, token);
                if (token.IsCancellationRequested) return;
            }

            Dispatcher.Invoke(() => ApplyDisplay(count1, err1, count2, err2, hasKey2));
        }
        catch (OperationCanceledException) { }
    }

    private void ApplyDisplay(int count1, string? err1, int count2, string? err2, bool hasKey2)
    {
        const int limit = 500;
        bool key1Exhausted = err1 == null && count1 >= limit;
        bool key2HasCapacity = hasKey2 && err2 == null && count2 < limit;
        bool key2Exhausted   = hasKey2 && err2 == null && count2 >= limit;

        if (key1Exhausted && key2HasCapacity)
        {
            // Actively on fallback key — show only key2
            gridKey1.Visibility = Visibility.Collapsed;
            ShowUsageFormatted(gridKey2, barKey2, txtKey2Status, txtKey2Count, "secondary", count2, limit);
        }
        else if (key1Exhausted && (key2Exhausted || (hasKey2 && err2 != null)))
        {
            // Both exhausted / both in error — show both
            RenderKey1(count1, err1, limit);
            RenderKey2(count2, err2, limit, hasKey2);
        }
        else
        {
            // Key1 has capacity (or error without key2) — show only key1
            RenderKey1(count1, err1, limit);
            gridKey2.Visibility = Visibility.Collapsed;
        }
    }

    private void RenderKey1(int count, string? err, int limit)
    {
        if (err != null) ShowError(gridKey1, barKey1, txtKey1Status, txtKey1Count, $"Primary: {err}");
        else             ShowUsageFormatted(gridKey1, barKey1, txtKey1Status, txtKey1Count, "primary", count, limit);
    }

    private void RenderKey2(int count, string? err, int limit, bool hasKey2)
    {
        if (!hasKey2) { gridKey2.Visibility = Visibility.Collapsed; return; }
        if (err != null) ShowError(gridKey2, barKey2, txtKey2Status, txtKey2Count, $"Fallback: {err}");
        else             ShowUsageFormatted(gridKey2, barKey2, txtKey2Status, txtKey2Count, "secondary", count, limit);
    }

    private static void ShowLoading(Grid grid, ProgressBar bar, TextBlock status, TextBlock count, string prefix)
    {
        grid.Visibility = Visibility.Visible;
        status.Text = $"{prefix} Checking...";
        status.ClearValue(TextBlock.ForegroundProperty);
        count.Text = "";
        bar.IsIndeterminate = true;
        bar.ClearValue(ProgressBar.ForegroundProperty);
    }

    private static void ShowUsage(Grid grid, ProgressBar bar, TextBlock status, TextBlock count, string prefix, int used, int limit)
    {
        grid.Visibility = Visibility.Visible;
        status.Text = prefix;
        status.ClearValue(TextBlock.ForegroundProperty);
        count.Text = $"{used} / {limit}";
        bar.IsIndeterminate = false;
        bar.Value = used;
        bar.Foreground = TinifyChecker.CreateUsageBrush(used, limit);
    }

    private static void ShowUsageFormatted(Grid grid, ProgressBar bar, TextBlock status, TextBlock count, string keyLabel, int used, int limit)
    {
        grid.Visibility = Visibility.Visible;
        status.Inlines.Clear();
        status.Inlines.Add(new System.Windows.Documents.Run("TinyPNG Compression limit "));
        status.Inlines.Add(new System.Windows.Documents.Run($"({keyLabel})")
        {
            Foreground = (System.Windows.Media.Brush)status.FindResource("TextFillColorTertiaryBrush")
        });
        count.Text = $"{used} / {limit}";
        bar.IsIndeterminate = false;
        bar.Value = used;
        bar.Foreground = TinifyChecker.CreateUsageBrush(used, limit);
    }

    private static void ShowError(Grid grid, ProgressBar bar, TextBlock status, TextBlock count, string message)
    {
        grid.Visibility = Visibility.Visible;
        status.Text = message;
        status.Foreground = new SolidColorBrush(Colors.OrangeRed);
        count.Text = "";
        bar.IsIndeterminate = false;
        bar.Value = 0;
        bar.ClearValue(ProgressBar.ForegroundProperty);
    }
}
