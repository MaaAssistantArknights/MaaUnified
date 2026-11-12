using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MAAUnified.Application.Services.VersionUpdate;

/// <summary>
/// 安装文件完整性检查结果。
/// </summary>
/// <param name="MissingFiles">缺失文件的相对路径（以 <c>/</c> 分隔），无缺失时为空。</param>
/// <param name="CheckedFileCount">实际参与检查的文件条目数。</param>
/// <param name="FileListAvailable">清单文件是否存在且可读；不存在时视为通过。</param>
public sealed record InstallationIntegrityResult(
    IReadOnlyList<string> MissingFiles,
    int CheckedFileCount,
    bool FileListAvailable)
{
    public bool Passed => MissingFiles.Count == 0;
}

/// <summary>
/// 对照 CI 生成的 <c>filelist.txt</c> 检查安装目录完整性。
/// 清单每行一个安装目录相对路径（正斜杠分隔）；清单缺失（旧版本升级、开发环境、资源包）时视为通过。
/// 与 WPF <c>ResourceIntegrityChecker</c>（<c>6591943eaf</c>）一致，检查时跳过 dll/exe/py：
/// 这些文件缺失时 UI 或内核会直接报错，无需本检查兜底；Python 目录供用户自行接入调用，允许删除。
/// 只做存在性检查：资源更新链路只增不删且只覆盖，清单不会因日常资源更新误报。
/// </summary>
public static class InstallationIntegrityChecker
{
    public const string FileListName = "filelist.txt";

    private static readonly HashSet<string> SkippedExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".dll",
        ".exe",
        ".py",
    };

    public static InstallationIntegrityResult Check(string baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            throw new ArgumentException("Base directory must be provided.", nameof(baseDirectory));
        }

        var fileListPath = Path.Combine(baseDirectory, FileListName);
        if (!File.Exists(fileListPath))
        {
            return new InstallationIntegrityResult([], 0, FileListAvailable: false);
        }

        string[] expectedFiles;
        try
        {
            expectedFiles = File.ReadAllLines(fileListPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 清单损坏或不可读时不阻断启动，交由调用方的日志链路记录。
            return new InstallationIntegrityResult([], 0, FileListAvailable: false);
        }

        var missingFiles = new List<string>();
        var checkedFiles = 0;
        foreach (var rawLine in expectedFiles)
        {
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                continue;
            }

            var relativePath = NormalizeEntry(rawLine);
            if (relativePath is null)
            {
                continue;
            }

            checkedFiles++;
            if (!File.Exists(Path.Combine(baseDirectory, relativePath)))
            {
                missingFiles.Add(relativePath);
            }
        }

        return new InstallationIntegrityResult(missingFiles, checkedFiles, FileListAvailable: true);
    }

    /// <summary>
    /// 规范化清单条目。目录条目（以 <c>/</c> 或 <c>\</c> 结尾）、绝对路径与带路径穿越的条目返回 <c>null</c>：
    /// 前者会让 <see cref="File.Exists(string)"/> 恒为 false 而整批误报，后者会把检查指向安装目录之外。
    /// </summary>
    private static string? NormalizeEntry(string rawLine)
    {
        var trimmed = rawLine.Trim();
        if (trimmed.Length == 0)
        {
            return null;
        }

        if (trimmed.EndsWith('/') || trimmed.EndsWith('\\'))
        {
            return null;
        }

        var normalized = trimmed.Replace('\\', '/');
        if (Path.IsPathRooted(trimmed) || normalized.Split('/').Contains("..", StringComparer.Ordinal))
        {
            return null;
        }

        if (SkippedExtensions.Contains(Path.GetExtension(normalized)))
        {
            return null;
        }

        return normalized;
    }
}
