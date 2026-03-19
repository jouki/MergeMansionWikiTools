using System.Windows.Media;
using TinifyAPI;

namespace MergeMansionWikiTools.Services;

/// <summary>Shared TinyPNG validation helper — serialises access to the static Tinify.Key.</summary>
internal static class TinifyChecker
{
    // Track-space gradient (position 0–1 = 0%–100% of limit)
    // Teal → soft yellow at 55 % → orange at 65–75 % → red at 90 %
    private static readonly (double Pos, Color Col)[] TrackGradient =
    [
        (0.00, Color.FromRgb( 59, 200, 200)),  // teal
        (0.50, Color.FromRgb( 59, 200, 200)),  // hold teal until 50 %
        (0.60, Color.FromRgb(255, 185,  30)),  // soft amber/yellow
        (0.75, Color.FromRgb(255, 140,   0)),  // orange
        (0.90, Color.FromRgb(255, 140,   0)),  // hold orange until 90 %
        (1.00, Color.FromRgb(220,  20,  60)),  // red
    ];

    /// <summary>Shared semaphore — prevents concurrent writes to the static Tinify.Key.</summary>
    public static readonly SemaphoreSlim Semaphore = new(1, 1);

    /// <summary>Validates a TinyPNG key and returns its current monthly compression count.</summary>
    public static async Task<(int Count, string? Error)> CheckAsync(string key, CancellationToken token = default)
    {
        await Semaphore.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            Tinify.Key = key;
            await Tinify.Validate();
            return ((int)(Tinify.CompressionCount ?? 0), null);
        }
        catch (OperationCanceledException) { throw; }
        catch (AccountException) { return (0, "Invalid API key"); }
        catch (Exception)        { return (0, "Connection error"); }
        finally { Semaphore.Release(); }
    }

    /// <summary>
    /// Creates a LinearGradientBrush that shows the correct portion of the teal→orange→red
    /// spectrum for the given fill fraction, so the colour matches the actual track position.
    /// </summary>
    public static LinearGradientBrush CreateUsageBrush(int used, int maximum = 500)
    {
        double fraction = Math.Clamp((double)used / maximum, 0.001, 1.0);
        var brush = new LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0.5),
            EndPoint   = new System.Windows.Point(1, 0.5),
        };

        bool boundaryAdded = false;
        foreach (var (pos, col) in TrackGradient)
        {
            double indicatorPos = pos / fraction;
            if (indicatorPos <= 1.0)
            {
                brush.GradientStops.Add(new GradientStop(col, indicatorPos));
            }
            else
            {
                // Track position is past current fill — add interpolated boundary colour
                brush.GradientStops.Add(new GradientStop(SampleGradient(fraction), 1.0));
                boundaryAdded = true;
                break;
            }
        }

        if (!boundaryAdded)
        {
            // All stops fit (fraction ≈ 1.0) — ensure last stop reaches 1.0
            var last = brush.GradientStops[^1];
            if (last.Offset < 1.0)
                brush.GradientStops.Add(new GradientStop(last.Color, 1.0));
        }

        return brush;
    }

    private static Color SampleGradient(double t)
    {
        if (t <= TrackGradient[0].Pos) return TrackGradient[0].Col;
        if (t >= TrackGradient[^1].Pos) return TrackGradient[^1].Col;
        for (int i = 0; i < TrackGradient.Length - 1; i++)
        {
            if (t <= TrackGradient[i + 1].Pos)
            {
                double range = TrackGradient[i + 1].Pos - TrackGradient[i].Pos;
                double localT = range == 0 ? 0 : (t - TrackGradient[i].Pos) / range;
                Color a = TrackGradient[i].Col, b = TrackGradient[i + 1].Col;
                return Color.FromRgb(
                    (byte)(a.R + (b.R - a.R) * localT),
                    (byte)(a.G + (b.G - a.G) * localT),
                    (byte)(a.B + (b.B - a.B) * localT));
            }
        }
        return TrackGradient[^1].Col;
    }
}
