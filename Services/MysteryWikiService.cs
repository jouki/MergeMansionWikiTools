using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MergeMansionWikiTools.Models;
using MergeMansionWikiTools.Views;

namespace MergeMansionWikiTools.Services;

public static class MysteryWikiService
{
	private record TabSection(string Header, string[] Content);

	private record TabberRegions(string[] Before, List<TabSection> Tabs, string[] After);

	public enum AtlasTileType
	{
		Decoration,
		Icon
	}

	private const string BaseApiUrl = "https://merge-mansion.fandom.com/api.php";

	private static readonly HttpClient Http = CreateHttpClient();

	private static readonly string StatusCachePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mystery_wiki_status_cache.json");


	private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
	{
		WriteIndented = true
	};

	private const string PassXpHeader = "{{#Invoke:Utils|Icon|name=Silver Pass Ticket|suppressLevel=true|size=32}}";

	private const string GoldPassHeader = "{{#Invoke:Utils|Icon|name=Gold Pass Ticket|suppressLevel=true|size=32}}";

	private const string InventorySlotIcon = "{{#Invoke:Utils|Icon|name=InventorySlot|suppressLevel=true|size=24}}";

	private static readonly HashSet<string> NoLevelItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Missing Evidence", "Clues Envelope", "Unlimited Energy" };

	private static readonly HashSet<string> PlainItemItems = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Brown Chest", "Fancy Blue Chest" };

	private static Dictionary<string, string>? _petDisplayNames;

	public static bool HasPetDisplayNames => _petDisplayNames != null && _petDisplayNames.Count > 0;

	private static HttpClient CreateHttpClient()
	{
		HttpClient httpClient = new HttpClient(new HttpClientHandler
		{
			UseProxy = false
		});
		httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("MergeMansionWikiTools/1.0");
		return httpClient;
	}

	public static MysteryWikiStatusCache LoadStatusCache()
	{
		try
		{
			if (File.Exists(StatusCachePath))
			{
				string json = File.ReadAllText(StatusCachePath);
				return JsonSerializer.Deserialize<MysteryWikiStatusCache>(json, JsonOpts) ?? new MysteryWikiStatusCache();
			}
		}
		catch
		{
		}
		return new MysteryWikiStatusCache();
	}

	public static void ApplyCachedStatus(IReadOnlyList<MysteryEvent> mysteries, DataService? ds = null)
	{
		MysteryWikiStatusCache mysteryWikiStatusCache = LoadStatusCache();
		if (mysteryWikiStatusCache.Entries.Count == 0)
		{
			return;
		}
		HashSet<MysteryEvent> hashSet = (from g in mysteries.GroupBy<MysteryEvent, string>((MysteryEvent mysteryEvent) => mysteryEvent.Name, StringComparer.OrdinalIgnoreCase)
			where g.Count() > 1
			select g).SelectMany((IGrouping<string, MysteryEvent> g) => g).ToHashSet();
		foreach (MysteryEvent m in mysteries)
		{
			if (!string.IsNullOrEmpty(m.WikiStatus.SuggestedPageTitle))
			{
				continue;
			}
			string pageName = m.Name;
			bool flag = false;
			if (hashSet.Contains(m))
			{
				flag = true;
			}
			if (!flag && ds != null)
			{
				flag = ds.ChainNames.Values.Any((string n) => string.Equals(n, pageName, StringComparison.OrdinalIgnoreCase));
				if (!flag)
				{
					flag = ds.ItemNames.Values.Any((string n) => string.Equals(n, pageName, StringComparison.OrdinalIgnoreCase));
				}
			}
			if (!flag)
			{
				flag = mysteries.Any((MysteryEvent other) => other != m && string.Equals(other.EventItemName, pageName, StringComparison.OrdinalIgnoreCase));
			}
			if (!flag)
			{
				flag = mysteries.Any((MysteryEvent other) => other != m && string.Equals(other.Name, m.EventItemName, StringComparison.OrdinalIgnoreCase));
			}
			m.WikiStatus.SuggestedPageTitle = ((flag && m.StartDate.HasValue) ? $"{pageName} (Mystery {m.StartDate.Value.Year})" : pageName);
		}
		ApplyCache(mysteries, mysteryWikiStatusCache);
		AppLogger.Info($"Applied cached wiki status for {mysteries.Count} mysteries");
	}

	public static void ResetMatchedLabelsInCache()
	{
		try
		{
			MysteryWikiStatusCache cache = LoadStatusCache();
			ResetMatchedEntries(cache);
			SaveStatusCache(cache);
		}
		catch
		{
		}
	}

	public static void ResetMatchedLabelsFromMemory(IReadOnlyList<MysteryEvent> mysteries)
	{
		try
		{
			MysteryWikiStatusCache mysteryWikiStatusCache = LoadStatusCache();
			foreach (MysteryEvent mystery in mysteries)
			{
				if (mysteryWikiStatusCache.Entries.TryGetValue(mystery.ProgressionEventId, out CachedMysteryStatus value))
				{
					WikiCheckState eventPageState = mystery.WikiStatus.EventPageState;
					if (eventPageState == WikiCheckState.Match || eventPageState == WikiCheckState.Confirmed)
					{
						value.EventPageContentMatches = null;
						value.EventPageConfirmed = false;
					}
					WikiCheckState eventItemPageState = mystery.WikiStatus.EventItemPageState;
					if (eventItemPageState == WikiCheckState.Match || eventItemPageState == WikiCheckState.Confirmed)
					{
						value.EventItemPageContentMatches = null;
						value.ItemPageConfirmed = false;
					}
					WikiCheckState rewardTemplateState = mystery.WikiStatus.RewardTemplateState;
					if (rewardTemplateState == WikiCheckState.Match || rewardTemplateState == WikiCheckState.Confirmed)
					{
						value.RewardTemplateMatches = null;
						value.RewardContentMatches = null;
						value.MatchingVariant = null;
						value.RewardsConfirmed = null;
					}
					WikiCheckState imagesState = mystery.WikiStatus.ImagesState;
					if (imagesState == WikiCheckState.Match || imagesState == WikiCheckState.Confirmed)
					{
						value.ImagesExistOnWiki = 0;
						value.ImagesTotalExpected = 0;
						value.ImagesConfirmed = false;
					}
					WikiCheckState wikiListedState = mystery.WikiStatus.WikiListedState;
					if (wikiListedState == WikiCheckState.Match)
					{
						value.WikiMainPageListed = null;
						value.WikiMysteryTableListed = null;
						value.WikiModuleListed = null;
					}
				}
			}
			SaveStatusCache(mysteryWikiStatusCache);
		}
		catch
		{
		}
	}

	internal static void ResetMatchedEntries(MysteryWikiStatusCache cache)
	{
		foreach (CachedMysteryStatus value in cache.Entries.Values)
		{
			if (value.EventPageContentMatches == true)
			{
				value.EventPageContentMatches = null;
			}
			if (value.EventItemPageContentMatches == true)
			{
				value.EventItemPageContentMatches = null;
			}
			if (value.RewardTemplateMatches == true)
			{
				value.RewardTemplateMatches = null;
				value.RewardContentMatches = null;
				value.MatchingVariant = null;
			}
			if (value.ImagesExistOnWiki >= value.ImagesTotalExpected && value.ImagesTotalExpected > 0)
			{
				value.ImagesExistOnWiki = 0;
				value.ImagesTotalExpected = 0;
			}
			if (value.WikiMainPageListed == true && value.WikiMysteryTableListed == true && value.WikiModuleListed == true)
			{
				value.WikiMainPageListed = null;
				value.WikiMysteryTableListed = null;
				value.WikiModuleListed = null;
			}
		}
	}

	public static void ClearStatusCache()
	{
		try
		{
			if (File.Exists(StatusCachePath))
			{
				File.Delete(StatusCachePath);
			}
		}
		catch
		{
		}
	}

	public static async Task LoadManualConfirmFlagsAsync(IReadOnlyList<MysteryEvent> mysteries)
	{
		try
		{
			string content = await FetchPageContentAsync("Module:Datatable/Various");
			if (string.IsNullOrEmpty(content))
			{
				return;
			}
			Regex entryPattern = new Regex("\\[(\\d+)\\]\\s*=\\s*\\{([^}]+)\\}", RegexOptions.Compiled);
			foreach (Match match in entryPattern.Matches(content))
			{
				int index = int.Parse(match.Groups[1].Value);
				string fields = match.Groups[2].Value;
				Match nameMatch = Regex.Match(fields, "name\\s*=\\s*\"([^\"]+)\"");
				if (nameMatch.Success)
				{
					string entryName = nameMatch.Groups[1].Value;
					MysteryEvent mystery = mysteries.FirstOrDefault((MysteryEvent m) => string.Equals(m.WikiStatus.SuggestedPageTitle, entryName, StringComparison.OrdinalIgnoreCase) || string.Equals(m.Name, entryName, StringComparison.OrdinalIgnoreCase));
					if (mystery != null)
					{
						mystery.WikiStatus.MysteryTableIndex = index;
						mystery.WikiStatus.ManualConfirm.EventPageConfirmed = Regex.IsMatch(fields, "eventPageManualConfirm\\s*=\\s*true", RegexOptions.IgnoreCase);
						var rewardsMatch = Regex.Match(fields, "rewardsManualConfirm\\s*=\\s*(?:\"([^\"]+)\"|true)", RegexOptions.IgnoreCase);
						mystery.WikiStatus.ManualConfirm.RewardsConfirmed = rewardsMatch.Success
							? (rewardsMatch.Groups[1].Success ? rewardsMatch.Groups[1].Value : "true")
							: null;
						mystery.WikiStatus.ManualConfirm.ItemPageConfirmed = Regex.IsMatch(fields, "itemPageConfirmed\\s*=\\s*true", RegexOptions.IgnoreCase);
						mystery.WikiStatus.ManualConfirm.ImagesConfirmed = Regex.IsMatch(fields, "imagesConfirmed\\s*=\\s*true", RegexOptions.IgnoreCase);
					}
				}
			}
			AppLogger.Info($"Loaded manual confirm flags for {mysteries.Count((MysteryEvent m) => m.WikiStatus.MysteryTableIndex.HasValue)} mysteries");
		}
		catch (Exception ex)
		{
			AppLogger.Warn("Failed to load manual confirm flags: " + ex.Message);
		}
	}

	public static Task<(string Before, string After, bool Success)> SetManualConfirmFlagAsync(string username, string password, MysteryEvent mystery, string flagName, bool value)
		=> SetManualConfirmFlagStringAsync(username, password, mystery, flagName, value ? "true" : null);

	public static async Task<(string Before, string After, bool Success)> SetManualConfirmFlagStringAsync(string username, string password, MysteryEvent mystery, string flagName, string? value)
	{
		string content = await FetchPageContentAsync("Module:Datatable/Various");
		if (string.IsNullOrEmpty(content))
		{
			return (Before: "", After: "", Success: false);
		}
		int? idx = mystery.WikiStatus.MysteryTableIndex;
		if (!idx.HasValue)
		{
			return (Before: "", After: "", Success: false);
		}
		Regex linePattern = new Regex($"(\\[{idx.Value}\\]\\s*=\\s*\\{{)([^}}]+)(\\}})", RegexOptions.Compiled);
		Match lineMatch = linePattern.Match(content);
		if (!lineMatch.Success)
		{
			return (Before: "", After: "", Success: false);
		}
		string beforeLine = lineMatch.Value;
		string fields = lineMatch.Groups[2].Value;
		string afterFields;
		if (value != null)
		{
			// Format: flagName = "value" for strings, flagName = true for plain true
			string luaValue = value == "true" ? "true" : $"\"{value}\"";
			string insertText = flagName + " = " + luaValue;
			if (!Regex.IsMatch(fields, flagName + "\\s*=", RegexOptions.IgnoreCase))
				afterFields = fields.TrimEnd().TrimEnd(',') + ", " + insertText;
			else
				afterFields = Regex.Replace(fields, flagName + "\\s*=\\s*(?:\"[^\"]*\"|\\w+)", insertText, RegexOptions.IgnoreCase);
		}
		else
		{
			// Remove the flag entirely (handles both quoted and unquoted values)
			afterFields = Regex.Replace(fields, ",?\\s*" + flagName + "\\s*=\\s*(?:\"[^\"]*\"|true)", "", RegexOptions.IgnoreCase);
			afterFields = Regex.Replace(afterFields, ",\\s*$", "");
		}
		string afterLine = lineMatch.Groups[1].Value + afterFields + lineMatch.Groups[3].Value;
		if (beforeLine == afterLine)
		{
			return (Before: beforeLine, After: afterLine, Success: true);
		}
		string text = content.Substring(0, lineMatch.Index);
		string text2 = content;
		int num = lineMatch.Index + lineMatch.Length;
		string updatedContent = text + afterLine + text2.Substring(num, text2.Length - num);
		string action = value != null ? "Set" : "Remove";
		await PublishPageAsync(username, password, "Module:Datatable/Various", updatedContent, $"{action} {flagName} for {mystery.Name} (via MergeMansionWikiTools)");
		return (Before: beforeLine, After: afterLine, Success: true);
	}

	public static void UpdateSingleMysteryCache(MysteryEvent mystery)
	{
		try
		{
			MysteryWikiStatusCache mysteryWikiStatusCache = LoadStatusCache();
			CachedMysteryStatus value = new CachedMysteryStatus
			{
				EventPageExists = mystery.WikiStatus.EventPageExists,
				EventPageContentMatches = mystery.WikiStatus.EventPageContentMatches,
				EventItemPageExists = mystery.WikiStatus.EventItemPageExists,
				EventItemPageContentMatches = mystery.WikiStatus.EventItemPageContentMatches,
				RewardTemplateMatches = mystery.WikiStatus.RewardTemplateMatches,
				RewardContentMatches = mystery.WikiStatus.RewardContentMatches,
				MatchingVariant = mystery.WikiStatus.MatchingVariant,
				SuggestedPageTitle = mystery.WikiStatus.SuggestedPageTitle,
				ImagesTotalExpected = mystery.WikiStatus.ImagesTotalExpected,
				ImagesExistOnWiki = mystery.WikiStatus.ImagesExistOnWiki,
				EventPageConfirmed = mystery.WikiStatus.ManualConfirm.EventPageConfirmed,
				RewardsConfirmed = mystery.WikiStatus.ManualConfirm.RewardsConfirmed,
				ItemPageConfirmed = mystery.WikiStatus.ManualConfirm.ItemPageConfirmed,
				ImagesConfirmed = mystery.WikiStatus.ManualConfirm.ImagesConfirmed,
				MysteryTableIndex = mystery.WikiStatus.MysteryTableIndex.GetValueOrDefault(),
				WikiMainPageListed = mystery.WikiStatus.WikiMainPageListed,
				WikiMysteryTableListed = mystery.WikiStatus.WikiMysteryTableListed,
				WikiModuleListed = mystery.WikiStatus.WikiModuleListed
			};
			mysteryWikiStatusCache.Entries[mystery.ProgressionEventId] = value;
			SaveStatusCache(mysteryWikiStatusCache);
		}
		catch
		{
		}
	}

	public static void SaveStatusCache(MysteryWikiStatusCache cache)
	{
		try
		{
			string contents = JsonSerializer.Serialize(cache, JsonOpts);
			File.WriteAllText(StatusCachePath, contents);
		}
		catch
		{
		}
	}

	internal static void ApplyCache(IReadOnlyList<MysteryEvent> mysteries, MysteryWikiStatusCache cache, bool hasDialogueService = false)
	{
		foreach (MysteryEvent mystery in mysteries)
		{
			if (cache.Entries.TryGetValue(mystery.ProgressionEventId, out CachedMysteryStatus value))
			{
				if (value.EventPageExists.HasValue)
				{
					mystery.WikiStatus.EventPageExists = value.EventPageExists;
				}
				mystery.WikiStatus.EventPageContentMatches = value.EventPageContentMatches;
				if (value.EventItemPageExists.HasValue)
				{
					mystery.WikiStatus.EventItemPageExists = value.EventItemPageExists;
				}
				mystery.WikiStatus.EventItemPageContentMatches = value.EventItemPageContentMatches;
				if (value.RewardTemplateMatches.HasValue)
				{
					mystery.WikiStatus.RewardTemplateMatches = value.RewardTemplateMatches;
				}
				mystery.WikiStatus.MatchingVariant = value.MatchingVariant;
				if (value.RewardContentMatches.HasValue)
				{
					mystery.WikiStatus.RewardContentMatches = value.RewardContentMatches;
				}
				mystery.WikiStatus.ImagesTotalExpected = value.ImagesTotalExpected;
				mystery.WikiStatus.ImagesExistOnWiki = value.ImagesExistOnWiki;
				if (value.WikiMainPageListed.HasValue)
				{
					mystery.WikiStatus.WikiMainPageListed = value.WikiMainPageListed;
				}
				if (value.WikiMysteryTableListed.HasValue)
				{
					mystery.WikiStatus.WikiMysteryTableListed = value.WikiMysteryTableListed;
				}
				if (value.WikiModuleListed.HasValue)
				{
					mystery.WikiStatus.WikiModuleListed = value.WikiModuleListed;
				}
				mystery.WikiStatus.ManualConfirm.EventPageConfirmed = value.EventPageConfirmed;
				mystery.WikiStatus.ManualConfirm.RewardsConfirmed = value.RewardsConfirmed;
				mystery.WikiStatus.ManualConfirm.ItemPageConfirmed = value.ItemPageConfirmed;
				mystery.WikiStatus.ManualConfirm.ImagesConfirmed = value.ImagesConfirmed;
				if (value.MysteryTableIndex > 0)
				{
					mystery.WikiStatus.MysteryTableIndex = value.MysteryTableIndex;
				}
				if (!string.IsNullOrEmpty(value.SuggestedPageTitle))
				{
					mystery.WikiStatus.SuggestedPageTitle = value.SuggestedPageTitle;
				}
			}
		}
	}

	private static void UpdateCache(IReadOnlyList<MysteryEvent> mysteries, MysteryWikiStatusCache cache)
	{
		foreach (MysteryEvent mystery in mysteries)
		{
			CachedMysteryStatus value = new CachedMysteryStatus
			{
				EventPageExists = mystery.WikiStatus.EventPageExists,
				EventPageContentMatches = mystery.WikiStatus.EventPageContentMatches,
				EventItemPageExists = mystery.WikiStatus.EventItemPageExists,
				EventItemPageContentMatches = mystery.WikiStatus.EventItemPageContentMatches,
				RewardTemplateMatches = mystery.WikiStatus.RewardTemplateMatches,
				RewardContentMatches = mystery.WikiStatus.RewardContentMatches,
				MatchingVariant = mystery.WikiStatus.MatchingVariant,
				SuggestedPageTitle = mystery.WikiStatus.SuggestedPageTitle,
				ImagesTotalExpected = mystery.WikiStatus.ImagesTotalExpected,
				ImagesExistOnWiki = mystery.WikiStatus.ImagesExistOnWiki,
				EventPageConfirmed = mystery.WikiStatus.ManualConfirm.EventPageConfirmed,
				RewardsConfirmed = mystery.WikiStatus.ManualConfirm.RewardsConfirmed,
				ItemPageConfirmed = mystery.WikiStatus.ManualConfirm.ItemPageConfirmed,
				ImagesConfirmed = mystery.WikiStatus.ManualConfirm.ImagesConfirmed,
				MysteryTableIndex = mystery.WikiStatus.MysteryTableIndex.GetValueOrDefault(),
				WikiMainPageListed = mystery.WikiStatus.WikiMainPageListed,
				WikiMysteryTableListed = mystery.WikiStatus.WikiMysteryTableListed,
				WikiModuleListed = mystery.WikiStatus.WikiModuleListed
			};
			cache.Entries[mystery.ProgressionEventId] = value;
		}
	}

