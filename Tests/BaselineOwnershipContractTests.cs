namespace MAAUnified.Tests;

public sealed class BaselineOwnershipContractTests
{
    // M01-R5-01: the Aligned-closure gate trusts hand-assigned owner_item_id values, so every
    // configuration key's owner is pinned here against the frozen WPF consumer surface (frozen commit
    // 23ddd271b40a3d0c18de8a6ae8305357cc032e19). Longest-prefix match; full-key entries act as exact
    // exceptions. Evidence for the corrected assignments:
    // - MainFunction.PostActions: frozen Models/PostActionSetting.cs persists
    //   ConfigurationKeys.PostActions (a post-action concern, not a Fight setting); Avalonia
    //   PostActionFeatureService migrates it. -> Task.PostAction
    // - MainFunction.ActionAfterCompleted: Avalonia-only alias consumed by the same post-action
    //   migration. -> Task.PostAction
    // - GUI.Background.*: frozen BackgroundSettingsUserControlModel.cs reads/writes these global
    //   keys. -> Settings.Background
    // Remaining rules rest on the frozen consumers spot-checked across rounds (for example
    // GameSettingsUserControlModel.cs for TimeOut.Timer.*, GuiSettingsUserControlModel.cs for GUI.*).
    private static readonly (string Prefix, string OwnerItemId)[] OwnershipRules =
    [
        ("MainFunction.PostActions", "Task.PostAction"),
        ("MainFunction.ActionAfterCompleted", "Task.PostAction"),
        ("MainFunction.", "Task.Fight"),
        ("Fight.", "Task.Fight"),
        ("GUI.Background.", "Settings.Background"),
        ("GUI.", "Settings.Gui"),
        ("Guide.", "Settings.Gui"),
        ("Copilot.", "Advanced.Copilot"),
        ("Depot.", "Advanced.Toolbox"),
        ("Gacha.", "Advanced.Toolbox"),
        ("MiniGame.", "Advanced.Toolbox"),
        ("OperBox.", "Advanced.Toolbox"),
        ("Peep.", "Advanced.Toolbox"),
        ("Toolbox.", "Advanced.Toolbox"),
        ("Debug.TaskName", "Advanced.Toolbox"),
        ("Announcement.", "Dialog.Announcement"),
        ("Achievement.", "Settings.Achievement"),
        ("Configurations", "Settings.ConfigurationManager"),
        ("Current", "Settings.ConfigurationManager"),
        ("Data", "Settings.ConfigurationManager"),
        ("Default", "Settings.ConfigurationManager"),
        ("Global", "Settings.ConfigurationManager"),
        ("Connect.", "Settings.Connect"),
        ("Bluestacks.", "Settings.Connect"),
        ("ExternalNotification.", "Settings.ExternalNotification"),
        ("Penguin.", "Settings.Game"),
        ("Yituliu.", "Settings.Game"),
        ("TimeOut.", "Settings.Game"),
        // Avalonia extension keys for the dev-v2 stall detection and single-run duration limit
        // (WPF dev-v2 24639a0fdd / 5a55fc8ac1); both are owned by the game settings page.
        ("RuntimeSettings.", "Settings.Game"),
        ("HotKeys", "Settings.HotKey"),
        ("Performance.", "Settings.Performance"),
        ("RemoteControl.", "Settings.RemoteControl"),
        ("Start.", "Settings.Start"),
        ("Timer.", "Settings.Timer"),
        ("Cron", "Settings.Timer"),
        ("VersionUpdate.", "Settings.VersionUpdate"),
        ("Mission.", "Task.Award"),
        ("Infrast.", "Task.Infrast"),
        ("Mall.", "Task.Mall"),
        ("Visit.", "Task.Mall"),
        ("Reclamation.", "Task.Reclamation"),
        ("AutoRecruit.", "Task.Recruit"),
        ("Recruit.", "Task.Recruit"),
        ("Roguelike.", "Task.Roguelike"),
        ("Overlay.", "TaskQueueRoot"),
    ];

    [Fact]
    public void ConfigKeyOwnership_ShouldMatchFrozenWpfConsumerSurface()
    {
        var baseline = BaselineTestSupport.LoadBaseline();

        foreach (var mapping in baseline.ConfigKeyMappings)
        {
            var expected = ResolveOwner(mapping.Key);
            Assert.True(expected is not null, $"No ownership rule covers `{mapping.Key}`.");
            Assert.True(
                string.Equals(expected, mapping.OwnerItemId, StringComparison.Ordinal),
                $"Key `{mapping.Key}` is owned by `{mapping.OwnerItemId}` but the frozen-WPF consumer surface assigns it to `{expected}`.");
        }

        // No dead rules: every rule must cover at least one current baseline key.
        foreach (var (prefix, _) in OwnershipRules)
        {
            Assert.True(
                baseline.ConfigKeyMappings.Any(mapping => mapping.Key.StartsWith(prefix, StringComparison.Ordinal)),
                $"Ownership rule `{prefix}` matches no baseline key.");
        }
    }

    private static string? ResolveOwner(string key)
    {
        return OwnershipRules
            .Where(rule => key.StartsWith(rule.Prefix, StringComparison.Ordinal))
            .OrderByDescending(rule => rule.Prefix.Length)
            .Select(rule => (string?)rule.OwnerItemId)
            .FirstOrDefault();
    }
}
