using System.IO;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using static MergeMansionWikiTools.Services.AssetExtractionService;
using Point = SixLabors.ImageSharp.Point;
using Rectangle = SixLabors.ImageSharp.Rectangle;

namespace MergeMansionWikiTools.Services;

/// <summary>
/// Shared image processing utilities used by both Image Optimiser and Flowchart icon extraction.
/// Contains flood-fill detection, column merge, object ordering, canvas sizing, and crop logic.
/// </summary>
internal static class ImageProcessingService
{
    // ── Constants (shared between Image Optimiser and FlowchartImageService) ──

    public const int AlphaThreshold = 5;
    public const int MainAlphaThreshold = 80;
    public const int MinCellArea = 400;

    // ── Flood-Fill Detection ──────────────────────────────────────────

    /// <summary>
    /// Detects objects via flood fill, then merges vertically stacked column parts.
    /// </summary>
    public static List<(Rectangle Full, Rectangle Main)> DetectObjects(Image<Rgba32> img)
    {
        return MergeColumnStacks(DetectObjectsRaw(img));
    }

    /// <summary>Raw flood-fill detection without column merge.</summary>
    public static List<(Rectangle Full, Rectangle Main)> DetectObjectsRaw(Image<Rgba32> img)
    {
        if (img.Width < 130 && img.Height < 130)
            return new List<(Rectangle, Rectangle)>
            {
                (new Rectangle(0, 0, img.Width, img.Height),
                 new Rectangle(0, 0, img.Width, img.Height))
            };

        var visited = new bool[img.Width, img.Height];
        var list = new List<(Rectangle Full, Rectangle Main)>();

        for (int y = 0; y < img.Height; y++)
            for (int x = 0; x < img.Width; x++)
            {
                if (!visited[x, y] && img[x, y].A > AlphaThreshold)
                {
                    var r = FloodFill(img, x, y, visited);
                    if (r.Full.Width * r.Full.Height >= MinCellArea)
                        list.Add(r);
                }
            }

        return list;
    }

    /// <summary>4-connected flood fill returning Full (all pixels) and Main (high-alpha core) bounding boxes.</summary>
    public static (Rectangle Full, Rectangle Main) FloodFill(
        Image<Rgba32> img, int sx, int sy, bool[,] v)
    {
        int x1 = sx, x2 = sx, y1 = sy, y2 = sy;
        int mx1 = int.MaxValue, mx2 = int.MinValue, my1 = int.MaxValue, my2 = int.MinValue;
        bool hasMain = false;

        var q = new Queue<Point>();
        q.Enqueue(new Point(sx, sy));
        v[sx, sy] = true;

        while (q.Count > 0)
        {
            var p = q.Dequeue();
            x1 = Math.Min(x1, p.X); x2 = Math.Max(x2, p.X);
            y1 = Math.Min(y1, p.Y); y2 = Math.Max(y2, p.Y);

            if (img[p.X, p.Y].A >= MainAlphaThreshold)
            {
                mx1 = Math.Min(mx1, p.X); mx2 = Math.Max(mx2, p.X);
                my1 = Math.Min(my1, p.Y); my2 = Math.Max(my2, p.Y);
                hasMain = true;
            }

            foreach (var (dx, dy) in new[] { (0, 1), (0, -1), (1, 0), (-1, 0) })
            {
                int nx = p.X + dx, ny = p.Y + dy;
                if (nx >= 0 && nx < img.Width && ny >= 0 && ny < img.Height
                    && !v[nx, ny] && img[nx, ny].A > AlphaThreshold)
                {
                    v[nx, ny] = true;
                    q.Enqueue(new Point(nx, ny));
                }
            }
        }

        var full = new Rectangle(x1, y1, x2 - x1 + 1, y2 - y1 + 1);
        var main = hasMain ? new Rectangle(mx1, my1, mx2 - mx1 + 1, my2 - my1 + 1) : full;
        return (full, main);
    }

    // ── Column Merge (Union-Find) ─────────────────────────────────────

