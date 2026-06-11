using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MergeMansionWikiTools.Models;
using MergeMansionWikiTools.Views;

namespace MergeMansionWikiTools.Services;

public static partial class MysteryWikiService
{
	public enum AtlasTileType
	{
		Decoration,
		Icon
	}

	private static Dictionary<string, string>? _petDisplayNames;

	public static bool HasPetDisplayNames => _petDisplayNames != null && _petDisplayNames.Count > 0;

	public static void LoadPetDisplayNamesFromPath(string path)
	{
		_petDisplayNames = null;
		if (string.IsNullOrEmpty(path) || !File.Exists(path)) return;
		LoadPetDisplayNamesInternal(path);
	}

	public static void LoadPetDisplayNames(string? basePath, string? apkVersion)
	{
		_petDisplayNames = null;
		if (string.IsNullOrEmpty(basePath) || string.IsNullOrEmpty(apkVersion))
		{
			return;
		}
		// Check main Dump/ first, then Experimental/
		string path = Path.Combine(basePath, apkVersion, "Dump", "Pets.json");
		if (!File.Exists(path))
			path = Path.Combine(basePath, apkVersion, "Dump", "Experimental", "Pets.json");
		if (!File.Exists(path)) return;
		LoadPetDisplayNamesInternal(path);
	}

	private static void LoadPetDisplayNamesInternal(string path)
	{
		try
		{
			string json = File.ReadAllText(path);
			JsonDocument jsonDocument = JsonDocument.Parse(json);
			JsonElement jsonElement = jsonDocument.RootElement;
			if (jsonElement.TryGetProperty("Data", out var value))
			{
				jsonElement = value;
			}
			_petDisplayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			JsonElement jsonElement2;
			if (jsonElement.ValueKind == JsonValueKind.Array)
			{
				jsonElement2 = jsonElement;
			}
			else
			{
				if (!jsonElement.TryGetProperty("Pets", out var value2))
				{
					return;
				}
				jsonElement2 = value2;
			}
			foreach (JsonElement item in jsonElement2.EnumerateArray())
			{
				JsonElement value3;
				string text = (item.TryGetProperty("PetId", out value3) ? value3.GetString() : null);
				JsonElement value5;
				string value4 = (item.TryGetProperty("SelectionHeader", out value5) ? value5.GetString() : null);
				if (!string.IsNullOrEmpty(text) && !string.IsNullOrEmpty(value4))
				{
					_petDisplayNames[text] = value4;
				}
			}
			AppLogger.Info($"Loaded {_petDisplayNames.Count} pet display names from Pets.json");
		}
		catch (Exception ex)
		{
			AppLogger.Warn("Failed to load Pets.json: " + ex.Message);
		}
	}

	public static string FormatPetDisplayName(string? configKey)
	{
		if (string.IsNullOrEmpty(configKey))
		{
			return "Pet";
		}
		if (_petDisplayNames != null && _petDisplayNames.TryGetValue(configKey, out string value))
		{
			return StripPetSuffix(value);
		}
		return configKey;
	}

	/// <summary>
	/// Strips " the ..." suffix from pet SelectionHeader to get wiki display name.
	/// E.g., "Amy the Cat" → "Amy", "Pablo the Goat" → "Pablo",
	/// but "Klepto &amp; Bandit" → "Klepto &amp; Bandit" (no " the "), "Boo!" → "Boo!" (no " the ").
	/// </summary>
	private static string StripPetSuffix(string name)
	{
		int idx = name.IndexOf(" the ", StringComparison.OrdinalIgnoreCase);
		if (idx > 0)
			return name[..idx];
		return name;
	}

	public static string? ResolveExportPngsDir(string? basePath, string? apkVersion)
	{
		if (string.IsNullOrEmpty(basePath) || string.IsNullOrEmpty(apkVersion))
		{
			return null;
		}
		string text = Path.Combine(basePath, apkVersion, "Export - PNGs");
		return Directory.Exists(text) ? text : null;
	}

