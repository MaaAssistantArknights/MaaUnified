using MAAUnified.Compat.Runtime;

namespace MAAUnified.Tests;

public sealed class ResourceDirectoryMaintenanceTests
{
    [Fact]
    public void RemoveFlattenedPluginShadowFiles_WhenPluginFilesAreShadowed_RemovesOnlyShadowCopies()
    {
        var root = Path.Combine(Path.GetTempPath(), "maa-unified-resource-maintenance", Guid.NewGuid().ToString("N"));
        var resourceDirectory = Path.Combine(root, "resource");
        var pluginDirectory = Path.Combine(resourceDirectory, "tasks", "RA", "plugin");

        Directory.CreateDirectory(pluginDirectory);
        File.WriteAllText(Path.Combine(resourceDirectory, "tasks", "RA", "base.json"), "root-base");
        File.WriteAllText(Path.Combine(pluginDirectory, "base.json"), "plugin-base");
        File.WriteAllText(Path.Combine(resourceDirectory, "tasks", "RA", "Tales.json"), "root-tales");
        File.WriteAllText(Path.Combine(pluginDirectory, "Tales.json"), "plugin-tales");
        File.WriteAllText(Path.Combine(pluginDirectory, "OnlyPlugin.json"), "plugin-only");

        try
        {
            var removedCount = ResourceDirectoryMaintenance.RemoveFlattenedPluginShadowFiles(resourceDirectory);

            Assert.Equal(2, removedCount);
            Assert.False(File.Exists(Path.Combine(pluginDirectory, "base.json")));
            Assert.False(File.Exists(Path.Combine(pluginDirectory, "Tales.json")));
            Assert.True(File.Exists(Path.Combine(pluginDirectory, "OnlyPlugin.json")));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }
}