    /// <summary>
    /// Merges vertically stacked objects that share &gt;40% horizontal overlap.
    /// Safety: only merges if vertical gap ≤ 50% of average width and majority are single-object columns.
    /// </summary>
    public static List<(Rectangle Full, Rectangle Main)> MergeColumnStacks(
        List<(Rectangle Full, Rectangle Main)> objects)
    {
        if (objects.Count <= 1) return objects;

        int n = objects.Count;
        var parent = Enumerable.Range(0, n).ToArray();

        int Find(int x) { while (parent[x] != x) x = parent[x] = parent[parent[x]]; return x; }
        void Unite(int a, int b) { parent[Find(a)] = Find(b); }

        for (int i = 0; i < n; i++)
            for (int j = i + 1; j < n; j++)
            {
                int overlapLeft = Math.Max(objects[i].Full.Left, objects[j].Full.Left);
                int overlapRight = Math.Min(objects[i].Full.Left + objects[i].Full.Width,
                                            objects[j].Full.Left + objects[j].Full.Width);
                int overlap = Math.Max(0, overlapRight - overlapLeft);
                int narrower = Math.Min(objects[i].Full.Width, objects[j].Full.Width);
                if (narrower > 0 && (double)overlap / narrower > 0.4)
                {
                    int iBot = objects[i].Full.Top + objects[i].Full.Height;
                    int jBot = objects[j].Full.Top + objects[j].Full.Height;
                    int vertGap = Math.Max(0, Math.Max(objects[i].Full.Top, objects[j].Full.Top)
                                             - Math.Min(iBot, jBot));
                    double avgW = (objects[i].Full.Width + objects[j].Full.Width) / 2.0;
                    if (vertGap <= avgW * 0.5)
                        Unite(i, j);
                }
            }

        var groups = new Dictionary<int, List<int>>();
        for (int i = 0; i < n; i++)
        {
            int root = Find(i);
            if (!groups.ContainsKey(root)) groups[root] = new();
            groups[root].Add(i);
        }

        int singleCount = groups.Values.Count(g => g.Count == 1);
        if (singleCount * 2 <= groups.Count) return objects;

        var result = new List<(Rectangle Full, Rectangle Main)>();
        foreach (var group in groups.Values)
        {
            if (group.Count == 1)
            {
                result.Add(objects[group[0]]);
                continue;
            }

            int fL = int.MaxValue, fT = int.MaxValue, fR = int.MinValue, fB = int.MinValue;
            int mL = int.MaxValue, mT = int.MaxValue, mR = int.MinValue, mB = int.MinValue;
            foreach (int idx in group)
            {
                var (full, main) = objects[idx];
                fL = Math.Min(fL, full.Left); fT = Math.Min(fT, full.Top);
                fR = Math.Max(fR, full.Left + full.Width); fB = Math.Max(fB, full.Top + full.Height);
                mL = Math.Min(mL, main.Left); mT = Math.Min(mT, main.Top);
                mR = Math.Max(mR, main.Left + main.Width); mB = Math.Max(mB, main.Top + main.Height);
            }
            result.Add((new Rectangle(fL, fT, fR - fL, fB - fT),
                         new Rectangle(mL, mT, mR - mL, mB - mT)));
        }

        return result;
    }

    // ── Object Ordering (reading order) ───────────────────────────────

    /// <summary>Sorts objects in reading order: top-to-bottom rows, left-to-right within each row.</summary>
    public static List<(Rectangle Full, Rectangle Main)> OrderObjects(
        List<(Rectangle Full, Rectangle Main)> objects)
    {
        if (objects.Count <= 1) return objects;
        var rows = SplitIntoObjectRows(objects);
        return rows.SelectMany(r => r.OrderBy(o => o.Full.Left)).ToList();
    }

