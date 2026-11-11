using MAAUnified.Compat.Mapping.Baseline;

namespace MAAUnified.Tests;

public sealed class BaselineTargetPackageClosureTests
{
    private static readonly string[] TargetItemIds =
    [
        "TaskQueueRoot",
        "System.Connect",
        "System.TrayMenu.HideTray",
        "System.TrayMenu.Restart",
        "Task.StartUp",
        "Task.Fight",
        "Task.Recruit",
        "Task.Infrast",
        "Task.Mall",
        "Task.Award",
        "Task.PostAction",
        "Task.Roguelike",
        "Task.Reclamation",
        "Task.Custom",
        "Settings.Connect",
        "Settings.Game",
        "Settings.Start",
        "Settings.Performance",
        "Settings.Timer",
        "Settings.Gui",
        "Settings.Background",
        "Settings.HotKey",
        "Settings.RemoteControl",
        "Settings.ExternalNotification",
    ];

    private static readonly string[] TargetMappingOwners =
    [
        "Task.StartUp",
        "Task.Fight",
        "Task.Recruit",
        "Task.Infrast",
        "Task.Mall",
        "Task.Award",
        "Task.PostAction",
        "Task.Roguelike",
        "Task.Reclamation",
        "Task.Custom",
        "Settings.Connect",
        "Settings.Game",
        "Settings.Start",
        "Settings.Performance",
        "Settings.Timer",
        "Settings.Gui",
        "Settings.Background",
        "Settings.HotKey",
        "Settings.RemoteControl",
        "Settings.ExternalNotification",
    ];

    private static readonly string[] TargetWindowCapabilities =
    [
        "Tray",
        "Notification",
        "Hotkey",
        "Autostart",
        "Overlay",
    ];

    [Fact]
    public void TargetItems_WhenAligned_ShouldHaveClosedFrozenWpfConfigSurface()
    {
        var baseline = BaselineTestSupport.LoadBaseline();
        var statuses = baseline.Items.ToDictionary(i => i.ItemId, i => i.ParityStatus, StringComparer.Ordinal);

        // M01-R3-02: the previous policy ("target items must not remain Gap") forced false Aligned
        // claims while owned P0 frozen-WPF configuration keys were still Gap. Target items may now be
        // explicit Gaps, but an Aligned claim requires every owned frozen-WPF config row to be Aligned
        // or covered by an explicit scoped waiver.
        foreach (var itemId in TargetItemIds)
        {
            Assert.True(statuses.TryGetValue(itemId, out var status), $"Missing baseline item: {itemId}");
            Assert.Contains(status, BaselineConstants.AllowedParityStatus);
        }

        var alignedTargets = TargetItemIds
            .Where(itemId => string.Equals(statuses[itemId], "Aligned", StringComparison.Ordinal))
            .ToHashSet(StringComparer.Ordinal);

        var offenders = baseline.ConfigKeyMappings
            .Where(mapping => alignedTargets.Contains(mapping.OwnerItemId))
            .Where(mapping => string.Equals(mapping.ParityStatus, "Gap", StringComparison.Ordinal) && mapping.Waiver is null)
            .Where(mapping => !BaselineTestSupport.AvaloniaExtensionConfigKeys.Contains(mapping.Key))
            .Select(mapping => $"{mapping.OwnerItemId}:{mapping.Key}")
            .OrderBy(entry => entry, StringComparer.Ordinal)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void TargetConfigKeyMappings_ShouldNotClaimAlignmentWithoutRuntimeLocators()
    {
        var baseline = BaselineTestSupport.LoadBaseline();
        var targetOwnerSet = TargetMappingOwners.ToHashSet(StringComparer.Ordinal);

        var unsupportedAligned = baseline.ConfigKeyMappings
            .Where(mapping => targetOwnerSet.Contains(mapping.OwnerItemId) && string.Equals(mapping.ParityStatus, "Aligned", StringComparison.Ordinal))
            .Where(mapping => string.IsNullOrWhiteSpace(mapping.MappingTarget)
                              || string.IsNullOrWhiteSpace(mapping.ReadPath)
                              || string.IsNullOrWhiteSpace(mapping.WritePath))
            .Select(mapping => $"{mapping.OwnerItemId}:{mapping.Key}")
            .ToList();

        Assert.True(unsupportedAligned.Count == 0, $"Unsupported aligned mappings: {string.Join(", ", unsupportedAligned)}");
    }

    [Fact]
    public void TargetWindowsFallbackCapabilities_ShouldNotRemainGap()
    {
        var baseline = BaselineTestSupport.LoadBaseline();
        var capabilitySet = TargetWindowCapabilities.ToHashSet(StringComparer.Ordinal);

        var remainedGap = baseline.FallbackCapabilities
            .Where(row => string.Equals(row.Platform, "windows", StringComparison.OrdinalIgnoreCase))
            .Where(row => capabilitySet.Contains(row.CapabilityId))
            .Where(row => string.Equals(row.ParityStatus, "Gap", StringComparison.Ordinal))
            .Select(row => $"{row.CapabilityId}:{row.Platform}")
            .ToList();

        Assert.True(remainedGap.Count == 0, $"Windows fallback gaps remain: {string.Join(", ", remainedGap)}");
    }
}
