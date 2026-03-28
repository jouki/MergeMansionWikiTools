using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using MergeMansionWikiTools.Models;
using MergeMansionWikiTools.Services;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace MergeMansionWikiTools.Views;

public partial class MysteriesPage : UserControl
{
	private readonly MainWindow _main;

	private MysteryService? _mysteryService;

	private MysteryItemMapping? _itemMapping;

	private DialogueService? _dialogueService;

	private bool _loaded;

	public MysteriesPage(MainWindow main)
	{
		_main = main;
		InitializeComponent();
		_itemMapping = MysteryService.LoadMapping();
		TryUsePreloaded();
		MysteryWikiService.LoadPetDisplayNames(_main.Settings.ImageExporterBasePath, _main.Settings.SelectedApkVersion);
		TryLoadDialoguesAsync();
		_main.EventsFileChanged += delegate
		{
			base.Dispatcher.InvokeAsync(delegate
			{
				_loaded = false;
				_mysteryService = null;
				_dialogueService = null;
				mysteryListPanel.Children.Clear();
				TryUsePreloaded();
				TryLoadDialoguesAsync();
			});
		};
	}

	private async Task TryLoadDialoguesAsync()
	{
		List<string> candidates = new List<string>();
		string eventsPath = _main.Settings.EventsJsonPath;
		if (!string.IsNullOrEmpty(eventsPath))
		{
			string dir = Path.GetDirectoryName(eventsPath);
			if (!string.IsNullOrEmpty(dir))
			{
				candidates.Add(Path.Combine(dir, "dialogues.json"));
			}
		}
		if (!string.IsNullOrEmpty(_main.Settings.DumperOutputPath))
		{
			candidates.Add(Path.Combine(_main.Settings.DumperOutputPath, "dialogues.json"));
		}
		if (!string.IsNullOrEmpty(_main.Settings.ImageExporterBasePath) && !string.IsNullOrEmpty(_main.Settings.SelectedApkVersion))
		{
			string dumpDir = Path.Combine(_main.Settings.ImageExporterBasePath, _main.Settings.SelectedApkVersion, "Dump");
			candidates.Add(Path.Combine(dumpDir, "dialogues.json"));
		}
		AppLogger.Info($"DialogueService: searching {candidates.Count} candidates: {string.Join(", ", candidates)}");
		string dialoguesPath = candidates.FirstOrDefault(File.Exists);
		if (dialoguesPath == null)
		{
			AppLogger.Warn("DialogueService: dialogues.json not found in any candidate path");
			return;
		}
		try
		{
			_dialogueService = new DialogueService();
			await _dialogueService.LoadAsync(dialoguesPath);
			AppLogger.Info("DialogueService loaded from: " + dialoguesPath);
		}
		catch (Exception ex)
		{
			AppLogger.Warn("Failed to load dialogues.json: " + ex.Message);
			_dialogueService = null;
		}
	}

	private void TryUsePreloaded()
	{
		if (_main.MysteryService != null && _main.MysteryService.Mysteries.Count > 0)
		{
			_mysteryService = _main.MysteryService;
			if (_main.DataService != null)
			{
				_mysteryService.ResolveRewardItems(_main.DataService, _main.WikiMapping, _itemMapping);
			}
			MysteryWikiService.ApplyCachedStatus(_mysteryService.Mysteries, _main.DataService);
			_loaded = true;
			emptyState.Visibility = Visibility.Collapsed;
			BuildMysteryList();
			AutoCheckNonGreenAsync();
		}
		else
		{
			TryLoadAsync();
		}
	}

	private async Task TryLoadAsync()
	{
		string path = _main.Settings.EventsJsonPath;
		if (string.IsNullOrEmpty(path) || !File.Exists(path))
		{
			emptyState.Visibility = Visibility.Visible;
			txtSummary.Text = "";
			return;
		}
		emptyState.Visibility = Visibility.Collapsed;
		try
		{
			_mysteryService = new MysteryService();
			await _mysteryService.LoadAsync(path);
			if (_main.DataService != null)
			{
				_mysteryService.ResolveEventItems(_main.DataService);
				_mysteryService.ResolveRewardItems(_main.DataService, _main.WikiMapping, _itemMapping);
			}
			_main.MysteryService = _mysteryService;
			MysteryWikiService.ApplyCachedStatus(_mysteryService.Mysteries, _main.DataService);
			_loaded = true;
			BuildMysteryList();
		}
		catch (Exception ex)
		{
			ShowInfo("Failed to load events.json: " + ex.Message, InfoBarSeverity.Error);
			emptyState.Visibility = Visibility.Visible;
		}
	}

	private async Task AutoCheckNonGreenAsync()
	{
		if (_mysteryService == null)
		{
			return;
		}
		try
		{
			if (_dialogueService == null)
			{
				await TryLoadDialoguesAsync();
			}
			await MysteryWikiService.CheckAllMysteryStatusAsync(_mysteryService.Mysteries, _main.DataService, _dialogueService);
			BuildMysteryList();
		}
		catch
		{
		}
	}

