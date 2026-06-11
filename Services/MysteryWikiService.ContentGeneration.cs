using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MergeMansionWikiTools.Models;

namespace MergeMansionWikiTools.Services;

public static partial class MysteryWikiService
{
	// TTL cache for Mystery Pass reward/gallery templates (avoids re-fetching ~all templates on every check)
	private static readonly TimeSpan TemplatesCacheTtl = TimeSpan.FromHours(1);

	private static Dictionary<string, string>? _rewardTemplatesCache;

	private static DateTime _rewardTemplatesCachedAt;

	private static Dictionary<string, string>? _galleryTemplatesCache;

	private static DateTime _galleryTemplatesCachedAt;

	private const string PassXpHeader = "{{#Invoke:Utils|Icon|name=Silver Pass Ticket|suppressLevel=true|size=32}}";

	private const string GoldPassHeader = "{{#Invoke:Utils|Icon|name=Gold Pass Ticket|suppressLevel=true|size=32}}";

	private const string InventorySlotIcon = "{{#Invoke:Utils|Icon|name=InventorySlot|suppressLevel=true|size=24}}";

	private static readonly HashSet<string> NoLevelItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Missing Evidence", "Wild Card", "Clues Envelope", "Unlimited Energy" };

	// Game version 26.01.01 (2026-01-20) renamed the EN locale string for the InformantTip item ("TCE_Case01_InformantTip" loc key)
	// from "Missing Evidence" to "Wild Card". Mysteries with StartDate >= this cutoff use the new name in generated wiki content.
	private static readonly DateTime WildCardRenameDate = new DateTime(2026, 1, 20);

	internal static string GetInformantTipDisplayName(DateTime? mysteryStartDate)
		=> (mysteryStartDate.HasValue && mysteryStartDate.Value >= WildCardRenameDate) ? "Wild Card" : "Missing Evidence";

	private static readonly HashSet<string> PlainItemItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Brown Chest", "Fancy Blue Chest" };

	public static async Task<Dictionary<string, string>> FetchRewardTemplatesAsync(bool forceRefresh = false, CancellationToken ct = default)
	{
		Dictionary<string, string>? cached = _rewardTemplatesCache;
		if (!forceRefresh && cached != null && DateTime.UtcNow - _rewardTemplatesCachedAt < TemplatesCacheTtl)
		{
			AppLogger.Info($"FetchRewardTemplates: cache hit ({cached.Count} templates)");
			return cached;
		}
		Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		string listUrl = "https://merge-mansion.fandom.com/api.php?action=query&list=allpages&apprefix=Mystery_Pass/Rewards&apnamespace=10&aplimit=100&format=json";
		JsonDocument listDoc = JsonDocument.Parse(await Http.GetStringAsync(listUrl, ct));
		JsonElement allPages = listDoc.RootElement.GetProperty("query").GetProperty("allpages");
		List<string> templateTitles = new List<string>();
		foreach (JsonElement item in allPages.EnumerateArray())
		{
			string title = item.GetProperty("title").GetString();
			if (!string.IsNullOrEmpty(title))
			{
				templateTitles.Add(title);
			}
		}
		for (int i = 0; i < templateTitles.Count; i += 50)
		{
			ct.ThrowIfCancellationRequested();
			IEnumerable<string> batch = templateTitles.Skip(i).Take(50);
			string joined = string.Join("|", batch);
			string url = "https://merge-mansion.fandom.com/api.php?action=query&titles=" + Uri.EscapeDataString(joined) + "&prop=revisions&rvprop=content&rvslots=main&format=json";
			JsonDocument doc = JsonDocument.Parse(await Http.GetStringAsync(url, ct));
			foreach (JsonProperty page in doc.RootElement.GetProperty("query").GetProperty("pages").EnumerateObject())
			{
				if (page.Value.TryGetProperty("revisions", out var revisions))
				{
					string title2 = page.Value.GetProperty("title").GetString() ?? "";
					string content = revisions[0].GetProperty("slots").GetProperty("main").GetProperty("*")
						.GetString() ?? "";
					result[title2] = content;
					revisions = default(JsonElement);
				}
			}
		}
		_rewardTemplatesCache = result;
		_rewardTemplatesCachedAt = DateTime.UtcNow;
		return result;
	}

	// ── Gallery template detection ──────────────────────────────────
	/// <summary>Fetches all Mystery Pass/Gallery templates from wiki.</summary>
	public static async Task<Dictionary<string, string>> FetchGalleryTemplatesAsync(bool forceRefresh = false, CancellationToken ct = default)
	{
		Dictionary<string, string>? cached = _galleryTemplatesCache;
		if (!forceRefresh && cached != null && DateTime.UtcNow - _galleryTemplatesCachedAt < TemplatesCacheTtl)
		{
			AppLogger.Info($"FetchGalleryTemplates: cache hit ({cached.Count} templates)");
			return cached;
		}
		var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		string listUrl = "https://merge-mansion.fandom.com/api.php?action=query&list=allpages&apprefix=Mystery_Pass/Gallery&apnamespace=10&aplimit=100&format=json";
		var listDoc = JsonDocument.Parse(await Http.GetStringAsync(listUrl, ct));
		var allPages = listDoc.RootElement.GetProperty("query").GetProperty("allpages");
		var titles = new List<string>();
		foreach (var p in allPages.EnumerateArray())
		{
			string? t = p.GetProperty("title").GetString();
			if (!string.IsNullOrEmpty(t)) titles.Add(t);
		}
		for (int i = 0; i < titles.Count; i += 50)
		{
			ct.ThrowIfCancellationRequested();
			string joined = string.Join("|", titles.Skip(i).Take(50));
			string url = "https://merge-mansion.fandom.com/api.php?action=query&titles=" + Uri.EscapeDataString(joined) + "&prop=revisions&rvprop=content&rvslots=main&format=json";
			var doc = JsonDocument.Parse(await Http.GetStringAsync(url, ct));
			foreach (var page in doc.RootElement.GetProperty("query").GetProperty("pages").EnumerateObject())
			{
				if (page.Value.TryGetProperty("revisions", out var revs))
				{
					string title = page.Value.GetProperty("title").GetString() ?? "";
					string content = revs[0].GetProperty("slots").GetProperty("main").GetProperty("*").GetString() ?? "";
					result[title] = content;
				}
			}
		}
		_galleryTemplatesCache = result;
		_galleryTemplatesCachedAt = DateTime.UtcNow;
		return result;
	}

