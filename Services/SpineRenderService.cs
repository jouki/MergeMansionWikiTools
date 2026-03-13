using System.IO;
using System.Text.Json;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace MergeMansionWikiTools.Services;

/// <summary>
/// Renders Spine skeleton icons from atlas + skeleton JSON + texture PNG.
/// Hybrid approach: whole-image affine for regions, triangle rasterization for meshes.
/// </summary>
public static class SpineRenderService
{
    private static readonly HashSet<string> AlwaysSkip = ["DustCloud2"];

    private record AtlasRegion(
        int X, int Y, int W, int H,
        int OrigW, int OrigH, int OffsetX, int OffsetY,
        bool Rotated);

    private record BoneXform(double A, double B, double C, double D, double Wx, double Wy);

    // ── Public API ──────────────────────────────────────────────────────

    public static Image<Rgba32>? RenderIcon(
        string skelJson, string atlasText, Image<Rgba32> texture,
        string animName = "Select", int canvasSize = 256, float padding = 0.03f)
    {
        var atlas = ParseAtlas(atlasText);
        var tex = texture.Clone();
        UnPremultiply(tex);

        JsonElement skel;
        try { skel = JsonDocument.Parse(skelJson).RootElement; }
        catch { return null; }

        var animOverrides = GetAnimBoneOverrides(skel, animName);
        var deformOverrides = GetAnimDeformOverrides(skel, animName);
        var bones = ComputeBoneTransforms(skel.GetProperty("bones"), animOverrides);
        var slotColors = GetSlotColors(skel, animName);

        var skin = skel.GetProperty("skins")[0];
        var attachments = skin.GetProperty("attachments");
        var slotOrder = new List<(string name, string bone, string? att)>();
        foreach (var s in skel.GetProperty("slots").EnumerateArray())
        {
            slotOrder.Add((
                s.GetProperty("name").GetString()!,
                s.TryGetProperty("bone", out var b) ? b.GetString()! : "root",
                s.TryGetProperty("attachment", out var a) ? a.GetString() : null));
        }

        // Collect renderable items
        var items = new List<RenderItem>();
        foreach (var (slotName, boneName, defaultAtt) in slotOrder)
        {
            if (AlwaysSkip.Contains(slotName) || defaultAtt == null) continue;
            if (!attachments.TryGetProperty(slotName, out var slotAtts)) continue;
            if (!slotAtts.TryGetProperty(defaultAtt, out var attData)) continue;

            if (!slotColors.TryGetValue(slotName, out var color))
                color = (255, 255, 255, 255);
            if (color.a == 0) continue;

            var attType = attData.TryGetProperty("type", out var t) ? t.GetString()! : "region";
            var regionName = attData.TryGetProperty("path", out var p) ? p.GetString()!
                : attData.TryGetProperty("name", out var n) ? n.GetString()! : defaultAtt;

            if (!atlas.TryGetValue(regionName, out var region)) continue;
            var bone = bones.GetValueOrDefault(boneName, new BoneXform(1, 0, 0, 1, 0, 0));

            double attX = GetDouble(attData, "x"), attY = GetDouble(attData, "y");
            double attRot = GetDouble(attData, "rotation");
            double attSx = GetDouble(attData, "scaleX", 1), attSy = GetDouble(attData, "scaleY", 1);
            double attW = GetDouble(attData, "width", region.OrigW);
            double attH = GetDouble(attData, "height", region.OrigH);

            // World corners (BL, TL, TR, BR)
            double hw = attW / 2 * attSx, hh = attH / 2 * attSy;
            (double, double)[] cornersPre = [(-hw, -hh), (-hw, hh), (hw, hh), (hw, -hh)];
            double rad = attRot * Math.PI / 180;
            double cosA = Math.Cos(rad), sinA = Math.Sin(rad);
            var worldCorners = cornersPre.Select(cp =>
            {
                double bx = cp.Item1 * cosA - cp.Item2 * sinA + attX;
                double by = cp.Item1 * sinA + cp.Item2 * cosA + attY;
                return (bone.A * bx + bone.B * by + bone.Wx, bone.C * bx + bone.D * by + bone.Wy);
            }).ToArray();

            var item = new RenderItem
            {
                Slot = slotName, Region = region, Color = color,
                WorldCorners = worldCorners, Type = attType
            };

            if (attType == "mesh")
            {
                item.MeshData = BuildMeshData(attData, skel, bones, deformOverrides, slotName, defaultAtt, bone);
                if (item.MeshData != null)
                    item.WorldCorners = item.MeshData.WorldVerts; // use mesh verts for bounds
            }

            items.Add(item);
        }

        if (items.Count == 0) return null;

        // Compute world bounds
        double minX = double.MaxValue, minY = double.MaxValue;
        double maxX = double.MinValue, maxY = double.MinValue;
        foreach (var it in items)
            foreach (var (wx, wy) in it.WorldCorners)
            {
                if (wx < minX) minX = wx; if (wx > maxX) maxX = wx;
                if (wy < minY) minY = wy; if (wy > maxY) maxY = wy;
            }

        double spanX = maxX - minX, spanY = maxY - minY;
        double margin = Math.Max(spanX, spanY) * 0.02;
        minX -= margin; minY -= margin; spanX += 2 * margin; spanY += 2 * margin;

        int internalSize = 512;
        double pixScale = internalSize / Math.Max(spanX, spanY);
        double offX = (internalSize - spanX * pixScale) / 2;
        double offY = (internalSize - spanY * pixScale) / 2;

        (double sx, double sy) WorldToScreen(double wx, double wy) =>
            (offX + (wx - minX) * pixScale, offY + (spanY - (wy - minY)) * pixScale);

        var canvas = new Image<Rgba32>(internalSize, internalSize);

        // Render
        foreach (var item in items)
        {
            var sprite = GetRegionSprite(tex, item.Region);
            int ow = item.Region.OrigW, oh = item.Region.OrigH;

            if (item.Type == "mesh" && item.MeshData != null)
            {
                var md = item.MeshData;
                var temp = new Image<Rgba32>(internalSize, internalSize);
                var sv = md.WorldVerts.Select(v => WorldToScreen(v.x, v.y)).ToArray();
                var tv = md.Uvs.Select(uv => (uv.u * ow, uv.v * oh)).ToArray();

                foreach (var (i0, i1, i2) in md.Triangles)
                    RenderTriangleOnCanvas(temp, sprite, item.Color,
                        [tv[i0], tv[i1], tv[i2]], [sv[i0], sv[i1], sv[i2]]);

                CompositeOnto(canvas, temp);
                temp.Dispose();
            }
            else
            {
                // Region: whole-image affine
                var sc = item.WorldCorners.Select(w => WorldToScreen(w.x, w.y)).ToArray();
                // BL=sc[0], TL=sc[1], TR=sc[2], BR=sc[3]
                // Sprite: TL=(0,0), TR=(ow,0), BL=(0,oh)
                var affine = SolveAffine(
                    [sc[1], sc[2], sc[0]],
                    [(0, 0), (ow, 0), (0, oh)]);
                if (affine == null) continue;

                RenderAffineOnCanvas(canvas, sprite, item.Color, affine.Value,
                    sc.Select(c => c.sx).Min(), sc.Select(c => c.sx).Max(),
                    sc.Select(c => c.sy).Min(), sc.Select(c => c.sy).Max());
            }

            sprite.Dispose();
        }

        // Content-aware crop (alpha > 128)
        int bx0 = internalSize, by0 = internalSize, bx1 = 0, by1 = 0;
        canvas.ProcessPixelRows(acc =>
        {
            for (int y = 0; y < acc.Height; y++)
            {
                var row = acc.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                    if (row[x].A > 128)
                    {
                        if (x < bx0) bx0 = x; if (x > bx1) bx1 = x;
                        if (y < by0) by0 = y; if (y > by1) by1 = y;
                    }
            }
        });

        if (bx1 <= bx0 || by1 <= by0) return null;
        bx1++; by1++;
        int cw = bx1 - bx0, ch = by1 - by0;
        int side = Math.Max(cw, ch);
        int padPx = (int)(side * padding);
        side += 2 * padPx;
        int cx = (bx0 + bx1) / 2, cy = (by0 + by1) / 2;
        int cropX = cx - side / 2, cropY = cy - side / 2;

        // Crop with padding (handle out-of-bounds)
        var cropped = new Image<Rgba32>(side, side);
        cropped.ProcessPixelRows(canvas, (dstAcc, srcAcc) =>
        {
            for (int y = 0; y < side; y++)
            {
                int srcY = y + cropY;
                if (srcY < 0 || srcY >= internalSize) continue;
                var dstRow = dstAcc.GetRowSpan(y);
                var srcRow = srcAcc.GetRowSpan(srcY);
                for (int x = 0; x < side; x++)
                {
                    int srcX = x + cropX;
                    if (srcX >= 0 && srcX < internalSize)
                        dstRow[x] = srcRow[srcX];
                }
            }
        });
        canvas.Dispose();

        cropped.Mutate(ctx => ctx.Resize(canvasSize, canvasSize, KnownResamplers.Lanczos3));

        // Subtle fade: contrast 0.88, brightness 1.07
        ApplyFade(cropped, 0.88f, 1.07f);

        return cropped;
    }

