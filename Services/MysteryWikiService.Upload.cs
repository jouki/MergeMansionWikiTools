using System;
using System.Collections.Generic;
using System.Globalization;
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
	public static async Task LoadManualConfirmFlagsAsync(IReadOnlyList<MysteryEvent> mysteries, CancellationToken ct = default)
	{
		try
		{
			string content = await FetchPageContentAsync("Module:Datatable/Various", ct);
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
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
			throw;
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
			throw WikiMappingService.WikiEditException(error.GetProperty("info").GetString());
		}
		if (editDoc.RootElement.TryGetProperty("edit", out var edit))
		{
			InvalidateTemplateCachesFor(pageTitle);
			JsonElement r;
			string editResult = (edit.TryGetProperty("result", out r) ? r.GetString() : "?");
			return "Edit result: " + editResult;
		}
		return "Unknown response";
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
		string itemName = mystery.EventItemName ?? "Unknown";
		string startDateStr = (mystery.StartDate.HasValue ? FormatDateNoYear(mystery.StartDate.Value) : "Unknown");
		string endDateStr = (mystery.EndDate.HasValue ? FormatDateNoYear(mystery.EndDate.Value) : "");
		string durationStr = (mystery.DurationDays.HasValue ? $"{mystery.DurationDays.Value} d" : "21 d");
		string year = FormatYearColumn(mystery);
		string iconPart = (hasDisplayName ? $"{{{{Item/Icon|{suggestedTitle}|displayName={mystery.Name}}}}}" : ("{{Item/Icon|" + suggestedTitle + "}}"));
		string linkPart = (hasDisplayName ? $"[[{suggestedTitle}|{mystery.Name}]]" : ("[[" + suggestedTitle + "]]"));
		string expectedItemGroup = "{{Item/Group|" + itemName + "|4}}";

		if (wikiContent.Contains(suggestedTitle, StringComparison.OrdinalIgnoreCase))
		{
			// Row exists — check if values need fixing
			var diffs = CheckMysteryTableRow(wikiContent, suggestedTitle, mystery.Name,
				expectedItemGroup, durationStr, year, startDateStr, endDateStr);
			if (diffs.Count == 0)
				return "Mystery already listed in mystery table.";

			// Fix the existing row by replacing cell values
			string updatedContent = FixMysteryTableRow(wikiContent, suggestedTitle, mystery.Name,
				expectedItemGroup, durationStr, year, startDateStr, endDateStr);
			string fixedFields = string.Join(", ", diffs.Select(d => d.field));
			return await PublishPageAsync(username, password, templateTitle, updatedContent,
				$"Fix {mystery.Name} row: {fixedFields} (via MergeMansionWikiTools)");
		}

		string newRow = "|-\n| 8\n| " + iconPart + "\n| " + linkPart + "\n| " + expectedItemGroup + "\n| " + durationStr + "\n! " + year + "\n| " + startDateStr + "\n| " + endDateStr + "\n";
		int insertPos = FindChronologicalInsertPosition(wikiContent, mystery.StartDate);
		string updatedPage = wikiContent.Substring(0, insertPos) + newRow + wikiContent.Substring(insertPos);
		return await PublishPageAsync(username, password, templateTitle, updatedPage, "Add " + mystery.Name + " to mystery table (via MergeMansionWikiTools)");
	}

	/// <summary>
	/// Finds the table row block for a mystery by looking for the wiki link [[pageTitle...]].
	/// Returns (startIndex, endIndex) of the row block (from |- to next |- or |}).
	/// </summary>
	private static (int start, int end, List<string> lines)? FindMysteryTableRowBlock(
		string tableContent, string pageTitle, string mysteryName)
	{
		// Search for [[pageTitle]] or [[pageTitle| in the table
		string linkPattern1 = $"[[{pageTitle}]]";
		string linkPattern2 = $"[[{pageTitle}|";
		int linkIdx = tableContent.IndexOf(linkPattern1, StringComparison.OrdinalIgnoreCase);
		if (linkIdx < 0)
			linkIdx = tableContent.IndexOf(linkPattern2, StringComparison.OrdinalIgnoreCase);
		if (linkIdx < 0) return null;

		// Walk backward to find the |- that starts this row
		int rowStart = tableContent.LastIndexOf("\n|-", linkIdx, StringComparison.Ordinal);
		if (rowStart < 0) return null;
		rowStart++; // skip the leading \n

		// Walk forward to find the next |- or |} that ends this row
		int afterSep = tableContent.IndexOf('\n', rowStart) + 1; // skip the |- line itself
		int nextRowSep = tableContent.IndexOf("\n|-", afterSep, StringComparison.Ordinal);
		int tableEnd = tableContent.IndexOf("\n|}", afterSep, StringComparison.Ordinal);
		int rowEnd;
		if (nextRowSep >= 0 && (tableEnd < 0 || nextRowSep < tableEnd))
			rowEnd = nextRowSep + 1; // +1 to include the \n
		else if (tableEnd >= 0)
			rowEnd = tableEnd + 1;
		else
			return null;

		string rowBlock = tableContent[afterSep..rowEnd];
		var lines = rowBlock.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
		return (rowStart, rowEnd, lines);
	}

	/// <summary>
	/// Parses cell values from row lines (lines starting with | or !).
	/// Strips rowspan="N" | prefix if present.
	/// </summary>
	private static List<string> ParseRowCells(List<string> lines)
	{
		var cells = new List<string>();
		foreach (var line in lines)
		{
			var trimmed = line.TrimStart();
			if (!trimmed.StartsWith("|") && !trimmed.StartsWith("!")) continue;
			string cellContent = trimmed[1..].Trim();
			// Strip rowspan prefix: 'rowspan="2" | actual content'
			var rspanMatch = Regex.Match(cellContent, @"^rowspan=""\d+""\s*\|\s*(.*)$");
			if (rspanMatch.Success)
				cellContent = rspanMatch.Groups[1].Value.Trim();
			cells.Add(cellContent);
		}
		return cells;
	}

	/// <summary>
	/// Checks an existing mystery table row for correctness. Returns list of field diffs.
	/// </summary>
	private static List<(string field, string oldVal, string newVal)> CheckMysteryTableRow(
		string tableContent, string pageTitle, string mysteryName,
		string expectedItemGroup, string expectedDuration, string expectedYear,
		string expectedStartDate, string expectedEndDate)
	{
		var diffs = new List<(string field, string oldVal, string newVal)>();

		var rowInfo = FindMysteryTableRowBlock(tableContent, pageTitle, mysteryName);
		if (rowInfo == null) return diffs;

		var cells = ParseRowCells(rowInfo.Value.lines);
		// Expected: [0]=Level(8), [1]=Icon, [2]=Link, [3]=Item/Group, [4]=Duration, [5]=Year, [6]=Start, [7]=End
		if (cells.Count < 8) return diffs;

		string itemCell = cells[3];
		if (!itemCell.Equals(expectedItemGroup, StringComparison.OrdinalIgnoreCase))
			diffs.Add(("Event Item", itemCell, expectedItemGroup));

		string durCell = cells[4];
		if (!durCell.Equals(expectedDuration, StringComparison.OrdinalIgnoreCase))
			diffs.Add(("Duration", durCell, expectedDuration));

		string yearCell = cells[5];
		if (!yearCell.Equals(expectedYear, StringComparison.Ordinal))
			diffs.Add(("Year", yearCell, expectedYear));

		string startCell = cells[6];
		if (!startCell.Equals(expectedStartDate, StringComparison.OrdinalIgnoreCase))
			diffs.Add(("Start Date", startCell, expectedStartDate));

		string endCell = cells[7];
		if (!endCell.Equals(expectedEndDate, StringComparison.OrdinalIgnoreCase))
			diffs.Add(("Finish Date", endCell, expectedEndDate));

		return diffs;
	}

	/// <summary>
	/// Replaces cell values in an existing mystery table row with correct values.
	/// Rebuilds only cells 3-7 (Item, Duration, Year, Start, End), preserving the rest.
	/// </summary>
	private static string FixMysteryTableRow(string tableContent, string pageTitle, string mysteryName,
		string expectedItemGroup, string expectedDuration, string expectedYear,
		string expectedStartDate, string expectedEndDate)
	{
		var rowInfo = FindMysteryTableRowBlock(tableContent, pageTitle, mysteryName);
		if (rowInfo == null) return tableContent;

		var (rowStart, rowEnd, lines) = rowInfo.Value;

		// Find cell lines by index (lines starting with | or !)
		int cellIdx = 0;
		for (int i = 0; i < lines.Count; i++)
		{
			var trimmed = lines[i].TrimStart();
			if (!trimmed.StartsWith("|") && !trimmed.StartsWith("!")) continue;

			// Replace cells 3-7 (Item, Duration, Year, Start, End)
			switch (cellIdx)
			{
				case 3: lines[i] = "| " + expectedItemGroup; break;
				case 4: lines[i] = "| " + expectedDuration; break;
				case 5: lines[i] = "! " + expectedYear; break;
				case 6: lines[i] = "| " + expectedStartDate; break;
				case 7: lines[i] = "| " + expectedEndDate; break;
			}
			cellIdx++;
		}

		// Rebuild: |- separator + new cell lines
		string sepLine = tableContent[rowStart..tableContent.IndexOf('\n', rowStart)];
		string newRowBlock = sepLine + "\n" + string.Join("\n", lines) + "\n";
		return tableContent[..rowStart] + newRowBlock + tableContent[rowEnd..];
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
		string tableContent = await FetchPageContentAsync("Template:Events/Mystery Events");
		bool tableExists = tableContent?.Contains(pageTitle, StringComparison.OrdinalIgnoreCase) ?? false;
		string itemName = mystery.EventItemName ?? "Unknown";
		string year = FormatYearColumn(mystery);
		string startDateStr = (mystery.StartDate.HasValue ? FormatDateNoYear(mystery.StartDate.Value) : "Unknown");
		string endDateStr = (mystery.EndDate.HasValue ? FormatDateNoYear(mystery.EndDate.Value) : "");
		string durationStr = (mystery.DurationDays.HasValue ? $"{mystery.DurationDays.Value} d" : "21 d");
		bool hasDisplayName = pageTitle != mystery.Name;
		string previewIcon = (hasDisplayName ? $"{{{{Item/Icon|{pageTitle}|displayName={mystery.Name}}}}}" : ("{{Item/Icon|" + pageTitle + "}}"));
		string previewLink = (hasDisplayName ? $"[[{pageTitle}|{mystery.Name}]]" : ("[[" + pageTitle + "]]"));
		string expectedItemGroup = "{{Item/Group|" + itemName + "|4}}";
		string tableRowPreview = "|-\n| 8\n| " + previewIcon + "\n| " + previewLink + "\n| " + expectedItemGroup + "\n| " + durationStr + "\n! " + year + "\n| " + startDateStr + "\n| " + endDateStr;

		// If listed, check if row values are correct
		string? tableFixDetail = null;
		string? tableFixPreview = null;
		bool tableNeedsFix = false;
		if (tableExists && tableContent != null)
		{
			var diffs = CheckMysteryTableRow(tableContent, pageTitle, mystery.Name, expectedItemGroup, durationStr, year, startDateStr, endDateStr);
			if (diffs.Count > 0)
			{
				tableNeedsFix = true;
				tableFixDetail = "Fix row: " + string.Join(", ", diffs.Select(d => d.field));
				tableFixPreview = string.Join("\n", diffs.Select(d => $"  {d.field}: {d.oldVal} → {d.newVal}"));
			}
		}

		steps.Add(new MysteryUpdateStep
		{
			Title = "Update Mystery page (table)",
			Detail = tableNeedsFix ? tableFixDetail!
				: (tableExists ? "Already listed (no change)" : $"Add row: {mystery.Name}, {itemName}, {durationStr}"),
			IsNoChange = tableExists && !tableNeedsFix,
			IsEnabled = !tableExists || tableNeedsFix,
			WikiUrl = wikiBase + "Template:Events/Mystery_Events",
			Icon = "\ud83d\udccb",
			ContentPreview = tableNeedsFix ? tableFixPreview : (tableExists ? null : tableRowPreview)
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
