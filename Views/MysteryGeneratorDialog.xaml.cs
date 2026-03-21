using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Text.RegularExpressions;
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
		if ((uint)(rewardState - 3) <= 1u)
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
		pnlOutput.Visibility = Visibility.Collapsed;
		pnlDiff.Visibility = Visibility.Collapsed;
		imagesControl.Visibility = Visibility.Collapsed;
		_isDiffMode = false;
		btnPublish.Content = "Publish to Wiki";
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
			MysteryGeneratorMode currentMode = _currentMode;
			if (1 == 0)
			{
			}
			string fullOutput = currentMode switch
			{
				MysteryGeneratorMode.Rewards => MysteryWikiService.GenerateRewardTemplate(_mystery, _mapping), 
				MysteryGeneratorMode.EventPage => MysteryWikiService.GenerateEventPageWithDialogues(_mystery, (_mystery.WikiStatus.RewardContentMatches == true) ? _mystery.WikiStatus.MatchingVariant : (_hypotheticalRewardVariant ?? _mystery.WikiStatus.MatchingVariant), _dialogueService), 
				MysteryGeneratorMode.EventItemPage => MysteryWikiService.GenerateEventItemPage(_mystery, _main.DataService, _main.WikiMapping), 
				_ => "", 
			};
			if (1 == 0)
			{
			}
			_fullOutput = fullOutput;
			MysteryGeneratorMode currentMode2 = _currentMode;
			if (1 == 0)
			{
			}
			bool flag = currentMode2 switch
			{
				MysteryGeneratorMode.EventPage => _mystery.WikiStatus.EventPageExists == true, 
				MysteryGeneratorMode.EventItemPage => _mystery.WikiStatus.EventItemPageExists == true, 
				MysteryGeneratorMode.Rewards => _mystery.WikiStatus.RewardTemplateMatches == true, 
				_ => false, 
			};
			if (1 == 0)
			{
			}
			if (flag)
			{
				LoadDiffAsync();
				return;
			}
			ShowPlainOutput();
			if (_currentMode == MysteryGeneratorMode.Rewards && _mystery.WikiStatus.RewardTemplateMatches != true)
			{
				btnPublish.Content = "Create New Reward Template";
			}
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

	private async Task LoadDiffAsync()
	{
		pnlDiff.Visibility = Visibility.Visible;
		pnlDiffLoading.Visibility = Visibility.Visible;
		pnlLeft.Children.Clear();
		pnlRight.Children.Clear();
		try
		{
			MysteryGeneratorMode currentMode = _currentMode;
			if (1 == 0)
			{
			}
			MysteryDiffScope mysteryDiffScope = currentMode switch
			{
				MysteryGeneratorMode.EventPage => MysteryDiffScope.EventPage, 
				MysteryGeneratorMode.Rewards => MysteryDiffScope.Rewards, 
				_ => MysteryDiffScope.EventItemPage, 
			};
			if (1 == 0)
			{
			}
			MysteryDiffScope scope = mysteryDiffScope;
			(string? WikiContent, string GeneratedContent, List<DiffLine> Diffs, string PageTitle) tuple = await MysteryWikiService.ComputeDiffAsync(_mystery, scope, _main.DataService, _main.WikiMapping, _mapping, _dialogueService);
			var (wikiContent, _, _, _) = tuple;
			_ = tuple.GeneratedContent;
			List<DiffLine> diffs = tuple.Diffs;
			string diffPageTitle = tuple.PageTitle;
			pnlDiffLoading.Visibility = Visibility.Collapsed;
			if (wikiContent == null)
			{
				pnlDiff.Visibility = Visibility.Collapsed;
				runDiffLeftSuffix.Text = "";
				runDiffRightSuffix.Text = "";
				ShowPlainOutput();
				return;
			}
			runDiffLeftSuffix.Text = (string.IsNullOrEmpty(diffPageTitle) ? "" : (" - " + diffPageTitle));
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
					btnPublish.Content = "Create New Reward Template";
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
			Exception ex2 = ex;
			pnlDiffLoading.Visibility = Visibility.Collapsed;
			pnlDiff.Visibility = Visibility.Collapsed;
			ShowPlainOutput();
			warningBar.Message = "Diff failed: " + ex2.Message;
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
			int item = item3[item3.Count / 2].Item1;
			if (item >= 0 && item < pnlRight.Children.Count)
			{
				HashSet<int> indices = new HashSet<int>(item3.Select<(int, int), int>(((int row, int di) g) => g.di));
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
				grid.ColumnDefinitions.Add(new ColumnDefinition
				{
					Width = GridLength.Auto
				});
				grid.ColumnDefinitions.Add(new ColumnDefinition
				{
					Width = new GridLength(1.0, GridUnitType.Star)
				});
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
				_savedRightElements[item] = pnlRight.Children[item];
				pnlRight.Children.RemoveAt(item);
				pnlRight.Children.Insert(item, border2);
			}
		}
	}

	private void ApplyMerge(HashSet<int> indicesToMerge)
	{
		if (_currentDiffs == null || indicesToMerge.Count == 0 || _lastWikiContent == null)
		{
			return;
		}
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
					list.Add(diffLine.Text);
					if (_pairedRemovedIndices.Contains(i) && i + 1 < _currentDiffs.Count && _currentDiffs[i + 1].Type == DiffLineType.Added)
					{
						i++;
					}
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
		HashSet<int> hashSet = new HashSet<int>();
		if (_originalOutput == null)
		{
			return hashSet;
		}
		List<DiffLine> list = MysteryWikiService.ComputeLineDiffs(_originalOutput, _fullOutput);
		int num = 0;
		foreach (DiffLine item in list)
		{
			if (item.Type == DiffLineType.Match)
			{
				num++;
			}
			else if (item.Type == DiffLineType.Added)
			{
				hashSet.Add(num);
				num++;
			}
			else if (item.Type == DiffLineType.Modified)
			{
				hashSet.Add(num);
				num++;
			}
		}
		return hashSet;
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
		string value = null;
		foreach (DiffLine item in list)
		{
			if (item.Type == DiffLineType.Match)
			{
				value = null;
				num++;
			}
			else if (item.Type == DiffLineType.Removed)
			{
				value = item.Text;
			}
			else if (item.Type == DiffLineType.Modified)
			{
				dictionary[num] = item.OldText;
				value = null;
				num++;
			}
			else if (item.Type == DiffLineType.Added)
			{
				dictionary[num] = value;
				value = null;
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

	private void UpdateConfirmButton()
	{
		if (_currentMode == MysteryGeneratorMode.Images)
		{
			btnConfirmManual.Visibility = Visibility.Collapsed;
			return;
		}
		MysteryManualConfirmFlags manualConfirm = _mystery.WikiStatus.ManualConfirm;
		MysteryGeneratorMode currentMode = _currentMode;
		if (1 == 0)
		{
		}
		bool flag = currentMode switch
		{
			MysteryGeneratorMode.EventPage => manualConfirm.EventPageConfirmed, 
			MysteryGeneratorMode.Rewards => manualConfirm.RewardsConfirmed, 
			MysteryGeneratorMode.EventItemPage => manualConfirm.ItemPageConfirmed, 
			_ => false, 
		};
		if (1 == 0)
		{
		}
		bool flag2 = flag;
		MysteryGeneratorMode currentMode2 = _currentMode;
		if (1 == 0)
		{
		}
		WikiCheckState wikiCheckState = currentMode2 switch
		{
			MysteryGeneratorMode.EventPage => _mystery.WikiStatus.EventPageState, 
			MysteryGeneratorMode.Rewards => _mystery.WikiStatus.RewardTemplateState, 
			MysteryGeneratorMode.EventItemPage => _mystery.WikiStatus.EventItemPageState, 
			_ => WikiCheckState.Unknown, 
		};
		if (1 == 0)
		{
		}
		WikiCheckState wikiCheckState2 = wikiCheckState;
		if (flag2)
		{
			btnConfirmManual.Content = "Remove Confirmation";
			btnConfirmManual.Visibility = Visibility.Visible;
			btnPublish.IsEnabled = false;
			MysteryGeneratorMode currentMode3 = _currentMode;
			if (1 == 0)
			{
			}
			string text = currentMode3 switch
			{
				MysteryGeneratorMode.EventPage => "Event Page", 
				MysteryGeneratorMode.Rewards => "Rewards", 
				MysteryGeneratorMode.EventItemPage => "Event Item Page", 
				_ => "", 
			};
			if (1 == 0)
			{
			}
			string text2 = text;
			btnPublish.ToolTip = "Publish disabled: " + text2 + " is manually confirmed as correct. Remove confirmation first.";
			ToolTipService.SetInitialShowDelay(btnPublish, 0);
			ToolTipService.SetShowOnDisabled(btnPublish, value: true);
		}
		else
		{
			btnPublish.IsEnabled = true;
			btnPublish.ToolTip = null;
			if (wikiCheckState2 == WikiCheckState.Mismatch)
			{
				btnConfirmManual.Content = "Confirm as Correct";
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
		MysteryGeneratorMode currentMode = _currentMode;
		if (1 == 0)
		{
		}
		string text = currentMode switch
		{
			MysteryGeneratorMode.EventPage => "eventPageManualConfirm", 
			MysteryGeneratorMode.Rewards => "rewardsManualConfirm", 
			MysteryGeneratorMode.EventItemPage => "itemPageConfirmed", 
			_ => "", 
		};
		if (1 == 0)
		{
		}
		string flagName = text;
		if (string.IsNullOrEmpty(flagName))
		{
			return;
		}
		MysteryGeneratorMode currentMode2 = _currentMode;
		if (1 == 0)
		{
		}
		bool flag = currentMode2 switch
		{
			MysteryGeneratorMode.EventPage => flags.EventPageConfirmed, 
			MysteryGeneratorMode.Rewards => flags.RewardsConfirmed, 
			MysteryGeneratorMode.EventItemPage => flags.ItemPageConfirmed, 
			_ => false, 
		};
		if (1 == 0)
		{
		}
		bool currentValue = flag;
		bool newValue = !currentValue;
		string action = (newValue ? "Set" : "Remove");
		MysteryGeneratorMode currentMode3 = _currentMode;
		if (1 == 0)
		{
		}
		text = currentMode3 switch
		{
			MysteryGeneratorMode.EventPage => "Event Page", 
			MysteryGeneratorMode.Rewards => "Rewards", 
			MysteryGeneratorMode.EventItemPage => "Event Item Page", 
			_ => "", 
		};
		if (1 == 0)
		{
		}
		string sectionName = text;
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
			if (newValue)
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
			if ((await MysteryWikiService.SetManualConfirmFlagAsync(_main.Settings.WikiUsername, _main.Settings.WikiPassword, _mystery, flagName, newValue)).Item3)
			{
				switch (_currentMode)
				{
				case MysteryGeneratorMode.EventPage:
					flags.EventPageConfirmed = newValue;
					break;
				case MysteryGeneratorMode.Rewards:
					flags.RewardsConfirmed = newValue;
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
		catch (Exception ex2)
		{
			Exception ex3 = ex2;
			warningBar.Message = "Publish failed: " + ex3.Message;
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
			bool flag3 = (uint)(rewardTemplateState - 3) <= 1u;
			flag2 = !flag3;
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
		btnPublish.IsEnabled = false;
		warningBar.Message = "Publishing to " + pageTitle + "...";
		warningBar.Severity = InfoBarSeverity.Informational;
		warningBar.IsOpen = true;
		bool published = false;
		try
		{
			MysteryGeneratorMode currentMode = _currentMode;
			if (1 == 0)
			{
			}
			string text = currentMode switch
			{
				MysteryGeneratorMode.Rewards => "Create reward template (via MergeMansionWikiTools)", 
				MysteryGeneratorMode.EventPage => "Create/update mystery page (via MergeMansionWikiTools)", 
				MysteryGeneratorMode.EventItemPage => "Create/update event item page (via MergeMansionWikiTools)", 
				_ => "Edit via MergeMansionWikiTools", 
			};
			if (1 == 0)
			{
			}
			string summary = text;
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
				break;
			case MysteryGeneratorMode.Rewards:
			{
				_mystery.WikiStatus.RewardTemplateMatches = true;
				_mystery.WikiStatus.RewardContentMatches = true;
				string rewardsPrefix = "Template:Mystery Pass/Rewards";
				if (pageTitle.StartsWith(rewardsPrefix))
				{
					text = pageTitle;
					int length = rewardsPrefix.Length;
					string after = text.Substring(length, text.Length - length).TrimStart('/');
					_mystery.WikiStatus.MatchingVariant = (string.IsNullOrEmpty(after) ? "" : after);
				}
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
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			warningBar.Message = "Publish failed: " + ex2.Message;
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

	private void BtnClose_Click(object sender, RoutedEventArgs e)
	{
		Close();
	}

}
