using System.Diagnostics;

namespace MAAUnified.Application.Services.Features;

/// <summary>
/// Discovers running emulator processes and their bundled ADB executables.
/// Mirrors the WPF freeze WinAdapter behavior (process names, emulator ids, relative ADB paths).
/// Process enumeration only runs on Windows; the mapping and path resolution logic is
/// platform independent so it can be unit tested on any host.
/// </summary>
internal static class EmulatorRuntimeDiscovery
{
    private static readonly IReadOnlyDictionary<string, string> EmulatorIdByProcessName =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["HD-Player"] = "BlueStacks",
            ["dnplayer"] = "LDPlayer",
            ["Nox"] = "Nox",
            ["MuMuPlayer"] = "MuMuEmulator12",
            ["MuMuNxDevice"] = "MuMuEmulator12",
            ["MEmu"] = "XYAZ",
        };

    private static readonly IReadOnlyDictionary<string, IReadOnlyList<string>> AdbRelativePathsByEmulatorId =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["BlueStacks"] = [@"HD-Adb.exe", @"Engine\ProgramFiles\HD-Adb.exe"],
            ["LDPlayer"] = ["adb.exe"],
            ["Nox"] = ["nox_adb.exe"],
            ["MuMuEmulator12"] =
            [
                @"..\..\..\nx_main\adb.exe",
                @"..\vmonitor\bin\adb_server.exe",
                @"..\..\MuMu\emulator\nemu\vmonitor\bin\adb_server.exe",
                "adb.exe",
            ],
            ["XYAZ"] = ["adb.exe"],
        };

    public static bool TryMapEmulatorProcessName(string? processName, out string emulatorId)
    {
        emulatorId = string.Empty;
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        return EmulatorIdByProcessName.TryGetValue(processName.Trim(), out emulatorId!);
    }

    public static string? ResolveEmulatorAdbPath(
        string emulatorId,
        string? processFilePath,
        Func<string, bool>? fileExists = null)
    {
        if (string.IsNullOrWhiteSpace(processFilePath)
            || !AdbRelativePathsByEmulatorId.TryGetValue(emulatorId, out var relativePaths))
        {
            return null;
        }

        var directory = Path.GetDirectoryName(processFilePath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return null;
        }

        fileExists ??= File.Exists;

        // WPF parity (WinAdapter.RefreshEmulatorsInfo): the WPF loop overwrites the resolved
        // ADB path for every existing relative path, so the last existing entry in the list
        // wins. Iterate in reverse so the first hit matches that last-wins semantics.
        for (var index = relativePaths.Count - 1; index >= 0; index--)
        {
            var relativePath = relativePaths[index];
            string candidate;
            try
            {
                candidate = Path.GetFullPath(CombineRelativePath(directory, relativePath));
            }
            catch (Exception)
            {
                continue;
            }

            if (fileExists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    public static IReadOnlyList<EmulatorRuntimeInfo> DetectRunningEmulators(
        Func<IReadOnlyList<EmulatorProcessSnapshot>>? processProvider = null,
        Func<string, bool>? fileExists = null)
    {
        if (processProvider is null && !OperatingSystem.IsWindows())
        {
            return [];
        }

        IReadOnlyList<EmulatorProcessSnapshot> processes;
        try
        {
            processes = processProvider?.Invoke() ?? EnumerateProcesses();
        }
        catch (Exception)
        {
            return [];
        }

        var discovered = new List<EmulatorRuntimeInfo>();
        foreach (var process in processes)
        {
            if (!TryMapEmulatorProcessName(process.ProcessName, out var emulatorId)
                || discovered.Any(existing => string.Equals(existing.EmulatorId, emulatorId, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            discovered.Add(new EmulatorRuntimeInfo(
                emulatorId,
                process.ProcessFilePath,
                ResolveEmulatorAdbPath(emulatorId, process.ProcessFilePath, fileExists)));
        }

        return discovered;
    }

    private static IReadOnlyList<EmulatorProcessSnapshot> EnumerateProcesses()
    {
        var processes = new List<EmulatorProcessSnapshot>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                processes.Add(new EmulatorProcessSnapshot(process.ProcessName, SafeGetProcessPath(process)));
            }
            catch (Exception)
            {
                // Best effort: skip processes that cannot be inspected.
            }
        }

        return processes;
    }

    private static string? SafeGetProcessPath(Process process)
    {
        try
        {
            return process.MainModule?.FileName;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string CombineRelativePath(string directory, string relativePath)
    {
        var combined = directory;
        foreach (var segment in relativePath.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(segment, ".", StringComparison.Ordinal))
            {
                continue;
            }

            combined = Path.Combine(combined, segment);
        }

        return combined;
    }
}

internal sealed record EmulatorProcessSnapshot(string ProcessName, string? ProcessFilePath);

internal sealed record EmulatorRuntimeInfo(string EmulatorId, string? ProcessFilePath, string? AdbPath);
