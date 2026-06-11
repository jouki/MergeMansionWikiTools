using System.Windows;
using Wpf.Ui.Controls;

namespace MergeMansionWikiTools.Views;

/// <summary>
/// ImageOptimiserPage — upload domain: collecting upload items per cluster
/// (split groups, linked clusters, singles) and launching the WikiUploadDialog.
/// Also handles the Mystery return mode short-circuit of the upload button.
/// </summary>
public partial class ImageOptimiserPage
{
    // ══════════════════════════════════════════════════════════════
    //  UPLOAD TO WIKI
    // ══════════════════════════════════════════════════════════════

    private void BtnUploadWiki_Click(object sender, RoutedEventArgs e)
    {
        // Mystery return mode — collect split results and return
        if (_mysteryReturnCallback != null)
        {
            var resultFiles = new List<string>();
            foreach (var cluster in _clusters)
                foreach (var oi in cluster.Images)
                    if (oi.IsSplit && oi.SplitResultFiles.Count > 0)
                        resultFiles.AddRange(oi.SplitResultFiles);

            if (resultFiles.Count == 0)
            {
                infoBar.Message = "No split images to return. Split images first.";
                infoBar.Severity = InfoBarSeverity.Warning;
                infoBar.IsOpen = true;
                return;
            }

            var callback = _mysteryReturnCallback;
            _mysteryReturnCallback = null;
            // Restore button
            btnUploadWiki.Content = "Upload to Wiki";
            btnUploadWiki.Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.ArrowUpload24 };

            callback(resultFiles);
            return;
        }

        if (!_main.Settings.WikiVerified)
        {
            infoBar.Message = "Wiki bot not configured. Set up credentials in Settings.";
            infoBar.Severity = InfoBarSeverity.Warning;
            infoBar.IsOpen = true;
            return;
        }

        if (_clusters.Count == 0) return;

        // Collect upload items: group by cluster; merged cluster split results share one group
        var uploadItems = new List<WikiUploadItem>();
        foreach (var cluster in _clusters)
        {
            // Determine the cluster's name source image
            var nameSourceIdx = Math.Clamp(cluster.NameSourceIndex, 0, cluster.Images.Count - 1);
            var nameSourceImg = cluster.Images[nameSourceIdx];
            var chainName = _activeChain?.DisplayName;
            var clusterChainName = chainName ?? nameSourceImg.DetectedChainName;
            var clusterGroupPath = nameSourceImg.FilePath; // shared SplitGroupSourcePath for the whole cluster

            // Collect all split result files across the cluster
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
                // All split results from this cluster form ONE group
                foreach (var splitPath in clusterSplitFiles)
                {
                    uploadItems.Add(new WikiUploadItem
                    {
                        FilePath = splitPath,
                        DetectedChainName = clusterChainName,
                        IsPartOfSplitGroup = true,
                        SplitGroupSourcePath = clusterGroupPath,
                        IsOptimized = _optimizedFiles.Contains(splitPath)
                    });
                }
            }
            else if (cluster.Images.Count > 1)
            {
                // Multi-image cluster (linked): treat as group
                foreach (var oi in cluster.Images)
                {
                    uploadItems.Add(new WikiUploadItem
                    {
                        FilePath = oi.FilePath,
                        DetectedChainName = chainName ?? clusterChainName ?? oi.DetectedChainName,
                        IsPartOfSplitGroup = true,
                        SplitGroupSourcePath = clusterGroupPath,
                        IsOptimized = _optimizedFiles.Contains(oi.FilePath)
                    });
                }
            }
            else
            {
                // Single image: individual row
                var oi = cluster.Images[0];
                uploadItems.Add(new WikiUploadItem
                {
                    FilePath = oi.FilePath,
                    DetectedChainName = chainName ?? oi.DetectedChainName,
                    IsPartOfSplitGroup = false,
                    IsOptimized = _optimizedFiles.Contains(oi.FilePath)
                });
            }
        }

        var dialog = new WikiUploadDialog(_main, uploadItems)
        {
            Owner = Window.GetWindow(this)
        };
        dialog.Closed += (_, _) =>
        {
            if (dialog.UploadedCount > 0)
            {
                infoBar.Message = $"Uploaded {dialog.UploadedCount} images to wiki.";
                infoBar.Severity = InfoBarSeverity.Success;
                infoBar.IsOpen = true;
            }
        };
        dialog.Show();
    }
}
