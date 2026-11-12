using MAAUnified.Application.Models;
using MAAUnified.Application.Services.Localization;

namespace MAAUnified.Application.Services;

/// <summary>
/// 禁止开始新任务的原因。判定顺序即优先级：资源破损 &gt; 需重启 &gt; 内核未初始化。
/// </summary>
public enum TaskStartBlockKind
{
    /// <summary>
    /// Core 资源与程序版本不匹配或损坏（例如任务链引用了未定义任务），
    /// 继续下发任务会让 Core 崩溃或静默缺步，必须先修复资源。
    /// </summary>
    ResourceBroken,

    /// <summary>
    /// 本会话状态已不可信（停止超时强收、GPU 推理设备被移除），须重启进程。
    /// </summary>
    RequiresRestart,

    /// <summary>
    /// Core 尚未初始化完成，任务无法下发。
    /// </summary>
    CoreNotReady,
}

/// <summary>
/// 一次启动拦截的判定结果。
/// </summary>
/// <param name="Kind">拦截原因。</param>
/// <param name="ErrorCode">与 <see cref="UiErrorCode"/> 对应的错误码。</param>
/// <param name="Message">本地化后的用户可见原因（含修复引导）。</param>
public readonly record struct TaskStartBlock(TaskStartBlockKind Kind, string ErrorCode, string Message);

/// <summary>
/// 统一的任务启动拦截判定。WPF parity（<c>Bootstrapper.TryGetTaskBlockReason</c>，
/// <c>2e0ff15edd</c>/<c>a413ff2cfd</c>/<c>43965095df</c>）：所有会向 Core 下发任务的入口
/// （主队列、单次运行、Copilot、工具箱、远程控制、托盘/热键）统一经此判定，
/// 避免各入口各写一套 gate 后漏判或判定顺序不一致。
/// </summary>
public sealed class TaskStartGate
{
    private readonly UiLogService? _logService;
    private readonly Func<string> _languageProvider;
    private int _resourceBroken;
    private int _requiresRestart;
    private int _coreReady = 1;

    /// <summary>
    /// Initializes a new instance of the <see cref="TaskStartGate"/> class.
    /// </summary>
    /// <param name="logService">可选的运行日志；有值时记录拦截与状态迁移原因。</param>
    /// <param name="languageProvider">可选的当前 UI 语言提供器；缺省使用默认语言。</param>
    public TaskStartGate(UiLogService? logService = null, Func<string>? languageProvider = null)
    {
        _logService = logService;
        _languageProvider = languageProvider ?? (() => UiLanguageCatalog.DefaultLanguage);
    }

    /// <summary>
    /// 资源破损时触发，参数为本地化的拦截原因（含修复引导）。供 Shell 弹右上角提示，
    /// 保证资源破损不只写日志。
    /// </summary>
    public event Action<string>? ResourceBrokenMarked;

    /// <summary>
    /// Gets a value indicating whether the loaded Core resource is known to be broken this session.
    /// </summary>
    public bool IsResourceBroken => Volatile.Read(ref _resourceBroken) != 0;

    /// <summary>
    /// Gets the diagnostic reason recorded when <see cref="IsResourceBroken"/> was first set.
    /// </summary>
    public string? ResourceBrokenReason { get; private set; }

    /// <summary>
    /// Gets a value indicating whether this session must restart before new tasks may run.
    /// </summary>
    public bool RequiresRestart => Volatile.Read(ref _requiresRestart) != 0;

    /// <summary>
    /// Gets the diagnostic reason recorded when <see cref="RequiresRestart"/> was first set.
    /// </summary>
    public string? RequiresRestartReason { get; private set; }

    /// <summary>
    /// Gets a value indicating whether Core finished initialization. Defaults to
    /// <see langword="true"/> so hosts without a Core warmup phase (tests, UI preview) are not
    /// blocked by an event that never arrives.
    /// </summary>
    public bool IsCoreReady => Volatile.Read(ref _coreReady) != 0;

    /// <summary>
    /// Gets the message shown while Core is still loading.
    /// </summary>
    public string CoreNotReadyMessage { get; private set; } = string.Empty;

    /// <summary>
    /// 标记资源破损。幂等：首个原因与首次修复提示保留，重复标记不再重复告警。
    /// </summary>
    /// <param name="reason">诊断原因（触发点）。</param>
    public void MarkResourceBroken(string reason)
    {
        var normalized = NormalizeReason(reason);
        if (Interlocked.Exchange(ref _resourceBroken, 1) == 1)
        {
            return;
        }

        ResourceBrokenReason = normalized;
        _logService?.Error(
            $"Task start gate: resource is broken ({normalized}). Every task start entry is blocked until the resources reload successfully.");
        ResourceBrokenMarked?.Invoke(BuildBlockMessage(TaskStartBlockKind.ResourceBroken));
    }