	/// <summary>
	/// Invalidates the reward/gallery template TTL caches when a matching template page was just edited,
	/// so the next fetch sees the freshly published content.
	/// </summary>
	private static void InvalidateTemplateCachesFor(string pageTitle)
	{
		if (pageTitle.StartsWith("Template:Mystery Pass/Rewards", StringComparison.OrdinalIgnoreCase))
		{
			_rewardTemplatesCache = null;
		}
		else if (pageTitle.StartsWith("Template:Mystery Pass/Gallery", StringComparison.OrdinalIgnoreCase))
		{
			_galleryTemplatesCache = null;
		}
	}

	/// <summary>
	/// Counts decoration slots in a Gallery template (including Pet Icon which is decoration #0).
	/// </summary>
	private static int CountGalleryDecorationSlots(string templateContent)
	{
		int count = 0;
		foreach (var line in templateContent.Split('\n'))
			if (line.Contains("Decoration #") || line.Contains("Decoration|") || line.Contains("Pet Icon"))
				count++;
		return count;
	}

	/// <summary>
	/// Finds a Gallery template variant matching the decoration count.
	/// Returns variant string ("" = default, "2", "Pet", etc.) or null if none found.
	/// </summary>
	public static string? FindMatchingGalleryVariant(int decoCount, bool isPet, Dictionary<string, string> galleryTemplates)
	{
		foreach (var (title, content) in galleryTemplates)
		{
			int idx = title.IndexOf("/Gallery", StringComparison.OrdinalIgnoreCase);
			if (idx < 0) continue;
			string suffix = title[(idx + "/Gallery".Length)..].TrimStart('/');
			bool isPetTemplate = suffix.StartsWith("Pet", StringComparison.OrdinalIgnoreCase);

			if (isPet != isPetTemplate) continue;

			int slots = CountGalleryDecorationSlots(content);
			if (slots == decoCount) return suffix.Length == 0 ? "" : suffix;
		}
		return null; // no match
	}

	/// <summary>
	/// Generates Gallery template content for a given decoration count (standard mystery).
	/// Follows the same pattern as existing templates.
	/// </summary>
	public static string GenerateGalleryTemplateContent(int decoCount, bool isPet)
	{
		var sb = new StringBuilder();
		sb.AppendLine("{{#tag:gallery|");
		sb.AppendLine("{{PAGENAME}}.png {{!}} Event Splash Art");
		if (isPet)
		{
			sb.AppendLine("{{ItemNameToFilename|{{PAGENAME}}Decoration|0}} {{!}} Pet Icon");
			for (int d = 1; d <= decoCount; d++)
				sb.AppendLine($"{{{{ItemNameToFilename|{{{{PAGENAME}}}}Decoration|{d}}}}} {{{{!}}}} Decoration #{d}");
		}
		else
		{
			for (int d = 1; d <= decoCount; d++)
				sb.AppendLine($"{{{{ItemNameToFilename|{{{{PAGENAME}}}}Decoration|{d}}}}} {{{{!}}}} Decoration #{d}");
		}
		sb.Append("}}");
		return sb.ToString();
	}

	/// <summary>
	/// Gets the next available Gallery variant name (numeric) for creating a new template.
	/// </summary>
	public static async Task<string> GetNextGalleryVariantNameAsync(bool isPet)
	{
		// forceRefresh: computing a NEW unique variant name must not rely on a stale TTL cache
		var templates = await FetchGalleryTemplatesAsync(forceRefresh: true);
		int maxNum = 0;
		foreach (var title in templates.Keys)
		{
			int idx = title.IndexOf("/Gallery", StringComparison.OrdinalIgnoreCase);
			if (idx < 0) continue;
			string suffix = title[(idx + "/Gallery".Length)..].TrimStart('/');
			if (isPet && !suffix.StartsWith("Pet", StringComparison.OrdinalIgnoreCase)) continue;
			if (!isPet && suffix.StartsWith("Pet", StringComparison.OrdinalIgnoreCase)) continue;

			string numPart = isPet ? suffix.Replace("Pet", "").TrimStart('/') : suffix;
			if (int.TryParse(numPart, out int num) && num > maxNum) maxNum = num;
			if (string.IsNullOrEmpty(numPart) && !isPet) maxNum = Math.Max(maxNum, 1); // default = "1"
		}
		return isPet ? $"Pet/{maxNum + 1}" : (maxNum + 1).ToString();
	}

