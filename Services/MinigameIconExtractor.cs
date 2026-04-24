using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AssetsTools.NET;
using AssetsTools.NET.Extra;
using AssetsTools.NET.Texture;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace MergeMansionWikiTools.Services;

/// <summary>
/// Extracts minigame task icons from Unity asset bundles for each discovered theme.
///
/// Fully dynamic: no hardcoded theme names or bundle paths.
///   - Themes are passed in from the caller (enumerated at runtime from
///     <c>config.CardStacks[].Theme</c> + <c>config.CustomTables[].Theme</c>).
///   - Sprite names are generated from candidate patterns (see <see cref="SpriteCandidates"/>).
///   - Bundles to scan are matched against a loose filename pattern
///     (see <see cref="BundlePatterns"/>) — new bundles matching these patterns
///     are picked up automatically when the game adds more minigames.
///
/// Verified candidate coverage (26.03.01):
///   Dollhouse → MapSpot_Icon_DollhouseTask  (uigeneric)
///   Painting  → ui_icon_painting            (uigeneric)
///   Perfumery → MapSpot_Icon_PerfumeryTask  (uigeneric)
///   Card      → MapSpot_Icon_Card           (featuresstackminigamesprites)
///   Book      → MapSpot_Icon_Book           (featuresstackminigamesprites)
///   SpyNotes  → MapSpot_Icon_SpyNotesTask   (featuresstackminigamesprites)
/// </summary>
public static class MinigameIconExtractor
{
    // Bundle filename substrings we consider — first hit wins, ordered by likelihood.
    // New themes added by the game should end up in one of these (both Painting-style
    // UI icons live in uigeneric, minigame-specific sprites live in featuresstack*).
    private static readonly string[] BundlePatterns = new[]
    {
        "uigeneric_assets_all",
        "featuresstackminigamesprites_assets_all",
        "featuresillustrationtask",
        "uiicons_assets_all",
        "uisharedalleventsui_assets_all",
        "scriptableobjectsillustration",
        "scriptableobjectsareaiconslibrary",
    };

    /// <summary>
    /// Generates candidate sprite names for a theme, in priority order.
    /// Patterns inferred from 26.03.01 naming conventions.
    /// </summary>
    private static IEnumerable<string> SpriteCandidates(string theme)
    {
        var t = theme;
        var tl = theme.ToLowerInvariant();
        yield return $"MapSpot_Icon_{t}Task";
        yield return $"MapSpot_Icon_{t}";
        yield return $"ui_icon_{tl}";
        yield return $"ui_icon_{tl}_white";
        yield return $"ui_icon_area_hotspot_{tl}";
        yield return $"Icon_{t}";
        yield return $"{t}_Icon";
    }

    public record ExtractionResult(int Extracted, int Missing, List<string> Warnings, List<string> OutputPaths, Dictionary<string, string> ThemeToFile);

