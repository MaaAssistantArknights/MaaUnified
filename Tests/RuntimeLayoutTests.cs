using MAAUnified.Compat.Runtime;

namespace MAAUnified.Tests;

public sealed class RuntimeLayoutTests
{
    [Fact]
    public void ResolveRuntimeBaseDirectory_WhenExecutableLivesInPackageRoot_ReturnsExecutableDirectory()
    {
        var executableBaseDirectory = Path.Combine(Path.GetTempPath(), "maa-unified-runtime-layout", Guid.NewGuid().ToString("N"));

        var runtimeBaseDirectory = RuntimeLayout.ResolveRuntimeBaseDirectory(executableBaseDirectory);

        Assert.Equal(Path.GetFullPath(executableBaseDirectory), runtimeBaseDirectory);
    }

    [Fact]
    public void ResolveRuntimeBaseDirectory_WhenExecutableLivesInBinDirectory_ReturnsParentDirectory()
    {
        var runtimeRoot = Path.Combine(Path.GetTempPath(), "maa-unified-runtime-layout", Guid.NewGuid().ToString("N"));
        var executableBaseDirectory = Path.Combine(runtimeRoot, "bin");

        var resolved = RuntimeLayout.ResolveRuntimeBaseDirectory(executableBaseDirectory);

        Assert.Equal(Path.GetFullPath(runtimeRoot), resolved);
    }

    [Fact]
    public void ResolveRuntimeBaseDirectory_WhenExecutableLivesInMacApp_ReturnsApplicationSupportDirectory()
    {
        var appSupportRoot = Path.Combine(Path.GetTempPath(), "maa-unified-app-support", Guid.NewGuid().ToString("N"));
        var executableBaseDirectory = Path.Combine(
            Path.GetTempPath(),
            "maa-unified-runtime-layout",
            Guid.NewGuid().ToString("N"),
            "MAAUnified.app",
            "Contents",
            "MacOS");

        var resolved = RuntimeLayout.ResolveRuntimeBaseDirectory(executableBaseDirectory, appSupportRoot);

        Assert.Equal(
            Path.GetFullPath(Path.Combine(appSupportRoot, RuntimeLayout.MacAppSupportDirectoryName)),
            resolved);
    }

    [Fact]
    public void ResolveRuntimeBaseDirectory_WhenRunningInsideAppImage_ReturnsLinuxApplicationSupportDirectory()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var originalAppDir = Environment.GetEnvironmentVariable("APPDIR");
        var originalAppImage = Environment.GetEnvironmentVariable("APPIMAGE");
        var originalXdgDataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        var xdgDataHome = Path.Combine(Path.GetTempPath(), "maa-unified-linux-app-support", Guid.NewGuid().ToString("N"));
        var executableBaseDirectory = Path.Combine(
            Path.GetTempPath(),
            "maa-unified-runtime-layout",
            Guid.NewGuid().ToString("N"),
            "AppDir",
            "usr",
            "share",
            "maaunified",
            "bin");