	public static string GenerateRewardTemplate(MysteryEvent mystery, MysteryItemMapping? mapping)
	{
		var sb = new StringBuilder();
		bool isV2 = mystery.IsV2;

		// Assign sequential decoration numbering across Silver then Gold tiers
		int decoNum = 1;
		foreach (var level in mystery.SilverTier)
			foreach (var reward in level.Rewards)
				if (reward.Type == MysteryRewardType.Decoration)
					reward.ItemLevel = decoNum++;
		foreach (var level in mystery.GoldTier)
			foreach (var reward in level.Rewards)
				if (reward.Type == MysteryRewardType.Decoration)
					reward.ItemLevel = decoNum++;

		// ── Pre-compute XP totals for cumulative + % Progress columns ──
		// Cumulative is monotonically growing across L0 → regular L1..N → bonus/recurring.
		// The % Progress denominator is anchored at the LAST regular level (= L50), NOT the
		// grand total — so premium/bonus rows go above 100 % to reflect that those rewards
		// are extra effort beyond the standard pass completion.
		int regularEndCumulative = 0;
		int cumulativeXp = 0;

		// Pre-walk regular levels (L0 + L1..N) to find regular-end cumulative.
		int regularStart = mystery.HasZeroLevel ? 1 : 0;
		int regularMax = Math.Max(mystery.FreeTier.Count,
			Math.Max(mystery.SilverTier.Count, mystery.GoldTier.Count));
		// L0 contributes 0 XP, no need to add it.
		for (int i = regularStart; i < regularMax; i++)
		{
			var freeLv = i < mystery.FreeTier.Count ? mystery.FreeTier[i] : null;
			var silverLv = i < mystery.SilverTier.Count ? mystery.SilverTier[i] : null;
			var goldLv = i < mystery.GoldTier.Count ? mystery.GoldTier[i] : null;
			regularEndCumulative += freeLv?.XpRequired ?? silverLv?.XpRequired ?? goldLv?.XpRequired ?? 0;
		}

		string EmitPassXpRow(int xp, bool isZeroLevel)
		{
			cumulativeXp += xp;
			double pct = regularEndCumulative > 0 ? (double)cumulativeXp / regularEndCumulative * 100.0 : 0.0;
			// Anchor "100" at the regular-end row to avoid 99.99/100.01 float drift.
			string pctStr = Math.Abs(pct - 100.0) < 0.05
				? "100"
				: pct.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
			// nowrap on the cell keeps "'''N''' / cumN" on a single line; otherwise the
			// space between numerator and slash lets MediaWiki break it into two visual rows.
			if (isZeroLevel)
				return "| style=\"white-space:nowrap\" | {{Pass XP}} '''-''' / 0\n| -";
			return $"| style=\"white-space:nowrap\" | {{{{Pass XP}}}} '''{xp}''' / {cumulativeXp}\n| {pctStr} %";
		}

		// ── Table header ──
		sb.AppendLine("{| class=\"article-table\"");
		sb.AppendLine("! Level");
		sb.AppendLine("! style=\"white-space:nowrap\" | {{Pass XP}} Points Needed");
		sb.AppendLine("! % Progress");
		sb.AppendLine("! F2P  Reward");
		if (isV2)
		{
			sb.AppendLine("! {{#Invoke:Utils|Icon|name=Silver Pass Ticket|suppressLevel=true|size=32}} Silver Pass Reward");
			sb.AppendLine("! {{#Invoke:Utils|Icon|name=Gold Pass Ticket|suppressLevel=true|size=32}} Gold Pass Reward");
		}
		else
		{
			sb.AppendLine("! {{#Invoke:Utils|Icon|name=Gold Pass Ticket|suppressLevel=true|size=32}} Premium Pass Reward");
		}

		// ── Level 0 ──
		if (mystery.HasZeroLevel)
		{
			sb.AppendLine("|-");
			sb.AppendLine("| 0");
			sb.AppendLine(EmitPassXpRow(0, isZeroLevel: true));

			// Free L0 from data (typically Energy 10)
			var freeL0 = mystery.FreeTier.Count > 0 ? mystery.FreeTier[0] : null;
			sb.AppendLine($"| {FormatRewards(freeL0?.Rewards, mystery, "free", true)}");

			if (isV2)
			{
				sb.AppendLine($"| {FormatPerkLevel0(mystery.Track1PerkData)}");
				// Track2 data includes inherited Track1 perks — subtract to show only Gold-specific contribution
				sb.AppendLine($"| {FormatPerkLevel0(SubtractPerks(mystery.Track2PerkData, mystery.Track1PerkData))}");
			}
			else
			{
				var premL0 = mystery.SilverTier.Count > 0 ? mystery.SilverTier[0] : null;
				sb.AppendLine($"| {FormatRewards(premL0?.Rewards, mystery, "gold", true)}");
			}
		}

		// ── Regular levels ──
		int startIndex = mystery.HasZeroLevel ? 1 : 0;
		int maxLevels = Math.Max(mystery.FreeTier.Count,
			Math.Max(mystery.SilverTier.Count, mystery.GoldTier.Count));

		for (int i = startIndex; i < maxLevels; i++)
		{
			var freeLevel = i < mystery.FreeTier.Count ? mystery.FreeTier[i] : null;
			var silverLevel = i < mystery.SilverTier.Count ? mystery.SilverTier[i] : null;
			var goldLevel = i < mystery.GoldTier.Count ? mystery.GoldTier[i] : null;
			int xp = freeLevel?.XpRequired ?? silverLevel?.XpRequired ?? goldLevel?.XpRequired ?? 0;

			sb.AppendLine("|-");
			sb.AppendLine($"| {i}");
			sb.AppendLine(EmitPassXpRow(xp, isZeroLevel: false));
			sb.AppendLine($"| {FormatRewards(freeLevel?.Rewards, mystery, "free", true)}");

			if (isV2)
			{
				sb.AppendLine($"| {FormatRewards(silverLevel?.Rewards, mystery, "silver", true)}");
				sb.AppendLine($"| {FormatRewards(goldLevel?.Rewards, mystery, "gold", true)}");
			}
			else
			{
				sb.AppendLine($"| {FormatRewards(silverLevel?.Rewards, mystery, "gold", true)}");
			}
		}

		// ── Bonus levels (from data) ──
		for (int j = 0; j < mystery.BonusTier.Count; j++)
		{
			var bonus = mystery.BonusTier[j];
			sb.AppendLine("|-");
			sb.AppendLine($"| {{{{PremiumLevel|{j + 1}}}}}");
			sb.AppendLine(EmitPassXpRow(bonus.XpRequired, isZeroLevel: false));
			sb.AppendLine("| {{Dash}}");
			string bonusContent = FormatRewards(bonus.Rewards, mystery, "free", true);
			if (isV2)
				sb.AppendLine($"| colspan = 2 style = \"text-align: center\" | {bonusContent}");
			else
				sb.AppendLine($"| {bonusContent}");
		}

		// ── Recurring levels (V1 only) ──
		if (mystery.RecurringFreeTier.Count > 0 || mystery.RecurringPremiumTier.Count > 0)
		{
			int recurMax = Math.Max(mystery.RecurringFreeTier.Count, mystery.RecurringPremiumTier.Count);
			for (int k = 0; k < recurMax; k++)
			{
				var recurFree = k < mystery.RecurringFreeTier.Count ? mystery.RecurringFreeTier[k] : null;
				var recurPrem = k < mystery.RecurringPremiumTier.Count ? mystery.RecurringPremiumTier[k] : null;
				int recurXp = recurFree?.XpRequired ?? recurPrem?.XpRequired ?? 0;

				sb.AppendLine("|-");
				sb.AppendLine($"| {{{{PremiumLevel|{k + 1}}}}}");
				sb.AppendLine(EmitPassXpRow(recurXp, isZeroLevel: false));
				sb.AppendLine($"| {FormatRewards(recurFree?.Rewards, mystery, "free", true)}");
				sb.AppendLine($"| {FormatRewards(recurPrem?.Rewards, mystery, "gold", true)}");
			}
		}

		sb.AppendLine("|}");
		return sb.ToString();
	}

