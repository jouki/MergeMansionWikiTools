using System.Collections.Generic;
using System.Windows;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace MergeMansionWikiTools.Views;

/// <summary>Lets the user pick which level-2 section the Garage Cleanup section should be inserted after,
/// when neither "Rewards" nor "Progress Bar" exists on the page.</summary>
public partial class SectionPickerDialog : FluentWindow
{
    /// <summary>The chosen section heading, or null if cancelled.</summary>
    public string? SelectedSection { get; private set; }

    public SectionPickerDialog(IEnumerable<string> sections)
    {
        InitializeComponent();
        ApplicationThemeManager.Apply(this);

        foreach (var s in sections) cmbSections.Items.Add(s);
        if (cmbSections.Items.Count > 0) cmbSections.SelectedIndex = cmbSections.Items.Count - 1;
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        SelectedSection = cmbSections.SelectedItem as string;
        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