    /// <summary>Convenience overload accepting file paths.</summary>
    public static Image<Rgba32>? RenderIcon(
        string skelPath, string atlasPath, string texturePath,
        string animName = "Select", int canvasSize = 256, float padding = 0.03f)
    {
        if (!File.Exists(skelPath) || !File.Exists(atlasPath) || !File.Exists(texturePath))
            return null;
        var skelJson = File.ReadAllText(skelPath);
        var atlasText = File.ReadAllText(atlasPath);
        using var texture = Image.Load<Rgba32>(texturePath);
        return RenderIcon(skelJson, atlasText, texture, animName, canvasSize, padding);
    }

    // ── Atlas Parsing ───────────────────────────────────────────────────

    /// <summary>Returns the set of region names in a Spine atlas (for skeleton↔atlas matching).</summary>
    public static HashSet<string> GetAtlasRegionNames(string atlasText) =>
        new(ParseAtlas(atlasText).Keys, StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, AtlasRegion> ParseAtlas(string text)
    {
        var regions = new Dictionary<string, AtlasRegion>();
        var lines = text.Split('\n');
        int i = 0;

        // Skip blank lines at start
        while (i < lines.Length && string.IsNullOrWhiteSpace(lines[i])) i++;
        // Skip texture filename
        if (i < lines.Length) i++;
        // Skip page header props (non-indented lines with colons: size, format, filter, repeat, pma)
        while (i < lines.Length && !lines[i].StartsWith("  ") && lines[i].Contains(':')) i++;

        while (i < lines.Length)
        {
            var name = lines[i].Trim();
            if (string.IsNullOrEmpty(name)) { i++; continue; }

            var props = new Dictionary<string, string>();
            i++;
            while (i < lines.Length && lines[i].StartsWith("  "))
            {
                var line = lines[i].Trim();
                var sep = line.IndexOf(": ", StringComparison.Ordinal);
                if (sep > 0) props[line[..sep]] = line[(sep + 2)..];
                i++;
            }

            if (!props.ContainsKey("xy")) continue; // skip non-region entries

            var xy = props["xy"].Split(',');
            var size = (props.GetValueOrDefault("size") ?? "0,0").Split(',');
            var orig = (props.GetValueOrDefault("orig") ?? props.GetValueOrDefault("size") ?? "0,0").Split(',');
            var offset = (props.GetValueOrDefault("offset") ?? "0,0").Split(',');
            bool rotated = props.GetValueOrDefault("rotate") == "true";

            regions[name] = new AtlasRegion(
                int.Parse(xy[0].Trim()), int.Parse(xy[1].Trim()),
                int.Parse(size[0].Trim()), int.Parse(size[1].Trim()),
                int.Parse(orig[0].Trim()), int.Parse(orig[1].Trim()),
                int.Parse(offset[0].Trim()), int.Parse(offset[1].Trim()),
                rotated);
        }
        return regions;
    }

    // ── Skeleton Parsing ────────────────────────────────────────────────

    private static Dictionary<string, Dictionary<string, double>> GetAnimBoneOverrides(
        JsonElement skel, string animName)
    {
        var ov = new Dictionary<string, Dictionary<string, double>>();
        if (!skel.TryGetProperty("animations", out var anims)) return ov;
        if (!anims.TryGetProperty(animName, out var anim)) return ov;
        if (!anim.TryGetProperty("bones", out var bonesAnim)) return ov;

        foreach (var boneProp in bonesAnim.EnumerateObject())
        {
            var d = new Dictionary<string, double>();
            if (boneProp.Value.TryGetProperty("rotate", out var rot))
                foreach (var f in rot.EnumerateArray())
                    if (GetDouble(f, "time") <= 0.001) { d["rotate"] = GetDouble(f, "angle"); break; }
                    else break;

            if (boneProp.Value.TryGetProperty("translate", out var tr))
                foreach (var f in tr.EnumerateArray())
                    if (GetDouble(f, "time") <= 0.001)
                    { d["tx"] = GetDouble(f, "x"); d["ty"] = GetDouble(f, "y"); break; }
                    else break;

            if (boneProp.Value.TryGetProperty("scale", out var sc))
                foreach (var f in sc.EnumerateArray())
                    if (GetDouble(f, "time") <= 0.001)
                    { d["sx"] = GetDouble(f, "x", 1); d["sy"] = GetDouble(f, "y", 1); break; }
                    else break;

            if (d.Count > 0) ov[boneProp.Name] = d;
        }
        return ov;
    }

    private static Dictionary<string, Dictionary<string, (double dx, double dy)[]>> GetAnimDeformOverrides(
        JsonElement skel, string animName)
    {
        var result = new Dictionary<string, Dictionary<string, (double, double)[]>>();
        if (!skel.TryGetProperty("animations", out var anims)) return result;
        if (!anims.TryGetProperty(animName, out var anim)) return result;
        if (!anim.TryGetProperty("deform", out var deform)) return result;

        foreach (var skinProp in deform.EnumerateObject())
            foreach (var slotProp in skinProp.Value.EnumerateObject())
                foreach (var attProp in slotProp.Value.EnumerateObject())
                    foreach (var frame in attProp.Value.EnumerateArray())
                    {
                        if (GetDouble(frame, "time") > 0.001) break;
                        int skip = frame.TryGetProperty("offset", out var o) ? o.GetInt32() : 0;
                        var verts = frame.TryGetProperty("vertices", out var v)
                            ? v.EnumerateArray().Select(e => e.GetDouble()).ToArray()
                            : [];
                        var full = new double[skip + verts.Length];
                        Array.Copy(verts, 0, full, skip, verts.Length);
                        var deltas = new (double, double)[full.Length / 2];
                        for (int j = 0; j < deltas.Length; j++)
                            deltas[j] = (full[j * 2], full[j * 2 + 1]);

                        if (!result.ContainsKey(slotProp.Name))
                            result[slotProp.Name] = new Dictionary<string, (double, double)[]>();
                        result[slotProp.Name][attProp.Name] = deltas;
                        break;
                    }
        return result;
    }

    private static Dictionary<string, BoneXform> ComputeBoneTransforms(
        JsonElement bonesArray,
        Dictionary<string, Dictionary<string, double>> animOv)
    {
        var bones = new Dictionary<string, BoneXform>();
        foreach (var bdef in bonesArray.EnumerateArray())
        {
            string name = bdef.GetProperty("name").GetString()!;
            string? parent = bdef.TryGetProperty("parent", out var pp) ? pp.GetString() : null;
            double lx = GetDouble(bdef, "x"), ly = GetDouble(bdef, "y");
            double lr = GetDouble(bdef, "rotation");
            double lsx = GetDouble(bdef, "scaleX", 1), lsy = GetDouble(bdef, "scaleY", 1);

            if (animOv.TryGetValue(name, out var ov))
            {
                lr += ov.GetValueOrDefault("rotate");
                lx += ov.GetValueOrDefault("tx");
                ly += ov.GetValueOrDefault("ty");
                lsx *= ov.GetValueOrDefault("sx", 1);
                lsy *= ov.GetValueOrDefault("sy", 1);
            }

            double rad = lr * Math.PI / 180;
            double cosR = Math.Cos(rad), sinR = Math.Sin(rad);
            double la = cosR * lsx, lb = -sinR * lsy;
            double lc = sinR * lsx, ld = cosR * lsy;

            double a, b, c, d, wx, wy;
            if (parent != null && bones.TryGetValue(parent, out var pb))
            {
                wx = pb.A * lx + pb.B * ly + pb.Wx;
                wy = pb.C * lx + pb.D * ly + pb.Wy;
                a = pb.A * la + pb.B * lc;
                b = pb.A * lb + pb.B * ld;
                c = pb.C * la + pb.D * lc;
                d = pb.C * lb + pb.D * ld;
            }
            else
            {
                wx = lx; wy = ly; a = la; b = lb; c = lc; d = ld;
            }
            bones[name] = new BoneXform(a, b, c, d, wx, wy);
        }
        return bones;
    }

    private static Dictionary<string, (byte r, byte g, byte b, byte a)> GetSlotColors(
        JsonElement skel, string animName)
    {
        var colors = new Dictionary<string, (byte, byte, byte, byte)>();
        foreach (var s in skel.GetProperty("slots").EnumerateArray())
            if (s.TryGetProperty("color", out var c))
                colors[s.GetProperty("name").GetString()!] = ParseColor(c.GetString()!);

        if (skel.TryGetProperty("animations", out var anims) &&
            anims.TryGetProperty(animName, out var anim) &&
            anim.TryGetProperty("slots", out var slotsAnim))
        {
            foreach (var sp in slotsAnim.EnumerateObject())
                if (sp.Value.TryGetProperty("color", out var colorArr))
                    foreach (var f in colorArr.EnumerateArray())
                        if (GetDouble(f, "time") <= 0.001 && f.TryGetProperty("color", out var cv))
                        { colors[sp.Name] = ParseColor(cv.GetString()!); break; }
                        else break;
        }
        return colors;
    }

    // ── Image Operations ────────────────────────────────────────────────

    private static void UnPremultiply(Image<Rgba32> image)
    {
        image.ProcessPixelRows(acc =>
        {
            for (int y = 0; y < acc.Height; y++)
            {
                var row = acc.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    ref var px = ref row[x];
                    if (px.A == 0) { px = default; continue; }
                    if (px.A == 255) continue;
                    float a = px.A / 255f;
                    px.R = (byte)Math.Clamp((int)(px.R / a), 0, 255);
                    px.G = (byte)Math.Clamp((int)(px.G / a), 0, 255);
                    px.B = (byte)Math.Clamp((int)(px.B / a), 0, 255);
                }
            }
        });
    }