	/// <summary>
	/// Returns the difference between two perk sets (total - inherited = tier-specific).
	/// Track2 data includes inherited Track1 perks — subtracting gives the Gold-only contribution.
	/// </summary>
	private static MysteryPerkData? SubtractPerks(MysteryPerkData? total, MysteryPerkData? inherited)
	{
		if (total == null) return null;
		if (inherited == null) return total;
		return new MysteryPerkData
		{
			FreeDailyGems = total.FreeDailyGems - inherited.FreeDailyGems,
			ExtraInventorySlots = total.ExtraInventorySlots - inherited.ExtraInventorySlots,
			EventXpBonus = total.EventXpBonus - inherited.EventXpBonus,
		};
	}

	private static string FormatPerkLevel0(MysteryPerkData? perkData)
	{
		if (perkData == null)
			return "5 {{Gems}}/day <br> 3 {{#Invoke:Utils|Icon|name=InventorySlot|suppressLevel=true|size=24}} [[Inventory]] slots";

		var parts = new List<string>();
		if (perkData.FreeDailyGems > 0)
			parts.Add($"{perkData.FreeDailyGems} {{{{Gems}}}}/day");
		if (perkData.ExtraInventorySlots > 0)
			parts.Add($"{perkData.ExtraInventorySlots} {{{{#Invoke:Utils|Icon|name=InventorySlot|suppressLevel=true|size=24}}}} [[Inventory]] slots");
		if (perkData.EventXpBonus > 0)
			parts.Add($"{perkData.EventXpBonus} Event Points");
		return parts.Count > 0 ? string.Join(" <br> ", parts) : "{{Dash}}";
	}

	private static string FormatRewards(List<MysteryReward>? rewards, MysteryEvent mystery, string tier = "free", bool dashIfEmpty = false)
	{
		string empty = dashIfEmpty ? "{{Dash}}" : "?";
		if (rewards == null || rewards.Count == 0)
			return empty;

		var list = new List<string>();
		foreach (var reward in rewards)
		{
			if (reward.Type != MysteryRewardType.Perk)
			{
				string text = FormatSingleReward(reward, mystery, tier);
				if (!string.IsNullOrEmpty(text))
					list.Add(text);
			}
		}
		return list.Count > 0 ? string.Join(" <br> ", list) : empty;
	}

	private static string FormatSingleReward(MysteryReward reward, MysteryEvent mystery, string tier)
	{
		return reward.Type switch
		{
			MysteryRewardType.Coins => $"{{{{Coins}}}} {reward.Amount}",
			MysteryRewardType.Diamonds => $"{{{{Gems}}}} {reward.Amount}",
			MysteryRewardType.Energy => $"{{{{Energy}}}} {reward.Amount}",
			MysteryRewardType.Experience => $"{{{{XP}}}} {reward.Amount}",
			MysteryRewardType.Item => FormatItemReward(reward),
			MysteryRewardType.Decoration => FormatDecorationReward(reward, tier),
			MysteryRewardType.CardPack => FormatCardPack(reward),
			MysteryRewardType.Pet => "{{Decoration|silver|0|text={{{pet}}}}}",
			MysteryRewardType.InformantTip => FormatInformantTip(reward, mystery),
			MysteryRewardType.CooldownRemover => FormatCooldownRemover(reward),
			MysteryRewardType.ActivateInfiniteEnergy => FormatActivateInfiniteEnergy(reward),
			MysteryRewardType.SkipTime => FormatSkipTime(reward),
			_ => "",
		};
	}

