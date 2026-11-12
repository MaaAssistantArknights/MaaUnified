namespace MAAUnified.Application.Configuration;

/// <summary>
/// WPF dev-v2 <c>CurrentConfig.Copilot</c> 中 MAAUnified 此前没有对应扁平键的设置项。
/// 其余 Copilot 键沿用 <see cref="MAAUnified.Compat.Constants.ConfigurationKeys"/> 的历史常量，
/// 避免新增常量进入冻结 WPF 键表审计（BaselineConfigKeys_ShouldSeparateFrozenWpfKeysFromAvaloniaExtensions）。
/// </summary>
public static class CopilotSettingKeys
{
    public const string UseFormation = "Copilot.UseFormation";
}