    private static Image<Rgba32> GetRegionSprite(Image<Rgba32> texture, AtlasRegion r)
    {
        Image<Rgba32> sprite;
        if (r.Rotated)
        {
            sprite = texture.Clone(ctx => ctx.Crop(new Rectangle(r.X, r.Y, r.H, r.W)));
            sprite.Mutate(ctx => ctx.Rotate(90)); // PIL rotate(-90) = 90° CW = ImageSharp Rotate(90)
        }
        else
            sprite = texture.Clone(ctx => ctx.Crop(new Rectangle(r.X, r.Y, r.W, r.H)));

        // Reconstruct to orig size if trimmed
        if (r.W == r.OrigW && r.H == r.OrigH) return sprite;
        var full = new Image<Rgba32>(r.OrigW, r.OrigH);
        int pasteX = r.OffsetX;
        int pasteY = r.OrigH - r.OffsetY - r.H;
        full.Mutate(ctx => ctx.DrawImage(sprite, new Point(pasteX, pasteY), 1f));
        sprite.Dispose();
        return full;
    }

    // ── Affine Rendering ────────────────────────────────────────────────

    private static (double a, double b, double c, double d, double e, double f)? SolveAffine(
        (double x, double y)[] dst, (double x, double y)[] src)
    {
        var (x0, y0) = dst[0]; var (x1, y1) = dst[1]; var (x2, y2) = dst[2];
        var (u0, v0) = src[0]; var (u1, v1) = src[1]; var (u2, v2) = src[2];
        double denom = x0 * (y1 - y2) + x1 * (y2 - y0) + x2 * (y0 - y1);
        if (Math.Abs(denom) < 1e-10) return null;
        double inv = 1.0 / denom;
        return (
            (u0 * (y1 - y2) + u1 * (y2 - y0) + u2 * (y0 - y1)) * inv,
            (u0 * (x2 - x1) + u1 * (x0 - x2) + u2 * (x1 - x0)) * inv,
            (u0 * (x1 * y2 - x2 * y1) + u1 * (x2 * y0 - x0 * y2) + u2 * (x0 * y1 - x1 * y0)) * inv,
            (v0 * (y1 - y2) + v1 * (y2 - y0) + v2 * (y0 - y1)) * inv,
            (v0 * (x2 - x1) + v1 * (x0 - x2) + v2 * (x1 - x0)) * inv,
            (v0 * (x1 * y2 - x2 * y1) + v1 * (x2 * y0 - x0 * y2) + v2 * (x0 * y1 - x1 * y0)) * inv);
    }