	private static string FormatItemReward(MysteryReward reward)
	{
		string text = reward.ItemKey ?? "";
		// Bonus chests: SP_{event}_MysteryPassChest{A-E}_01 → Challenge Chest 1-5
		var chestMatch = System.Text.RegularExpressions.Regex.Match(text, @"MysteryPassChest([A-E])_?\d+$");
		if (chestMatch.Success)
		{
			int chestLevel = chestMatch.Groups[1].Value[0] - 'A' + 1;
			return $"{{{{Item/Group|Challenge Chest|{chestLevel}|iconLevel=1}}}}";
		}
		if (text.StartsWith("TimeSkipBoosterSingle") && reward.DurationMs.HasValue)
		{
			string text2 = FormatDuration(reward.DurationMs.Value);
			return text2 + " {{Item/Group|Hourglass|1}}";
		}
		if (text.StartsWith("TimeSkipBooster_"))
		{
			int value = ((!text.Contains("_02")) ? 1 : 2);
			return $"{{{{Item/Group|Time Skip Booster|{value}}}}}";
		}
		if (text.StartsWith("InfiniteEnergy"))
		{
			int value2 = (text.Contains("Mid") ? 2 : ((!text.Contains("Big")) ? 1 : 3));
			return $"{{{{Item/nolevel|Unlimited Energy|{value2}}}}}";
		}
		string text3 = !string.IsNullOrEmpty(reward.ItemDisplayName) ? reward.ItemDisplayName
			: !string.IsNullOrEmpty(reward.ItemKey) ? reward.ItemKey : "Unknown";
		if (NoLevelItems.Contains(text3))
		{
			if (reward.ItemLevel.HasValue && reward.ItemLevel.Value > 0)
			{
				return $"{{{{Item/nolevel|{text3}|{reward.ItemLevel.Value}}}}}";
			}
			return "{{Item/nolevel|" + text3 + "}}";
		}
		if (PlainItemItems.Contains(text3))
		{
			if (reward.ItemLevel.HasValue && reward.ItemLevel.Value > 0)
			{
				return (reward.Amount > 1) ? $"{{{{Item|{text3}|{reward.ItemLevel.Value}}}}} x{reward.Amount}" : $"{{{{Item|{text3}|{reward.ItemLevel.Value}}}}}";
			}
			return "{{Item|" + text3 + "}}";
		}
		if (reward.ItemLevel.HasValue && reward.ItemLevel.Value > 0)
		{
			return (reward.Amount > 1) ? $"{{{{Item/Group|{text3}|{reward.ItemLevel.Value}}}}} x{reward.Amount}" : $"{{{{Item/Group|{text3}|{reward.ItemLevel.Value}}}}}";
		}
		return (reward.Amount > 1) ? $"{{{{Item/nolevel|{text3}}}}} x{reward.Amount}" : ("{{Item/nolevel|" + text3 + "}}");
	}

	private static string FormatDecorationReward(MysteryReward reward, string tier)
	{
		int valueOrDefault = reward.ItemLevel.GetValueOrDefault();
		return $"{{{{Decoration|{tier}|{valueOrDefault}}}}}";
	}

	private static string FormatCardPack(MysteryReward reward)
	{
		int value = 1;
		if (reward.CardPackId != null)
		{
			Match match = Regex.Match(reward.CardPackId, "(\\d)Stars");
			if (match.Success)
			{
				value = int.Parse(match.Groups[1].Value);
			}
		}
		return $"{{{{Item/nolevel|Clues Envelope|{value}}}}}";
	}

	private static string FormatCooldownRemover(MysteryReward reward)
	{
		// "Unlimited Production" booster (AAR_CooldownRemover_FTUE wording in dialogues).
		// Game UI shows duration as label on the icon. DurationMs is in milliseconds.
		string durationText = reward.DurationMs.HasValue
			? FormatDuration(reward.DurationMs.Value)
			: "";
		return string.IsNullOrEmpty(durationText)
			? "{{Item/nolevel|Unlimited Production}}"
			: $"{durationText} {{{{Item/nolevel|Unlimited Production}}}}";
	}

	private static string FormatActivateInfiniteEnergy(MysteryReward reward)
	{
		// Auto-activated Unlimited Energy booster (RewardActivateInfiniteEnergy). Distinct from
		// the inventory item Unlimited Energy chain but shares the display name on wiki;
		// level 0 disambiguates the auto-activate variant from item levels 1/2/3.
		string durationText = reward.DurationMs.HasValue
			? FormatDuration(reward.DurationMs.Value)
			: "";
		return string.IsNullOrEmpty(durationText)
			? "{{Item/nolevel|Unlimited Energy|0}}"
			: $"{durationText} {{{{Item/nolevel|Unlimited Energy|0}}}}";
	}

	private static string FormatSkipTime(MysteryReward reward)
	{
		// RewardSkipTime — auto-applied Time Skip booster (advances all producer timers on
		// the specified merge boards by DurationToSkip ms).
		string durationText = reward.DurationMs.HasValue
			? FormatDuration(reward.DurationMs.Value)
			: "";
		return string.IsNullOrEmpty(durationText)
			? "{{Item/nolevel|Time Skip}}"
			: $"{durationText} {{{{Item/nolevel|Time Skip}}}}";
	}

	private static string FormatInformantTip(MysteryReward reward, MysteryEvent mystery)
	{
		string? informantTipCardId = reward.InformantTipCardId;
		int value = ((informantTipCardId == null || !informantTipCardId.Contains("Special")) ? 1 : 2);
		string itemName = GetInformantTipDisplayName(mystery.StartDate);
		return $"{{{{Item/nolevel|{itemName}|{value}}}}}";
	}

	private static string FormatDuration(long ms)
	{
		long num = ms / 60000;
		long num2 = num / 60;
		long num3 = num % 60;
		if (num2 > 0 && num3 > 0)
		{
			return $"{num2} h {num3} m";
		}
		if (num2 <= 0)
		{
			return $"{num3} m";
		}
		return $"{num2} h";
	}

	public static string GenerateEventPage(MysteryEvent mystery, string? rewardVariant)
	{
		return GenerateEventPageWithDialogues(mystery, rewardVariant, null);
	}