	private void BuildMysteryList()
	{
		mysteryListPanel.Children.Clear();
		if (_mysteryService == null || _mysteryService.Mysteries.Count == 0)
		{
			emptyState.Visibility = Visibility.Visible;
			txtSummary.Text = "";
			return;
		}
		emptyState.Visibility = Visibility.Collapsed;
		string search = txtSearch.Text?.Trim() ?? "";
		List<MysteryEvent> list = (string.IsNullOrEmpty(search) ? _mysteryService.Mysteries : _mysteryService.Mysteries.Where(delegate(MysteryEvent m)
		{
			int result;
			if (!m.Name.Contains(search, StringComparison.OrdinalIgnoreCase))
			{
				string? eventItemName = m.EventItemName;
				if (eventItemName == null || !eventItemName.Contains(search, StringComparison.OrdinalIgnoreCase))
				{
					result = (m.ProgressionEventId.Contains(search, StringComparison.OrdinalIgnoreCase) ? 1 : 0);
					goto IL_0044;
				}
			}
			result = 1;
			goto IL_0044;
			IL_0044:
			return (byte)result != 0;
		}).ToList());
		int count = _mysteryService.Mysteries.Count;
		int value = _mysteryService.Mysteries.Count((MysteryEvent m) => m.MysteryType == MysteryType.Standard);
		int value2 = _mysteryService.Mysteries.Count((MysteryEvent m) => m.MysteryType == MysteryType.Pet);
		txtSummary.Text = $"{count} mysteries  {value} Standard  {value2} Pet" + ((list.Count != count) ? $"  {list.Count} shown" : "");
		if (list.Count == 0)
		{
			System.Windows.Controls.TextBlock textBlock = new System.Windows.Controls.TextBlock
			{
				Text = "No mysteries match your search.",
				FontSize = 13.0,
				Margin = new Thickness(4.0, 20.0, 0.0, 0.0)
			};
			textBlock.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
			mysteryListPanel.Children.Add(textBlock);
			return;
		}
		int? num = null;
		foreach (MysteryEvent item in list)
		{
			int? num2 = item.StartDate?.Year;
			if (num2 != num)
			{
				System.Windows.Controls.TextBlock textBlock2 = new System.Windows.Controls.TextBlock
				{
					Text = (num2?.ToString() ?? "Unknown date"),
					FontSize = 18.0,
					FontWeight = FontWeights.SemiBold,
					Margin = new Thickness(0.0, num.HasValue ? 16 : 0, 0.0, 8.0)
				};
				textBlock2.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
				mysteryListPanel.Children.Add(textBlock2);
				Border border = new Border
				{
					Height = 1.0,
					Margin = new Thickness(0.0, 0.0, 0.0, 10.0)
				};
				border.SetResourceReference(Border.BackgroundProperty, "DividerStrokeColorDefaultBrush");
				mysteryListPanel.Children.Add(border);
				num = num2;
			}
			Border element = CreateMysteryCard(item);
			mysteryListPanel.Children.Add(element);
		}
	}