	public static async Task<Dictionary<string, bool>> CheckPagesExistAsync(IEnumerable<string> titles)
	{
		Dictionary<string, bool> result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
		List<string> titleList = titles.ToList();
		for (int i = 0; i < titleList.Count; i += 50)
		{
			IEnumerable<string> batch = titleList.Skip(i).Take(50);
			string joined = string.Join("|", batch);
			string url = "https://merge-mansion.fandom.com/api.php?action=query&titles=" + Uri.EscapeDataString(joined) + "&format=json";
			AppLogger.Info("CheckPagesExist batch: " + url);
			JsonDocument doc = JsonDocument.Parse(await Http.GetStringAsync(url));
			foreach (JsonProperty page in doc.RootElement.GetProperty("query").GetProperty("pages").EnumerateObject())
			{
				JsonElement value = page.Value.GetProperty("title");
				string title = value.GetString() ?? "";
				bool missing = page.Value.TryGetProperty("missing", out value);
				result[title] = !missing;
				result[title.Replace(' ', '_')] = !missing;
				result[title.Replace('_', ' ')] = !missing;
			}
		}
		return result;
	}

	public static async Task<WikiPageStatus> CheckMysteryStatusAsync(MysteryEvent mystery, DataService? ds)
	{
		WikiPageStatus status = new WikiPageStatus();
		// Age check: only this year and last year mysteries are eligible for main page
		int currentYear = DateTime.Now.Year;
		status.MainPageEligible = !(mystery.StartDate.HasValue && mystery.StartDate.Value.Year < currentYear - 1);
		string pageName = mystery.Name;
		status.SuggestedPageTitle = pageName;
		if (ds != null)
		{
			bool collision = ds.ChainNames.Values.Any((string n) => string.Equals(n, pageName, StringComparison.OrdinalIgnoreCase));
			if (!collision)
			{
				collision = ds.ItemNames.Values.Any((string n) => string.Equals(n, pageName, StringComparison.OrdinalIgnoreCase));
			}
			if (collision && mystery.StartDate.HasValue)
			{
				status.SuggestedPageTitle = $"{pageName} (Mystery {mystery.StartDate.Value.Year})";
			}
		}
		List<string> titlesToCheck = new List<string> { status.SuggestedPageTitle };
		if (!string.IsNullOrEmpty(mystery.EventItemName))
		{
			titlesToCheck.Add(mystery.EventItemName);
		}
		Dictionary<string, bool> existMap = await CheckPagesExistAsync(titlesToCheck);
		status.EventPageExists = existMap.GetValueOrDefault(status.SuggestedPageTitle, defaultValue: false);
		if (!string.IsNullOrEmpty(mystery.EventItemName))
		{
			status.EventItemPageExists = existMap.GetValueOrDefault(mystery.EventItemName, defaultValue: false);
		}
		return status;
	}

	public static async Task<Dictionary<string, string>> FetchRewardTemplatesAsync()
	{
		Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		string listUrl = "https://merge-mansion.fandom.com/api.php?action=query&list=allpages&apprefix=Mystery_Pass/Rewards&apnamespace=10&aplimit=100&format=json";
		JsonDocument listDoc = JsonDocument.Parse(await Http.GetStringAsync(listUrl));
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
			IEnumerable<string> batch = templateTitles.Skip(i).Take(50);
			string joined = string.Join("|", batch);
			string url = "https://merge-mansion.fandom.com/api.php?action=query&titles=" + Uri.EscapeDataString(joined) + "&prop=revisions&rvprop=content&rvslots=main&format=json";
			JsonDocument doc = JsonDocument.Parse(await Http.GetStringAsync(url));
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
		return result;
	}

	// ── Gallery template detection ──────────────────────────────────

	/// <summary>Fetches all Mystery Pass/Gallery templates from wiki.</summary>
	public static async Task<Dictionary<string, string>> FetchGalleryTemplatesAsync()
	{
		var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		string listUrl = "https://merge-mansion.fandom.com/api.php?action=query&list=allpages&apprefix=Mystery_Pass/Gallery&apnamespace=10&aplimit=100&format=json";
		var listDoc = JsonDocument.Parse(await Http.GetStringAsync(listUrl));
		var allPages = listDoc.RootElement.GetProperty("query").GetProperty("allpages");
		var titles = new List<string>();
		foreach (var p in allPages.EnumerateArray())
		{
			string? t = p.GetProperty("title").GetString();
			if (!string.IsNullOrEmpty(t)) titles.Add(t);
		}
		for (int i = 0; i < titles.Count; i += 50)
		{
			string joined = string.Join("|", titles.Skip(i).Take(50));
			string url = "https://merge-mansion.fandom.com/api.php?action=query&titles=" + Uri.EscapeDataString(joined) + "&prop=revisions&rvprop=content&rvslots=main&format=json";
			var doc = JsonDocument.Parse(await Http.GetStringAsync(url));
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
		return result;
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
		var templates = await FetchGalleryTemplatesAsync();
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

	public static async Task<(bool Matches, string? Variant)> CompareWithExistingTemplatesAsync(MysteryEvent mystery, MysteryItemMapping? mapping)
	{
		return CompareWithTemplates(mystery, await FetchRewardTemplatesAsync());
	}

	public static (bool Matches, string? Variant) CompareWithTemplates(MysteryEvent mystery, Dictionary<string, string> templates)
	{
		bool isPet = mystery.MysteryType == MysteryType.Pet;
		string mysteryName = mystery.WikiStatus.SuggestedPageTitle ?? mystery.Name;

		// Build variant→content list (filtered by pet/non-pet)
		var candidates = new List<(string Variant, string Content)>();
		foreach (var (title, content) in templates)
		{
			int idx = title.IndexOf("/Rewards", StringComparison.OrdinalIgnoreCase);
			string variant = null;
			if (idx >= 0)
			{
				string after = title[(idx + "/Rewards".Length)..].TrimStart('/');
				if (!string.IsNullOrEmpty(after))
					variant = after;
			}
			bool isPetTemplate = variant?.StartsWith("Pet", StringComparison.OrdinalIgnoreCase) ?? false;
			if (isPet == isPetTemplate)
				candidates.Add((variant ?? "", content));
		}

		// Pass 1: Find full match (XP + content identical)
		foreach (var (variant, content) in candidates)
		{
			if (CompareRewardsWithTemplate(mystery, content))
				return (Matches: true, Variant: variant);
		}

		// Pass 2: No match — check if a mystery-named template exists (for diff comparison)
		var namedMatch = candidates.FirstOrDefault(c =>
			string.Equals(c.Variant, mysteryName, StringComparison.OrdinalIgnoreCase));
		if (namedMatch.Content != null)
			return (Matches: false, Variant: namedMatch.Variant);

		// Pass 3: No match at all — compute next available variant from existing templates
		string nextVariant = ComputeNextVariant(isPet, templates);
		return (Matches: false, Variant: nextVariant);
	}

	/// <summary>
	/// Synchronously computes the next available reward variant name from a templates dictionary.
	/// Pet: "Pet", "Pet/2", "Pet/3"... Standard: "", "2", "3"...
	/// </summary>
	private static string ComputeNextVariant(bool isPet, Dictionary<string, string> templates)
	{
		int maxNum = 0;
		foreach (string title in templates.Keys)
		{
			int idx = title.IndexOf("/Rewards", StringComparison.OrdinalIgnoreCase);
			if (idx < 0) continue;
			string after = title[(idx + "/Rewards".Length)..].TrimStart('/');
			if (isPet)
			{
				if (!after.StartsWith("Pet", StringComparison.OrdinalIgnoreCase)) continue;
				string petAfter = after.Length <= 3 ? "" : after[3..].TrimStart('/');
				if (string.IsNullOrEmpty(petAfter))
					maxNum = Math.Max(maxNum, 1);
				else if (int.TryParse(petAfter, out int n))
					maxNum = Math.Max(maxNum, n);
			}
			else
			{
				if (after.StartsWith("Pet", StringComparison.OrdinalIgnoreCase)) continue;
				if (string.IsNullOrEmpty(after))
					maxNum = Math.Max(maxNum, 1);
				else if (int.TryParse(after, out int n))
					maxNum = Math.Max(maxNum, n);
			}
		}
		int next = maxNum + 1;
		return isPet
			? (next == 1 ? "Pet" : $"Pet/{next}")
			: (next == 1 ? "" : $"{next}");
	}

	public static async Task<string> GetNextVariantNameAsync(bool isPet)
	{
		Dictionary<string, string> templates = await FetchRewardTemplatesAsync();
		if (!isPet)
		{
		}
		int maxNum = (isPet ? 0 : 0);
		foreach (string title in templates.Keys)
		{
			int idx = title.IndexOf("/Rewards", StringComparison.OrdinalIgnoreCase);
			if (idx < 0)
			{
				continue;
			}
			string text = title;
			int num = idx + "/Rewards".Length;
			string after = text.Substring(num, text.Length - num).TrimStart('/');
			if (isPet)
			{
				if (after.StartsWith("Pet", StringComparison.OrdinalIgnoreCase))
				{
					object obj;
					if (after.Length <= 3)
					{
						obj = "";
					}
					else
					{
						text = after;
						obj = text.Substring(3, text.Length - 3).TrimStart('/');
					}
					string petAfter = (string)obj;
					int n;
					if (string.IsNullOrEmpty(petAfter))
					{
						maxNum = Math.Max(maxNum, 1);
					}
					else if (int.TryParse(petAfter, out n))
					{
						maxNum = Math.Max(maxNum, n);
					}
				}
			}
			else if (!after.StartsWith("Pet", StringComparison.OrdinalIgnoreCase))
			{
				int n2;
				if (string.IsNullOrEmpty(after))
				{
					maxNum = Math.Max(maxNum, 1);
				}
				else if (int.TryParse(after, out n2))
				{
					maxNum = Math.Max(maxNum, n2);
				}
			}
		}
		int next = maxNum + 1;
		if (isPet)
		{
			return (next == 1) ? "Pet" : $"Pet/{next}";
		}
		return (next == 1) ? "" : $"{next}";
	}

	private static bool CompareRewardsWithTemplate(MysteryEvent mystery, string templateContent)
	{
		templateContent = Regex.Replace(templateContent, "<!--.*?-->", "", RegexOptions.Singleline);
		List<List<string>> list = ParseTemplateRows(templateContent);
		List<MysteryRewardLevel> list2 = ((mystery.FreeTier.Count > 0) ? mystery.FreeTier : ((mystery.SilverTier.Count > 0) ? mystery.SilverTier : mystery.GoldTier));
		if (list2.Count == 0 || list.Count == 0)
			return false;
		List<List<string>> list3 = list.Where((List<string> r) => r.Count >= 5 && !r[0].Contains("PremiumLevel") && r[0].Trim() != "0").ToList();
		if (list3.Count != list2.Count - 1)
			return false;
		for (int num = 0; num < list3.Count; num++)
		{
			Match match = Regex.Match(list3[num][1], "\\{\\{Pass XP\\}\\}\\s*(\\d+)");
			if (!match.Success)
				return false;
			if (int.Parse(match.Groups[1].Value) != list2[num + 1].XpRequired)
				return false;
		}
		string content = GenerateRewardTemplate(mystery, null);
		List<List<string>> source = ParseTemplateRows(content);
		List<List<string>> list4 = source.Where((List<string> r) => r.Count >= 5 && !r[0].Contains("PremiumLevel") && r[0].Trim() != "0").ToList();
		if (list4.Count != list3.Count)
			return false;
		for (int num2 = 0; num2 < list3.Count; num2++)
		{
			for (int num3 = 1; num3 <= 4; num3++)
			{
				if (num3 >= list3[num2].Count || num3 >= list4[num2].Count)
					return false;
				if (NormalizeCell(list3[num2][num3]) != NormalizeCell(list4[num2][num3]))
					return false;
			}
		}
		return true;
	}

	private static List<List<string>> ParseTemplateRows(string content)
	{
		List<List<string>> list = new List<List<string>>();
		content = content.Replace("\r\n", "\n").Replace("\r", "\n");
		MatchCollection matchCollection = Regex.Matches(content, "\\|\\-\\s*\\n((?:\\|(?!\\-|\\})[^\\n]*\\n?)+)");
		foreach (Match item in matchCollection)
		{
			List<string> list2 = new List<string>();
			string[] array = item.Groups[1].Value.Split('\n');
			foreach (string text in array)
			{
				string text2 = text.Trim();
				if (text2.StartsWith("|") && !text2.StartsWith("|-") && !text2.StartsWith("|}"))
				{
					string text3 = text2;
					list2.Add(text3.Substring(1, text3.Length - 1).Trim());
				}
			}
			if (list2.Count > 0)
			{
				list.Add(list2);
			}
		}
		return list;
	}

	private static string NormalizeCell(string cell)
	{
		cell = cell.Trim();
		cell = cell.Replace('\u2014', '-').Replace('\u2013', '-');
		cell = Regex.Replace(cell, "\\s+", " ");
		return cell;
	}

	private static string NormalizeTemplateForComparison(string content)
	{
		content = Regex.Replace(content, "\\s*<!--.*?-->\\s*", " ", RegexOptions.Singleline);
		content = content.Replace("\r\n", "\n").Replace("\r", "\n");
		content = content.Replace('\u2014', '-').Replace('\u2013', '-');
		content = Regex.Replace(content, "^\\|\\s*(?=[^-}|!])", "| ", RegexOptions.Multiline);
		content = string.Join("\n", from l in content.Split('\n')
			select l.TrimEnd());
		content = Regex.Replace(content, "\\n{3,}", "\n\n");
		return content.Trim();
	}

	public static string GenerateRewardTemplate(MysteryEvent mystery, MysteryItemMapping? mapping)
	{
		StringBuilder stringBuilder = new StringBuilder();
		bool flag = mystery.MysteryType == MysteryType.Pet;
		int num = 1;
		foreach (MysteryRewardLevel item in mystery.SilverTier)
		{
			foreach (MysteryReward reward in item.Rewards)
			{
				if (reward.Type == MysteryRewardType.Decoration)
				{
					reward.ItemLevel = num++;
				}
			}
		}
		foreach (MysteryRewardLevel item2 in mystery.GoldTier)
		{
			foreach (MysteryReward reward2 in item2.Rewards)
			{
				if (reward2.Type == MysteryRewardType.Decoration)
				{
					reward2.ItemLevel = num++;
				}
			}
		}
		stringBuilder.AppendLine("{| class=\"article-table\"");
		stringBuilder.AppendLine("! Level");
		stringBuilder.AppendLine("! {{Pass XP}}Points Needed");
		stringBuilder.AppendLine("! F2P  Reward");
		stringBuilder.AppendLine("! {{#Invoke:Utils|Icon|name=Silver Pass Ticket|suppressLevel=true|size=32}} Silver Pass Reward");
		stringBuilder.AppendLine("! {{#Invoke:Utils|Icon|name=Gold Pass Ticket|suppressLevel=true|size=32}} Gold Pass Reward");
		stringBuilder.AppendLine("|-");
		stringBuilder.AppendLine("| 0");
		stringBuilder.AppendLine("| {{Pass XP}} -");
		stringBuilder.AppendLine("| {{Energy}} 10");
		stringBuilder.AppendLine("| 5 {{Gems}}/day <br> 3 {{#Invoke:Utils|Icon|name=InventorySlot|suppressLevel=true|size=24}} [[Inventory]] slots");
		stringBuilder.AppendLine("| 5 {{Gems}}/day <br> 3 {{#Invoke:Utils|Icon|name=InventorySlot|suppressLevel=true|size=24}} [[Inventory]] slots");
		int num2 = Math.Max(mystery.FreeTier.Count, Math.Max(mystery.SilverTier.Count, mystery.GoldTier.Count));
		for (int i = 1; i < num2; i++)
		{
			MysteryRewardLevel mysteryRewardLevel = ((i < mystery.FreeTier.Count) ? mystery.FreeTier[i] : null);
			MysteryRewardLevel mysteryRewardLevel2 = ((i < mystery.SilverTier.Count) ? mystery.SilverTier[i] : null);
			MysteryRewardLevel mysteryRewardLevel3 = ((i < mystery.GoldTier.Count) ? mystery.GoldTier[i] : null);
			int value = mysteryRewardLevel?.XpRequired ?? mysteryRewardLevel2?.XpRequired ?? mysteryRewardLevel3?.XpRequired ?? 0;
			stringBuilder.AppendLine("|-");
			StringBuilder stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder3 = stringBuilder2;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(2, 1, stringBuilder2);
			handler.AppendLiteral("| ");
			handler.AppendFormatted(i);
			stringBuilder3.AppendLine(ref handler);
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder4 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(14, 1, stringBuilder2);
			handler.AppendLiteral("| {{Pass XP}} ");
			handler.AppendFormatted(value);
			stringBuilder4.AppendLine(ref handler);
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder5 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(2, 1, stringBuilder2);
			handler.AppendLiteral("| ");
			handler.AppendFormatted(FormatRewards(mysteryRewardLevel?.Rewards));
			stringBuilder5.AppendLine(ref handler);
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder6 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(2, 1, stringBuilder2);
			handler.AppendLiteral("| ");
			handler.AppendFormatted(FormatRewards(mysteryRewardLevel2?.Rewards, "silver"));
			stringBuilder6.AppendLine(ref handler);
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder7 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(2, 1, stringBuilder2);
			handler.AppendLiteral("| ");
			handler.AppendFormatted(FormatRewards(mysteryRewardLevel3?.Rewards, "gold"));
			stringBuilder7.AppendLine(ref handler);
		}
		int[] array = new int[5] { 2000, 3000, 3000, 4000, 5000 };
		for (int j = 0; j < 5; j++)
		{
			stringBuilder.AppendLine("|-");
			StringBuilder stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder8 = stringBuilder2;
			StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(19, 1, stringBuilder2);
			handler.AppendLiteral("| {{PremiumLevel|");
			handler.AppendFormatted(j + 1);
			handler.AppendLiteral("}}");
			stringBuilder8.AppendLine(ref handler);
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder9 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(14, 1, stringBuilder2);
			handler.AppendLiteral("| {{Pass XP}} ");
			handler.AppendFormatted(array[j]);
			stringBuilder9.AppendLine(ref handler);
			stringBuilder.AppendLine("| {{Dash}}");
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder10 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(88, 1, stringBuilder2);
			handler.AppendLiteral("| colspan = 2 style = \"text-align: center\" | {{Item/Group|Challenge Chest|");
			handler.AppendFormatted(j + 1);
			handler.AppendLiteral("|iconLevel=1}}");
			stringBuilder10.AppendLine(ref handler);
		}
		stringBuilder.AppendLine("|}");
		return stringBuilder.ToString();
	}

	private static string FormatRewards(List<MysteryReward>? rewards, string tier = "free")
	{
		if (rewards == null || rewards.Count == 0)
		{
			return "?";
		}
		List<string> list = new List<string>();
		foreach (MysteryReward reward in rewards)
		{
			if (reward.Type != MysteryRewardType.Perk)
			{
				string text = FormatSingleReward(reward, tier);
				if (!string.IsNullOrEmpty(text))
				{
					list.Add(text);
				}
			}
		}
		return (list.Count > 0) ? string.Join(" <br> ", list) : "?";
	}

	private static string FormatSingleReward(MysteryReward reward, string tier)
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
			MysteryRewardType.InformantTip => FormatInformantTip(reward),
			_ => "",
		};
	}