    private static void RenderAffineOnCanvas(
        Image<Rgba32> canvas, Image<Rgba32> sprite, (byte r, byte g, byte b, byte a) color,
        (double a, double b, double c, double d, double e, double f) aff,
        double scrMinX, double scrMaxX, double scrMinY, double scrMaxY)
    {
        int x0 = Math.Max(0, (int)Math.Floor(scrMinX));
        int y0 = Math.Max(0, (int)Math.Floor(scrMinY));
        int x1 = Math.Min(canvas.Width, (int)Math.Ceiling(scrMaxX) + 1);
        int y1 = Math.Min(canvas.Height, (int)Math.Ceiling(scrMaxY) + 1);
        int sw = sprite.Width, sh = sprite.Height;

        canvas.ProcessPixelRows(sprite, (canvasAcc, spriteAcc) =>
        {
            for (int dy = y0; dy < y1; dy++)
            {
                var cRow = canvasAcc.GetRowSpan(dy);
                for (int dx = x0; dx < x1; dx++)
                {
                    double sx = aff.a * dx + aff.b * dy + aff.c;
                    double sy = aff.d * dx + aff.e * dy + aff.f;
                    if (sx < -0.5 || sx >= sw + 0.5 || sy < -0.5 || sy >= sh + 0.5) continue;

                    var sampled = BilinearSample(spriteAcc, sx, sy, sw, sh);
                    if (sampled.A == 0) continue;

                    // Apply tint
                    sampled = Tint(sampled, color);

                    // Alpha composite
                    AlphaComposite(ref cRow[dx], sampled);
                }
            }
        });
    }

