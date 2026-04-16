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
}