    /// <summary>
    /// Extracts an icon PNG for every theme. Returns per-theme output paths.
    /// </summary>
    /// <param name="gameFilesRoot">Folder containing APK/ and/or Server/ subfolders.</param>
    /// <param name="outputDir">Where to write PNGs.</param>
    /// <param name="tpkPath">classdata.tpk path (Unity type database).</param>
    /// <param name="themes">Themes to extract, typically from SharedGameConfig.</param>
    public static Task<ExtractionResult> ExtractAsync(
        string gameFilesRoot,
        string outputDir,
        string tpkPath,
        IEnumerable<string> themes,
        IProgress<string>? progress = null,
        CancellationToken ct = default)
    {
        return Task.Run(() =>
        {
            var warnings = new List<string>();
            var outPaths = new List<string>();
            var themeToFile = new Dictionary<string, string>(StringComparer.Ordinal);

            Directory.CreateDirectory(outputDir);

            if (!File.Exists(tpkPath))
            {
                warnings.Add($"[FATAL] classdata.tpk not found: {tpkPath}");
                return new ExtractionResult(0, 0, warnings, outPaths, themeToFile);
            }

            // Build lookup: candidateSprite → theme (first theme wins on collision, unlikely).
            // Also keep an ordered unresolved set to short-circuit sprite enumeration once all found.
            var themeList = themes.Where(t => !string.IsNullOrEmpty(t))
                                  .Distinct(StringComparer.Ordinal)
                                  .ToList();
            var unresolved = new HashSet<string>(themeList, StringComparer.Ordinal);

            // spriteName → (preferred theme, candidate rank) — lower rank = higher priority.
            var spriteToTheme = new Dictionary<string, (string Theme, int Rank)>(StringComparer.OrdinalIgnoreCase);
            foreach (var theme in themeList)
            {
                int rank = 0;
                foreach (var cand in SpriteCandidates(theme))
                {
                    // Don't overwrite an earlier (better-ranked) entry if collision.
                    if (!spriteToTheme.ContainsKey(cand))
                        spriteToTheme[cand] = (theme, rank);
                    rank++;
                }
            }

            // Candidate bundles (search APK + Server subdirs for each pattern).
            var bundles = DiscoverBundles(gameFilesRoot);
            progress?.Report($"Scanning {bundles.Count} bundle(s) for {themeList.Count} theme(s)...");

            // PASS 1 — find the best-ranked candidate per theme across ALL bundles.
            // Picking "first-found" wins would let an early bundle's low-priority candidate
            // (e.g. ui_icon_{theme}_white, rank 3) mask a later bundle's high-priority
            // candidate (e.g. MapSpot_Icon_{Theme}Task, rank 0). Scanning everything first
            // and then committing to the best option produces the right visual each time.
            var bestPerTheme = new Dictionary<string, (int Rank, string Bundle, string SpriteName)>(StringComparer.Ordinal);
            foreach (var bundlePath in bundles)
            {
                ct.ThrowIfCancellationRequested();
                if (themeList.All(t => bestPerTheme.TryGetValue(t, out var b) && b.Rank == 0))
                    break; // every theme has the best possible candidate already
                try
                {
                    var am = new AssetsManager();
                    am.LoadClassPackage(tpkPath);
                    var bunInst = am.LoadBundleFile(bundlePath, unpackIfPacked: true);
                    for (int i = 0; i < bunInst.file.BlockAndDirInfo.DirectoryInfos.Count; i++)
                    {
                        AssetsFileInstance? afInst;
                        try { afInst = am.LoadAssetsFileFromBundle(bunInst, i); } catch { continue; }
                        if (afInst?.file == null) continue;
                        try { am.LoadClassDatabaseFromPackage(afInst.file.Metadata.UnityVersion); } catch { }
                        foreach (var si in afInst.file.GetAssetsOfType(AssetClassID.Sprite))
                        {
                            string nm;
                            try { nm = am.GetBaseField(afInst, si)["m_Name"].AsString ?? ""; } catch { continue; }
                            if (!spriteToTheme.TryGetValue(nm, out var tr)) continue;
                            if (bestPerTheme.TryGetValue(tr.Theme, out var existing) && existing.Rank <= tr.Rank) continue;
                            bestPerTheme[tr.Theme] = (tr.Rank, bundlePath, nm);
                        }
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add($"[PASS1-SCAN] {Path.GetFileName(bundlePath)}: {ex.GetType().Name}: {ex.Message}");
                }
            }

            // PASS 2 — extract the chosen sprites, grouped by bundle to open each bundle once.
            foreach (var group in bestPerTheme.Values.GroupBy(b => b.Bundle))
            {
                ct.ThrowIfCancellationRequested();
                // Map: sprite_name → (theme, rank=0) so ExtractFromBundle treats only these as targets.
                var wantedSpriteToTheme = group.ToDictionary(
                    g => g.SpriteName,
                    g => (Theme: bestPerTheme.First(kv => kv.Value.SpriteName == g.SpriteName).Key, Rank: 0),
                    StringComparer.OrdinalIgnoreCase);
                var resolved = new HashSet<string>(wantedSpriteToTheme.Values.Select(v => v.Theme));
                try
                {
                    var hits = ExtractFromBundle(group.Key, tpkPath, outputDir, wantedSpriteToTheme, resolved, warnings, progress, ct);
                    foreach (var (theme, path) in hits)
                    {
                        themeToFile[theme] = path;
                        outPaths.Add(path);
                        unresolved.Remove(theme);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    warnings.Add($"[BUNDLE-ERROR] {Path.GetFileName(group.Key)}: {ex.GetType().Name}: {ex.Message}");
                }
            }

            foreach (var t in unresolved)
                warnings.Add($"[NOT-FOUND] Theme '{t}' — no matching sprite in any scanned bundle (tried: {string.Join(", ", SpriteCandidates(t))})");

            return new ExtractionResult(outPaths.Count, unresolved.Count, warnings, outPaths, themeToFile);
        }, ct);
    }

    /// <summary>
    /// Finds all *.bundle files under gameFilesRoot/APK and /Server whose filename
    /// contains any of the BundlePatterns. First-priority bundles come first.
    /// </summary>
    private static List<string> DiscoverBundles(string gameFilesRoot)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();

        foreach (var pattern in BundlePatterns)
        {
            foreach (var sub in new[] { "APK", "Server" })
            {
                var dir = Path.Combine(gameFilesRoot, sub);
                if (!Directory.Exists(dir)) continue;
                try
                {
                    foreach (var b in Directory.EnumerateFiles(dir, "*.bundle", SearchOption.TopDirectoryOnly))
                    {
                        if (!Path.GetFileName(b).Contains(pattern, StringComparison.OrdinalIgnoreCase)) continue;
                        if (seen.Add(b)) ordered.Add(b);
                    }
                }
                catch { }
            }
        }
        return ordered;
    }

    private record ThemeMatch(string Theme, int Rank, AssetFileInfo SpriteInfo, long TexturePathId, float Rx, float Ry, float Rw, float Rh);

    private static List<(string Theme, string OutPath)> ExtractFromBundle(
        string bundlePath,
        string tpkPath,
        string outputDir,
        Dictionary<string, (string Theme, int Rank)> spriteToTheme,
        HashSet<string> unresolved,
        List<string> warnings,
        IProgress<string>? progress,
        CancellationToken ct)
    {
        var results = new List<(string Theme, string OutPath)>();
        var bundleName = Path.GetFileName(bundlePath);

        var am = new AssetsManager();
        am.LoadClassPackage(tpkPath);

        BundleFileInstance bunInst;
        try { bunInst = am.LoadBundleFile(bundlePath, unpackIfPacked: true); }
        catch (Exception ex)
        {
            warnings.Add($"[LOAD] {bundleName}: {ex.Message}");
            return results;
        }

        // Load every asset file in the bundle so we can look up cross-file texture references
        // (sprites sometimes live in a different assets file than their parent texture).
        var allAssetFiles = new List<AssetsFileInstance>();
        for (int i = 0; i < bunInst.file.BlockAndDirInfo.DirectoryInfos.Count; i++)
        {
            AssetsFileInstance? fi;
            try { fi = am.LoadAssetsFileFromBundle(bunInst, i); } catch { continue; }
            if (fi?.file != null) allAssetFiles.Add(fi);
        }
        foreach (var afInst in allAssetFiles)
        {
            try { am.LoadClassDatabaseFromPackage(afInst.file.Metadata.UnityVersion); } catch { }
        }

        // Build SpriteAtlas fallback lookup: spriteName → (textureRect, parent texture pathId).
        // When a Sprite's m_RD.texture.m_PathID is 0 the sprite is atlas-packed; fetch the
        // real rect + parent texture from the SpriteAtlas' m_PackedSpriteNamesToIndex +
        // m_RenderDataMap arrays.
        var atlasFallback = new Dictionary<string, (long TexPathId, float Rx, float Ry, float Rw, float Rh)>(StringComparer.Ordinal);
        foreach (var afInst in allAssetFiles)
        {
            foreach (var atlas in afInst.file.GetAssetsOfType(AssetClassID.SpriteAtlas))
            {
                try
                {
                    var bf = am.GetBaseField(afInst, atlas);
                    var names = new List<string>();
                    var namesField = bf["m_PackedSpriteNamesToIndex.Array"];
                    if (!namesField.IsDummy)
                        foreach (var c in namesField.Children) names.Add(c.AsString);
                    var mapField = bf["m_RenderDataMap.Array"];
                    if (mapField.IsDummy) continue;
                    int idx = 0;
                    foreach (var entry in mapField.Children)
                    {
                        if (idx >= names.Count) break;
                        var value = entry[1];
                        var rect = value["textureRect"];
                        long texPathId = value["texture"]["m_PathID"].AsLong;
                        var name = names[idx];
                        if (!string.IsNullOrEmpty(name) && !atlasFallback.ContainsKey(name))
                            atlasFallback[name] = (texPathId,
                                rect["x"].AsFloat, rect["y"].AsFloat,
                                rect["width"].AsFloat, rect["height"].AsFloat);
                        idx++;
                    }
                }
                catch (Exception ex)
                {
                    warnings.Add($"[ATLAS-META] {bundleName}: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        // Cross-file texture lookup + decode cache.
        var texturesByPathId = new Dictionary<long, (AssetsFileInstance AfInst, AssetFileInfo Info)>();
        foreach (var afInst in allAssetFiles)
            foreach (var ti in afInst.file.GetAssetsOfType(AssetClassID.Texture2D))
                texturesByPathId[ti.PathId] = (afInst, ti);
        var decoded = new Dictionary<long, Image<Bgra32>?>();

        Image<Bgra32>? Decode(long pathId)
        {
            if (decoded.TryGetValue(pathId, out var c)) return c;
            if (!texturesByPathId.TryGetValue(pathId, out var entry)) { decoded[pathId] = null; return null; }
            try
            {
                var bf = am.GetBaseField(entry.AfInst, entry.Info);
                var tf = TextureFile.ReadTextureFile(bf);
                byte[]? data = null;
                try { data = tf.FillPictureData(entry.AfInst); } catch { }
                if (data == null || data.Length == 0) { tf.SetPictureDataFromBundle(bunInst); data = tf.pictureData; }
                if (data == null || data.Length == 0) { decoded[pathId] = null; return null; }
                var raw = tf.DecodeTextureRaw(data, useBgra: true);
                if (raw == null || raw.Length == 0) { decoded[pathId] = null; return null; }
                var img = Image.LoadPixelData<Bgra32>(raw, tf.m_Width, tf.m_Height);
                img.Mutate(x => x.Flip(FlipMode.Vertical));
                decoded[pathId] = img;
                return img;
            }
            catch (Exception ex)
            {
                warnings.Add($"[DECODE] {bundleName}/tex@{pathId}: {ex.GetType().Name}: {ex.Message}");
                decoded[pathId] = null;
                return null;
            }
        }

        foreach (var afInst in allAssetFiles)
        {
            ct.ThrowIfCancellationRequested();

            // Collect all theme matches in this asset file first. Crop later so we can
            // pick the best candidate (lowest rank) per theme, in case multiple
            // candidate sprite names exist within the same bundle.
            var matchesByTheme = new Dictionary<string, ThemeMatch>(StringComparer.Ordinal);
            foreach (var si in afInst.file.GetAssetsOfType(AssetClassID.Sprite))
            {
                string nm;
                AssetTypeValueField sf;
                try
                {
                    sf = am.GetBaseField(afInst, si);
                    nm = sf["m_Name"].AsString ?? "";
                }
                catch { continue; }
                if (string.IsNullOrEmpty(nm)) continue;

                if (!spriteToTheme.TryGetValue(nm, out var tr)) continue;
                if (!unresolved.Contains(tr.Theme)) continue;

                // Keep the best-ranked candidate per theme.
                if (matchesByTheme.TryGetValue(tr.Theme, out var existing) && existing.Rank <= tr.Rank)
                    continue;

                try
                {
                    var rd = sf["m_RD"];
                    long texPathId = rd["texture"]["m_PathID"].AsLong;
                    var rect = rd["textureRect"];
                    float rx = rect["x"].AsFloat;
                    float ry = rect["y"].AsFloat;
                    float rw = rect["width"].AsFloat;
                    float rh = rect["height"].AsFloat;
                    // Fallback: if the sprite is atlas-packed (no direct texture ref),
                    // grab the real rect + parent tex from the SpriteAtlas metadata.
                    if (texPathId == 0 && atlasFallback.TryGetValue(nm, out var atlasEntry))
                    {
                        texPathId = atlasEntry.TexPathId;
                        rx = atlasEntry.Rx; ry = atlasEntry.Ry; rw = atlasEntry.Rw; rh = atlasEntry.Rh;
                    }
                    matchesByTheme[tr.Theme] = new ThemeMatch(tr.Theme, tr.Rank, si, texPathId, rx, ry, rw, rh);
                }
                catch (Exception ex)
                {
                    warnings.Add($"[SPRITE-META] {bundleName}/{nm} ({tr.Theme}): {ex.GetType().Name}: {ex.Message}");
                }
            }

            foreach (var match in matchesByTheme.Values)
            {
                var parent = Decode(match.TexturePathId);
                if (parent == null)
                {
                    warnings.Add($"[NOTEX] Theme '{match.Theme}' references tex @{match.TexturePathId} — not decodable");
                    continue;
                }

                int cx = (int)Math.Round(match.Rx);
                int cy = parent.Height - (int)Math.Round(match.Ry) - (int)Math.Round(match.Rh);
                int cw = (int)Math.Round(match.Rw);
                int ch = (int)Math.Round(match.Rh);
                cx = Math.Clamp(cx, 0, parent.Width - 1);
                cy = Math.Clamp(cy, 0, parent.Height - 1);
                cw = Math.Clamp(cw, 1, parent.Width - cx);
                ch = Math.Clamp(ch, 1, parent.Height - cy);

                try
                {
                    using var crop = parent.Clone(x => x.Crop(new Rectangle(cx, cy, cw, ch)));
                    var outPath = Path.Combine(outputDir, $"MinigameIcon_{match.Theme}.png");
                    crop.SaveAsPng(outPath);
                    results.Add((match.Theme, outPath));
                    progress?.Report($"  {match.Theme} ({cw}x{ch}) — {Path.GetFileName(outPath)}");
                }
                catch (Exception ex)
                {
                    warnings.Add($"[CROP] Theme '{match.Theme}': {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        foreach (var img in decoded.Values) img?.Dispose();
        return results;
    }

    /// <summary>
    /// Enumerates theme strings visible in SharedGameConfig for CardStacks + CustomTables
    /// using reflection (keeps this extractor independent of the SharedGameConfig stub shape).
    /// </summary>
    public static List<string> DiscoverThemesFromConfig(object sharedGameConfig)
    {
        var themes = new HashSet<string>(StringComparer.Ordinal);
        if (sharedGameConfig == null) return themes.ToList();

        foreach (var libName in new[] { "CardStacks", "CustomTables" })
        {
            var libProp = sharedGameConfig.GetType().GetProperty(libName);
            var lib = libProp?.GetValue(sharedGameConfig);
            if (lib == null) continue;
            var enumMethod = lib.GetType().GetMethod("EnumerateAll");
            var entries = enumMethod?.Invoke(lib, null) as System.Collections.IEnumerable;
            if (entries == null) continue;
            foreach (var kv in entries)
            {
                // kv is KeyValuePair<TKey, IGameConfigData>
                var valueProp = kv.GetType().GetProperty("Value");
                var info = valueProp?.GetValue(kv);
                if (info == null) continue;
                var themeProp = info.GetType().GetProperty("Theme");
                var theme = themeProp?.GetValue(info) as string;
                if (!string.IsNullOrEmpty(theme)) themes.Add(theme);
            }
        }
        return themes.ToList();
    }
}