	public static List<(AtlasTileType Type, byte[] PngData)> SliceDecorationAtlas(string atlasPath)
	{
		List<(AtlasTileType, byte[])> list = new List<(AtlasTileType, byte[])>();
		if (!File.Exists(atlasPath))
		{
			return list;
		}
		PngBitmapDecoder pngBitmapDecoder = new PngBitmapDecoder(new Uri(atlasPath), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
		BitmapFrame bitmapFrame = pngBitmapDecoder.Frames[0];
		int pixelWidth = bitmapFrame.PixelWidth;
		int pixelHeight = bitmapFrame.PixelHeight;
		int num = 256;
		int num2 = pixelWidth / num;
		int num3 = pixelHeight / num;
		for (int i = 0; i < num3; i++)
		{
			for (int j = 0; j < num2; j++)
			{
				int num4 = j * num;
				int num5 = i * num;
				if (num4 + num > pixelWidth || num5 + num > pixelHeight)
				{
					continue;
				}
				CroppedBitmap croppedBitmap = new CroppedBitmap(bitmapFrame, new Int32Rect(num4, num5, num, num));
				byte[] array = new byte[num * num * 4];
				croppedBitmap.CopyPixels(array, num * 4, 0);
				int num6 = num;
				int num7 = num;
				int num8 = 0;
				int num9 = 0;
				bool flag = false;
				for (int k = 0; k < num; k++)
				{
					for (int l = 0; l < num; l++)
					{
						int num10 = array[(k * num + l) * 4 + 3];
						if (num10 > 10)
						{
							flag = true;
							if (l < num6)
							{
								num6 = l;
							}
							if (l > num8)
							{
								num8 = l;
							}
							if (k < num7)
							{
								num7 = k;
							}
							if (k > num9)
							{
								num9 = k;
							}
						}
					}
				}
				if (!flag)
				{
					continue;
				}
				int num11 = num8 - num6 + 1;
				int num12 = num9 - num7 + 1;
				bool flag2 = num11 <= 100 && num12 <= 100;
				BitmapSource source;
				if (flag2)
				{
					int num13 = 80;
					int num14 = num4 + num6 + num11 / 2;
					int num15 = num5 + num7 + num12 / 2;
					int num16 = Math.Max(0, num14 - num13 / 2);
					int num17 = Math.Max(0, num15 - num13 / 2);
					if (num16 + num13 > pixelWidth)
					{
						num16 = pixelWidth - num13;
					}
					if (num17 + num13 > pixelHeight)
					{
						num17 = pixelHeight - num13;
					}
					source = new CroppedBitmap(bitmapFrame, new Int32Rect(num16, num17, num13, num13));
				}
				else
				{
					source = croppedBitmap;
				}
				using MemoryStream memoryStream = new MemoryStream();
				PngBitmapEncoder pngBitmapEncoder = new PngBitmapEncoder();
				pngBitmapEncoder.Frames.Add(BitmapFrame.Create(source));
				pngBitmapEncoder.Save(memoryStream);
				list.Add((flag2 ? AtlasTileType.Icon : AtlasTileType.Decoration, memoryStream.ToArray()));
			}
		}
		List<int> list2 = new List<int>();
		for (int m = 0; m < list.Count; m++)
		{
			if (list[m].Item1 == AtlasTileType.Decoration)
			{
				list2.Add(m);
			}
		}
		if (list2.Count >= 2)
		{
			(AtlasTileType, byte[]) value = list[list2[0]];
			list[list2[0]] = list[list2[1]];
			list[list2[1]] = value;
		}
		return list;
	}

	private static string CopyToProcessed(string sourcePath, string wikiFilename, string? processedDir)
	{
		if (string.IsNullOrEmpty(processedDir))
		{
			return sourcePath;
		}
		string text = Path.Combine(processedDir, wikiFilename);
		if (File.Exists(text))
		{
			try
			{
				byte[] array = File.ReadAllBytes(text);
				if (OptimizationWindow.HasOptMarker(array))
				{
					return text;
				}
			}
			catch
			{
			}
		}
		try
		{
			File.Copy(sourcePath, text, overwrite: true);
		}
		catch
		{
			return sourcePath;
		}
		return text;
	}

	private static long? CheckOptMarker(string path)
	{
		try
		{
			byte[] array = File.ReadAllBytes(path);
			if (OptimizationWindow.HasOptMarker(array))
			{
				return array.Length;
			}
		}
		catch
		{
		}
		return null;
	}

	/// <summary>
	/// Recognises a pet Season Pass decoration sprite and returns its export position. The pet
	/// itself (<c>{id}_Decor_Pet</c>) is always position 0; the habitat/"PetHome" stages follow.
	/// Two naming conventions appear in game data, both encoding a stage index:
	///   • <c>{id}_Decor_PetHomeTA / TB / TC …</c>  (every pet pass except one)
	///   • <c>{id}_TA1_PetHome / TA2_PetHome …</c>   (SP_IguanaPet2026)
	/// Returns false for anything that isn't a pet decoration. Deriving order from the sprite
	/// identity (not atlas X-position) is what keeps the pet at slot 0 across both conventions.
	/// </summary>
	private static bool TryGetPetDecorOrder(string spriteName, string eventId, out int order)
	{
		order = 0;
		string id = System.Text.RegularExpressions.Regex.Escape(eventId);
		if (spriteName.Equals(eventId + "_Decor_Pet", StringComparison.OrdinalIgnoreCase))
			return true; // pet → 0

		var m = System.Text.RegularExpressions.Regex.Match(
			spriteName, "^" + id + @"_Decor_PetHomeT([A-Z])$",
			System.Text.RegularExpressions.RegexOptions.IgnoreCase);
		if (m.Success) { order = char.ToUpperInvariant(m.Groups[1].Value[0]) - 'A' + 1; return true; }

		m = System.Text.RegularExpressions.Regex.Match(
			spriteName, "^" + id + @"_TA(\d+)_PetHome$",
			System.Text.RegularExpressions.RegexOptions.IgnoreCase);
		if (m.Success) { order = int.Parse(m.Groups[1].Value); return true; }

		return false;
	}

	private static bool ExtractDecorationsFromSpriteMetadata(string exportDir, string progressionEventId, string mysteryName, string pageNameUnderscore, string? processedDir, bool isPet, ref int decoNum, List<DetectedDecorationFile> result, MysteryEvent? mystery = null)
	{
		string directoryName = Path.GetDirectoryName(exportDir);
		if (string.IsNullOrEmpty(directoryName))
		{
			return false;
		}
		string path = Path.Combine(directoryName, "image_atlas_data.json");
		if (!File.Exists(path))
		{
			AppLogger.Debug($"[DecoExtract] atlas json NOT found at '{path}' → return false (Slice fallback)");
			return false;
		}
		try
		{
			AppLogger.Debug($"[DecoExtract] ENTER eventId='{progressionEventId}' isPet={isPet} mysteryName='{mysteryName}' exportDir='{exportDir}'");
			string json = File.ReadAllText(path);
			JsonDocument jsonDocument = JsonDocument.Parse(json);
			if (!jsonDocument.RootElement.TryGetProperty("sprites", out var value))
			{
				AppLogger.Debug("[DecoExtract] no 'sprites' property → return false");
				return false;
			}
			HashSet<string> allowedSlots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			if (mystery != null)
			{
				IEnumerable<MysteryRewardLevel> enumerable = mystery.FreeTier.Concat(mystery.SilverTier).Concat(mystery.GoldTier);
				foreach (MysteryRewardLevel item in enumerable)
				{
					foreach (MysteryReward reward in item.Rewards)
					{
						if (reward.Type == MysteryRewardType.Decoration && !string.IsNullOrEmpty(reward.DecorationId))
						{
							allowedSlots.Add(reward.DecorationId);
						}
					}
				}
			}
			List<(string, string, int, int, int, int)> list = new List<(string, string, int, int, int, int)>();
			// Per-sprite metadata extracted from standalone entries (pivot, canvas size, border)
			// and atlas entries (textureRectOffset). Defaults assumed if absent: center pivot,
			// no trim offset. In Merge Mansion data these are consistently the defaults, but
			// reading them lets crop/padding logic stay correct if game ever changes that.
			var pivots = new Dictionary<string, (double X, double Y)>(StringComparer.OrdinalIgnoreCase);
			var trimOffsets = new Dictionary<string, (double X, double Y)>(StringComparer.OrdinalIgnoreCase);
			string text = null;
			int num = 0;
			int num2 = 0;
			int num3 = 0;
			int num4 = 0;
			// Pet-specific sprite collection (no sactx filter required)
			var petDecors = new List<(string Name, string Texture, int X, int Y, int W, int H, int Order)>();
			foreach (JsonElement item2 in value.EnumerateArray())
			{
				string text2 = item2.GetProperty("name").GetString() ?? "";
				string text3 = item2.GetProperty("textureName").GetString() ?? "";

				// Capture sprite-asset-side metadata (pivot, etc.) from standalone entries.
				// In image_atlas_data.json each sprite appears twice: once as atlas member
				// (textureName == atlas asset) and once as standalone (textureName == sprite
				// name). Pivot is meaningful only on the standalone entry since it describes
				// the sprite's intrinsic anchor; atlas entry doesn't carry it.
				if (string.Equals(text3, text2, StringComparison.OrdinalIgnoreCase))
				{
					// TryGetProperty returns true even when the JSON value is null, so the
					// ValueKind guard is mandatory: atlas data frequently has pivotX/Y present
					// but null (e.g. the "DoesNotBelong" sprite). Calling GetDouble() on a null
					// element throws and aborts the WHOLE extraction → SliceDecorationAtlas
					// fallback with naive X-ordering (this is what mis-ordered the Iguana pet).
					if (item2.TryGetProperty("pivotX", out var pxEl) && item2.TryGetProperty("pivotY", out var pyEl)
						&& pxEl.ValueKind == JsonValueKind.Number && pyEl.ValueKind == JsonValueKind.Number)
					{
						pivots[text2] = (pxEl.GetDouble(), pyEl.GetDouble());
					}
				}
				else
				{
					// Atlas entry: textureRectOffset is the trim offset within the atlas rect.
					// In the current data ~15k atlas entries carry these fields as null (only the
					// standalone entry has numbers), so the ValueKind guard is required — without
					// it GetDouble() throws on the first null and the whole extraction aborts.
					if (item2.TryGetProperty("textureRectOffsetX", out var oxEl) && item2.TryGetProperty("textureRectOffsetY", out var oyEl)
						&& oxEl.ValueKind == JsonValueKind.Number && oyEl.ValueKind == JsonValueKind.Number)
					{
						double ox = oxEl.GetDouble(), oy = oyEl.GetDouble();
						if (ox != 0 || oy != 0)
						{
							trimOffsets[text2] = (ox, oy);
							AppLogger.Warn($"Sprite '{text2}' has non-zero textureRectOffset ({ox}, {oy}) — atlas is trimmed; crop logic may need offset compensation.");
						}
					}
				}
				// Collect pet decoration sprites before the sactx filter (they may live on non-sactx
				// atlas textures). Done regardless of isPet: SP_IguanaPet2026 carries the pet
				// decorations but has NO RewardPet reward, so MysteryType.Pet detection misses it —
				// yet Decor_Pet / PetHome are still the real decorations and the pet must come first.
				// Position comes from the sprite identity (TryGetPetDecorOrder), not atlas X-order.
				if (TryGetPetDecorOrder(text2, progressionEventId, out int petOrder))
				{
					int prX = (int)item2.GetProperty("rectX").GetSingle();
					int prY = (int)item2.GetProperty("rectY").GetSingle();
					int prW = (int)item2.GetProperty("rectWidth").GetSingle();
					int prH = (int)item2.GetProperty("rectHeight").GetSingle();
					petDecors.Add((text2, text3, prX, prY, prW, prH, petOrder));
				}
				// Icon: handle both {id}_Set_Icon and {id}Set_Icon (some game data omits underscore)
				if (isPet && text == null
					&& (text2.Equals(progressionEventId + "_Set_Icon", StringComparison.OrdinalIgnoreCase)
						|| text2.Equals(progressionEventId + "Set_Icon", StringComparison.OrdinalIgnoreCase))
					&& !text2.Contains("Badge", StringComparison.OrdinalIgnoreCase))
				{
					text = text3;
					num = (int)item2.GetProperty("rectX").GetSingle();
					num2 = (int)item2.GetProperty("rectY").GetSingle();
					num3 = (int)item2.GetProperty("rectWidth").GetSingle();
					num4 = (int)item2.GetProperty("rectHeight").GetSingle();
				}
				// Skip standalone single-sprite-texture duplicate entries (textureName ==
				// sprite name). Image_atlas_data.json contains both an atlas member entry
				// (textureName = atlas name) AND a standalone copy (textureName = sprite
				// name, rect = native canvas) for every sprite. We want only the atlas
				// member — the standalone is redundant for crop purposes.
				//
				// Previous filter `if (!text3.StartsWith("sactx-")) continue;` was unreliable:
				// game v26.04.01+ exports atlas textureName WITHOUT the sactx- prefix for
				// some mysteries (SP_WorldCup2026, all CBE_*), which silently filtered out
				// every atlas entry → primary path returned false → fallback SliceDecorationAtlas
				// ran with naive 256×256 grid, producing decorations with bleed from
				// neighbouring atlas tiles.
				if (string.Equals(text3, text2, StringComparison.OrdinalIgnoreCase))
				{
					continue;
				}
				bool flag = text2.StartsWith(progressionEventId + "_Decoration_Slot", StringComparison.OrdinalIgnoreCase);
				bool flag2 = text2.StartsWith(progressionEventId + "_Decor_Item_", StringComparison.OrdinalIgnoreCase);
				if (flag || flag2)
				{
					string text4 = text2;
					if (flag2)
					{
						string text5 = text2;
						int num5 = text2.LastIndexOf('_') + 1;
						string text6 = text5.Substring(num5, text5.Length - num5);
						text4 = progressionEventId + "_Decoration_Slot" + text6;
					}
					if (allowedSlots.Count <= 0 || allowedSlots.Contains(text4))
					{
						list.Add((text4, text3, (int)item2.GetProperty("rectX").GetSingle(), (int)item2.GetProperty("rectY").GetSingle(), (int)item2.GetProperty("rectWidth").GetSingle(), (int)item2.GetProperty("rectHeight").GetSingle()));
					}
				}
				else if (text2.Equals(progressionEventId + "_Set_Icon", StringComparison.OrdinalIgnoreCase))
				{
					text = text3;
					num = (int)item2.GetProperty("rectX").GetSingle();
					num2 = (int)item2.GetProperty("rectY").GetSingle();
					num3 = (int)item2.GetProperty("rectWidth").GetSingle();
					num4 = (int)item2.GetProperty("rectHeight").GetSingle();
				}
			}
			AppLogger.Debug($"[DecoExtract] after sprite loop: list(slots)={list.Count} petDecors={petDecors.Count} [{string.Join(", ", petDecors.Select(p => p.Name + "#" + p.Order))}]");
			// Pet fallback: if standard detection found nothing, use pet-specific sprites. No longer
			// gated on isPet — SP_IguanaPet2026 is classified Standard (no RewardPet) yet still needs
			// its Decor_Pet / PetHome sprites extracted in pet-first order.
			if (list.Count == 0 && petDecors.Count > 0)
			{
				// Deduplicate by name, prefer entries whose texture resolves to a file; order Pet→TA→TB
				var seenPet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				var orderedPet = petDecors
					.OrderBy(p => p.Order)
					.ThenByDescending(p => ResolveSpriteTexturePath(exportDir, p.Texture) != null ? 1 : 0)
					.Where(p => seenPet.Add(p.Name))
					.ToList();
				// Find atlas texture from PetHome sprites (for Decor_Pet fallback)
				string? atlasTextureName = orderedPet
					.Where(p => p.Texture.Contains("Atlas", StringComparison.OrdinalIgnoreCase))
					.Select(p => p.Texture)
					.FirstOrDefault();
				foreach (var pd in orderedPet)
				{
					bool isDecorPet = pd.Name.EndsWith("_Decor_Pet", StringComparison.OrdinalIgnoreCase);
					string? resolvedTex = ResolveSpriteTexturePath(exportDir, pd.Texture);
					if (resolvedTex != null)
					{
						// Texture file found — use metadata rect as-is
						list.Add((pd.Name, pd.Texture, pd.X, pd.Y, pd.W, pd.H));
					}
					else if (isDecorPet && atlasTextureName != null && ResolveSpriteTexturePath(exportDir, atlasTextureName) != null)
					{
						// Decor_Pet standalone missing — compute position on Decorations Atlas
						int maxDecoEndX = orderedPet
							.Where(p => !p.Name.EndsWith("_Decor_Pet", StringComparison.OrdinalIgnoreCase)
										&& p.Texture.Equals(atlasTextureName, StringComparison.OrdinalIgnoreCase))
							.Select(p => p.X + p.W)
							.DefaultIfEmpty(0)
							.Max();
						int petX = maxDecoEndX + 4; // small padding after last decoration
						list.Add((pd.Name, atlasTextureName, petX, pd.Y, pd.W, pd.H));
					}
					else if (!isDecorPet)
					{
						// Non-pet decoration with unresolvable texture — still add, texture resolution will retry later
						list.Add((pd.Name, pd.Texture, pd.X, pd.Y, pd.W, pd.H));
					}
				}
			}
			if (list.Count == 0)
			{
				AppLogger.Debug("[DecoExtract] list empty after fallback → return false (Slice fallback)");
				return false;
			}
			Dictionary<string, int> slotOrder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
			if (mystery != null)
			{
				int num6 = 0;
				foreach (MysteryRewardLevel item3 in mystery.SilverTier)
				{
					foreach (MysteryReward reward2 in item3.Rewards)
					{
						if (reward2.Type == MysteryRewardType.Decoration && !string.IsNullOrEmpty(reward2.DecorationId))
						{
							slotOrder.TryAdd(reward2.DecorationId, num6++);
						}
					}
				}
				foreach (MysteryRewardLevel item4 in mystery.GoldTier)
				{
					foreach (MysteryReward reward3 in item4.Rewards)
					{
						if (reward3.Type == MysteryRewardType.Decoration && !string.IsNullOrEmpty(reward3.DecorationId))
						{
							slotOrder.TryAdd(reward3.DecorationId, num6++);
						}
					}
				}
			}
			var source = (from s in list
				where allowedSlots.Count == 0 || allowedSlots.Contains(s.Item1)
					|| s.Item1.Contains("_Decor_Pet", StringComparison.OrdinalIgnoreCase)
					|| TryGetPetDecorOrder(s.Item1, progressionEventId, out _)
				// Pet decorations carry their own position (pet=0, home stages follow); they are
				// not in slotOrder and their names contain stray digits ("2026", "TA1") that
				// ExtractSlotNumber would misread — so honour the pet order first.
				orderby TryGetPetDecorOrder(s.Item1, progressionEventId, out var petOrd)
							? petOrd
							: (slotOrder.TryGetValue(s.Item1, out var value4) ? value4 : ExtractSlotNumber(s.Item1))
				select s).ToList();
			HashSet<string> seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			source = source.Where(s => seenNames.Add(s.Item1)).ToList();
			AppLogger.Debug($"[DecoExtract] FINAL source order (decoNum starts {decoNum}): [{string.Join(" → ", source.Select(s => s.Item1))}]");
			Dictionary<string, BitmapSource> dictionary = new Dictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);
			foreach (var item5 in source)
			{
				string text7 = FormatFileName(mysteryName + "Decoration", decoNum);
				AppLogger.Debug($"[DecoExtract]   slot {decoNum} ← {item5.Item1}  → file '{text7}'  (existingReuse={(!string.IsNullOrEmpty(processedDir) && File.Exists(Path.Combine(processedDir, text7)) && CheckOptMarker(Path.Combine(processedDir, text7)).HasValue)})");
				if (!string.IsNullOrEmpty(processedDir))
				{
					string text8 = Path.Combine(processedDir, text7);
					if (File.Exists(text8))
					{
						long? optimizedSize = CheckOptMarker(text8);
						if (optimizedSize.HasValue)
						{
							result.Add(new DetectedDecorationFile
							{
								SourcePath = text8,
								WikiFilename = text7,
								Category = "Decoration",
								Width = item5.Item5,
								Height = item5.Item6,
								OptimizedSize = optimizedSize
							});
							decoNum++;
							continue;
						}
					}
				}
				string text9 = Path.Combine(exportDir, item5.Item2 + ".png");
				if (!File.Exists(text9))
				{
					// Fallback: try sactx-prefixed atlas file
					text9 = ResolveSpriteTexturePath(exportDir, item5.Item2);
					if (text9 == null)
					{
						decoNum++;
						continue;
					}
				}
				if (!dictionary.TryGetValue(text9, out var value2))
				{
					PngBitmapDecoder pngBitmapDecoder = new PngBitmapDecoder(new Uri(text9), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
					value2 = (dictionary[text9] = pngBitmapDecoder.Frames[0]);
				}
				int pixelHeight = value2.PixelHeight;
				int num7 = pixelHeight - item5.Item4 - item5.Item6;
				if (num7 < 0)
				{
					num7 = 0;
				}
				int num8 = Math.Min(item5.Item5, value2.PixelWidth - item5.Item3);
				int num9 = Math.Min(item5.Item6, pixelHeight - num7);
				if (num8 <= 0 || num9 <= 0)
				{
					decoNum++;
					continue;
				}
				CroppedBitmap croppedBitmap = new CroppedBitmap(value2, new Int32Rect(item5.Item3, num7, num8, num9));
				BitmapSource source2 = croppedBitmap;
				if (num8 < 256 || num9 < 256)
				{
					int num10 = 256;
					RenderTargetBitmap renderTargetBitmap = new RenderTargetBitmap(num10, num10, 96.0, 96.0, PixelFormats.Pbgra32);
					DrawingVisual drawingVisual = new DrawingVisual();
					// Pivot-aware positioning. Unity sprite pivot is normalized 0..1 with
					// origin at bottom-left, so Y is flipped for top-left WPF rendering.
					// Default (0.5, 0.5) = center → matches the previous hardcoded behaviour.
					var (pvx, pvy) = pivots.TryGetValue(item5.Item1, out var pv) ? pv : (0.5, 0.5);
					double drawX = (num10 - num8) * pvx;
					double drawY = (num10 - num9) * (1.0 - pvy);
					using (DrawingContext drawingContext = drawingVisual.RenderOpen())
					{
						drawingContext.DrawImage(croppedBitmap, new Rect(drawX, drawY, num8, num9));
					}
					renderTargetBitmap.Render(drawingVisual);
					source2 = renderTargetBitmap;
				}
				byte[] bytes;
				using (MemoryStream memoryStream = new MemoryStream())
				{
					PngBitmapEncoder pngBitmapEncoder = new PngBitmapEncoder();
					pngBitmapEncoder.Frames.Add(BitmapFrame.Create(source2));
					pngBitmapEncoder.Save(memoryStream);
					bytes = memoryStream.ToArray();
				}
				string text10 = ((!string.IsNullOrEmpty(processedDir)) ? Path.Combine(processedDir, text7) : Path.Combine(Path.GetTempPath(), text7));
				File.WriteAllBytes(text10, bytes);
				result.Add(new DetectedDecorationFile
				{
					SourcePath = text10,
					WikiFilename = text7,
					Category = "Decoration",
					Width = item5.Item5,
					Height = item5.Item6
				});
				decoNum++;
			}
			if (text != null && num3 > 0 && num4 > 0 && !result.Any((DetectedDecorationFile r) => r.Category == "Icon"))
			{
				string text11 = pageNameUnderscore + "_Icon.png";
				if (!string.IsNullOrEmpty(processedDir))
				{
					string text12 = Path.Combine(processedDir, text11);
					if (File.Exists(text12))
					{
						long? optimizedSize2 = CheckOptMarker(text12);
						if (optimizedSize2.HasValue)
						{
							result.Add(new DetectedDecorationFile
							{
								SourcePath = text12,
								WikiFilename = text11,
								Category = "Icon",
								OptimizedSize = optimizedSize2
							});
							return true;
						}
					}
				}
				string text13 = Path.Combine(exportDir, text + ".png");
				if (!File.Exists(text13))
					text13 = ResolveSpriteTexturePath(exportDir, text);
				if (text13 != null && File.Exists(text13))
				{
					if (!dictionary.TryGetValue(text13, out var value3))
					{
						PngBitmapDecoder pngBitmapDecoder2 = new PngBitmapDecoder(new Uri(text13), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
						value3 = pngBitmapDecoder2.Frames[0];
					}
					int pixelHeight2 = value3.PixelHeight;
					int num11 = pixelHeight2 - num2 - num4;
					if (num11 < 0)
					{
						num11 = 0;
					}
					CroppedBitmap source3 = new CroppedBitmap(value3, new Int32Rect(num, num11, Math.Min(num3, value3.PixelWidth - num), Math.Min(num4, pixelHeight2 - num11)));
					byte[] bytes2;
					using (MemoryStream memoryStream2 = new MemoryStream())
					{
						PngBitmapEncoder pngBitmapEncoder2 = new PngBitmapEncoder();
						pngBitmapEncoder2.Frames.Add(BitmapFrame.Create(source3));
						pngBitmapEncoder2.Save(memoryStream2);
						bytes2 = memoryStream2.ToArray();
					}
					string text14 = ((!string.IsNullOrEmpty(processedDir)) ? Path.Combine(processedDir, text11) : Path.Combine(Path.GetTempPath(), text11));
					File.WriteAllBytes(text14, bytes2);
					result.Add(new DetectedDecorationFile
					{
						SourcePath = text14,
						WikiFilename = text11,
						Category = "Icon"
					});
				}
			}
			return true;
		}
		catch (Exception ex)
		{
			AppLogger.Warn("ExtractDecorationsFromSpriteMetadata failed: " + ex.Message);
			return false;
		}
	}

	/// <summary>Resolves a sprite texture name to an actual file path, trying direct name then sactx-prefixed glob.</summary>
	private static string? ResolveSpriteTexturePath(string exportDir, string textureName)
	{
		string direct = Path.Combine(exportDir, textureName + ".png");
		if (File.Exists(direct)) return direct;
		try
		{
			string[] sactxFiles = Directory.GetFiles(exportDir, "sactx-*-" + textureName + "-*.png");
			if (sactxFiles.Length > 0) return sactxFiles[0];
		}
		catch { }
		return null;
	}

	private static int ExtractSlotNumber(string spriteName)
	{
		Match match = Regex.Match(spriteName, "Slot(\\d+)$");
		return match.Success ? int.Parse(match.Groups[1].Value) : 0;
	}

	public static List<DetectedDecorationFile> DetectDecorationFiles(string exportDir, string progressionEventId, string mysteryName, bool isPet = false, MysteryEvent? mystery = null)
	{
		List<DetectedDecorationFile> list = new List<DetectedDecorationFile>();
		if (!Directory.Exists(exportDir))
		{
			return list;
		}
		string text = mysteryName.Replace(' ', '_');
		string text2 = FormatFileName(mysteryName, 0, suppressLevel: true).Replace(".png", "");
		string text3 = progressionEventId;
		if (isPet)
		{
			List<string> source = (from f in Directory.GetFiles(exportDir, "*_Decor_Pet.png")
				select Path.GetFileNameWithoutExtension(f).Replace("_Decor_Pet", "") into p
				where !string.Equals(p, progressionEventId, StringComparison.OrdinalIgnoreCase)
				select p).ToList();
			string eventSuffix = progressionEventId.Replace("SP_", "").Replace("Pet", "").TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
			string text4 = source.FirstOrDefault((string p) => p.Contains(eventSuffix, StringComparison.OrdinalIgnoreCase));
			if (text4 != null)
			{
				text3 = text4;
				AppLogger.Info($"DetectDecorationFiles: alt prefix '{text3}' for '{progressionEventId}'");
			}
		}
		string text5 = null;
		string directoryName = Path.GetDirectoryName(exportDir);
		string text6 = ((!string.IsNullOrEmpty(directoryName)) ? Path.GetDirectoryName(directoryName) : null);
		if (!string.IsNullOrEmpty(text6))
		{
			text5 = Path.Combine(text6, "Processed Images");
			if (!Directory.Exists(text5))
			{
				Directory.CreateDirectory(text5);
			}
		}
		// Wallpaper detection. Game v26.04.01+ uses suffix convention
		// "{progressionEventId}_PopupSharedArt.png" instead of legacy
		// "PopupSharedArt_{progressionEventId}.png". Both are searched for compat.
		string[] array = ((!isPet) ? new string[5]
		{
			progressionEventId + "_PopupSharedArt*.png",         // NEW (26.04.01+)
			progressionEventId + "_ProgressionPopupArt*.png",    // NEW
			"ProgressionPopupArt_" + progressionEventId + "*.png",
			"Popup_Progression_Art_" + progressionEventId + "*.png",
			"Popup_Header_" + progressionEventId + "*.png"
		} : new string[11]
		{
			progressionEventId + "_PopupSharedArt*.png",         // NEW (26.04.01+)
			text3 + "_PopupSharedArt*.png",                      // NEW (alt prefix for pets)
			"PopupSharedArt_" + progressionEventId + "*.png",
			"Popup_Shared_Art_" + progressionEventId + "*.png",
			"Popup_Header_Art_" + progressionEventId + "*.png",
			"Popup_Progression_Art_" + progressionEventId + "*.png",
			"ProgressionPopupArt_" + progressionEventId + "*.png",
			"PopupSharedArt_" + text3 + "*.png",
			"Popup_Shared_Art_" + text3 + "*.png",
			"Popup_Header_Art_" + text3 + "*.png",
			"Popup_Header_background_" + text3 + "*.png"
		});
		List<string> list2 = new List<string>();
		string[] array2 = array;
		foreach (string searchPattern in array2)
		{
			list2.AddRange(Directory.GetFiles(exportDir, searchPattern));
		}
		if (list2.Count > 0)
		{
			string sourcePath;
			if (isPet)
			{
				sourcePath = list2[0];
				double num2 = double.MaxValue;
				foreach (string item4 in list2)
				{
					try
					{
						BitmapDecoder bitmapDecoder = BitmapDecoder.Create(new Uri(item4), BitmapCreateOptions.None, BitmapCacheOption.None);
						int pixelWidth = bitmapDecoder.Frames[0].PixelWidth;
						int pixelHeight = bitmapDecoder.Frames[0].PixelHeight;
						double num3 = Math.Sqrt(Math.Pow(pixelWidth - 844, 2.0) + Math.Pow(pixelHeight - 760, 2.0));
						if (num3 < num2)
						{
							num2 = num3;
							sourcePath = item4;
						}
					}
					catch
					{
					}
				}
			}
			else
			{
				sourcePath = list2[0];
				double num4 = double.MaxValue;
				foreach (string item5 in list2)
				{
					try
					{
						BitmapDecoder bitmapDecoder2 = BitmapDecoder.Create(new Uri(item5), BitmapCreateOptions.None, BitmapCacheOption.None);
						int pixelWidth2 = bitmapDecoder2.Frames[0].PixelWidth;
						int pixelHeight2 = bitmapDecoder2.Frames[0].PixelHeight;
						double num5 = Math.Sqrt(Math.Pow(pixelWidth2 - 1440, 2.0) + Math.Pow(pixelHeight2 - 760, 2.0));
						if (num5 < num4)
						{
							num4 = num5;
							sourcePath = item5;
						}
					}
					catch
					{
					}
				}
			}
			string wikiFilename = text + ".png";
			string text7 = CopyToProcessed(sourcePath, wikiFilename, text5);
			list.Add(new DetectedDecorationFile
			{
				SourcePath = text7,
				WikiFilename = wikiFilename,
				Category = "Wallpaper",
				OptimizedSize = CheckOptMarker(text7)
			});
		}
		// Badge detection. Game v26.04.01+ uses suffix convention
		// "{progressionEventId}_MainHubBadgeArt.png" instead of legacy
		// "MainHubBadgeArt_{progressionEventId}.png". Both are searched.
		List<string> list3 = new List<string>
		{
			progressionEventId + "_MainHubBadgeArt*.png",        // NEW (26.04.01+)
			"MainHubBadgeArt_" + progressionEventId + "*.png",
			"MainHub_Badge_" + progressionEventId + "*.png"
		};
		if (text3 != progressionEventId)
		{
			list3.Add(text3 + "_MainHubBadgeArt*.png");          // NEW (alt prefix)
			list3.Add("MainHubBadgeArt_" + text3 + "*.png");
			list3.Add("MainHub_Badge_" + text3 + "*.png");
		}
		// Add at most one Badge entry. Game asset extraction sometimes saves the same
		// texture multiple times when it appears in multiple bundles (e.g.
		// MainHubBadgeArt_SP_WorldCup2026.png + _2.png + _3.png — bit-identical, just
		// dedup-suffixed by GetUniqueFilePath when the user re-runs extraction).
		// Without this guard each pattern × each duplicate file produced its own
		// DetectedDecorationFile, so the dialog showed 3× "Badge" entries.
		bool badgeAdded = false;
		foreach (string item6 in list3)
		{
			if (badgeAdded) break;
			string[] files = Directory.GetFiles(exportDir, item6);
			if (files.Length == 0) continue;
			// Pick canonical file: shortest filename = without "_2/_3/..." dedup suffix.
			string sourcePath2 = files
				.OrderBy(f => Path.GetFileName(f).Length)
				.ThenBy(f => f, StringComparer.OrdinalIgnoreCase)
				.First();
			string wikiFilename2 = FormatFileName(mysteryName, 1);
			string text8 = CopyToProcessed(sourcePath2, wikiFilename2, text5);
			list.Add(new DetectedDecorationFile
			{
				SourcePath = text8,
				WikiFilename = wikiFilename2,
				Category = "Badge",
				OptimizedSize = CheckOptMarker(text8)
			});
			badgeAdded = true;
		}
		int decoNum = ((!isPet) ? 1 : 0);
		List<string> list4 = (ExtractDecorationsFromSpriteMetadata(exportDir, progressionEventId, mysteryName, text, text5, isPet, ref decoNum, list, mystery) ? new List<string>() : (from f in Directory.GetFiles(exportDir, "*" + progressionEventId + "*Decorations*Atlas*.png")
			orderby f
			select f).ToList());
		if (list4.Count > 0)
		{
			string text9 = null;
			string directoryName2 = Path.GetDirectoryName(exportDir);
			string text10 = ((!string.IsNullOrEmpty(directoryName2)) ? Path.GetDirectoryName(directoryName2) : null);
			if (!string.IsNullOrEmpty(text10))
			{
				string text11 = Path.Combine(text10, "Processed Images");
				if (!Directory.Exists(text11))
				{
					Directory.CreateDirectory(text11);
				}
				text9 = text11;
			}
			if (string.IsNullOrEmpty(text9) && !string.IsNullOrEmpty(directoryName2))
			{
				string text12 = Path.Combine(directoryName2, "Export - Items");
				if (!Directory.Exists(text12))
				{
					Directory.CreateDirectory(text12);
				}
				text9 = text12;
			}
			List<(string, string, int)> list5 = new List<(string, string, int)>();
			int num7 = decoNum;
			foreach (string item7 in list4)
			{
				try
				{
					PngBitmapDecoder pngBitmapDecoder = new PngBitmapDecoder(new Uri(item7), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
					int pixelWidth3 = pngBitmapDecoder.Frames[0].PixelWidth;
					int pixelHeight3 = pngBitmapDecoder.Frames[0].PixelHeight;
					int num8 = pixelWidth3 / 256;
					int num9 = pixelHeight3 / 256;
					for (int num10 = 0; num10 < num9; num10++)
					{
						for (int num11 = 0; num11 < num8; num11++)
						{
							list5.Add((FormatFileName(mysteryName + "Decoration", num7++), "Decoration", num7 - 1));
						}
					}
					list5.Add((text + "_Icon.png", "Icon", -1));
				}
				catch
				{
				}
			}
			Dictionary<string, string> dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
			if (!string.IsNullOrEmpty(text9))
			{
				foreach (var item8 in list5)
				{
					string item = item8.Item1;
					string text13 = Path.Combine(text9, item);
					if (!File.Exists(text13))
					{
						continue;
					}
					try
					{
						byte[] array3 = File.ReadAllBytes(text13);
						if (OptimizationWindow.HasOptMarker(array3))
						{
							dictionary[item] = text13;
						}
					}
					catch
					{
					}
				}
			}
			foreach (string item9 in list4)
			{
				if (!string.IsNullOrEmpty(text9))
				{
					try
					{
						string text14 = Path.Combine(text9, Path.GetFileName(item9));
						if (!File.Exists(text14))
						{
							File.Copy(item9, text14);
						}
					}
					catch
					{
					}
				}
				int num12 = (isPet ? 3 : 5);
				bool flag = list.Any((DetectedDecorationFile r) => r.Category == "Icon");
				List<(AtlasTileType, byte[])> list6 = SliceDecorationAtlas(item9);
				foreach (var item10 in list6)
				{
					AtlasTileType item2 = item10.Item1;
					byte[] item3 = item10.Item2;
					int width = 0;
					int height = 0;
					string text15;
					string category;
					if (item2 == AtlasTileType.Icon)
					{
						if (flag)
						{
							continue;
						}
						text15 = text + "_Icon.png";
						category = "Icon";
						flag = true;
					}
					else
					{
						int num13 = list.Count((DetectedDecorationFile r) => r.Category == "Decoration");
						if (num13 >= num12)
						{
							continue;
						}
						text15 = FormatFileName(mysteryName + "Decoration", decoNum);
						category = "Decoration";
						width = 256;
						height = 256;
						decoNum++;
					}
					if (dictionary.TryGetValue(text15, out var value))
					{
						list.Add(new DetectedDecorationFile
						{
							SourcePath = value,
							WikiFilename = text15,
							Category = category,
							Width = width,
							Height = height,
							OptimizedSize = new FileInfo(value).Length
						});
					}
					else
					{
						string text16 = ((!string.IsNullOrEmpty(text9)) ? Path.Combine(text9, text15) : Path.Combine(Path.GetTempPath(), text15));
						File.WriteAllBytes(text16, item3);
						list.Add(new DetectedDecorationFile
						{
							SourcePath = text16,
							WikiFilename = text15,
							Category = category,
							Width = width,
							Height = height
						});
					}
				}
			}
		}
		List<string> list7 = (from f in Directory.GetFiles(exportDir, progressionEventId + "_Decor_*.png")
			orderby f
			select f).ToList();
		if (list7.Count == 0 && text3 != progressionEventId)
		{
			list7 = (from f in Directory.GetFiles(exportDir, text3 + "_Decor_*.png")
				orderby f
				select f).ToList();
		}
		foreach (string item11 in list7)
		{
			string wikiFilename3 = FormatFileName(mysteryName + "Decoration", decoNum);
			string text17 = CopyToProcessed(item11, wikiFilename3, text5);
			list.Add(new DetectedDecorationFile
			{
				SourcePath = text17,
				WikiFilename = wikiFilename3,
				Category = "Decoration",
				OptimizedSize = CheckOptMarker(text17)
			});
			decoNum++;
		}
		List<string> list8 = (from f in Directory.GetFiles(exportDir, progressionEventId + "_Set_Icon*.png")
			where !Path.GetFileName(f).Contains("Badge", StringComparison.OrdinalIgnoreCase)
			select f).ToList();
		if (list8.Count == 0 && text3 != progressionEventId)
		{
			list8 = (from f in Directory.GetFiles(exportDir, text3 + "_Set_Icon*.png")
				where !Path.GetFileName(f).Contains("Badge", StringComparison.OrdinalIgnoreCase)
				select f).ToList();
		}
		foreach (string item12 in list8)
		{
			if (!list.Any((DetectedDecorationFile r) => r.Category == "Icon"))
			{
				string wikiFilename4 = text + "_Icon.png";
				string text18 = CopyToProcessed(item12, wikiFilename4, text5);
				list.Add(new DetectedDecorationFile
				{
					SourcePath = text18,
					WikiFilename = wikiFilename4,
					Category = "Icon",
					OptimizedSize = CheckOptMarker(text18)
				});
			}
		}
		List<string> list9 = new List<string>();

		// Priority 1: Deterministic — resolve texture filename via PoolTag from game data
		if (mystery?.EventItemPoolTag is { Length: > 0 } poolTag)
		{
			var textureName = SpriteMetadataService.ResolveSkeletonForPoolTag(poolTag, exportDir);
			if (textureName != null)
			{
				var exactPath = Path.Combine(exportDir, textureName + ".png");
				if (File.Exists(exactPath))
					list9.Add(exactPath);
				else
				{
					// Try glob in case of suffix variations
					list9.AddRange(Directory.GetFiles(exportDir, textureName + "*.png"));
				}
			}
		}

		// Priority 2: Heuristic — CollectableItems pattern matching
		if (list9.Count == 0)
		{
			foreach (string item13 in new string[2] { progressionEventId, text3 }.Distinct())
			{
				list9.AddRange(Directory.GetFiles(exportDir, item13 + "*CollectableItems*.png"));
				list9.AddRange(Directory.GetFiles(exportDir, item13 + "*CollectableItem.png"));
			}
		}
		if (list9.Count == 0)
		{
			string text19;
			if (!progressionEventId.StartsWith("SP_"))
			{
				text19 = progressionEventId;
			}
			else
			{
				string text20 = progressionEventId;
				text19 = text20.Substring(3, text20.Length - 3);
			}
			string input = text19;
			Match match = Regex.Match(input, "\\d{4}");
			HashSet<string> genericWords = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Mystery", "Pet", "Event", "The" };
			List<string> list10 = (from Match m in Regex.Matches(input, "[A-Z][a-z]+")
				select m.Value into w
				where w.Length >= 3 && !genericWords.Contains(w)
				select w).ToList();
			if (match.Success && list10.Count > 0)
			{
				string[] files2 = Directory.GetFiles(exportDir, "SP_*CollectableItem*.png");
				foreach (string text21 in files2)
				{
					string fn = Path.GetFileNameWithoutExtension(text21);
					if (fn.Contains(match.Value) && list10.Any((string kw) => fn.Contains(kw, StringComparison.OrdinalIgnoreCase)))
					{
						list9.Add(text21);
					}
				}
			}
		}
		foreach (string item14 in list9.Distinct())
		{
			list.Add(new DetectedDecorationFile
			{
				SourcePath = item14,
				WikiFilename = "",
				Category = "EventItem"
			});
		}
		return list;
	}
}