	public static string GenerateEventItemPage(MysteryEvent mystery, DataService? ds = null, WikiMappingCache? wikiMapping = null)
	{
		StringBuilder stringBuilder = new StringBuilder();
		string eventPageTitle = mystery.WikiStatus.SuggestedPageTitle ?? mystery.Name;
		string year = FormatYearColumn(mystery);
		string eventItemName = mystery.EventItemName ?? "{{PAGENAME}}";
		string strippedName = StripParenthetical(eventItemName);
		bool hasParenthetical = strippedName != eventItemName;
		bool hasDisambiguation = eventPageTitle != mystery.Name;
		stringBuilder.Append($"{{{{#vardefine:EventName|{eventPageTitle}}}}}");
		if (hasDisambiguation)
			stringBuilder.AppendLine($"{{{{#vardefine:EventDisplayName|{mystery.Name}}}}}");
		if (hasParenthetical)
		{
			stringBuilder.AppendLine($"{{{{#vardefine:DisplayTitle|{strippedName}}}}}");
			stringBuilder.AppendLine("{{DISPLAYTITLE:{{#var:DisplayTitle}}}}");
		}
		stringBuilder.AppendLine("{{Infobox Items");
		stringBuilder.AppendLine("| image1 = ");
		stringBuilder.AppendLine("{{#tag:gallery|");
		stringBuilder.AppendLine("{{ItemNameToFilename|{{PAGENAME}}|1}} {{!}} Level 1");
		stringBuilder.AppendLine("{{ItemNameToFilename|{{PAGENAME}}|{{#Invoke:Items|GetItemMaxLevelFromChainName}}}} {{!}} Level {{#Invoke:Items|GetItemMaxLevelFromChainName}}");
		stringBuilder.AppendLine("}}");
		if (hasParenthetical)
			stringBuilder.AppendLine("| title1 = {{#var:DisplayTitle}}");
		stringBuilder.AppendLine("| type   = Drop Item");
		stringBuilder.AppendLine(hasDisambiguation
			? "| source = Merging Items during {{Item/nolevel|{{#var:EventName}}|displayName={{#var:EventDisplayName}}}} Event"
			: "| source = Merging Items during {{Item/nolevel|{{#var:EventName}}}} Event");
		stringBuilder.AppendLine("}}");
		string itemGroupRef = hasParenthetical ? "{{Item/Group|{{PAGENAME}}|4|displayName={{#var:DisplayTitle}}}}" : "{{Item/Group|{{PAGENAME}}|4}}";
		string eventRef = hasDisambiguation
			? "{{Item/nolevel|{{#var:EventName}}|displayName={{#var:EventDisplayName}}}}"
			: "{{Item/nolevel|{{#var:EventName}}}}";
		stringBuilder.AppendLine($"{itemGroupRef} is an item in '''''Merge Mansion'''''.  It is used in the {eventRef} [[Events|Event]] of {year}.");
		stringBuilder.AppendLine();
		string itemRef = hasParenthetical ? "{{Item/nolevel|{{PAGENAME}}|1|displayName={{#var:DisplayTitle}}}}" : "{{Item/nolevel|{{PAGENAME}}|1}}";
		string displayName = hasParenthetical ? "{{#var:DisplayTitle}}" : "{{PAGENAME}}";
		stringBuilder.AppendLine($"* {itemRef}  can spawn from any merge action which takes place on the normal board and also on any Story Event boards like other [[Events#Progression_Events|Mystery Pass events]].");
		stringBuilder.AppendLine($"* {itemRef}  can be merged up to level 4, which then gives the max points of 20.");
		stringBuilder.AppendLine($"* Similar to {{{{XP}}}}[[XP]] {displayName} can be collected by tapping.");
		stringBuilder.AppendLine("* It is advisable to leave 2 empty spots whilst merging, as the priority order for drops whilst merging goes to:");
		stringBuilder.AppendLine($"# {itemRef}");
		stringBuilder.AppendLine("# Double Bubbles");
		stringBuilder.AppendLine("# {{XP}} XP");
		stringBuilder.AppendLine();
		stringBuilder.AppendLine($"Therefore, to maximise the {displayName} drops and XP, it is best to keep 2 free spots for them to drop.");
		stringBuilder.AppendLine();
		stringBuilder.AppendLine("== Descriptions == ");
		for (int i = 1; i <= 4; i++)
		{
			stringBuilder.AppendLine($"{{{{Item/Icon|{{{{PAGENAME}}}}|{i}}}}} {{{{#Invoke:Items|GetItemDescFromChainName|{i}}}}}");
			stringBuilder.AppendLine();
		}
		stringBuilder.AppendLine("== Statistics == ");
		stringBuilder.AppendLine("=== Merge Stages === ");
		ParsedChain parsedChain = null;
		if (ds != null && !string.IsNullOrEmpty(mystery.EventItemType))
		{
			// Find chain by ConfigKey derived from EventItemType (e.g. "CBE_X_01" → "CBE_X")
			// This finds the original chain WITHOUT merged aliases
			int lastUnderscore = mystery.EventItemType.LastIndexOf('_');
			if (lastUnderscore > 0)
			{
				string configKey = mystery.EventItemType[..lastUnderscore];
				parsedChain = ds.Chains.FirstOrDefault(c =>
					string.Equals(c.ConfigKey, configKey, StringComparison.OrdinalIgnoreCase));
			}
			// Fallback: match by ItemType presence
			if (parsedChain == null)
				parsedChain = ds.Chains.FirstOrDefault(c => c.Items.Any(i => i.ItemType == mystery.EventItemType));
		}
		if (parsedChain != null)
		{
			// If chain was merged with aliases, create a filtered copy with only non-alias items
			if (parsedChain.Items.Any(i => i.IsAlias))
			{
				parsedChain = new ParsedChain
				{
					ConfigKey = parsedChain.ConfigKey,
					OriginalName = parsedChain.OriginalName,
					DisplayName = parsedChain.DisplayName,
					Items = parsedChain.Items.Where(i => !i.IsAlias).ToList(),
					PoolTag = parsedChain.PoolTag,
					IsNameFromWiki = parsedChain.IsNameFromWiki,
				};
			}
			WikiTableGenerator wikiTableGenerator = new WikiTableGenerator(ds, wikiMapping);
			string? captionOverride = hasParenthetical ? "{{#var:DisplayTitle}}" : null;
			stringBuilder.Append(wikiTableGenerator.Generate(parsedChain, mystery.EventItemName ?? "{{PAGENAME}}", lowPrices: false,
				captionOverride: captionOverride));
		}
		else
		{
			stringBuilder.AppendLine("{| class=\"article-table\"");
			stringBuilder.AppendLine($"|+ <u>{displayName}</u>");
			stringBuilder.AppendLine("! Lvl");
			stringBuilder.AppendLine("! Image");
			stringBuilder.AppendLine("! Item");
			stringBuilder.AppendLine("! [[Coins|Sells for]]");
			stringBuilder.AppendLine("! Drops");
			for (int lvl = 1; lvl <= 4; lvl++)
			{
				stringBuilder.AppendLine("|-");
				stringBuilder.AppendLine($"| {lvl}");
				stringBuilder.AppendLine($"| style=\"text-align:center;\" |{{{{Item/Icon|{{{{PAGENAME}}}}|{lvl}}}}}");
				stringBuilder.AppendLine($"| <u>{{{{#Invoke:Items|GetItemNameFromChainName|{lvl}}}}}</u>");
				stringBuilder.AppendLine($"| {{{{#Invoke:Items|GetItemPriceByLevel|{lvl}}}}}");
				stringBuilder.AppendLine("| {{Dash}}");
			}
			stringBuilder.AppendLine("|}");
		}
		stringBuilder.AppendLine();
		stringBuilder.AppendLine("=== [[Double Bubble]]s === ");
		stringBuilder.AppendLine("{{#Invoke:Items|GetItemBubbleTableFromChainName}}");
		return stringBuilder.ToString();
	}

