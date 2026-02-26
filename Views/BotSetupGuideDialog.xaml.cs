using System.Diagnostics;
using System.Windows;
using System.Windows.Navigation;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace MergeMansionWikiTools.Views;

public partial class BotSetupGuideDialog : FluentWindow
{
    public BotSetupGuideDialog()
    {
        InitializeComponent();
        ApplicationThemeManager.Apply(this);
    }

    private void Hyperlink_Navigate(object sender, RequestNavigateEventArgs e)
    {
        Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        e.Handled = true;
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => Close();
}
