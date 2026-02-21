using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace MergeMansionWikiTools.Views;

public partial class FolderUpdateDialog : FluentWindow
{
    public bool UpdateDefault { get; private set; }
    public bool DontAskAgain { get; private set; }

    public FolderUpdateDialog(string folderPath)
    {
        InitializeComponent();
        ApplicationThemeManager.Apply(this);
        txtFolderPath.Text = folderPath;
    }

    private void BtnYes_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        UpdateDefault = true;
        DontAskAgain = chkDontAsk.IsChecked == true;
        DialogResult = true;
    }

    private void BtnNo_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        UpdateDefault = false;
        DontAskAgain = chkDontAsk.IsChecked == true;
        DialogResult = true;
    }
}
