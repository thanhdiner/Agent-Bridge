using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Windows;
using LocalMcp.BuildingBlocks.Configuration;
using Wpf.Ui.Controls;
using Forms = System.Windows.Forms;

namespace AgentBridge.Desktop;

public partial class WorkspaceDialog : FluentWindow
{
    private static readonly Regex AliasPattern = new(
        "^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private readonly HashSet<string> _reservedAliases;

    public WorkspaceDialog(
        WorkspaceConfigurationEntry? existing,
        IReadOnlyCollection<string> reservedAliases)
    {
        InitializeComponent();

        _reservedAliases = reservedAliases.ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (existing is null)
            return;

        HeadingText.Text = "Edit workspace";
        AliasTextBox.Text = existing.Alias;
        FolderTextBox.Text = existing.Path;
        DescriptionTextBox.Text = existing.Description ?? string.Empty;
        WritableCheckBox.IsChecked = existing.Writable;
    }

    public WorkspaceConfigurationEntry? Result { get; private set; }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Choose a folder for this AgentBridge workspace",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
            InitialDirectory = Directory.Exists(FolderTextBox.Text)
                ? FolderTextBox.Text
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };

        if (dialog.ShowDialog() == Forms.DialogResult.OK)
            FolderTextBox.Text = dialog.SelectedPath;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        ValidationInfoBar.IsOpen = false;

        var alias = AliasTextBox.Text.Trim();
        if (!AliasPattern.IsMatch(alias))
        {
            ShowValidation(
                "Alias must be 1-64 letters or numbers, with optional dots, dashes, or underscores.");
            AliasTextBox.Focus();
            return;
        }

        if (_reservedAliases.Contains(alias))
        {
            ShowValidation($"The alias '{alias}' already exists.");
            AliasTextBox.Focus();
            return;
        }

        var folder = FolderTextBox.Text.Trim();
        if (!Path.IsPathFullyQualified(folder) || !Directory.Exists(folder))
        {
            ShowValidation("Choose an existing folder using an absolute path.");
            FolderTextBox.Focus();
            return;
        }

        Result = new WorkspaceConfigurationEntry
        {
            Alias = alias,
            Path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)),
            Writable = WritableCheckBox.IsChecked == true,
            Description = string.IsNullOrWhiteSpace(DescriptionTextBox.Text)
                ? null
                : DescriptionTextBox.Text.Trim()
        };

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    private void ShowValidation(string message)
    {
        ValidationInfoBar.Title = "Check this workspace";
        ValidationInfoBar.Message = message;
        ValidationInfoBar.IsOpen = true;
    }
}