    /// <summary>
    /// 解除资源破损状态。仅在资源重新加载或修复确认成功后调用。
    /// </summary>
    /// <param name="reason">诊断原因（解除来源）。</param>
    /// <returns>本次调用是否真的解除了拦截。</returns>
    public bool ClearResourceBroken(string reason)
    {
        if (Interlocked.Exchange(ref _resourceBroken, 0) != 1)
        {
            return false;
        }

        var normalized = NormalizeReason(reason);
        ResourceBrokenReason = null;
        _logService?.Info($"Task start gate: resource broken state cleared ({normalized}). Task starts are allowed again.");
        return true;
    }

    /// <summary>
    /// 标记本会话需重启后才能继续任务。幂等：首个原因保留。
    /// </summary>
    /// <param name="reason">诊断原因（触发点）。</param>
    public void MarkRequiresRestart(string reason)
    {
        var normalized = NormalizeReason(reason);
        if (Interlocked.Exchange(ref _requiresRestart, 1) == 1)
        {
            return;
        }

        RequiresRestartReason = normalized;
        _logService?.Warn(
            $"Task start gate: runtime requires restart ({normalized}). Further task starts are blocked until the process restarts.");
    }

    /// <summary>
    /// 更新 Core 初始化完成状态。Shell 预热路径负责调用。
    /// </summary>
    /// <param name="isReady">Core 是否可用。</param>
    /// <param name="message">Core 未就绪时的用户可见原因；为空时沿用上一次的值。</param>
    public void SetCoreReady(bool isReady, string? message = null)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            CoreNotReadyMessage = message.Trim();
        }

        Volatile.Write(ref _coreReady, isReady ? 1 : 0);
    }

    /// <summary>
    /// 获取当前禁止开始新任务的原因；<see langword="null"/> 表示可启动。
    /// 顺序固定为资源破损 &gt; 需重启 &gt; 内核未初始化（与 WPF 一致：缺任务时 Core 会直接崩溃，
    /// 停止超时后 Core 状态不可信，二者都比 ｢还没加载完｣ 更需要先处理）。
    /// </summary>
    /// <param name="scope">发起启动的入口标识，用于日志定位。</param>
    /// <param name="coreWarmupAvailable">
    /// 该入口自身会在启动流程里触发 Core 预热（MAAUnified 的延迟预热设计）。为
    /// <see langword="true"/> 时不把 ｢内核未初始化｣ 当作硬拦截，交给预热流程报告真实失败。
    /// </param>
    /// <returns>拦截结果；可启动时为 <see langword="null"/>。</returns>
    public TaskStartBlock? TryGetBlock(string scope, bool coreWarmupAvailable = false)
    {
        var normalizedScope = string.IsNullOrWhiteSpace(scope) ? "TaskStart" : scope.Trim();

        if (IsResourceBroken)
        {
            _logService?.Warn(
                $"{normalizedScope} blocked: resource is broken ({ResourceBrokenReason ?? "unspecified"}).");
            return new TaskStartBlock(
                TaskStartBlockKind.ResourceBroken,
                UiErrorCode.ResourceBroken,
                BuildBlockMessage(TaskStartBlockKind.ResourceBroken));
        }

        if (RequiresRestart)
        {
            _logService?.Warn(
                $"{normalizedScope} blocked: the session requires a restart ({RequiresRestartReason ?? "unspecified"}).");
            return new TaskStartBlock(
                TaskStartBlockKind.RequiresRestart,
                UiErrorCode.RequiresRestart,
                BuildBlockMessage(TaskStartBlockKind.RequiresRestart));
        }

        if (!IsCoreReady)
        {
            if (coreWarmupAvailable)
            {
                // 该入口会自行预热 Core：放行到预热步骤，避免启动路径把自己的预热挡在门外。
                return null;
            }

            _logService?.Warn($"{normalizedScope} blocked: core is still initializing.");
            return new TaskStartBlock(
                TaskStartBlockKind.CoreNotReady,
                UiErrorCode.CoreNotReady,
                BuildBlockMessage(TaskStartBlockKind.CoreNotReady));
        }

        return null;
    }

    private string BuildBlockMessage(TaskStartBlockKind kind)
    {
        return kind switch
        {
            TaskStartBlockKind.ResourceBroken => Localize(
                "ResourceBrokenTaskBlocked",
                "The resources are damaged, so tasks cannot be started. {key=ResourceBrokenRepairGuidance}"),
            TaskStartBlockKind.RequiresRestart => Localize(
                "RestartRecommendation",
                "It is recommended to restart MAA to restore normal state"),
            _ => string.IsNullOrWhiteSpace(CoreNotReadyMessage)
                ? Localize("CoreLoadingTip", "The MAA core is still loading. Please try again shortly.")
                : CoreNotReadyMessage,
        };
    }

    private string Localize(string key, string fallback)
    {
        var language = _languageProvider();
        return new UiLocalizer(language).GetOrDefault(key, fallback, "Root.Localization.TaskStartGate");
    }

    private static string NormalizeReason(string? reason)
        => string.IsNullOrWhiteSpace(reason) ? "unspecified" : reason.Trim();
}
