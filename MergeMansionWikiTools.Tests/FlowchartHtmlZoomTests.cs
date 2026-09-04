using MergeMansionWikiTools.Services;
using Xunit;

namespace MergeMansionWikiTools.Tests;

/// <summary>
/// Interactive Discord HTML flowcharts must be zoomable on mobile. The original wrapper
/// (≤ v0.24.46) used width=device-width viewport + an overflow:auto scroll container, so
/// the page itself always "fit" the phone screen and native pinch-zoom-out was clamped
/// at 1.0 — the user saw a single 280px node and could not zoom out.
///
/// Current approach (v0.24.49, experiment): NATIVE browser zoom — the page is as large
/// as the SVG (no clipping scroll container, no overflow:hidden), so the mobile browser
/// itself allows pinch-zoom-out to fit the content. The custom pan+zoom JS from
/// v0.24.47-48 is commented out in the generator for a possible revert.
/// </summary>
public class FlowchartHtmlZoomTests
{
    private const string SampleSvg =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" xmlns:xlink=\"http://www.w3.org/1999/xlink\" version=\"1.1\"\n" +
        "     width=\"1600\" height=\"9000\"\n" +
        "     viewBox=\"0 0 1600 9000\">\n</svg>";

    private static string Wrap() =>
        FlowchartService.WrapAsInteractiveHtml(SampleSvg, "Test Area", 5);

    [Fact]
    public void Html_PageIsNotClippedToScreen()
    {
        var html = Wrap();
        // Native pinch-zoom-out only works when the PAGE itself is as large as the SVG:
        // no overflow:hidden on body, no overflow:auto scroll box hiding the content size.
        Assert.DoesNotContain("overflow: hidden", html);
        Assert.DoesNotContain("overflow: auto", html);
    }

    [Fact]
    public void Html_ViewportAllowsZoomingOut()
    {
        var html = Wrap();
        Assert.Contains("minimum-scale=0.1", html);
    }

    [Fact]
    public void Html_CustomZoomIsDisabled()
    {
        var html = Wrap();
        // The v0.24.47-48 custom pan+zoom must NOT be emitted while the native
        // browser-zoom experiment runs (generator keeps it commented out).
        Assert.DoesNotContain("btn-zoom-in", html);
        Assert.DoesNotContain("touch-action", html);
        Assert.DoesNotContain("requestAnimationFrame", html);
    }

    [Fact]
    public void Html_KeepsTaskTrackingIntact()
    {
        var html = Wrap();
        Assert.Contains("data-node-idx", html);
        Assert.Contains("localStorage", html);
        Assert.Contains("btn-reset", html);
    }
}
