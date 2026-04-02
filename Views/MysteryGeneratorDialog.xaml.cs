using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Navigation;
using MergeMansionWikiTools.Models;
using MergeMansionWikiTools.Services;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace MergeMansionWikiTools.Views;

public enum MysteryGeneratorMode { EventPage, Rewards, EventItemPage, Images }

public partial class MysteryGeneratorDialog : FluentWindow
{
	private readonly MainWindow _main;

	private readonly MysteryEvent _mystery;

	private readonly MysteryItemMapping? _mapping;

	private readonly DialogueService? _dialogueService;

	private MysteryGeneratorMode _currentMode;

	private bool _suppressScrollSync;

	private string _fullOutput = "";

	private bool _isDiffMode;

	private List<DiffLine>? _currentDiffs;

	private readonly HashSet<int> _selectedRemovedIndices = new HashSet<int>();

	private string? _originalOutput;

	private readonly Dictionary<int, UIElement> _savedRightElements = new Dictionary<int, UIElement>();

	private string? _hypotheticalRewardVariant;

	private CancellationTokenSource? _diffCts;

	// Reward template comparison dropdown
	private Dictionary<string, string>? _rewardTemplatesCache;
	private bool _suppressCompareSelection;
	private string? _selectedCompareTemplateTitle; // full page title of template selected in Compare dropdown

	/// <summary>True when MatchingVariant is a mystery-named template (e.g. "Secrets of Serenity"),
	/// not a numeric index ("6") or Pet-prefixed. Named templates get updated instead of creating new.</summary>
	private bool IsNamedRewardVariant =>
		_mystery.WikiStatus.MatchingVariant != null
		&& !int.TryParse(_mystery.WikiStatus.MatchingVariant, out _)
		&& _mystery.WikiStatus.MatchingVariant != ""
		&& !_mystery.WikiStatus.MatchingVariant.StartsWith("Pet", StringComparison.OrdinalIgnoreCase);

	private static readonly Brush BrushAddedBg = new SolidColorBrush(Color.FromArgb(37, 48, 192, 48));

	private static readonly Brush BrushRemovedBg = new SolidColorBrush(Color.FromArgb(37, 208, 64, 64));

	private static readonly Brush BrushAddedFg = new SolidColorBrush(Color.FromRgb(64, 208, 64));

	private static readonly Brush BrushRemovedFg = new SolidColorBrush(Color.FromRgb(224, 80, 80));

	private bool _isDragging;

	private bool _dragIsDeselecting;

	private readonly Dictionary<int, int> _rowToDiffIndex = new Dictionary<int, int>();

	private readonly Dictionary<int, int> _diffIndexToRow = new Dictionary<int, int>();

	private readonly HashSet<int> _pairedRemovedIndices = new HashSet<int>();

	private static readonly Brush BrushSelectedBg = new SolidColorBrush(Color.FromArgb(53, 64, 128, 224));

	private static readonly Brush BrushSelectedFg = new SolidColorBrush(Color.FromRgb(112, 176, 240));

	private static readonly Brush BrushArrowBg = new SolidColorBrush(Color.FromArgb(64, 64, 128, 224));

	private static readonly Brush BrushArrowFg = new SolidColorBrush(Color.FromRgb(128, 192, byte.MaxValue));

	private static readonly Brush BrushInlineRemovedBg = new SolidColorBrush(Color.FromArgb(80, 208, 48, 48));

	private static readonly Brush BrushInlineAddedBg = new SolidColorBrush(Color.FromArgb(80, 48, 192, 48));

	private string? _lastWikiContent;

	private static readonly Brush BrushMergedBg = new SolidColorBrush(Color.FromArgb(37, 64, 128, 224));

	private static readonly Brush BrushMergedFg = new SolidColorBrush(Color.FromRgb(96, 160, 240));

	private readonly HashSet<int> _mergedRightRows = new HashSet<int>();

	private bool _isDraggingRight;

	private readonly Dictionary<int, int> _mergedRowToOutputIdx = new Dictionary<int, int>();

	private readonly HashSet<int> _revertRows = new HashSet<int>();

	/// <summary>Exact output line indices that were inserted/replaced by merge operations.</summary>
	private readonly HashSet<int> _mergedOutputLineIndices = new HashSet<int>();

	private static readonly Brush BrushModifiedBg = new SolidColorBrush(Color.FromArgb(32, 208, 160, 32));

	private static readonly Brush BrushModifiedFg = new SolidColorBrush(Color.FromRgb(208, 176, 64));


	public Action? OnStatusChanged { get; set; }

	public Func<string, Task>? OnRewardTemplateCreated { get; set; }

	public void ShowImagesTab()
	{
		tabMode.SelectedIndex = (int)MysteryGeneratorMode.Images;
	}

	public void ResetImagesForRefresh()
	{
		imagesControl.ResetForRefresh();
	}