    /// <summary>Splits objects into visual rows based on vertical center gaps.</summary>
    public static List<List<(Rectangle Full, Rectangle Main)>> SplitIntoObjectRows(
        List<(Rectangle Full, Rectangle Main)> objects)
    {
        var sorted = objects.OrderBy(o => o.Full.Top + o.Full.Height / 2.0).ToList();
        var rows = new List<List<(Rectangle Full, Rectangle Main)>>();
        var currentRow = new List<(Rectangle Full, Rectangle Main)> { sorted[0] };

        for (int i = 1; i < sorted.Count; i++)
        {
            double prevCenter = currentRow.Last().Full.Top + currentRow.Last().Full.Height / 2.0;
            double currCenter = sorted[i].Full.Top + sorted[i].Full.Height / 2.0;
            double gap = currCenter - prevCenter;
            double threshold = Math.Max(currentRow.Last().Full.Height, sorted[i].Full.Height) / 2.0;

            if (gap > threshold)
            {
                rows.Add(currentRow);
                currentRow = new List<(Rectangle Full, Rectangle Main)>();
            }
            currentRow.Add(sorted[i]);
        }
        rows.Add(currentRow);
        return rows;
    }

    // ── Canvas Sizing ─────────────────────────────────────────────────

    /// <summary>Returns the smallest standard canvas size that fits the given dimensions.</summary>
    public static int GetCanvasSize(int w, int h)
    {
        int m = Math.Max(w, h);
        int[] s = { 96, 100, 105, 110, 115, 120, 128, 132, 136, 142, 148, 154, 160, 164, 172, 180, 188, 192, 196, 208, 216, 224, 240, 256, 512, 768, 1024 };
        return s.FirstOrDefault(x => x >= m) == 0 ? 256 : s.First(x => x >= m);
    }

    // ── Crop & Save with rotation ─────────────────────────────────────

    /// <summary>
    /// Crops an object from the source image, applies rotation correction if needed,
    /// centers on a square canvas, and saves as PNG.
    /// </summary>
    public static void CropAndSave(Image<Rgba32> source, Rectangle full, Rectangle main,
        string outPath, float rotation = 0f, bool keepAspectRatio = false)
    {
        using var crop = source.Clone(x => x.Crop(full));

        bool isRotated = rotation != 0f;
        if (isRotated)
            crop.Mutate(x => x.Rotate(rotation));

        // Keep original aspect ratio: save the crop as-is, skipping the square (1:1) canvas centering.
        if (keepAspectRatio)
        {
            using var arFs = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None);
            crop.SaveAsPng(arFs);
            return;
        }

        int size = GetCanvasSize(crop.Width, crop.Height);
        using var canvas = new Image<Rgba32>(size, size);

        int px, py;
        if (isRotated)
        {
            px = (size - crop.Width) / 2;
            py = (size - crop.Height) / 2;
        }
        else
        {
            float cx = (main.Left + main.Right + 1) / 2f;
            float cy = (main.Top + main.Bottom + 1) / 2f;
            px = (int)Math.Round(size / 2f + 1.0f - (cx - full.Left));
            py = (int)Math.Round(size / 2f + 1.0f - (cy - full.Top));
        }

        canvas.Mutate(x => x.DrawImage(crop, new Point(px, py), 1f));

