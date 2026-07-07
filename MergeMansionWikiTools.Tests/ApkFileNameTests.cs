using MergeMansionWikiTools.Services;
using Xunit;

namespace MergeMansionWikiTools.Tests;

/// <summary>
/// Tests for ApkDownloadService.SanitizeApkFileName — Content-Disposition filenames from the
/// APKPure CDN arrive URL-encoded with query junk appended after the extension
/// ("Merge+Mansion%3A+Puzzles+%26+Story_26.06.01_APKPure.xapk&amp;full_screen=true"), which
/// produced files whose extension was not .xapk and broke the "APK exists" detection.
/// </summary>
public class ApkFileNameTests
{
    [Fact]
    public void Decodes_and_truncates_cdn_query_junk()
    {
        var result = ApkDownloadService.SanitizeApkFileName(
            "Merge+Mansion%3A+Puzzles+%26+Story_26.06.01_APKPure.xapk&full_screen=true", "26.06.01");
        // ':' (from %3A) is invalid on Windows → '_'; '+' → space; '&' (from %26) is valid; query junk cut
        Assert.Equal("Merge Mansion_ Puzzles & Story_26.06.01_APKPure.xapk", result);
        Assert.EndsWith(".xapk", result);
    }

    [Fact]
    public void Plain_quoted_filename_is_kept()
        => Assert.Equal("merge-mansion.xapk",
            ApkDownloadService.SanitizeApkFileName("\"merge-mansion.xapk\"", "26.06.01"));

    [Fact]
    public void Apk_extension_is_kept()
        => Assert.Equal("game_26.06.01.apk",
            ApkDownloadService.SanitizeApkFileName("game_26.06.01.apk", "26.06.01"));

    [Fact]
    public void Null_falls_back_to_version_name()
        => Assert.Equal("merge-mansion-26.06.01.xapk",
            ApkDownloadService.SanitizeApkFileName(null, "26.06.01"));

    [Fact]
    public void Name_without_known_extension_falls_back()
        => Assert.Equal("merge-mansion-26.06.01.xapk",
            ApkDownloadService.SanitizeApkFileName("index.html", "26.06.01"));
}
