namespace MAAUnified.Application.Configuration;

/// <summary>
/// WPF dev-v2 (66e003377c) 把工具箱设置拆为 <c>Toolbox.Recruitment/OperBox/Gacha/Peep/MiniGame</c>
/// 五个分节，生息演算从任务队列迁入 <c>Toolbox.MiniGame.Reclamation</c>。
/// MAAUnified 的工具箱设置仍以扁平键存放在全局配置里；新增键集中在此，
/// 避免新增常量进入冻结 WPF 键表审计（BaselineConfigKeys_ShouldSeparateFrozenWpfKeysFromAvaloniaExtensions）。
/// </summary>
public static class ToolboxSettingKeys
{
    /// <summary>
    /// dev-v2 的工具箱对象路径前缀；导入时该前缀下的键按分节映射或原样保留。
    /// </summary>
    public const string SectionPrefix = "Toolbox.";

    // MiniGame.Reclamation（dev-v2 Settings/Reclamation.cs）
    public const string ReclamationTheme = "Toolbox.MiniGame.Reclamation.Theme";
    public const string ReclamationMode = "Toolbox.MiniGame.Reclamation.Mode";
    public const string ReclamationIncrementMode = "Toolbox.MiniGame.Reclamation.IncrementMode";
    public const string ReclamationMaxCraftCountPerRound = "Toolbox.MiniGame.Reclamation.MaxCraftCountPerRound";
    public const string ReclamationToolToCraft = "Toolbox.MiniGame.Reclamation.ToolToCraft";
    public const string ReclamationClearStore = "Toolbox.MiniGame.Reclamation.ClearStore";

    // MiniGame.EventShop（dev-v2 Settings/MiniGame.cs）
    public const string EventShopBlackList = "Toolbox.MiniGame.EventShopBlackList";
    public const string EventShopBlackListDataSupplementInstrument = "Toolbox.MiniGame.EventShopBlackListDataSupplementInstrument";
    public const string EventShopBlackListDataSupplementStick = "Toolbox.MiniGame.EventShopBlackListDataSupplementStick";
    public const string EventShopBlackListLmd = "Toolbox.MiniGame.EventShopBlackListLmd";
    public const string EventShopBlackListFurniturePart = "Toolbox.MiniGame.EventShopBlackListFurniturePart";
    public const string EventShopBlackListOther = "Toolbox.MiniGame.EventShopBlackListOther";

    // MAAUnified extension: WPF keeps this VM-only (MiniGameUseNormalToken), persisting it
    // keeps the toolbox option stable across restarts.
    public const string AutoRaisePotentialUseNormalToken = "Toolbox.MiniGame.AutoRaisePotentialUseNormalToken";
}
