using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MergeMansionWikiTools.Models;
using MergeMansionWikiTools.Services;
using TinifyAPI;
using Wpf.Ui.Controls;

namespace MergeMansionWikiTools.Views;

public partial class MysteryImagesControl : UserControl
{
	private MainWindow? _main;

	private MysteryEvent? _mystery;

	private List<DetectedDecorationFile> _detectedFiles = new List<DetectedDecorationFile>();

	private readonly Dictionary<DetectedDecorationFile, CheckBox> _checkboxes = new Dictionary<DetectedDecorationFile, CheckBox>();

	private readonly HashSet<DetectedDecorationFile> _optimizedFiles = new HashSet<DetectedDecorationFile>();

	private bool _initialized;

	private string? _initializedForPageTitle;

	private double _zoomLevel = 1.0;

	private int _imgNativeW;

	private int _imgNativeH;

	private Point _dragStart;

	private double _dragStartX;

	private double _dragStartY;

	private bool _isDragging;

	private bool _didDrag;















	public Action? OnStatusChanged { get; set; }

	public MysteryImagesControl()
	{
		InitializeComponent();
	}

	public void Initialize(MainWindow main, MysteryEvent mystery)
	{
		string currentPageTitle = mystery.WikiStatus.SuggestedPageTitle ?? mystery.Name;
		if (_initialized && _initializedForPageTitle != currentPageTitle)
		{
			// SuggestedPageTitle changed (e.g. disambiguation resolved after Event Page tab loaded)
			// Reset so we re-scan with the correct wiki image name
			_initialized = false;
			_detectedFiles.Clear();
			_checkboxes.Clear();
			_optimizedFiles.Clear();
		}
		if (!_initialized)
		{
			_initialized = true;
			_initializedForPageTitle = currentPageTitle;
			_main = main;
			_mystery = mystery;
			pnlLoading.Visibility = Visibility.Visible;
			pnlEmpty.Visibility = Visibility.Collapsed;
			scrollFiles.Visibility = Visibility.Collapsed;
			AutoScanAsync();
			if (!string.IsNullOrWhiteSpace(main.Settings.TinifyApiKey))
				tinifyUsage.Initialize(main.Settings.TinifyApiKey, main.Settings.TinifyApiKey2);
		}
	}

	private async Task AutoScanAsync()
	{
		if (_main == null || _mystery == null)
		{
			return;
		}
		string exportDir = MysteryWikiService.ResolveExportPngsDir(_main.Settings.ImageExporterBasePath, _main.Settings.SelectedApkVersion);
		if (string.IsNullOrEmpty(exportDir))
		{
			pnlLoading.Visibility = Visibility.Collapsed;
			pnlEmpty.Visibility = Visibility.Visible;
			txtEmptyMessage.Text = "Export path not configured.\nSet Image Exporter base path and APK version in Settings.";
			return;
		}
		txtExportPath.Text = exportDir;
		try
		{
			_detectedFiles = await Task.Run(() => MysteryWikiService.DetectDecorationFiles(exportDir, _mystery.ProgressionEventId, _mystery.WikiImageName, _mystery.MysteryType == MysteryType.Pet, _mystery));
			pnlLoading.Visibility = Visibility.Collapsed;
			if (_detectedFiles.Count == 0)
			{
				pnlEmpty.Visibility = Visibility.Visible;
				txtEmptyMessage.Text = "No image files found for " + _mystery.ProgressionEventId + "\nin " + exportDir;
				return;
			}
			List<DetectedDecorationFile> uploadable = _detectedFiles.Where((DetectedDecorationFile detectedDecorationFile) => detectedDecorationFile.Category != "EventItem").ToList();
			if (uploadable.Count > 0)
			{
				List<string> wikiFilenames = uploadable.Select((DetectedDecorationFile detectedDecorationFile) => "File:" + detectedDecorationFile.WikiFilename).ToList();
				Dictionary<string, bool> existMap = await MysteryWikiService.CheckPagesExistAsync(wikiFilenames);
				foreach (DetectedDecorationFile file in uploadable)
				{
					file.ExistsOnWiki = existMap.GetValueOrDefault("File:" + file.WikiFilename, defaultValue: false);
				}
			}
			foreach (DetectedDecorationFile f in _detectedFiles)
			{
				if (f.OptimizedSize.HasValue)
				{
					_optimizedFiles.Add(f);
				}
			}
			await CheckExistingSplitEventItemAsync();
			BuildFileList();
			scrollFiles.Visibility = Visibility.Visible;
			btnOptimize.IsEnabled = true;
			btnUpload.IsEnabled = true;
			int uploadableCount = _detectedFiles.Count((DetectedDecorationFile detectedDecorationFile) => detectedDecorationFile.Category != "EventItem");
			int preOptimized = _optimizedFiles.Count;
			string msg = $"Found {_detectedFiles.Count} files ({uploadableCount} uploadable)";
			if (preOptimized > 0)
			{
				msg += $", {preOptimized} already optimized";
			}
			ShowInfo(msg + ".", InfoBarSeverity.Success);
		}
		catch (Exception ex)
		{
			pnlLoading.Visibility = Visibility.Collapsed;
			pnlEmpty.Visibility = Visibility.Visible;
			txtEmptyMessage.Text = "Scan failed: " + ex.Message;
		}
	}