	private static string StripParenthetical(string name)
	{
		int num = name.LastIndexOf('(');
		if (num > 0)
		{
			return name.Substring(0, num).TrimEnd();
		}
		return name;
	}

	private static string FormatStartDate(DateTime? date)
	{
		if (!date.HasValue)
		{
			return "Unknown";
		}
		DateTime value = date.Value;
		return $"{FormatDateNoYear(value)}, {value.Year}";
	}

	private static string FormatDateNoYear(DateTime d)
	{
		string value = ((d.Day % 10 == 1 && d.Day != 11) ? "st" : ((d.Day % 10 == 2 && d.Day != 12) ? "nd" : ((d.Day % 10 == 3 && d.Day != 13) ? "rd" : "th")));
		string value2 = d.ToString("MMMM", CultureInfo.InvariantCulture);
		return $"{value2} {d.Day}{value}";
	}

	/// <summary>Formats the Year column: "2024" or "2024 - 2025" if event spans across years.</summary>
	private static string FormatYearColumn(MysteryEvent mystery)
	{
		if (!mystery.StartDate.HasValue) return "????";
		int startYear = mystery.StartDate.Value.Year;
		if (mystery.EndDate.HasValue && mystery.EndDate.Value.Year > startYear)
			return $"{startYear} - {mystery.EndDate.Value.Year}";
		return startYear.ToString();
	}

