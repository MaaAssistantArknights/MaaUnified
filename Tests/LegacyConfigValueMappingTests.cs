using MAAUnified.Application.Configuration;

namespace MAAUnified.Tests;

public sealed class LegacyConfigValueMappingTests
{
    [Theory]
    [InlineData("Notification.EnableStallTimeout", "TimeOut.Timer.StallTimeoutEnabled")]
    [InlineData("Notification.StallTimeoutMinutes", "TimeOut.Timer.StallTimeoutMinutes")]
    [InlineData("Notification.ReminderIntervalMinutes", "TimeOut.Timer.ReminderIntervalMinutes")]
    [InlineData("RuntimeSettings.EnableStallTimeout", "TimeOut.Timer.StallTimeoutEnabled")]
    [InlineData("RuntimeSettings.StallTimeoutMinutes", "TimeOut.Timer.StallTimeoutMinutes")]
    [InlineData("RuntimeSettings.StallTimeoutReminderIntervalMinutes", "TimeOut.Timer.ReminderIntervalMinutes")]
    [InlineData("RuntimeSettings.EnableRunDurationLimit", "RuntimeSettings.EnableRunDurationLimit")]
    [InlineData("RuntimeSettings.RunDurationLimitMinutes", "RuntimeSettings.RunDurationLimitMinutes")]
    [InlineData("RuntimeSettings.RunDurationLimitExecutePostActions", "RuntimeSettings.RunDurationLimitExecutePostActions")]
    [InlineData("ThirdParty.YituliuOpenApiToken", "ThirdParty.YituliuOpenApiToken")]
    [InlineData("ThirdParty.OperBoxUseYituliuApi", "ThirdParty.OperBoxUseYituliuApi")]
    [InlineData("ThirdParty.EnableOperBoxAssist", "ThirdParty.EnableOperBoxAssist")]
    public void DevV2ProfileGuiPaths_ShouldMapToFlatRuntimeKeys(string relativePath, string expected)
    {
        Assert.Equal(expected, LegacyConfigValueMappings.TryMapDevV2ProfileGuiKey(relativePath));
    }
}