    private static void RenderTriangleOnCanvas(
        Image<Rgba32> canvas, Image<Rgba32> sprite, (byte r, byte g, byte b, byte a) color,
        (double x, double y)[] triSrc, (double x, double y)[] triDst)
    {
        var affine = SolveAffine(triDst, triSrc);
        if (affine == null) return;
        var aff = affine.Value;

        double minSx = triDst.Min(p => p.x), maxSx = triDst.Max(p => p.x);
        double minSy = triDst.Min(p => p.y), maxSy = triDst.Max(p => p.y);
        int x0 = Math.Max(0, (int)Math.Floor(minSx));
        int y0 = Math.Max(0, (int)Math.Floor(minSy));
        int x1 = Math.Min(canvas.Width, (int)Math.Ceiling(maxSx) + 1);
        int y1 = Math.Min(canvas.Height, (int)Math.Ceiling(maxSy) + 1);
        int sw = sprite.Width, sh = sprite.Height;

        canvas.ProcessPixelRows(sprite, (canvasAcc, spriteAcc) =>
        {
            for (int dy = y0; dy < y1; dy++)
            {
                var cRow = canvasAcc.GetRowSpan(dy);
                for (int dx = x0; dx < x1; dx++)
                {
                    if (!PointInTriangle(dx, dy, triDst)) continue;

                    double sx = aff.a * dx + aff.b * dy + aff.c;
                    double sy = aff.d * dx + aff.e * dy + aff.f;
                    if (sx < -0.5 || sx >= sw + 0.5 || sy < -0.5 || sy >= sh + 0.5) continue;

                    var sampled = BilinearSample(spriteAcc, sx, sy, sw, sh);
                    if (sampled.A == 0) continue;
                    sampled = Tint(sampled, color);
                    AlphaComposite(ref cRow[dx], sampled);
                }
            }
        });
    }

