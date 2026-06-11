using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MergeMansionWikiTools.Models;

namespace MergeMansionWikiTools.Services;

public static partial class MysteryWikiService
{
	private const string BaseApiUrl = "https://merge-mansion.fandom.com/api.php";

	private static readonly HttpClient Http = HttpClients.WikiApi;

	private static readonly string StatusCachePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "mystery_wiki_status_cache.json");

	private static readonly JsonSerializerOptions JsonOpts = new JsonSerializerOptions
	{
		WriteIndented = true
	};

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
					// Strip legacy "Season Pass - " prefix that may have been stored before the rename refactor.
					mystery.WikiStatus.SuggestedPageTitle = MysteryService.StripSeasonPassPrefix(value.SuggestedPageTitle);
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

	public static async Task<Dictionary<string, bool>> CheckPagesExistAsync(IEnumerable<string> titles, CancellationToken ct = default)
	{
		Dictionary<string, bool> result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
		List<string> titleList = titles.ToList();
		// Run 50-title batches in parallel, throttled to 2 concurrent requests (Fandom rate-limit safe).
		using SemaphoreSlim throttle = new SemaphoreSlim(2);
		List<Task<Dictionary<string, bool>>> batchTasks = new List<Task<Dictionary<string, bool>>>();
		for (int i = 0; i < titleList.Count; i += 50)
		{
			List<string> batch = titleList.Skip(i).Take(50).ToList();
			batchTasks.Add(CheckPagesExistBatchAsync(batch, throttle, ct));
		}
		// Merge per-batch results in original batch order (same overwrite semantics as the old sequential loop).
		foreach (Dictionary<string, bool> batchResult in await Task.WhenAll(batchTasks))
		{
			foreach (KeyValuePair<string, bool> kv in batchResult)
			{
				result[kv.Key] = kv.Value;
			}
		}
		return result;
	}

	private static async Task<Dictionary<string, bool>> CheckPagesExistBatchAsync(IReadOnlyList<string> batch, SemaphoreSlim throttle, CancellationToken ct = default)
	{
		await throttle.WaitAsync(ct);
		try
		{
			Dictionary<string, bool> result = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
			string joined = string.Join("|", batch);
			string url = "https://merge-mansion.fandom.com/api.php?action=query&titles=" + Uri.EscapeDataString(joined) + "&format=json";
			AppLogger.Info("CheckPagesExist batch: " + url);
			JsonDocument doc = JsonDocument.Parse(await Http.GetStringAsync(url, ct));
			foreach (JsonProperty page in doc.RootElement.GetProperty("query").GetProperty("pages").EnumerateObject())
			{
				JsonElement value = page.Value.GetProperty("title");
				string title = value.GetString() ?? "";
				bool missing = page.Value.TryGetProperty("missing", out value);
				result[title] = !missing;
				result[title.Replace(' ', '_')] = !missing;
				result[title.Replace('_', ' ')] = !missing;
			}
			return result;
		}
		finally
		{
			throttle.Release();
		}
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

	public static async Task<Dictionary<string, string>> FetchPagesContentAsync(IEnumerable<string> titles, CancellationToken ct = default)
	{
		Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
		List<string> titleList = titles.ToList();
		for (int i = 0; i < titleList.Count; i += 50)
		{
			ct.ThrowIfCancellationRequested();
			IEnumerable<string> batch = titleList.Skip(i).Take(50);
			string joined = string.Join("|", batch);
			string url = "https://merge-mansion.fandom.com/api.php?action=query&titles=" + Uri.EscapeDataString(joined) + "&prop=revisions&rvprop=content&rvslots=main&format=json";
			JsonDocument doc = JsonDocument.Parse(await Http.GetStringAsync(url, ct));
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

	public static async Task CheckAllMysteryStatusAsync(IReadOnlyList<MysteryEvent> mysteries, DataService? ds, DialogueService? dialogueService = null, IProgress<string>? progress = null, CancellationToken ct = default)
	{
		using (AppLogger.Timed($"CheckAllMysteryStatusAsync ({mysteries.Count} mysteries)"))
		{
			ct.ThrowIfCancellationRequested();
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
				ct.ThrowIfCancellationRequested();
				progress?.Report("Resolving page titles...");
				using (AppLogger.Timed($"WikiDisambiguationCheck ({needsWikiDisambig.Count} mysteries)"))
				{
					IEnumerable<string> yearTitles = needsWikiDisambig
						.Select(m => $"{m.Name} (Mystery {m.StartDate!.Value.Year})")
						.Distinct(StringComparer.OrdinalIgnoreCase);
					Dictionary<string, bool> yearExistMap = await CheckPagesExistAsync(yearTitles, ct);
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
			ct.ThrowIfCancellationRequested();
			try
			{
				progress?.Report("Fetching gallery templates...");
				var galleryTemplates = await FetchGalleryTemplatesAsync(forceRefresh: false, ct);
				foreach (var m in mysteries)
				{
					int decoCount = CountDecorations(m);
					bool isPetM = m.MysteryType == MysteryType.Pet;
					var galleryVariant = FindMatchingGalleryVariant(decoCount, isPetM, galleryTemplates);
					m.WikiStatus.MatchingGalleryVariant = galleryVariant;
				}
				AppLogger.Info($"GalleryCheck: matched {mysteries.Count(m => m.WikiStatus.MatchingGalleryVariant != null)} of {mysteries.Count}");
			}
			catch (OperationCanceledException) { throw; }
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
				ct.ThrowIfCancellationRequested();
				progress?.Report("Checking page existence...");
				Dictionary<string, bool> existMap;
				using (AppLogger.Timed("CheckPagesExistAsync"))
				{
					existMap = await CheckPagesExistAsync(titleToMystery.Keys, ct);
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
				ct.ThrowIfCancellationRequested();
				progress?.Report("Fetching reward templates...");
				try
				{
					Dictionary<string, string> templates;
					using (AppLogger.Timed("FetchRewardTemplatesAsync"))
					{
						templates = await FetchRewardTemplatesAsync(forceRefresh: false, ct);
					}
					foreach (MysteryEvent m3 in needsTemplateCheck)
					{
						var (matches, variant) = CompareWithTemplates(m3, templates);
						m3.WikiStatus.RewardTemplateMatches = matches;
						m3.WikiStatus.RewardContentMatches = matches;
						m3.WikiStatus.MatchingVariant = variant;
					}
				}
				catch (OperationCanceledException)
				{
					throw;
				}
				catch
				{
				}
			}
			List<MysteryEvent> needsPageContentCheck = mysteries.Where((MysteryEvent mysteryEvent) => mysteryEvent.WikiStatus.EventPageExists == true && mysteryEvent.WikiStatus.EventPageState != WikiCheckState.Match).ToList();
			AppLogger.Info($"EventPageContentCheck: {needsPageContentCheck.Count} mysteries need check");
			if (needsPageContentCheck.Count > 0)
			{
				ct.ThrowIfCancellationRequested();
				progress?.Report("Checking page content...");
				try
				{
					List<string> pageTitles = needsPageContentCheck.Select((MysteryEvent mysteryEvent) => mysteryEvent.WikiStatus.SuggestedPageTitle ?? mysteryEvent.Name).Distinct<string>(StringComparer.OrdinalIgnoreCase).ToList();
					Dictionary<string, string> pageContents;
					using (AppLogger.Timed("FetchEventPagesContentAsync"))
					{
						pageContents = await FetchPagesContentAsync(pageTitles, ct);
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
							string resolvedContent = await FetchPageContentAsync(resolvedTitle, ct);
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
				catch (OperationCanceledException)
				{
					throw;
				}
				catch
				{
				}
			}
			List<MysteryEvent> needsItemContentCheck = mysteries.Where((MysteryEvent mysteryEvent) => mysteryEvent.WikiStatus.EventItemPageExists == true && mysteryEvent.WikiStatus.EventItemPageState != WikiCheckState.Match && !string.IsNullOrEmpty(mysteryEvent.EventItemName)).ToList();
			AppLogger.Info($"ItemPageContentCheck: {needsItemContentCheck.Count} mysteries need check");
			if (needsItemContentCheck.Count > 0)
			{
				ct.ThrowIfCancellationRequested();
				progress?.Report("Checking item pages...");
				try
				{
					List<string> itemTitles = needsItemContentCheck.Select((MysteryEvent mysteryEvent) => mysteryEvent.EventItemName).Distinct<string>(StringComparer.OrdinalIgnoreCase).ToList();
					Dictionary<string, string> itemContents;
					using (AppLogger.Timed("FetchItemPagesContentAsync"))
					{
						itemContents = await FetchPagesContentAsync(itemTitles, ct);
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
				catch (OperationCanceledException)
				{
					throw;
				}
				catch
				{
				}
			}
			ct.ThrowIfCancellationRequested();
			try
			{
				progress?.Report("Checking images...");
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
					Dictionary<string, bool> imgExistMap = await CheckPagesExistAsync(imageFileNames, ct);
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
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex)
			{
				AppLogger.Warn("Images check failed: " + ex.Message);
			}
			ct.ThrowIfCancellationRequested();
			try
			{
				progress?.Report("Checking wiki listings...");
				using (AppLogger.Timed("WikiListingCheck"))
				{
					// Fetch all three listing pages in parallel
					Task<string?> mainPageTask = FetchPageContentAsync("Merge Mansion Wiki", ct);
					Task<string?> mysteryTableTask = FetchPageContentAsync("Template:Events/Mystery Events", ct);
					Task<string?> moduleTask = FetchPageContentAsync("Module:Datatable/Various", ct);
					await Task.WhenAll(mainPageTask, mysteryTableTask, moduleTask);
					string mainPageContent = await mainPageTask;
					string mysteryTableContent = await mysteryTableTask;
					string moduleContent = await moduleTask;
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
						bool tableListed = mysteryTableContent != null && (
							mysteryTableContent.Contains($"[[{pt}]]", StringComparison.OrdinalIgnoreCase) ||
							mysteryTableContent.Contains($"[[{pt}|", StringComparison.OrdinalIgnoreCase) ||
							mysteryTableContent.Contains($"|{pt}|", StringComparison.OrdinalIgnoreCase) ||
							mysteryTableContent.Contains($"|{pt}}}", StringComparison.OrdinalIgnoreCase));
						// If listed, verify row values are correct — wrong values = not fully listed
						if (tableListed && mysteryTableContent != null)
						{
							string itemName = m8.EventItemName ?? "Unknown";
							string durationStr = m8.DurationDays.HasValue ? $"{m8.DurationDays.Value} d" : "";
							string yearStr = FormatYearColumn(m8);
							string startStr = m8.StartDate.HasValue ? FormatDateNoYear(m8.StartDate.Value) : "";
							string endStr = m8.EndDate.HasValue ? FormatDateNoYear(m8.EndDate.Value) : "";
							string expectedItem = "{{Item/Group|" + itemName + "|4}}";
							var rowDiffs = CheckMysteryTableRow(mysteryTableContent, pt, m8.Name,
								expectedItem, durationStr, yearStr, startStr, endStr);
							if (rowDiffs.Count > 0)
								tableListed = false;
						}
						m8.WikiStatus.WikiMysteryTableListed = tableListed;
						m8.WikiStatus.WikiModuleListed = moduleContent != null && (moduleContent.Contains("\"" + m8.Name + "\"", StringComparison.OrdinalIgnoreCase) || moduleContent.Contains("\"" + pt + "\"", StringComparison.OrdinalIgnoreCase));
					}
				}
			}
			catch (OperationCanceledException)
			{
				throw;
			}
			catch (Exception ex2)
			{
				AppLogger.Warn("Wiki listing check failed: " + ex2.Message);
			}
			ct.ThrowIfCancellationRequested();
			await LoadManualConfirmFlagsAsync(mysteries, ct);
			ct.ThrowIfCancellationRequested();
			UpdateCache(mysteries, cache);
			SaveStatusCache(cache);
		}
	}

	public static async Task CheckSingleMysteryStatusAsync(MysteryEvent mystery, IReadOnlyList<MysteryEvent> allMysteries, DataService? ds, DialogueService? dialogueService = null, CancellationToken ct = default)
	{
		using (AppLogger.Timed($"CheckSingleMysteryStatusAsync ({mystery.Name})"))
		{
			ct.ThrowIfCancellationRequested();
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
				ct.ThrowIfCancellationRequested();
				string yearTitle = $"{mystery.Name} (Mystery {mystery.StartDate.Value.Year})";
				bool yearExists = await WikiMappingService.CheckPageExistsAsync(yearTitle);
				if (yearExists)
				{
					mystery.WikiStatus.SuggestedPageTitle = yearTitle;
					AppLogger.Info($"Wiki disambiguation: '{mystery.Name}' → '{yearTitle}'");
				}
			}

			// Gallery template detection
			ct.ThrowIfCancellationRequested();
			try
			{
				var galleryTemplates = await FetchGalleryTemplatesAsync(forceRefresh: false, ct);
				int decoCount = CountDecorations(mystery);
				bool isPetM = mystery.MysteryType == MysteryType.Pet;
				mystery.WikiStatus.MatchingGalleryVariant = FindMatchingGalleryVariant(decoCount, isPetM, galleryTemplates);
			}
			catch (OperationCanceledException) { throw; }
			catch (Exception ex) { AppLogger.Info($"GalleryCheck error: {ex.Message}"); }

			// Page existence
			ct.ThrowIfCancellationRequested();
			string eventTitle = mystery.WikiStatus.SuggestedPageTitle ?? mystery.Name;
			mystery.WikiStatus.EventPageExists = await WikiMappingService.CheckPageExistsAsync(eventTitle);
			if (!string.IsNullOrEmpty(mystery.EventItemName))
				mystery.WikiStatus.EventItemPageExists = await WikiMappingService.CheckPageExistsAsync(mystery.EventItemName);

			// Reward template check
			ct.ThrowIfCancellationRequested();
			try
			{
				var templates = await FetchRewardTemplatesAsync(forceRefresh: false, ct);
				var (matches, variant) = CompareWithTemplates(mystery, templates);
				mystery.WikiStatus.RewardTemplateMatches = matches;
				mystery.WikiStatus.RewardContentMatches = matches;
				mystery.WikiStatus.MatchingVariant = variant;
			}
			catch (OperationCanceledException) { throw; }
			catch { }

			// Event page content check
			if (mystery.WikiStatus.EventPageExists == true)
			{
				ct.ThrowIfCancellationRequested();
				try
				{
					string wikiContent = await FetchPageContentAsync(eventTitle, ct);
					if (wikiContent != null)
					{
						if (IsDisambiguationPage(wikiContent) && mystery.StartDate.HasValue)
						{
							string resolvedTitle = $"{mystery.Name} (Mystery {mystery.StartDate.Value.Year})";
							mystery.WikiStatus.SuggestedPageTitle = resolvedTitle;
							wikiContent = await FetchPageContentAsync(resolvedTitle, ct);
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
				catch (OperationCanceledException) { throw; }
				catch { }
			}

			// Event item page content check
			if (mystery.WikiStatus.EventItemPageExists == true && !string.IsNullOrEmpty(mystery.EventItemName))
			{
				ct.ThrowIfCancellationRequested();
				try
				{
					string wikiContent = await FetchPageContentAsync(mystery.EventItemName, ct);
					if (wikiContent != null)
						mystery.WikiStatus.EventItemPageContentMatches = CompareEventItemPageContent(mystery, wikiContent, ds);
				}
				catch (OperationCanceledException) { throw; }
				catch { }
			}

			// Images check
			ct.ThrowIfCancellationRequested();
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
				var imgExistMap = await CheckPagesExistAsync(imgFileNames, ct);
				mystery.WikiStatus.ImagesExistOnWiki = expectedImages.Count(f => imgExistMap.GetValueOrDefault("File:" + f, false));
			}
			catch (OperationCanceledException) { throw; }
			catch (Exception ex) { AppLogger.Warn("Images check failed: " + ex.Message); }

			// Wiki listing check
			ct.ThrowIfCancellationRequested();
			try
			{
				// Fetch all three listing pages in parallel
				Task<string?> mainPageTask = FetchPageContentAsync("Merge Mansion Wiki", ct);
				Task<string?> mysteryTableTask = FetchPageContentAsync("Template:Events/Mystery Events", ct);
				Task<string?> moduleTask = FetchPageContentAsync("Module:Datatable/Various", ct);
				await Task.WhenAll(mainPageTask, mysteryTableTask, moduleTask);
				string mainPageContent = await mainPageTask;
				string mysteryTableContent = await mysteryTableTask;
				string moduleContent = await moduleTask;
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
				bool singleTableListed = mysteryTableContent != null && (
					mysteryTableContent.Contains($"[[{pt}]]", StringComparison.OrdinalIgnoreCase) ||
					mysteryTableContent.Contains($"[[{pt}|", StringComparison.OrdinalIgnoreCase) ||
					mysteryTableContent.Contains($"|{pt}|", StringComparison.OrdinalIgnoreCase) ||
					mysteryTableContent.Contains($"|{pt}}}", StringComparison.OrdinalIgnoreCase));
				if (singleTableListed && mysteryTableContent != null)
				{
					string itemName = mystery.EventItemName ?? "Unknown";
					string durationStr = mystery.DurationDays.HasValue ? $"{mystery.DurationDays.Value} d" : "";
					string yearStr = FormatYearColumn(mystery);
					string startStr = mystery.StartDate.HasValue ? FormatDateNoYear(mystery.StartDate.Value) : "";
					string endStr = mystery.EndDate.HasValue ? FormatDateNoYear(mystery.EndDate.Value) : "";
					string expectedItem = "{{Item/Group|" + itemName + "|4}}";
					var rowDiffs = CheckMysteryTableRow(mysteryTableContent, pt, mystery.Name,
						expectedItem, durationStr, yearStr, startStr, endStr);
					if (rowDiffs.Count > 0)
						singleTableListed = false;
				}
				mystery.WikiStatus.WikiMysteryTableListed = singleTableListed;
				mystery.WikiStatus.WikiModuleListed = moduleContent != null && (moduleContent.Contains("\"" + mystery.Name + "\"", StringComparison.OrdinalIgnoreCase) || moduleContent.Contains("\"" + pt + "\"", StringComparison.OrdinalIgnoreCase));
			}
			catch (OperationCanceledException) { throw; }
			catch (Exception ex) { AppLogger.Warn("Wiki listing check failed: " + ex.Message); }

			ct.ThrowIfCancellationRequested();
			await LoadManualConfirmFlagsAsync(new[] { mystery }, ct);
			ct.ThrowIfCancellationRequested();
			UpdateCache(new[] { mystery }, cache);
			SaveStatusCache(cache);
		}
	}

	public static async Task<string?> FetchPageContentAsync(string title, CancellationToken ct = default)
	{
		string content;
		return (await FetchPagesContentAsync(new string[1] { title }, ct)).TryGetValue(title, out content) ? content : null;
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
}