	private Border CreateMysteryCard(MysteryEvent mystery)
	{
		Border border = new Border
		{
			CornerRadius = new CornerRadius(6.0),
			BorderThickness = new Thickness(1.0),
			Margin = new Thickness(0.0, 0.0, 0.0, 8.0),
			Padding = new Thickness(16.0, 12.0, 16.0, 12.0)
		};
		border.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
		border.SetResourceReference(Border.BorderBrushProperty, "CardStrokeColorDefaultBrush");
		Grid grid = new Grid();
		grid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = new GridLength(1.0, GridUnitType.Star)
		});
		grid.ColumnDefinitions.Add(new ColumnDefinition
		{
			Width = GridLength.Auto
		});
		StackPanel stackPanel = new StackPanel
		{
			VerticalAlignment = VerticalAlignment.Center
		};
		StackPanel stackPanel2 = new StackPanel
		{
			Orientation = Orientation.Horizontal
		};
		System.Windows.Controls.TextBlock textBlock = new System.Windows.Controls.TextBlock
		{
			Text = mystery.Name,
			FontSize = 14.0,
			FontWeight = FontWeights.SemiBold,
			VerticalAlignment = VerticalAlignment.Center
		};
		textBlock.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
		stackPanel2.Children.Add(textBlock);
		Border border2 = new Border
		{
			CornerRadius = new CornerRadius(4.0),
			Padding = new Thickness(6.0, 2.0, 6.0, 2.0),
			Margin = new Thickness(8.0, 0.0, 0.0, 0.0),
			VerticalAlignment = VerticalAlignment.Center
		};
		border2.SetResourceReference(Border.BackgroundProperty, (mystery.MysteryType == MysteryType.Pet) ? "AccentFillColorDefaultBrush" : "SubtleFillColorSecondaryBrush");
		System.Windows.Controls.TextBlock textBlock2 = new System.Windows.Controls.TextBlock
		{
			Text = ((mystery.MysteryType == MysteryType.Pet) ? "Pet" : "Standard"),
			FontSize = 11.0
		};
		textBlock2.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, (mystery.MysteryType == MysteryType.Pet) ? "TextOnAccentFillColorPrimaryBrush" : "TextFillColorSecondaryBrush");
		border2.Child = textBlock2;
		stackPanel2.Children.Add(border2);
		stackPanel.Children.Add(stackPanel2);
		string text = mystery.StartDate?.ToString("MMM d, yyyy") ?? "Unknown date";
		string text2 = mystery.EventItemName ?? "Unknown item";
		System.Windows.Controls.TextBlock textBlock3 = new System.Windows.Controls.TextBlock
		{
			Text = text + "  Event Item: " + text2,
			FontSize = 11.0,
			Margin = new Thickness(0.0, 3.0, 0.0, 0.0),
			TextTrimming = TextTrimming.CharacterEllipsis
		};
		textBlock3.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
		stackPanel.Children.Add(textBlock3);
		StackPanel stackPanel3 = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Margin = new Thickness(0.0, 4.0, 0.0, 0.0)
		};
		WikiCheckState eventPageState = mystery.WikiStatus.EventPageState;
		Border border3 = CreateStatusIndicator("Page", eventPageState);
		border3.Cursor = Cursors.Hand;
		border3.Tag = (mystery, MysteryDiffScope.EventPage);
		border3.MouseLeftButtonDown += StatusIndicator_Click;
		ToolTipService.SetInitialShowDelay(border3, 0);
		border3.ToolTip = "Click to diff Event Page";
		stackPanel3.Children.Add(border3);
		WikiCheckState rewardTemplateState = mystery.WikiStatus.RewardTemplateState;
		string label = ((!string.IsNullOrEmpty(mystery.WikiStatus.MatchingVariant)) ? ("Rewards (" + mystery.WikiStatus.MatchingVariant + ")") : "Rewards");
		Border border4 = CreateStatusIndicator(label, rewardTemplateState);
		border4.Cursor = Cursors.Hand;
		border4.Tag = (mystery, MysteryDiffScope.Rewards);
		border4.MouseLeftButtonDown += StatusIndicator_Click;
		ToolTipService.SetInitialShowDelay(border4, 0);
		border4.ToolTip = "Click to diff Rewards";
		stackPanel3.Children.Add(border4);
		WikiCheckState eventItemPageState = mystery.WikiStatus.EventItemPageState;
		Border border5 = CreateStatusIndicator("Item", eventItemPageState);
		border5.Cursor = Cursors.Hand;
		border5.Tag = (mystery, MysteryDiffScope.EventItemPage);
		border5.MouseLeftButtonDown += StatusIndicator_Click;
		ToolTipService.SetInitialShowDelay(border5, 0);
		border5.ToolTip = "Click to diff Event Item Page";
		stackPanel3.Children.Add(border5);
		WikiCheckState imagesState = mystery.WikiStatus.ImagesState;
		string label2 = ((mystery.WikiStatus.ImagesExistOnWiki > 0) ? $"Images ({mystery.WikiStatus.ImagesExistOnWiki}/{mystery.WikiStatus.ImagesTotalExpected})" : "Images");
		Border border6 = CreateStatusIndicator(label2, imagesState);
		border6.Cursor = Cursors.Hand;
		border6.Tag = (mystery, MysteryDiffScope.EventPage);
		border6.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs _)
		{
			if (s is Border border8)
			{
				MysteryEvent item = (((MysteryEvent, MysteryDiffScope))border8.Tag).Item1;
				OpenPrepareDialog(item, MysteryGeneratorMode.Images);
			}
		};
		ToolTipService.SetInitialShowDelay(border6, 0);
		border6.ToolTip = "Click to open Images";
		stackPanel3.Children.Add(border6);
		mystery.WikiStatus.UpdateMainPageEligibility(mystery.StartDate);
		WikiCheckState wikiListedState = mystery.WikiStatus.WikiListedState;
		int wikiListedCount = mystery.WikiStatus.WikiListedCount;
		int wikiListedTotal = mystery.WikiStatus.WikiListedTotal;
		string label3 = ((wikiListedState == WikiCheckState.Unknown) ? "Wiki" : $"Wiki ({wikiListedCount}/{wikiListedTotal})");
		Border border7 = CreateStatusIndicator(label3, wikiListedState);
		ToolTipService.SetInitialShowDelay(border7, 0);
		List<string> list = new List<string>();
		list.Add("Main page: " + ((mystery.WikiStatus.WikiMainPageListed == true) ? "\u2713" : ((mystery.WikiStatus.WikiMainPageListed == false) ? "\u2717" : "?")));
		list.Add("Mystery table: " + ((mystery.WikiStatus.WikiMysteryTableListed == true) ? "\u2713" : ((mystery.WikiStatus.WikiMysteryTableListed == false) ? "\u2717" : "?")));
		list.Add("Module: " + ((mystery.WikiStatus.WikiModuleListed == true) ? "\u2713" : ((mystery.WikiStatus.WikiModuleListed == false) ? "\u2717" : "?")));
		border7.ToolTip = string.Join("\n", list);
		stackPanel3.Children.Add(border7);
		if (stackPanel3.Children.Count > 0)
		{
			stackPanel.Children.Add(stackPanel3);
		}
		Grid.SetColumn(stackPanel, 0);
		grid.Children.Add(stackPanel);
		StackPanel stackPanel4 = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			VerticalAlignment = VerticalAlignment.Center
		};
		// Per-mystery refresh button (icon only)
		Wpf.Ui.Controls.Button btnRefreshSingle = new Wpf.Ui.Controls.Button
		{
			Icon = new SymbolIcon { Symbol = SymbolRegular.ArrowClockwise24 },
			Appearance = ControlAppearance.Secondary,
			Height = 32.0,
			Width = 32.0,
			Padding = new Thickness(0),
			Margin = new Thickness(0, 0, 4, 0),
			Tag = mystery,
			ToolTip = "Refresh wiki status for this mystery"
		};
		ToolTipService.SetInitialShowDelay(btnRefreshSingle, 0);
		btnRefreshSingle.Click += BtnRefreshSingle_Click;
		stackPanel4.Children.Add(btnRefreshSingle);
		Wpf.Ui.Controls.Button button = new Wpf.Ui.Controls.Button
		{
			Content = "Prepare",
			Appearance = ControlAppearance.Secondary,
			Height = 32.0,
			Margin = new Thickness(0, 0, 0, 0),
			Tag = mystery
		};
		button.Click += BtnPrepare_Click;
		stackPanel4.Children.Add(button);
		if (_main.Settings.WikiVerified)
		{
			Wpf.Ui.Controls.Button button2 = new Wpf.Ui.Controls.Button
			{
				Content = "Update Wiki",
				Appearance = ControlAppearance.Primary,
				Height = 32.0,
				Margin = new Thickness(4.0, 0.0, 0.0, 0.0),
				Tag = mystery
			};
			button2.Click += BtnUpdateWiki_Click;
			stackPanel4.Children.Add(button2);
		}
		Grid.SetColumn(stackPanel4, 1);
		grid.Children.Add(stackPanel4);
		border.Child = grid;
		return border;
	}

	private static Border CreateStatusIndicator(string label, WikiCheckState state)
	{
		if (1 == 0)
		{
		}
		(SolidColorBrush, SolidColorBrush, string) tuple = state switch
		{
			WikiCheckState.Match => (new SolidColorBrush(Color.FromArgb(48, 0, 160, 0)), new SolidColorBrush(Color.FromRgb(48, 192, 48)), "\u2713 "),
			WikiCheckState.Confirmed => (new SolidColorBrush(Color.FromArgb(37, 64, 128, 224)), new SolidColorBrush(Color.FromRgb(96, 160, 240)), "\u2713 "),
			WikiCheckState.Mismatch => (new SolidColorBrush(Color.FromArgb(48, 192, 144, 0)), new SolidColorBrush(Color.FromRgb(208, 160, 32)), "\u26a0 "),
			WikiCheckState.Missing => (new SolidColorBrush(Color.FromArgb(48, 208, 0, 0)), new SolidColorBrush(Color.FromRgb(208, 64, 64)), "\u2717 "),
			_ => (new SolidColorBrush(Color.FromArgb(32, 128, 128, 128)), new SolidColorBrush(Color.FromRgb(144, 144, 144)), "? "),
		};
		if (1 == 0)
		{
		}
		(SolidColorBrush, SolidColorBrush, string) tuple2 = tuple;
		SolidColorBrush item = tuple2.Item1;
		SolidColorBrush item2 = tuple2.Item2;
		string item3 = tuple2.Item3;
		Border border = new Border
		{
			CornerRadius = new CornerRadius(3.0),
			Padding = new Thickness(5.0, 1.0, 5.0, 1.0),
			Margin = new Thickness(0.0, 0.0, 6.0, 0.0),
			Background = item
		};
		System.Windows.Controls.TextBlock textBlock = new System.Windows.Controls.TextBlock
		{
			FontSize = 10.0,
			Foreground = item2
		};
		textBlock.Inlines.Add(new Run(item3));
		textBlock.Inlines.Add(new Run(label));
		border.Child = textBlock;
		return border;
	}

	private void TxtSearch_TextChanged(object sender, TextChangedEventArgs e)
	{
		if (_loaded)
		{
			BuildMysteryList();
		}
	}

	private async void BtnRefresh_Click(object sender, RoutedEventArgs e)
	{
		if (_mysteryService != null)
		{
			MysteryWikiService.ResetMatchedLabelsFromMemory(_mysteryService.Mysteries);
		}
		_loaded = false;
		_mysteryService = null;
		mysteryListPanel.Children.Clear();
		await TryLoadAsync();
		ShowInfo("Green labels reset to Unknown. Click 'Check Wiki Status' to re-verify.", InfoBarSeverity.Success);
	}

	private async void BtnCheckWiki_Click(object sender, RoutedEventArgs e)
	{
		if (_mysteryService == null || _mysteryService.Mysteries.Count == 0)
		{
			ShowInfo("No mysteries loaded.", InfoBarSeverity.Warning);
			return;
		}
		btnCheckWiki.IsEnabled = false;
		checkWikiProgress.Visibility = Visibility.Visible;
		ShowInfo("Checking wiki status...", InfoBarSeverity.Informational, autoClose: false);
		try
		{
			if (_dialogueService == null)
			{
				await TryLoadDialoguesAsync();
			}
			await MysteryWikiService.CheckAllMysteryStatusAsync(_mysteryService.Mysteries, _main.DataService, _dialogueService);
			BuildMysteryList();

			ShowInfo("Wiki status checked.", InfoBarSeverity.Success);
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			ShowInfo("Wiki check failed: " + ex2.Message, InfoBarSeverity.Error);
		}
		finally
		{
			btnCheckWiki.IsEnabled = true;
			checkWikiProgress.Visibility = Visibility.Collapsed;
		}
	}

	private async void BtnRefreshSingle_Click(object sender, RoutedEventArgs e)
	{
		if (sender is not Wpf.Ui.Controls.Button { Tag: MysteryEvent mystery } btn)
			return;
		if (_mysteryService == null || _mysteryService.Mysteries.Count == 0)
			return;

		// Replace icon with spinner inside the same button
		var savedIcon = btn.Icon;
		btn.Icon = null;
		btn.Content = new ProgressRing { Width = 16, Height = 16, IsIndeterminate = true };
		btn.IsEnabled = false;

		try
		{
			MysteryWikiService.ResetMatchedLabelsFromMemory(new[] { mystery });

			if (_dialogueService == null)
				await TryLoadDialoguesAsync();

			await MysteryWikiService.CheckSingleMysteryStatusAsync(mystery, _mysteryService.Mysteries, _main.DataService, _dialogueService);
			BuildMysteryList();
		}
		catch (Exception ex)
		{
			ShowInfo($"Check failed for {mystery.Name}: {ex.Message}", InfoBarSeverity.Error);
			// Restore button state on error (BuildMysteryList won't be called)
			btn.Content = null;
			btn.Icon = savedIcon;
			btn.IsEnabled = true;
		}
	}

	private void BtnPrepare_Click(object sender, RoutedEventArgs e)
	{
		if (sender is Wpf.Ui.Controls.Button { Tag: MysteryEvent tag })
		{
			OpenPrepareDialog(tag, MysteryGeneratorMode.EventPage);
		}
	}

	private void StatusIndicator_Click(object sender, MouseButtonEventArgs e)
	{
		if (sender is Border border)
		{
			var (mystery, mysteryDiffScope) = ((MysteryEvent, MysteryDiffScope))border.Tag;
			if (1 == 0)
			{
			}
			MysteryGeneratorMode mysteryGeneratorMode = mysteryDiffScope switch
			{
				MysteryDiffScope.Rewards => MysteryGeneratorMode.Rewards, 
				MysteryDiffScope.EventPage => MysteryGeneratorMode.EventPage, 
				MysteryDiffScope.EventItemPage => MysteryGeneratorMode.EventItemPage, 
				_ => MysteryGeneratorMode.Rewards, 
			};
			if (1 == 0)
			{
			}
			MysteryGeneratorMode mode = mysteryGeneratorMode;
			OpenPrepareDialog(mystery, mode);
		}
	}

	private void OpenPrepareDialog(MysteryEvent mystery, MysteryGeneratorMode mode)
	{
		MysteryGeneratorDialog mysteryGeneratorDialog = new MysteryGeneratorDialog(_main, mystery, _itemMapping, mode, _dialogueService);
		mysteryGeneratorDialog.OnStatusChanged = delegate
		{
			base.Dispatcher.InvokeAsync(BuildMysteryList);
		};
		mysteryGeneratorDialog.OnRewardTemplateCreated = async delegate
		{
			try
			{
				Dictionary<string, string> templates = await MysteryWikiService.FetchRewardTemplatesAsync();
				if (_mysteryService != null)
				{
					foreach (MysteryEvent m in _mysteryService.Mysteries)
					{
						WikiCheckState rewardTemplateState = m.WikiStatus.RewardTemplateState;
						if ((uint)(rewardTemplateState - 3) > 1u)
						{
							var (matches, variant) = MysteryWikiService.CompareWithTemplates(m, templates);
							m.WikiStatus.RewardTemplateMatches = matches;
							m.WikiStatus.RewardContentMatches = matches;
							m.WikiStatus.MatchingVariant = variant;
							MysteryWikiService.UpdateSingleMysteryCache(m);
						}
					}
					await base.Dispatcher.InvokeAsync(BuildMysteryList);
				}
			}
			catch
			{
			}
		};
		mysteryGeneratorDialog.Owner = Window.GetWindow(this);
		mysteryGeneratorDialog.Show();
	}

	private async void BtnUpdateWiki_Click(object sender, RoutedEventArgs e)
	{
		Wpf.Ui.Controls.Button btn = sender as Wpf.Ui.Controls.Button;
		MysteryEvent mystery = default(MysteryEvent);
		int num;
		if (btn != null)
		{
			object tag = btn.Tag;
			mystery = tag as MysteryEvent;
			num = ((mystery == null) ? 1 : 0);
		}
		else
		{
			num = 1;
		}
		if (num != 0)
		{
			return;
		}
		if (!_main.Settings.WikiVerified)
		{
			ShowInfo("Wiki account not verified.", InfoBarSeverity.Warning);
			return;
		}
		btn.IsEnabled = false;
		ShowInfo("Fetching preview...", InfoBarSeverity.Informational, autoClose: false);
		List<MysteryUpdateStep> steps;
		try
		{
			steps = await MysteryWikiService.PreviewWikiUpdatesAsync(mystery);
		}
		catch (Exception ex)
		{
			Exception ex2 = ex;
			ShowInfo("Preview failed: " + ex2.Message, InfoBarSeverity.Error);
			btn.IsEnabled = true;
			return;
		}
		infoBar.IsOpen = false;
		Wpf.Ui.Controls.MessageBox previewBox = BuildUpdatePreviewDialog(mystery, steps);
		if (await previewBox.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary)
		{
			btn.IsEnabled = true;
			return;
		}
		ShowInfo("Updating wiki pages...", InfoBarSeverity.Informational, autoClose: false);
		List<string> results = new List<string>();
		try
		{
			if (steps[0].IsEnabled)
			{
				try
				{
					results.Add("Main page: " + await MysteryWikiService.UpdateMainPageAsync(_main.Settings.WikiUsername, _main.Settings.WikiPassword, mystery.Name, mystery.WikiStatus.SuggestedPageTitle ?? mystery.Name, mystery.StartDate));
					mystery.WikiStatus.WikiMainPageListed = true;
				}
				catch (Exception ex)
				{
					Exception ex3 = ex;
					results.Add("Main page: " + ex3.Message);
				}
			}
			if (steps[1].IsEnabled)
			{
				try
				{
					results.Add("Mystery page: " + await MysteryWikiService.UpdateMysteryPageTableAsync(_main.Settings.WikiUsername, _main.Settings.WikiPassword, mystery));
					mystery.WikiStatus.WikiMysteryTableListed = true;
				}
				catch (Exception ex)
				{
					Exception ex4 = ex;
					results.Add("Mystery page: " + ex4.Message);
				}
			}
			if (steps[2].IsEnabled)
			{
				try
				{
					results.Add("Module: " + await MysteryWikiService.UpdateMysteryTableAsync(_main.Settings.WikiUsername, _main.Settings.WikiPassword, mystery));
					mystery.WikiStatus.WikiModuleListed = true;
				}
				catch (Exception ex)
				{
					Exception ex5 = ex;
					results.Add("Module: " + ex5.Message);
				}
			}
			if (results.Count == 0)
			{
				ShowInfo("No steps selected.", InfoBarSeverity.Informational);
				return;
			}
			ShowInfo(string.Join(" | ", results), InfoBarSeverity.Success);
			MysteryWikiService.UpdateSingleMysteryCache(mystery);
			BuildMysteryList();
		}
		catch (Exception ex)
		{
			Exception ex6 = ex;
			ShowInfo("Update failed: " + ex6.Message, InfoBarSeverity.Error);
		}
		finally
		{
			btn.IsEnabled = true;
		}
	}

	private Wpf.Ui.Controls.MessageBox BuildUpdatePreviewDialog(MysteryEvent mystery, List<MysteryUpdateStep> steps)
	{
		Brush foreground = (Brush)FindResource("TextFillColorPrimaryBrush");
		Brush brush = (Brush)FindResource("TextFillColorSecondaryBrush");
		Brush brush2 = (Brush)FindResource("TextFillColorTertiaryBrush");
		Brush background = (Brush)FindResource("SubtleFillColorSecondaryBrush");
		SolidColorBrush foreground2 = new SolidColorBrush(Color.FromRgb(48, 192, 48));
		StackPanel stackPanel = new StackPanel
		{
			Margin = new Thickness(0.0, 0.0, 0.0, 10.0)
		};
		string text = mystery.WikiStatus.SuggestedPageTitle ?? mystery.Name;
		string value = mystery.StartDate?.ToString("MMM d, yyyy") ?? "Unknown";
		string value2 = ((mystery.MysteryType == MysteryType.Pet) ? "Pet" : "Standard");
		stackPanel.Children.Add(new System.Windows.Controls.TextBlock
		{
			Text = $"{mystery.Name}  {value2}  {value}",
			FontSize = 13.0,
			Foreground = brush,
			Margin = new Thickness(0.0, 0.0, 0.0, 2.0)
		});
		if (text != mystery.Name)
		{
			stackPanel.Children.Add(new System.Windows.Controls.TextBlock
			{
				Text = "Page title: " + text,
				FontSize = 11.0,
				Foreground = brush2,
				Margin = new Thickness(0.0, 0.0, 0.0, 4.0)
			});
		}
		stackPanel.Children.Add(new Border
		{
			Height = 1.0,
			Margin = new Thickness(0.0, 6.0, 0.0, 10.0),
			Background = (Brush)FindResource("ControlStrokeColorDefaultBrush")
		});
		int num = steps.Count((MysteryUpdateStep s) => !s.IsNoChange);
		System.Windows.Controls.TextBlock summaryText = new System.Windows.Controls.TextBlock
		{
			FontSize = 13.0,
			Foreground = brush,
			Margin = new Thickness(0.0, 0.0, 0.0, 10.0)
		};
		stackPanel.Children.Add(summaryText);
		List<(CheckBox cb, MysteryUpdateStep step)> checkboxes = new List<(CheckBox, MysteryUpdateStep)>();
		Wpf.Ui.Controls.MessageBox dialogRef = null;
		foreach (MysteryUpdateStep step in steps)
		{
			Border border = new Border
			{
				Background = background,
				CornerRadius = new CornerRadius(6.0),
				Padding = new Thickness(12.0, 8.0, 12.0, 8.0),
				Margin = new Thickness(0.0, 0.0, 0.0, 6.0)
			};
			StackPanel stackPanel2 = new StackPanel();
			Grid grid = new Grid();
			grid.ColumnDefinitions.Add(new ColumnDefinition
			{
				Width = GridLength.Auto
			});
			grid.ColumnDefinitions.Add(new ColumnDefinition
			{
				Width = new GridLength(24.0)
			});
			grid.ColumnDefinitions.Add(new ColumnDefinition
			{
				Width = new GridLength(1.0, GridUnitType.Star)
			});
			CheckBox checkBox = new CheckBox
			{
				IsChecked = step.IsEnabled,
				IsEnabled = !step.IsNoChange && step.DisabledReason == null,
				VerticalAlignment = VerticalAlignment.Top,
				Margin = new Thickness(0.0, 0.0, 6.0, 0.0),
				Visibility = (step.IsNoChange ? Visibility.Hidden : Visibility.Visible)
			};
			MysteryUpdateStep capturedStep = step;
			checkBox.Checked += delegate
			{
				capturedStep.IsEnabled = true;
				UpdateSummary();
			};
			checkBox.Unchecked += delegate
			{
				capturedStep.IsEnabled = false;
				UpdateSummary();
			};
			checkboxes.Add((checkBox, step));
			Grid.SetColumn(checkBox, 0);
			grid.Children.Add(checkBox);
			System.Windows.Controls.TextBlock element = new System.Windows.Controls.TextBlock
			{
				Text = step.Icon,
				FontSize = 14.0,
				VerticalAlignment = VerticalAlignment.Top,
				Margin = new Thickness(0.0, 1.0, 0.0, 0.0)
			};
			Grid.SetColumn(element, 1);
			grid.Children.Add(element);
			StackPanel stackPanel3 = new StackPanel();
			if (step.WikiUrl != null)
			{
				System.Windows.Controls.TextBlock textBlock = new System.Windows.Controls.TextBlock
				{
					FontSize = 12.0,
					FontWeight = FontWeights.SemiBold,
					Foreground = foreground,
					TextWrapping = TextWrapping.Wrap
				};
				Run linkRun = new Run(step.Title)
				{
					Cursor = Cursors.Hand
				};
				linkRun.MouseEnter += delegate
				{
					linkRun.TextDecorations = TextDecorations.Underline;
				};
				linkRun.MouseLeave += delegate
				{
					linkRun.TextDecorations = null;
				};
				string url = step.WikiUrl;
				linkRun.MouseLeftButtonDown += delegate
				{
					Process.Start(new ProcessStartInfo(url)
					{
						UseShellExecute = true
					});
				};
				textBlock.Inlines.Add(linkRun);
				stackPanel3.Children.Add(textBlock);
			}
			else
			{
				stackPanel3.Children.Add(new System.Windows.Controls.TextBlock
				{
					Text = step.Title,
					FontSize = 12.0,
					FontWeight = FontWeights.SemiBold,
					Foreground = foreground,
					TextWrapping = TextWrapping.Wrap
				});
			}
			Brush foreground3 = (step.IsNoChange ? brush2 : brush);
			stackPanel3.Children.Add(new System.Windows.Controls.TextBlock
			{
				Text = (step.IsNoChange ? "? " : " ") + step.Detail,
				FontSize = 11.0,
				Foreground = foreground3,
				TextWrapping = TextWrapping.Wrap,
				Margin = new Thickness(0.0, 2.0, 0.0, 0.0)
			});
			if (!string.IsNullOrEmpty(step.DisabledReason))
			{
				stackPanel3.Children.Add(new System.Windows.Controls.TextBlock
				{
					Text = "\u26a0 " + step.DisabledReason,
					FontSize = 11.0,
					Foreground = new SolidColorBrush(Color.FromRgb(208, 160, 32)),
					TextWrapping = TextWrapping.Wrap,
					Margin = new Thickness(0.0, 2.0, 0.0, 0.0)
				});
			}
			Grid.SetColumn(stackPanel3, 2);
			grid.Children.Add(stackPanel3);
			stackPanel2.Children.Add(grid);
			if (!string.IsNullOrEmpty(step.ContentPreview))
			{
				Border border2 = new Border
				{
					Background = new SolidColorBrush(Color.FromArgb(24, 48, 192, 48)),
					CornerRadius = new CornerRadius(4.0),
					Padding = new Thickness(8.0, 6.0, 8.0, 6.0),
					Margin = new Thickness(42.0, 6.0, 0.0, 0.0)
				};
				StackPanel stackPanel4 = new StackPanel();
				if (!string.IsNullOrEmpty(step.ContextAbove))
				{
					stackPanel4.Children.Add(new System.Windows.Controls.TextBlock
					{
						FontFamily = new FontFamily("Consolas"),
						FontSize = 11.0,
						Foreground = brush2,
						TextWrapping = TextWrapping.Wrap,
						Text = "  " + step.ContextAbove
					});
				}
				string[] array = step.ContentPreview.Split('\n');
				foreach (string text2 in array)
				{
					stackPanel4.Children.Add(new System.Windows.Controls.TextBlock
					{
						FontFamily = new FontFamily("Consolas"),
						FontSize = 11.0,
						Foreground = foreground2,
						TextWrapping = TextWrapping.Wrap,
						Text = "+ " + text2
					});
				}
				if (!string.IsNullOrEmpty(step.ContextBelow))
				{
					stackPanel4.Children.Add(new System.Windows.Controls.TextBlock
					{
						FontFamily = new FontFamily("Consolas"),
						FontSize = 11.0,
						Foreground = brush2,
						TextWrapping = TextWrapping.Wrap,
						Text = "  " + step.ContextBelow
					});
				}
				border2.Child = stackPanel4;
				stackPanel2.Children.Add(border2);
			}
			border.Child = stackPanel2;
			stackPanel.Children.Add(border);
		}
		Window owner = Window.GetWindow(this);
		Wpf.Ui.Controls.MessageBox dialog = new Wpf.Ui.Controls.MessageBox
		{
			Title = "Update Mystery Wiki Pages",
			Content = stackPanel,
			PrimaryButtonText = "Update",
			CloseButtonText = "Cancel",
			Owner = owner,
			MinWidth = 540.0,
			SizeToContent = SizeToContent.Height,
			MaxHeight = SystemParameters.WorkArea.Height * 0.8
		};
		dialogRef = dialog;
		UpdateSummary();
		dialog.Loaded += delegate
		{
			dialog.Top = Math.Max(owner.Top + 30.0, dialog.Top - owner.ActualHeight * 0.12);
		};
		ApplicationThemeManager.Apply(dialog);
		return dialog;
		void UpdateSummary()
		{
			int num3 = checkboxes.Count<(CheckBox, MysteryUpdateStep)>(((CheckBox cb, MysteryUpdateStep step) x) => x.cb.IsChecked == true);
			int count = steps.Count;
			if (num3 > 0)
			{
				summaryText.Text = $"{count} pages checked  {num3} selected for update";
			}
			else
			{
				summaryText.Text = $"{count} pages checked  no changes selected";
			}
			if (dialogRef != null)
			{
				dialogRef.IsPrimaryButtonEnabled = num3 > 0;
			}
		}
	}

	private void GoToSettings_Click(object sender, RoutedEventArgs e)
	{
		_main.NavigateToSettingsHighlightEvents();
	}

	private void ShowInfo(string message, InfoBarSeverity severity, bool autoClose = true)
	{
		infoBar.Message = message;
		infoBar.Severity = severity;
		infoBar.IsOpen = true;
		if (autoClose && severity != InfoBarSeverity.Error)
		{
			AutoCloseInfo();
		}
	}

	private async Task AutoCloseInfo()
	{
		await Task.Delay(4000);
		infoBar.IsOpen = false;
	}

}