	private void BuildFileList()
	{
		pnlFiles.Children.Clear();
		Dictionary<DetectedDecorationFile, bool> dictionary = new Dictionary<DetectedDecorationFile, bool>();
		foreach (KeyValuePair<DetectedDecorationFile, CheckBox> checkbox in _checkboxes)
		{
			checkbox.Deconstruct(out var key, out var value);
			DetectedDecorationFile key2 = key;
			CheckBox checkBox = value;
			dictionary[key2] = checkBox.IsChecked == true;
		}
		_checkboxes.Clear();
		CheckBox checkBox2 = new CheckBox
		{
			Content = "Select All",
			IsChecked = false,
			Margin = new Thickness(0.0, 0.0, 0.0, 8.0),
			FontSize = 12.0
		};
		checkBox2.SetResourceReference(Control.ForegroundProperty, "TextFillColorSecondaryBrush");
		pnlFiles.Children.Add(checkBox2);
		List<DetectedDecorationFile> list = _detectedFiles.Where((DetectedDecorationFile f) => f.Category.StartsWith("Event Item Lv")).ToList();
		bool flag = false;
		foreach (DetectedDecorationFile detectedFile in _detectedFiles)
		{
			if (!flag && detectedFile.Category.StartsWith("Event Item Lv") && list.Count > 0)
			{
				flag = true;
				Border border = new Border
				{
					CornerRadius = new CornerRadius(4.0),
					Padding = new Thickness(10.0, 8.0, 10.0, 8.0),
					Margin = new Thickness(0.0, 8.0, 0.0, 0.0)
				};
				border.SetResourceReference(Border.BackgroundProperty, "SubtleFillColorSecondaryBrush");
				System.Windows.Controls.TextBlock textBlock = new System.Windows.Controls.TextBlock
				{
					Text = $"Event Item ({list.Count} files): {_mystery?.EventItemName ?? "Unknown"}",
					FontSize = 12.0,
					FontWeight = FontWeights.SemiBold
				};
				textBlock.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
				border.Child = textBlock;
				pnlFiles.Children.Add(border);
			}
			bool flag2 = detectedFile.Category == "EventItem";
			Border border2 = new Border
			{
				CornerRadius = new CornerRadius(4.0),
				Padding = new Thickness(10.0, 8.0, 10.0, 8.0),
				Margin = new Thickness(0.0, 0.0, 0.0, 4.0)
			};
			border2.SetResourceReference(Border.BackgroundProperty, "CardBackgroundFillColorDefaultBrush");
			Grid grid = new Grid();
			grid.ColumnDefinitions.Add(new ColumnDefinition
			{
				Width = GridLength.Auto
			});
			grid.ColumnDefinitions.Add(new ColumnDefinition
			{
				Width = GridLength.Auto
			});
			grid.ColumnDefinitions.Add(new ColumnDefinition
			{
				Width = new GridLength(1.0, GridUnitType.Star)
			});
			grid.ColumnDefinitions.Add(new ColumnDefinition
			{
				Width = GridLength.Auto
			});
			if (flag2)
			{
				Border element = new Border
				{
					Width = 24.0
				};
				Grid.SetColumn(element, 0);
				grid.Children.Add(element);
			}
			else
			{
				bool value3;
				bool value2 = (dictionary.TryGetValue(detectedFile, out value3) ? value3 : (detectedFile.ExistsOnWiki != true));
				CheckBox checkBox3 = new CheckBox
				{
					IsChecked = value2,
					VerticalAlignment = VerticalAlignment.Center,
					Margin = new Thickness(0.0, 0.0, 8.0, 0.0)
				};
				Grid.SetColumn(checkBox3, 0);
				grid.Children.Add(checkBox3);
				_checkboxes[detectedFile] = checkBox3;
			}
			try
			{
				if (File.Exists(detectedFile.SourcePath))
				{
					BitmapImage bitmapImage = new BitmapImage();
					bitmapImage.BeginInit();
					bitmapImage.UriSource = new Uri(detectedFile.SourcePath);
					bitmapImage.DecodePixelWidth = 48;
					bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
					bitmapImage.EndInit();
					bitmapImage.Freeze();
					System.Windows.Controls.Image image = new System.Windows.Controls.Image
					{
						Source = bitmapImage,
						Width = 48.0,
						Height = 48.0,
						Stretch = Stretch.Uniform,
						Margin = new Thickness(0.0, 0.0, 10.0, 0.0),
						VerticalAlignment = VerticalAlignment.Center,
						Cursor = Cursors.Hand,
						ToolTip = "Click to preview",
						Tag = detectedFile.SourcePath
					};
					ToolTipService.SetInitialShowDelay(image, 0);
					image.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs _)
					{
						if (s is System.Windows.Controls.Image { Tag: string tag })
						{
							ShowPreview(tag);
						}
					};
					Grid.SetColumn(image, 1);
					grid.Children.Add(image);
				}
			}
			catch
			{
			}
			StackPanel stackPanel = new StackPanel
			{
				VerticalAlignment = VerticalAlignment.Center
			};
			bool flag3 = detectedFile.Category.StartsWith("Event Item Lv");
			string text = (flag2 ? Path.GetFileName(detectedFile.SourcePath) : (flag3 ? (detectedFile.Category.Replace("Event Item ", "") + "  " + detectedFile.WikiFilename) : detectedFile.WikiFilename));
			System.Windows.Controls.TextBlock textBlock2 = new System.Windows.Controls.TextBlock
			{
				Text = text,
				FontSize = 12.0,
				FontWeight = FontWeights.Medium,
				TextTrimming = TextTrimming.CharacterEllipsis
			};
			textBlock2.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorPrimaryBrush");
			stackPanel.Children.Add(textBlock2);
			string text2 = detectedFile.Category + "  " + Path.GetFileName(detectedFile.SourcePath);
			if (_optimizedFiles.Contains(detectedFile) && detectedFile.OptimizedSize.HasValue)
			{
				text2 += $"  {(double)detectedFile.OptimizedSize.Value / 1024.0:F1} KB";
			}
			System.Windows.Controls.TextBlock textBlock3 = new System.Windows.Controls.TextBlock
			{
				Text = text2,
				FontSize = 11.0
			};
			textBlock3.SetResourceReference(System.Windows.Controls.TextBlock.ForegroundProperty, "TextFillColorTertiaryBrush");
			stackPanel.Children.Add(textBlock3);
			Grid.SetColumn(stackPanel, 2);
			grid.Children.Add(stackPanel);
			if (flag2)
			{
				Wpf.Ui.Controls.Button button = new Wpf.Ui.Controls.Button
				{
					Content = "Image Optimiser",
					Appearance = ControlAppearance.Secondary,
					Height = 32.0,
					FontSize = 11.0,
					VerticalAlignment = VerticalAlignment.Center,
					Margin = new Thickness(8.0, 0.0, 0.0, 0.0),
					Tag = detectedFile.SourcePath
				};
				button.Click += BtnOpenInOptimiser_Click;
				Grid.SetColumn(button, 3);
				grid.Children.Add(button);
			}
			else
			{
				Border border3 = new Border
				{
					CornerRadius = new CornerRadius(4.0),
					Padding = new Thickness(6.0, 2.0, 6.0, 2.0),
					VerticalAlignment = VerticalAlignment.Center,
					Margin = new Thickness(8.0, 0.0, 0.0, 0.0)
				};
				System.Windows.Controls.TextBlock textBlock4 = new System.Windows.Controls.TextBlock
				{
					FontSize = 11.0
				};
				bool flag4 = _optimizedFiles.Contains(detectedFile);
				if (flag4 && detectedFile.ExistsOnWiki == true)
				{
					border3.Background = new SolidColorBrush(Color.FromArgb(37, 96, 160, 224));
					textBlock4.Foreground = new SolidColorBrush(Color.FromRgb(112, 176, 240));
					textBlock4.Text = "? Ready  Exists";
				}
				else if (flag4)
				{
					border3.Background = new SolidColorBrush(Color.FromArgb(48, 0, 160, 0));
					textBlock4.Foreground = new SolidColorBrush(Color.FromRgb(48, 192, 48));
					textBlock4.Text = "? Optimized";
				}
				else if (detectedFile.ExistsOnWiki == true)
				{
					border3.Background = new SolidColorBrush(Color.FromArgb(48, 192, 144, 0));
					textBlock4.Foreground = new SolidColorBrush(Color.FromRgb(208, 160, 32));
					textBlock4.Text = "Exists";
				}
				else
				{
					border3.Background = new SolidColorBrush(Color.FromArgb(48, 0, 160, 0));
					textBlock4.Foreground = new SolidColorBrush(Color.FromRgb(48, 192, 48));
					textBlock4.Text = "New";
				}
				border3.Child = textBlock4;
				Grid.SetColumn(border3, 3);
				grid.Children.Add(border3);
			}
			border2.Child = grid;
			pnlFiles.Children.Add(border2);
		}
		checkBox2.Checked += delegate
		{
			foreach (var (detectedDecorationFile2, checkBox5) in _checkboxes)
			{
				if (detectedDecorationFile2.Category != "EventItem")
				{
					checkBox5.IsChecked = true;
				}
			}
		};
		checkBox2.Unchecked += delegate
		{
			foreach (KeyValuePair<DetectedDecorationFile, CheckBox> checkbox2 in _checkboxes)
			{
				checkbox2.Deconstruct(out var _, out var value4);
				CheckBox checkBox4 = value4;
				checkBox4.IsChecked = false;
			}
		};
		List<KeyValuePair<DetectedDecorationFile, CheckBox>> list2 = _checkboxes.Where<KeyValuePair<DetectedDecorationFile, CheckBox>>((KeyValuePair<DetectedDecorationFile, CheckBox> kv) => kv.Key.Category != "EventItem").ToList();
		if (list2.Count > 0 && list2.All((KeyValuePair<DetectedDecorationFile, CheckBox> kv) => kv.Value.IsChecked == true))
		{
			checkBox2.IsChecked = true;
		}
	}

	private async Task CheckExistingSplitEventItemAsync()
	{
		if (_main == null || _mystery == null)
		{
			return;
		}
		DetectedDecorationFile eventItemEntry = _detectedFiles.FirstOrDefault((DetectedDecorationFile detectedDecorationFile) => detectedDecorationFile.Category == "EventItem");
		if (eventItemEntry == null)
		{
			return;
		}
		string eventItemName = _mystery.EventItemName;
		if (string.IsNullOrEmpty(eventItemName))
		{
			return;
		}
		string searchDir = null;
		string workspaceDir = _main.Settings.ImageExporterBasePath;
		if (!string.IsNullOrEmpty(workspaceDir))
		{
			string pd = Path.Combine(workspaceDir, "Processed Images");
			if (Directory.Exists(pd))
			{
				searchDir = pd;
			}
		}
		if (searchDir == null)
		{
			return;
		}
		List<(string path, int level, string wikiName)> foundSplits = new List<(string, int, string)>();
		for (int lvl = 1; lvl <= 10; lvl++)
		{
			string wikiName = MysteryWikiService.FormatFileName(eventItemName, lvl);
			string filePath = Path.Combine(searchDir, wikiName);
			if (File.Exists(filePath))
			{
				foundSplits.Add((filePath, lvl, wikiName));
				continue;
			}
			break;
		}
		if (foundSplits.Count == 0)
		{
			return;
		}
		_detectedFiles.Remove(eventItemEntry);
		foreach (var item in foundSplits)
		{
			string path = item.path;
			int level = item.level;
			string wikiName2 = item.wikiName;
			DetectedDecorationFile file = new DetectedDecorationFile
			{
				SourcePath = path,
				WikiFilename = wikiName2,
				Category = $"Event Item Lv{level:D2}"
			};
			try
			{
				byte[] bytes = await File.ReadAllBytesAsync(path);
				if (OptimizationWindow.HasOptMarker(bytes))
				{
					file.OptimizedSize = bytes.Length;
					_optimizedFiles.Add(file);
				}
			}
			catch
			{
			}
			_detectedFiles.Add(file);
		}
		List<string> wikiNames = foundSplits.Select<(string, int, string), string>(((string path, int level, string wikiName) s) => "File:" + s.wikiName).ToList();
		Dictionary<string, bool> existMap = await MysteryWikiService.CheckPagesExistAsync(wikiNames);
		foreach (DetectedDecorationFile f in _detectedFiles.Where((DetectedDecorationFile detectedDecorationFile) => detectedDecorationFile.Category.StartsWith("Event Item Lv")))
		{
			f.ExistsOnWiki = existMap.GetValueOrDefault("File:" + f.WikiFilename, defaultValue: false);
		}
	}

	private void BtnOpenInOptimiser_Click(object sender, RoutedEventArgs e)
	{
		if (_main != null && sender is Wpf.Ui.Controls.Button { Tag: string tag })
		{
			_main.NavigateToImageOptimiserWithFile(tag);
			Window.GetWindow(this)?.Close();
		}
	}

	private void BtnOptimize_Click(object sender, RoutedEventArgs e)
	{
		if (_main != null && _mystery != null)
		{
			MysteryDecorationUploadDialog mysteryDecorationUploadDialog = new MysteryDecorationUploadDialog(_main, _mystery);
			mysteryDecorationUploadDialog.OnStatusChanged = OnStatusChanged;
			mysteryDecorationUploadDialog.Owner = Window.GetWindow(this);
			mysteryDecorationUploadDialog.ShowDialog();
			_initialized = false;
			_detectedFiles.Clear();
			_checkboxes.Clear();
			_optimizedFiles.Clear();
			AutoScanAsync();
			if (!string.IsNullOrWhiteSpace(_main.Settings.TinifyApiKey))
				tinifyUsage.Initialize(_main.Settings.TinifyApiKey, _main.Settings.TinifyApiKey2);
		}
	}

	private async void BtnUpload_Click(object sender, RoutedEventArgs e)
	{
		if (_main == null || _mystery == null)
		{
			return;
		}
		if (!_main.Settings.WikiVerified)
		{
			ShowInfo("Wiki account not verified. Go to Settings  Wiki to log in.", InfoBarSeverity.Warning);
			return;
		}
		CheckBox value;
		List<DetectedDecorationFile> selected = _detectedFiles.Where((DetectedDecorationFile detectedDecorationFile) => detectedDecorationFile.Category != "EventItem" && _checkboxes.TryGetValue(detectedDecorationFile, out value) && value.IsChecked == true).ToList();
		if (selected.Count == 0)
		{
			ShowInfo("No files selected for upload.", InfoBarSeverity.Warning);
			return;
		}
		string apiKey = _main.Settings.TinifyApiKey;
		string apiKey2 = _main.Settings.TinifyApiKey2;
		Func<byte[], Task<byte[]>> optimizeFunc = async delegate(byte[] data)
		{
			if (string.IsNullOrWhiteSpace(apiKey))
			{
				throw new Exception("TinyPNG API key not set.");
			}
			Tinify.Key = apiKey;
			try
			{
				return await (await Tinify.FromBuffer(data)).ToBuffer();
			}
			catch (AccountException) when (!string.IsNullOrWhiteSpace(apiKey2))
			{
				Tinify.Key = apiKey2;
				return await (await Tinify.FromBuffer(data)).ToBuffer();
			}
		};
		btnUpload.IsEnabled = false;
		btnOptimize.IsEnabled = false;
		int uploaded = 0;
		int skipped = 0;
		int failed = 0;
		bool forceAll = false;
		bool skipAll = false;
		bool confirmAll = false;
		bool optimizeAll = false;
		ShowInfo($"Processing {selected.Count} files...", InfoBarSeverity.Informational);
		try
		{
			using HttpClient client = await WikiMappingService.CreateAuthenticatedClientAsync(_main.Settings.WikiUsername, _main.Settings.WikiPassword);
			string csrfToken = JsonDocument.Parse(await client.GetStringAsync("https://merge-mansion.fandom.com/api.php?action=query&meta=tokens&format=json")).RootElement.GetProperty("query").GetProperty("tokens").GetProperty("csrftoken")
				.GetString();
			Window ownerWindow = Window.GetWindow(this);
			for (int idx = 0; idx < selected.Count; idx++)
			{
				DetectedDecorationFile file = selected[idx];
				try
				{
					if (!File.Exists(file.SourcePath))
					{
						failed++;
						continue;
					}
					bool needsOptimize = !_optimizedFiles.Contains(file);
					int remaining = selected.Count - idx - 1;
					bool hasUnoptimizedRemaining = selected.Skip(idx + 1).Any((DetectedDecorationFile item) => !_optimizedFiles.Contains(item));
					if (needsOptimize && !optimizeAll)
					{
						UploadConflictDialog wizardDlg = UploadConflictDialog.CreateOptimizeWizard(file.WikiFilename, remaining, file.SourcePath, optimizeFunc, _main.Settings.WikiUsername, hasUnoptimizedRemaining);
						wizardDlg.Owner = ownerWindow;
						wizardDlg.ShowDialog();
						try
						{
							byte[] postBytes = File.ReadAllBytes(file.SourcePath);
							if (OptimizationWindow.HasOptMarker(postBytes))
							{
								_optimizedFiles.Add(file);
								file.OptimizedSize = postBytes.Length;
							}
						}
						catch
						{
						}
						switch (wizardDlg.Choice)
						{
						case UploadConflictChoice.ForceAll:
							forceAll = true;
							confirmAll = true;
							break;
						case UploadConflictChoice.ForceAllOptimize:
							optimizeAll = true;
							forceAll = true;
							confirmAll = true;
							break;
						case UploadConflictChoice.Skip:
							skipped++;
							goto end_IL_0402;
						case UploadConflictChoice.SkipAll:
							skipAll = true;
							skipped++;
							goto end_IL_0402;
						case UploadConflictChoice.Cancel:
							goto end_IL_0319;
						}
						goto IL_0886;
					}
					if (needsOptimize && optimizeAll)
					{
						byte[] marked = OptimizationWindow.InsertOptMarker(await optimizeFunc(await File.ReadAllBytesAsync(file.SourcePath)));
						await File.WriteAllBytesAsync(file.SourcePath, marked);
						_optimizedFiles.Add(file);
						file.OptimizedSize = marked.Length;
						goto IL_0886;
					}
					if (!needsOptimize)
					{
						goto IL_0886;
					}
					skipped++;
					goto end_IL_0402;
					IL_0886:
					if (forceAll || confirmAll)
					{
						goto IL_0bd3;
					}
					Dictionary<string, bool> existMap = await MysteryWikiService.CheckPagesExistAsync(new string[1] { "File:" + file.WikiFilename });
					file.ExistsOnWiki = existMap.GetValueOrDefault("File:" + file.WikiFilename, defaultValue: false) || existMap.GetValueOrDefault("File:" + file.WikiFilename.Replace('_', ' '), defaultValue: false);
					if (file.ExistsOnWiki == true)
					{
						if (skipAll)
						{
							skipped++;
							continue;
						}
						if (!needsOptimize || optimizeAll)
						{
							UploadConflictDialog dlg = new UploadConflictDialog(file.WikiFilename, remaining, file.SourcePath, isPartOfSplit: false, _main.Settings.WikiUsername)
							{
								Owner = ownerWindow
							};
							dlg.ShowDialog();
							switch (dlg.Choice)
							{
							case UploadConflictChoice.ForceAll:
								forceAll = true;
								break;
							case UploadConflictChoice.Skip:
								skipped++;
								goto end_IL_0402;
							case UploadConflictChoice.SkipAll:
								skipAll = true;
								skipped++;
								goto end_IL_0402;
							case UploadConflictChoice.Cancel:
								goto end_IL_0319;
							}
						}
						goto IL_0bd3;
					}
					if (needsOptimize)
					{
						goto IL_0bd3;
					}
					UploadConflictDialog confirmDlg = UploadConflictDialog.CreateNewFileDialog(file.WikiFilename, remaining, file.SourcePath, _main.Settings.WikiUsername);
					confirmDlg.ShowForceOptimizeButton(hasUnoptimizedRemaining);
					confirmDlg.Owner = ownerWindow;
					confirmDlg.ShowDialog();
					if (confirmDlg.Choice == UploadConflictChoice.ForceAll)
					{
						confirmAll = true;
						goto IL_0bd3;
					}
					if (confirmDlg.Choice == UploadConflictChoice.ForceAllOptimize)
					{
						optimizeAll = true;
						forceAll = true;
						confirmAll = true;
						goto IL_0bd3;
					}
					if (confirmDlg.Choice == UploadConflictChoice.Force)
					{
						goto IL_0bd3;
					}
					goto end_IL_0319;
					IL_0bd3:
					await WikiMappingService.UploadFileAsync(fileData: await File.ReadAllBytesAsync(file.SourcePath), description: (file.ExistsOnWiki != true) ? "{{Permission}}" : null, client: client, csrfToken: csrfToken, filename: file.WikiFilename);
					uploaded++;
					UserStatsService.Increment(delegate(UserStats s)
					{
						s.MysteryPagesPublished++;
					});
					end_IL_0402:;
				}
				catch (Exception ex)
				{
					AppLogger.Warn("Upload failed for " + file.WikiFilename + ": " + ex.Message);
					failed++;
				}
			}
			end_IL_0319:;
		}
		catch (Exception ex2)
		{
			Exception ex3 = ex2;
			ShowInfo("Authentication failed: " + ex3.Message, InfoBarSeverity.Error);
			btnUpload.IsEnabled = true;
			return;
		}
		btnUpload.IsEnabled = true;
		btnOptimize.IsEnabled = true;
		if (_main != null && !string.IsNullOrWhiteSpace(_main.Settings.TinifyApiKey))
			tinifyUsage.Initialize(_main.Settings.TinifyApiKey, _main.Settings.TinifyApiKey2);
		if (uploaded > 0)
		{
			List<string> allWikiNames = (from detectedDecorationFile in _detectedFiles
				where detectedDecorationFile.Category != "EventItem" && !string.IsNullOrEmpty(detectedDecorationFile.WikiFilename)
				select "File:" + detectedDecorationFile.WikiFilename).ToList();
			if (allWikiNames.Count > 0)
			{
				Dictionary<string, bool> existMap2 = await MysteryWikiService.CheckPagesExistAsync(allWikiNames);
				foreach (DetectedDecorationFile f in _detectedFiles.Where((DetectedDecorationFile detectedDecorationFile) => !string.IsNullOrEmpty(detectedDecorationFile.WikiFilename)))
				{
					f.ExistsOnWiki = existMap2.GetValueOrDefault("File:" + f.WikiFilename, defaultValue: false);
				}
			}
			BuildFileList();
			_mystery.WikiStatus.ImagesExistOnWiki = _detectedFiles.Count((DetectedDecorationFile detectedDecorationFile) => detectedDecorationFile.Category != "EventItem" && detectedDecorationFile.ExistsOnWiki == true);
			MysteryWikiService.UpdateSingleMysteryCache(_mystery);
			OnStatusChanged?.Invoke();
		}
		List<string> parts = new List<string>();
		if (uploaded > 0)
		{
			parts.Add($"{uploaded} uploaded");
		}
		if (skipped > 0)
		{
			parts.Add($"{skipped} skipped");
		}
		if (failed > 0)
		{
			parts.Add($"{failed} failed");
		}
		ShowInfo(string.Join("  ", parts), (failed <= 0) ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
	}

	private void ShowPreview(string filePath)
	{
		try
		{
			BitmapImage bitmapImage = new BitmapImage();
			bitmapImage.BeginInit();
			bitmapImage.UriSource = new Uri(filePath);
			bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
			bitmapImage.EndInit();
			bitmapImage.Freeze();
			previewImage.Source = bitmapImage;
			_imgNativeW = bitmapImage.PixelWidth;
			_imgNativeH = bitmapImage.PixelHeight;
			previewOverlay.Visibility = Visibility.Visible;
			base.Dispatcher.InvokeAsync(CenterAndFitPreview, DispatcherPriority.Loaded);
		}
		catch
		{
		}
	}

	private void CenterAndFitPreview()
	{
		double actualWidth = previewCanvas.ActualWidth;
		double actualHeight = previewCanvas.ActualHeight;
		if (!(actualWidth <= 0.0) && !(actualHeight <= 0.0) && _imgNativeW > 0)
		{
			_zoomLevel = 1.0;
			while (_zoomLevel > 0.25 && ((double)_imgNativeW * _zoomLevel > actualWidth || (double)_imgNativeH * _zoomLevel > actualHeight))
			{
				_zoomLevel -= 0.125;
			}
			previewImage.Width = (double)_imgNativeW * _zoomLevel;
			previewImage.Height = (double)_imgNativeH * _zoomLevel;
			Canvas.SetLeft(previewImage, (actualWidth - previewImage.Width) / 2.0);
			Canvas.SetTop(previewImage, (actualHeight - previewImage.Height) / 2.0);
			int value = (int)(_zoomLevel * 100.0);
			txtPreviewInfo.Text = $"{_imgNativeW}�{_imgNativeH}  {value}%";
		}
	}

	private void PreviewCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
	{
		if (previewOverlay.Visibility == Visibility.Visible && previewImage.Source != null)
		{
			CenterAndFitPreview();
		}
	}

	private void PreviewOverlay_MouseDown(object sender, MouseButtonEventArgs e)
	{
		_isDragging = true;
		_didDrag = false;
		_dragStart = e.GetPosition(previewCanvas);
		_dragStartX = Canvas.GetLeft(previewImage);
		_dragStartY = Canvas.GetTop(previewImage);
		if (double.IsNaN(_dragStartX))
		{
			_dragStartX = 0.0;
		}
		if (double.IsNaN(_dragStartY))
		{
			_dragStartY = 0.0;
		}
		previewOverlay.Cursor = Cursors.SizeAll;
		previewOverlay.CaptureMouse();
		e.Handled = true;
	}

	private void PreviewOverlay_MouseMove(object sender, MouseEventArgs e)
	{
		if (_isDragging)
		{
			Point position = e.GetPosition(previewCanvas);
			if (Math.Abs(position.X - _dragStart.X) > 3.0 || Math.Abs(position.Y - _dragStart.Y) > 3.0)
			{
				_didDrag = true;
			}
			Canvas.SetLeft(previewImage, _dragStartX + (position.X - _dragStart.X));
			Canvas.SetTop(previewImage, _dragStartY + (position.Y - _dragStart.Y));
		}
	}

	private void PreviewOverlay_MouseUp(object sender, MouseButtonEventArgs e)
	{
		bool isDragging = _isDragging;
		_isDragging = false;
		previewOverlay.Cursor = Cursors.Arrow;
		previewOverlay.ReleaseMouseCapture();
		if (isDragging && !_didDrag)
		{
			double num = Canvas.GetLeft(previewImage);
			double num2 = Canvas.GetTop(previewImage);
			if (double.IsNaN(num))
			{
				num = 0.0;
			}
			if (double.IsNaN(num2))
			{
				num2 = 0.0;
			}
			Point position = e.GetPosition(previewCanvas);
			if (!(position.X >= num) || !(position.Y >= num2) || !(position.X <= num + previewImage.Width) || !(position.Y <= num2 + previewImage.Height))
			{
				previewOverlay.Visibility = Visibility.Collapsed;
				previewImage.Source = null;
			}
		}
	}

	private void PreviewClose_Click(object sender, MouseButtonEventArgs e)
	{
		previewOverlay.Visibility = Visibility.Collapsed;
		previewImage.Source = null;
		e.Handled = true;
	}

	private void PreviewScroll_MouseWheel(object sender, MouseWheelEventArgs e)
	{
		e.Handled = true;
		double zoomLevel = _zoomLevel;
		_zoomLevel += ((e.Delta > 0) ? 0.125 : (-0.125));
		_zoomLevel = Math.Max(0.25, Math.Min(4.0, _zoomLevel));
		Point position = e.GetPosition(previewCanvas);
		double num = Canvas.GetLeft(previewImage);
		double num2 = Canvas.GetTop(previewImage);
		if (double.IsNaN(num))
		{
			num = 0.0;
		}
		if (double.IsNaN(num2))
		{
			num2 = 0.0;
		}
		double num3 = _zoomLevel / zoomLevel;
		previewImage.Width = (double)_imgNativeW * _zoomLevel;
		previewImage.Height = (double)_imgNativeH * _zoomLevel;
		Canvas.SetLeft(previewImage, position.X - (position.X - num) * num3);
		Canvas.SetTop(previewImage, position.Y - (position.Y - num2) * num3);
		int value = (int)(_zoomLevel * 100.0);
		txtPreviewInfo.Text = $"{_imgNativeW}�{_imgNativeH}  {value}%";
	}

	private void ShowInfo(string message, InfoBarSeverity severity)
	{
		infoBar.Message = message;
		infoBar.Severity = severity;
		infoBar.IsOpen = true;
	}

}