    // ── Pixel Operations ────────────────────────────────────────────────

    private static Rgba32 BilinearSample(
        PixelAccessor<Rgba32> acc, double x, double y, int w, int h)
    {
        int ix = (int)Math.Floor(x), iy = (int)Math.Floor(y);
        double fx = x - ix, fy = y - iy;

        // Out-of-bounds → transparent (matches PIL behavior, smooth sprite edges)
        Rgba32 p00 = (ix >= 0 && iy >= 0 && ix < w && iy < h) ? acc.GetRowSpan(iy)[ix] : default;
        Rgba32 p10 = (ix + 1 >= 0 && iy >= 0 && ix + 1 < w && iy < h) ? acc.GetRowSpan(iy)[ix + 1] : default;
        Rgba32 p01 = (ix >= 0 && iy + 1 >= 0 && ix < w && iy + 1 < h) ? acc.GetRowSpan(iy + 1)[ix] : default;
        Rgba32 p11 = (ix + 1 >= 0 && iy + 1 >= 0 && ix + 1 < w && iy + 1 < h) ? acc.GetRowSpan(iy + 1)[ix + 1] : default;

        // Bilinear in premultiplied space
        double a00 = p00.A / 255.0, a10 = p10.A / 255.0, a01 = p01.A / 255.0, a11 = p11.A / 255.0;
        double w00 = (1 - fx) * (1 - fy), w10 = fx * (1 - fy), w01 = (1 - fx) * fy, w11 = fx * fy;

        double ra = p00.R * a00 * w00 + p10.R * a10 * w10 + p01.R * a01 * w01 + p11.R * a11 * w11;
        double ga = p00.G * a00 * w00 + p10.G * a10 * w10 + p01.G * a01 * w01 + p11.G * a11 * w11;
        double ba = p00.B * a00 * w00 + p10.B * a10 * w10 + p01.B * a01 * w01 + p11.B * a11 * w11;
        double aa = a00 * w00 + a10 * w10 + a01 * w01 + a11 * w11;

        if (aa <= 0) return default;
        return new Rgba32(
            (byte)Math.Clamp(ra / aa, 0, 255),
            (byte)Math.Clamp(ga / aa, 0, 255),
            (byte)Math.Clamp(ba / aa, 0, 255),
            (byte)Math.Clamp(aa * 255, 0, 255));
    }

