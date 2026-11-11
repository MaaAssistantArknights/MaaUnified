namespace MAAUnified.Tests;

public sealed class BaselineFallbackParityPolicyTests
{
    // M01-R11-03: the policy test used to assert nothing on the current 15 rows (no row had
    // expected=full&current!=full, and no degrade-visible row used the literal current="fallback").
    // Pin every row's (expected_mode, current_mode, parity_status) concretely so a regression of
    // any windows/macos/linux fallback row fails this gate, and assert that each policy branch is
    // actually reached.
    private static readonly IReadOnlyDictionary<string, (string Expected, string Current, string Parity)> PinnedFallbackRows =
        new Dictionary<string, (string, string, string)>(StringComparer.Ordinal)
        {
            ["Tray:windows"] = ("full", "full", "Aligned"),
            ["Tray:macos"] = ("full", "full", "Aligned"),
            ["Tray:linux"] = ("degrade-visible", "conditional-native-with-fallback", "Aligned"),
            ["Notification:windows"] = ("full", "full", "Aligned"),
            ["Notification:macos"] = ("degrade-visible", "conditional-native-with-fallback", "Aligned"),
            ["Notification:linux"] = ("degrade-visible", "conditional-native-with-fallback", "Aligned"),
            ["Hotkey:windows"] = ("full", "full", "Aligned"),
            ["Hotkey:macos"] = ("degrade-visible", "conditional-native-with-fallback", "Aligned"),
            ["Hotkey:linux"] = ("degrade-visible", "conditional-native-with-fallback", "Aligned"),
            ["Autostart:windows"] = ("full", "full", "Aligned"),
            ["Autostart:macos"] = ("degrade-visible", "conditional-native-with-fallback", "Aligned"),
            ["Autostart:linux"] = ("degrade-visible", "conditional-native-with-fallback", "Aligned"),
            ["Overlay:windows"] = ("full", "full", "Aligned"),
            ["Overlay:macos"] = ("degrade-visible", "conditional-native-with-fallback", "Aligned"),
            ["Overlay:linux"] = ("degrade-visible", "conditional-native-with-fallback", "Aligned"),
        };

    [Fact]
    public void FallbackRows_ShouldMatchThePinnedContractExactly()
    {
        var baseline = BaselineTestSupport.LoadBaseline();

        var actual = baseline.FallbackCapabilities.ToDictionary(
            row => $"{row.CapabilityId}:{row.Platform}",
            row => (row.ExpectedMode, row.CurrentMode, row.ParityStatus),
            StringComparer.Ordinal);

        Assert.Equal(
            PinnedFallbackRows.OrderBy(pair => pair.Key, StringComparer.Ordinal),
            actual.OrderBy(pair => pair.Key, StringComparer.Ordinal));
    }

    [Fact]
    public void FallbackParityPolicy_ShouldMatchExpectedVsCurrentMode()
    {
        var baseline = BaselineTestSupport.LoadBaseline();

        var fullModeRows = 0;
        var degradeVisibleRows = 0;

        foreach (var row in baseline.FallbackCapabilities)
        {
            if (string.Equals(row.ExpectedMode, "full", StringComparison.OrdinalIgnoreCase))
            {
                fullModeRows++;
                if (!string.Equals(row.CurrentMode, "full", StringComparison.OrdinalIgnoreCase))
                {
                    Assert.Equal("Waived", row.ParityStatus);
                    Assert.NotNull(row.Waiver);
                    continue;
                }

                Assert.Equal("Aligned", row.ParityStatus);
                continue;
            }

            if (string.Equals(row.ExpectedMode, "degrade-visible", StringComparison.OrdinalIgnoreCase))
            {
                degradeVisibleRows++;

                // Degrade-visible rows run a native implementation when the platform backend is
                // available and a visible fallback otherwise; both the literal fallback mode and
                // the conditional-native mode honor the contract, anything else does not.
                Assert.True(
                    string.Equals(row.CurrentMode, "fallback", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(row.CurrentMode, "conditional-native-with-fallback", StringComparison.OrdinalIgnoreCase),
                    $"Unexpected current mode for {row.CapabilityId}:{row.Platform} => {row.CurrentMode}");
                Assert.True(
                    string.Equals(row.ParityStatus, "Aligned", StringComparison.Ordinal)
                    || string.Equals(row.ParityStatus, "Waived", StringComparison.Ordinal),
                    $"Unexpected parity for {row.CapabilityId}:{row.Platform} => {row.ParityStatus}");
                continue;
            }

            Assert.Fail($"Unknown expected mode for {row.CapabilityId}:{row.Platform} => {row.ExpectedMode}");
        }

        // Non-vacuity: both policy branches must be exercised by the current data. The exact
        // counts are pinned by FallbackRows_ShouldMatchThePinnedContractExactly; here we only
        // require that neither branch silently disappeared.
        Assert.True(fullModeRows > 0, "Policy test is vacuous: no full-mode fallback rows exercised.");
        Assert.True(degradeVisibleRows > 0, "Policy test is vacuous: no degrade-visible fallback rows exercised.");
        Assert.Equal(fullModeRows + degradeVisibleRows, baseline.FallbackCapabilities.Count);
    }
}
