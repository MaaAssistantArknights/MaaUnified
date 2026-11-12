using System.Text.Json.Nodes;
using MAAUnified.Application.Models;
using MAAUnified.Application.Services;

namespace MAAUnified.App.ViewModels.TaskQueue;

/// <summary>
/// 更换主题任务（WPF dev-v2 9c86aae937）。候选主题名的列表式编辑沿用仓库既有的
/// 多行文本约定（见 Mall 的 buy_first / blacklist）：每行一个主题名，分号亦可分隔。
/// </summary>
public sealed class SwitchThemeModuleViewModel : TaskModuleSettingsViewModelBase
{
    private string _themesText = string.Empty;

    public SwitchThemeModuleViewModel(MAAUnifiedRuntime runtime, LocalizedTextMap texts)
        : base(runtime, texts, TaskModuleTypes.SwitchTheme)
    {
    }

    public string ThemesText
    {
        get => _themesText;
        set
        {
            var normalized = NormalizeThemesText(value);
            if (!SetProperty(ref _themesText, normalized))
            {
                return;
            }

            OnPropertyChanged(nameof(HasThemes));
            OnPropertyChanged(nameof(ShowEmptyHint));
            QueuePersist();
        }
    }

    public bool HasThemes => ParseThemes(ThemesText).Count > 0;

    public bool ShowEmptyHint => !HasThemes;

    public string EmptyHintText => Texts.GetOrDefault(
        "SwitchTheme.EmptyHint",
        "No theme name entered; this task is skipped at run time.");

    public static List<string> ParseThemes(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return text
            .Replace('；', ';')
            .Split([';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToList();
    }

    protected override Task LoadFromParametersAsync(JsonObject parameters, CancellationToken cancellationToken)
    {
        var model = SwitchThemeParams.FromJson(parameters);
        ThemesText = string.Join(Environment.NewLine, model.Themes);
        return Task.CompletedTask;
    }

    protected override JsonObject BuildParameters()
    {
        return new SwitchThemeParams
        {
            Themes = ParseThemes(ThemesText),
        }.ToJson();
    }

    private static string NormalizeThemesText(string? text)
        => string.IsNullOrWhiteSpace(text)
            ? string.Empty
            : text.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
}
