using System.Windows;
using System.Windows.Input;
using MergeMansionWikiTools.Services;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace MergeMansionWikiTools.Views;

public partial class ChainNameDialog : FluentWindow
{
    public string ChosenName { get; private set; } = "";
    public bool SaveToWiki => chkSaveToWiki.IsChecked == true;

    public ChainNameDialog(ChainNameService nameService, string configKey, string currentDisplayName,
        bool canSaveToWiki = false)
    {
        InitializeComponent();
        ApplicationThemeManager.Apply(this);

        if (canSaveToWiki)
            chkSaveToWiki.Visibility = Visibility.Visible;

        txtConfigKey.Text = $"ConfigKey: {configKey}";
        txtCurrentName.Text = $"Current: {currentDisplayName}";

        // Pre-fill with existing custom name if available
        var existing = nameService.GetCustomName(configKey);
        if (!string.IsNullOrEmpty(existing))
            txtName.Text = existing;
        else
            txtName.Text = currentDisplayName;

        // Load existing names list
        var names = nameService.GetAllNames();
        lvExistingNames.ItemsSource = names;

        // Focus the text box
        Loaded += (_, _) =>
        {
            txtName.Focus();
            txtName.SelectAll();
        };
    }

    private void LvExistingNames_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (lvExistingNames.SelectedItem is string selected)
            txtName.Text = selected;
    }

    private void TxtName_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Confirm();
        }
        else if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Skip();
        }
    }

    private void BtnConfirm_Click(object sender, RoutedEventArgs e) => Confirm();
    private void BtnSkip_Click(object sender, RoutedEventArgs e) => Skip();

    private void Confirm()
    {
        ChosenName = txtName.Text?.Trim() ?? "";
        DialogResult = true;
        Close();
    }

    private void Skip()
    {
        ChosenName = "";
        DialogResult = false;
        Close();
    }
}