        try
        {
            Environment.SetEnvironmentVariable("APPDIR", Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(executableBaseDirectory))))!);
            Environment.SetEnvironmentVariable("APPIMAGE", "/tmp/MAAUnified.AppImage");
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", xdgDataHome);

            var resolved = RuntimeLayout.ResolveRuntimeBaseDirectory(executableBaseDirectory);

            Assert.Equal(
                Path.GetFullPath(Path.Combine(xdgDataHome, RuntimeLayout.LinuxAppSupportDirectoryName)),
                resolved);
        }
        finally
        {
            Environment.SetEnvironmentVariable("APPDIR", originalAppDir);
            Environment.SetEnvironmentVariable("APPIMAGE", originalAppImage);
            Environment.SetEnvironmentVariable("XDG_DATA_HOME", originalXdgDataHome);
        }
    }

    [Fact]
    public void ResolveRuntimeBaseDirectory_WhenRunningFromPortableLinuxPackage_ReturnsAppImageDirectory()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var originalAppDir = Environment.GetEnvironmentVariable("APPDIR");
        var originalAppImage = Environment.GetEnvironmentVariable("APPIMAGE");
        var root = Path.Combine(Path.GetTempPath(), "maa-unified-runtime-layout", Guid.NewGuid().ToString("N"));
        var appImagePath = Path.Combine(root, "MAAUnified.AppImage");
        var executableBaseDirectory = Path.Combine(root, "bin");

        try
        {
            Directory.CreateDirectory(executableBaseDirectory);
            Directory.CreateDirectory(Path.Combine(root, "resource"));
            File.WriteAllText(appImagePath, "launcher");
            File.WriteAllText(Path.Combine(executableBaseDirectory, "MAAUnified"), "app");
            File.WriteAllText(Path.Combine(root, "libMaaCore.so"), "core");
            Environment.SetEnvironmentVariable("APPDIR", Path.Combine(Path.GetTempPath(), "maa-unified-appdir", Guid.NewGuid().ToString("N")));
            Environment.SetEnvironmentVariable("APPIMAGE", appImagePath);

            var resolved = RuntimeLayout.ResolveRuntimeBaseDirectory(executableBaseDirectory);

            Assert.Equal(Path.GetFullPath(root), resolved);
        }
        finally
        {
            Environment.SetEnvironmentVariable("APPDIR", originalAppDir);
            Environment.SetEnvironmentVariable("APPIMAGE", originalAppImage);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void IsLinuxPortablePackageLaunchInvalid_WhenResourceIsMissing_ReturnsTrue()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var originalAppImage = Environment.GetEnvironmentVariable("APPIMAGE");
        var root = Path.Combine(Path.GetTempPath(), "maa-unified-runtime-layout", Guid.NewGuid().ToString("N"));
        var appImagePath = Path.Combine(root, "MAAUnified.AppImage");
        var executableBaseDirectory = Path.Combine(root, "bin");

        try
        {
            Directory.CreateDirectory(executableBaseDirectory);
            File.WriteAllText(appImagePath, "launcher");
            File.WriteAllText(Path.Combine(executableBaseDirectory, "MAAUnified"), "app");
            File.WriteAllText(Path.Combine(root, "libMaaCore.so"), "core");
            Environment.SetEnvironmentVariable("APPIMAGE", appImagePath);

            Assert.True(RuntimeLayout.IsLinuxPortablePackageLaunchInvalid(executableBaseDirectory, linuxAppImagePath: null, out var packageRoot));
            Assert.Equal(Path.GetFullPath(root), packageRoot);
        }
        finally
        {
            Environment.SetEnvironmentVariable("APPIMAGE", originalAppImage);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void ResolveSingleInstanceIdentityPath_WhenRunningFromPortableLinuxPackage_UsesPackageRoot()
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        var originalAppImage = Environment.GetEnvironmentVariable("APPIMAGE");
        var root = Path.Combine(Path.GetTempPath(), "maa-unified-runtime-layout", Guid.NewGuid().ToString("N"));
        var appImagePath = Path.Combine(root, "MAAUnified.AppImage");
        var executableBaseDirectory = Path.Combine(root, "bin");

        try
        {
            Directory.CreateDirectory(executableBaseDirectory);
            Directory.CreateDirectory(Path.Combine(root, "resource"));
            File.WriteAllText(appImagePath, "launcher");
            File.WriteAllText(Path.Combine(executableBaseDirectory, "MAAUnified"), "app");
            File.WriteAllText(Path.Combine(root, "libMaaCore.so"), "core");
            Environment.SetEnvironmentVariable("APPIMAGE", appImagePath);

            var resolved = RuntimeLayout.ResolveSingleInstanceIdentityPath(executableBaseDirectory);

            Assert.Equal(Path.GetFullPath(root), resolved);
        }
        finally
        {
            Environment.SetEnvironmentVariable("APPIMAGE", originalAppImage);
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void IsPackagedBinDirectory_WhenDevelopmentOutputDirectory_ReturnsFalse()
    {
        var developmentOutputDirectory = Path.Combine(
            Path.GetTempPath(),
            "maa-unified-runtime-layout",
            Guid.NewGuid().ToString("N"),
            "bin",
            "Release",
            "net10.0");

        Assert.False(RuntimeLayout.IsPackagedBinDirectory(developmentOutputDirectory));
    }

    [Fact]
    public void IsMacAppContentsMacOsDirectory_WhenInsideAppBundle_ReturnsTrue()
    {
        var executableBaseDirectory = Path.Combine(
            Path.GetTempPath(),
            "maa-unified-runtime-layout",
            Guid.NewGuid().ToString("N"),
            "MAAUnified.app",
            "Contents",
            "MacOS");

        Assert.True(RuntimeLayout.IsMacAppContentsMacOsDirectory(executableBaseDirectory));
    }

    [Fact]
    public void EnsureSeeded_WhenMacAppRuntimeIsMissingResources_CopiesBundleSeed()
    {
        var root = Path.Combine(Path.GetTempPath(), "maa-unified-runtime-seed", Guid.NewGuid().ToString("N"));
        var executableBaseDirectory = Path.Combine(root, "MAAUnified.app", "Contents", "MacOS");
        var bundleResourceDirectory = Path.Combine(root, "MAAUnified.app", "Contents", "Resources", "resource");
        var runtimeBaseDirectory = Path.Combine(root, "Application Support", "MAAUnified");
        Directory.CreateDirectory(executableBaseDirectory);
        Directory.CreateDirectory(bundleResourceDirectory);
        File.WriteAllText(Path.Combine(executableBaseDirectory, "libMaaCore.dylib"), "core");
        File.WriteAllText(Path.Combine(bundleResourceDirectory, "battle_data.json"), "{}");

        var result = MacAppRuntimeSeed.EnsureSeeded(executableBaseDirectory, runtimeBaseDirectory);

        Assert.Equal(MacAppRuntimeSeedStatus.Ready, result.Status);
        Assert.True(result.ResourceSeeded);
        Assert.Equal("runtime-resource-missing", result.ResourceSeedReason);
        Assert.Equal(1, result.NativeLibraryCount);
        Assert.True(File.Exists(Path.Combine(runtimeBaseDirectory, "libMaaCore.dylib")));
        Assert.True(File.Exists(Path.Combine(runtimeBaseDirectory, "resource", "battle_data.json")));
    }

    [Fact]
    public void EnsureSeeded_WhenMacAppHasMaaAdbControlUnit_CopiesAndReportsMaaFrameworkRuntimeLibrary()
    {
        var root = Path.Combine(Path.GetTempPath(), "maa-unified-runtime-seed", Guid.NewGuid().ToString("N"));
        var executableBaseDirectory = Path.Combine(root, "MAAUnified.app", "Contents", "MacOS");
        var bundleResourceDirectory = Path.Combine(root, "MAAUnified.app", "Contents", "Resources", "resource");
        var runtimeBaseDirectory = Path.Combine(root, "Application Support", "MAAUnified");
        Directory.CreateDirectory(executableBaseDirectory);
        Directory.CreateDirectory(bundleResourceDirectory);
        File.WriteAllText(Path.Combine(executableBaseDirectory, RuntimeLayout.MacCoreLibraryFileName), "core");
        File.WriteAllText(Path.Combine(executableBaseDirectory, RuntimeLayout.MacMaaAdbControlUnitLibraryFileName), "control-unit");

        var result = MacAppRuntimeSeed.EnsureSeeded(executableBaseDirectory, runtimeBaseDirectory);

        Assert.Equal(MacAppRuntimeSeedStatus.Ready, result.Status);
        Assert.Equal(2, result.NativeLibraryCount);
        Assert.Contains(RuntimeLayout.MacMaaAdbControlUnitLibraryFileName, result.SeededMaaFrameworkRuntimeLibraries ?? []);
        Assert.Empty(result.MissingMaaFrameworkRuntimeLibraries ?? []);
        Assert.True(File.Exists(Path.Combine(runtimeBaseDirectory, RuntimeLayout.MacCoreLibraryFileName)));
        Assert.True(File.Exists(Path.Combine(runtimeBaseDirectory, RuntimeLayout.MacMaaAdbControlUnitLibraryFileName)));
    }

    [Fact]
    public void EnsureSeeded_WhenMacAppIsMissingMaaAdbControlUnit_ReportsMissingMaaFrameworkRuntimeLibrary()
    {
        var root = Path.Combine(Path.GetTempPath(), "maa-unified-runtime-seed", Guid.NewGuid().ToString("N"));
        var executableBaseDirectory = Path.Combine(root, "MAAUnified.app", "Contents", "MacOS");
        var bundleResourceDirectory = Path.Combine(root, "MAAUnified.app", "Contents", "Resources", "resource");
        var runtimeBaseDirectory = Path.Combine(root, "Application Support", "MAAUnified");
        Directory.CreateDirectory(executableBaseDirectory);
        Directory.CreateDirectory(bundleResourceDirectory);
        File.WriteAllText(Path.Combine(executableBaseDirectory, RuntimeLayout.MacCoreLibraryFileName), "core");

        var result = MacAppRuntimeSeed.EnsureSeeded(executableBaseDirectory, runtimeBaseDirectory);

        Assert.Equal(MacAppRuntimeSeedStatus.Ready, result.Status);
        Assert.Contains(RuntimeLayout.MacMaaAdbControlUnitLibraryFileName, result.MissingMaaFrameworkRuntimeLibraries ?? []);
        Assert.DoesNotContain(RuntimeLayout.MacMaaAdbControlUnitLibraryFileName, result.SeededMaaFrameworkRuntimeLibraries ?? []);
    }

    [Fact]
    public void EnsureSeeded_WhenRuntimeResourceAlreadyExists_DoesNotOverwriteResource()
    {
        var root = Path.Combine(Path.GetTempPath(), "maa-unified-runtime-seed", Guid.NewGuid().ToString("N"));
        var executableBaseDirectory = Path.Combine(root, "MAAUnified.app", "Contents", "MacOS");
        var bundleResourceDirectory = Path.Combine(root, "MAAUnified.app", "Contents", "Resources", "resource");
        var runtimeResourceDirectory = Path.Combine(root, "Application Support", "MAAUnified", "resource");
        Directory.CreateDirectory(executableBaseDirectory);
        Directory.CreateDirectory(bundleResourceDirectory);
        Directory.CreateDirectory(runtimeResourceDirectory);
        File.WriteAllText(Path.Combine(executableBaseDirectory, "libMaaCore.dylib"), "core");
        File.WriteAllText(Path.Combine(bundleResourceDirectory, "battle_data.json"), "bundle");
        File.WriteAllText(Path.Combine(runtimeResourceDirectory, "battle_data.json"), "user");

        var result = MacAppRuntimeSeed.EnsureSeeded(
            executableBaseDirectory,
            Path.Combine(root, "Application Support", "MAAUnified"));

        Assert.False(result.ResourceSeeded);
        Assert.Equal("runtime-resource-current", result.ResourceSeedReason);
        Assert.Equal("user", File.ReadAllText(Path.Combine(runtimeResourceDirectory, "battle_data.json")));
    }

    [Fact]
    public void EnsureSeeded_WhenBundleResourceIsNewer_RefreshesRuntimeResource()
    {
        var root = Path.Combine(Path.GetTempPath(), "maa-unified-runtime-seed", Guid.NewGuid().ToString("N"));
        var executableBaseDirectory = Path.Combine(root, "MAAUnified.app", "Contents", "MacOS");
        var bundleResourceDirectory = Path.Combine(root, "MAAUnified.app", "Contents", "Resources", "resource");
        var runtimeResourceDirectory = Path.Combine(root, "Application Support", "MAAUnified", "resource");
        Directory.CreateDirectory(executableBaseDirectory);
        Directory.CreateDirectory(bundleResourceDirectory);
        Directory.CreateDirectory(runtimeResourceDirectory);
        File.WriteAllText(Path.Combine(executableBaseDirectory, "libMaaCore.dylib"), "core");
        File.WriteAllText(Path.Combine(bundleResourceDirectory, "version.json"), """{"last_updated":"2026-05-08 08:36:02.000"}""");
        File.WriteAllText(Path.Combine(bundleResourceDirectory, "battle_data.json"), "bundle");
        File.WriteAllText(Path.Combine(runtimeResourceDirectory, "version.json"), """{"last_updated":"2026-05-01 08:36:02.000"}""");
        File.WriteAllText(Path.Combine(runtimeResourceDirectory, "battle_data.json"), "user");

        var result = MacAppRuntimeSeed.EnsureSeeded(
            executableBaseDirectory,
            Path.Combine(root, "Application Support", "MAAUnified"));

        Assert.True(result.ResourceSeeded);
        Assert.Equal("bundle-resource-newer", result.ResourceSeedReason);
        Assert.Equal("bundle", File.ReadAllText(Path.Combine(runtimeResourceDirectory, "battle_data.json")));
    }

    [Fact]
    public void EnsureSeeded_WhenRuntimeResourceIsNewer_KeepsRuntimeResource()
    {
        var root = Path.Combine(Path.GetTempPath(), "maa-unified-runtime-seed", Guid.NewGuid().ToString("N"));
        var executableBaseDirectory = Path.Combine(root, "MAAUnified.app", "Contents", "MacOS");
        var bundleResourceDirectory = Path.Combine(root, "MAAUnified.app", "Contents", "Resources", "resource");
        var runtimeResourceDirectory = Path.Combine(root, "Application Support", "MAAUnified", "resource");
        Directory.CreateDirectory(executableBaseDirectory);
        Directory.CreateDirectory(bundleResourceDirectory);
        Directory.CreateDirectory(runtimeResourceDirectory);
        File.WriteAllText(Path.Combine(executableBaseDirectory, "libMaaCore.dylib"), "core");
        File.WriteAllText(Path.Combine(bundleResourceDirectory, "version.json"), """{"last_updated":"2026-05-01 08:36:02.000"}""");
        File.WriteAllText(Path.Combine(bundleResourceDirectory, "battle_data.json"), "bundle");
        File.WriteAllText(Path.Combine(runtimeResourceDirectory, "version.json"), """{"last_updated":"2026-05-08 08:36:02.000"}""");
        File.WriteAllText(Path.Combine(runtimeResourceDirectory, "battle_data.json"), "user");

        var result = MacAppRuntimeSeed.EnsureSeeded(
            executableBaseDirectory,
            Path.Combine(root, "Application Support", "MAAUnified"));

        Assert.False(result.ResourceSeeded);
        Assert.Equal("runtime-resource-current", result.ResourceSeedReason);
        Assert.Equal("user", File.ReadAllText(Path.Combine(runtimeResourceDirectory, "battle_data.json")));
    }
}
