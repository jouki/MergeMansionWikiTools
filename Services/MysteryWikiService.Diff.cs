using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using MergeMansionWikiTools.Models;

namespace MergeMansionWikiTools.Services;

public static partial class MysteryWikiService
{
	private record TabSection(string Header, string[] Content);

	private record TabberRegions(string[] Before, List<TabSection> Tabs, string[] After);

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
		// forceRefresh: computing a NEW unique variant name must not rely on a stale TTL cache
		Dictionary<string, string> templates = await FetchRewardTemplatesAsync(forceRefresh: true);
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
		int expectedCols = mystery.IsV2 ? 5 : 4;

		// Parse ALL data rows from wiki template (regular levels + bonus/PremiumLevel)
		List<List<string>> wikiRows = ParseTemplateRows(templateContent)
			.Where(r => r.Count >= 2).ToList(); // at least level + 1 column
		if (wikiRows.Count == 0)
			return false;

		// Generate our template and parse its rows
		string generated = GenerateRewardTemplate(mystery, null);
		List<List<string>> genRows = ParseTemplateRows(generated)
			.Where(r => r.Count >= 2).ToList();

		// Row count must match
		if (wikiRows.Count != genRows.Count)
			return false;

		// Cell-by-cell comparison of ALL rows (regular + bonus)
		for (int i = 0; i < wikiRows.Count; i++)
		{
			int cols = Math.Min(wikiRows[i].Count, genRows[i].Count);
			for (int j = 0; j < cols; j++)
			{
				if (NormalizeCell(wikiRows[i][j]) != NormalizeCell(genRows[i][j]))
					return false;
			}
			// If one row has more columns than the other → mismatch
			if (wikiRows[i].Count != genRows[i].Count)
				return false;
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
}