	public MysteryGeneratorDialog(MainWindow main, MysteryEvent mystery, MysteryItemMapping? mapping, MysteryGeneratorMode initialMode, DialogueService? dialogueService = null)
	{
		_main = main;
		_mystery = mystery;
		_mapping = mapping;
		_dialogueService = dialogueService;
		_currentMode = initialMode;
		InitializeComponent();
		ApplicationThemeManager.Apply(this);
		SourceInitialized += (_, _) =>
		{
			var source = System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(this).Handle);
			source?.AddHook((IntPtr _, int msg, IntPtr wParam, IntPtr _, ref bool handled) =>
			{
				if (msg == 0x020E) MainWindow.HandleMouseHWheel(wParam, ref handled);
				return IntPtr.Zero;
			});
		};
		txtMysteryInfo.Text = mystery.Name;
		string value = mystery.StartDate?.ToString("MMM d, yyyy") ?? "Unknown";
		string value2 = ((mystery.MysteryType == MysteryType.Pet) ? "Pet" : "Standard");
		txtMysteryDetail.Text = $"{value2}  {value}  {mystery.FreeTier.Count} levels  Event Item: {mystery.EventItemName ?? "Unknown"}";
		tabMode.SelectedIndex = (int)initialMode;
		btnPublish.Visibility = ((!_main.Settings.WikiVerified) ? Visibility.Collapsed : Visibility.Visible);
		base.Loaded += async delegate
		{
			await PrecomputeHypotheticalVariantAsync();
			GenerateOutput();
		};
	}

	private async Task PrecomputeHypotheticalVariantAsync()
	{
		WikiCheckState rewardState = _mystery.WikiStatus.RewardTemplateState;
		if (rewardState == WikiCheckState.Match || rewardState == WikiCheckState.Confirmed)
		{
			return;
		}
		try
		{
			bool isPet = _mystery.MysteryType == MysteryType.Pet;
			_hypotheticalRewardVariant = await MysteryWikiService.GetNextVariantNameAsync(isPet);
		}
		catch
		{
		}
	}

	private void TabMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (base.IsLoaded)
		{
			_currentMode = (MysteryGeneratorMode)tabMode.SelectedIndex;
			GenerateOutput();
		}
	}

	private void GenerateOutput()
	{
		// Cancel any in-flight async diff/compare operations from previous tab
		_diffCts?.Cancel();
		_diffCts = new CancellationTokenSource();
		// Reset merge/selection state from previous tab
		_selectedRemovedIndices.Clear();
		_savedRightElements.Clear();
		_mergedRightRows.Clear();
		_mergedRowToOutputIdx.Clear();
		_revertRows.Clear();
		_mergedOutputLineIndices.Clear();
		_originalOutput = null;
		pnlOutput.Visibility = Visibility.Collapsed;
		pnlDiff.Visibility = Visibility.Collapsed;
		imagesControl.Visibility = Visibility.Collapsed;
		_isDiffMode = false;
		btnPublish.Content = _currentMode == MysteryGeneratorMode.Rewards
			? (IsNamedRewardVariant ? "Update Reward Template" : "Create New Reward Template")
			: "Publish to Wiki";
		// Show compare dropdown only in Rewards mode
		pnlCompareDropdown.Visibility = _currentMode == MysteryGeneratorMode.Rewards
			? Visibility.Visible : Visibility.Collapsed;
		if (_currentMode == MysteryGeneratorMode.Rewards)
		{
			// Always set _fullOutput for Rewards before any early return
			_fullOutput = MysteryWikiService.GenerateRewardTemplate(_mystery, _mapping);
			PopulateCompareDropdownAsync();
			// If populate auto-selected a confirmed variant, SelectionChanged will handle the diff view
			if (cmbCompareTemplate.SelectedIndex > 0)
				return;
		}
		if (_currentMode == MysteryGeneratorMode.Images)
		{
			imagesControl.Visibility = Visibility.Visible;
			imagesControl.OnStatusChanged = OnStatusChanged;
			imagesControl.Initialize(_main, _mystery);
			btnCopy.IsEnabled = false;
			btnCopy.Visibility = Visibility.Collapsed;
			btnPublish.Visibility = Visibility.Collapsed;
			return;
		}
		btnCopy.Visibility = Visibility.Visible;
		try
		{
			_fullOutput = _currentMode switch
			{
				MysteryGeneratorMode.Rewards => MysteryWikiService.GenerateRewardTemplate(_mystery, _mapping),
				MysteryGeneratorMode.EventPage => MysteryWikiService.GenerateEventPageWithDialogues(_mystery, GetEffectiveRewardVariant(), _dialogueService),
				MysteryGeneratorMode.EventItemPage => MysteryWikiService.GenerateEventItemPage(_mystery, _main.DataService, _main.WikiMapping),
				_ => "",
			};
			bool shouldLoadDiff = _currentMode switch
			{
				MysteryGeneratorMode.EventPage => _mystery.WikiStatus.EventPageExists == true,
				MysteryGeneratorMode.EventItemPage => _mystery.WikiStatus.EventItemPageExists == true,
				MysteryGeneratorMode.Rewards => _mystery.WikiStatus.RewardTemplateMatches == true || _mystery.WikiStatus.MatchingVariant != null,
				_ => false,
			};
			if (shouldLoadDiff)
			{
				UpdateConfirmButton();
				LoadDiffAsync(_diffCts.Token);
				return;
			}
			ShowPlainOutput();
			UpdateConfirmButton();
		}
		catch (Exception ex)
		{
			ShowPlainOutput();
			warningBar.Message = "Generation error: " + ex.Message;
			warningBar.Severity = InfoBarSeverity.Error;
			warningBar.IsOpen = true;
		}
	}

	private void ShowPlainOutput()
	{
		pnlOutput.Visibility = Visibility.Visible;
		txtOutput.Text = _fullOutput;
		txtOutput.Visibility = Visibility.Visible;
		txtOutputPlaceholder.Visibility = Visibility.Collapsed;
		btnCopy.IsEnabled = true;
		btnPublish.Visibility = ((!_main.Settings.WikiVerified) ? Visibility.Collapsed : Visibility.Visible);
		warningBar.IsOpen = false;
	}

	private async Task LoadDiffAsync(CancellationToken ct = default)
	{
		var modeAtStart = _currentMode;
		pnlOutput.Visibility = Visibility.Collapsed;
		pnlDiff.Visibility = Visibility.Visible;
		pnlDiffLoading.Visibility = Visibility.Visible;
		pnlLeft.Children.Clear();
		pnlRight.Children.Clear();
		pnlCenter.Children.Clear();
		scrollLeft.ScrollToVerticalOffset(0);
		scrollRight.ScrollToVerticalOffset(0);
		scrollCenter.ScrollToVerticalOffset(0);
		pnlLeft.UpdateLayout();
		pnlRight.UpdateLayout();
		try
		{
			MysteryDiffScope scope = modeAtStart switch
			{
				MysteryGeneratorMode.EventPage => MysteryDiffScope.EventPage,
				MysteryGeneratorMode.Rewards => MysteryDiffScope.Rewards,
				_ => MysteryDiffScope.EventItemPage,
			};
			string? effectiveVariant = (modeAtStart == MysteryGeneratorMode.EventPage) ? GetEffectiveRewardVariant() : null;
			(string? WikiContent, string GeneratedContent, List<DiffLine> Diffs, string PageTitle) tuple = await MysteryWikiService.ComputeDiffAsync(_mystery, scope, _main.DataService, _main.WikiMapping, _mapping, _dialogueService, effectiveVariant);
			if (ct.IsCancellationRequested || _currentMode != modeAtStart) return;
			var (wikiContent, _, _, _) = tuple;
			_ = tuple.GeneratedContent;
			List<DiffLine> diffs = tuple.Diffs;
			string diffPageTitle = tuple.PageTitle;
			pnlDiffLoading.Visibility = Visibility.Collapsed;
			if (wikiContent == null)
			{
				pnlDiff.Visibility = Visibility.Collapsed;
				SetDiffLeftLink(null);
				runDiffRightSuffix.Text = "";
				ShowPlainOutput();
				return;
			}
			SetDiffLeftLink(diffPageTitle);
			if (_currentMode == MysteryGeneratorMode.Rewards && _mystery.WikiStatus.RewardContentMatches != true && !string.IsNullOrEmpty(_hypotheticalRewardVariant))
			{
				string suffix = (string.IsNullOrEmpty(_hypotheticalRewardVariant) ? "" : ("/" + _hypotheticalRewardVariant));
				runDiffRightSuffix.Text = " - Template:Mystery Pass/Rewards" + suffix;
			}
			else
			{
				runDiffRightSuffix.Text = "";
			}
			_lastWikiContent = wikiContent;
			if (_originalOutput == null)
			{
				_originalOutput = _fullOutput;
			}
			bool allMatch = diffs.All((DiffLine d) => d.Type == DiffLineType.Match);
			// Update content match status so UpdateConfirmButton sees Mismatch (not Unknown)
			switch (_currentMode)
			{
				case MysteryGeneratorMode.EventPage:
					_mystery.WikiStatus.EventPageContentMatches = allMatch;
					break;
				case MysteryGeneratorMode.EventItemPage:
					_mystery.WikiStatus.EventItemPageContentMatches = allMatch;
					break;
				case MysteryGeneratorMode.Rewards:
					_mystery.WikiStatus.RewardContentMatches = allMatch;
					break;
			}
			_isDiffMode = true;
			BuildDiffView(diffs);
			btnCopy.IsEnabled = true;
			btnPublish.Visibility = ((!_main.Settings.WikiVerified) ? Visibility.Collapsed : Visibility.Visible);
			if (allMatch)
			{
				warningBar.Message = "Content matches wiki. No changes needed.";
				warningBar.Severity = InfoBarSeverity.Success;
				warningBar.IsOpen = true;
				btnPublish.Visibility = Visibility.Collapsed;
			}
			else
			{
				int added = diffs.Count((DiffLine d) => d.Type == DiffLineType.Added);
				int removed = diffs.Count((DiffLine d) => d.Type == DiffLineType.Removed);
				int modified = diffs.Count((DiffLine d) => d.Type == DiffLineType.Modified);
				List<string> parts = new List<string>();
				if (added > 0)
				{
					parts.Add($"{added} added");
				}
				if (removed > 0)
				{
					parts.Add($"{removed} removed");
				}
				if (modified > 0)
				{
					parts.Add($"{modified} modified");
				}
				warningBar.Message = string.Join("  ", parts);
				warningBar.Severity = InfoBarSeverity.Informational;
				warningBar.IsOpen = true;
				if (_currentMode == MysteryGeneratorMode.Rewards)
				{
					btnPublish.Content = IsNamedRewardVariant ? "Update Reward Template" : "Create New Reward Template";
				}
				else
				{
					btnPublish.Content = "Publish to Wiki";
				}
			}
			UpdateConfirmButton();
		}
		catch (Exception ex)
		{
			pnlDiffLoading.Visibility = Visibility.Collapsed;
			pnlDiff.Visibility = Visibility.Collapsed;
			ShowPlainOutput();
			warningBar.Message = "Diff failed: " + ex.Message;
			warningBar.Severity = InfoBarSeverity.Warning;
			warningBar.IsOpen = true;
		}
	}

	private void BuildDiffView(List<DiffLine> diffs)
	{
		pnlLeft.Children.Clear();
		pnlRight.Children.Clear();
		pnlCenter.Children.Clear();
		_currentDiffs = diffs;
		_selectedRemovedIndices.Clear();
		_savedRightElements.Clear();
		_rowToDiffIndex.Clear();
		_diffIndexToRow.Clear();
		_pairedRemovedIndices.Clear();
		int num = 0;
		int num2 = 0;
		while (num2 < diffs.Count)
		{
			DiffLine diffLine = diffs[num2];
			if (diffLine.Type == DiffLineType.Match)
			{
				pnlLeft.Children.Add(CreateDiffLine(diffLine.Text, DiffLineType.Match));
				pnlRight.Children.Add(CreateDiffLine(diffLine.Text, DiffLineType.Match));
				pnlCenter.Children.Add(CreateCenterSpacer());
				num++;
				num2++;
			}
			else if (diffLine.Type == DiffLineType.Removed && num2 + 1 < diffs.Count && diffs[num2 + 1].Type == DiffLineType.Added)
			{
				string text = diffLine.Text;
				string text2 = diffs[num2 + 1].Text;
				string text3 = Regex.Replace(text.Trim(), "\\s+", " ");
				string text4 = Regex.Replace(text2.Trim(), "\\s+", " ");
				if (text3 == text4)
				{
					pnlLeft.Children.Add(CreateModifiedLine(text));
					pnlRight.Children.Add(CreateModifiedLine(text2));
					pnlCenter.Children.Add(CreateCenterSpacer());
				}
				else
				{
					_rowToDiffIndex[num] = num2;
					_diffIndexToRow[num2] = num;
					_pairedRemovedIndices.Add(num2);
					Border element = CreateInlineDiffSelectable(text, text2, isLeft: true, num2);
					Border element2 = CreateInlineDiffLine(text2, text, isLeft: false);
					pnlLeft.Children.Add(element);
					pnlRight.Children.Add(element2);
					pnlCenter.Children.Add(CreateCenterSpacer());
				}
				num++;
				num2 += 2;
			}
			else if (diffLine.Type == DiffLineType.Removed)
			{
				_rowToDiffIndex[num] = num2;
				_diffIndexToRow[num2] = num;
				pnlLeft.Children.Add(CreateSelectableLine(diffLine.Text, num2));
				pnlRight.Children.Add(CreateDiffPlaceholder(DiffLineType.Removed));
				pnlCenter.Children.Add(CreateCenterSpacer());
				num++;
				num2++;
			}
			else if (diffLine.Type == DiffLineType.Modified)
			{
				string text5 = diffLine.OldText ?? "";
				string text6 = diffLine.Text;
				string text7 = Regex.Replace(text5.Trim(), "\\s+", " ");
				string text8 = Regex.Replace(text6.Trim(), "\\s+", " ");
				if (text7 == text8)
				{
					pnlLeft.Children.Add(CreateModifiedLine(text5));
					pnlRight.Children.Add(CreateModifiedLine(text6));
					pnlCenter.Children.Add(CreateCenterSpacer());
				}
				else
				{
					_rowToDiffIndex[num] = num2;
					_diffIndexToRow[num2] = num;
					_pairedRemovedIndices.Add(num2);
					Border element3 = CreateInlineDiffSelectable(text5, text6, isLeft: true, num2);
					Border element4 = CreateInlineDiffLine(text6, text5, isLeft: false);
					pnlLeft.Children.Add(element3);
					pnlRight.Children.Add(element4);
					pnlCenter.Children.Add(CreateCenterSpacer());
				}
				num++;
				num2++;
			}
			else
			{
				pnlLeft.Children.Add(CreateDiffPlaceholder(DiffLineType.Added));
				pnlRight.Children.Add(CreateDiffLine(diffLine.Text, DiffLineType.Added));
				pnlCenter.Children.Add(CreateCenterSpacer());
				num++;
				num2++;
			}
		}
	}

	private static Border CreateCenterSpacer()
	{
		return new Border
		{
			Padding = new Thickness(4.0, 1.0, 4.0, 1.0),
			Margin = new Thickness(0.0, 0.0, 0.0, 1.0),
			Child = new System.Windows.Controls.TextBlock
			{
				Text = " ",
				FontFamily = new FontFamily("Consolas"),
				FontSize = 12.0
			}
		};
	}

	private static System.Windows.Controls.TextBlock BuildInlineDiffTextBlock(string text, string otherText, bool isLeft)
	{
		Brush foreground = (isLeft ? BrushRemovedFg : BrushAddedFg);
		Brush background = (isLeft ? BrushInlineRemovedBg : BrushInlineAddedBg);
		System.Windows.Controls.TextBlock textBlock = new System.Windows.Controls.TextBlock
		{
			FontFamily = new FontFamily("Consolas"),
			FontSize = 12.0,
			TextWrapping = TextWrapping.NoWrap
		};
		int i = 0;
		int num;
		for (num = Math.Min(text.Length, otherText.Length); i < num && text[i] == otherText[i]; i++)
		{
		}
		int j;
		for (j = 0; j < num - i && text[text.Length - 1 - j] == otherText[otherText.Length - 1 - j]; j++)
		{
		}
		if (i > 0)
		{
			textBlock.Inlines.Add(new Run(text.Substring(0, i))
			{
				Foreground = foreground
			});
		}
		int num2 = i;
		string text2 = text.Substring(num2, text.Length - j - num2);
		if (text2.Length > 0)
		{
			textBlock.Inlines.Add(new Run(text2)
			{
				Foreground = foreground,
				Background = background,
				FontWeight = FontWeights.Bold
			});
		}
		if (j > 0)
		{
			InlineCollection inlines = textBlock.Inlines;
			num2 = j;
			int length = text.Length;
			int num3 = length - num2;
			inlines.Add(new Run(text.Substring(num3, length - num3))
			{
				Foreground = foreground
			});
		}
		if (textBlock.Inlines.Count == 0)
		{
			textBlock.Inlines.Add(new Run(string.IsNullOrEmpty(text) ? " " : text)
			{
				Foreground = foreground
			});
		}
		return textBlock;
	}

	private static Border CreateInlineDiffLine(string text, string otherText, bool isLeft)
	{
		Border border = new Border
		{
			Padding = new Thickness(4.0, 1.0, 4.0, 1.0),
			Margin = new Thickness(0.0, 0.0, 0.0, 1.0),
			CornerRadius = new CornerRadius(2.0),
			Background = (isLeft ? BrushRemovedBg : BrushAddedBg)
		};
		border.Child = BuildInlineDiffTextBlock(text, otherText, isLeft);
		return border;
	}

	private Border CreateInlineDiffSelectable(string wikiText, string genText, bool isLeft, int diffIdx)
	{
		Border border = new Border
		{
			Padding = new Thickness(4.0, 1.0, 4.0, 1.0),
			Margin = new Thickness(0.0, 0.0, 0.0, 1.0),
			CornerRadius = new CornerRadius(2.0),
			Background = BrushRemovedBg,
			Cursor = Cursors.Hand,
			Tag = diffIdx
		};
		border.Child = BuildInlineDiffTextBlock(wikiText, genText, isLeft: true);
		AttachSelectHandlers(border);
		return border;
	}

	private Border CreateSelectableLine(string text, int diffIndex)
	{
		Border border = new Border
		{
			Padding = new Thickness(4.0, 1.0, 4.0, 1.0),
			Margin = new Thickness(0.0, 0.0, 0.0, 1.0),
			CornerRadius = new CornerRadius(2.0),
			Background = BrushRemovedBg,
			Cursor = Cursors.Hand,
			Tag = diffIndex
		};
		border.Child = new System.Windows.Controls.TextBlock
		{
			Text = (string.IsNullOrEmpty(text) ? " " : text),
			FontFamily = new FontFamily("Consolas"),
			FontSize = 12.0,
			TextWrapping = TextWrapping.NoWrap,
			Foreground = BrushRemovedFg
		};
		AttachSelectHandlers(border);
		return border;
	}

	private void AttachSelectHandlers(Border border)
	{
		border.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
		{
			_isDragging = true;
			int item = (int)((Border)s).Tag;
			_dragIsDeselecting = _selectedRemovedIndices.Contains(item);
			ToggleLeftLine(s as Border);
			e.Handled = true;
		};
		border.MouseEnter += delegate(object s, MouseEventArgs _)
		{
			if (_isDragging && s is Border border2)
			{
				int item = (int)border2.Tag;
				bool flag = _selectedRemovedIndices.Contains(item);
				if (_dragIsDeselecting && flag)
				{
					ToggleLeftLine(border2);
				}
				else if (!_dragIsDeselecting && !flag)
				{
					ToggleLeftLine(border2);
				}
			}
		};
	}

	private void ToggleLeftLine(Border? border)
	{
		if (border?.Tag is int item)
		{
			if (_selectedRemovedIndices.Contains(item))
			{
				_selectedRemovedIndices.Remove(item);
				border.Background = BrushRemovedBg;
			}
			else
			{
				_selectedRemovedIndices.Add(item);
				border.Background = BrushSelectedBg;
			}
			UpdateMergeArrows();
		}
	}

	private int GetVisualRow(Border border)
	{
		return pnlLeft.Children.IndexOf(border);
	}

	private void UpdateMergeArrows()
	{
		foreach (var (num2, element) in _savedRightElements)
		{
			if (num2 < pnlRight.Children.Count)
			{
				pnlRight.Children.RemoveAt(num2);
				pnlRight.Children.Insert(num2, element);
			}
		}
		_savedRightElements.Clear();
		if (_selectedRemovedIndices.Count == 0)
		{
			return;
		}
		List<(int, int)> list = new List<(int, int)>();
		for (int i = 0; i < pnlLeft.Children.Count; i++)
		{
			if (pnlLeft.Children[i] is Border { Tag: var tag } && tag is int num3 && _selectedRemovedIndices.Contains(num3))
			{
				list.Add((i, num3));
			}
		}
		if (list.Count == 0)
		{
			return;
		}
		List<List<(int, int)>> list2 = new List<List<(int, int)>>();
		List<(int, int)> list3 = null;
		int num4 = -2;
		foreach (var item2 in list)
		{
			if (item2.Item1 != num4 + 1)
			{
				list3 = new List<(int, int)>();
				list2.Add(list3);
			}
			list3.Add(item2);
			(num4, _) = item2;
		}
		foreach (List<(int, int)> item3 in list2)
		{
			int midRow = item3[item3.Count / 2].Item1;
			if (midRow < 0 || midRow >= pnlRight.Children.Count) continue;

			HashSet<int> indices = new HashSet<int>(item3.Select<(int, int), int>(((int row, int di) g) => g.di));

			// Save all right-side elements in the group
			foreach (var (row, _) in item3)
			{
				if (row >= 0 && row < pnlRight.Children.Count && !_savedRightElements.ContainsKey(row))
					_savedRightElements[row] = pnlRight.Children[row];
			}

			// Replace middle row with merge button
			Border border2 = new Border
			{
				Padding = new Thickness(8.0, 1.0, 8.0, 1.0),
				Margin = new Thickness(0.0, 0.0, 0.0, 1.0),
				CornerRadius = new CornerRadius(2.0),
				Cursor = Cursors.Hand,
				Tag = "merge-arrow",
				Background = BrushArrowBg
			};
			Grid grid = new Grid();
			grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
			grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.0, GridUnitType.Star) });
			System.Windows.Controls.TextBlock element2 = new System.Windows.Controls.TextBlock
			{
				Text = $">  Merge {item3.Count} line{((item3.Count > 1) ? "s" : "")}",
				FontFamily = new FontFamily("Consolas"),
				FontSize = 12.0,
				Foreground = BrushArrowFg
			};
			grid.Children.Add(element2);
			border2.Child = grid;
			border2.PreviewMouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
			{
				ApplyMerge(indices);
				e.Handled = true;
			};
			pnlRight.Children.RemoveAt(midRow);
			pnlRight.Children.Insert(midRow, border2);

			// Replace other rows in the group with empty placeholders
			foreach (var (row, _) in item3)
			{
				if (row != midRow && row >= 0 && row < pnlRight.Children.Count)
				{
					var placeholder = new Border
					{
						Padding = new Thickness(4.0, 1.0, 4.0, 1.0),
						Margin = new Thickness(0.0, 0.0, 0.0, 1.0),
						CornerRadius = new CornerRadius(2.0),
						Background = BrushArrowBg,
						Child = new System.Windows.Controls.TextBlock
						{
							Text = " ",
							FontFamily = new FontFamily("Consolas"),
							FontSize = 12.0
						}
					};
					pnlRight.Children.RemoveAt(row);
					pnlRight.Children.Insert(row, placeholder);
				}
			}
		}
	}

	private void ApplyMerge(HashSet<int> indicesToMerge)
	{
		if (_currentDiffs == null || indicesToMerge.Count == 0 || _lastWikiContent == null)
			return;
		List<string> list = new List<string>();
		for (int i = 0; i < _currentDiffs.Count; i++)
		{
			DiffLine diffLine = _currentDiffs[i];
			if (diffLine.Type == DiffLineType.Match)
			{
				list.Add(diffLine.Text);
			}
			else if (diffLine.Type == DiffLineType.Modified)
			{
				if (indicesToMerge.Contains(i))
				{
					_mergedOutputLineIndices.Add(list.Count);
					list.Add(diffLine.OldText ?? diffLine.Text);
				}
				else
				{
					list.Add(diffLine.Text);
				}
			}
			else if (diffLine.Type == DiffLineType.Removed)
			{
				if (indicesToMerge.Contains(i))
				{
					_mergedOutputLineIndices.Add(list.Count);
					list.Add(diffLine.Text);
					if (_pairedRemovedIndices.Contains(i) && i + 1 < _currentDiffs.Count && _currentDiffs[i + 1].Type == DiffLineType.Added)
						i++;
				}
			}
			else if (diffLine.Type == DiffLineType.Added)
			{
				list.Add(diffLine.Text);
			}
		}
		_fullOutput = string.Join("\n", list);
		ReDiffLocally();
	}

	private void ReDiffLocally()
	{
		if (_lastWikiContent != null)
		{
			btnCopy.IsEnabled = true;
			List<DiffLine> list = ((_currentMode == MysteryGeneratorMode.Rewards) ? MysteryWikiService.ComputeRewardLevelDiff(_lastWikiContent, _fullOutput) : MysteryWikiService.ComputeLineDiffs(_lastWikiContent, _fullOutput));
			_isDiffMode = true;
			HashSet<int> mergedOutputIndices = ComputeMergedOutputIndices();
			BuildDiffViewWithMerged(list, mergedOutputIndices);
			int num = list.Count((DiffLine d) => d.Type == DiffLineType.Added);
			int num2 = list.Count((DiffLine d) => d.Type == DiffLineType.Removed);
			int num3 = list.Count((DiffLine d) => d.Type == DiffLineType.Modified);
			bool flag = num == 0 && num2 == 0 && num3 == 0;
			List<string> list2 = new List<string>();
			if (num > 0)
			{
				list2.Add($"{num} added");
			}
			if (num2 > 0)
			{
				list2.Add($"{num2} removed");
			}
			if (num3 > 0)
			{
				list2.Add($"{num3} modified");
			}
			warningBar.Message = (flag ? "Content matches wiki." : string.Join("  ", list2));
			warningBar.Severity = (flag ? InfoBarSeverity.Success : InfoBarSeverity.Informational);
			warningBar.IsOpen = true;
		}
	}

	private HashSet<int> ComputeMergedOutputIndices()
	{
		return new HashSet<int>(_mergedOutputLineIndices);
	}

	private void BuildDiffViewWithMerged(List<DiffLine> diffs, HashSet<int> mergedOutputIndices)
	{
		BuildDiffView(diffs);
		_mergedRightRows.Clear();
		_mergedRowToOutputIdx.Clear();
		if (_originalOutput == null || mergedOutputIndices.Count == 0)
		{
			return;
		}
		int num = 0;
		int num2 = 0;
		int num3 = 0;
		while (num3 < diffs.Count && num2 < pnlRight.Children.Count)
		{
			DiffLine diffLine = diffs[num3];
			if (diffLine.Type == DiffLineType.Match)
			{
				if (mergedOutputIndices.Contains(num))
				{
					MarkRowAsMerged(num2, num);
				}
				num++;
				num2++;
				num3++;
			}
			else if (diffLine.Type == DiffLineType.Modified)
			{
				if (mergedOutputIndices.Contains(num))
				{
					MarkRowAsMerged(num2, num);
				}
				num++;
				num2++;
				num3++;
			}
			else if (diffLine.Type == DiffLineType.Removed && num3 + 1 < diffs.Count && diffs[num3 + 1].Type == DiffLineType.Added)
			{
				num++;
				num2++;
				num3 += 2;
			}
			else if (diffLine.Type == DiffLineType.Removed)
			{
				num2++;
				num3++;
			}
			else
			{
				num++;
				num2++;
				num3++;
			}
		}
	}

	private void MarkRowAsMerged(int visualRow, int outputLineIdx)
	{
		_mergedRightRows.Add(visualRow);
		_mergedRowToOutputIdx[visualRow] = outputLineIdx;
		if (visualRow >= pnlRight.Children.Count || !(pnlRight.Children[visualRow] is Border border))
		{
			return;
		}
		border.Background = BrushMergedBg;
		if (border.Child is System.Windows.Controls.TextBlock textBlock)
		{
			textBlock.Foreground = BrushMergedFg;
		}
		border.Cursor = Cursors.Hand;
		border.Tag = visualRow;
		border.ToolTip = "Click or drag to revert";
		ToolTipService.SetInitialShowDelay(border, 0);
		border.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
		{
			_isDraggingRight = true;
			if (s is Border { Tag: var tag } && tag is int row)
			{
				MarkForRevert(row);
			}
			e.Handled = true;
		};
		border.MouseEnter += delegate(object s, MouseEventArgs _)
		{
			if (_isDraggingRight && s is Border { Tag: var tag } && tag is int num && _mergedRightRows.Contains(num))
			{
				MarkForRevert(num);
			}
		};
	}

	private void MarkForRevert(int row)
	{
		if (_mergedRightRows.Contains(row))
		{
			_revertRows.Add(row);
			if (row < pnlRight.Children.Count && pnlRight.Children[row] is Border border)
			{
				border.Opacity = 0.4;
			}
		}
	}

	private void PnlRight_MouseUp(object sender, MouseButtonEventArgs e)
	{
		if (_isDraggingRight && _revertRows.Count > 0 && _originalOutput != null)
		{
			HashSet<int> hashSet = new HashSet<int>(from r in _revertRows
				where _mergedRowToOutputIdx.ContainsKey(r)
				select _mergedRowToOutputIdx[r]);
			if (hashSet.Count > 0)
			{
				RevertMergedLines(hashSet);
			}
		}
		_isDraggingRight = false;
		_revertRows.Clear();
	}

	private void RevertMergedLines(HashSet<int> outputIndicesToRevert)
	{
		if (_originalOutput == null || _lastWikiContent == null)
		{
			return;
		}
		List<DiffLine> list = MysteryWikiService.ComputeLineDiffs(_originalOutput, _fullOutput);
		Dictionary<int, string> dictionary = new Dictionary<int, string>();
		int num = 0;
		var removedQueue = new Queue<string>();
		foreach (DiffLine item in list)
		{
			if (item.Type == DiffLineType.Match)
			{
				removedQueue.Clear();
				num++;
			}
			else if (item.Type == DiffLineType.Removed)
			{
				removedQueue.Enqueue(item.Text);
			}
			else if (item.Type == DiffLineType.Modified)
			{
				dictionary[num] = item.OldText;
				removedQueue.Clear();
				num++;
			}
			else if (item.Type == DiffLineType.Added)
			{
				dictionary[num] = removedQueue.Count > 0 ? removedQueue.Dequeue() : null;
				num++;
			}
		}
		List<string> list2 = _fullOutput.Replace("\r\n", "\n").Split('\n').ToList();
		foreach (int item2 in outputIndicesToRevert.OrderByDescending((int i) => i))
		{
			if (dictionary.TryGetValue(item2, out var value2))
			{
				if (value2 != null)
				{
					list2[item2] = value2;
				}
				else if (item2 < list2.Count)
				{
					list2.RemoveAt(item2);
				}
			}
		}
		_fullOutput = string.Join("\n", list2);
		foreach (int idx in outputIndicesToRevert)
			_mergedOutputLineIndices.Remove(idx);
		_revertRows.Clear();
		ReDiffLocally();
	}

	private void PnlLeft_MouseUp(object sender, MouseButtonEventArgs e)
	{
		_isDragging = false;
	}

	private static Border CreateDiffLine(string text, DiffLineType type)
	{
		Border border = new Border
		{
			Padding = new Thickness(4.0, 1.0, 4.0, 1.0),
			Margin = new Thickness(0.0, 0.0, 0.0, 1.0),
			CornerRadius = new CornerRadius(2.0)
		};
		System.Windows.Controls.TextBlock textBlock = new System.Windows.Controls.TextBlock
		{
			Text = (string.IsNullOrEmpty(text) ? " " : text),
			FontFamily = new FontFamily("Consolas"),
			FontSize = 12.0,
			TextWrapping = TextWrapping.NoWrap
		};
		switch (type)
		{
		case DiffLineType.Added:
			border.Background = BrushAddedBg;
			textBlock.Foreground = BrushAddedFg;
			break;
		case DiffLineType.Removed:
			border.Background = BrushRemovedBg;
			textBlock.Foreground = BrushRemovedFg;
			break;
		default:
			textBlock.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
			break;
		}
		border.Child = textBlock;
		return border;
	}

	private static Border CreateModifiedLine(string text)
	{
		Border border = new Border
		{
			Padding = new Thickness(4.0, 1.0, 4.0, 1.0),
			Margin = new Thickness(0.0, 0.0, 0.0, 1.0),
			CornerRadius = new CornerRadius(2.0),
			Background = BrushModifiedBg
		};
		System.Windows.Controls.TextBlock child = new System.Windows.Controls.TextBlock
		{
			Text = (string.IsNullOrEmpty(text) ? " " : text),
			FontFamily = new FontFamily("Consolas"),
			FontSize = 12.0,
			TextWrapping = TextWrapping.NoWrap,
			Foreground = BrushModifiedFg
		};
		border.Child = child;
		return border;
	}

	private static Border CreateDiffPlaceholder(DiffLineType type)
	{
		Border border = new Border
		{
			Padding = new Thickness(4.0, 1.0, 4.0, 1.0),
			Margin = new Thickness(0.0, 0.0, 0.0, 1.0),
			CornerRadius = new CornerRadius(2.0),
			Opacity = 0.3
		};
		System.Windows.Controls.TextBlock child = new System.Windows.Controls.TextBlock
		{
			Text = " ",
			FontFamily = new FontFamily("Consolas"),
			FontSize = 12.0
		};
		border.Background = ((type == DiffLineType.Added) ? BrushAddedBg : BrushRemovedBg);
		border.Child = child;
		return border;
	}

	private void SetDiffLeftLink(string? pageTitle)
	{
		if (!string.IsNullOrEmpty(pageTitle))
		{
			runDiffLeftSuffix.Text = " - ";
			runDiffLeftLink.Text = pageTitle;
			linkDiffLeftPage.NavigateUri = new Uri("https://merge-mansion.fandom.com/wiki/" + Uri.EscapeDataString(pageTitle));
			linkDiffLeftPage.Cursor = Cursors.Hand;
		}
		else
		{
			runDiffLeftSuffix.Text = "";
			runDiffLeftLink.Text = "";
			linkDiffLeftPage.NavigateUri = null;
			linkDiffLeftPage.Cursor = Cursors.Arrow;
		}
	}

	private void LinkDiffLeftPage_MouseEnter(object sender, MouseEventArgs e)
	{
		if (linkDiffLeftPage.NavigateUri != null)
			linkDiffLeftPage.TextDecorations = System.Windows.TextDecorations.Underline;
	}

	private void LinkDiffLeftPage_MouseLeave(object sender, MouseEventArgs e)
	{
		linkDiffLeftPage.TextDecorations = null;
	}

	private void LinkDiffLeftPage_RequestNavigate(object sender, RequestNavigateEventArgs e)
	{
		Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
		e.Handled = true;
	}

	private void ScrollLeft_ScrollChanged(object sender, ScrollChangedEventArgs e)
	{
		if (!_suppressScrollSync)
		{
			_suppressScrollSync = true;
			scrollRight.ScrollToVerticalOffset(scrollLeft.VerticalOffset);
			scrollRight.ScrollToHorizontalOffset(scrollLeft.HorizontalOffset);
			scrollCenter.ScrollToVerticalOffset(scrollLeft.VerticalOffset);
			_suppressScrollSync = false;
		}
	}

	private void ScrollRight_ScrollChanged(object sender, ScrollChangedEventArgs e)
	{
		if (!_suppressScrollSync)
		{
			_suppressScrollSync = true;
			scrollLeft.ScrollToVerticalOffset(scrollRight.VerticalOffset);
			scrollCenter.ScrollToVerticalOffset(scrollRight.VerticalOffset);
			scrollLeft.ScrollToHorizontalOffset(scrollRight.HorizontalOffset);
			_suppressScrollSync = false;
		}
	}

	private void ScrollCenter_ScrollChanged(object sender, ScrollChangedEventArgs e)
	{
		if (!_suppressScrollSync)
		{
			_suppressScrollSync = true;
			scrollLeft.ScrollToVerticalOffset(scrollCenter.VerticalOffset);
			scrollRight.ScrollToVerticalOffset(scrollCenter.VerticalOffset);
			_suppressScrollSync = false;
		}
	}

	private string? GetEffectiveRewardVariant()
	{
		string? confirmed = _mystery.WikiStatus.ManualConfirm.RewardsConfirmed;
		if (confirmed != null && confirmed != "true")
			return confirmed; // Specific variant confirmed (e.g. "Pet/3", "6")
		if (confirmed == "true")
			return _mystery.WikiStatus.MatchingVariant; // Confirmed with whatever wiki has
		// Not confirmed — current behavior
		return (_mystery.WikiStatus.RewardContentMatches == true)
			? _mystery.WikiStatus.MatchingVariant
			: (_hypotheticalRewardVariant ?? _mystery.WikiStatus.MatchingVariant);
	}

	private void UpdateConfirmButton()
	{
		if (_currentMode == MysteryGeneratorMode.Images)
		{
			btnConfirmManual.Visibility = Visibility.Collapsed;
			return;
		}
		MysteryManualConfirmFlags manualConfirm = _mystery.WikiStatus.ManualConfirm;
		bool flag2 = _currentMode switch
		{
			MysteryGeneratorMode.EventPage => manualConfirm.EventPageConfirmed,
			MysteryGeneratorMode.Rewards => manualConfirm.RewardsConfirmed != null,
			MysteryGeneratorMode.EventItemPage => manualConfirm.ItemPageConfirmed,
			_ => false,
		};
		WikiCheckState checkState = _currentMode switch
		{
			MysteryGeneratorMode.EventPage => _mystery.WikiStatus.EventPageState,
			MysteryGeneratorMode.Rewards => _mystery.WikiStatus.RewardTemplateState,
			MysteryGeneratorMode.EventItemPage => _mystery.WikiStatus.EventItemPageState,
			_ => WikiCheckState.Unknown,
		};
		if (flag2)
		{
			btnConfirmManual.Content = "Remove Confirmation";
			btnConfirmManual.Visibility = Visibility.Visible;
			btnPublish.IsEnabled = false;
			string sectionName = _currentMode switch
			{
				MysteryGeneratorMode.EventPage => "Event Page",
				MysteryGeneratorMode.Rewards => "Rewards",
				MysteryGeneratorMode.EventItemPage => "Event Item Page",
				_ => "",
			};
			btnPublish.ToolTip = $"Publish disabled: {sectionName} is manually confirmed as correct. Remove confirmation first.";
			ToolTipService.SetInitialShowDelay(btnPublish, 0);
			ToolTipService.SetShowOnDisabled(btnPublish, true);
		}
		else
		{
			btnPublish.IsEnabled = true;
			btnPublish.ToolTip = null;
			bool showConfirm = checkState == WikiCheckState.Mismatch
				|| (_currentMode == MysteryGeneratorMode.Rewards && checkState != WikiCheckState.Match);
			if (showConfirm)
			{
				if (_currentMode == MysteryGeneratorMode.Rewards
					&& cmbCompareTemplate.SelectedIndex > 0
					&& cmbCompareTemplate.SelectedItem is ComboBoxItem selItem
					&& selItem.Content is string selDisplay)
				{
					string flagValue = selDisplay.StartsWith("Rewards/", StringComparison.OrdinalIgnoreCase)
						? selDisplay["Rewards/".Length..] : "true";
					var sp = new StackPanel { HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
					sp.Children.Add(new System.Windows.Controls.TextBlock { Text = "Confirm as Correct", HorizontalAlignment = System.Windows.HorizontalAlignment.Center });
					var sub = new System.Windows.Controls.TextBlock { Text = flagValue, FontSize = 10, Opacity = 0.6, HorizontalAlignment = System.Windows.HorizontalAlignment.Center };
					sp.Children.Add(sub);
					btnConfirmManual.Content = sp;
				}
				else
				{
					btnConfirmManual.Content = "Confirm as Correct";
				}
				btnConfirmManual.Visibility = Visibility.Visible;
			}
			else
			{
				btnConfirmManual.Visibility = Visibility.Collapsed;
			}
		}
	}

	private async void BtnConfirmManual_Click(object sender, RoutedEventArgs e)
	{
		if (!_main.Settings.WikiVerified)
		{
			warningBar.Message = "Wiki account not verified. Go to Settings  Wiki to log in.";
			warningBar.Severity = InfoBarSeverity.Warning;
			warningBar.IsOpen = true;
			return;
		}
		MysteryManualConfirmFlags flags = _mystery.WikiStatus.ManualConfirm;
		string flagName = _currentMode switch
		{
			MysteryGeneratorMode.EventPage => "eventPageManualConfirm",
			MysteryGeneratorMode.Rewards => "rewardsManualConfirm",
			MysteryGeneratorMode.EventItemPage => "itemPageConfirmed",
			_ => "",
		};
		if (string.IsNullOrEmpty(flagName))
			return;
		bool currentValue = _currentMode switch
		{
			MysteryGeneratorMode.EventPage => flags.EventPageConfirmed,
			MysteryGeneratorMode.Rewards => flags.RewardsConfirmed != null,
			MysteryGeneratorMode.EventItemPage => flags.ItemPageConfirmed,
			_ => false,
		};
		bool newValue = !currentValue;
		// For Rewards mode: determine the variant string to store
		string? rewardsConfirmValue = null;
		if (_currentMode == MysteryGeneratorMode.Rewards && newValue)
		{
			if (cmbCompareTemplate.SelectedIndex > 0 && cmbCompareTemplate.SelectedItem is ComboBoxItem selectedItem
				&& selectedItem.Content is string display)
			{
				if (display.StartsWith("Rewards/", StringComparison.OrdinalIgnoreCase))
					rewardsConfirmValue = display["Rewards/".Length..];
				else
					rewardsConfirmValue = "true";
			}
			else
				rewardsConfirmValue = "true";
		}
		string action = newValue ? "Set" : "Remove";
		string sectionName = _currentMode switch
		{
			MysteryGeneratorMode.EventPage => "Event Page",
			MysteryGeneratorMode.Rewards => "Rewards",
			MysteryGeneratorMode.EventItemPage => "Event Item Page",
			_ => "",
		};
		btnConfirmManual.IsEnabled = false;
		warningBar.Message = "Loading preview...";
		warningBar.Severity = InfoBarSeverity.Informational;
		warningBar.IsOpen = true;
		string beforeLine;
		string afterLine;
		try
		{
			string content = await MysteryWikiService.FetchPageContentAsync("Module:Datatable/Various");
			if (string.IsNullOrEmpty(content))
			{
				warningBar.Message = "Failed to fetch Module:Datatable/Various.";
				warningBar.Severity = InfoBarSeverity.Error;
				btnConfirmManual.IsEnabled = true;
				return;
			}
			int? idx = _mystery.WikiStatus.MysteryTableIndex;
			if (!idx.HasValue)
			{
				warningBar.Message = "Mystery not found in Module:Datatable/Various. Run 'Check Wiki Status' first.";
				warningBar.Severity = InfoBarSeverity.Warning;
				btnConfirmManual.IsEnabled = true;
				return;
			}
			Regex linePattern = new Regex($"\\[{idx.Value}\\]\\s*=\\s*\\{{[^}}]+\\}}");
			Match lineMatch = linePattern.Match(content);
			beforeLine = (lineMatch.Success ? lineMatch.Value.Trim() : "(entry not found)");
			if (_currentMode == MysteryGeneratorMode.Rewards)
			{
				// Rewards uses string? value (e.g. "Pet/3", "true", or null to remove)
				if (rewardsConfirmValue != null)
				{
					string luaValue = rewardsConfirmValue == "true" ? "true" : $"\"{rewardsConfirmValue}\"";
					string insertText = flagName + " = " + luaValue;
					if (!Regex.IsMatch(beforeLine, flagName + "\\s*="))
						afterLine = Regex.Replace(beforeLine, "\\s*\\},?\\s*$", ", " + insertText + " },");
					else
						afterLine = Regex.Replace(beforeLine, flagName + "\\s*=\\s*(?:\"[^\"]*\"|\\w+)", insertText);
				}
				else
				{
					afterLine = Regex.Replace(beforeLine, ",?\\s*" + flagName + "\\s*=\\s*(?:\"[^\"]*\"|true)", "");
					afterLine = Regex.Replace(afterLine, ",(\\s*\\})", "$1");
				}
			}
			else if (newValue)
			{
				afterLine = ((!beforeLine.Contains(flagName)) ? Regex.Replace(beforeLine, "\\s*\\},?\\s*$", ", " + flagName + " = true },") : beforeLine);
			}
			else
			{
				afterLine = Regex.Replace(beforeLine, ",?\\s*" + flagName + "\\s*=\\s*true", "");
				afterLine = Regex.Replace(afterLine, ",(\\s*\\})", "$1");
			}
		}
		catch (Exception ex)
		{
			warningBar.Message = "Preview failed: " + ex.Message;
			warningBar.Severity = InfoBarSeverity.Error;
			btnConfirmManual.IsEnabled = true;
			return;
		}
		warningBar.IsOpen = false;
		StackPanel dialogContent = new StackPanel();
		System.Windows.Controls.TextBlock descText = new System.Windows.Controls.TextBlock
		{
			Text = $"{action} manual confirmation for {sectionName}:\n{_mystery.Name}",
			TextWrapping = TextWrapping.Wrap,
			FontSize = 13.0,
			Margin = new Thickness(0.0, 0.0, 0.0, 12.0)
		};
		descText.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
		dialogContent.Children.Add(descText);
		System.Windows.Controls.TextBlock beforeLabel = new System.Windows.Controls.TextBlock
		{
			Text = "Before:",
			FontSize = 11.0,
			FontWeight = FontWeights.SemiBold,
			Margin = new Thickness(0.0, 0.0, 0.0, 4.0)
		};
		beforeLabel.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
		dialogContent.Children.Add(beforeLabel);
		Border beforeBox = new Border
		{
			CornerRadius = new CornerRadius(4.0),
			Padding = new Thickness(10.0, 6.0, 10.0, 6.0),
			Margin = new Thickness(0.0, 0.0, 0.0, 12.0)
		};
		beforeBox.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorSecondaryBrush");
		System.Windows.Controls.TextBlock beforeTb = new System.Windows.Controls.TextBlock
		{
			Text = beforeLine,
			FontFamily = new FontFamily("Consolas"),
			FontSize = 11.0,
			TextWrapping = TextWrapping.Wrap
		};
		beforeTb.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
		beforeBox.Child = beforeTb;
		dialogContent.Children.Add(beforeBox);
		System.Windows.Controls.TextBlock afterLabel = new System.Windows.Controls.TextBlock
		{
			Text = "After:",
			FontSize = 11.0,
			FontWeight = FontWeights.SemiBold,
			Margin = new Thickness(0.0, 0.0, 0.0, 4.0)
		};
		afterLabel.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
		dialogContent.Children.Add(afterLabel);
		Border afterBox = new Border
		{
			CornerRadius = new CornerRadius(4.0),
			Padding = new Thickness(10.0, 6.0, 10.0, 6.0)
		};
		afterBox.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorSecondaryBrush");
		System.Windows.Controls.TextBlock afterTb = new System.Windows.Controls.TextBlock
		{
			Text = afterLine,
			FontFamily = new FontFamily("Consolas"),
			FontSize = 11.0,
			TextWrapping = TextWrapping.Wrap
		};
		afterTb.Foreground = (newValue ? new SolidColorBrush(Color.FromRgb(64, 208, 64)) : new SolidColorBrush(Color.FromRgb(224, 128, 64)));
		afterBox.Child = afterTb;
		dialogContent.Children.Add(afterBox);
		Wpf.Ui.Controls.MessageBox msgBox = new Wpf.Ui.Controls.MessageBox
		{
			Title = action + " Manual Confirmation",
			Content = dialogContent,
			PrimaryButtonText = (newValue ? "Confirm" : "Remove Confirmation"),
			CloseButtonText = "Cancel",
			MinWidth = 650.0,
			Owner = this
		};
		ApplicationThemeManager.Apply(msgBox);
		if (await msgBox.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary)
		{
			btnConfirmManual.IsEnabled = true;
			return;
		}
		warningBar.Message = "Publishing to Module:Datatable/Various...";
		warningBar.Severity = InfoBarSeverity.Informational;
		warningBar.IsOpen = true;
		try
		{
			bool publishSuccess;
			if (_currentMode == MysteryGeneratorMode.Rewards)
				publishSuccess = (await MysteryWikiService.SetManualConfirmFlagStringAsync(_main.Settings.WikiUsername, _main.Settings.WikiPassword, _mystery, flagName, rewardsConfirmValue)).Item3;
			else
				publishSuccess = (await MysteryWikiService.SetManualConfirmFlagAsync(_main.Settings.WikiUsername, _main.Settings.WikiPassword, _mystery, flagName, newValue)).Item3;
			if (publishSuccess)
			{
				switch (_currentMode)
				{
				case MysteryGeneratorMode.EventPage:
					flags.EventPageConfirmed = newValue;
					break;
				case MysteryGeneratorMode.Rewards:
					flags.RewardsConfirmed = rewardsConfirmValue;
					break;
				case MysteryGeneratorMode.EventItemPage:
					flags.ItemPageConfirmed = newValue;
					break;
				}
				warningBar.Message = "Manual confirmation " + (newValue ? "set" : "removed") + " successfully.";
				warningBar.Severity = InfoBarSeverity.Success;
				UpdateConfirmButton();
				MysteryWikiService.UpdateSingleMysteryCache(_mystery);
				OnStatusChanged?.Invoke();
			}
			else
			{
				warningBar.Message = "Failed to update module.";
				warningBar.Severity = InfoBarSeverity.Error;
			}
		}
		catch (Exception ex)
		{
			warningBar.Message = "Publish failed: " + ex.Message;
			warningBar.Severity = InfoBarSeverity.Error;
		}
		finally
		{
			btnConfirmManual.IsEnabled = true;
		}
	}

	private void BtnCopy_Click(object sender, RoutedEventArgs e)
	{
		if (!string.IsNullOrEmpty(_fullOutput))
		{
			App.NativeSetClipboardText(_fullOutput);
			UserStatsService.Increment(delegate(UserStats s)
			{
				s.MysteryTemplatesGenerated++;
			});
			btnCopy.Content = "Copied!";
			ResetCopyButton();
		}
	}

	private async Task ResetCopyButton()
	{
		await Task.Delay(2000);
		btnCopy.Content = "Copy to Clipboard";
	}

	private async void BtnPublish_Click(object sender, RoutedEventArgs e)
	{
		if (string.IsNullOrEmpty(_fullOutput))
		{
			return;
		}
		if (!_main.Settings.WikiVerified)
		{
			warningBar.Message = "Wiki account not verified.";
			warningBar.Severity = InfoBarSeverity.Warning;
			warningBar.IsOpen = true;
			return;
		}
		bool flag = _currentMode == MysteryGeneratorMode.EventPage;
		bool flag2 = flag;
		if (flag2)
		{
			WikiCheckState rewardTemplateState = _mystery.WikiStatus.RewardTemplateState;
			// Only skip the dialog if the reward template is confirmed to match
			flag2 = rewardTemplateState != WikiCheckState.Match && rewardTemplateState != WikiCheckState.Confirmed;
		}
		if (flag2)
		{
			Wpf.Ui.Controls.MessageBox msgBox = new Wpf.Ui.Controls.MessageBox
			{
				Title = "Reward Template Required",
				Content = "A matching reward template must be created first.\n\nCreate the template on the Rewards tab before publishing the Event Page.",
				PrimaryButtonText = "Go to Rewards tab",
				Owner = this
			};
			ApplicationThemeManager.Apply(msgBox);
			if (await msgBox.ShowDialogAsync() == Wpf.Ui.Controls.MessageBoxResult.Primary)
			{
				tabMode.SelectedIndex = 1;
			}
			return;
		}
		string pageTitle = await GetPageTitleForModeAsync();
		if (string.IsNullOrEmpty(pageTitle))
		{
			warningBar.Message = "Cannot determine page title.";
			warningBar.Severity = InfoBarSeverity.Warning;
			warningBar.IsOpen = true;
			return;
		}

		// Confirmation dialog when updating an existing template selected in Compare dropdown
		if (_currentMode == MysteryGeneratorMode.Rewards && !string.IsNullOrEmpty(_selectedCompareTemplateTitle))
		{
			var shortName = pageTitle.Replace("Template:Mystery Pass/", "");
			var result = await new Wpf.Ui.Controls.MessageBox
			{
				Title = "Update existing template?",
				Content = $"You are about to overwrite the existing template \"{shortName}\".\n\nThis will replace its current content with the generated rewards. Are you sure?",
				PrimaryButtonText = "Update",
				CloseButtonText = "Cancel",
			}.ShowDialogAsync();
			if (result != Wpf.Ui.Controls.MessageBoxResult.Primary) return;
		}

		btnPublish.IsEnabled = false;
		warningBar.Message = "Publishing to " + pageTitle + "...";
		warningBar.Severity = InfoBarSeverity.Informational;
		warningBar.IsOpen = true;
		bool published = false;
		try
		{
			bool isRewardUpdate = _currentMode == MysteryGeneratorMode.Rewards &&
				(!string.IsNullOrEmpty(_selectedCompareTemplateTitle) || IsNamedRewardVariant);
			string summary = _currentMode switch
			{
				MysteryGeneratorMode.Rewards => isRewardUpdate ? "Update reward template (via MergeMansionWikiTools)" : "Create reward template (via MergeMansionWikiTools)",
				MysteryGeneratorMode.EventPage => "Create/update mystery page (via MergeMansionWikiTools)",
				MysteryGeneratorMode.EventItemPage => "Create/update event item page (via MergeMansionWikiTools)",
				_ => "Edit via MergeMansionWikiTools",
			};
			string result = await MysteryWikiService.PublishPageAsync(_main.Settings.WikiUsername, _main.Settings.WikiPassword, pageTitle, _fullOutput, summary);
			UserStatsService.Increment(delegate(UserStats s)
			{
				s.MysteryPagesPublished++;
			});
			published = true;
			switch (_currentMode)
			{
			case MysteryGeneratorMode.EventPage:
				_mystery.WikiStatus.EventPageContentMatches = true;
				_mystery.WikiStatus.EventPageExists = true;
				// Auto-create pending gallery template if needed
				if (!string.IsNullOrEmpty(_mystery.WikiStatus.PendingGalleryTemplateContent) &&
				    !string.IsNullOrEmpty(_mystery.WikiStatus.MatchingGalleryVariant))
				{
					try
					{
						string galleryPageTitle = "Template:Mystery Pass/Gallery/" + _mystery.WikiStatus.MatchingGalleryVariant;
						await MysteryWikiService.PublishPageAsync(
							_main.Settings.WikiUsername!, _main.Settings.WikiPassword!,
							galleryPageTitle, _mystery.WikiStatus.PendingGalleryTemplateContent,
							$"Auto-create gallery template ({MysteryWikiService.CountDecorations(_mystery)} decorations, via MergeMansionWikiTools)");
						_mystery.WikiStatus.PendingGalleryTemplateContent = null;
						AppLogger.Info($"Created gallery template: {galleryPageTitle}");
					}
					catch (Exception ex)
					{
						AppLogger.Info($"Failed to create gallery template: {ex.Message}");
					}
				}
				break;
			case MysteryGeneratorMode.Rewards:
			{
				_mystery.WikiStatus.RewardTemplateMatches = true;
				_mystery.WikiStatus.RewardContentMatches = true;
				string rewardsPrefix = "Template:Mystery Pass/Rewards";
				if (pageTitle.StartsWith(rewardsPrefix))
				{
					string after = pageTitle[rewardsPrefix.Length..].TrimStart('/');
					_mystery.WikiStatus.MatchingVariant = string.IsNullOrEmpty(after) ? "" : after;
				}
				// Re-check Event Page — the matching variant may have changed,
				// which changes the generated Event Page content (reward template reference).
				// If the only difference was the template number, Event Page now matches.
				try
				{
					var effectiveVariant = GetEffectiveRewardVariant();
					var (wikiContent, generated, _, _) = await MysteryWikiService.ComputeDiffAsync(
						_mystery, MysteryDiffScope.EventPage, _main.DataService, _main.WikiMapping, _mapping, _dialogueService, effectiveVariant);
					if (wikiContent != null)
					{
						bool pageMatches = MysteryWikiService.ComputeLineDiffs(wikiContent, generated).All(d => d.Type == DiffLineType.Match);
						_mystery.WikiStatus.EventPageContentMatches = pageMatches;
					}
				}
				catch { /* non-critical — status will update on next Check Wiki Status */ }
				// Invalidate template cache so dropdown picks up the new/updated template
				_rewardTemplatesCache = null;
				break;
			}
			case MysteryGeneratorMode.EventItemPage:
				_mystery.WikiStatus.EventItemPageContentMatches = true;
				_mystery.WikiStatus.EventItemPageExists = true;
				break;
			}
			MysteryWikiService.UpdateSingleMysteryCache(_mystery);
			OnStatusChanged?.Invoke();
			if (_currentMode == MysteryGeneratorMode.Rewards)
			{
				string wikiUrl = "https://merge-mansion.fandom.com/wiki/" + Uri.EscapeDataString(pageTitle.Replace(' ', '_'));
				warningBar.Message = "Created: " + pageTitle;
				warningBar.Severity = InfoBarSeverity.Success;
				warningBar.IsOpen = true;
				System.Windows.Controls.TextBlock linkText = new System.Windows.Controls.TextBlock
				{
					FontSize = 11.0
				};
				Hyperlink hyperlink = new Hyperlink(new Run("Open on Wiki"))
				{
					NavigateUri = new Uri(wikiUrl)
				};
				hyperlink.RequestNavigate += delegate(object _, RequestNavigateEventArgs args)
				{
					Process.Start(new ProcessStartInfo(args.Uri.AbsoluteUri)
					{
						UseShellExecute = true
					});
				};
				linkText.Inlines.Add(" - ");
				linkText.Inlines.Add(hyperlink);
				warningBar.Content = linkText;
				OnRewardTemplateCreated?.Invoke(_mystery.WikiStatus.MatchingVariant ?? "");
			}
			else
			{
				warningBar.Message = "Published: " + result;
				warningBar.Severity = InfoBarSeverity.Success;
				warningBar.IsOpen = true;
			}
			// Refresh dropdown with new template list + re-compare with fresh wiki data
			if (_currentMode == MysteryGeneratorMode.Rewards)
				PopulateCompareDropdownAsync(); // re-fetches templates, auto-selects matching variant
			else
				await LoadDiffAsync();
		}
		catch (Exception ex)
		{
			warningBar.Message = "Publish failed: " + ex.Message;
			warningBar.Severity = InfoBarSeverity.Error;
			warningBar.IsOpen = true;
		}
		finally
		{
			if (published)
			{
				btnPublish.ToolTip = "Already published - content matches wiki";
			}
			else
			{
				btnPublish.IsEnabled = true;
			}
		}
	}

	private async Task<string?> GetPageTitleForModeAsync()
	{
		switch (_currentMode)
		{
		case MysteryGeneratorMode.Rewards:
		{
			// User selected a specific template in Compare dropdown → update that template
			if (!string.IsNullOrEmpty(_selectedCompareTemplateTitle))
				return _selectedCompareTemplateTitle;
			// Named template (e.g. "Rewards/Secrets of Serenity") → update existing
			if (IsNamedRewardVariant)
				return "Template:Mystery Pass/Rewards/" + _mystery.WikiStatus.MatchingVariant;
			// Otherwise create new with next available index
			bool isPet = _mystery.MysteryType == MysteryType.Pet;
			string variant = await MysteryWikiService.GetNextVariantNameAsync(isPet);
			string suffix = (string.IsNullOrEmpty(variant) ? "" : ("/" + variant));
			return "Template:Mystery Pass/Rewards" + suffix;
		}
		case MysteryGeneratorMode.EventPage:
			return _mystery.WikiStatus.SuggestedPageTitle ?? _mystery.Name;
		case MysteryGeneratorMode.EventItemPage:
			return _mystery.EventItemName;
		default:
			return null;
		}
	}

	// ── Reward template comparison dropdown ──────────────────────

	private async void PopulateCompareDropdownAsync()
	{
		var ct = _diffCts?.Token ?? CancellationToken.None;
		if (_rewardTemplatesCache == null)
		{
			try
			{
				_rewardTemplatesCache = await MysteryWikiService.FetchRewardTemplatesAsync();
			}
			catch
			{
				return;
			}
		}
		if (ct.IsCancellationRequested || _currentMode != MysteryGeneratorMode.Rewards) return;

		bool isPet = _mystery.MysteryType == MysteryType.Pet;
		_suppressCompareSelection = true;
		cmbCompareTemplate.Items.Clear();
		cmbCompareTemplate.Items.Add("(no comparison)");
		cmbCompareTemplate.SelectedIndex = 0;

		foreach (var (title, _) in _rewardTemplatesCache.OrderBy(kv => kv.Key))
		{
			int idx = title.IndexOf("/Rewards", StringComparison.OrdinalIgnoreCase);
			string variant = "";
			if (idx >= 0)
			{
				string after = title[(idx + "/Rewards".Length)..].TrimStart('/');
				if (!string.IsNullOrEmpty(after)) variant = after;
			}
			bool isPetTemplate = variant.StartsWith("Pet", StringComparison.OrdinalIgnoreCase);
			if (isPet != isPetTemplate) continue;

			string display = string.IsNullOrEmpty(variant) ? "Rewards (base)" : "Rewards/" + variant;
			cmbCompareTemplate.Items.Add(new ComboBoxItem
			{
				Content = display,
				Tag = title
			});
		}
		_suppressCompareSelection = false;
		// Auto-select: confirmed variant > matching variant > none
		string? autoSelectVariant = null;
		string? confirmed = _mystery.WikiStatus.ManualConfirm.RewardsConfirmed;
		if (confirmed != null && confirmed != "true")
			autoSelectVariant = confirmed;
		else if (!string.IsNullOrEmpty(_mystery.WikiStatus.MatchingVariant))
			autoSelectVariant = _mystery.WikiStatus.MatchingVariant;

		if (autoSelectVariant != null)
		{
			string targetDisplay = "Rewards/" + autoSelectVariant;
			for (int i = 1; i < cmbCompareTemplate.Items.Count; i++)
			{
				if (cmbCompareTemplate.Items[i] is ComboBoxItem item
					&& item.Content is string d
					&& string.Equals(d, targetDisplay, StringComparison.OrdinalIgnoreCase))
				{
					cmbCompareTemplate.SelectedIndex = i;
					break;
				}
			}
		}
	}

	private async void CmbCompareTemplate_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (_suppressCompareSelection || _currentMode != MysteryGeneratorMode.Rewards) return;

		if (cmbCompareTemplate.SelectedIndex <= 0 || cmbCompareTemplate.SelectedItem is not ComboBoxItem selected)
		{
			// "(no comparison)" selected — revert to plain output
			_selectedCompareTemplateTitle = null;
			pnlDiff.Visibility = Visibility.Collapsed;
			ShowPlainOutput();
			btnPublish.Content = IsNamedRewardVariant ? "Update Reward Template" : "Create New Reward Template";
			UpdateConfirmButton();
			return;
		}

		string templateTitle = (string)selected.Tag;
		_selectedCompareTemplateTitle = templateTitle;
		var ct = _diffCts?.Token ?? CancellationToken.None;
		var modeAtStart = _currentMode;

		// Show diff: generated (left) vs selected template (right)
		pnlOutput.Visibility = Visibility.Collapsed;
		pnlDiff.Visibility = Visibility.Visible;
		pnlDiffLoading.Visibility = Visibility.Visible;
		pnlLeft.Children.Clear();
		pnlRight.Children.Clear();
		pnlCenter.Children.Clear();
		scrollLeft.ScrollToVerticalOffset(0);
		scrollRight.ScrollToVerticalOffset(0);

		try
		{
			string wikiContent;
			if (_rewardTemplatesCache != null && _rewardTemplatesCache.TryGetValue(templateTitle, out var cached))
				wikiContent = cached;
			else
				wikiContent = await MysteryWikiService.FetchPageContentAsync(templateTitle);
			if (ct.IsCancellationRequested || _currentMode != modeAtStart) return;

			if (string.IsNullOrEmpty(wikiContent))
			{
				pnlDiffLoading.Visibility = Visibility.Collapsed;
				pnlDiff.Visibility = Visibility.Collapsed;
				ShowPlainOutput();
				warningBar.Message = "Could not fetch template content.";
				warningBar.Severity = InfoBarSeverity.Warning;
				warningBar.IsOpen = true;
				return;
			}

			// Headers: left = selected template, right = generated
			SetDiffLeftLink(templateTitle);
			runDiffRightSuffix.Text = "";

			_lastWikiContent = wikiContent;
			if (_originalOutput == null)
				_originalOutput = _fullOutput;

			var diffs = MysteryWikiService.ComputeRewardLevelDiff(wikiContent, _fullOutput);
			pnlDiffLoading.Visibility = Visibility.Collapsed;

			bool allMatch = diffs.All(d => d.Type == DiffLineType.Match);
			_isDiffMode = true;
			BuildDiffView(diffs);
			btnCopy.IsEnabled = true;
			btnPublish.Visibility = _main.Settings.WikiVerified ? Visibility.Visible : Visibility.Collapsed;

			if (allMatch)
			{
				warningBar.Message = "Content matches selected template.";
				warningBar.Severity = InfoBarSeverity.Success;
				warningBar.IsOpen = true;
				btnPublish.Visibility = Visibility.Collapsed;
			}
			else
			{
				int added = diffs.Count(d => d.Type == DiffLineType.Added);
				int removed = diffs.Count(d => d.Type == DiffLineType.Removed);
				int modified = diffs.Count(d => d.Type == DiffLineType.Modified);
				var parts = new List<string>();
				if (added > 0) parts.Add($"{added} added");
				if (removed > 0) parts.Add($"{removed} removed");
				if (modified > 0) parts.Add($"{modified} modified");
				warningBar.Message = string.Join("  ", parts);
				warningBar.Severity = InfoBarSeverity.Informational;
				warningBar.IsOpen = true;
				// When comparing with a specific template → always offer to update it
				var shortName = templateTitle.Replace("Template:Mystery Pass/", "");
				btnPublish.Content = $"Update {shortName}";
				btnPublish.Visibility = _main.Settings.WikiVerified ? Visibility.Visible : Visibility.Collapsed;
			}
			UpdateConfirmButton();
		}
		catch (Exception ex)
		{
			pnlDiffLoading.Visibility = Visibility.Collapsed;
			pnlDiff.Visibility = Visibility.Collapsed;
			ShowPlainOutput();
			warningBar.Message = "Compare failed: " + ex.Message;
			warningBar.Severity = InfoBarSeverity.Error;
			warningBar.IsOpen = true;
		}
	}

	private void BtnClose_Click(object sender, RoutedEventArgs e)
	{
		Close();
	}

}
