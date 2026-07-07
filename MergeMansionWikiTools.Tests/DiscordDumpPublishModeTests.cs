using System;
using MergeMansionWikiTools.Services;
using Xunit;

namespace MergeMansionWikiTools.Tests;

/// <summary>
/// Tests for the Discord dump publish decision: NEW post when the data is newer than the
/// last published dump; UPDATE-in-place when the data timestamp matches the newest Discord
/// post but the running MMWT version is newer than the one recorded in that post (the
/// archive + "MMWT Version:" line get replaced on the existing message); otherwise nothing.
/// </summary>
public class DiscordDumpPublishModeTests
{
    private const string Content =
        "New dumps from 07.07.2026 14:56+02:00.\n" +
        "The data itself was created at 2026-07-07T06:37:36.347\n" +
        "Game Version: 26.06.01\n" +
        "MMWT Version: v0.23.52";

    private static DiscordDumpService.LastPublishedInfo Info(
        string createdAt = "2026-07-07T06:37:36.347", string? mmwt = "v0.23.52")
        => new("m1", "c1",
            DateTimeOffset.Parse(createdAt, null, System.Globalization.DateTimeStyles.RoundtripKind),
            mmwt, Content);

    // ── ParseMmwtVersionFromMessage ──

    [Theory]
    [InlineData("MMWT Version: v0.23.52", "v0.23.52")]
    [InlineData("Game Version: 26.06.01\nMMWT Version: v0.23.52\n", "v0.23.52")]
    [InlineData("MMWT Version: v0\\.23\\.52", "v0.23.52")]   // Discord editor escaping
    [InlineData("Game Version: 26.06.01", null)]              // old post without the line
    public void ParseMmwtVersion_cases(string content, string? expected)
        => Assert.Equal(expected, DiscordDumpService.ParseMmwtVersionFromMessage(content));

    // ── DecidePublishMode ──

    [Fact]
    public void Newer_data_means_new_post()
        => Assert.Equal(DiscordDumpService.PublishMode.NewPost,
            DiscordDumpService.DecidePublishMode(
                "2026-07-08T06:00:00.000", Info(), "v0.23.64"));

    [Fact]
    public void Same_data_and_newer_app_means_update()
        => Assert.Equal(DiscordDumpService.PublishMode.UpdateExisting,
            DiscordDumpService.DecidePublishMode(
                "2026-07-07T06:37:36.347", Info(mmwt: "v0.23.52"), "v0.23.64"));

    [Fact]
    public void Same_data_and_same_app_means_none()
        => Assert.Equal(DiscordDumpService.PublishMode.None,
            DiscordDumpService.DecidePublishMode(
                "2026-07-07T06:37:36.347", Info(mmwt: "v0.23.64"), "v0.23.64"));

    [Fact]
    public void Same_data_without_mmwt_line_means_update()
        // old posts (pre-v0.23.38 format) carry no MMWT line — any current version is newer
        => Assert.Equal(DiscordDumpService.PublishMode.UpdateExisting,
            DiscordDumpService.DecidePublishMode(
                "2026-07-07T06:37:36.347", Info(mmwt: null), "v0.23.64"));

    [Fact]
    public void Older_data_means_none()
        => Assert.Equal(DiscordDumpService.PublishMode.None,
            DiscordDumpService.DecidePublishMode(
                "2026-07-06T00:00:00.000", Info(), "v0.23.64"));

    [Fact]
    public void No_previous_publish_means_new_post()
        => Assert.Equal(DiscordDumpService.PublishMode.NewPost,
            DiscordDumpService.DecidePublishMode(
                "2026-07-07T06:37:36.347", null, "v0.23.64"));

    // ── BuildUpdatedContent ──

    [Fact]
    public void Updated_content_replaces_only_the_mmwt_line()
    {
        var updated = DiscordDumpService.BuildUpdatedContent(Content, "v0.23.64");
        Assert.Contains("MMWT Version: v0.23.64", updated);
        Assert.DoesNotContain("v0.23.52", updated);
        // everything else untouched
        Assert.Contains("New dumps from 07.07.2026 14:56+02:00.", updated);
        Assert.Contains("The data itself was created at 2026-07-07T06:37:36.347", updated);
        Assert.Contains("Game Version: 26.06.01", updated);
    }

    [Fact]
    public void Updated_content_appends_line_when_missing()
    {
        var noLine = "New dumps from 07.07.2026 14:56+02:00.\n" +
                     "The data itself was created at 2026-07-07T06:37:36.347";
        var updated = DiscordDumpService.BuildUpdatedContent(noLine, "v0.23.64");
        Assert.Contains("MMWT Version: v0.23.64", updated);
        Assert.StartsWith(noLine, updated);
    }

    // ── ForwardSnapshotMatchesData (stale-forward detection for re-forward) ──

    private static readonly DateTimeOffset TargetTs =
        DateTimeOffset.Parse("2026-07-07T06:37:36.347", null,
            System.Globalization.DateTimeStyles.RoundtripKind);

    [Fact]
    public void Forward_snapshot_matches_same_data_timestamp()
        => Assert.True(DiscordDumpService.ForwardSnapshotMatchesData(Content, TargetTs));

    [Fact]
    public void Forward_snapshot_does_not_match_different_timestamp()
    {
        var other = "The data itself was created at 2026-07-08T00:00:00.000";
        Assert.False(DiscordDumpService.ForwardSnapshotMatchesData(other, TargetTs));
    }

    [Fact]
    public void Forward_snapshot_null_or_empty_does_not_match()
    {
        Assert.False(DiscordDumpService.ForwardSnapshotMatchesData(null, TargetTs));
        Assert.False(DiscordDumpService.ForwardSnapshotMatchesData("", TargetTs));
        Assert.False(DiscordDumpService.ForwardSnapshotMatchesData("no timestamp here", TargetTs));
    }

    [Fact]
    public void Forward_note_is_matchable_for_its_own_cleanup()
    {
        // The note embeds the data timestamp so the NEXT update finds+deletes it. Round-trip:
        // build note for a timestamp → the same matcher used on plain content must match it.
        var note = DiscordDumpService.BuildForwardNote(TargetTs);
        Assert.NotNull(note);
        Assert.Contains("Updated re-upload", note!);
        Assert.True(DiscordDumpService.ForwardSnapshotMatchesData(note, TargetTs));
    }

    [Fact]
    public void Forward_note_null_when_no_timestamp()
        => Assert.Null(DiscordDumpService.BuildForwardNote(null));
}
