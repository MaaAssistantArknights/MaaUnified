using System.Text.RegularExpressions;
using System.Xml.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using MAAUnified.App.Features.Advanced;
using MAAUnified.App.Features.Root;
using MAAUnified.App.ViewModels.Copilot;
using MAAUnified.App.ViewModels.TaskQueue;
using MAAUnified.Application.Services.Localization;

namespace MAAUnified.Tests;

public sealed class TaskQueueRowKeyboardRegressionTests
{
    private const string XamlNameNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";

    [Fact]
    public void RowCheckBoxes_ShouldNotRouteListLevelKeyboardOrContextCommands()
    {
        var selectedTask = new TaskQueueItemViewModel("Fight", "Selected task", true);
        var focusedTask = new TaskQueueItemViewModel("Fight", "Focused task", true);
        var taskCheckBox = new CheckBox
        {
            DataContext = focusedTask,
            Tag = focusedTask,
        };

        Assert.NotEqual(selectedTask.Name, focusedTask.Name);
        Assert.Same(focusedTask, taskCheckBox.DataContext);
        Assert.Same(focusedTask, taskCheckBox.Tag);
        Assert.Equal(
            TaskQueueView.TaskQueueRowMenuKeyboardAction.None,
            TaskQueueView.GetTaskQueueListKeyboardAction(Key.Space, KeyModifiers.None, taskCheckBox));
        Assert.Equal(
            TaskQueueView.TaskQueueRowMenuKeyboardAction.None,
            TaskQueueView.GetTaskQueueListKeyboardAction(Key.F10, KeyModifiers.Shift, taskCheckBox));
        Assert.False(TaskQueueView.ShouldHandleTaskQueueListContextRequest(taskCheckBox));

        var selectedCopilot = new CopilotItemViewModel("Selected copilot", "Normal");
        var focusedCopilot = new CopilotItemViewModel("Focused copilot", "Normal");
        var copilotCheckBox = new CheckBox
        {
            DataContext = focusedCopilot,
            Tag = focusedCopilot,
        };

        Assert.NotEqual(selectedCopilot.DisplayName, focusedCopilot.DisplayName);
        Assert.Same(focusedCopilot, copilotCheckBox.DataContext);
        Assert.Same(focusedCopilot, copilotCheckBox.Tag);
        Assert.Equal(
            CopilotView.CopilotListItemKeyboardAction.None,
            CopilotView.GetCopilotListKeyboardAction(Key.Space, KeyModifiers.None, copilotCheckBox));
        Assert.Equal(
            CopilotView.CopilotListItemKeyboardAction.None,
            CopilotView.GetCopilotListKeyboardAction(Key.F10, KeyModifiers.Shift, copilotCheckBox));
        Assert.False(CopilotView.ShouldHandleCopilotListContextRequest(copilotCheckBox));

        var root = TestRepoLayout.GetMaaUnifiedRoot();
        var taskQueueXaml = File.ReadAllText(Path.Combine(root, "App", "Features", "Root", "TaskQueueView.axaml"));
        var taskQueueCode = File.ReadAllText(Path.Combine(root, "App", "Features", "Root", "TaskQueueView.axaml.cs"));
        var copilotXaml = File.ReadAllText(Path.Combine(root, "App", "Features", "Advanced", "CopilotView.axaml"));
        var copilotCode = File.ReadAllText(Path.Combine(root, "App", "Features", "Advanced", "CopilotView.axaml.cs"));

        var taskCheckBoxXaml = Regex.Match(
            taskQueueXaml,
            "<CheckBox[^>]*OnTaskEnabledCheckBoxPointerPressed[^>]*/>",
            RegexOptions.Singleline);
        Assert.True(taskCheckBoxXaml.Success);
        Assert.Contains("Tag=\"{Binding}\"", taskCheckBoxXaml.Value, StringComparison.Ordinal);
        Assert.Contains("GetTaskQueueListKeyboardAction(e.Key, e.KeyModifiers, e.Source)", taskQueueCode, StringComparison.Ordinal);
        Assert.Contains("ShouldHandleTaskQueueListContextRequest(e.Source)", taskQueueCode, StringComparison.Ordinal);

        var copilotCheckBoxXaml = Regex.Match(
            copilotXaml,
            "<CheckBox[^>]*OnCopilotListItemCheckClick[^>]*>",
            RegexOptions.Singleline);
        Assert.True(copilotCheckBoxXaml.Success);
        Assert.Contains("Tag=\"{Binding}\"", copilotCheckBoxXaml.Value, StringComparison.Ordinal);
        Assert.Contains("GetCopilotListKeyboardAction(e.Key, e.KeyModifiers, e.Source)", copilotCode, StringComparison.Ordinal);
        Assert.Contains("ShouldHandleCopilotListContextRequest(e.Source)", copilotCode, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ProcessPickerDialogView.axaml", "RefreshButton")]
    [InlineData("ProcessPickerDialogView.axaml", "CancelButton")]
    [InlineData("ProcessPickerDialogView.axaml", "ConfirmButton")]
    [InlineData("EmulatorPathSelectionDialogView.axaml", "CancelButton")]
    [InlineData("EmulatorPathSelectionDialogView.axaml", "ConfirmButton")]
    public void DialogActionButtons_ShouldKeepWindowScopedEnterAndEscapeSemantics(
        string fileName,
        string focusedButtonName)
    {
        var root = TestRepoLayout.GetMaaUnifiedRoot();
        var document = XDocument.Parse(File.ReadAllText(Path.Combine(root, "App", "Features", "Dialogs", fileName)));
        var buttonName = XName.Get("Name", XamlNameNamespace);
        var buttons = document.Root!
            .Descendants(document.Root.Name.Namespace + "Button")
            .Where(button => button.Attribute(buttonName) is not null)
            .ToDictionary(button => button.Attribute(buttonName)!.Value, StringComparer.Ordinal);

        var focused = buttons[focusedButtonName];
        var confirm = buttons["ConfirmButton"];
        var cancel = buttons["CancelButton"];
        Assert.Equal("True", confirm.Attribute("IsDefault")?.Value);
        Assert.Equal("True", cancel.Attribute("IsCancel")?.Value);
        Assert.Same(confirm.Parent, focused.Parent);
    }

    [Theory]
    [InlineData("zh-cn", "bilibili", "源码: GitHub", "QQ 群", "QQ 频道", "Telegram", "Discord")]
    [InlineData("zh-tw", "bilibili", "原始碼：GitHub", "QQ 群", "QQ 頻道", "Telegram", "Discord")]
    [InlineData("en-us", "bilibili", "Source code: GitHub", "QQ group", "QQ channel", "Telegram", "Discord")]
    [InlineData("ja-jp", "bilibili", "ソースコード: GitHub", "QQグループ", "QQ channel", "Telegram", "Discord")]
    [InlineData("ko-kr", "bilibili", "소스 코드: GitHub", "QQ 그룹", "QQ 채널", "Telegram", "Discord")]
    [InlineData("pallas", "bilibili", "Source code: GitHub", "QQ group", "QQ channel", "Telegram", "Discord")]
    public void AboutLinkCaptions_ShouldResolveFromUiTextsForAllSixLanguages(
        string language,
        string bilibili,
        string github,
        string qqGroup,
        string qqChannel,
        string telegram,
        string discord)
    {
        var root = TestRepoLayout.GetMaaUnifiedRoot();
        var aboutXaml = File.ReadAllText(Path.Combine(root, "App", "Features", "Settings", "AboutSettingsView.axaml"));
        Assert.Contains("RootTexts[Settings.About.Bilibili]", aboutXaml, StringComparison.Ordinal);
        Assert.Contains("RootTexts[Settings.About.Github]", aboutXaml, StringComparison.Ordinal);
        Assert.Contains("RootTexts[Settings.About.QqGroup]", aboutXaml, StringComparison.Ordinal);
        Assert.Contains("RootTexts[Settings.About.QqChannel]", aboutXaml, StringComparison.Ordinal);
        Assert.Contains("RootTexts[Settings.About.Telegram]", aboutXaml, StringComparison.Ordinal);
        Assert.Contains("RootTexts[Settings.About.Discord]", aboutXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"bilibili\"", aboutXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"GitHub\"", aboutXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"QQ Group\"", aboutXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"QQ Channel\"", aboutXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"Telegram\"", aboutXaml, StringComparison.Ordinal);
        Assert.DoesNotContain("Content=\"Discord\"", aboutXaml, StringComparison.Ordinal);

        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Settings.About.Bilibili"] = bilibili,
            ["Settings.About.Github"] = github,
            ["Settings.About.QqGroup"] = qqGroup,
            ["Settings.About.QqChannel"] = qqChannel,
            ["Settings.About.Telegram"] = telegram,
            ["Settings.About.Discord"] = discord,
        };
        var manager = UiLocalizationResourceManager.Shared;
        var englishBaseline = expected.ToDictionary(
            pair => pair.Key,
            pair => manager.Lookup("en-us", pair.Key).Value,
            StringComparer.Ordinal);

        foreach (var (key, expectedValue) in expected)
        {
            var result = manager.Lookup(language, key);
            Assert.False(result.IsFallback, $"{language}:{key} unexpectedly fell back to {result.FallbackSource}.");
            if (string.Equals(language, "pallas", StringComparison.Ordinal))
            {
                Assert.StartsWith("[!! ", result.Value, StringComparison.Ordinal);
                Assert.NotEqual(englishBaseline[key], result.Value);
            }
            else
            {
                Assert.Equal(expectedValue, result.Value);
            }
        }
    }
}
