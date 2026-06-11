using System.IO;
using System.Windows;
using MergeMansionWikiTools.Services;
using Wpf.Ui.Controls;
using static MergeMansionWikiTools.Services.UserStatsService;

namespace MergeMansionWikiTools.Views;

/// <summary>
/// ImageOptimiserPage — optimization domain: TinyPNG batch optimization including
/// the unsplit-clusters pre-flight dialog (split / load existing / skip).
/// </summary>
public partial class ImageOptimiserPage
{
    // ══════════════════════════════════════════════════════════════
    //  OPTIMIZE ALL (TinyPNG)
    // ══════════════════════════════════════════════════════════════

    private async void BtnOptimizeAll_Click(object sender, RoutedEventArgs e)
    {
        // Save current cluster's index text before checking
        if (_selectedCluster != null)
            _selectedCluster.IndexText = inputIndices.Text;

        // Check if any cluster has actual indices set but hasn't been split yet
        var unsplitClusters = _clusters.Where(c =>
            ImageSplitLogic.ParseIndexTokens(c.IndexText).Length > 0 &&
            c.Images.Any(i => i.IsScissorsActive) &&
            !c.Images.Any(i => i.IsSplit)).ToList();

        if (unsplitClusters.Count > 0)
        {
            // Check if existing processed files are available for any unsplit cluster
            bool hasExisting = false;
            int existingCount = 0;
            int existingOptCount = 0;
            var processedDir = GetProcessedImagesDir();

            if (processedDir != null)
            {
                foreach (var cluster in unsplitClusters)
                {
                    var nameIdx = Math.Clamp(cluster.NameSourceIndex, 0, cluster.Images.Count - 1);
                    var nameSourceImg = cluster.Images[nameIdx];
                    var clName = cluster.UseChainName
                        ? (_activeChain?.ConfigKey ?? nameSourceImg.DetectedChainName ?? System.IO.Path.GetFileNameWithoutExtension(nameSourceImg.FilePath))
                        : System.IO.Path.GetFileNameWithoutExtension(nameSourceImg.FilePath);
                    var suffArr = ImageSplitLogic.ParseIndexTokens(cluster.IndexText);
                    foreach (var suf in suffArr)
                    {
                        if (suf == "-") continue;
                        var path = System.IO.Path.Combine(processedDir, ImageSplitLogic.SplitFileName(clName, suf));
                        if (File.Exists(path))
                        {
                            hasExisting = true;
                            existingCount++;
                            try
                            {
                                if (OptimizationWindow.HasOptMarker(File.ReadAllBytes(path)))
                                    existingOptCount++;
                            }
                            catch { }
                        }
                    }
                }
            }

            string content = "You have entered split levels but haven't split the images yet.\n\n";
            if (hasExisting)
                content += $"{existingCount} existing file(s) found in Processed Images" +
                    (existingOptCount > 0 ? $" ({existingOptCount} optimized)" : "") +
                    ".\n\n";
            content += "Do you want to split them and proceed to optimisation?";

            var msgBox = new Wpf.Ui.Controls.MessageBox
            {
                Title = "Unsplit images detected",
                Content = content,
                PrimaryButtonText = hasExisting ? "Load Existing" : "Split & proceed",
                SecondaryButtonText = hasExisting ? "Split & proceed" : "Skip",
                CloseButtonText = "Cancel",
                MinWidth = 500,
                Owner = Window.GetWindow(this)
            };
            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(msgBox);
            var result = await msgBox.ShowDialogAsync();

            bool doSplit = false;
            bool doLoad = false;

            if (hasExisting)
            {
                if (result == Wpf.Ui.Controls.MessageBoxResult.Primary) doLoad = true;
                else if (result == Wpf.Ui.Controls.MessageBoxResult.Secondary) doSplit = true;
                else return; // Cancel
            }
            else
            {
                if (result == Wpf.Ui.Controls.MessageBoxResult.Primary) doSplit = true;
                else if (result == Wpf.Ui.Controls.MessageBoxResult.None) return;
                // Secondary = Skip → fall through to optimization
            }

            var savedImage = _selectedImage;
            var savedCluster = _selectedCluster;

            _suppressIndexReset = true;
            foreach (var cluster in unsplitClusters)
            {
                _selectedCluster = cluster;
                _selectedImage = cluster.Images.FirstOrDefault();
                inputIndices.Text = cluster.IndexText;

                if (doLoad)
                    ProcessSplit(loadExistingIfFound: true);
                else if (doSplit)
                    ProcessSplit(suppressExistingDialog: true);
            }
            _suppressIndexReset = false;

            if (savedImage != null && savedCluster != null && _clusters.Contains(savedCluster))
                SelectImage(savedImage);
            else if (_clusters.Count > 0)
                SelectImage(_clusters[0].Images[0]);
        }

        // Collect files to optimize: iterate by cluster to avoid duplicate merged images
        // If source is from export dir and not yet split, copy to output dir first
        var filesToOptimize = new List<string>();
        bool anyRedirected = false;
        foreach (var cluster in _clusters)
        {
            var clusterSplitFiles = new List<string>();
            bool hasSplit = false;
            foreach (var oi in cluster.Images)
            {
                if (oi.IsSplit && oi.SplitResultFiles.Count > 0)
                {
                    clusterSplitFiles.AddRange(oi.SplitResultFiles);
                    hasSplit = true;
                }
            }

            if (hasSplit && clusterSplitFiles.Count > 0)
            {
                filesToOptimize.AddRange(clusterSplitFiles);
            }
            else
            {
                foreach (var oi in cluster.Images)
                {
                    var (outDir, redirected) = GetOutputDir(oi.FilePath);
                    if (redirected)
                    {
                        anyRedirected = true;
                        Directory.CreateDirectory(outDir);
                        var destPath = System.IO.Path.Combine(outDir,
                            System.IO.Path.GetFileName(oi.FilePath));
                        File.Copy(oi.FilePath, destPath, overwrite: true);
                        filesToOptimize.Add(destPath);
                    }
                    else
                    {
                        filesToOptimize.Add(oi.FilePath);
                    }
                }
            }
        }

        if (filesToOptimize.Count == 0) return;

        var apiKey = _main.Settings.TinifyApiKey;
        var apiKey2 = _main.Settings.TinifyApiKey2;

        var optWin = new OptimizationWindow(filesToOptimize, apiKey, apiKey2)
        {
            Owner = Window.GetWindow(this)
        };
        optWin.ShowDialog();

        // Track which individual files were optimized
        var optimizedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in optWin.Files)
        {
            if (f.IsOptimized)
                optimizedPaths.Add(f.Path);
        }
        _optimizedFiles.UnionWith(optimizedPaths);

