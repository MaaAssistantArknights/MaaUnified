using System.IO;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using MAAUnified.App.Features.Dialogs;
using MAAUnified.App.ViewModels.Settings;
using MAAUnified.Application.Models;
using MAAUnified.Compat.Constants;

namespace MAAUnified.App.Features.Settings;

public partial class ConfigurationManagerView : UserControl
{
    private bool _suppressProfileSelectionChanged;

    public ConfigurationManagerView()
    {
        InitializeComponent();
    }

    private SettingsPageViewModel? VM => DataContext as SettingsPageViewModel;
    private string T(string key) => VM?.RootTexts[key] ?? key;

    private async void OnConfigurationProfileSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_suppressProfileSelectionChanged)
        {
            return;
        }

        if (VM is not null)
        {
            await VM.SwitchConfigurationProfileAsync();
        }
    }

    private async void OnCreateProfileClick(object? sender, RoutedEventArgs e)
    {
        if (VM is not null)
        {
            await VM.AddConfigurationProfileAsync();
        }
    }

    private async void OnDeleteCurrentProfileClick(object? sender, RoutedEventArgs e)
    {
        await ShowDeleteCurrentProfileDialogAsync();
    }

    private async void OnDeleteProfileEntryClick(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;

        if (VM is null || sender is not Button { Tag: string targetProfile } button)
        {
            return;
        }

        await ShowDeleteCurrentProfileDialogAsync(targetProfile, ResolveOwningDropDown(button));
    }

    private async Task ShowDeleteCurrentProfileDialogAsync(string? targetProfile = null, Control? reopenOwner = null)
    {
        var vm = VM;
        if (vm is null)
        {
            return;
        }

        var target = (targetProfile ?? vm.ConfigurationManagerSelectedProfile)?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(target))
        {
            return;
        }

        var confirmed = await ShowWarningDialogAsync(
            vm.Language,
            T("Settings.ConfigurationManager.Dialog.DeleteTitle"),
            string.Format(
                CultureInfo.CurrentCulture,
                T("Settings.ConfigurationManager.Dialog.DeleteMessage"),
                target),
            confirmText: T("Settings.Action.Delete"),
            cancelText: T("Settings.Action.Cancel"),
            sourceScope: "Settings.ConfigurationManager.DeleteConfirm.Dialog");
        if (!confirmed)
        {
            ReopenOwningDropDown(reopenOwner);
            return;
        }

        _suppressProfileSelectionChanged = true;
        try
        {
            vm.ConfigurationManagerSelectedProfile = target;
            await vm.DeleteConfigurationProfileAsync();
        }
        finally
        {
            _suppressProfileSelectionChanged = false;
            ReopenOwningDropDown(reopenOwner);
        }
    }

    private void OnDropdownDeleteButtonPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        e.Handled = true;
    }

    private void OnDropdownDeleteButtonPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        e.Handled = true;
    }

    private async void OnExportAllProfilesClick(object? sender, RoutedEventArgs e)
    {
        var vm = VM;
        if (vm is null)
        {
            return;
        }

        var savePath = await PickSavePathAsync(
            T("Settings.ConfigurationManager.Dialog.ExportAllTitle"),
            BuildSuggestedFileName(
                T("Settings.ConfigurationManager.ExportAll.FileNamePrefix"),
                profileName: null));
        if (string.IsNullOrWhiteSpace(savePath))
        {
            return;
        }

        await vm.ExportAllConfigurationsAsync(savePath);
    }

    private async void OnExportCurrentProfileClick(object? sender, RoutedEventArgs e)
    {
        var vm = VM;
        if (vm is null)
        {
            return;
        }

        var savePath = await PickSavePathAsync(
            T("Settings.ConfigurationManager.Dialog.ExportCurrentTitle"),
            BuildSuggestedFileName(
                T("Settings.ConfigurationManager.ExportCurrent.FileNamePrefix"),
                vm.ConfigurationManagerSelectedProfile));
        if (string.IsNullOrWhiteSpace(savePath))
        {
            return;
        }

        await vm.ExportCurrentConfigurationAsync(savePath);
    }

    private async void OnImportProfilesClick(object? sender, RoutedEventArgs e)
    {
        var vm = VM;
        if (vm is null)
        {
            return;
        }
        var language = vm.Language;
        Func<string, string> text = key => vm.RootTexts[key];

        while (true)
        {
            var analysis = await ShowImportModeDialogAsync(text);
            if (analysis is null)
            {
                return;
            }

            switch (analysis.Kind)
            {
                case ConfigurationImportSelectionKind.UnifiedConfig:
                    await vm.ImportConfigurationsAsync(analysis.UnifiedConfigPath!);
                    return;

                case ConfigurationImportSelectionKind.LegacyReady:
                    if (await TryRunLegacyImportAsync(vm, analysis, language, text))
                    {
                        return;
                    }

                    continue;

                case ConfigurationImportSelectionKind.LegacyPartial:
                {
                    var forceImport = await ShowWarningDialogAsync(
                        language,
                        text("Settings.ConfigurationManager.Dialog.ImportLegacyTitle"),
                        analysis.Message,
                        confirmText: text("Settings.Action.ImportAnyway"),
                        cancelText: text("Settings.Action.ChooseAgain"),
                        sourceScope: "Settings.ConfigurationManager.ImportLegacyPartial.Dialog");
                    if (!forceImport)
                    {
                        continue;
                    }

                    if (await TryRunLegacyImportAsync(vm, analysis, language, text, allowPartialImport: true))
                    {
                        return;
                    }

                    continue;
                }

                default:
                {
                    var close = await ShowWarningDialogAsync(
                        language,
                        text("Settings.ConfigurationManager.Dialog.ImportConfigTitle"),
                        analysis.Message,
                        confirmText: text("Settings.Action.Close"),
                        cancelText: text("Settings.Action.ChooseAgain"),
                        sourceScope: "Settings.ConfigurationManager.ImportSelection.Dialog");
                    if (!close)
                    {
                        continue;
                    }

                    return;
                }
            }
        }
    }

    private async Task<string?> PickSavePathAsync(string title, string suggestedFileName)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is not { CanSave: true } storageProvider)
        {
            return null;
        }

        var file = await storageProvider.SaveFilePickerAsync(
            new FilePickerSaveOptions
            {
                Title = title,
                SuggestedFileName = suggestedFileName,
                DefaultExtension = "json",
                ShowOverwritePrompt = true,
                FileTypeChoices =
                [
                    CreateJsonFileType(T("Settings.ConfigurationManager.FileType.Json")),
                ],
            });
        return file?.TryGetLocalPath();
    }

    private async Task<ConfigurationImportSelectionAnalysis?> PickLegacyWindowsImportAsync(
        TopLevel topLevel,
        Func<string, string> text)
    {
        var directory = await PickImportFolderAsync(
            topLevel,
            text("Settings.ConfigurationManager.Dialog.ImportLegacyWindowsFolderTitle"));
        return string.IsNullOrWhiteSpace(directory)
            ? null
            : ConfigurationImportSelectionAnalyzer.AnalyzeLegacyDirectory(directory, text);
    }

    private async Task<ConfigurationImportSelectionAnalysis?> PickUnifiedImportAsync(
        TopLevel topLevel,
        Func<string, string> text)
    {
        var directory = await PickImportFolderAsync(
            topLevel,
            text("Settings.ConfigurationManager.Dialog.ImportUnifiedFolderTitle"));
        return string.IsNullOrWhiteSpace(directory)
            ? null
            : ConfigurationImportSelectionAnalyzer.AnalyzeUnifiedDirectory(directory, text);
    }

    private async Task<ConfigurationImportSelectionAnalysis?> PickManualImportAsync(
        TopLevel topLevel,
        Func<string, string> text)
    {
        var importPaths = await PickImportPathsAsync(
            topLevel,
            text("Settings.ConfigurationManager.Dialog.ImportTitle"),
            text("Settings.ConfigurationManager.FileType.Json"));
        return importPaths.Count == 0
            ? null
            : ConfigurationImportSelectionAnalyzer.Analyze(importPaths, text);
    }

    private static async Task<string?> PickImportFolderAsync(TopLevel topLevel, string title)
    {
        if (topLevel?.StorageProvider is not { CanOpen: true } storageProvider)
        {
            return null;
        }

        var folders = await storageProvider.OpenFolderPickerAsync(
            new FolderPickerOpenOptions
            {
                Title = title,
                AllowMultiple = false,
            });
        return folders
            .Select(folder => folder.TryGetLocalPath())
            .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
    }

    private static async Task<IReadOnlyList<string>> PickImportPathsAsync(TopLevel topLevel, string title, string jsonTypeDisplayName)
    {
        if (topLevel?.StorageProvider is not { CanOpen: true } storageProvider)
        {
            return [];
        }

        var files = await storageProvider.OpenFilePickerAsync(
            new FilePickerOpenOptions
            {
                Title = title,
                AllowMultiple = true,
                FileTypeFilter =
                [
                    CreateJsonFileType(jsonTypeDisplayName),
                ],
            });
        return files
            .Select(file => file.TryGetLocalPath())
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Cast<string>()
            .ToArray();
    }

    private static async Task<bool> TryRunLegacyImportAsync(
        SettingsPageViewModel vm,
        ConfigurationImportSelectionAnalysis analysis,
        string? language,
        Func<string, string> text,
        bool allowPartialImport = false)
    {
        var request = new LegacyImportRequest(
            LegacyConfigSnapshot.FromPaths(analysis.GuiNewPath, analysis.GuiPath),
            analysis.LegacyImportSource,
            ManualImport: true,
            AllowPartialImport: allowPartialImport,
            FallbackGlobalValues: BuildLegacyImportFallbackGlobalValues(vm));
        var report = await vm.ImportLegacyConfigurationsAsync(request);
        if (report.AppliedConfig)
        {
            await ShowUnreadableImportedValuesDialogAsync(report, language, text);
            return true;
        }

        if (report.DamagedFiles.Count > 0 && report.ImportedFiles.Count > 0 && !allowPartialImport)
        {
            var importUsable = await ShowWarningDialogAsync(
                language,
                text("Settings.ConfigurationManager.Dialog.ImportLegacyTitle"),
                string.Format(
                    CultureInfo.CurrentCulture,
                    text("Settings.ConfigurationManager.Dialog.DamagedFiles"),
                    string.Join(", ", report.DamagedFiles)),
                confirmText: text("Settings.Action.ImportValidContent"),
                cancelText: text("Settings.Action.ChooseAgain"),
                sourceScope: "Settings.ConfigurationManager.ImportLegacyDamagedFiles.Dialog");
            if (!importUsable)
            {
                return false;
            }

            var retriedReport = await vm.ImportLegacyConfigurationsAsync(request with { AllowPartialImport = true });
            if (retriedReport.AppliedConfig)
            {
                await ShowUnreadableImportedValuesDialogAsync(retriedReport, language, text);
                return true;
            }

            if (retriedReport.DamagedFiles.Count > 0)
            {
                var close = await ShowWarningDialogAsync(
                    language,
                    text("Settings.ConfigurationManager.Dialog.ImportLegacyTitle"),
                    string.Format(
                        CultureInfo.CurrentCulture,
                        text("Settings.ConfigurationManager.Dialog.DamagedFilesNoImport"),
                        string.Join(", ", retriedReport.DamagedFiles)),
                    confirmText: text("Settings.Action.Close"),
                    cancelText: text("Settings.Action.ChooseAgain"),
                    sourceScope: "Settings.ConfigurationManager.ImportLegacyNoImport.Dialog");
                return close;
            }

            return true;
        }

        if (report.DamagedFiles.Count > 0)
        {
            var close = await ShowWarningDialogAsync(
                language,
                text("Settings.ConfigurationManager.Dialog.ImportLegacyTitle"),
                string.Format(
                    CultureInfo.CurrentCulture,
                    text("Settings.ConfigurationManager.Dialog.DamagedFilesNoImport"),
                    string.Join(", ", report.DamagedFiles)),
                confirmText: text("Settings.Action.Close"),
                cancelText: text("Settings.Action.ChooseAgain"),
                sourceScope: "Settings.ConfigurationManager.ImportLegacyFailed.Dialog");
            return close;
        }

        return true;
    }

    private static async Task ShowUnreadableImportedValuesDialogAsync(
        ImportReport report,
        string? language,
        Func<string, string> text)
    {
        if (report.UnreadableValues.Count == 0)
        {
            return;
        }

        await ShowWarningDialogAsync(
            language,
            text("Settings.ConfigurationManager.Dialog.ImportUnreadableValuesTitle"),
            BuildUnreadableImportedValuesMessage(report, language, text),
            confirmText: text("Settings.Action.GotIt"),
            cancelText: string.Empty,
            sourceScope: "Settings.ConfigurationManager.ImportUnreadableValues.Dialog",
            showCancelButton: false);
    }

    private static string BuildUnreadableImportedValuesMessage(
        ImportReport report,
        string? language,
        Func<string, string> text)
    {
        var useChinese = string.IsNullOrWhiteSpace(language)
                         || language.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        var builder = new StringBuilder();
        foreach (var value in report.UnreadableValues)
        {
            var displayName = ResolveImportedValueDisplayName(value, text);
            var configurationName = string.IsNullOrWhiteSpace(value.ConfigurationName)
                ? (useChinese ? "全局" : "Global")
                : value.ConfigurationName.Trim();

            if (builder.Length > 0)
            {
                builder.AppendLine();
            }

            if (useChinese)
            {
                builder
                    .Append(configurationName)
                    .Append(" 配置的 ")
                    .Append(displayName)
                    .Append(" 导入失败，无法读取 ")
                    .Append(displayName)
                    .Append(" 配置。请手动修改。");
            }
            else
            {
                builder
                    .Append("Failed to import ")
                    .Append(displayName)
                    .Append(" in ")
                    .Append(configurationName)
                    .Append(". The ")
                    .Append(displayName)
                    .Append(" configuration could not be read. Please edit it manually.");
            }
        }

        return builder.ToString();
    }

    private static string ResolveImportedValueDisplayName(
        ImportUnreadableConfigValue value,
        Func<string, string> text)
    {
        if (!string.IsNullOrWhiteSpace(value.DisplayResourceKey))
        {
            var localized = text(value.DisplayResourceKey);
            if (!string.Equals(localized, value.DisplayResourceKey, StringComparison.Ordinal))
            {
                return localized;
            }
        }

        return string.IsNullOrWhiteSpace(value.DisplayName)
            ? value.Key
            : value.DisplayName;
    }

    private async Task<ConfigurationImportSelectionAnalysis?> ShowImportModeDialogAsync(Func<string, string> text)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel is null)
        {
            return null;
        }

        if (topLevel is not Window owner)
        {
            return await PickManualImportAsync(topLevel, text);
        }

        var dialog = new ConfigurationImportModeDialogView();
        dialog.ApplyRequest(new ConfigurationImportModeDialogRequest(
            Title: text("Settings.ConfigurationManager.ImportMode.Title"),
            LegacyWindowsTitle: text("Settings.ConfigurationManager.ImportMode.LegacyWindows"),
            LegacyWindowsDescription: text("Settings.ConfigurationManager.ImportMode.LegacyWindowsDescription"),
            UnifiedTitle: text("Settings.ConfigurationManager.ImportMode.Unified"),
            UnifiedDescription: text("Settings.ConfigurationManager.ImportMode.UnifiedDescription"),
            ManualTitle: text("Settings.ConfigurationManager.ImportMode.Manual"),
            ManualDescription: text("Settings.ConfigurationManager.ImportMode.ManualDescription")));
        DialogWindowScaling.ApplyOwnerUiScale(dialog, owner);
        dialog.Show(owner);

        try
        {
            var mode = await dialog.WaitForSelectionAsync();
            return mode switch
            {
                ConfigurationImportMode.LegacyWindows => await PickLegacyWindowsImportAsync(dialog, text),
                ConfigurationImportMode.Unified => await PickUnifiedImportAsync(dialog, text),
                ConfigurationImportMode.Manual => await PickManualImportAsync(dialog, text),
                _ => null,
            };
        }
        finally
        {
            dialog.CompleteSelectionFlowAndClose();
        }
    }

    private static IReadOnlyDictionary<string, JsonNode?> BuildLegacyImportFallbackGlobalValues(SettingsPageViewModel vm)
    {
        return new Dictionary<string, JsonNode?>(StringComparer.OrdinalIgnoreCase)
        {
            [ConfigurationKeys.UiScalePercent] = JsonValue.Create(vm.UiScalePercent),
        };
    }

    private static async Task<bool> ShowWarningDialogAsync(
        string? language,
        string title,
        string message,
        string confirmText,
        string cancelText,
        string sourceScope,
        bool showCancelButton = true)
    {
        var completion = await ResolveDialogService().ShowWarningConfirmAsync(
            new WarningConfirmDialogRequest(
                Title: title,
                Message: message,
                ConfirmText: confirmText,
                CancelText: cancelText,
                Language: language ?? "en-us",
                ShowCancelButton: showCancelButton),
            sourceScope);
        return completion.Return == DialogReturnSemantic.Confirm;
    }

    private static IAppDialogService ResolveDialogService()
    {
        return Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime
            ? new AvaloniaDialogService(App.Runtime)
            : NoOpAppDialogService.Instance;
    }

    private static Control? ResolveOwningDropDown(Control source)
    {
        if (source is Visual visual)
        {
            if (visual.FindAncestorOfType<ComboBox>() is { } comboBox)
            {
                return comboBox;
            }

        }

        return null;
    }

    private static void ReopenOwningDropDown(Control? owner)
    {
        switch (owner)
        {
            case ComboBox comboBox:
                comboBox.IsDropDownOpen = true;
                break;
        }
    }

    private static FilePickerFileType CreateJsonFileType(string displayName) => new(displayName)
    {
        Patterns = ["*.json"],
        MimeTypes = ["application/json"],
    };

    private static string BuildSuggestedFileName(string prefix, string? profileName)
    {
        var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var safePrefix = SanitizeFileNameSegment(prefix);
        var safeProfileName = SanitizeFileNameSegment(profileName);

        var baseName = string.Join(
            "-",
            new[] { "maa", safePrefix, safeProfileName, timestamp }
                .Where(segment => !string.IsNullOrWhiteSpace(segment)));
        return $"{baseName}.json";
    }

    private static string SanitizeFileNameSegment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var invalidChars = Path.GetInvalidFileNameChars();
        return new string(value
                .Trim()
                .Select(c => invalidChars.Contains(c) ? '_' : c)
                .ToArray())
            .Trim();
    }
}
