using System;
using System.Windows;
using System.Windows.Media.Imaging;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace MergeMansionWikiTools.Views;

public enum UploadConflictChoice
{
    Force,
    ForceAll,
    Skip,
    SkipAll,
    Cancel
}

public partial class UploadConflictDialog : FluentWindow
{
    public UploadConflictChoice Choice { get; private set; } = UploadConflictChoice.Cancel;

    public UploadConflictDialog(string filename, int remaining, string? localFilePath = null)
    {
        InitializeComponent();
        ApplicationThemeManager.Apply(this);

        txtMessage.Text = $"{filename} already exists on the wiki.";
        txtRemaining.Text = remaining > 0
            ? $"{remaining} file(s) remaining after this one."
            : "This is the last file.";

        if (localFilePath != null)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.UriSource = new Uri(localFilePath);
                bmp.DecodePixelHeight = 80;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze();
                imgPreview.Source = bmp;
            }
            catch { /* no preview */ }
        }
    }

    private void BtnForce_Click(object sender, RoutedEventArgs e)    { Choice = UploadConflictChoice.Force;    Close(); }
    private void BtnForceAll_Click(object sender, RoutedEventArgs e) { Choice = UploadConflictChoice.ForceAll; Close(); }
    private void BtnSkip_Click(object sender, RoutedEventArgs e)     { Choice = UploadConflictChoice.Skip;     Close(); }
    private void BtnSkipAll_Click(object sender, RoutedEventArgs e)  { Choice = UploadConflictChoice.SkipAll;  Close(); }
    private void BtnCancel_Click(object sender, RoutedEventArgs e)   { Choice = UploadConflictChoice.Cancel;   Close(); }
}