	private static string FormatItemReward(MysteryReward reward)
	{
		string text = reward.ItemKey ?? "";
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
		string text3 = reward.ItemDisplayName ?? reward.ItemKey ?? "Unknown";
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

	private static string FormatInformantTip(MysteryReward reward)
	{
		string? informantTipCardId = reward.InformantTipCardId;
		int value = ((informantTipCardId == null || !informantTipCardId.Contains("Special")) ? 1 : 2);
		return $"{{{{Item/nolevel|Missing Evidence|{value}}}}}";
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
		string text = mystery.WikiStatus.SuggestedPageTitle ?? mystery.Name;
		string value = mystery.StartDate?.Year.ToString() ?? "YYYY";
		string text2 = mystery.EventItemName ?? "{{PAGENAME}}";
		string text3 = StripParenthetical(text2);
		bool flag = text3 != text2;
		bool flag2 = text != mystery.Name;
		StringBuilder stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder3 = stringBuilder2;
		StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(25, 1, stringBuilder2);
		handler.AppendLiteral("{{#vardefine:EventName|");
		handler.AppendFormatted(text);
		handler.AppendLiteral("}}");
		stringBuilder3.Append(ref handler);
		if (flag2)
		{
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder4 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(32, 1, stringBuilder2);
			handler.AppendLiteral("{{#vardefine:EventDisplayName|");
			handler.AppendFormatted(mystery.Name);
			handler.AppendLiteral("}}");
			stringBuilder4.AppendLine(ref handler);
		}
		if (flag)
		{
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder5 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(28, 1, stringBuilder2);
			handler.AppendLiteral("{{#vardefine:DisplayTitle|");
			handler.AppendFormatted(text3);
			handler.AppendLiteral("}}");
			stringBuilder5.AppendLine(ref handler);
			stringBuilder.AppendLine("{{DISPLAYTITLE:{{#var:DisplayTitle}}}}");
		}
		stringBuilder.AppendLine("{{Infobox Items");
		stringBuilder.AppendLine("| image1 = ");
		stringBuilder.AppendLine("{{#tag:gallery|");
		stringBuilder.AppendLine("{{ItemNameToFilename|{{PAGENAME}}|1}} {{!}} Level 1");
		stringBuilder.AppendLine("{{ItemNameToFilename|{{PAGENAME}}|{{#Invoke:Items|GetItemMaxLevelFromChainName}}}} {{!}} Level {{#Invoke:Items|GetItemMaxLevelFromChainName}}");
		stringBuilder.AppendLine("}}");
		if (flag)
		{
			stringBuilder.AppendLine("| title1 = {{#var:DisplayTitle}}");
		}
		stringBuilder.AppendLine("| type   = Drop Item");
		stringBuilder.AppendLine(flag2
			? "| source = Merging Items during {{Item/nolevel|{{#var:EventName}}|displayName={{#var:EventDisplayName}}}} Event"
			: "| source = Merging Items during {{Item/nolevel|{{#var:EventName}}}} Event");
		stringBuilder.AppendLine("}}");
		string value2 = (flag ? "{{Item/Group|{{PAGENAME}}|4|displayName={{#var:DisplayTitle}}}}" : "{{Item/Group|{{PAGENAME}}|4}}");
		string eventRef = flag2
			? "{{Item/nolevel|{{#var:EventName}}|displayName={{#var:EventDisplayName}}}}"
			: "{{Item/nolevel|{{#var:EventName}}}}";
		stringBuilder.AppendLine($"{value2} is an item in '''''Merge Mansion'''''.  It is used in the {eventRef} [[Events|Event]] of {value}.");
		stringBuilder.AppendLine();
		string value3 = (flag ? "{{Item/nolevel|{{PAGENAME}}|1|displayName={{#var:DisplayTitle}}}}" : "{{Item/nolevel|{{PAGENAME}}|1}}");
		string value4 = (flag ? "{{#var:DisplayTitle}}" : "{{PAGENAME}}");
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder7 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(170, 1, stringBuilder2);
		handler.AppendLiteral("* ");
		handler.AppendFormatted(value3);
		handler.AppendLiteral("  can spawn from any merge action which takes place on the normal board and also on any Story Event boards like other [[Events#Progression_Events|Mystery Pass events]].");
		stringBuilder7.AppendLine(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder8 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(71, 1, stringBuilder2);
		handler.AppendLiteral("* ");
		handler.AppendFormatted(value3);
		handler.AppendLiteral("  can be merged up to level 4, which then gives the max points of 20.");
		stringBuilder8.AppendLine(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder9 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(55, 1, stringBuilder2);
		handler.AppendLiteral("* Similar to {{XP}}[[XP]] ");
		handler.AppendFormatted(value4);
		handler.AppendLiteral(" can be collected by tapping.");
		stringBuilder9.AppendLine(ref handler);
		stringBuilder.AppendLine("* It is advisable to leave 2 empty spots whilst merging, as the priority order for drops whilst merging goes to:");
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder10 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(2, 1, stringBuilder2);
		handler.AppendLiteral("# ");
		handler.AppendFormatted(value3);
		stringBuilder10.AppendLine(ref handler);
		stringBuilder.AppendLine("# Double Bubbles");
		stringBuilder.AppendLine("# {{XP}} XP");
		stringBuilder.AppendLine();
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder11 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(91, 1, stringBuilder2);
		handler.AppendLiteral("Therefore, to maximise the ");
		handler.AppendFormatted(value4);
		handler.AppendLiteral(" drops and XP, it is best to keep 2 free spots for them to drop.");
		stringBuilder11.AppendLine(ref handler);
		stringBuilder.AppendLine();
		stringBuilder.AppendLine("== Descriptions == ");
		for (int i = 1; i <= 4; i++)
		{
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder12 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(71, 2, stringBuilder2);
			handler.AppendLiteral("{{Item/Icon|{{PAGENAME}}|");
			handler.AppendFormatted(i);
			handler.AppendLiteral("}} {{#Invoke:Items|GetItemDescFromChainName|");
			handler.AppendFormatted(i);
			handler.AppendLiteral("}}");
			stringBuilder12.AppendLine(ref handler);
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
			string? captionOverride = flag ? "{{#var:DisplayTitle}}" : null;
			stringBuilder.Append(wikiTableGenerator.Generate(parsedChain, mystery.EventItemName ?? "{{PAGENAME}}", lowPrices: false, captionOverride: captionOverride));
		}
		else
		{
			stringBuilder.AppendLine("{| class=\"article-table\"");
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder13 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(10, 1, stringBuilder2);
			handler.AppendLiteral("|+ <u>");
			handler.AppendFormatted(value4);
			handler.AppendLiteral("</u>");
			stringBuilder13.AppendLine(ref handler);
			stringBuilder.AppendLine("! Lvl");
			stringBuilder.AppendLine("! Image");
			stringBuilder.AppendLine("! Item");
			stringBuilder.AppendLine("! [[Coins|Sells for]]");
			stringBuilder.AppendLine("! Drops");
			int[] array = new int[4] { 1, 2, 4, 6 };
			for (int num = 1; num <= 4; num++)
			{
				stringBuilder.AppendLine("|-");
				stringBuilder2 = stringBuilder;
				StringBuilder stringBuilder14 = stringBuilder2;
				handler = new StringBuilder.AppendInterpolatedStringHandler(2, 1, stringBuilder2);
				handler.AppendLiteral("| ");
				handler.AppendFormatted(num);
				stringBuilder14.AppendLine(ref handler);
				stringBuilder2 = stringBuilder;
				StringBuilder stringBuilder15 = stringBuilder2;
				handler = new StringBuilder.AppendInterpolatedStringHandler(57, 1, stringBuilder2);
				handler.AppendLiteral("| style=\"text-align:center;\" |{{Item/Icon|{{PAGENAME}}|");
				handler.AppendFormatted(num);
				handler.AppendLiteral("}}");
				stringBuilder15.AppendLine(ref handler);
				stringBuilder2 = stringBuilder;
				StringBuilder stringBuilder16 = stringBuilder2;
				handler = new StringBuilder.AppendInterpolatedStringHandler(52, 1, stringBuilder2);
				handler.AppendLiteral("| <u>{{#Invoke:Items|GetItemNameFromChainName|");
				handler.AppendFormatted(num);
				handler.AppendLiteral("}}</u>");
				stringBuilder16.AppendLine(ref handler);
				stringBuilder2 = stringBuilder;
				StringBuilder stringBuilder17 = stringBuilder2;
				handler = new StringBuilder.AppendInterpolatedStringHandler(11, 1, stringBuilder2);
				handler.AppendLiteral("| {{Coins}}");
				handler.AppendFormatted(array[num - 1]);
				stringBuilder17.AppendLine(ref handler);
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

	public static async Task<Dictionary<string, string>> FetchPagesContentAsync(IEnumerable<string> titles)
	{
		Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		List<string> titleList = titles.ToList();
		for (int i = 0; i < titleList.Count; i += 50)
		{
			IEnumerable<string> batch = titleList.Skip(i).Take(50);
			string joined = string.Join("|", batch);
			string url = "https://merge-mansion.fandom.com/api.php?action=query&titles=" + Uri.EscapeDataString(joined) + "&prop=revisions&rvprop=content&rvslots=main&format=json";
			JsonDocument doc = JsonDocument.Parse(await Http.GetStringAsync(url));
			foreach (JsonProperty page in doc.RootElement.GetProperty("query").GetProperty("pages").EnumerateObject())
			{
				if (page.Value.TryGetProperty("revisions", out var revisions))
				{
					string title = page.Value.GetProperty("title").GetString() ?? "";
					string content = revisions[0].GetProperty("slots").GetProperty("main").GetProperty("*")
						.GetString() ?? "";
					result[title] = content;
					revisions = default(JsonElement);
				}
			}
		}
		return result;
	}

	public static bool CompareEventPageContent(string generated, string wikiContent)
	{
		generated = NormalizeWikiContent(generated);
		wikiContent = NormalizeWikiContent(wikiContent);
		return generated == wikiContent;
	}

	public static bool CompareEventItemPageContent(MysteryEvent mystery, string wikiContent, DataService? ds = null)
	{
		string generated = GenerateEventItemPage(mystery, ds);
		return CompareEventPageContent(generated, wikiContent);
	}

	private static string RemoveDialogueSection(string content)
	{
		return Regex.Replace(content, "={2,}\\s*Dialogue\\s*={2,}.*?(?=\\n={2,}\\s*[^=]|\\z)", "", RegexOptions.Singleline);
	}

	private static string NormalizeWikiContent(string content)
	{
		content = content.Replace("\r\n", "\n").Replace("\r", "\n");
		content = Regex.Replace(content, "(={2,})\\s*([^=\\n]+?)\\s*(={2,})", "$1 $2 $3");
		content = Regex.Replace(content, "\\[\\[Category:[^\\]]*\\]\\]\\s*", "");
		content = content.Replace("\u2026", "...").Replace("\u2013", "-").Replace("\u2014", "-")
			.Replace('\u2018', '\'')
			.Replace('\u2019', '\'')
			.Replace('\u201C', '"')
			.Replace('\u201D', '"');
		content = Regex.Replace(content, "'''([^']+?):'''\\s", "'''$1''': ");
		content = string.Join("\n", from l in content.Split('\n')
			select l.TrimEnd());
		content = Regex.Replace(content, "\\n{3,}", "\n\n");
		return content.Trim();
	}

	public static async Task CheckAllMysteryStatusAsync(IReadOnlyList<MysteryEvent> mysteries, DataService? ds, DialogueService? dialogueService = null)
	{
		using (AppLogger.Timed($"CheckAllMysteryStatusAsync ({mysteries.Count} mysteries)"))
		{
			MysteryWikiStatusCache cache = LoadStatusCache();
			AppLogger.Info($"Cache loaded: {cache.Entries.Count} entries");
			HashSet<MysteryEvent> nameGroups = (from g in mysteries.GroupBy<MysteryEvent, string>((MysteryEvent mysteryEvent) => mysteryEvent.Name, StringComparer.OrdinalIgnoreCase)
				where g.Count() > 1
				select g).SelectMany((IGrouping<string, MysteryEvent> g) => g).ToHashSet();
			foreach (MysteryEvent m in mysteries)
			{
				m.WikiStatus.SuggestedPageTitle = null;
				string pageName = m.Name;
				string suggestedTitle = pageName;
				if (nameGroups.Contains(m) && m.StartDate.HasValue)
				{
					suggestedTitle = $"{pageName} (Mystery {m.StartDate.Value.Year})";
				}
				else
				{
					bool collision = false;
					if (ds != null)
					{
						collision = ds.ChainNames.Values.Any((string n) => string.Equals(n, pageName, StringComparison.OrdinalIgnoreCase));
						if (!collision)
						{
							collision = ds.ItemNames.Values.Any((string n) => string.Equals(n, pageName, StringComparison.OrdinalIgnoreCase));
						}
					}
					if (!collision)
					{
						collision = mysteries.Any((MysteryEvent other) => other != m && string.Equals(other.EventItemName, pageName, StringComparison.OrdinalIgnoreCase));
					}
					if (!collision)
					{
						collision = mysteries.Any((MysteryEvent other) => other != m && string.Equals(other.Name, m.EventItemName, StringComparison.OrdinalIgnoreCase));
					}
					if (collision && m.StartDate.HasValue)
					{
						suggestedTitle = $"{pageName} (Mystery {m.StartDate.Value.Year})";
					}
				}
				m.WikiStatus.SuggestedPageTitle = suggestedTitle;
			}
			// Phase 1.5: Wiki-based disambiguation — MUST run before all other checks.
			// For mysteries where data-based detection didn't add (Mystery YYYY), check if the
			// year-specific wiki page exists. If it does, the mystery needs disambiguation.
			List<MysteryEvent> needsWikiDisambig = mysteries
				.Where(m => m.WikiStatus.SuggestedPageTitle == m.Name && m.StartDate.HasValue)
				.ToList();
			if (needsWikiDisambig.Count > 0)
			{
				using (AppLogger.Timed($"WikiDisambiguationCheck ({needsWikiDisambig.Count} mysteries)"))
				{
					IEnumerable<string> yearTitles = needsWikiDisambig
						.Select(m => $"{m.Name} (Mystery {m.StartDate!.Value.Year})")
						.Distinct(StringComparer.OrdinalIgnoreCase);
					Dictionary<string, bool> yearExistMap = await CheckPagesExistAsync(yearTitles);
					foreach (MysteryEvent m in needsWikiDisambig)
					{
						string yearTitle = $"{m.Name} (Mystery {m.StartDate!.Value.Year})";
						if (yearExistMap.GetValueOrDefault(yearTitle, false))
						{
							m.WikiStatus.SuggestedPageTitle = yearTitle;
							AppLogger.Info($"Wiki disambiguation: '{m.Name}' → '{yearTitle}'");
						}
					}
				}
			}
			// Gallery template detection
			try
			{
				var galleryTemplates = await FetchGalleryTemplatesAsync();
				foreach (var m in mysteries)
				{
					int decoCount = CountDecorations(m);
					bool isPetM = m.MysteryType == MysteryType.Pet;
					var galleryVariant = FindMatchingGalleryVariant(decoCount, isPetM, galleryTemplates);
					m.WikiStatus.MatchingGalleryVariant = galleryVariant;
				}
				AppLogger.Info($"GalleryCheck: matched {mysteries.Count(m => m.WikiStatus.MatchingGalleryVariant != null)} of {mysteries.Count}");
			}
			catch (Exception ex) { AppLogger.Info($"GalleryCheck error: {ex.Message}"); }
			Dictionary<string, List<(MysteryEvent Mystery, string Type)>> titleToMystery = new Dictionary<string, List<(MysteryEvent, string)>>(StringComparer.OrdinalIgnoreCase);
			foreach (MysteryEvent m2 in mysteries)
			{
				if (m2.WikiStatus.EventPageExists != true)
				{
					string title = m2.WikiStatus.SuggestedPageTitle ?? m2.Name;
					if (!titleToMystery.ContainsKey(title))
					{
						titleToMystery[title] = new List<(MysteryEvent, string)>();
					}
					titleToMystery[title].Add((m2, "EventPage"));
				}
				if (m2.WikiStatus.EventItemPageExists != true && !string.IsNullOrEmpty(m2.EventItemName))
				{
					if (!titleToMystery.ContainsKey(m2.EventItemName))
					{
						titleToMystery[m2.EventItemName] = new List<(MysteryEvent, string)>();
					}
					titleToMystery[m2.EventItemName].Add((m2, "ItemPage"));
				}
			}
			AppLogger.Info($"PageExistence: {titleToMystery.Count} titles to check");
			if (titleToMystery.Count > 0)
			{
				Dictionary<string, bool> existMap;
				using (AppLogger.Timed("CheckPagesExistAsync"))
				{
					existMap = await CheckPagesExistAsync(titleToMystery.Keys);
				}
				foreach (KeyValuePair<string, List<(MysteryEvent, string)>> item in titleToMystery)
				{
					item.Deconstruct(out var key, out var value);
					string title2 = key;
					List<(MysteryEvent Mystery, string Type)> entries = value;
					bool exists = existMap.GetValueOrDefault(title2, defaultValue: false);
					foreach (var (mystery, type) in entries)
					{
						if (type == "EventPage")
						{
							mystery.WikiStatus.EventPageExists = exists;
						}
						else
						{
							mystery.WikiStatus.EventItemPageExists = exists;
						}
					}
				}
			}
			List<MysteryEvent> needsTemplateCheck = mysteries.Where((MysteryEvent mysteryEvent) => mysteryEvent.WikiStatus.RewardTemplateState != WikiCheckState.Match).ToList();
			AppLogger.Info($"TemplateCheck: {needsTemplateCheck.Count} mysteries need check");
			if (needsTemplateCheck.Count > 0)
			{
				try
				{
					Dictionary<string, string> templates;
					using (AppLogger.Timed("FetchRewardTemplatesAsync"))
					{
						templates = await FetchRewardTemplatesAsync();
					}
					foreach (MysteryEvent m3 in needsTemplateCheck)
					{
						var (matches, variant) = CompareWithTemplates(m3, templates);
						m3.WikiStatus.RewardTemplateMatches = matches;
						m3.WikiStatus.RewardContentMatches = matches;
						m3.WikiStatus.MatchingVariant = variant;
					}
				}
				catch
				{
				}
			}
			List<MysteryEvent> needsPageContentCheck = mysteries.Where((MysteryEvent mysteryEvent) => mysteryEvent.WikiStatus.EventPageExists == true && mysteryEvent.WikiStatus.EventPageState != WikiCheckState.Match).ToList();
			AppLogger.Info($"EventPageContentCheck: {needsPageContentCheck.Count} mysteries need check");
			if (needsPageContentCheck.Count > 0)
			{
				try
				{
					List<string> pageTitles = needsPageContentCheck.Select((MysteryEvent mysteryEvent) => mysteryEvent.WikiStatus.SuggestedPageTitle ?? mysteryEvent.Name).Distinct<string>(StringComparer.OrdinalIgnoreCase).ToList();
					Dictionary<string, string> pageContents;
					using (AppLogger.Timed("FetchEventPagesContentAsync"))
					{
						pageContents = await FetchPagesContentAsync(pageTitles);
					}
					foreach (MysteryEvent m4 in needsPageContentCheck)
					{
						string title3 = m4.WikiStatus.SuggestedPageTitle ?? m4.Name;
						if (!pageContents.TryGetValue(title3, out string wikiContent))
						{
							continue;
						}
						if (IsDisambiguationPage(wikiContent) && m4.StartDate.HasValue)
						{
							string resolvedTitle = $"{m4.Name} (Mystery {m4.StartDate.Value.Year})";
							m4.WikiStatus.SuggestedPageTitle = resolvedTitle;
							string resolvedContent = await FetchPageContentAsync(resolvedTitle);
							if (resolvedContent == null)
							{
								m4.WikiStatus.EventPageExists = false;
								m4.WikiStatus.EventPageContentMatches = null;
								AppLogger.Info($"Disambiguation resolved: '{m4.Name}' → '{resolvedTitle}' (page not found)");
								continue;
							}
							wikiContent = resolvedContent;
							AppLogger.Info($"Disambiguation resolved: '{m4.Name}' → '{resolvedTitle}'");
						}
						string generated = GenerateEventPageWithDialogues(m4, m4.WikiStatus.MatchingVariant, dialogueService);
						m4.WikiStatus.EventPageContentMatches = CompareEventPageContent(generated, wikiContent);
						wikiContent = null;
					}
				}
				catch
				{
				}
			}
			List<MysteryEvent> needsItemContentCheck = mysteries.Where((MysteryEvent mysteryEvent) => mysteryEvent.WikiStatus.EventItemPageExists == true && mysteryEvent.WikiStatus.EventItemPageState != WikiCheckState.Match && !string.IsNullOrEmpty(mysteryEvent.EventItemName)).ToList();
			AppLogger.Info($"ItemPageContentCheck: {needsItemContentCheck.Count} mysteries need check");
			if (needsItemContentCheck.Count > 0)
			{
				try
				{
					List<string> itemTitles = needsItemContentCheck.Select((MysteryEvent mysteryEvent) => mysteryEvent.EventItemName).Distinct<string>(StringComparer.OrdinalIgnoreCase).ToList();
					Dictionary<string, string> itemContents;
					using (AppLogger.Timed("FetchItemPagesContentAsync"))
					{
						itemContents = await FetchPagesContentAsync(itemTitles);
					}
					foreach (MysteryEvent m5 in needsItemContentCheck)
					{
						if (itemContents.TryGetValue(m5.EventItemName, out string wikiContent2))
						{
							m5.WikiStatus.EventItemPageContentMatches = CompareEventItemPageContent(m5, wikiContent2, ds);
							wikiContent2 = null;
						}
					}
				}
				catch
				{
				}
			}
			try
			{
				List<string> imageFileNames = new List<string>();
				Dictionary<string, List<string>> mysteryImageMap = new Dictionary<string, List<string>>();
				foreach (MysteryEvent m6 in mysteries)
				{
					string imgName = m6.WikiImageName;
					string pageNameUsc = imgName.Replace(' ', '_');
					bool isPetM = m6.MysteryType == MysteryType.Pet;
					int decoCount = CountDecorations(m6);
					List<string> expectedImages = new List<string>
					{
						pageNameUsc + ".png",
						FormatFileName(imgName, 1),
						pageNameUsc + "_Icon.png"
					};
					for (int d = ((!isPetM) ? 1 : 0); d <= decoCount + (isPetM ? (-1) : 0); d++)
					{
						expectedImages.Add(FormatFileName(imgName + "Decoration", d));
					}
					// Event item images: one per chain level (find chain by display name)
					if (!string.IsNullOrEmpty(m6.EventItemName) && ds != null)
					{
						var eiChain = ds.Chains.FirstOrDefault(c =>
							string.Equals(c.DisplayName, m6.EventItemName, StringComparison.OrdinalIgnoreCase));
						if (eiChain != null && eiChain.Items.Count > 0)
						{
							var uniqueLevels = eiChain.Items.Select(i => i.Level).Distinct().OrderBy(l => l);
							foreach (var level in uniqueLevels)
								expectedImages.Add(FormatFileName(m6.EventItemName, level));
						}
					}
					m6.WikiStatus.ImagesTotalExpected = expectedImages.Count;
					mysteryImageMap[m6.ProgressionEventId] = expectedImages;
					imageFileNames.AddRange(expectedImages.Select((string f) => "File:" + f));
				}
				if (imageFileNames.Count > 0)
				{
					Dictionary<string, bool> imgExistMap = await CheckPagesExistAsync(imageFileNames);
					foreach (MysteryEvent m7 in mysteries)
					{
						if (mysteryImageMap.TryGetValue(m7.ProgressionEventId, out List<string> imgs))
						{
							m7.WikiStatus.ImagesExistOnWiki = imgs.Count((string f) => imgExistMap.GetValueOrDefault("File:" + f, defaultValue: false));
							imgs = null;
						}
					}
				}
			}
			catch (Exception ex)
			{
				AppLogger.Warn("Images check failed: " + ex.Message);
			}
			try
			{
				using (AppLogger.Timed("WikiListingCheck"))
				{
					string mainPageContent = await FetchPageContentAsync("Merge Mansion Wiki");
					string mysteryTableContent = await FetchPageContentAsync("Template:Events/Mystery Events");
					string moduleContent = await FetchPageContentAsync("Module:Datatable/Various");
					// Scope main page check to Mystery Events section only (avoid false positives from Seasonal Events)
					var mysteryEventsMatch = Regex.Match(mainPageContent ?? "", "! colspan = 2 \\| Latest \\[\\[Mystery Events\\]\\]");
					string mysteryEventsSection = "";
					if (mysteryEventsMatch.Success && mainPageContent != null)
					{
						string afterHdr = mainPageContent.Substring(mysteryEventsMatch.Index + mysteryEventsMatch.Length);
						Match nextHdr = Regex.Match(afterHdr, "! colspan = 2 \\|");
						mysteryEventsSection = nextHdr.Success
							? mainPageContent.Substring(mysteryEventsMatch.Index, mysteryEventsMatch.Length + nextHdr.Index)
							: mainPageContent.Substring(mysteryEventsMatch.Index);
					}
					foreach (MysteryEvent m8 in mysteries)
					{
						string pt = m8.WikiStatus.SuggestedPageTitle ?? m8.Name;
						// Main page: check only within Mystery Events section
						m8.WikiStatus.WikiMainPageListed = mysteryEventsSection.Contains(m8.Name, StringComparison.OrdinalIgnoreCase)
							|| (pt != m8.Name && mysteryEventsSection.Contains(pt, StringComparison.OrdinalIgnoreCase));
						// Mystery table: use link-specific matching to avoid false positives from same-name mysteries in other years
						m8.WikiStatus.WikiMysteryTableListed = mysteryTableContent != null && (
							mysteryTableContent.Contains($"[[{pt}]]", StringComparison.OrdinalIgnoreCase) ||
							mysteryTableContent.Contains($"[[{pt}|", StringComparison.OrdinalIgnoreCase) ||
							mysteryTableContent.Contains($"|{pt}|", StringComparison.OrdinalIgnoreCase) ||
							mysteryTableContent.Contains($"|{pt}}}", StringComparison.OrdinalIgnoreCase));
						m8.WikiStatus.WikiModuleListed = moduleContent != null && (moduleContent.Contains("\"" + m8.Name + "\"", StringComparison.OrdinalIgnoreCase) || moduleContent.Contains("\"" + pt + "\"", StringComparison.OrdinalIgnoreCase));
					}
				}
			}
			catch (Exception ex2)
			{
				AppLogger.Warn("Wiki listing check failed: " + ex2.Message);
			}
			await LoadManualConfirmFlagsAsync(mysteries);
			UpdateCache(mysteries, cache);
			SaveStatusCache(cache);
		}
	}

	public static async Task CheckSingleMysteryStatusAsync(MysteryEvent mystery, IReadOnlyList<MysteryEvent> allMysteries, DataService? ds, DialogueService? dialogueService = null)
	{
		using (AppLogger.Timed($"CheckSingleMysteryStatusAsync ({mystery.Name})"))
		{
			MysteryWikiStatusCache cache = LoadStatusCache();

			// Phase 1: Disambiguation (reuse allMysteries for name collision detection)
			HashSet<MysteryEvent> nameGroups = (from g in allMysteries.GroupBy(m => m.Name, StringComparer.OrdinalIgnoreCase)
				where g.Count() > 1
				select g).SelectMany(g => g).ToHashSet();

			mystery.WikiStatus.SuggestedPageTitle = null;
			string pageName = mystery.Name;
			string suggestedTitle = pageName;
			if (nameGroups.Contains(mystery) && mystery.StartDate.HasValue)
			{
				suggestedTitle = $"{pageName} (Mystery {mystery.StartDate.Value.Year})";
			}
			else
			{
				bool collision = false;
				if (ds != null)
				{
					collision = ds.ChainNames.Values.Any(n => string.Equals(n, pageName, StringComparison.OrdinalIgnoreCase));
					if (!collision)
						collision = ds.ItemNames.Values.Any(n => string.Equals(n, pageName, StringComparison.OrdinalIgnoreCase));
				}
				if (!collision)
					collision = allMysteries.Any(other => other != mystery && string.Equals(other.EventItemName, pageName, StringComparison.OrdinalIgnoreCase));
				if (!collision)
					collision = allMysteries.Any(other => other != mystery && string.Equals(other.Name, mystery.EventItemName, StringComparison.OrdinalIgnoreCase));
				if (collision && mystery.StartDate.HasValue)
					suggestedTitle = $"{pageName} (Mystery {mystery.StartDate.Value.Year})";
			}
			mystery.WikiStatus.SuggestedPageTitle = suggestedTitle;

			// Phase 1.5: Wiki-based disambiguation
			if (mystery.WikiStatus.SuggestedPageTitle == mystery.Name && mystery.StartDate.HasValue)
			{
				string yearTitle = $"{mystery.Name} (Mystery {mystery.StartDate.Value.Year})";
				bool yearExists = await WikiMappingService.CheckPageExistsAsync(yearTitle);
				if (yearExists)
				{
					mystery.WikiStatus.SuggestedPageTitle = yearTitle;
					AppLogger.Info($"Wiki disambiguation: '{mystery.Name}' → '{yearTitle}'");
				}
			}

			// Gallery template detection
			try
			{
				var galleryTemplates = await FetchGalleryTemplatesAsync();
				int decoCount = CountDecorations(mystery);
				bool isPetM = mystery.MysteryType == MysteryType.Pet;
				mystery.WikiStatus.MatchingGalleryVariant = FindMatchingGalleryVariant(decoCount, isPetM, galleryTemplates);
			}
			catch (Exception ex) { AppLogger.Info($"GalleryCheck error: {ex.Message}"); }

			// Page existence
			string eventTitle = mystery.WikiStatus.SuggestedPageTitle ?? mystery.Name;
			mystery.WikiStatus.EventPageExists = await WikiMappingService.CheckPageExistsAsync(eventTitle);
			if (!string.IsNullOrEmpty(mystery.EventItemName))
				mystery.WikiStatus.EventItemPageExists = await WikiMappingService.CheckPageExistsAsync(mystery.EventItemName);

			// Reward template check
			try
			{
				var templates = await FetchRewardTemplatesAsync();
				var (matches, variant) = CompareWithTemplates(mystery, templates);
				mystery.WikiStatus.RewardTemplateMatches = matches;
				mystery.WikiStatus.RewardContentMatches = matches;
				mystery.WikiStatus.MatchingVariant = variant;
			}
			catch { }

			// Event page content check
			if (mystery.WikiStatus.EventPageExists == true)
			{
				try
				{
					string wikiContent = await FetchPageContentAsync(eventTitle);
					if (wikiContent != null)
					{
						if (IsDisambiguationPage(wikiContent) && mystery.StartDate.HasValue)
						{
							string resolvedTitle = $"{mystery.Name} (Mystery {mystery.StartDate.Value.Year})";
							mystery.WikiStatus.SuggestedPageTitle = resolvedTitle;
							wikiContent = await FetchPageContentAsync(resolvedTitle);
							if (wikiContent == null)
							{
								mystery.WikiStatus.EventPageExists = false;
								mystery.WikiStatus.EventPageContentMatches = null;
							}
						}
						if (wikiContent != null)
						{
							string generated = GenerateEventPageWithDialogues(mystery, mystery.WikiStatus.MatchingVariant, dialogueService);
							mystery.WikiStatus.EventPageContentMatches = CompareEventPageContent(generated, wikiContent);
						}
					}
				}
				catch { }
			}

			// Event item page content check
			if (mystery.WikiStatus.EventItemPageExists == true && !string.IsNullOrEmpty(mystery.EventItemName))
			{
				try
				{
					string wikiContent = await FetchPageContentAsync(mystery.EventItemName);
					if (wikiContent != null)
						mystery.WikiStatus.EventItemPageContentMatches = CompareEventItemPageContent(mystery, wikiContent, ds);
				}
				catch { }
			}

			// Images check
			try
			{
				string imgName = mystery.WikiImageName;
				string pageNameUsc = imgName.Replace(' ', '_');
				bool isPetM = mystery.MysteryType == MysteryType.Pet;
				int decoCount = CountDecorations(mystery);
				List<string> expectedImages = new List<string>
				{
					pageNameUsc + ".png",
					FormatFileName(imgName, 1),
					pageNameUsc + "_Icon.png"
				};
				for (int d = (!isPetM ? 1 : 0); d <= decoCount + (isPetM ? -1 : 0); d++)
					expectedImages.Add(FormatFileName(imgName + "Decoration", d));
				if (!string.IsNullOrEmpty(mystery.EventItemName) && ds != null)
				{
					var eiChain = ds.Chains.FirstOrDefault(c =>
						string.Equals(c.DisplayName, mystery.EventItemName, StringComparison.OrdinalIgnoreCase));
					if (eiChain != null && eiChain.Items.Count > 0)
					{
						foreach (var level in eiChain.Items.Select(i => i.Level).Distinct().OrderBy(l => l))
							expectedImages.Add(FormatFileName(mystery.EventItemName, level));
					}
				}
				mystery.WikiStatus.ImagesTotalExpected = expectedImages.Count;
				var imgFileNames = expectedImages.Select(f => "File:" + f);
				var imgExistMap = await CheckPagesExistAsync(imgFileNames);
				mystery.WikiStatus.ImagesExistOnWiki = expectedImages.Count(f => imgExistMap.GetValueOrDefault("File:" + f, false));
			}
			catch (Exception ex) { AppLogger.Warn("Images check failed: " + ex.Message); }

			// Wiki listing check
			try
			{
				string mainPageContent = await FetchPageContentAsync("Merge Mansion Wiki");
				string mysteryTableContent = await FetchPageContentAsync("Template:Events/Mystery Events");
				string moduleContent = await FetchPageContentAsync("Module:Datatable/Various");
				var mysteryEventsMatch = System.Text.RegularExpressions.Regex.Match(mainPageContent ?? "", "! colspan = 2 \\| Latest \\[\\[Mystery Events\\]\\]");
				string mysteryEventsSection = "";
				if (mysteryEventsMatch.Success && mainPageContent != null)
				{
					string afterHdr = mainPageContent.Substring(mysteryEventsMatch.Index + mysteryEventsMatch.Length);
					var nextHdr = System.Text.RegularExpressions.Regex.Match(afterHdr, "! colspan = 2 \\|");
					mysteryEventsSection = nextHdr.Success
						? mainPageContent.Substring(mysteryEventsMatch.Index, mysteryEventsMatch.Length + nextHdr.Index)
						: mainPageContent.Substring(mysteryEventsMatch.Index);
				}
				string pt = mystery.WikiStatus.SuggestedPageTitle ?? mystery.Name;
				mystery.WikiStatus.WikiMainPageListed = mysteryEventsSection.Contains(mystery.Name, StringComparison.OrdinalIgnoreCase)
					|| (pt != mystery.Name && mysteryEventsSection.Contains(pt, StringComparison.OrdinalIgnoreCase));
				mystery.WikiStatus.WikiMysteryTableListed = mysteryTableContent != null && (
					mysteryTableContent.Contains($"[[{pt}]]", StringComparison.OrdinalIgnoreCase) ||
					mysteryTableContent.Contains($"[[{pt}|", StringComparison.OrdinalIgnoreCase) ||
					mysteryTableContent.Contains($"|{pt}|", StringComparison.OrdinalIgnoreCase) ||
					mysteryTableContent.Contains($"|{pt}}}", StringComparison.OrdinalIgnoreCase));
				mystery.WikiStatus.WikiModuleListed = moduleContent != null && (moduleContent.Contains("\"" + mystery.Name + "\"", StringComparison.OrdinalIgnoreCase) || moduleContent.Contains("\"" + pt + "\"", StringComparison.OrdinalIgnoreCase));
			}
			catch (Exception ex) { AppLogger.Warn("Wiki listing check failed: " + ex.Message); }

			await LoadManualConfirmFlagsAsync(new[] { mystery });
			UpdateCache(new[] { mystery }, cache);
			SaveStatusCache(cache);
		}
	}

	public static async Task<string> PublishPageAsync(string username, string password, string pageTitle, string content, string editSummary)
	{
		using HttpClient client = await WikiMappingService.CreateAuthenticatedClientAsync(username, password);
		string csrfToken = JsonDocument.Parse(await client.GetStringAsync("https://merge-mansion.fandom.com/api.php?action=query&meta=tokens&format=json")).RootElement.GetProperty("query").GetProperty("tokens").GetProperty("csrftoken")
			.GetString();
		JsonDocument editDoc = JsonDocument.Parse(await (await client.PostAsync("https://merge-mansion.fandom.com/api.php", new FormUrlEncodedContent(new Dictionary<string, string>
		{
			["action"] = "edit",
			["title"] = pageTitle,
			["text"] = content,
			["token"] = csrfToken,
			["summary"] = editSummary,
			["bot"] = "1",
			["format"] = "json"
		}))).Content.ReadAsStringAsync());
		if (editDoc.RootElement.TryGetProperty("error", out var error))
		{
			throw new Exception("Wiki edit failed: " + error.GetProperty("info").GetString());
		}
		if (editDoc.RootElement.TryGetProperty("edit", out var edit))
		{
			JsonElement r;
			string editResult = (edit.TryGetProperty("result", out r) ? r.GetString() : "?");
			return "Edit result: " + editResult;
		}
		return "Unknown response";
	}

	public static async Task<string?> FetchPageContentAsync(string title)
	{
		string content;
		return (await FetchPagesContentAsync(new string[1] { title })).TryGetValue(title, out content) ? content : null;
	}

	public static bool IsDisambiguationPage(string content)
	{
		return content.Contains("[[Category:Disambiguation]]", StringComparison.OrdinalIgnoreCase);
	}

	public static async Task<(string PageTitle, string? Content)> FetchEventPageResolvingDisambigAsync(string pageTitle, string mysteryName, DateTime? startDate)
	{
		string content = await FetchPageContentAsync(pageTitle);
		if (content != null && IsDisambiguationPage(content) && startDate.HasValue)
		{
			string resolvedTitle = $"{mysteryName} (Mystery {startDate.Value.Year})";
			AppLogger.Info($"Disambiguation detected at '{pageTitle}', resolving to '{resolvedTitle}'");
			return (PageTitle: resolvedTitle, Content: await FetchPageContentAsync(resolvedTitle));
		}
		return (PageTitle: pageTitle, Content: content);
	}

	public static string NormalizeDiffContent(string content)
	{
		content = Regex.Replace(content, "<!--.*?-->", "", RegexOptions.Singleline);
		content = content.Replace("\r\n", "\n").Replace("\r", "\n");
		content = content.Replace('\u2018', '\'').Replace('\u2019', '\'').Replace('\u201C', '"')
			.Replace('\u201D', '"')
			.Replace("…", "...")
			.Replace("\u2014", "-")
			.Replace("\u2013", "-");
		content = Regex.Replace(content, "'''(.+?):'''\\s*", "'''$1''': ");
		content = Regex.Replace(content, "(={2,})\\s*([^=\\n]+?)\\s*(={2,})", "$1 $2 $3");
		content = Regex.Replace(content, "\\|-\\|\\s*(.+?)\\s*=\\s*$", "|-| $1 =", RegexOptions.Multiline);
		content = Regex.Replace(content, "^\\|\\s*(?=[^-}|!])", "| ", RegexOptions.Multiline);
		IEnumerable<string> values = from l in content.Split('\n')
			select l.Trim();
		content = string.Join("\n", values);
		content = Regex.Replace(content, "\\n{3,}", "\n\n");
		return content.Trim();
	}

	public static List<DiffLine> ComputeLineDiffs(string wikiContent, string generatedContent)
	{
		string text = NormalizeDiffContent(wikiContent);
		string text2 = NormalizeDiffContent(generatedContent);
		string[] array = text.Split('\n');
		string[] array2 = text2.Split('\n');
		if (array.Any(IsTabberHeader) && array2.Any(IsTabberHeader))
		{
			return ComputeTabberAwareDiff(array, array2);
		}
		return ComputeLcsDiff(array, array2);
	}

	private static List<DiffLine> ComputeTabberAwareDiff(string[] wikiLines, string[] genLines)
	{
		TabberRegions tabberRegions = SplitByTabber(wikiLines);
		TabberRegions tabberRegions2 = SplitByTabber(genLines);
		List<DiffLine> list = new List<DiffLine>();
		list.AddRange(ComputeLcsDiff(tabberRegions.Before, tabberRegions2.Before));
		List<TabSection> tabs = tabberRegions.Tabs;
		List<TabSection> tabs2 = tabberRegions2.Tabs;
		List<(int, int)> list2 = MatchTabSectionsLcs(tabs, tabs2);
		int num = 0;
		int num2 = 0;
		foreach (var (num3, num4) in list2)
		{
			while (num < num3)
			{
				EmitTabRemoved(list, tabs[num++]);
			}
			while (num2 < num4)
			{
				EmitTabAdded(list, tabs2[num2++]);
			}
			EmitMatchedTabPair(list, tabs[num3], tabs2[num4]);
			num = num3 + 1;
			num2 = num4 + 1;
		}
		while (num < tabs.Count)
		{
			EmitTabRemoved(list, tabs[num++]);
		}
		while (num2 < tabs2.Count)
		{
			EmitTabAdded(list, tabs2[num2++]);
		}
		list.AddRange(ComputeLcsDiff(tabberRegions.After, tabberRegions2.After));
		return list;
	}

	private static List<(int WikiIdx, int GenIdx)> MatchTabSectionsLcs(List<TabSection> wTabs, List<TabSection> gTabs)
	{
		int count = wTabs.Count;
		int count2 = gTabs.Count;
		int[,] array = new int[count + 1, count2 + 1];
		for (int i = 1; i <= count; i++)
		{
			for (int j = 1; j <= count2; j++)
			{
				array[i, j] = (TabSectionsAreRelated(wTabs[i - 1], gTabs[j - 1]) ? (array[i - 1, j - 1] + 1) : Math.Max(array[i - 1, j], array[i, j - 1]));
			}
		}
		List<(int, int)> list = new List<(int, int)>();
		int num = count;
		int num2 = count2;
		while (num > 0 && num2 > 0)
		{
			if (TabSectionsAreRelated(wTabs[num - 1], gTabs[num2 - 1]) && array[num, num2] == array[num - 1, num2 - 1] + 1)
			{
				list.Add((num - 1, num2 - 1));
				num--;
				num2--;
			}
			else if (array[num - 1, num2] >= array[num, num2 - 1])
			{
				num--;
			}
			else
			{
				num2--;
			}
		}
		list.Reverse();
		return list;
	}

	private static bool TabSectionsAreRelated(TabSection a, TabSection b)
	{
		if (a.Header == b.Header)
		{
			return true;
		}
		HashSet<string> hashSet = (from l in a.Content.Select(StripWikiFormatting)
			where !string.IsNullOrWhiteSpace(l)
			select l).ToHashSet();
		HashSet<string> bLines = new HashSet<string>(from l in b.Content.Select(StripWikiFormatting)
			where !string.IsNullOrWhiteSpace(l)
			select l);
		if (hashSet.Count == 0 && bLines.Count == 0)
		{
			return false;
		}
		int num = hashSet.Count((string l) => bLines.Contains(l));
		int num2 = Math.Max(hashSet.Count, bLines.Count);
		return num2 > 0 && (double)num / (double)num2 > 0.5;
	}

	private static string StripWikiFormatting(string line)
	{
		string input = Regex.Replace(line, "'{2,}", "");
		input = Regex.Replace(input, "</?[a-zA-Z][^>]*>", "");
		input = Regex.Replace(input, "\\s+", " ");
		return input.Trim();
	}

	private static void EmitTabRemoved(List<DiffLine> result, TabSection tab)
	{
		result.Add(new DiffLine
		{
			Type = DiffLineType.Removed,
			Text = tab.Header
		});
		string[] content = tab.Content;
		foreach (string text in content)
		{
			result.Add(new DiffLine
			{
				Type = DiffLineType.Removed,
				Text = text
			});
		}
	}

	private static void EmitTabAdded(List<DiffLine> result, TabSection tab)
	{
		result.Add(new DiffLine
		{
			Type = DiffLineType.Added,
			Text = tab.Header
		});
		string[] content = tab.Content;
		foreach (string text in content)
		{
			result.Add(new DiffLine
			{
				Type = DiffLineType.Added,
				Text = text
			});
		}
	}

	private static void EmitMatchedTabPair(List<DiffLine> result, TabSection wTab, TabSection gTab)
	{
		if (wTab.Header == gTab.Header)
		{
			result.Add(new DiffLine
			{
				Type = DiffLineType.Match,
				Text = wTab.Header
			});
		}
		else
		{
			result.Add(new DiffLine
			{
				Type = DiffLineType.Removed,
				Text = wTab.Header
			});
			result.Add(new DiffLine
			{
				Type = DiffLineType.Added,
				Text = gTab.Header
			});
		}
		result.AddRange(ComputeLcsDiff(wTab.Content, gTab.Content));
	}

	private static bool IsTabberHeader(string line)
	{
		return line.TrimStart().StartsWith("|-|");
	}

	private static TabberRegions SplitByTabber(string[] lines)
	{
		int num = -1;
		int num2 = -1;
		for (int i = 0; i < lines.Length; i++)
		{
			string text = lines[i].Trim().ToLowerInvariant();
			if (text == "<tabber>" && num < 0)
			{
				num = i;
			}
			if (text == "</tabber>")
			{
				num2 = i;
			}
		}
		if (num < 0 || num2 < 0)
		{
			return new TabberRegions(lines, new List<TabSection>(), Array.Empty<string>());
		}
		string[] subArray = lines[..(num + 1)];
		string[] subArray2 = lines[num2..];
		List<TabSection> list = new List<TabSection>();
		string text2 = null;
		List<string> list2 = new List<string>();
		for (int j = num + 1; j < num2; j++)
		{
			if (IsTabberHeader(lines[j]))
			{
				if (text2 != null)
				{
					list.Add(new TabSection(text2, list2.ToArray()));
				}
				text2 = lines[j];
				list2.Clear();
			}
			else if (text2 != null)
			{
				list2.Add(lines[j]);
			}
			else
			{
				list2.Add(lines[j]);
			}
		}
		if (text2 != null)
		{
			list.Add(new TabSection(text2, list2.ToArray()));
		}
		return new TabberRegions(subArray, list, subArray2);
	}

	public static List<DiffLine> ComputeRewardLevelDiff(string wikiContent, string generatedContent)
	{
		string text = NormalizeDiffContent(wikiContent);
		string text2 = NormalizeDiffContent(generatedContent);
		string[] lines = text.Split('\n');
		string[] lines2 = text2.Split('\n');
		List<(string, List<string>)> list = SplitRewardIntoLevelBlocks(lines);
		List<(string, List<string>)> list2 = SplitRewardIntoLevelBlocks(lines2);
		Dictionary<string, List<string>> dictionary = new Dictionary<string, List<string>>();
		Dictionary<string, List<string>> dictionary2 = new Dictionary<string, List<string>>();
		List<string> list3 = new List<string>();
		List<string> list4 = new List<string>();
		foreach (var item5 in list)
		{
			string item = item5.Item1;
			List<string> item2 = item5.Item2;
			dictionary[item] = item2;
			list3.Add(item);
		}
		foreach (var item6 in list2)
		{
			string item3 = item6.Item1;
			List<string> item4 = item6.Item2;
			dictionary2[item3] = item4;
			list4.Add(item3);
		}
		List<string> list5 = new List<string>();
		int num = 0;
		int num2 = 0;
		HashSet<string> hashSet = new HashSet<string>(list4);
		HashSet<string> hashSet2 = new HashSet<string>(list3);
		while (num < list3.Count || num2 < list4.Count)
		{
			if (num2 < list4.Count && num < list3.Count && list3[num] == list4[num2])
			{
				list5.Add(list4[num2]);
				num++;
				num2++;
			}
			else if (num2 < list4.Count && !hashSet2.Contains(list4[num2]))
			{
				list5.Add(list4[num2]);
				num2++;
			}
			else if (num < list3.Count && !hashSet.Contains(list3[num]))
			{
				list5.Add(list3[num]);
				num++;
			}
			else if (num2 < list4.Count)
			{
				list5.Add(list4[num2]);
				num2++;
			}
			else
			{
				list5.Add(list3[num]);
				num++;
			}
		}
		HashSet<string> hashSet3 = new HashSet<string>();
		List<string> list6 = new List<string>();
		foreach (string item7 in list5)
		{
			if (hashSet3.Add(item7))
			{
				list6.Add(item7);
			}
		}
		List<DiffLine> list7 = new List<DiffLine>();
		foreach (string item8 in list6)
		{
			List<string> value;
			bool flag = dictionary.TryGetValue(item8, out value);
			List<string> value2;
			bool flag2 = dictionary2.TryGetValue(item8, out value2);
			if (flag && flag2)
			{
				if (item8.StartsWith("_"))
				{
					list7.AddRange(ComputeLcsDiff(value.ToArray(), value2.ToArray()));
				}
				else
				{
					list7.AddRange(ComputePositionalDiff(value, value2));
				}
			}
			else if (flag)
			{
				foreach (string item9 in value)
				{
					list7.Add(new DiffLine
					{
						Type = DiffLineType.Removed,
						Text = item9
					});
				}
			}
			else
			{
				if (!flag2)
				{
					continue;
				}
				foreach (string item10 in value2)
				{
					list7.Add(new DiffLine
					{
						Type = DiffLineType.Added,
						Text = item10
					});
				}
			}
		}
		return list7;
	}

	private static List<DiffLine> ComputePositionalDiff(List<string> wikiBlock, List<string> genBlock)
	{
		List<DiffLine> list = new List<DiffLine>();
		int num = Math.Max(wikiBlock.Count, genBlock.Count);
		for (int i = 0; i < num; i++)
		{
			bool flag = i < wikiBlock.Count;
			bool flag2 = i < genBlock.Count;
			if (flag && flag2)
			{
				if (wikiBlock[i] == genBlock[i])
				{
					list.Add(new DiffLine
					{
						Type = DiffLineType.Match,
						Text = wikiBlock[i]
					});
					continue;
				}
				list.Add(new DiffLine
				{
					Type = DiffLineType.Removed,
					Text = wikiBlock[i]
				});
				list.Add(new DiffLine
				{
					Type = DiffLineType.Added,
					Text = genBlock[i]
				});
			}
			else if (flag)
			{
				list.Add(new DiffLine
				{
					Type = DiffLineType.Removed,
					Text = wikiBlock[i]
				});
			}
			else
			{
				list.Add(new DiffLine
				{
					Type = DiffLineType.Added,
					Text = genBlock[i]
				});
			}
		}
		return list;
	}

	private static List<(string Key, List<string> Lines)> SplitRewardIntoLevelBlocks(string[] lines)
	{
		List<(string, List<string>)> list = new List<(string, List<string>)>();
		List<string> list2 = new List<string>();
		string item = "_header";
		for (int i = 0; i < lines.Length; i++)
		{
			string text = lines[i].Trim();
			if (text == "|-" && i + 1 < lines.Length)
			{
				string input = lines[i + 1].Trim();
				string text2 = null;
				Match match = Regex.Match(input, "^\\|\\s*(\\d+)\\s*$");
				if (match.Success)
				{
					text2 = match.Groups[1].Value;
				}
				Match match2 = Regex.Match(input, "^\\|\\s*\\{\\{PremiumLevel\\|(\\d+)\\}\\}\\s*$");
				if (match2.Success)
				{
					text2 = "PremiumLevel" + match2.Groups[1].Value;
				}
				if (text2 != null)
				{
					if (list2.Count > 0)
					{
						list.Add((item, list2));
					}
					list2 = new List<string> { lines[i] };
					item = text2;
					continue;
				}
			}
			if (text == "|}")
			{
				if (list2.Count > 0)
				{
					list.Add((item, list2));
				}
				list.Add(("_footer", new List<string> { lines[i] }));
				list2 = new List<string>();
				item = "_trailing";
			}
			else
			{
				list2.Add(lines[i]);
			}
		}
		if (list2.Count > 0)
		{
			list.Add((item, list2));
		}
		return list;
	}

	private static List<DiffLine> ComputeLcsDiff(string[] wikiLines, string[] genLines)
	{
		int num = wikiLines.Length;
		int num2 = genLines.Length;
		int[,] array = new int[num + 1, num2 + 1];
		for (int i = 1; i <= num; i++)
		{
			for (int j = 1; j <= num2; j++)
			{
				array[i, j] = ((wikiLines[i - 1] == genLines[j - 1]) ? (array[i - 1, j - 1] + 1) : Math.Max(array[i - 1, j], array[i, j - 1]));
			}
		}
		Stack<DiffLine> stack = new Stack<DiffLine>();
		int num3 = num;
		int num4 = num2;
		while (num3 > 0 || num4 > 0)
		{
			if (num3 > 0 && num4 > 0 && wikiLines[num3 - 1] == genLines[num4 - 1])
			{
				stack.Push(new DiffLine
				{
					Type = DiffLineType.Match,
					Text = wikiLines[num3 - 1]
				});
				num3--;
				num4--;
			}
			else if (num4 > 0 && (num3 == 0 || array[num3, num4 - 1] >= array[num3 - 1, num4]))
			{
				stack.Push(new DiffLine
				{
					Type = DiffLineType.Added,
					Text = genLines[num4 - 1]
				});
				num4--;
			}
			else
			{
				stack.Push(new DiffLine
				{
					Type = DiffLineType.Removed,
					Text = wikiLines[num3 - 1]
				});
				num3--;
			}
		}
		List<DiffLine> list = new List<DiffLine>(stack.Count);
		while (stack.Count > 0)
		{
			list.Add(stack.Pop());
		}
		return PairSimilarLines(list);
	}

	private static List<DiffLine> PairSimilarLines(List<DiffLine> diffs)
	{
		List<DiffLine> list = new List<DiffLine>(diffs.Count);
		int i = 0;
		while (i < diffs.Count)
		{
			if (diffs[i].Type == DiffLineType.Removed)
			{
				int num = i;
				for (; i < diffs.Count && diffs[i].Type == DiffLineType.Removed; i++)
				{
				}
				int num2 = i;
				for (; i < diffs.Count && diffs[i].Type == DiffLineType.Added; i++)
				{
				}
				int num3 = num2 - num;
				int num4 = i - num2;
				if (num3 > 0 && num4 > 0)
				{
					List<DiffLine> range = diffs.GetRange(num, num3);
					List<DiffLine> range2 = diffs.GetRange(num2, num4);
					bool[] array = new bool[num4];
					List<(int, int, double)> list2 = new List<(int, int, double)>();
					for (int j = 0; j < num3; j++)
					{
						int num5 = -1;
						double num6 = 0.0;
						for (int k = 0; k < num4; k++)
						{
							double num7 = LineSimilarity(range[j].Text, range2[k].Text);
							if (num7 > num6)
							{
								num6 = num7;
								num5 = k;
							}
						}
						if (num6 >= 0.4 && num5 >= 0)
						{
							list2.Add((j, num5, num6));
						}
					}
					list2.Sort(((int ri, int ai, double sim) a, (int ri, int ai, double sim) b) => b.sim.CompareTo(a.sim));
					int?[] array2 = new int?[num3];
					foreach (var (num8, num9, _) in list2)
					{
						if (!array2[num8].HasValue && !array[num9])
						{
							array2[num8] = num9;
							array[num9] = true;
						}
					}
					for (int num10 = 0; num10 < num3; num10++)
					{
						int? num11 = array2[num10];
						if (num11.HasValue)
						{
							int valueOrDefault = num11.GetValueOrDefault();
							if (true)
							{
								list.Add(new DiffLine
								{
									Type = DiffLineType.Modified,
									Text = range2[valueOrDefault].Text,
									OldText = range[num10].Text
								});
								continue;
							}
						}
						list.Add(range[num10]);
					}
					for (int num12 = 0; num12 < num4; num12++)
					{
						if (!array[num12])
						{
							list.Add(range2[num12]);
						}
					}
				}
				else
				{
					for (int num13 = num; num13 < num + num3; num13++)
					{
						list.Add(diffs[num13]);
					}
					for (int num14 = num2; num14 < num2 + num4; num14++)
					{
						list.Add(diffs[num14]);
					}
				}
			}
			else
			{
				list.Add(diffs[i]);
				i++;
			}
		}
		return list;
	}

	private static double LineSimilarity(string a, string b)
	{
		if (a == b)
		{
			return 1.0;
		}
		if (a.Length == 0 || b.Length == 0)
		{
			return 0.0;
		}
		int num = Math.Max(a.Length, b.Length);
		int num2 = LevenshteinDistance(a, b);
		return 1.0 - (double)num2 / (double)num;
	}

	private static int LevenshteinDistance(string s, string t)
	{
		if (s.Length > t.Length)
		{
			string text = t;
			t = s;
			s = text;
		}
		int length = s.Length;
		int length2 = t.Length;
		int[] array = new int[length + 1];
		for (int i = 0; i <= length; i++)
		{
			array[i] = i;
		}
		for (int j = 1; j <= length2; j++)
		{
			int num = array[0];
			array[0] = j;
			for (int k = 1; k <= length; k++)
			{
				int num2 = array[k];
				int num3 = ((s[k - 1] != t[j - 1]) ? 1 : 0);
				array[k] = Math.Min(Math.Min(array[k] + 1, array[k - 1] + 1), num + num3);
				num = num2;
			}
		}
		return array[length];
	}

	public static async Task<(string? WikiContent, string GeneratedContent, List<DiffLine> Diffs, string PageTitle)> ComputeDiffAsync(MysteryEvent mystery, MysteryDiffScope scope, DataService? ds, WikiMappingCache? wikiMapping, MysteryItemMapping? mapping, DialogueService? dialogueService, string? overrideRewardVariant = null)
	{
		string generated;
		string pageTitle;
		switch (scope)
		{
		case MysteryDiffScope.Rewards:
		{
			string variant = mystery.WikiStatus.MatchingVariant;
			if (variant == null)
			{
				string foundVariant = CompareWithTemplates(mystery, await FetchRewardTemplatesAsync()).Variant;
				variant = foundVariant;
			}
			generated = GenerateRewardTemplate(mystery, mapping);
			pageTitle = ((variant == null) ? ("Template:Mystery Pass/Rewards/" + (mystery.WikiStatus.SuggestedPageTitle ?? mystery.Name)) : ((variant == "") ? "Template:Mystery Pass/Rewards" : ("Template:Mystery Pass/Rewards/" + variant)));
			break;
		}
		case MysteryDiffScope.EventPage:
			pageTitle = mystery.WikiStatus.SuggestedPageTitle ?? mystery.Name;
			// Always (re-)detect gallery variant to ensure correctness
			if (true)
			{
				try
				{
					var galleryTemplates = await FetchGalleryTemplatesAsync();
					int decoCount = CountDecorations(mystery);
					bool isPetM = mystery.MysteryType == MysteryType.Pet;
					AppLogger.Info($"Gallery detect: {mystery.Name}, decoCount={decoCount}, isPet={isPetM}, templates={galleryTemplates.Count}");
					foreach (var (gt, gc) in galleryTemplates)
					{
						int slots = CountGalleryDecorationSlots(gc);
						int gIdx = gt.IndexOf("/Gallery", StringComparison.OrdinalIgnoreCase);
						string sfx = gIdx >= 0 ? gt[(gIdx + "/Gallery".Length)..].TrimStart('/') : "";
						bool isPetT = sfx.StartsWith("Pet", StringComparison.OrdinalIgnoreCase);
						AppLogger.Info($"  Template '{gt}': slots={slots}, isPet={isPetT}, suffix='{sfx}'");
					}
					mystery.WikiStatus.MatchingGalleryVariant = FindMatchingGalleryVariant(decoCount, isPetM, galleryTemplates);
					// If no match, assign next variant name (template will be created during publish)
					if (mystery.WikiStatus.MatchingGalleryVariant == null && decoCount > 0)
					{
						string newVariant = await GetNextGalleryVariantNameAsync(isPetM);
						mystery.WikiStatus.MatchingGalleryVariant = newVariant;
						mystery.WikiStatus.PendingGalleryTemplateContent = GenerateGalleryTemplateContent(decoCount, isPetM);
						AppLogger.Info($"Gallery: no match for {decoCount} decorations, will create Template:Mystery Pass/Gallery/{newVariant}");
					}
				}
				catch (Exception ex)
				{
					AppLogger.Info($"Gallery auto-detect failed: {ex.Message}");
				}
			}
			generated = GenerateEventPageWithDialogues(mystery, overrideRewardVariant ?? mystery.WikiStatus.MatchingVariant, dialogueService);
			break;
		case MysteryDiffScope.EventItemPage:
			pageTitle = mystery.EventItemName ?? mystery.Name;
			generated = GenerateEventItemPage(mystery, ds, wikiMapping);
			break;
		default:
			return (WikiContent: null, GeneratedContent: "", Diffs: new List<DiffLine>(), PageTitle: "");
		}
		string wikiContent;
		if (scope == MysteryDiffScope.EventPage)
		{
			(string PageTitle, string? Content) tuple = await FetchEventPageResolvingDisambigAsync(pageTitle, mystery.Name, mystery.StartDate);
			string resolvedTitle = tuple.PageTitle;
			string resolvedContent = tuple.Content;
			pageTitle = resolvedTitle;
			wikiContent = resolvedContent;
			if (resolvedTitle != (mystery.WikiStatus.SuggestedPageTitle ?? mystery.Name))
			{
				mystery.WikiStatus.SuggestedPageTitle = resolvedTitle;
				// Re-generate with updated SuggestedPageTitle so EventDisplayName is correct
				generated = GenerateEventPageWithDialogues(mystery, overrideRewardVariant ?? mystery.WikiStatus.MatchingVariant, dialogueService);
			}
		}
		else
		{
			wikiContent = await FetchPageContentAsync(pageTitle);
		}
		if (wikiContent == null)
		{
			return (WikiContent: null, GeneratedContent: generated, Diffs: new List<DiffLine>(), PageTitle: pageTitle);
		}
		List<DiffLine> diffs = ((scope == MysteryDiffScope.Rewards) ? ComputeRewardLevelDiff(wikiContent, generated) : ComputeLineDiffs(wikiContent, generated));
		return (WikiContent: wikiContent, GeneratedContent: generated, Diffs: diffs, PageTitle: pageTitle);
	}

	public static void LoadPetDisplayNames(string? basePath, string? apkVersion)
	{
		_petDisplayNames = null;
		if (string.IsNullOrEmpty(basePath) || string.IsNullOrEmpty(apkVersion))
		{
			return;
		}
		string path = Path.Combine(basePath, apkVersion, "Dump", "Experimental", "Pets.json");
		if (!File.Exists(path))
		{
			return;
		}
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

	internal static int CountDecorations(MysteryEvent mystery)
	{
		int num = 0;
		// Pet mysteries always have a Pet Icon (decoration #0)
		if (mystery.MysteryType == MysteryType.Pet) num++;
		foreach (MysteryRewardLevel item in mystery.FreeTier)
		{
			foreach (MysteryReward reward in item.Rewards)
			{
				if (reward.Type == MysteryRewardType.Decoration)
				{
					num++;
				}
			}
		}
		foreach (MysteryRewardLevel item in mystery.SilverTier)
		{
			foreach (MysteryReward reward in item.Rewards)
			{
				if (reward.Type == MysteryRewardType.Decoration)
				{
					num++;
				}
			}
		}
		foreach (MysteryRewardLevel item2 in mystery.GoldTier)
		{
			foreach (MysteryReward reward2 in item2.Rewards)
			{
				if (reward2.Type == MysteryRewardType.Decoration)
				{
					num++;
				}
			}
		}
		return num;
	}

	/// <summary>
	/// Returns decoration slot IDs ordered by reward tier position (Silver then Gold).
	/// E.g., ["SP_Pickleball2025_Decoration_Slot33", "SP_Pickleball2025_Decoration_Slot34", ...]
	/// </summary>
	internal static List<string> GetOrderedDecorationSlotIds(MysteryEvent mystery)
	{
		var result = new List<string>();
		foreach (var level in mystery.SilverTier)
			foreach (var reward in level.Rewards)
				if (reward.Type == MysteryRewardType.Decoration && !string.IsNullOrEmpty(reward.DecorationId))
					result.Add(reward.DecorationId);
		foreach (var level in mystery.GoldTier)
			foreach (var reward in level.Rewards)
				if (reward.Type == MysteryRewardType.Decoration && !string.IsNullOrEmpty(reward.DecorationId))
					result.Add(reward.DecorationId);
		return result;
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

	public static string GenerateEventPageWithDialogues(MysteryEvent mystery, string? rewardVariant, DialogueService? dialogueService)
	{
		StringBuilder stringBuilder = new StringBuilder();
		string text = mystery.EventItemName ?? "Unknown";
		string value = FormatStartDate(mystery.StartDate);
		bool flag = mystery.MysteryType == MysteryType.Pet;
		string suggestedPageTitle = mystery.WikiStatus.SuggestedPageTitle;
		string value2 = ((suggestedPageTitle != null && suggestedPageTitle != mystery.Name) ? mystery.Name : "{{PAGENAME}}");
		string text2 = StripParenthetical(text);
		bool flag2 = text2 != text;
		StringBuilder stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder3 = stringBuilder2;
		StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(25, 1, stringBuilder2);
		handler.AppendLiteral("{{#vardefine:EventItem|");
		handler.AppendFormatted(text);
		handler.AppendLiteral("}}");
		stringBuilder3.Append(ref handler);
		stringBuilder.Append(flag2 ? ("{{#vardefine:EventItemDisplayName|" + text2 + "}}") : "{{#vardefine:EventItemDisplayName|{{#var:EventItem}}}}");
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder4 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(32, 1, stringBuilder2);
		handler.AppendLiteral("{{#vardefine:EventDisplayName|");
		handler.AppendFormatted(value2);
		handler.AppendLiteral("}}");
		stringBuilder4.AppendLine(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder5 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(36, 1, stringBuilder2);
		handler.AppendLiteral("{{Mystery Pass/Intro|startingDate=");
		handler.AppendFormatted(value);
		handler.AppendLiteral("}}");
		stringBuilder5.AppendLine(ref handler);
		stringBuilder.AppendLine();
		stringBuilder.AppendLine("== Event Mechanics == ");
		stringBuilder.AppendLine("{{Mystery Pass/Event Mechanics}}");
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
			// Fallback for pet: {{PAGENAME}} variant (caller should provide rewardVariant from GetNextVariantNameAsync)
			string value4 = FormatPetDisplayName(mystery.PetName);
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder8 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(40, 1, stringBuilder2);
			handler.AppendLiteral("{{Mystery Pass/Rewards/{{PAGENAME}}|pet=");
			handler.AppendFormatted(value4);
			handler.AppendLiteral("}}");
			stringBuilder8.AppendLine(ref handler);
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
			stringBuilder.AppendLine("|-| Getting Event Item L4 =");
			stringBuilder.AppendLine();
			if (flag)
			{
				string value5 = ((!string.IsNullOrEmpty(mystery.PetName)) ? FormatPetDisplayName(mystery.PetName) : "Pet");
				stringBuilder2 = stringBuilder;
				StringBuilder stringBuilder9 = stringBuilder2;
				handler = new StringBuilder.AppendInterpolatedStringHandler(14, 1, stringBuilder2);
				handler.AppendLiteral("|-| Getting ");
				handler.AppendFormatted(value5);
				handler.AppendLiteral(" =");
				stringBuilder9.AppendLine(ref handler);
				stringBuilder.AppendLine();
				stringBuilder.AppendLine("|-| Decoration Level 1 =");
				stringBuilder.AppendLine();
				stringBuilder.AppendLine("|-| Decoration Level 2 =");
				stringBuilder.AppendLine();
			}
			else
			{
				for (int i = 1; i <= 5; i++)
				{
					stringBuilder2 = stringBuilder;
					StringBuilder stringBuilder10 = stringBuilder2;
					handler = new StringBuilder.AppendInterpolatedStringHandler(23, 1, stringBuilder2);
					handler.AppendLiteral("|-| Decoration Level ");
					handler.AppendFormatted(i);
					handler.AppendLiteral(" =");
					stringBuilder10.AppendLine(ref handler);
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
		bool flag = mystery.MysteryType == MysteryType.Pet;
		string wikiImageName = mystery.WikiImageName;
		stringBuilder.AppendLine("<gallery>");
		StringBuilder stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder3 = stringBuilder2;
		StringBuilder.AppendInterpolatedStringHandler handler = new StringBuilder.AppendInterpolatedStringHandler(24, 1, stringBuilder2);
		handler.AppendFormatted(wikiImageName);
		handler.AppendLiteral(" Wallpaper.png|Wallpaper");
		stringBuilder3.AppendLine(ref handler);
		stringBuilder2 = stringBuilder;
		StringBuilder stringBuilder4 = stringBuilder2;
		handler = new StringBuilder.AppendInterpolatedStringHandler(16, 1, stringBuilder2);
		handler.AppendFormatted(wikiImageName);
		handler.AppendLiteral(" Badge.png|Badge");
		stringBuilder4.AppendLine(ref handler);
		if (flag)
		{
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder5 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(30, 1, stringBuilder2);
			handler.AppendFormatted(wikiImageName);
			handler.AppendLiteral(" Decoration 1.png|Decoration 1");
			stringBuilder5.AppendLine(ref handler);
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder6 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(30, 1, stringBuilder2);
			handler.AppendFormatted(wikiImageName);
			handler.AppendLiteral(" Decoration 2.png|Decoration 2");
			stringBuilder6.AppendLine(ref handler);
			if (!string.IsNullOrEmpty(mystery.PetName))
			{
				stringBuilder2 = stringBuilder;
				StringBuilder stringBuilder7 = stringBuilder2;
				handler = new StringBuilder.AppendInterpolatedStringHandler(6, 3, stringBuilder2);
				handler.AppendFormatted(wikiImageName);
				handler.AppendLiteral(" ");
				handler.AppendFormatted(mystery.PetName);
				handler.AppendLiteral(".png|");
				handler.AppendFormatted(mystery.PetName);
				stringBuilder7.AppendLine(ref handler);
			}
		}
		else
		{
			for (int i = 1; i <= 5; i++)
			{
				stringBuilder2 = stringBuilder;
				StringBuilder stringBuilder8 = stringBuilder2;
				handler = new StringBuilder.AppendInterpolatedStringHandler(28, 3, stringBuilder2);
				handler.AppendFormatted(wikiImageName);
				handler.AppendLiteral(" Decoration ");
				handler.AppendFormatted(i);
				handler.AppendLiteral(".png|Decoration ");
				handler.AppendFormatted(i);
				stringBuilder8.AppendLine(ref handler);
			}
		}
		if (!string.IsNullOrEmpty(mystery.EventItemName))
		{
			stringBuilder2 = stringBuilder;
			StringBuilder stringBuilder9 = stringBuilder2;
			handler = new StringBuilder.AppendInterpolatedStringHandler(26, 1, stringBuilder2);
			handler.AppendFormatted(mystery.EventItemName);
			handler.AppendLiteral(" Event Item.png|Event Item");
			stringBuilder9.AppendLine(ref handler);
		}
		stringBuilder.AppendLine("</gallery>");
		return stringBuilder.ToString();
	}

	public static async Task<string> UpdateMainPageAsync(string username, string password, string mysteryName, string pageTitle, DateTime? startDate)
	{
		string mainPageTitle = "Merge Mansion Wiki";
		string wikiContent = await FetchPageContentAsync(mainPageTitle);
		if (string.IsNullOrEmpty(wikiContent))
		{
			throw new Exception("Could not fetch main wiki page content.");
		}
		// Always find Mystery Events section first, then restrict year-row searches to it
		string headerPattern = "! colspan = 2 \\| Latest \\[\\[Mystery Events\\]\\]";
		Match headerMatch = Regex.Match(wikiContent, headerPattern);
		if (!headerMatch.Success)
		{
			throw new Exception("Could not find 'Latest Mystery Events' header.");
		}
		int sectionOffset = headerMatch.Index;
		// Limit sectionContent to just this section (stop before the next "! colspan = 2 |" header)
		string afterHeader = wikiContent.Substring(sectionOffset + headerMatch.Length);
		Match nextHeaderMatch = Regex.Match(afterHeader, "! colspan = 2 \\|");
		string sectionContent = nextHeaderMatch.Success
			? wikiContent.Substring(sectionOffset, headerMatch.Length + nextHeaderMatch.Index)
			: wikiContent.Substring(sectionOffset);
		// Already listed check scoped to Mystery Events section only
		if (sectionContent.Contains(mysteryName, StringComparison.OrdinalIgnoreCase)
			|| (pageTitle != mysteryName && sectionContent.Contains(pageTitle, StringComparison.OrdinalIgnoreCase)))
		{
			return "Mystery already listed on main page.";
		}
		string year = startDate?.Year.ToString() ?? DateTime.Now.Year.ToString();
		string newTemplate = (pageTitle != mysteryName)
			? $"{{{{Item/Group|{pageTitle}|displayName={mysteryName}}}}}"
			: ("{{Item/Group|" + mysteryName + "}}");
		string yearPattern = "\\| '''(" + year + ")''':?\\s*\\n\\| ([^\\n]+)";
		Match yearMatch = Regex.Match(sectionContent, yearPattern);
		if (yearMatch.Success)
		{
			string existingItems = yearMatch.Groups[2].Value;
			string updatedItems = existingItems.TrimEnd() + " • " + newTemplate;
			int absStart = sectionOffset + yearMatch.Groups[2].Index;
			int absEnd = absStart + yearMatch.Groups[2].Length;
			string updatedPage = wikiContent.Substring(0, absStart) + updatedItems + wikiContent.Substring(absEnd);
			return await PublishPageAsync(username, password, mainPageTitle, updatedPage, "Add " + mysteryName + " to Latest Mystery Events (via MergeMansionWikiTools)");
		}
		// Pattern starting at |- to find correct chronological insert position (years ascending: older first)
		string yearBlockPattern = "\\|-[^\\n]*\\n\\| '''(\\d{4})''':?[^\\n]*\\n\\| [^\\n]+";
		MatchCollection allYearMatches = Regex.Matches(sectionContent, yearBlockPattern);
		if (allYearMatches.Count == 0)
		{
			throw new Exception("Could not find any year rows in Mystery Events section.");
		}
		int newYearInt = int.Parse(year);
		// Find the first block whose year is greater than the new year → insert before it
		Match? insertBeforeBlock = null;
		for (int i = 0; i < allYearMatches.Count; i++)
		{
			if (int.Parse(allYearMatches[i].Groups[1].Value) > newYearInt)
			{
				insertBeforeBlock = allYearMatches[i];
				break;
			}
		}
		int insertPos;
		string newYearRow;
		if (insertBeforeBlock != null)
		{
			insertPos = sectionOffset + insertBeforeBlock.Index;
			newYearRow = "|-\n| '''" + year + "''':\n| " + newTemplate + "\n";
		}
		else
		{
			Match lastBlock = allYearMatches[allYearMatches.Count - 1];
			insertPos = sectionOffset + lastBlock.Index + lastBlock.Length;
			newYearRow = "\n|-\n| '''" + year + "''':\n| " + newTemplate;
		}
		string updatedPage2 = wikiContent.Substring(0, insertPos) + newYearRow + wikiContent.Substring(insertPos);
		// Match rowspan regardless of quotes, optional style attr, or other attributes between number and pipe
	string rowspanPattern = "(! rowspan\\s*=\\s*\"?)(\\d+)(\"?[^|]*\\|\\s*{{Item/Group\\|Events)";
		Match rowspanMatch = Regex.Match(updatedPage2, rowspanPattern);
		if (rowspanMatch.Success)
		{
			int oldSpan = int.Parse(rowspanMatch.Groups[2].Value);
			updatedPage2 = updatedPage2.Substring(0, rowspanMatch.Groups[2].Index)
				+ (oldSpan + 1).ToString()
				+ updatedPage2.Substring(rowspanMatch.Groups[2].Index + rowspanMatch.Groups[2].Length);
		}
		return await PublishPageAsync(username, password, mainPageTitle, updatedPage2, "Add " + mysteryName + " to Latest Mystery Events (via MergeMansionWikiTools)");
	}

	public static async Task<string> UpdateMysteryTableAsync(string username, string password, MysteryEvent mystery)
	{
		string moduleTitle = "Module:Datatable/Various";
		string wikiContent = await FetchPageContentAsync(moduleTitle);
		if (string.IsNullOrEmpty(wikiContent))
		{
			throw new Exception("Could not fetch Module:Datatable/Various content.");
		}
		string pageTitle = mystery.WikiStatus.SuggestedPageTitle ?? mystery.Name;
		if (wikiContent.Contains("\"" + mystery.Name + "\"", StringComparison.OrdinalIgnoreCase) || wikiContent.Contains("\"" + pageTitle + "\"", StringComparison.OrdinalIgnoreCase))
		{
			return "Mystery already in Module:Datatable/Various.";
		}
		var (newIndex, updatedContent) = InsertMysteryIntoModule(wikiContent, mystery);
		if (updatedContent == null)
		{
			throw new Exception("Could not find p.mysteries in Module:Datatable/Various.");
		}
		// Compact indices to prevent ipairs gaps
		updatedContent = CompactMysteryIndices(updatedContent) ?? updatedContent;
		return await PublishPageAsync(username, password, moduleTitle, updatedContent, $"Add {mystery.Name} [#{newIndex}] to p.mysteries (via MergeMansionWikiTools)");
	}

	/// <summary>
	/// Re-indexes p.mysteries entries to close any gaps (e.g. [26], [28] → [26], [27]).
	/// Required because Lua ipairs() stops at the first gap, breaking navigation arrows.
	/// </summary>
	internal static string? CompactMysteryIndices(string wikiContent)
	{
		var regex = new Regex(@"p\.mysteries\s*=\s*\{");
		var match = regex.Match(wikiContent);
		if (!match.Success) return null;

		// Find the end of p.mysteries section (next p.xxx = assignment)
		var nextSection = Regex.Match(wikiContent[(match.Index + 12)..], @"\np\.\w+\s*=");
		int sectionEnd = nextSection.Success ? match.Index + 12 + nextSection.Index : wikiContent.Length;

		// Find all [N] = { entries ONLY within p.mysteries section
		var entryPattern = new Regex(@"\[(\d+)\](\s*=\s*\{)");
		var entries = new List<(Match Match, int OldIndex)>();
		foreach (Match m in entryPattern.Matches(wikiContent))
		{
			if (m.Index > match.Index && m.Index < sectionEnd)
				entries.Add((m, int.Parse(m.Groups[1].Value)));
		}

		if (entries.Count == 0) return null;

		// Check if indices are already compact (1, 2, 3, ...)
		entries.Sort((a, b) => a.OldIndex.CompareTo(b.OldIndex));
		bool hasGaps = false;
		for (int i = 0; i < entries.Count; i++)
		{
			if (entries[i].OldIndex != i + 1)
			{
				hasGaps = true;
				break;
			}
		}
		if (!hasGaps) return wikiContent; // already compact

		// Renumber in reverse order (highest index first to avoid offset shifts)
		var sb = new StringBuilder(wikiContent);
		for (int i = entries.Count - 1; i >= 0; i--)
		{
			int newIdx = i + 1;
			var entry = entries[i];
			if (entry.OldIndex != newIdx)
			{
				string oldText = $"[{entry.OldIndex}]";
				string newText = $"[{newIdx}]";
				sb.Remove(entry.Match.Index, oldText.Length);
				sb.Insert(entry.Match.Index, newText);
			}
		}

		return sb.ToString();
	}

	/// <summary>
	/// Fetches Module:Datatable/Various, compacts mystery indices, and publishes if changed.
	/// </summary>
	public static async Task<string> ReindexMysteryTableAsync(string username, string password)
	{
		string moduleTitle = "Module:Datatable/Various";
		string wikiContent = await FetchPageContentAsync(moduleTitle);
		if (string.IsNullOrEmpty(wikiContent))
			throw new Exception("Could not fetch Module:Datatable/Various content.");

		string? compacted = CompactMysteryIndices(wikiContent);
		if (compacted == null || compacted == wikiContent)
			return "No gaps found — indices are already compact.";

		return await PublishPageAsync(username, password, moduleTitle, compacted,
			"Reindex p.mysteries — close index gaps for ipairs (via MergeMansionWikiTools)");
	}

	internal static (int newIndex, string? updatedContent) InsertMysteryIntoModule(string wikiContent, MysteryEvent mystery)
	{
		string text = mystery.WikiStatus.SuggestedPageTitle ?? mystery.Name;
		bool flag = text != mystery.Name;
		string text2 = mystery.StartDate?.ToString("dd.MM.yyyy") ?? "";
		int num = mystery.StartDate?.Year ?? DateTime.Now.Year;
		Regex regex = new Regex("p\\.mysteries\\s*=\\s*\\{");
		Match match = regex.Match(wikiContent);
		if (!match.Success)
		{
			return (newIndex: 0, updatedContent: null);
		}
		Regex regex2 = new Regex("(?<=\\[)(\\d+)(?=\\]\\s*=\\s*\\{)");
		string text3 = $"-- {num}";
		int num2 = wikiContent.IndexOf(text3, StringComparison.Ordinal);
		int newIndex;
		int num8;
		string text4;
		int num4;
		if (num2 >= 0)
		{
			int num3 = num2 + text3.Length;
			text4 = wikiContent;
			num4 = num3;
			string text5 = text4.Substring(num4, text4.Length - num4);
			Match match2 = Regex.Match(text5, "^\\s*--\\s*\\d{4}", RegexOptions.Multiline);
			string input = text5[..(match2.Success ? match2.Index : text5.Length)];
			Regex regex3 = new Regex("\\[(\\d+)\\]\\s*=\\s*\\{[^}]*?startDate\\s*=\\s*\"(\\d{2})\\.(\\d{2})\\.(\\d{4})\"");
			List<(int, DateTime)> list = (from Match m in regex3.Matches(input)
				select (idx: int.Parse(m.Groups[1].Value), date: new DateTime(int.Parse(m.Groups[4].Value), int.Parse(m.Groups[3].Value), int.Parse(m.Groups[2].Value))) into e
				orderby e.date descending
				select e).ToList();
			DateTime dateTime = mystery.StartDate ?? DateTime.MinValue;
			if (list.Count == 0)
			{
				Match match3 = regex2.Match(text5);
				if (match3.Success)
				{
					newIndex = int.Parse(match3.Groups[1].Value) + 1;
				}
				else
				{
					int num5 = 0;
					foreach (Match item in regex2.Matches(wikiContent))
					{
						if (int.TryParse(item.Groups[1].Value, out var result) && result > num5)
						{
							num5 = result;
						}
					}
					newIndex = num5 + 1;
				}
			}
			else
			{
				int? num6 = null;
				foreach (var item2 in list)
				{
					if (item2.Item2 < dateTime)
					{
						num6 = item2.Item1;
						break;
					}
				}
				if (!num6.HasValue)
				{
					newIndex = list.Last().Item1;
				}
				else
				{
					newIndex = num6.Value + 1;
				}
			}
			int num7 = wikiContent.IndexOf('\n', num2);
			num8 = ((num7 >= 0) ? (num7 + 1) : wikiContent.Length);
		}
		else
		{
			MatchCollection matchCollection = Regex.Matches(wikiContent, "--\\s*(\\d{4})");
			int num9 = -1;
			foreach (Match item3 in matchCollection)
			{
				if (int.TryParse(item3.Groups[1].Value, out var result2) && result2 < num)
				{
					int num10 = wikiContent.LastIndexOf('\n', item3.Index) + 1;
					num9 = num10;
					break;
				}
			}
			if (num9 >= 0)
			{
				string input2 = wikiContent.Substring(0, num9);
				MatchCollection matchCollection2 = regex2.Matches(input2);
				if (matchCollection2.Count > 0)
				{
					newIndex = int.Parse(matchCollection2[matchCollection2.Count - 1].Groups[1].Value) + 1;
				}
				else
				{
					int num11 = 0;
					foreach (Match item4 in regex2.Matches(wikiContent))
					{
						if (int.TryParse(item4.Groups[1].Value, out var result3) && result3 > num11)
						{
							num11 = result3;
						}
					}
					newIndex = num11 + 1;
				}
				string text6 = "\t" + text3 + "\n";
				string text7 = wikiContent.Substring(0, num9);
				text4 = wikiContent;
				num4 = num9;
				wikiContent = text7 + text6 + text4.Substring(num4, text4.Length - num4);
				num8 = num9 + text6.Length;
			}
			else
			{
				int num12 = 0;
				foreach (Match item5 in regex2.Matches(wikiContent))
				{
					if (int.TryParse(item5.Groups[1].Value, out var result4) && result4 > num12)
					{
						num12 = result4;
					}
				}
				newIndex = num12 + 1;
				int num13 = match.Index + match.Length;
				string text8 = "\n\t" + text3 + "\n";
				string text9 = wikiContent.Substring(0, num13);
				text4 = wikiContent;
				num4 = num13;
				wikiContent = text9 + text8 + text4.Substring(num4, text4.Length - num4);
				num8 = num13 + text8.Length;
			}
		}
		List<(Match, int)> list2 = (from Match m in regex2.Matches(wikiContent)
			select (match: m, idx: int.Parse(m.Groups[1].Value)) into e
			where e.idx >= newIndex
			orderby e.idx descending
			select e).ToList();
		StringBuilder stringBuilder = new StringBuilder(wikiContent);
		foreach (var (match8, num14) in list2)
		{
			stringBuilder.Remove(match8.Index, match8.Length);
			stringBuilder.Insert(match8.Index, (num14 + 1).ToString());
		}
		string text10 = stringBuilder.ToString();
		int num15 = text10.IndexOf(text3, StringComparison.Ordinal);
		if (num15 >= 0)
		{
			int num16 = text10.IndexOf('\n', num15);
			num8 = ((num16 >= 0) ? (num16 + 1) : text10.Length);
			text4 = text10;
			num4 = num15 + text3.Length;
			string text11 = text4.Substring(num4, text4.Length - num4);
			Match match9 = Regex.Match(text11, "^\\s*--\\s*\\d{4}", RegexOptions.Multiline);
			string input3 = text11[..(match9.Success ? match9.Index : text11.Length)];
			Regex regex4 = new Regex($"^\\t?\\s*\\[{newIndex + 1}\\]\\s*=\\s*\\{{[^}}]*\\}},?", RegexOptions.Multiline);
			Match match10 = regex4.Match(input3);
			if (match10.Success)
			{
				int startIndex = num15 + text3.Length + match10.Index + match10.Length;
				int num17 = text10.IndexOf('\n', startIndex);
				num8 = ((num17 >= 0) ? (num17 + 1) : text10.Length);
			}
		}
		string text12 = $"\t[{newIndex}] = {{ name = \"{text}\"";
		if (flag)
		{
			text12 = text12 + ", displayName = \"" + mystery.Name + "\"";
		}
		if (!string.IsNullOrEmpty(text2))
		{
			text12 = text12 + ", startDate = \"" + text2 + "\"";
		}
		text12 += " },";
		string text13 = text10.Substring(0, num8);
		string text14 = text12;
		text4 = text10;
		num4 = num8;
		text10 = text13 + text14 + "\n" + text4.Substring(num4, text4.Length - num4);
		return (newIndex: newIndex, updatedContent: text10);
	}

	public static async Task<string> UpdateMysteryPageTableAsync(string username, string password, MysteryEvent mystery)
	{
		string templateTitle = "Template:Events/Mystery Events";
		string wikiContent = await FetchPageContentAsync(templateTitle);
		if (string.IsNullOrEmpty(wikiContent))
		{
			throw new Exception("Could not fetch Template:Events/Mystery Events content.");
		}
		string suggestedTitle = mystery.WikiStatus.SuggestedPageTitle ?? mystery.Name;
		bool hasDisplayName = suggestedTitle != mystery.Name;
		if (wikiContent.Contains(suggestedTitle, StringComparison.OrdinalIgnoreCase))
		{
			return "Mystery already listed in mystery table.";
		}
		string itemName = mystery.EventItemName ?? "Unknown";
		string startDateStr = (mystery.StartDate.HasValue ? FormatDateNoYear(mystery.StartDate.Value) : "Unknown");
		string endDateStr = (mystery.EndDate.HasValue ? FormatDateNoYear(mystery.EndDate.Value) : "");
		string durationStr = (mystery.DurationDays.HasValue ? $"{mystery.DurationDays.Value} d" : "21 d");
		string year = mystery.StartDate?.Year.ToString() ?? "????";
		string iconPart = (hasDisplayName ? $"{{{{Item/Icon|{suggestedTitle}|displayName={mystery.Name}}}}}" : ("{{Item/Icon|" + suggestedTitle + "}}"));
		string linkPart = (hasDisplayName ? $"[[{suggestedTitle}|{mystery.Name}]]" : ("[[" + suggestedTitle + "]]"));
		string newRow = "|-\n| 8\n| " + iconPart + "\n| " + linkPart + "\n| {{Item/Group|" + itemName + "|4}}\n| " + durationStr + "\n! " + year + "\n| " + startDateStr + "\n| " + endDateStr + "\n";
		int insertPos = FindChronologicalInsertPosition(wikiContent, mystery.StartDate);
		string text = wikiContent.Substring(0, insertPos);
		string text2 = wikiContent;
		int num = insertPos;
		string updatedPage = text + newRow + text2.Substring(num, text2.Length - num);
		return await PublishPageAsync(username, password, templateTitle, updatedPage, "Add " + mystery.Name + " to mystery table (via MergeMansionWikiTools)");
	}

	private static int FindChronologicalInsertPosition(string wikiContent, DateTime? startDate)
	{
		int num = wikiContent.LastIndexOf("|}", StringComparison.Ordinal);
		if (num < 0)
		{
			throw new Exception("Could not find table end in mystery events template.");
		}
		if (!startDate.HasValue)
		{
			return num;
		}
		Regex regex = new Regex("\\n\\|-\\s*\\n");
		MatchCollection matchCollection = regex.Matches(wikiContent);
		int result = num;
		for (int i = 0; i < matchCollection.Count; i++)
		{
			int num2 = matchCollection[i].Index + matchCollection[i].Length;
			int num3 = ((i + 1 < matchCollection.Count) ? matchCollection[i + 1].Index : num);
			int num4 = num2;
			string text = wikiContent.Substring(num4, num3 - num4);
			string text2 = text.Split('\n')[0];
			if (text2.TrimStart().StartsWith("!"))
			{
				continue;
			}
			Match match = Regex.Match(text, "^!\\s*(\\d{4})", RegexOptions.Multiline);
			if (!match.Success)
			{
				continue;
			}
			int year = int.Parse(match.Groups[1].Value);
			int num5 = text.IndexOf('\n', match.Index);
			if (num5 >= 0)
			{
				string text3 = text;
				num4 = num5 + 1;
				string dateStr = text3.Substring(num4, text3.Length - num4).Split('\n').FirstOrDefault()?.TrimStart('|').Trim();
				DateTime? dateTime = ParseWikiDate(dateStr, year);
				if (dateTime.HasValue && dateTime.Value > startDate.Value)
				{
					return matchCollection[i].Index + 1;
				}
				result = ((i + 1 < matchCollection.Count) ? (matchCollection[i + 1].Index + 1) : num);
			}
		}
		return result;
	}

	private static DateTime? ParseWikiDate(string? dateStr, int year)
	{
		if (string.IsNullOrWhiteSpace(dateStr))
		{
			return null;
		}
		string s = Regex.Replace(dateStr.Trim(), "(\\d+)(st|nd|rd|th)\\b", "$1");
		if (DateTime.TryParseExact(s, new string[2] { "MMMM d", "MMMM dd" }, CultureInfo.InvariantCulture, DateTimeStyles.None, out var result))
		{
			return new DateTime(year, result.Month, result.Day);
		}
		return null;
	}

	public static string FormatFileName(string name, int level, bool suppressLevel = false)
	{
		StringBuilder stringBuilder = new StringBuilder();
		bool flag = false;
		foreach (char c in name)
		{
			if (flag && char.IsLetter(c))
			{
				stringBuilder.Append(char.ToUpper(c));
				flag = false;
			}
			else
			{
				stringBuilder.Append(c);
			}
			if (c == ' ')
			{
				flag = true;
			}
		}
		string text = stringBuilder.ToString();
		text = text.Replace("'", "")
			.Replace(":", "")
			.Replace("!", "")
			.Replace("?", "")
			.Replace("/", "")
			.Replace("&", "And");
		text = Regex.Replace(text, "\\s+", "");
		if (!suppressLevel)
		{
			text += level.ToString("D2");
		}
		return text + ".png";
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
			return false;
		}
		try
		{
			string json = File.ReadAllText(path);
			JsonDocument jsonDocument = JsonDocument.Parse(json);
			if (!jsonDocument.RootElement.TryGetProperty("sprites", out var value))
			{
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
			string text = null;
			int num = 0;
			int num2 = 0;
			int num3 = 0;
			int num4 = 0;
			foreach (JsonElement item2 in value.EnumerateArray())
			{
				string text2 = item2.GetProperty("name").GetString() ?? "";
				string text3 = item2.GetProperty("textureName").GetString() ?? "";
				if (!text3.StartsWith("sactx-"))
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
			if (list.Count == 0)
			{
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
				orderby slotOrder.TryGetValue(s.Item1, out var value4) ? value4 : ExtractSlotNumber(s.Item1)
				select s).ToList();
			HashSet<string> seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
			source = source.Where(s => seenNames.Add(s.Item1)).ToList();
			Dictionary<string, BitmapSource> dictionary = new Dictionary<string, BitmapSource>(StringComparer.OrdinalIgnoreCase);
			foreach (var item5 in source)
			{
				string text7 = FormatFileName(mysteryName + "Decoration", decoNum);
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
					decoNum++;
					continue;
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
					using (DrawingContext drawingContext = drawingVisual.RenderOpen())
					{
						drawingContext.DrawImage(croppedBitmap, new Rect((double)(num10 - num8) / 2.0, (double)(num10 - num9) / 2.0, num8, num9));
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
				if (File.Exists(text13))
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

	private static List<(BitmapSource Bmp, byte[] PngData, bool IsIcon)> AutoOrderByWikiMatch(List<(BitmapSource Bmp, byte[] PngData, bool IsIcon)> tiles, string mysteryName, int expectedCount, bool isPet)
	{
		if (tiles.Count <= 1)
		{
			return tiles;
		}
		try
		{
			HttpClient httpClient = new HttpClient();
			(BitmapSource, byte[], bool)?[] array = new(BitmapSource, byte[], bool)?[tiles.Count];
			HashSet<int> hashSet = new HashSet<int>();
			int num = 0;
			for (int i = ((!isPet) ? 1 : 0); i <= tiles.Count + ((!isPet) ? 1 : 0) - 1; i++)
			{
				string stringToEscape = FormatFileName(mysteryName + "Decoration", i);
				string requestUri = "https://merge-mansion.fandom.com/api.php?action=query&titles=File:" + Uri.EscapeDataString(stringToEscape) + "&prop=imageinfo&iiprop=url&format=json";
				string text = null;
				try
				{
					string result = httpClient.GetStringAsync(requestUri).GetAwaiter().GetResult();
					JsonDocument jsonDocument = JsonDocument.Parse(result);
					foreach (JsonProperty item2 in jsonDocument.RootElement.GetProperty("query").GetProperty("pages").EnumerateObject())
					{
						if (item2.Value.TryGetProperty("imageinfo", out var value) && value.GetArrayLength() > 0)
						{
							text = value[0].GetProperty("url").GetString();
						}
					}
				}
				catch
				{
					continue;
				}
				if (text == null)
				{
					continue;
				}
				byte[] result2;
				try
				{
					string text2 = (text.Contains('?') ? "&" : "?");
					result2 = httpClient.GetByteArrayAsync(text + text2 + "format=original").GetAwaiter().GetResult();
				}
				catch
				{
					continue;
				}
				BitmapSource bitmapSource;
				try
				{
					MemoryStream bitmapStream = new MemoryStream(result2);
					BitmapDecoder bitmapDecoder = BitmapDecoder.Create(bitmapStream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
					bitmapSource = bitmapDecoder.Frames[0];
					if (bitmapSource.Format != PixelFormats.Bgra32)
					{
						bitmapSource = new FormatConvertedBitmap(bitmapSource, PixelFormats.Bgra32, null, 0.0);
					}
				}
				catch
				{
					continue;
				}
				int num2 = -1;
				double num3 = 0.0;
				for (int j = 0; j < tiles.Count; j++)
				{
					if (hashSet.Contains(j))
					{
						continue;
					}
					BitmapSource item = tiles[j].Bmp;
					if (item.PixelWidth != bitmapSource.PixelWidth || item.PixelHeight != bitmapSource.PixelHeight)
					{
						continue;
					}
					int pixelWidth = item.PixelWidth;
					int pixelHeight = item.PixelHeight;
					byte[] array2 = new byte[pixelWidth * pixelHeight * 4];
					byte[] array3 = new byte[pixelWidth * pixelHeight * 4];
					BitmapSource bitmapSource2 = ((item.Format == PixelFormats.Bgra32) ? item : new FormatConvertedBitmap(item, PixelFormats.Bgra32, null, 0.0));
					bitmapSource2.CopyPixels(array2, pixelWidth * 4, 0);
					bitmapSource.CopyPixels(array3, pixelWidth * 4, 0);
					int num4 = 0;
					int num5 = 0;
					int num6 = Math.Max(1, pixelWidth * pixelHeight / 200);
					for (int k = 0; k < pixelWidth * pixelHeight; k += num6)
					{
						num5++;
						int num7 = k * 4;
						if (Math.Abs(array2[num7] - array3[num7]) < 20 && Math.Abs(array2[num7 + 1] - array3[num7 + 1]) < 20 && Math.Abs(array2[num7 + 2] - array3[num7 + 2]) < 20)
						{
							num4++;
						}
					}
					double num8 = ((num5 > 0) ? ((double)num4 / (double)num5) : 0.0);
					if (num8 > num3)
					{
						num3 = num8;
						num2 = j;
					}
				}
				if (num2 >= 0 && num3 > 0.7)
				{
					int num9 = i - ((!isPet) ? 1 : 0);
					if (num9 >= 0 && num9 < array.Length)
					{
						array[num9] = tiles[num2];
						hashSet.Add(num2);
						num++;
					}
				}
			}
			if (num == 0)
			{
				return tiles;
			}
			int l = 0;
			for (int m = 0; m < array.Length; m++)
			{
				if (!array[m].HasValue)
				{
					for (; l < tiles.Count && hashSet.Contains(l); l++)
					{
					}
					if (l < tiles.Count)
					{
						array[m] = tiles[l];
						l++;
					}
				}
			}
			AppLogger.Info($"AutoOrderByWikiMatch: matched {num}/{tiles.Count} decorations");
			return (from t in array
				where t.HasValue
				select t.Value).ToList();
		}
		catch (Exception ex)
		{
			AppLogger.Warn("AutoOrderByWikiMatch failed: " + ex.Message);
			return tiles;
		}
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
		string[] array = ((!isPet) ? new string[3]
		{
			"ProgressionPopupArt_" + progressionEventId + "*.png",
			"Popup_Progression_Art_" + progressionEventId + "*.png",
			"Popup_Header_" + progressionEventId + "*.png"
		} : new string[9]
		{
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
		List<string> list3 = new List<string>
		{
			"MainHubBadgeArt_" + progressionEventId + "*.png",
			"MainHub_Badge_" + progressionEventId + "*.png"
		};
		if (text3 != progressionEventId)
		{
			list3.Add("MainHubBadgeArt_" + text3 + "*.png");
			list3.Add("MainHub_Badge_" + text3 + "*.png");
		}
		foreach (string item6 in list3)
		{
			string[] files = Directory.GetFiles(exportDir, item6);
			foreach (string sourcePath2 in files)
			{
				string wikiFilename2 = FormatFileName(mysteryName, 1);
				string text8 = CopyToProcessed(sourcePath2, wikiFilename2, text5);
				list.Add(new DetectedDecorationFile
				{
					SourcePath = text8,
					WikiFilename = wikiFilename2,
					Category = "Badge",
					OptimizedSize = CheckOptMarker(text8)
				});
			}
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

	public static async Task<List<MysteryUpdateStep>> PreviewWikiUpdatesAsync(MysteryEvent mystery)
	{
		List<MysteryUpdateStep> steps = new List<MysteryUpdateStep>();
		// Ensure disambiguation is resolved before any checks (same logic as CheckAllMysteryStatusAsync phase 1.5)
		if ((mystery.WikiStatus.SuggestedPageTitle == null || mystery.WikiStatus.SuggestedPageTitle == mystery.Name) && mystery.StartDate.HasValue)
		{
			string yearTitle = $"{mystery.Name} (Mystery {mystery.StartDate.Value.Year})";
			Dictionary<string, bool> yearExist = await CheckPagesExistAsync(new[] { yearTitle });
			if (yearExist.GetValueOrDefault(yearTitle, false))
			{
				mystery.WikiStatus.SuggestedPageTitle = yearTitle;
			}
		}
		string pageTitle = mystery.WikiStatus.SuggestedPageTitle ?? mystery.Name;
		string wikiBase = "https://merge-mansion.fandom.com/api.php".Replace("/api.php", "") + "/wiki/";
		string mainContent = await FetchPageContentAsync("Merge Mansion Wiki");
		int mainYear = mystery.StartDate?.Year ?? DateTime.Now.Year;
		string mainTemplate = ((pageTitle != mystery.Name) ? $"{{{{Item/Group|{pageTitle}|displayName={mystery.Name}}}}}" : ("{{Item/Group|" + mystery.Name + "}}"));
		// Scope all checks to Mystery Events section only
		var mainSectionMatch = Regex.Match(mainContent ?? "", "! colspan = 2 \\| Latest \\[\\[Mystery Events\\]\\]");
		string mainSectionStr = "";
		if (mainSectionMatch.Success)
		{
			string afterMainHeader = mainContent.Substring(mainSectionMatch.Index + mainSectionMatch.Length);
			Match nextMainHeader = Regex.Match(afterMainHeader, "! colspan = 2 \\|");
			mainSectionStr = nextMainHeader.Success
				? mainContent.Substring(mainSectionMatch.Index, mainSectionMatch.Length + nextMainHeader.Index)
				: mainContent.Substring(mainSectionMatch.Index);
		}
		bool mainExists = mainSectionStr.Contains(mystery.Name, StringComparison.OrdinalIgnoreCase)
			|| (pageTitle != mystery.Name && mainSectionStr.Contains(pageTitle, StringComparison.OrdinalIgnoreCase));
		bool yearRowExists = mainSectionStr.Contains($"'''{mainYear}'''", StringComparison.Ordinal);
		string mainDetail;
		string mainPreview;
		if (mainExists)
		{
			mainDetail = "Already listed (no change)";
			mainPreview = null;
		}
		else if (yearRowExists)
		{
			mainDetail = $"Add to {mainYear} row";
			mainPreview = "... • " + mainTemplate;
		}
		else
		{
			mainDetail = $"Create {mainYear} row (+ rowspan update)";
			mainPreview = $"| '''{mainYear}''':\n| {mainTemplate}";
		}
		// Age check: only this year and last year can be added to main page
		const bool ageCheckEnabled = true;
		int currentYear = DateTime.Now.Year;
		bool isTooOld = ageCheckEnabled && mystery.StartDate.HasValue && mystery.StartDate.Value.Year < currentYear - 1;
		string mainDisabledReason = isTooOld
			? $"Only this year's ({currentYear}) and last year's ({currentYear - 1}) mysteries can be added to the main page"
			: null;
		steps.Add(new MysteryUpdateStep
		{
			Title = "Update Merge Mansion Wiki",
			Detail = (mainExists ? "Already listed (no change)" : mainDetail),
			IsNoChange = mainExists,
			IsEnabled = !mainExists && mainDisabledReason == null,
			DisabledReason = mainDisabledReason,
			WikiUrl = wikiBase + "Merge_Mansion_Wiki",
			Icon = "\ud83c\udfe0",
			ContentPreview = mainPreview
		});
		bool tableExists = (await FetchPageContentAsync("Template:Events/Mystery Events"))?.Contains(pageTitle, StringComparison.OrdinalIgnoreCase) ?? false;
		string itemName = mystery.EventItemName ?? "Unknown";
		string year = mystery.StartDate?.Year.ToString() ?? "????";
		string startDateStr = (mystery.StartDate.HasValue ? FormatDateNoYear(mystery.StartDate.Value) : "Unknown");
		string endDateStr = (mystery.EndDate.HasValue ? FormatDateNoYear(mystery.EndDate.Value) : "");
		string durationStr = (mystery.DurationDays.HasValue ? $"{mystery.DurationDays.Value} d" : "21 d");
		bool hasDisplayName = pageTitle != mystery.Name;
		string previewIcon = (hasDisplayName ? $"{{{{Item/Icon|{pageTitle}|displayName={mystery.Name}}}}}" : ("{{Item/Icon|" + pageTitle + "}}"));
		string previewLink = (hasDisplayName ? $"[[{pageTitle}|{mystery.Name}]]" : ("[[" + pageTitle + "]]"));
		string tableRowPreview = "|-\n| 8\n| " + previewIcon + "\n| " + previewLink + "\n| {{Item/Group|" + itemName + "|4}}\n| " + durationStr + "\n! " + year + "\n| " + startDateStr + "\n| " + endDateStr;
		steps.Add(new MysteryUpdateStep
		{
			Title = "Update Mystery page (table)",
			Detail = (tableExists ? "Already listed (no change)" : $"Add row: {mystery.Name}, {itemName}, {durationStr}"),
			IsNoChange = tableExists,
			IsEnabled = !tableExists,
			WikiUrl = wikiBase + "Template:Events/Mystery_Events",
			Icon = "\ud83d\udccb",
			ContentPreview = (tableExists ? null : tableRowPreview)
		});
		string moduleContent = await FetchPageContentAsync("Module:Datatable/Various");
		bool moduleExists = moduleContent != null && (moduleContent.Contains("\"" + mystery.Name + "\"", StringComparison.OrdinalIgnoreCase) || moduleContent.Contains("\"" + pageTitle + "\"", StringComparison.OrdinalIgnoreCase));
		string luaPreview = null;
		string ctxAbove = null;
		string ctxBelow = null;
		int previewIndex = 0;
		if (!moduleExists && moduleContent != null)
		{
			(int, string) tuple = InsertMysteryIntoModule(moduleContent, mystery);
			int newIdx = tuple.Item1;
			string insertedContent = tuple.Item2;
			previewIndex = newIdx;
			string luaDateStr = mystery.StartDate?.ToString("dd.MM.yyyy") ?? "";
			bool needsDN = pageTitle != mystery.Name;
			string luaEntry = $"[{newIdx}] = {{ name = \"{pageTitle}\"";
			if (needsDN)
			{
				luaEntry = luaEntry + ", displayName = \"" + mystery.Name + "\"";
			}
			if (!string.IsNullOrEmpty(luaDateStr))
			{
				luaEntry = luaEntry + ", startDate = \"" + luaDateStr + "\"";
			}
			luaEntry += " },";
			int mysteryYear = mystery.StartDate?.Year ?? DateTime.Now.Year;
			string yearCommentStr = $"-- {mysteryYear}";
			bool isNewYear = !moduleContent.Contains(yearCommentStr, StringComparison.Ordinal);
			luaPreview = (isNewYear ? (yearCommentStr + "\n" + luaEntry) : luaEntry);
			if (insertedContent != null)
			{
				Regex newEntryPattern = new Regex($"^\\t?\\s*\\[{newIdx}\\]\\s*=\\s*\\{{.*{Regex.Escape(pageTitle)}.*\\}},?", RegexOptions.Multiline);
				Match newMatch = newEntryPattern.Match(insertedContent);
				if (newMatch.Success)
				{
					string before = insertedContent.Substring(0, newMatch.Index).TrimEnd('\n');
					int lastNewline = before.LastIndexOf('\n');
					string text;
					int num;
					if (lastNewline >= 0)
					{
						text = before;
						num = lastNewline + 1;
						string aboveLine = text.Substring(num, text.Length - num).Trim();
						if (aboveLine.StartsWith("["))
						{
							ctxAbove = aboveLine;
						}
						else if (aboveLine.StartsWith("--"))
						{
							if (isNewYear)
							{
								string beforeComment = before.Substring(0, lastNewline).TrimEnd('\n');
								int prevNewline = beforeComment.LastIndexOf('\n');
								if (prevNewline >= 0)
								{
									text = beforeComment;
									num = prevNewline + 1;
									string entryAboveComment = text.Substring(num, text.Length - num).Trim();
									if (entryAboveComment.StartsWith("["))
									{
										ctxAbove = entryAboveComment;
									}
								}
							}
							else
							{
								List<string> ctxParts = new List<string>();
								string beforeComment2 = before.Substring(0, lastNewline).TrimEnd('\n');
								int prevNewline2 = beforeComment2.LastIndexOf('\n');
								if (prevNewline2 >= 0)
								{
									text = beforeComment2;
									num = prevNewline2 + 1;
									string entryAboveComment2 = text.Substring(num, text.Length - num).Trim();
									if (entryAboveComment2.StartsWith("["))
									{
										ctxParts.Add(entryAboveComment2);
									}
								}
								ctxParts.Add(aboveLine);
								ctxAbove = string.Join("\n", ctxParts);
							}
						}
					}
					int afterStart = newMatch.Index + newMatch.Length;
					text = insertedContent;
					num = afterStart;
					IEnumerable<string> afterLines = from l in text.Substring(num, text.Length - num).Split('\n')
						select l.Trim() into l
						where !string.IsNullOrEmpty(l)
						select l;
					List<string> belowParts = new List<string>();
					foreach (string line in afterLines)
					{
						if (line.StartsWith("--"))
						{
							belowParts.Add(line);
							continue;
						}
						if (line.StartsWith("["))
						{
							belowParts.Add(line);
						}
						break;
					}
					if (belowParts.Count > 0)
					{
						ctxBelow = string.Join("\n", belowParts);
					}
				}
			}
		}
		steps.Add(new MysteryUpdateStep
		{
			Title = "Update Module:Datatable/Various",
			Detail = (moduleExists ? "Already listed (no change)" : $"Add entry #{previewIndex} to p.mysteries"),
			IsNoChange = moduleExists,
			IsEnabled = !moduleExists,
			WikiUrl = wikiBase + "Module:Datatable/Various",
			Icon = "\ud83d\udcdd",
			ContentPreview = luaPreview,
			ContextAbove = ctxAbove,
			ContextBelow = ctxBelow
		});
		return steps;
	}
}