        using var fs = new FileStream(outPath, FileMode.Create, FileAccess.Write, FileShare.None);
        canvas.SaveAsPng(fs);
    }

    // ── Atlas-based Prediction Pipeline ───────────────────────────────

    /// <summary>
    /// Result of atlas-based prediction: per-object level, rotation, and positions.
    /// </summary>
    public record PredictionResult(
        int[] Indices,
        float[] Rotations,
        List<(Rectangle Full, Rectangle Main)> KeptPositions,
        int[] AllPositionLevels,     // one entry per orderedAtlas position (0=skip, >0=level)
        float[] AllPositionRotations // one entry per orderedAtlas position
    );

    /// <summary>
    /// Maps atlas sprites to chain item levels using sprite metadata.
    /// Identical logic to Image Optimiser's PredictFromSpriteMetadata steps 1-6.
    /// </summary>
    public static PredictionResult? PredictFromSpriteMetadata(
        List<SpriteInfo> textureSprites,
        List<SkinMapping> allSkinMappings,
        string textureName,
        List<Models.ParsedItem> chainItems,
        int imageHeight)
    {
        if (textureSprites.Count == 0) return null;

        // Step 1: Sort sprites (Unity Y desc, X asc) — same as Image Optimiser
        var orderedSprites = textureSprites
            .OrderByDescending(s => s.RectY)
            .ThenBy(s => s.RectX)
            .ToList();

        // Step 2: Build sprite index → level mapping
        int[] indices;
        var deterministicResult = SpriteMetadataService.PredictIndicesFromSkinMapping(
            textureSprites, chainItems, allSkinMappings, textureName);

        if (deterministicResult != null && deterministicResult.Any(l => l > 0))
        {
            indices = deterministicResult;
            // Hybrid: fill unmatched with heuristic
            if (indices.Any(l => l == 0))
            {
                var heuristicIndices = SpriteMetadataService.PredictIndices(textureSprites, chainItems);
                var hybridUsed = new HashSet<int>(indices.Where(l => l > 0));
                for (int i = 0; i < indices.Length; i++)
                {
                    if (indices[i] == 0 && heuristicIndices[i] > 0 && !hybridUsed.Contains(heuristicIndices[i]))
                    {
                        indices[i] = heuristicIndices[i];
                        hybridUsed.Add(heuristicIndices[i]);
                    }
                }
            }
        }
        else
        {
            indices = SpriteMetadataService.PredictIndices(textureSprites, chainItems);
        }

        // Step 3: Convert sprite positions to image coordinates (Unity Y → image Y)
        var spriteObjects = orderedSprites.Select(s =>
        {
            int x = (int)s.RectX;
            int y = imageHeight - (int)(s.RectY + s.RectHeight);
            int w = Math.Max(1, (int)s.RectWidth);
            int h = Math.Max(1, (int)s.RectHeight);
            var rect = new Rectangle(x, y, w, h);
            return (Full: rect, Main: rect);
        }).ToList();

        // Step 4: Order atlas sprites, map to levels with deduplication
        var orderedAtlas = OrderObjects(spriteObjects);
        var parts = new List<int>();
        var rotationsList = new List<float>();
        var keptPositions = new List<(Rectangle Full, Rectangle Main)>();
        // Full-position arrays: one entry per orderedAtlas slot (for FlowchartImageService)
        var allLevels = new int[orderedAtlas.Count];
        var allRotations = new float[orderedAtlas.Count];
        var usedLevels = new HashSet<int>();
        var usedSpriteIndices = new HashSet<int>();

        for (int objIdx = 0; objIdx < orderedAtlas.Count; objIdx++)
        {
            var obj = orderedAtlas[objIdx];
            var objCenterX = obj.Full.Left + obj.Full.Width / 2.0;
            var objCenterY = obj.Full.Top + obj.Full.Height / 2.0;

            // Find nearest unmatched sprite
            int bestIdx = -1;
            double bestDist = double.MaxValue;
            for (int i = 0; i < orderedSprites.Count; i++)
            {
                if (usedSpriteIndices.Contains(i)) continue;
                var s = orderedSprites[i];
                var dx = objCenterX - (s.RectX + s.RectWidth / 2.0);
                var dy = objCenterY - (imageHeight - s.RectY - s.RectHeight / 2.0);
                var dist = dx * dx + dy * dy;
                if (dist < bestDist) { bestDist = dist; bestIdx = i; }
            }

            if (bestIdx < 0) { allLevels[objIdx] = 0; continue; }
            usedSpriteIndices.Add(bestIdx);

            int level = indices[bestIdx];
            float rot = orderedSprites[bestIdx].Rotated ? 90f : 0f;

            if (level > 0 && usedLevels.Contains(level))
            {
                allLevels[objIdx] = 0; // duplicate — skip
                continue;
            }

            if (level > 0)
            {
                usedLevels.Add(level);
                parts.Add(level);
            }
            else
            {
                // Unmatched — check overlap with kept sprites
                bool overlapsKept = false;
                foreach (var kept in keptPositions)
                {
                    int oL = Math.Max(obj.Full.Left, kept.Full.Left);
                    int oR = Math.Min(obj.Full.Left + obj.Full.Width, kept.Full.Left + kept.Full.Width);
                    int oT = Math.Max(obj.Full.Top, kept.Full.Top);
                    int oB = Math.Min(obj.Full.Top + obj.Full.Height, kept.Full.Top + kept.Full.Height);

                    if (oR > oL && oB > oT)
                    {
                        int overlapArea = (oR - oL) * (oB - oT);
                        int spriteArea = obj.Full.Width * obj.Full.Height;
                        if (spriteArea > 0 && (double)overlapArea / spriteArea > 0.3)
                        {
                            overlapsKept = true;
                            break;
                        }
                    }
                }
                if (overlapsKept) { allLevels[objIdx] = 0; continue; }

                parts.Add(0); // unmatched marker
            }

            allLevels[objIdx] = level;
            allRotations[objIdx] = rot;
            rotationsList.Add(rot);
            keptPositions.Add(obj);
        }

        return new PredictionResult(parts.ToArray(), rotationsList.ToArray(), keptPositions,
            allLevels, allRotations);
    }

    // ── Adaptive merge/expand (shared with FlowchartImageService) ──

    /// <summary>
    /// Per-row column merge: merges fragments within rows but prevents cross-row merging.
    /// Used when global MergeColumnStacks over-merges objects.
    /// </summary>
    public static List<(Rectangle Full, Rectangle Main)> MergeColumnStacksPerRow(
        List<(Rectangle Full, Rectangle Main)> objects)
    {
        if (objects.Count <= 1) return objects;
        var rows = SplitIntoObjectRows(objects);
        var result = new List<(Rectangle Full, Rectangle Main)>();
        foreach (var row in rows)
            result.AddRange(MergeColumnStacks(row));
        return result;
    }

    /// <summary>
    /// Adapts flood-fill detection count to match expected sprite count.
    /// Identical logic to Image Optimiser's real-time merge/expand.
    /// </summary>
    public static List<(Rectangle Full, Rectangle Main)> AdaptDetectionCount(
        Image<Rgba32> img, int expectedCount)
    {
        var raw = DetectObjectsRaw(img);
        var merged = MergeColumnStacks(raw);

        if (merged.Count == expectedCount)
            return merged;

        if (merged.Count > expectedCount)
            return MergeToExpectedCount(merged, expectedCount);

        // merged.Count < expectedCount — try per-row merge (less aggressive)
        if (raw.Count > merged.Count)
        {
            var perRow = MergeColumnStacksPerRow(raw);
            if (perRow.Count > expectedCount)
                return MergeToExpectedCount(perRow, expectedCount);
            return perRow;
        }

        return merged;
    }

    /// <summary>
    /// Repeatedly merges the best pair of objects within rows until count matches expected.
    /// </summary>
    public static List<(Rectangle Full, Rectangle Main)> MergeToExpectedCount(
        List<(Rectangle Full, Rectangle Main)> objects, int expectedCount)
    {
        if (objects.Count <= expectedCount) return objects;

        var list = new List<(Rectangle Full, Rectangle Main)>(objects);

        while (list.Count > expectedCount)
        {
            var ordered = OrderObjects(list);
            list = ordered;

            var areas = list.Select(o => (double)(o.Full.Width * o.Full.Height)).OrderBy(a => a).ToList();
            double medianW = list.Select(o => (double)o.Full.Width).OrderBy(w => w).ElementAt(list.Count / 2);
            double medianArea = areas[areas.Count / 2];

            // Group into rows
            var rows = new List<List<int>>();
            var currentRow = new List<int> { 0 };
            for (int i = 1; i < list.Count; i++)
            {
                double prevCenter = list[currentRow.Last()].Full.Top + list[currentRow.Last()].Full.Height / 2.0;
                double currCenter = list[i].Full.Top + list[i].Full.Height / 2.0;
                double gap = currCenter - prevCenter;
                double thresh = Math.Max(list[currentRow.Last()].Full.Height, list[i].Full.Height) / 2.0;
                if (gap > thresh) { rows.Add(currentRow); currentRow = new List<int>(); }
                currentRow.Add(i);
            }
            rows.Add(currentRow);

            // Find best pair to merge within rows
            double bestScore = double.MinValue;
            int bestA = -1, bestB = -1;
            foreach (var row in rows)
            {
                for (int ri = 0; ri < row.Count; ri++)
                    for (int rj = ri + 1; rj < row.Count; rj++)
                    {
                        int ai = row[ri], bi = row[rj];
                        var a = list[ai]; var b = list[bi];
                        int xOvlp = Math.Max(0, Math.Min(a.Full.Left + a.Full.Width, b.Full.Left + b.Full.Width) - Math.Max(a.Full.Left, b.Full.Left));
                        int yOvlp = Math.Max(0, Math.Min(a.Full.Top + a.Full.Height, b.Full.Top + b.Full.Height) - Math.Max(a.Full.Top, b.Full.Top));
                        double overlapArea = xOvlp * yOvlp;
                        double smallerArea = Math.Min((double)a.Full.Width * a.Full.Height, (double)b.Full.Width * b.Full.Height);
                        double bboxOverlapRatio = smallerArea > 0 ? overlapArea / smallerArea : 0;
                        double edgeGap = Math.Max(0, Math.Max(a.Full.Left, b.Full.Left) - Math.Min(a.Full.Left + a.Full.Width, b.Full.Left + b.Full.Width));
                        double areaA = a.Full.Width * a.Full.Height;
                        double areaB = b.Full.Width * b.Full.Height;
                        bool fragA = areaA < 0.5 * medianArea;
                        bool fragB = areaB < 0.5 * medianArea;
                        double fragmentScore = (fragA && fragB) ? 2.0 : (fragA || fragB) ? -0.5 : 0.0;
                        double mergedW = Math.Max(a.Full.Left + a.Full.Width, b.Full.Left + b.Full.Width) - Math.Min(a.Full.Left, b.Full.Left);
                        double sizeScore = 1.0 / (1.0 + Math.Abs(mergedW - medianW) / Math.Max(medianW, 1));
                        double gapScore = 1.0 / (1.0 + edgeGap);
                        double score = 10.0 * bboxOverlapRatio + 3.0 * fragmentScore + 2.0 * sizeScore + 1.0 * gapScore;
                        if (score > bestScore) { bestScore = score; bestA = ai; bestB = bi; }
                    }
            }

            if (bestA < 0 || bestB < 0) break;

            var objA = list[bestA]; var objB = list[bestB];
            int fL = Math.Min(objA.Full.Left, objB.Full.Left), fT = Math.Min(objA.Full.Top, objB.Full.Top);
            int fR = Math.Max(objA.Full.Left + objA.Full.Width, objB.Full.Left + objB.Full.Width);
            int fB = Math.Max(objA.Full.Top + objA.Full.Height, objB.Full.Top + objB.Full.Height);
            int mL = Math.Min(objA.Main.Left, objB.Main.Left), mT = Math.Min(objA.Main.Top, objB.Main.Top);
            int mR = Math.Max(objA.Main.Left + objA.Main.Width, objB.Main.Left + objB.Main.Width);
            int mB2 = Math.Max(objA.Main.Top + objA.Main.Height, objB.Main.Top + objB.Main.Height);

            list.RemoveAt(bestB);
            list.RemoveAt(bestA);
            list.Add((new Rectangle(fL, fT, fR - fL, fB - fT), new Rectangle(mL, mT, mR - mL, mB2 - mT)));
        }

        return OrderObjects(list);
    }
}
