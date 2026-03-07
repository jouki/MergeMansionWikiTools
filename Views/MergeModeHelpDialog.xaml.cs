using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace MergeMansionWikiTools.Views;

public partial class MergeModeHelpDialog : FluentWindow
{
    public MergeModeHelpDialog()
    {
        InitializeComponent();
        ApplicationThemeManager.Apply(this);
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }
}
