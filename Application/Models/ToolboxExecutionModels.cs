using MAAUnified.Application.Models.TaskParams;

namespace MAAUnified.Application.Models;

public enum ToolboxToolKind
{
    Recruit = 0,
    OperBox = 1,
    Depot = 2,
    Gacha = 3,
    VideoRecognition = 4,
    MiniGame = 5,
}

public sealed record ToolboxRecruitRequest(
    IReadOnlyList<int> SelectLevels,
    bool AutoSetTime,
    int Level3Time,
    int Level4Time,
    int Level5Time,
    string ServerType);

public sealed record ToolboxGachaRequest(bool Once);

/// <summary>
/// 小游戏条目请求。EventShop (<c>SS@Store@Begin</c>) 可附带活动商店黑名单关键词。
/// </summary>
public sealed record ToolboxMiniGameRequest(
    string TaskName,
    IReadOnlyList<string>? EventShopBlackList = null);

/// <summary>
/// 生息演算（原任务队列条目，dev-v2 66e003377c 起为工具箱小游戏）。
/// </summary>
public sealed record ToolboxReclamationRequest(ReclamationTaskParamsDto Parameters);

/// <summary>
/// 自动升级潜能（<c>MiniGame@AutoRaisePotential@Begin</c>）。
/// </summary>
public sealed record ToolboxAutoRaisePotentialRequest(bool UseNormalToken);

public sealed record ToolboxDispatchRequest(
    ToolboxToolKind Tool,
    ToolboxRecruitRequest? Recruit = null,
    ToolboxGachaRequest? Gacha = null,
    ToolboxMiniGameRequest? MiniGame = null,
    ToolboxReclamationRequest? Reclamation = null,
    ToolboxAutoRaisePotentialRequest? AutoRaisePotential = null,
    string? ParameterSummary = null);

public sealed record ToolboxDispatchResult(
    ToolboxToolKind Tool,
    string ParameterSummary,
    DateTimeOffset StartedAt,
    int? CoreTaskId = null,
    string? TaskType = null);