	public static string GenerateEventPageWithDialogues(MysteryEvent mystery, string? rewardVariant, DialogueService? dialogueService)
	{
		StringBuilder stringBuilder = new StringBuilder();
		string eventItemName = mystery.EventItemName ?? "Unknown";
		string startDate = FormatStartDate(mystery.StartDate);
		bool flag = mystery.MysteryType == MysteryType.Pet;
		string suggestedPageTitle = mystery.WikiStatus.SuggestedPageTitle;
		string eventDisplayName = (suggestedPageTitle != null && suggestedPageTitle != mystery.Name) ? mystery.Name : "{{PAGENAME}}";
		string strippedItemName = StripParenthetical(eventItemName);
		bool itemHasParenthetical = strippedItemName != eventItemName;
		stringBuilder.Append($"{{{{#vardefine:EventItem|{eventItemName}}}}}");
		stringBuilder.Append(itemHasParenthetical
			? $"{{{{#vardefine:EventItemDisplayName|{strippedItemName}}}}}"
			: "{{#vardefine:EventItemDisplayName|{{#var:EventItem}}}}");
		stringBuilder.AppendLine($"{{{{#vardefine:EventDisplayName|{eventDisplayName}}}}}");
		stringBuilder.AppendLine($"{{{{Mystery Pass/Intro|startingDate={startDate}}}}}");
		stringBuilder.AppendLine();
		stringBuilder.AppendLine("== Event Mechanics == ");
		if (mystery.IsV2)
		{
			stringBuilder.AppendLine("{{Mystery Pass/Event Mechanics}}");
		}
		else
		{
			int durationDays = mystery.DurationDays ?? 21;
			int mainLevels = mystery.FreeTier.Count - (mystery.HasZeroLevel ? 1 : 0);
			stringBuilder.AppendLine($"{{{{Mystery Pass/Event Mechanics/Old|duration={durationDays}|levels={mainLevels}}}}}");
		}
		stringBuilder.AppendLine();
		stringBuilder.AppendLine("== Item Descriptions == ");
		stringBuilder.AppendLine("{{Mystery Pass/ItemDesc}}");
		stringBuilder.AppendLine();
		stringBuilder.AppendLine("== Statistics == ");
		stringBuilder.AppendLine("{{Mystery Pass/Event Item}}");
		stringBuilder.AppendLine();
		stringBuilder.AppendLine("== Rewards == ");
		if (rewardVariant != null)
		{
			string rewardVariantDisplay = rewardVariant;
			string pageTitle = mystery.WikiStatus.SuggestedPageTitle ?? mystery.Name;
			if (rewardVariant == pageTitle)
				rewardVariantDisplay = "{{PAGENAME}}";
			// Build template call: base template = "{{Mystery Pass/Rewards}}", variant = "{{Mystery Pass/Rewards/X}}"
			string rewardsCall = string.IsNullOrEmpty(rewardVariantDisplay)
				? "Mystery Pass/Rewards"
				: $"Mystery Pass/Rewards/{rewardVariantDisplay}";

			if (flag && !string.IsNullOrEmpty(mystery.PetName))
			{
				string petDisplayName = FormatPetDisplayName(mystery.PetName);
				stringBuilder.AppendLine($"{{{{{rewardsCall}|pet={petDisplayName}}}}}");
			}
			else
			{
				stringBuilder.AppendLine($"{{{{{rewardsCall}}}}}");
			}
		}
		else if (flag && !string.IsNullOrEmpty(mystery.PetName))
		{
			string petDisplayName = FormatPetDisplayName(mystery.PetName);
			stringBuilder.AppendLine($"{{{{Mystery Pass/Rewards/{{{{PAGENAME}}}}|pet={petDisplayName}}}}}");
		}
		else
		{
			// Fallback for standard: {{PAGENAME}} variant (caller should provide rewardVariant from GetNextVariantNameAsync)
			stringBuilder.AppendLine("{{Mystery Pass/Rewards/{{PAGENAME}}}}");
		}
		stringBuilder.AppendLine();
		stringBuilder.AppendLine("== Dialogue == ");
		List<DialogueGroup> list = null;
		if (dialogueService != null && dialogueService.HasDialogues(mystery.ProgressionEventId))
		{
			string petName = ((!string.IsNullOrEmpty(mystery.PetName)) ? FormatPetDisplayName(mystery.PetName) : null);
			int decoCount = CountDecorations(mystery);
			var orderedSlots = GetOrderedDecorationSlotIds(mystery);
			list = dialogueService.GetMysteryDialogues(mystery.ProgressionEventId, mystery.MysteryType, petName, decoCount, orderedSlots);
		}
		if (list != null && list.Count > 0)
		{
			stringBuilder.Append(DialogueService.FormatAsWikiTabber(list));
		}
		else
		{
			stringBuilder.AppendLine("<tabber>");
			stringBuilder.AppendLine("|-| Event Intro =");
			stringBuilder.AppendLine();
			if (flag)
			{
				string petName = !string.IsNullOrEmpty(mystery.PetName) ? FormatPetDisplayName(mystery.PetName) : "Pet";
				stringBuilder.AppendLine($"|-| Getting {petName} =");
				stringBuilder.AppendLine();
				stringBuilder.AppendLine($"|-| Getting {petName} =");
				stringBuilder.AppendLine();
				stringBuilder.AppendLine("|-| Decoration Level 1 =");
				stringBuilder.AppendLine();
				stringBuilder.AppendLine("|-| Decoration Level 2 =");
				stringBuilder.AppendLine();
			}
			else
			{
				stringBuilder.AppendLine("|-| Getting Event Item L4 =");
				stringBuilder.AppendLine();
				for (int i = 1; i <= 5; i++)
				{
					stringBuilder.AppendLine($"|-| Decoration Level {i} =");
					stringBuilder.AppendLine();
				}
			}
			stringBuilder.AppendLine("|-| Event Outro =");
			stringBuilder.AppendLine();
			stringBuilder.AppendLine("</tabber>");
		}
		stringBuilder.AppendLine();
		stringBuilder.AppendLine("== Gallery == ");
		var gv = mystery.WikiStatus.MatchingGalleryVariant;
		string galleryTemplate;
		if (gv != null)
			galleryTemplate = gv == "" ? "{{Mystery Pass/Gallery}}" : $"{{{{Mystery Pass/Gallery/{gv}}}}}";
		else
			galleryTemplate = flag ? "{{Mystery Pass/Gallery/Pet}}" : "{{Mystery Pass/Gallery}}"; // fallback
		stringBuilder.AppendLine(galleryTemplate);
		return stringBuilder.ToString();
	}

	public static string GenerateGallerySection(MysteryEvent mystery)
	{
		StringBuilder stringBuilder = new StringBuilder();
		bool isPet = mystery.MysteryType == MysteryType.Pet;
		string img = mystery.WikiImageName;
		stringBuilder.AppendLine("<gallery>");
		stringBuilder.AppendLine($"{img} Wallpaper.png|Wallpaper");
		stringBuilder.AppendLine($"{img} Badge.png|Badge");
		if (isPet)
		{
			stringBuilder.AppendLine($"{img} Decoration 1.png|Decoration 1");
			stringBuilder.AppendLine($"{img} Decoration 2.png|Decoration 2");
			if (!string.IsNullOrEmpty(mystery.PetName))
				stringBuilder.AppendLine($"{img} {mystery.PetName}.png|{mystery.PetName}");
		}
		else
		{
			for (int i = 1; i <= 5; i++)
				stringBuilder.AppendLine($"{img} Decoration {i}.png|Decoration {i}");
		}
		if (!string.IsNullOrEmpty(mystery.EventItemName))
			stringBuilder.AppendLine($"{mystery.EventItemName} Event Item.png|Event Item");
		stringBuilder.AppendLine("</gallery>");
		return stringBuilder.ToString();
	}
}
