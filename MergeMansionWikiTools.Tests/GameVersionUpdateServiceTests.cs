using System;
using System.Collections.Generic;
using MergeMansionWikiTools.Services;
using Xunit;

namespace MergeMansionWikiTools.Tests;

/// <summary>
/// Tests for the "offer game version update" decision logic and Discord dump
/// selection used by the game-version update offer flow (GameUpdateDialog).
/// </summary>
public class GameVersionUpdateServiceTests
{
    [Theory]
    [InlineData("26.05.01", "26.06.01", "", true)]           // newer available → offer
    [InlineData("26.06.01", "26.06.01", "", false)]          // same version → no offer
    [InlineData("26.06.01", "26.05.01", "", false)]          // latest older than current → no offer
    [InlineData("26.05.01", "26.06.01", "26.06.01", false)]  // user skipped exactly this version → no offer
    [InlineData("26.05.01", "26.07.01", "26.06.01", true)]   // even newer than the skipped one → offer again
    [InlineData("", "26.06.01", "", false)]                   // no current version set → no offer
    [InlineData("26.05.01", "", "", false)]                   // no latest known → no offer
    [InlineData("26.9.1", "26.10.01", "", true)]              // numeric compare (string compare would say 26.9 > 26.10)
    public void ShouldOfferUpdate_cases(string current, string latest, string declined, bool expected)
        => Assert.Equal(expected,
            GameVersionUpdateService.ShouldOfferUpdate(current, latest, declined));

    // ── PickDumpForVersion / ResolveDumpVersion ──

    private static DiscordDumpDownloadService.DiscordDumpInfo Dump(
        string id, DateTimeOffset ts, string? gameVersion = null,
        DateTimeOffset? createdAt = null,
        string? url = "https://cdn.discordapp.com/x.7z")
        => new(id, ts, createdAt, url, "x.7z", 1, gameVersion);

    private static List<ApkDownloadService.ApkVersionInfo> Versions() =>
    [
        new("26.06.01", "2660", "a", new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero)),
        new("26.05.01", "2650", "b", new DateTimeOffset(2026, 6, 8, 0, 0, 0, TimeSpan.Zero)),
    ];

    [Fact]
    public void ResolveDumpVersion_prefers_comment_over_date()
    {
        // CreatedAt would date-match 26.06.01, but the comment says 26.05.01 — comment wins
        var dump = Dump("1", new DateTimeOffset(2026, 7, 2, 0, 0, 0, TimeSpan.Zero),
            gameVersion: "26.05.01",
            createdAt: new DateTimeOffset(2026, 7, 2, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal("26.05.01",
            DiscordDumpDownloadService.ResolveDumpVersion(dump, Versions()));
    }

    [Fact]
    public void ResolveDumpVersion_falls_back_to_date_match()
    {
        // No "Game Version:" comment; CreatedAt 2026-06-20 sits after the 26.05.01
        // release (Jun 8) and before 26.06.01 (Jul 1) → closest-not-after = 26.05.01
        var dump = Dump("1", new DateTimeOffset(2026, 6, 21, 0, 0, 0, TimeSpan.Zero),
            createdAt: new DateTimeOffset(2026, 6, 20, 0, 0, 0, TimeSpan.Zero));
        Assert.Equal("26.05.01",
            DiscordDumpDownloadService.ResolveDumpVersion(dump, Versions()));
    }

    [Fact]
    public void PickDumpForVersion_picks_first_match_newest_first()
    {
        var newer = Dump("2", new DateTimeOffset(2026, 7, 3, 0, 0, 0, TimeSpan.Zero), "26.06.01");
        var older = Dump("1", new DateTimeOffset(2026, 7, 2, 0, 0, 0, TimeSpan.Zero), "26.06.01");
        var picked = GameVersionUpdateService.PickDumpForVersion(
            [newer, older], "26.06.01", Versions());
        Assert.Equal("2", picked!.MessageId);
    }

    [Fact]
    public void PickDumpForVersion_returns_null_when_no_match()
    {
        var dump = Dump("1", new DateTimeOffset(2026, 7, 2, 0, 0, 0, TimeSpan.Zero), "26.05.01");
        Assert.Null(GameVersionUpdateService.PickDumpForVersion(
            [dump], "26.06.01", Versions()));
    }

    [Fact]
    public void PickDumpForVersion_skips_dump_without_attachment()
    {
        var broken = Dump("2", new DateTimeOffset(2026, 7, 3, 0, 0, 0, TimeSpan.Zero),
            "26.06.01", url: null);
        var ok = Dump("1", new DateTimeOffset(2026, 7, 2, 0, 0, 0, TimeSpan.Zero), "26.06.01");
        var picked = GameVersionUpdateService.PickDumpForVersion(
            [broken, ok], "26.06.01", Versions());
        Assert.Equal("1", picked!.MessageId);
    }
}
