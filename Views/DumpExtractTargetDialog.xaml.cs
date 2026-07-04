using System.Collections.Generic;
using System.Windows;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace MergeMansionWikiTools.Views;

/// <summary>
/// Shown when a Discord dump's assigned (date-matched) game version differs from the current
/// working version. Lets the user choose the extraction target version and whether to switch
/// their working version to it. The chosen version folder is created on extraction if missing.
/// </summary>
public partial class DumpExtractTargetDialog : FluentWindow
{
    /// <summary>Chosen target version (folder name). Defaults to the assigned version.</summary>
    public string TargetVersion { get; private set; }

    /// <summary>Whether to switch the app's working version to <see cref="TargetVersion"/>.</summary>
    public bool SwitchWorkingVersion { get; private set; }

    private readonly string _assignedVersion;

    public DumpExtractTargetDialog(string assignedVersion, string? currentVersion,
        IEnumerable<string> existingVersions)
    {
        InitializeComponent();
        ApplicationThemeManager.Apply(this);

        _assignedVersion = assignedVersion;
        TargetVersion = assignedVersion;

        txtInfo.Text =
            $"This dump is assigned to version v{assignedVersion}, but your current working " +
            $"version is v{(string.IsNullOrEmpty(currentVersion) ? "(none)" : currentVersion)}.\n" +
            "Where should it be extracted?";
        rbAssigned.Content = $"Extract to assigned version (v{assignedVersion})";
        rbAssigned.IsChecked = true;

        foreach (var v in existingVersions) cmbVersion.Items.Add(v);
        cmbVersion.Text = string.IsNullOrEmpty(currentVersion) ? assignedVersion : currentVersion;
    }

    private void Target_Changed(object sender, RoutedEventArgs e)
    {
        if (cmbVersion != null) cmbVersion.IsEnabled = rbOther.IsChecked == true;
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e)
    {
        if (rbOther.IsChecked == true)
        {
            var v = (cmbVersion.Text ?? "").Trim();
            TargetVersion = string.IsNullOrEmpty(v) ? _assignedVersion : v;
        }
        else
        {
            TargetVersion = _assignedVersion;
        }
        SwitchWorkingVersion = chkSwitch.IsChecked == true;
        DialogResult = true;
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