    private static Rgba32 Tint(Rgba32 px, (byte r, byte g, byte b, byte a) color)
    {
        if (color is (255, 255, 255, 255)) return px;
        return new Rgba32(
            (byte)(px.R * color.r / 255),
            (byte)(px.G * color.g / 255),
            (byte)(px.B * color.b / 255),
            (byte)(px.A * color.a / 255));
    }

    private static void AlphaComposite(ref Rgba32 dst, Rgba32 src)
    {
        if (src.A == 0) return;
        if (src.A == 255 || dst.A == 0) { dst = src; return; }
        float sa = src.A / 255f, da = dst.A / 255f;
        float oa = sa + da * (1 - sa);
        if (oa <= 0) return;
        dst = new Rgba32(
            (byte)((src.R * sa + dst.R * da * (1 - sa)) / oa),
            (byte)((src.G * sa + dst.G * da * (1 - sa)) / oa),
            (byte)((src.B * sa + dst.B * da * (1 - sa)) / oa),
            (byte)(oa * 255));
    }

    private static void CompositeOnto(Image<Rgba32> dst, Image<Rgba32> src)
    {
        dst.ProcessPixelRows(src, (dAcc, sAcc) =>
        {
            for (int y = 0; y < dAcc.Height; y++)
            {
                var dRow = dAcc.GetRowSpan(y);
                var sRow = sAcc.GetRowSpan(y);
                for (int x = 0; x < dRow.Length; x++)
                    AlphaComposite(ref dRow[x], sRow[x]);
            }
        });
    }

    private static bool PointInTriangle(double px, double py, (double x, double y)[] tri)
    {
        double d1 = (px - tri[1].x) * (tri[0].y - tri[1].y) - (tri[0].x - tri[1].x) * (py - tri[1].y);
        double d2 = (px - tri[2].x) * (tri[1].y - tri[2].y) - (tri[1].x - tri[2].x) * (py - tri[2].y);
        double d3 = (px - tri[0].x) * (tri[2].y - tri[0].y) - (tri[2].x - tri[0].x) * (py - tri[0].y);
        bool hasNeg = d1 < 0 || d2 < 0 || d3 < 0;
        bool hasPos = d1 > 0 || d2 > 0 || d3 > 0;
        return !(hasNeg && hasPos);
    }

    private static void ApplyFade(Image<Rgba32> image, float contrast, float brightness)
    {
        // Compute mean luminance for contrast adjustment
        // Compute mean luminance over ALL pixels (including transparent = black),
        // using ITU-R 601-2 luma weights — matches PIL ImageEnhance.Contrast behavior
        double sum = 0;
        int totalPixels = image.Width * image.Height;
        image.ProcessPixelRows(acc =>
        {
            for (int y = 0; y < acc.Height; y++)
            {
                var row = acc.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                    sum += 0.299 * row[x].R + 0.587 * row[x].G + 0.114 * row[x].B;
            }
        });
        double mean = totalPixels > 0 ? sum / totalPixels : 128;

        image.ProcessPixelRows(acc =>
        {
            for (int y = 0; y < acc.Height; y++)
            {
                var row = acc.GetRowSpan(y);
                for (int x = 0; x < row.Length; x++)
                {
                    ref var px = ref row[x];
                    if (px.A == 0) continue;
                    px.R = (byte)Math.Clamp((int)((contrast * (px.R - mean) + mean) * brightness), 0, 255);
                    px.G = (byte)Math.Clamp((int)((contrast * (px.G - mean) + mean) * brightness), 0, 255);
                    px.B = (byte)Math.Clamp((int)((contrast * (px.B - mean) + mean) * brightness), 0, 255);
                }
            }
        });
    }

    // ── Mesh Data ───────────────────────────────────────────────────────

    private class MeshData
    {
        public (double x, double y)[] WorldVerts = [];
        public (double u, double v)[] Uvs = [];
        public (int i0, int i1, int i2)[] Triangles = [];
    }

