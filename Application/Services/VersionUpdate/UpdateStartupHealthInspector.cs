using System.Collections.Generic;

namespace MAAUnified.Application.Services.VersionUpdate;

/// <summary>
/// 启动期更新健康度：跨启动保留的更新失败标记 + 安装文件完整性。
/// </summary>
/// <param name="HasPersistedUpdateFailure">上次更新失败标记仍存在。</param>
/// <param name="UpdateFailureReason">标记文件里记录的失败原因；读取失败时为空字符串。</param>
/// <param name="HasMissingInstallationFiles"><c>filelist.txt</c> 对照后确实存在缺失文件。</param>
/// <param name="MissingFiles">缺失文件相对路径。</param>
public sealed record UpdateStartupHealthReport(
    bool HasPersistedUpdateFailure,
    string? UpdateFailureReason,
    bool HasMissingInstallationFiles,
    IReadOnlyList<string> MissingFiles)
{
    /// <summary>
    /// 是否需要提示用户修复（失败标记或安装文件缺失任一成立即需要）。
    /// 资源损坏期间只允许完整包更新（WPF <c>da79ff88ad</c>）。
    /// </summary>
    public bool RequiresRepair => HasPersistedUpdateFailure || HasMissingInstallationFiles;
}

/// <summary>
/// 启动期更新健康度探测。只读：不删除失败标记、不修改配置，判定与提示分开，便于测试。
/// </summary>
public static class UpdateStartupHealthInspector
{
    public static UpdateStartupHealthReport Inspect(string baseDirectory)
    {
        var failureReason = PendingUpdateFailureState.TryRead(baseDirectory);
        var integrity = InstallationIntegrityChecker.Check(baseDirectory);
        return new UpdateStartupHealthReport(
            HasPersistedUpdateFailure: failureReason is not null,
            UpdateFailureReason: failureReason,
            HasMissingInstallationFiles: !integrity.Passed,
            MissingFiles: integrity.MissingFiles);
    }
}
