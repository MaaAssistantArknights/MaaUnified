using MAAUnified.Compat.Runtime;

namespace MAAUnified.Tests;

public sealed class LinuxPackagedRuntimeSeedTests
{
    [Fact]
    public void EnsureSeeded_WhenRunningInsideAppImage_CopiesNativeLibrariesAndResource()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var originalAppDir = Environment.GetEnvironmentVariable("APPDIR");
        var originalAppImage = Environment.GetEnvironmentVariable("APPIMAGE");
        var originalXdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var root = Path.Combine(Path.GetTempPath(), "maa-unified-linux-seed", Guid.NewGuid().ToString("N"));
        var appDir = Path.Combine(root, "AppDir");
        var packagedRuntimeBaseDirectory = Path.Combine(appDir, "usr", "share", "maaunified");
        var executableBaseDirectory = Path.Combine(packagedRuntimeBaseDirectory, "bin");
        var xdgDataHome = Path.Combine(root, "xdg-data");
        Directory.CreateDirectory(executableBaseDirectory);
        File.WriteAllText(Path.Combine(packagedRuntimeBaseDirectory, "libMaaCore.so"), "core");
        File.WriteAllText(Path.Combine(packagedRuntimeBaseDirectory, "libMaaUtils.so"), "utils");
        Directory.CreateDirectory(Path.Combine(packagedRuntimeBaseDirectory, "resource"));
        File.WriteAllText(
            Path.Combine(packagedRuntimeBaseDirectory, "resource", "version.json"),
            """{"last_updated":"2026-05-20 10:36:02.000"}""");
        File.WriteAllText(Path.Combine(packagedRuntimeBaseDirectory, "resource", "battle_data.json"), "bundle");

        try
        {
            Environment.SetEnvironmentVariable("APPDIR", appDir);
            Environment.SetEnvironmentVariable("APPIMAGE", "/tmp/MAAUnified.AppImage");
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", xdgDataHome);

            var runtimeBaseDirectory = RuntimeLayout.ResolveRuntimeBaseDirectory(executableBaseDirectory);
            var result = LinuxPackagedRuntimeSeed.EnsureSeeded(executableBaseDirectory, runtimeBaseDirectory);

            Assert.Equal(LinuxPackagedRuntimeSeedStatus.Ready, result.Status);
            Assert.Equal(Path.GetFullPath(packagedRuntimeBaseDirectory), result.PackagedRuntimeBaseDirectory);
            Assert.Equal(Path.GetFullPath(Path.Combine(xdgDataHome, RuntimeLayout.LinuxAppSupportDirectoryName)), result.RuntimeBaseDirectory);
            Assert.Equal(2, result.NativeLibraryCount);
            Assert.True(result.ResourceSeeded);
            Assert.True(File.Exists(Path.Combine(result.RuntimeBaseDirectory, "libMaaCore.so")));
            Assert.True(File.Exists(Path.Combine(result.RuntimeBaseDirectory, "libMaaUtils.so")));
            Assert.Equal("bundle", File.ReadAllText(Path.Combine(result.RuntimeBaseDirectory, "resource", "battle_data.json")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("APPDIR", originalAppDir);
            Environment.SetEnvironmentVariable("APPIMAGE", originalAppImage);
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", originalXdgDataHome);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void EnsureSeeded_WhenRuntimeResourceIsNewer_KeepsRuntimeResource()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var originalAppDir = Environment.GetEnvironmentVariable("APPDIR");
        var originalAppImage = Environment.GetEnvironmentVariable("APPIMAGE");
        var originalXdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var root = Path.Combine(Path.GetTempPath(), "maa-unified-linux-seed", Guid.NewGuid().ToString("N"));
        var appDir = Path.Combine(root, "AppDir");
        var packagedRuntimeBaseDirectory = Path.Combine(appDir, "usr", "share", "maaunified");
        var executableBaseDirectory = Path.Combine(packagedRuntimeBaseDirectory, "bin");
        var xdgDataHome = Path.Combine(root, "xdg-data");
        var runtimeBaseDirectory = Path.Combine(xdgDataHome, RuntimeLayout.LinuxAppSupportDirectoryName);
        var runtimeResourceDirectory = Path.Combine(runtimeBaseDirectory, "resource");
        Directory.CreateDirectory(executableBaseDirectory);
        Directory.CreateDirectory(Path.Combine(packagedRuntimeBaseDirectory, "resource"));
        Directory.CreateDirectory(runtimeResourceDirectory);
        File.WriteAllText(Path.Combine(packagedRuntimeBaseDirectory, "libMaaCore.so"), "core");
        File.WriteAllText(
            Path.Combine(packagedRuntimeBaseDirectory, "resource", "version.json"),
            """{"last_updated":"2026-05-01 10:36:02.000"}""");
        File.WriteAllText(Path.Combine(packagedRuntimeBaseDirectory, "resource", "battle_data.json"), "bundle");
        File.WriteAllText(
            Path.Combine(runtimeResourceDirectory, "version.json"),
            """{"last_updated":"2026-05-20 10:36:02.000"}""");
        File.WriteAllText(Path.Combine(runtimeResourceDirectory, "battle_data.json"), "user");

        try
        {
            Environment.SetEnvironmentVariable("APPDIR", appDir);
            Environment.SetEnvironmentVariable("APPIMAGE", "/tmp/MAAUnified.AppImage");
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", xdgDataHome);

            var result = LinuxPackagedRuntimeSeed.EnsureSeeded(executableBaseDirectory, runtimeBaseDirectory);

            Assert.Equal(LinuxPackagedRuntimeSeedStatus.Ready, result.Status);
            Assert.False(result.ResourceSeeded);
            Assert.Equal("runtime-resource-current", result.ResourceSeedReason);
            Assert.Equal("user", File.ReadAllText(Path.Combine(runtimeResourceDirectory, "battle_data.json")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("APPDIR", originalAppDir);
            Environment.SetEnvironmentVariable("APPIMAGE", originalAppImage);
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", originalXdgDataHome);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