    private class RenderItem
    {
        public string Slot = "";
        public string Type = "region";
        public AtlasRegion Region = null!;
        public (byte r, byte g, byte b, byte a) Color;
        public (double x, double y)[] WorldCorners = [];
        public MeshData? MeshData;
    }

    private static MeshData? BuildMeshData(
        JsonElement attData, JsonElement skel,
        Dictionary<string, BoneXform> bones,
        Dictionary<string, Dictionary<string, (double, double)[]>> deformOv,
        string slotName, string attName, BoneXform slotBone)
    {
        if (!attData.TryGetProperty("uvs", out var uvsEl)) return null;
        if (!attData.TryGetProperty("triangles", out var trisEl)) return null;
        var rawVerts = attData.TryGetProperty("vertices", out var vEl)
            ? vEl.EnumerateArray().Select(e => e.GetDouble()).ToArray() : [];
        var uvsFlat = uvsEl.EnumerateArray().Select(e => e.GetDouble()).ToArray();
        var triFlat = trisEl.EnumerateArray().Select(e => e.GetInt32()).ToArray();

        int numUvVerts = uvsFlat.Length / 2;
        var uvs = new (double, double)[numUvVerts];
        for (int i = 0; i < numUvVerts; i++)
            uvs[i] = (uvsFlat[i * 2], uvsFlat[i * 2 + 1]);

        bool isWeighted = rawVerts.Length != uvsFlat.Length;
        var deformDeltas = deformOv.GetValueOrDefault(slotName)?.GetValueOrDefault(attName)
            ?? [];

        var bonesArr = skel.GetProperty("bones");
        (double, double)[] worldVerts;

        if (isWeighted)
        {
            worldVerts = new (double, double)[numUvVerts];
            int idx = 0;
            for (int vi = 0; vi < numUvVerts; vi++)
            {
                int boneCount = (int)rawVerts[idx++];
                double wx = 0, wy = 0;
                for (int j = 0; j < boneCount; j++)
                {
                    int boneIdx = (int)rawVerts[idx];
                    double vx = rawVerts[idx + 1], vy = rawVerts[idx + 2];
                    double weight = rawVerts[idx + 3]; idx += 4;
                    if (vi < deformDeltas.Length)
                    { vx += deformDeltas[vi].Item1; vy += deformDeltas[vi].Item2; }
                    string bName = bonesArr[boneIdx].GetProperty("name").GetString()!;
                    var b = bones.GetValueOrDefault(bName, new BoneXform(1, 0, 0, 1, 0, 0));
                    wx += (b.A * vx + b.B * vy + b.Wx) * weight;
                    wy += (b.C * vx + b.D * vy + b.Wy) * weight;
                }
                worldVerts[vi] = (wx, wy);
            }
        }
        else
        {
            var localVerts = new (double, double)[numUvVerts];
            for (int i = 0; i < numUvVerts; i++)
                localVerts[i] = (rawVerts[i * 2], rawVerts[i * 2 + 1]);
            for (int i = 0; i < Math.Min(deformDeltas.Length, localVerts.Length); i++)
                localVerts[i] = (localVerts[i].Item1 + deformDeltas[i].Item1,
                                 localVerts[i].Item2 + deformDeltas[i].Item2);

            // Non-weighted: vertices are in bone-local space, transform to world
            worldVerts = new (double, double)[localVerts.Length];
            for (int i = 0; i < localVerts.Length; i++)
            {
                var (vx, vy) = localVerts[i];
                worldVerts[i] = (slotBone.A * vx + slotBone.B * vy + slotBone.Wx,
                                 slotBone.C * vx + slotBone.D * vy + slotBone.Wy);
            }
        }

        var triangles = new (int, int, int)[triFlat.Length / 3];
        for (int i = 0; i < triangles.Length; i++)
            triangles[i] = (triFlat[i * 3], triFlat[i * 3 + 1], triFlat[i * 3 + 2]);

        return new MeshData { WorldVerts = worldVerts, Uvs = uvs, Triangles = triangles };
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static double GetDouble(JsonElement el, string prop, double def = 0) =>
        el.TryGetProperty(prop, out var v) ? v.GetDouble() : def;

    private static (byte r, byte g, byte b, byte a) ParseColor(string hex) =>
        ((byte)Convert.ToInt32(hex[0..2], 16),
         (byte)Convert.ToInt32(hex[2..4], 16),
         (byte)Convert.ToInt32(hex[4..6], 16),
         (byte)Convert.ToInt32(hex[6..8], 16));
}