        if (optimizedPaths.Count > 0)
        {
            // Mark images as optimized if all their files are optimized
            foreach (var oi in AllImages)
            {
                if (oi.IsSplit && oi.SplitResultFiles.Count > 0)
                    oi.IsOptimized = oi.SplitResultFiles.All(f => _optimizedFiles.Contains(f));
                else
                    oi.IsOptimized = _optimizedFiles.Contains(oi.FilePath);
            }

            Increment(s => s.ImagesOptimized += optimizedPaths.Count);

            var optMsg = $"Optimised {optimizedPaths.Count} images.";
            if (anyRedirected)
            {
                var outDir = filesToOptimize.Select(f => System.IO.Path.GetDirectoryName(f)!)
                    .FirstOrDefault(d => d.EndsWith("Export - Items", StringComparison.OrdinalIgnoreCase));
                if (outDir != null)
                {
                    _lastOutputDir = outDir;
                    btnOpenOutputFolder.Visibility = Visibility.Visible;
                    optMsg += $" → {System.IO.Path.GetFileName(outDir)}";
                }
            }
            infoBar.Message = optMsg;
            infoBar.Severity = InfoBarSeverity.Success;
            infoBar.IsOpen = true;

            // Refresh thumbnails
            foreach (var oi in AllImages)
            {
                var newThumb = LoadThumbnail(oi.FilePath, 80);
                if (newThumb != null) oi.Thumbnail = newThumb;
            }
            RebuildThumbnailStrip();
        }

        UpdateUploadButtonState();
    }
}
