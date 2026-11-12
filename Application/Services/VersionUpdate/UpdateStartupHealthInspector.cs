using System.Collections.Generic;

namespace MAAUnified.Application.Services.VersionUpdate;

/// <summary>
/// 启动期更新健康度：跨启动保留的更新失败标记、安装修复标记与安装文件完整性。
/// </summary>
/// <param name="HasPersistedUpdateFailure">上次更新失败标记仍存在。</param>
/// <param name="UpdateFailureReason">标记文件里记录的失败原因；读取失败时为空字符串。</param>
/// <param name="HasMissingInstallationFiles"><c>filelist.txt</c> 对照后确实存在缺失文件。</param>
/// <param name="MissingFiles">缺失文件相对路径。</param>
/// <param name="HasPersistedRepairRequirement">安装修复标记仍存在（此前 filelist 校验失败）。</param>
/// <param name="RepairRequirementReason">修复标记里记录的原因；无标记时为 <c>null</c>。</param>
public sealed record UpdateStartupHealthReport(
    bool HasPersistedUpdateFailure,
    string? UpdateFailureReason,
    bool HasMissingInstallationFiles,
    IReadOnlyList<string> MissingFiles,
    bool HasPersistedRepairRequirement = false,
    string? RepairRequirementReason = null)
{
    /// <summary>
    /// 是否需要提示用户修复（失败标记、修复标记或安装文件缺失任一成立即需要）。
    /// 资源损坏期间只允许完整包更新（WPF <c>da79ff88ad</c>）。
    /// </summary>
    public bool RequiresRepair
        => HasPersistedUpdateFailure || HasMissingInstallationFiles || HasPersistedRepairRequirement;
}

/// <summary>
/// 启动期更新健康度探测。默认只读：不删除失败标记、不修改配置，判定与提示分开，便于测试。
/// 传 <c>persistInstallationRepairMarker</c> 时把本次 filelist 校验失败写成跨启动保留的修复标记，
/// 后续下载入口据此强制完整包；标记直到完整包应用成功才清除。
/// </summary>
public static class UpdateStartupHealthInspector
{
    public static UpdateStartupHealthReport Inspect(
        string baseDirectory,
        bool persistInstallationRepairMarker = false)
    {
        var failureReason = PendingUpdateFailureState.TryRead(baseDirectory);
        var integrity = InstallationIntegrityChecker.Check(baseDirectory);
        if (persistInstallationRepairMarker && !integrity.Passed)
        {
            UpdateRepairState.Write(
                baseDirectory,
                $"Installation integrity check failed: {integrity.MissingFiles.Count} file(s) missing.");
        }

        var repairReason = UpdateRepairState.TryRead(baseDirectory);
        return new UpdateStartupHealthReport(
            HasPersistedUpdateFailure: failureReason is not null,
            UpdateFailureReason: failureReason,
            HasMissingInstallationFiles: !integrity.Passed,
            MissingFiles: integrity.MissingFiles,
            HasPersistedRepairRequirement: repairReason is not null,
            RepairRequirementReason: repairReason);
    }
}
