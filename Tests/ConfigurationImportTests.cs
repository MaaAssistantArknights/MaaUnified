using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using MAAUnified.Application.Configuration;
using MAAUnified.Application.Models;
using MAAUnified.Application.Models.TaskParams;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Features;
using MAAUnified.Application.Services.TaskParams;
using MAAUnified.Platform;
using LegacyConfigurationKeys = MAAUnified.Compat.Constants.ConfigurationKeys;

namespace MAAUnified.Tests;

public sealed class ConfigurationImportTests
{
    private const string OperBoxSelectedExportValueKey = "OperBox.SelectedExportValue";

    [Fact]
    public async Task GuiImport_FlatMallMissingLists_UsesLocalizedWpfDefaults()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));
        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "TaskQueue.Mall.IsChecked": true,
                  "Mall.CreditShopping": true
                }
              },
              "Global": {
                "GUI.Localization": "ja-jp"
              }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.GuiOnly, manualImport: false);

        Assert.True(report.Success);
        var mall = service.CurrentConfig.Profiles["Default"].TaskQueue.Single(task => task.Type == TaskModuleTypes.Mall);
        Assert.Equal(["求人票"], ReadStringArray(Assert.IsType<JsonArray>(mall.Params["buy_first"])));
        Assert.Equal(["炭素材", "家具", "緊急招集票"], ReadStringArray(Assert.IsType<JsonArray>(mall.Params["blacklist"])));
    }

    [Fact]
    public async Task AutoImport_UsesGuiNewThenGuiFillMissing()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));

        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.new.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "TaskQueue": [
                    { "$type": "FightTask", "Name": "Fight", "IsEnable": true }
                  ],
                  "ConnectAddress": "127.0.0.1:5555"
                }
              },
              "GUI": {
                "Localization": "zh-cn"
              }
            }
            """);

        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "ConnectAddress": "10.0.0.2:1234",
                  "TouchMode": "maatouch"
                }
              },
              "Global": {
                "GUI.Localization": "en-us"
              }
            }
            """);

        var service = CreateService(root);
        var result = await service.LoadOrBootstrapAsync();

        Assert.False(result.LoadedFromExistingConfig);
        var import = Assert.IsType<ImportReport>(result.ImportReport);
        Assert.True(import.AppliedConfig);
        Assert.True(import.ImportedGuiNew);
        Assert.True(import.ImportedGui);
        Assert.Contains("gui.new.json", import.ImportedFiles, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("gui.json", import.ImportedFiles, StringComparer.OrdinalIgnoreCase);
        Assert.True(import.ConflictCount > 0);
        Assert.True(service.CurrentConfig.Profiles["Default"].TaskQueue.Count == 1);
        Assert.Equal(UnifiedConfig.LatestSchemaVersion, service.CurrentConfig.SchemaVersion);
        Assert.Equal("Fight", service.CurrentConfig.Profiles["Default"].TaskQueue[0].Type);
        Assert.Equal("127.0.0.1:5555", service.CurrentConfig.Profiles["Default"].Values["ConnectAddress"]?.GetValue<string>());
        Assert.Equal("maatouch", service.CurrentConfig.Profiles["Default"].Values["TouchMode"]?.GetValue<string>());
        Assert.True(service.CurrentConfig.Profiles["Default"].TaskQueue[0].Params.ContainsKey("stage"));
    }

    [Fact]
    public async Task GuiNewImport_ShouldNormalizeLegacyConnectionKeys_ToCanonicalProfileValues()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));

        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.new.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "Connect.Address": "10.6.0.6:7555",
                  "Connect.ConnectConfig": "LDPlayer",
                  "Connect.AdbPath": "/tmp/adb-normalized"
                }
              }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.GuiNewOnly, manualImport: false);

        Assert.True(report.Success);
        var profile = service.CurrentConfig.Profiles["Default"];
        Assert.Equal("10.6.0.6:7555", profile.Values["ConnectAddress"]?.GetValue<string>());
        Assert.Equal("LDPlayer", profile.Values["ConnectConfig"]?.GetValue<string>());
        Assert.Equal("/tmp/adb-normalized", profile.Values["AdbPath"]?.GetValue<string>());
        Assert.False(profile.Values.ContainsKey("Connect.Address"));
        Assert.False(profile.Values.ContainsKey("Connect.ConnectConfig"));
        Assert.False(profile.Values.ContainsKey("Connect.AdbPath"));
    }

    [Fact]
    public async Task GuiImport_DpapiEncryptedExternalNotificationValue_ShouldReportWarningAndClearWhenUnavailable()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));
        var legacyEncryptedValue = Convert.ToBase64String(
        [
            0x01, 0x00, 0x00, 0x00,
            0xd0, 0x8c, 0x9d, 0xdf, 0x01, 0x15, 0xd1, 0x11,
            0x8c, 0x7a, 0x00, 0xc0, 0x4f, 0xc2, 0x97, 0xeb,
            0x00, 0x01, 0x02, 0x03,
            0x04, 0x05, 0x06, 0x07,
            0x08, 0x09, 0x0a, 0x0b,
            0x0c, 0x0d, 0x0e, 0x0f,
            0x10, 0x11, 0x12, 0x13,
            0x14, 0x15, 0x16, 0x17,
            0x18, 0x19, 0x1a, 0x1b,
            0x1c, 0x1d, 0x1e, 0x1f,
            0x20, 0x21, 0x22, 0x23,
            0x24, 0x25, 0x26, 0x27,
            0x28, 0x29, 0x2a, 0x2b,
            0x2c, 0x2d, 0x2e, 0x2f,
        ]);

        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.json"),
            $$"""
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "ExternalNotification.Enabled": "Bark",
                  "ExternalNotification.Bark.SendKey": "{{legacyEncryptedValue}}"
                }
              }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.GuiOnly, manualImport: false);

        Assert.True(report.Success);
        Assert.Contains(
            report.Warnings,
            warning => warning.Contains("ExternalNotification.Bark.SendKey", StringComparison.Ordinal)
                       && warning.Contains("could not be decrypted", StringComparison.Ordinal)
                       && warning.Contains("cleared", StringComparison.Ordinal));
        var unreadableValue = Assert.Single(report.UnreadableValues);
        Assert.Equal("Default", unreadableValue.ConfigurationName);
        Assert.Equal("ExternalNotification.Bark.SendKey", unreadableValue.Key);
        Assert.Equal("ExternalNotificationBarkSendKey", unreadableValue.DisplayResourceKey);
        var profile = service.CurrentConfig.Profiles["Default"];
        Assert.Equal(string.Empty, profile.Values["ExternalNotification.Bark.SendKey"]?.GetValue<string>());
    }

    [Fact]
    public async Task GuiNewImport_FightCurrentOrLastStage_ShouldStoreSentinelValue()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));

        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.new.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "TaskQueue": [
                    {
                      "$type": "FightTask",
                      "Name": "Fight",
                      "IsEnable": true,
                      "EnableTimesLimit": true,
                      "TimesLimit": 1,
                      "Series": 1,
                      "StagePlan": [""]
                    }
                  ]
                }
              }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.GuiNewOnly, manualImport: false);

        Assert.True(report.Success);
        var task = Assert.Single(service.CurrentConfig.Profiles["Default"].TaskQueue);
        Assert.Equal(FightStageSelection.CurrentOrLast, task.Params["stage"]?.GetValue<string>());
    }

    [Fact]
    public async Task GuiNewImport_RootTimers_ShouldMapCustomProfileSelection()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));

        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.new.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {},
                "Night": {}
              },
              "Timers": {
                "0": { "Enable": true, "Config": "Night", "Hour": 23, "Minute": 45 }
              },
              "Timer": {
                "CustomConfig": true
              }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.GuiNewOnly, manualImport: false);

        Assert.True(report.Success);
        Assert.True(service.CurrentConfig.GlobalValues["Timer.CustomConfig"]?.GetValue<bool>());
        Assert.True(service.CurrentConfig.GlobalValues["Timer.Timer1"]?.GetValue<bool>());
        Assert.Equal(23, service.CurrentConfig.GlobalValues["Timer.Timer1Hour"]?.GetValue<int>());
        Assert.Equal(45, service.CurrentConfig.GlobalValues["Timer.Timer1Min"]?.GetValue<int>());
        Assert.Equal("Night", service.CurrentConfig.GlobalValues["Timer.Timer1.Config"]?.GetValue<string>());
    }

    [Fact]
    public async Task GuiNewImport_RootTimersArray_ShouldMapCustomProfileSelection()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));

        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.new.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {},
                "Night": {}
              },
              "Timers": [
                { "Key": 0, "Value": { "Enable": true, "Config": "Night", "Hour": 22, "Min": 30 } }
              ],
              "Timer": {
                "CustomConfig": true
              }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.GuiNewOnly, manualImport: false);

        Assert.True(report.Success);
        Assert.Equal("Night", service.CurrentConfig.GlobalValues["Timer.Timer1.Config"]?.GetValue<string>());
        Assert.Equal(22, service.CurrentConfig.GlobalValues["Timer.Timer1Hour"]?.GetValue<int>());
        Assert.Equal(30, service.CurrentConfig.GlobalValues["Timer.Timer1Min"]?.GetValue<int>());
    }

    [Fact]
    public async Task GuiNewImport_DevV2ProfileGuiSections_ShouldMapToLegacyProfileKeys()
    {
        // WPF dev-v2 382009b97b moved every per-profile setting into Configurations.<name>.Gui.<Section>;
        // the inverse map lives in Helper/ConfigConverter.cs at 3badedda9b.
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));

        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.new.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "TaskQueue": [],
                  "Gui": {
                    "ConnectSettings": {
                      "Address": "10.6.0.6:7555",
                      "Config": "MuMuEmulator12",
                      "AddressHistory": [ "127.0.0.1:5555" ],
                      "TouchMode": "MiniTouch",
                      "Extras": {
                        "MuMuEmulator12": { "IsEnabled": true, "InstanceIndex": 2 }
                      }
                    },
                    "StartUpSettings": {
                      "StartEmulator": true,
                      "EmulatorPath": "/tmp/emulator",
                      "EmulatorWaitSeconds": 90
                    },
                    "RuntimeSettings": {
                      "ClientType": "Bilibili",
                      "StartGame": true,
                      "PreRunScript": "start.bat",
                      "BlockSleep": true
                    },
                    "ThirdParty": {
                      "PenguinId": "42",
                      "ReportToYituliu": false
                    },
                    "ExternalNotification": {
                      "Delivery": {
                        "SendBeforeScheduledStart": true
                      }
                    },
                    "PostActions": 5
                  }
                }
              }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.GuiNewOnly, manualImport: false);

        Assert.True(report.Success);
        var profile = service.CurrentConfig.Profiles["Default"];
        Assert.Equal("10.6.0.6:7555", profile.Values["ConnectAddress"]?.GetValue<string>());
        Assert.Equal("MuMuEmulator12", profile.Values["ConnectConfig"]?.GetValue<string>());
        Assert.Equal("minitouch", profile.Values["TouchMode"]?.GetValue<string>());
        Assert.Equal(
            ["127.0.0.1:5555"],
            ReadStringArray(Assert.IsType<JsonArray>(profile.Values["ConnectAddressHistory"])));
        Assert.True(profile.Values["MuMu12ExtrasEnabled"]?.GetValue<bool>());
        Assert.Equal("2", profile.Values["MuMu12Index"]?.GetValue<string>());

        Assert.True(profile.Values["Start.OpenEmulatorAfterLaunch"]?.GetValue<bool>());
        Assert.Equal("/tmp/emulator", profile.Values["Start.EmulatorPath"]?.GetValue<string>());
        Assert.Equal(90, profile.Values["Start.EmulatorWaitSeconds"]?.GetValue<int>());

        Assert.Equal("Bilibili", profile.Values["ClientType"]?.GetValue<string>());
        Assert.True(profile.Values["StartGame"]?.GetValue<bool>());
        Assert.Equal("start.bat", profile.Values["Start.StartsWithScript"]?.GetValue<string>());
        Assert.True(profile.Values["Start.BlockSleep"]?.GetValue<bool>());

        Assert.Equal("42", profile.Values["PenguinId"]?.GetValue<string>());
        Assert.False(profile.Values["EnableYituliu"]?.GetValue<bool>());
        Assert.True(profile.Values["ExternalNotification.SendBeforeScheduledStart"]?.GetValue<bool>());

        // Legacy post action flags (5 = ExitArknights | ExitEmulator) are materialized into the
        // structured profile config by PostActionLegacyMigration, same as the gui.json path.
        var postActions = Assert.IsType<JsonObject>(profile.Values["TaskQueue.PostAction"]);
        Assert.True(postActions["exit_arknights"]?.GetValue<bool>());
        Assert.True(postActions["exit_emulator"]?.GetValue<bool>());
        Assert.False(postActions["exit_self"]?.GetValue<bool>());
        Assert.False(profile.Values.ContainsKey("MainFunction.PostActions"));

        // The dev-v2 section wrapper must not be stored as an opaque profile value.
        Assert.False(profile.Values.ContainsKey("Gui"));
        Assert.DoesNotContain(report.Warnings, warning => warning.Contains("Gui.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GuiNewImport_DevV2ProfileCopilotSection_ShouldMapToProfileValues_AndRoundTrip()
    {
        // WPF dev-v2 382009b97b / 5b39509df6：Copilot 设置迁入每档案 Copilot 分节
        // （TaskList/UserAdditional/EnableUserAdditional/SupportMode/UseFormation/SelectFormation/LoopTimes）
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));

        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.new.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "TaskQueue": [],
                  "Gui": {},
                  "Copilot": {
                    "TaskList": [
                      {
                        "Name": "1-7",
                        "FilePath": "resource/copilot/1-7.json",
                        "CopilotId": 123,
                        "IsNavNameOverride": true,
                        "Index": 0
                      }
                    ],
                    "UserAdditional": [
                      { "Name": "史尔特尔", "Skill": 3, "Module": 5 }
                    ],
                    "EnableUserAdditional": true,
                    "SupportMode": 3,
                    "UseFormation": true,
                    "SelectFormation": 2,
                    "LoopTimes": 5
                  }
                }
              }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.GuiNewOnly, manualImport: false);

        Assert.True(report.Success);
        var profile = service.CurrentConfig.Profiles["Default"];
        Assert.False(profile.Values.ContainsKey("Copilot"));
        Assert.DoesNotContain(report.Warnings, warning => warning.Contains("Copilot.", StringComparison.Ordinal));

        var taskList = Assert.IsType<JsonArray>(profile.Values["Copilot.CopilotTaskList"]);
        var task = Assert.IsType<JsonObject>(taskList[0]);
        Assert.Equal("1-7", task["Name"]?.GetValue<string>());
        Assert.True(task["IsNavNameOverride"]?.GetValue<bool>());

        var userAdditionalText = profile.Values["Copilot.UserAdditional"]?.GetValue<string>();
        Assert.False(string.IsNullOrWhiteSpace(userAdditionalText));
        var userAdditional = JsonNode.Parse(userAdditionalText!);
        var userAdditionalItem = Assert.IsType<JsonObject>(Assert.IsType<JsonArray>(userAdditional)[0]);
        Assert.Equal("史尔特尔", userAdditionalItem["Name"]?.GetValue<string>());
        Assert.Equal(3, userAdditionalItem["Skill"]?.GetValue<int>());
        Assert.Equal(5, userAdditionalItem["Module"]?.GetValue<int>());

        Assert.True(profile.Values["Copilot.AddUserAdditional"]?.GetValue<bool>());
        Assert.Equal("3", profile.Values["Copilot.SupportUnitUsage"]?.GetValue<string>());
        Assert.True(profile.Values["Copilot.UseFormation"]?.GetValue<bool>());
        Assert.Equal(2, profile.Values["Copilot.SelectFormation"]?.GetValue<int>());
        Assert.Equal(5, profile.Values["Copilot.LoopTimes"]?.GetValue<int>());

        // 档案化落盘往返：重新加载后键值与列表内容保持
        await service.SaveAsync();
        var reloaded = CreateService(root);
        await reloaded.LoadOrBootstrapAsync();
        var reloadedProfile = reloaded.CurrentConfig.Profiles["Default"];
        Assert.Equal(
            "1-7",
            Assert.IsType<JsonArray>(reloadedProfile.Values["Copilot.CopilotTaskList"])[0]?["Name"]?.GetValue<string>());
        Assert.True(reloadedProfile.Values["Copilot.AddUserAdditional"]?.GetValue<bool>());
        Assert.Equal("3", reloadedProfile.Values["Copilot.SupportUnitUsage"]?.GetValue<string>());
        Assert.True(reloadedProfile.Values["Copilot.UseFormation"]?.GetValue<bool>());
        Assert.Equal(2, reloadedProfile.Values["Copilot.SelectFormation"]?.GetValue<int>());
        Assert.Equal(5, reloadedProfile.Values["Copilot.LoopTimes"]?.GetValue<int>());
    }

    [Fact]
    public async Task CopilotLegacyGlobalSettings_ShouldMigrateIntoEveryProfile_AndPersist()
    {
        // WPF dev-v2 5b39509df6：旧扁平 Copilot 键迁移后 DeleteValue；MAAUnified 的旧键是全局值，
        // 迁移时搬到所有档案（旧语义对每个档案同样生效），并从 GlobalValues 删除
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));

        var bootstrap = CreateService(root);
        await bootstrap.LoadOrBootstrapAsync();
        bootstrap.CurrentConfig.Profiles["Alt"] = new UnifiedProfile();
        bootstrap.CurrentConfig.GlobalValues["Copilot.CopilotTaskList"] = JsonValue.Create(
            """[{"Name":"1-7","FilePath":"resource/copilot/1-7.json","IsRaid":false,"IsChecked":true}]""");
        bootstrap.CurrentConfig.GlobalValues["Copilot.UserAdditional"] =
            JsonValue.Create("""[{"name":"史尔特尔","skill":3,"module":1}]""");
        bootstrap.CurrentConfig.GlobalValues["Copilot.AddUserAdditional"] = JsonValue.Create(true);
        bootstrap.CurrentConfig.GlobalValues["Copilot.SelectFormation"] = JsonValue.Create(3);
        bootstrap.CurrentConfig.GlobalValues["Copilot.SupportUnitUsage"] = JsonValue.Create(3);
        bootstrap.CurrentConfig.GlobalValues["Copilot.LoopTimes"] = JsonValue.Create(7);
        await bootstrap.SaveAsync();

        var migrated = CreateService(root);
        await migrated.LoadOrBootstrapAsync();

        Assert.False(migrated.CurrentConfig.GlobalValues.ContainsKey("Copilot.CopilotTaskList"));
        Assert.False(migrated.CurrentConfig.GlobalValues.ContainsKey("Copilot.SelectFormation"));
        foreach (var profileName in new[] { "Default", "Alt" })
        {
            var profile = migrated.CurrentConfig.Profiles[profileName];
            Assert.Equal(3, profile.Values["Copilot.SelectFormation"]?.GetValue<int>());
            Assert.Equal(3, profile.Values["Copilot.SupportUnitUsage"]?.GetValue<int>());
            Assert.Equal(7, profile.Values["Copilot.LoopTimes"]?.GetValue<int>());
            Assert.True(profile.Values["Copilot.AddUserAdditional"]?.GetValue<bool>());
            Assert.NotNull(profile.Values["Copilot.CopilotTaskList"]);
        }

        // 迁移结果已落盘：再次加载仍然保持且不会重复迁移
        var reloaded = CreateService(root);
        await reloaded.LoadOrBootstrapAsync();
        Assert.False(reloaded.CurrentConfig.GlobalValues.ContainsKey("Copilot.LoopTimes"));
        Assert.Equal(7, reloaded.CurrentConfig.Profiles["Alt"].Values["Copilot.LoopTimes"]?.GetValue<int>());
    }

    [Fact]
    public async Task GuiNewImport_DevV2UnsupportedProfileGuiKeys_ShouldWarnInsteadOfDropping()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));

        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.new.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "TaskQueue": [],
                  "Gui": {
                    "RuntimeSettings": {
                      "ClientType": "Official",
                      "EnableRunDurationLimit": true
                    },
                    "StartUpSettings": {
                      "PcClientPath": "/tmp/pc-client"
                    },
                    "PostActions": 256
                  }
                }
              }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.GuiNewOnly, manualImport: false);

        Assert.True(report.Success);
        // dev-v2 5a55fc8ac1 run duration limit keys are supported now (P1-01), so they import
        // instead of warning.
        Assert.True(
            service.CurrentConfig.Profiles["Default"].Values["RuntimeSettings.EnableRunDurationLimit"]?.GetValue<bool>());
        Assert.DoesNotContain(
            report.Warnings,
            warning => warning.Contains("Gui.RuntimeSettings.EnableRunDurationLimit", StringComparison.Ordinal));
        Assert.Contains(
            report.Warnings,
            warning => warning.Contains("Gui.StartUpSettings.PcClientPath", StringComparison.Ordinal));
        Assert.Contains(
            report.Warnings,
            warning => warning.Contains("unsupported flags 0x100", StringComparison.Ordinal));
        Assert.Equal(
            "Official",
            service.CurrentConfig.Profiles["Default"].Values["ClientType"]?.GetValue<string>());
    }

    [Fact]
    public async Task GuiNewImport_DevV2UpdateSection_ShouldMapRenamedSubKeys()
    {
        // dev-v2 382009b97b renamed root `VersionUpdate` to `Update` and renamed
        // UpdateCheck/UpdateAutoCheck/Package (see Configuration/Global/Update.cs at 3badedda9b).
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));
        var guiNewPath = Path.Combine(root, "config", "gui.new.json");
        await File.WriteAllTextAsync(
            guiNewPath,
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {}
              },
              "Update": {
                "CheckOnStartup": false,
                "CheckOnSchedule": true,
                "UpdatePackage": "MAA-v6.21.5-win-x64.zip",
                "VersionType": "Nightly",
                "UpdateSource": "MirrorChyan",
                "MirrorChyanCdk": "cdk-token",
                "MirrorChyanCdkExpiredTime": 1767225600,
                "AllowNightlyUpdates": true,
                "Proxy": "127.0.0.1:7890",
                "ProxyType": "Http",
                "ShowUpdaterConsole": true,
                "GitHubToken": "ghp_secret"
              }
            }
            """);

        var importer = new GuiNewJsonConfigImporter();
        var target = new UnifiedConfig();
        var report = new ImportReport();
        await importer.ImportAsync(
            LegacyConfigSnapshot.FromPaths(guiNewPath, null),
            target,
            report,
            fillMissingOnly: false);

        Assert.False(target.GlobalValues["VersionUpdate.StartupUpdateCheck"]?.GetValue<bool>());
        Assert.True(target.GlobalValues["VersionUpdate.ScheduledUpdateCheck"]?.GetValue<bool>());
        Assert.Equal("MAA-v6.21.5-win-x64.zip", target.GlobalValues["VersionUpdate.package"]?.GetValue<string>());
        Assert.Equal("Nightly", target.GlobalValues["VersionUpdate.VersionType"]?.GetValue<string>());
        Assert.Equal("MirrorChyan", target.GlobalValues["VersionUpdate.ResourceUpdateSource"]?.GetValue<string>());
        Assert.Equal(
            "cdk-token",
            target.GlobalValues["VersionUpdate.ResourceUpdateSource.MirrorChyanCdk"]?.GetValue<string>());
        Assert.Equal(
            1767225600,
            target.GlobalValues["VersionUpdate.UpdateSource.MirrorChyanCdkExpired"]?.GetValue<int>());
        Assert.True(target.GlobalValues["VersionUpdate.AllowNightlyUpdates"]?.GetValue<bool>());
        Assert.Equal("127.0.0.1:7890", target.GlobalValues["VersionUpdate.Proxy"]?.GetValue<string>());
        Assert.True(target.GlobalValues["VersionUpdate.ShowUpdaterConsole"]?.GetValue<bool>());
        Assert.False(target.GlobalValues.ContainsKey("Update"));
        Assert.Contains(
            report.Warnings,
            warning => warning.Contains("Update key `GitHubToken`", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GuiNewImport_DevV2TimerList_ShouldImportEachEntryById()
    {
        // dev-v2 382009b97b replaced the `Timers` dictionary with `TimerSettings.List` of
        // `Timer { Id, IsEnabled, Config, Hour, Minute }`; ids are 0-based (ConfigConverter).
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));

        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.new.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {},
                "Night": {}
              },
              "Timers": {
                "ForceScheduledStart": true,
                "ShowWindowBeforeForceScheduledStart": false,
                "CustomConfig": true,
                "NotifyBeforeScheduledStart": true,
                "ScheduledStartNotificationMinutes": 15,
                "ScheduledWakeUp": true,
                "List": [
                  { "Id": 0, "IsEnabled": true, "Config": "Night", "Hour": 7, "Minute": 5 },
                  { "Id": 3, "IsEnabled": null, "Config": "Default", "Hour": 23, "Minute": 0 }
                ]
              }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.GuiNewOnly, manualImport: false);

        Assert.True(report.Success);
        var globals = service.CurrentConfig.GlobalValues;
        Assert.True(globals["Timer.ForceScheduledStart"]?.GetValue<bool>());
        Assert.False(globals["Timer.ShowWindowBeforeForceScheduledStart"]?.GetValue<bool>());
        Assert.True(globals["Timer.CustomConfig"]?.GetValue<bool>());
        Assert.True(globals["Timer.NotifyBeforeScheduledStart"]?.GetValue<bool>());
        Assert.Equal(15, globals["Timer.ScheduledStartNotificationMinutes"]?.GetValue<int>());
        Assert.True(globals["Timer.ScheduledWakeUp"]?.GetValue<bool>());

        Assert.True(globals["Timer.Timer1"]?.GetValue<bool>());
        Assert.Equal(7, globals["Timer.Timer1Hour"]?.GetValue<int>());
        Assert.Equal(5, globals["Timer.Timer1Min"]?.GetValue<int>());
        Assert.Equal("Night", globals["Timer.Timer1.Config"]?.GetValue<string>());

        // `IsEnabled: null` is the WPF half-checked state; the settings page stores it as an empty string.
        Assert.Equal(string.Empty, globals["Timer.Timer4"]?.GetValue<string>());
        Assert.Equal(23, globals["Timer.Timer4Hour"]?.GetValue<int>());
        Assert.Equal(0, globals["Timer.Timer4Min"]?.GetValue<int>());
        Assert.Equal("Default", globals["Timer.Timer4.Config"]?.GetValue<string>());

        Assert.DoesNotContain(report.Warnings, warning => warning.Contains("`List`", StringComparison.Ordinal));
        Assert.DoesNotContain(report.Warnings, warning => warning.Contains("Timer entry `List`", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GuiNewImport_DevV2RootGui_ShouldMapToLegacyGlobalKeys()
    {
        // dev-v2 382009b97b renamed root `GUI` to `Gui` and moved Background/Performance into it.
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));

        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.new.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {}
              },
              "Gui": {
                "Localization": "en-us",
                "DarkMode": "Dark",
                "MinimizeOnStartup": true,
                "TaskQueueInverseMode": true,
                "GuideStep": 4,
                "SettingOrders": [ "GUI" ],
                "Performance": {
                  "UseGpu": true,
                  "IgnoreBadModulesAndUseSoftwareRendering": true
                },
                "Background": {
                  "ImagePath": "bg.png",
                  "Opacity": 70
                }
              }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.GuiNewOnly, manualImport: false);

        Assert.True(report.Success);
        var globals = service.CurrentConfig.GlobalValues;
        Assert.Equal("en-us", globals["GUI.Localization"]?.GetValue<string>());
        Assert.Equal("Dark", globals["Theme.Mode"]?.GetValue<string>());
        Assert.True(globals["GUI.MinimizeDirectly"]?.GetValue<bool>());
        Assert.True(globals["MainFunction.InverseMode"]?.GetValue<bool>());
        Assert.Equal(4, globals["Guide.StepIndex"]?.GetValue<int>());
        Assert.True(globals["Performance.UseGpu"]?.GetValue<bool>());
        // dev-v2 f464016813 nests the software rendering switch in the global Performance section.
        Assert.True(globals["Performance.IgnoreBadModulesAndUseSoftwareRendering"]?.GetValue<bool>());
        Assert.False(globals.ContainsKey("GUI.IgnoreBadModulesAndUseSoftwareRendering"));
        Assert.Equal("bg.png", globals["GUI.Background.ImagePath"]?.GetValue<string>());
        Assert.Equal(70, globals["GUI.Background.Opacity"]?.GetValue<int>());
        Assert.DoesNotContain(
            report.Warnings,
            warning => warning.Contains("Root `Gui` key `SettingOrders`", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ManualLegacyImport_FallbackGlobalValues_ShouldFillMissingUiScale()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));
        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {}
              },
              "Global": {}
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(
            new LegacyImportRequest(
                LegacyConfigSnapshot.FromPaths(null, Path.Combine(root, "config", "gui.json")),
                ImportSource.GuiOnly,
                ManualImport: true,
                AllowPartialImport: true,
                FallbackGlobalValues: new Dictionary<string, JsonNode?>
                {
                    [MAAUnified.Compat.Constants.ConfigurationKeys.UiScalePercent] = JsonValue.Create(125),
                }));

        Assert.True(report.Success);
        Assert.Equal(125, service.CurrentConfig.GlobalValues[MAAUnified.Compat.Constants.ConfigurationKeys.UiScalePercent]?.GetValue<int>());
    }

    [Fact]
    public async Task LegacyGuiImport_ShouldPreserveTimerConfigSelection()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));
        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "Timer.Timer1": true,
                  "Timer.Timer1Hour": 8,
                  "Timer.Timer1Min": 15,
                  "Timer.Timer1.Config": "Night"
                },
                "Night": {}
              },
              "Global": {
                "Timer.CustomConfig": true
              }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.GuiOnly, manualImport: true);

        Assert.True(report.AppliedConfig);
        Assert.True(service.CurrentConfig.GlobalValues["Timer.CustomConfig"]?.GetValue<bool>());
        Assert.Equal("Night", service.CurrentConfig.Profiles["Default"].Values["Timer.Timer1.Config"]?.GetValue<string>());
    }

    [Fact]
    public async Task LegacyGuiImport_ShouldCanonicalizeStartAndConnectionKeys()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));
        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.json"),
            """
            {
              "Current": "BAccount",
              "Configurations": {
                "BAccount": {
                  "Start.ClientType": "Bilibili",
                  "Start.StartGame": "True",
                  "Connect.TouchMode": "maatouch",
                  "Connect.AutoDetect": "False",
                  "Connect.AttachWindow.ScreencapMethod": "16",
                  "Connect.AttachWindow.MouseMethod": "32",
                  "Connect.AttachWindow.KeyboardMethod": "128"
                }
              }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.GuiOnly, manualImport: false);

        Assert.True(report.Success);
        var profile = service.CurrentConfig.Profiles["BAccount"];
        Assert.Equal("Bilibili", profile.Values["ClientType"]?.GetValue<string>());
        Assert.Equal("True", profile.Values["StartGame"]?.GetValue<string>());
        Assert.Equal("maatouch", profile.Values["TouchMode"]?.GetValue<string>());
        Assert.Equal("False", profile.Values["AutoDetect"]?.GetValue<string>());
        Assert.Equal("16", profile.Values["AttachWindowScreencapMethod"]?.GetValue<string>());
        Assert.Equal("32", profile.Values["AttachWindowMouseMethod"]?.GetValue<string>());
        Assert.Equal("128", profile.Values["AttachWindowKeyboardMethod"]?.GetValue<string>());
        Assert.False(profile.Values.ContainsKey("Start.ClientType"));
        Assert.False(profile.Values.ContainsKey("Start.StartGame"));
    }

    [Fact]
    public async Task LegacyGuiImport_ShouldNormalizeHighConfidenceProfileValues()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));
        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.json"),
            """
            {
              "Current": "MuMu",
              "Configurations": {
                "MuMu": {
                  "Connect.ConnectConfig": "mumu",
                  "Connect.TouchMode": "MaaFramework",
                  "Connect.AttachWindow.ScreencapMethod": 2,
                  "Connect.AttachWindow.MouseMethod": 32,
                  "Connect.AttachWindow.KeyboardMethod": 2,
                  "Connect.MuMu12Index": 3,
                  "Connect.LdPlayerIndex": 4,
                  "Connect.AddressHistory": "[\"127.0.0.1:5555\",\"10.0.0.1:7555\"]"
                },
                "Androws": {
                  "Connect.ConnectConfig": "Androws",
                  "Connect.TouchMode": "ADB Input",
                  "Connect.AttachWindow.KeyboardMethod": 4
                }
              }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.GuiOnly, manualImport: false);

        Assert.True(report.Success);
        var mumu = service.CurrentConfig.Profiles["MuMu"];
        Assert.Equal("MuMuEmulator12", mumu.Values["ConnectConfig"]?.GetValue<string>());
        Assert.Equal("MaaFwAdb", mumu.Values["TouchMode"]?.GetValue<string>());
        Assert.Equal("2", mumu.Values["AttachWindowScreencapMethod"]?.GetValue<string>());
        Assert.Equal("32", mumu.Values["AttachWindowMouseMethod"]?.GetValue<string>());
        Assert.Equal("2", mumu.Values["AttachWindowKeyboardMethod"]?.GetValue<string>());
        Assert.Equal("3", mumu.Values["MuMu12Index"]?.GetValue<string>());
        Assert.Equal("4", mumu.Values["LdPlayerIndex"]?.GetValue<string>());
        var history = Assert.IsType<JsonArray>(mumu.Values["ConnectAddressHistory"]);
        Assert.Equal(["127.0.0.1:5555", "10.0.0.1:7555"], ReadStringArray(history));

        var androws = service.CurrentConfig.Profiles["Androws"];
        Assert.Equal("Androws", androws.Values["ConnectConfig"]?.GetValue<string>());
        Assert.Equal("adb", androws.Values["TouchMode"]?.GetValue<string>());
        Assert.Equal("4", androws.Values["AttachWindowKeyboardMethod"]?.GetValue<string>());
    }

    [Fact]
    public async Task LegacyGuiImport_ShouldNormalizeLocalizedClientTypeValues()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));
        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "Start.ClientType": "官服"
                },
                "BAccount": {
                  "Start.ClientType": "b服"
                },
                "EnAccount": {
                  "Start.ClientType": "Official (CN)"
                }
              }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.GuiOnly, manualImport: false);

        Assert.True(report.Success);
        Assert.Equal("Official", service.CurrentConfig.Profiles["Default"].Values["ClientType"]?.GetValue<string>());
        Assert.Equal("Bilibili", service.CurrentConfig.Profiles["BAccount"].Values["ClientType"]?.GetValue<string>());
        Assert.Equal("Official", service.CurrentConfig.Profiles["EnAccount"].Values["ClientType"]?.GetValue<string>());
    }

    [Fact]
    public async Task GuiNewImport_FightSeriesAuto_ShouldPreserveAutoValue()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));

        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.new.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "Start.ClientType": "BILIBILI",
                  "TaskQueue": [
                    {
                      "$type": "FightTask",
                      "Name": "Fight",
                      "IsEnable": true,
                      "StagePlan": ["1-7"],
                      "Series": "auto"
                    }
                  ]
                }
              }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.GuiNewOnly, manualImport: false);

        Assert.True(report.Success);
        var profile = service.CurrentConfig.Profiles["Default"];
        Assert.Equal("Bilibili", profile.Values["ClientType"]?.GetValue<string>());
        var task = Assert.Single(profile.TaskQueue);
        Assert.Equal(0, task.Params["series"]?.GetValue<int>());
        Assert.Equal("Bilibili", task.Params["client_type"]?.GetValue<string>());

        var (dto, issues) = TaskParamCompiler.ReadFight(task, strict: true);
        Assert.Empty(issues);
        Assert.Equal(0, dto.Series);
    }

    [Fact]
    public async Task GuiNewImport_FightSeriesNumericZeroAndMinusOne_ShouldPreserveValues()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));

        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.new.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "TaskQueue": [
                    {
                      "$type": "FightTask",
                      "Name": "FightAutoSeries",
                      "IsEnable": true,
                      "StagePlan": ["1-7"],
                      "Series": 0
                    },
                    {
                      "$type": "FightTask",
                      "Name": "FightNoSwitchSeries",
                      "IsEnable": true,
                      "StagePlan": ["1-7"],
                      "Series": -1
                    }
                  ]
                }
              }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.GuiNewOnly, manualImport: false);

        Assert.True(report.Success);
        var tasks = service.CurrentConfig.Profiles["Default"].TaskQueue;
        Assert.Equal(2, tasks.Count);
        Assert.Equal(0, tasks[0].Params["series"]?.GetValue<int>());
        Assert.Equal(-1, tasks[1].Params["series"]?.GetValue<int>());
    }

    [Fact]
    public async Task GuiNewImport_ShouldNormalizeHighConfidenceLegacyTaskValues()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));

        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.new.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "TaskQueue": [
                    {
                      "$type": "FightTask",
                      "Name": "Fight",
                      "IsEnable": true,
                      "StagePlan": "1-7; CE-6|AP-5",
                      "UseOptionalStage": true,
                      "StageResetMode": "Invalid",
                      "Series": "auto"
                    },
                    {
                      "$type": "InfrastTask",
                      "Name": "Infrast",
                      "IsEnable": true,
                      "Mode": "Custom",
                      "PlanSelect": -1
                    },
                    {
                      "$type": "RoguelikeTask",
                      "Name": "RoguelikeCollapse",
                      "IsEnable": true,
                      "Theme": "3",
                      "Mode": "CLP_PDS"
                    },
                    {
                      "$type": "RoguelikeTask",
                      "Name": "RoguelikeCollapseSami",
                      "IsEnable": true,
                      "Theme": "Sami",
                      "Mode": "CLP_PDS"
                    },
                    {
                      "$type": "RoguelikeTask",
                      "Name": "RoguelikeCollectible",
                      "IsEnable": true,
                      "Theme": "Mizuki",
                      "Mode": "Collectible",
                      "CollectibleStartAwards": "hot_water|shield;Idea;ticket"
                    },
                    {
                      "$type": "RoguelikeTask",
                      "Name": "RoguelikeFindPlaytime",
                      "IsEnable": true,
                      "Theme": "JieGarden",
                      "Mode": "FindPlaytime",
                      "FindPlaytimeTarget": "Shu"
                    },
                    {
                      "$type": "ReclamationTask",
                      "Name": "ReclamationNoSave",
                      "IsEnable": true,
                      "Theme": 0,
                      "Mode": "ProsperityNoSave",
                      "IncrementMode": "长按"
                    },
                    {
                      "$type": "ReclamationTask",
                      "Name": "ReclamationArchive",
                      "IsEnable": true,
                      "Mode": "ProsperityInSave",
                      "ToolToCraft": "荧光棒;工具A;工具A;工具B,工具C|工具D"
                    },
                    {
                      "$type": "ReclamationTask",
                      "Name": "ReclamationUnsupported",
                      "IsEnable": true,
                      "Theme": "RelaunchAnchor",
                      "Mode": 16
                    },
                    {
                      "$type": "RecruitTask",
                      "Name": "Recruit",
                      "IsEnable": true,
                      "Level6Choose": true,
                      "PreserveTagEnabled": true,
                      "PreserveTagList": ["支援机械", "高级资深干员"]
                    },
                    {
                      "$type": "CustomTask",
                      "Name": "Custom",
                      "IsEnable": true,
                      "CustomTaskName": "Fight\r\nRecruit；Roguelike|Reclamation，Award"
                    }
                  ]
                }
              }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.GuiNewOnly, manualImport: false);

        Assert.True(report.Success);
        var tasks = service.CurrentConfig.Profiles["Default"].TaskQueue;
        var fight = tasks.Single(task => task.Name == "Fight");
        Assert.Equal("Ignore", fight.Params["_ui_stage_reset_mode"]?.GetValue<string>());
        Assert.Equal(["1-7", "CE-6", "AP-5"], ReadStringArray((JsonArray)fight.Params["_ui_stage_plan"]!));

        var infrast = tasks.Single(task => task.Name == "Infrast");
        Assert.Equal(10000, infrast.Params["mode"]?.GetValue<int>());
        Assert.Equal(-1, infrast.Params["plan_index"]?.GetValue<int>());

        var roguelikeCollapse = tasks.Single(task => task.Name == "RoguelikeCollapse");
        Assert.Equal("Sarkaz", roguelikeCollapse.Params["theme"]?.GetValue<string>());
        Assert.Equal(0, roguelikeCollapse.Params["mode"]?.GetValue<int>());

        var roguelikeCollapseSami = tasks.Single(task => task.Name == "RoguelikeCollapseSami");
        Assert.Equal("Sami", roguelikeCollapseSami.Params["theme"]?.GetValue<string>());
        Assert.Equal(5, roguelikeCollapseSami.Params["mode"]?.GetValue<int>());

        var roguelikeCollectible = tasks.Single(task => task.Name == "RoguelikeCollectible");
        Assert.Equal("Mizuki", roguelikeCollectible.Params["theme"]?.GetValue<string>());
        Assert.Equal(4, roguelikeCollectible.Params["mode"]?.GetValue<int>());
        Assert.Equal(["hot_water", "shield"], ReadEnabledCollectibleAwards(roguelikeCollectible.Params["collectible_mode_start_list"]));

        var roguelikeFindPlaytime = tasks.Single(task => task.Name == "RoguelikeFindPlaytime");
        Assert.Equal(20001, roguelikeFindPlaytime.Params["mode"]?.GetValue<int>());
        Assert.Equal(2, roguelikeFindPlaytime.Params["find_playTime_target"]?.GetValue<int>());

        // WPF dev-v2 66e003377c: Reclamation queue entries are migrated into the toolbox
        // mini-game settings (Toolbox.MiniGame.Reclamation.*); the first entry supplies the
        // values and duplicates are dropped. No Reclamation row is left in the queue.
        Assert.DoesNotContain(tasks, task => string.Equals(task.Type, TaskModuleTypes.Reclamation, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(report.Warnings, warning => warning.Contains("Toolbox.MiniGame.Reclamation", StringComparison.Ordinal));

        var globals = service.CurrentConfig.GlobalValues;
        Assert.Equal("Fire", globals[ToolboxSettingKeys.ReclamationTheme]?.GetValue<string>());
        Assert.Equal(0, globals[ToolboxSettingKeys.ReclamationMode]?.GetValue<int>());
        Assert.Equal(1, globals[ToolboxSettingKeys.ReclamationIncrementMode]?.GetValue<int>());
        Assert.Equal(16, globals[ToolboxSettingKeys.ReclamationMaxCraftCountPerRound]?.GetValue<int>());
        Assert.Equal("荧光棒", globals[ToolboxSettingKeys.ReclamationToolToCraft]?.GetValue<string>());
        Assert.True(globals[ToolboxSettingKeys.ReclamationClearStore]?.GetValue<bool>());
        Assert.Equal("Reclamation", globals[MAAUnified.Compat.Constants.ConfigurationKeys.MiniGameTaskName]?.GetValue<string>());
        Assert.DoesNotContain(report.Warnings, warning => warning.Contains("RA/RelaunchAnchor", StringComparison.Ordinal));

        var recruit = tasks.Single(task => task.Name == "Recruit");
        // WPF dev-v2 exposes level-6 auto-confirm as a config-only flag.
        Assert.Contains(6, ((JsonArray)recruit.Params["select"]!).Select(item => item?.GetValue<int>()));
        Assert.Contains(6, ((JsonArray)recruit.Params["confirm"]!).Select(item => item?.GetValue<int>()));
        Assert.True(recruit.Params["_ui_preserve_tags_enabled"]?.GetValue<bool>());
        Assert.Equal(
            ["支援机械", "高级资深干员"],
            ReadStringArray((JsonArray)recruit.Params["preserve_tags"]!));
        Assert.DoesNotContain("skip_robot", recruit.Params.Select(pair => pair.Key), StringComparer.Ordinal);

        var custom = tasks.Single(task => task.Name == "Custom");
        Assert.Equal(
            ["Fight\r\nRecruit；Roguelike|Reclamation", "Award"],
            ReadStringArray((JsonArray)custom.Params["task_names"]!));
    }

    [Fact]
    public async Task GuiNewImport_AwardSignInEvent_MapsToCoreSignInEvent()
    {
        // WPF dev-v2 (19699d8483) adds AwardTask.SignInEvent; v6.3.7 payloads have
        // no such property and must keep defaulting to off.
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));

        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.new.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "TaskQueue": [
                    {
                      "$type": "AwardTask",
                      "Name": "AwardDevV2",
                      "IsEnable": true,
                      "Mail": true,
                      "SignInEvent": true
                    },
                    {
                      "$type": "AwardTask",
                      "Name": "AwardFrozen",
                      "IsEnable": true
                    }
                  ]
                }
              }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.GuiNewOnly, manualImport: false);

        Assert.True(report.Success);
        var tasks = service.CurrentConfig.Profiles["Default"].TaskQueue;
        var devV2 = tasks.Single(task => task.Name == "AwardDevV2");
        Assert.True(devV2.Params["signinevent"]?.GetValue<bool>());
        Assert.True(devV2.Params["mail"]?.GetValue<bool>());

        var frozen = tasks.Single(task => task.Name == "AwardFrozen");
        Assert.False(frozen.Params["signinevent"]?.GetValue<bool>());
    }

    [Fact]
    public async Task GuiNewImport_SwitchThemeAndOperProgressTasks_AreSupportedManagedTypes()
    {
        // WPF dev-v2 adds both task types (9c86aae937 / a412e6a294); imported rows must
        // stay enabled with their managed parameter shape instead of degrading to an
        // unsupported disabled row.
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));

        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.new.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "TaskQueue": [
                    {
                      "$type": "SwitchThemeTask",
                      "Name": "Theme",
                      "IsEnable": true,
                      "Themes": ["罗德岛", "彩六"]
                    },
                    {
                      "$type": "OperProgressTask",
                      "Name": "Oper",
                      "IsEnable": true,
                      "Plans": [
                        {
                          "Role": "Warrior",
                          "Name": "陈",
                          "Elite": 2,
                          "SkillLevel": 7,
                          "SkillMastery": [0, 0, 3],
                          "ShowRole": false
                        },
                        {
                          "Role": "Special",
                          "Name": "罗宾",
                          "Elite": 0,
                          "SkillLevel": 0,
                          "SkillMastery": [0, 0, 0]
                        }
                      ]
                    }
                  ]
                }
              }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.GuiNewOnly, manualImport: false);

        Assert.True(report.Success);
        var tasks = service.CurrentConfig.Profiles["Default"].TaskQueue;
        Assert.Equal(2, tasks.Count);
        Assert.DoesNotContain(
            report.Warnings,
            warning => warning.Contains("Unsupported legacy task type", StringComparison.Ordinal));

        var theme = tasks.Single(task => task.Name == "Theme");
        Assert.Equal(TaskModuleTypes.SwitchTheme, theme.Type);
        Assert.True(theme.IsEnabled);
        Assert.Equal(
            ["罗德岛", "彩六"],
            ReadStringArray((JsonArray)theme.Params["themes"]!));

        var oper = tasks.Single(task => task.Name == "Oper");
        Assert.Equal(TaskModuleTypes.OperProgress, oper.Type);
        Assert.True(oper.IsEnabled);
        var plans = Assert.IsType<JsonArray>(oper.Params["plans"]);
        Assert.Equal(2, plans.Count);
        Assert.Equal("陈", plans[0]!["name"]!.GetValue<string>());
        Assert.Equal("Warrior", plans[0]!["role"]!.GetValue<string>());
        Assert.Equal(2, plans[0]!["elite"]!.GetValue<int>());
        Assert.Equal(7, plans[0]!["skill_level"]!.GetValue<int>());
        Assert.Equal([0, 0, 3], plans[0]!["skill_mastery"]!.AsArray().Select(node => node!.GetValue<int>()));
        // Zero targets are omitted so Core treats them as "no requirement".
        Assert.Null(plans[1]!["elite"]);
        Assert.Null(plans[1]!["skill_level"]);
        Assert.Null(plans[1]!["skill_mastery"]);
    }

    [Fact]
    public async Task GuiNewImport_DepotMaintainTask_IsSupportedManagedType()
    {
        // WPF dev-v2 P0-08 (38b2e9e4f2 ... 0302381585): DepotMaintainTask must import as a
        // supported managed row instead of degrading into a disabled unsupported row.
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));

        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.new.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "TaskQueue": [
                    {
                      "$type": "DepotMaintainTask",
                      "Name": "Depot",
                      "IsEnable": true,
                      "UpdateDepot": true,
                      "IsStageManually": true,
                      "SkipDuringActivity": true,
                      "SkipDuringResourceCollection": false,
                      "OnlyFirstInsufficientPlan": true,
                      "SortByProgress": true,
                      "CountDualChipAsChipPack": true,
                      "UseAutoSeries": true,
                      "UseMedicine": false,
                      "UseStone": true,
                      "UseExpiringMedicine": true,
                      "PlanList": [
                        {
                          "Stage": "1-7",
                          "DropId": "30012",
                          "DropCount": 120,
                          "UseMedicine": true,
                          "MedicineCount": 2,
                          "UseStone": false,
                          "StoneCount": 0,
                          "TaskId": 3
                        },
                        {
                          "Stage": "CE-6",
                          "DropId": "4001",
                          "DropCount": 5000000,
                          "UseMedicine": false,
                          "MedicineCount": 0,
                          "UseStone": true,
                          "StoneCount": 1
                        }
                      ]
                    }
                  ]
                }
              }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.GuiNewOnly, manualImport: false);

        Assert.True(report.Success);
        Assert.DoesNotContain(
            report.Warnings,
            warning => warning.Contains("Unsupported legacy task type", StringComparison.Ordinal));

        var task = Assert.Single(service.CurrentConfig.Profiles["Default"].TaskQueue);
        Assert.Equal(TaskModuleTypes.DepotMaintain, task.Type);
        Assert.True(task.IsEnabled);
        Assert.True(task.Params["update_depot"]?.GetValue<bool>());
        Assert.True(task.Params["is_stage_manually"]?.GetValue<bool>());
        Assert.True(task.Params["skip_during_activity"]?.GetValue<bool>());
        Assert.False(task.Params["skip_during_resource_collection"]?.GetValue<bool>());
        Assert.True(task.Params["only_first_insufficient_plan"]?.GetValue<bool>());
        Assert.True(task.Params["sort_by_progress"]?.GetValue<bool>());
        Assert.True(task.Params["count_dual_chip_as_chip_pack"]?.GetValue<bool>());
        Assert.True(task.Params["use_auto_series"]?.GetValue<bool>());
        Assert.False(task.Params["use_medicine"]?.GetValue<bool>());
        Assert.True(task.Params["use_stone"]?.GetValue<bool>());
        Assert.True(task.Params["use_expiring_medicine"]?.GetValue<bool>());

        var plans = Assert.IsType<JsonArray>(task.Params["plans"]);
        Assert.Equal(2, plans.Count);
        Assert.Equal("1-7", plans[0]!["stage"]!.GetValue<string>());
        Assert.Equal("30012", plans[0]!["drop_id"]!.GetValue<string>());
        Assert.Equal(120, plans[0]!["drop_count"]!.GetValue<int>());
        Assert.True(plans[0]!["use_medicine"]!.GetValue<bool>());
        Assert.Equal(2, plans[0]!["medicine_count"]!.GetValue<int>());
        Assert.False(plans[0]!["use_stone"]!.GetValue<bool>());
        Assert.Equal("CE-6", plans[1]!["stage"]!.GetValue<string>());
        Assert.Equal(5000000, plans[1]!["drop_count"]!.GetValue<int>());
        // TaskId is a per-run Core id, not configuration.
        Assert.Null(plans[0]!["task_id"]);
    }

    [Fact]
    public async Task GuiNewImport_InfrastDevV2Params_MapToManagedShape()
    {
        // WPF dev-v2 c281b7aaca / 59963a90cb / 97d5e8ac38 / 3e570d1c23: the three
        // Fiammetta target fields collapse into the deduped target list, and the
        // weekly schedule stays UI-only state.
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));

        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.new.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "TaskQueue": [
                    {
                      "$type": "InfrastTask",
                      "Name": "Infrast",
                      "IsEnable": true,
                      "Mode": "Normal",
                      "FiammettaTarget1": "清流",
                      "FiammettaTarget2": "清流",
                      "FiammettaTarget3": " ",
                      "FiammettaRecoveryEnabled": true,
                      "UsePinusSylvestris": true,
                      "UsePerceptionInformation": false,
                      "UseWorldlyPlight": true,
                      "UseAbyssalHunter": false,
                      "UseWeeklySchedule": true,
                      "WeeklySchedule": {
                        "Monday": false,
                        "Tuesday": true
                      }
                    },
                    {
                      "$type": "InfrastTask",
                      "Name": "InfrastFrozen",
                      "IsEnable": true,
                      "Mode": "Normal"
                    }
                  ]
                }
              }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.GuiNewOnly, manualImport: false);

        Assert.True(report.Success);
        var tasks = service.CurrentConfig.Profiles["Default"].TaskQueue;
        var infrast = tasks.Single(task => task.Name == "Infrast");
        Assert.Equal(TaskModuleTypes.Infrast, infrast.Type);
        Assert.Equal(["清流"], ReadStringArray((JsonArray)infrast.Params["fiammetta_targets"]!));
        Assert.True(infrast.Params["fiammetta_recovery_enabled"]?.GetValue<bool>());
        Assert.True(infrast.Params["use_pinus_sylvestris"]?.GetValue<bool>());
        Assert.False(infrast.Params["use_perception_information"]?.GetValue<bool>());
        Assert.True(infrast.Params["use_worldly_plight"]?.GetValue<bool>());
        Assert.False(infrast.Params["use_abyssal_hunter"]?.GetValue<bool>());
        Assert.True(infrast.Params["_ui_use_weekly_schedule"]?.GetValue<bool>());
        Assert.False(infrast.Params["_ui_weekly_schedule_monday"]?.GetValue<bool>());
        Assert.True(infrast.Params["_ui_weekly_schedule_tuesday"]?.GetValue<bool>());
        // Missing day entries default to enabled, matching WPF JsonPredict behavior.
        Assert.True(infrast.Params["_ui_weekly_schedule_sunday"]?.GetValue<bool>());
        Assert.True(infrast.Params["_ui_weekly_schedule_saturday"]?.GetValue<bool>());

        var runtime = TaskParamCompiler.BuildCoreParams(TaskModuleTypes.Infrast, infrast.Params);
        Assert.Equal(["清流"], ReadStringArray((JsonArray)runtime["fiammetta_targets"]!));
        Assert.True(runtime["fiammetta_recovery_enabled"]?.GetValue<bool>());
        Assert.DoesNotContain(
            runtime.Select(pair => pair.Key),
            key => key.StartsWith("_ui_", StringComparison.Ordinal));

        // v6.3.7 rows carry no Fiammetta fields: keep the WPF task defaults.
        var frozen = tasks.Single(task => task.Name == "InfrastFrozen");
        Assert.Equal(
            ["清流", "可露希尔", "但书"],
            ReadStringArray((JsonArray)frozen.Params["fiammetta_targets"]!));
        Assert.False(frozen.Params["fiammetta_recovery_enabled"]?.GetValue<bool>());
        Assert.False(frozen.Params["_ui_use_weekly_schedule"]?.GetValue<bool>());
        Assert.True(frozen.Params["_ui_weekly_schedule_monday"]?.GetValue<bool>());
    }

    [Fact]
    public async Task GuiNewImport_RoguelikeModeRepairsAndEmptyParadigms_MatchFrozenWpf()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));

        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.new.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "TaskQueue": [
                    {
                      "$type": "RoguelikeTask",
                      "Name": "CollapseEmpty",
                      "IsEnable": true,
                      "Theme": "Sami",
                      "Mode": "CLP_PDS"
                    },
                    {
                      "$type": "RoguelikeTask",
                      "Name": "CollapseFilled",
                      "IsEnable": true,
                      "Theme": "Sami",
                      "Mode": "CLP_PDS",
                      "ExpectedCollapsalParadigms": "范式A;范式B"
                    },
                    {
                      "$type": "RoguelikeTask",
                      "Name": "FindPlaytimeSami",
                      "IsEnable": true,
                      "Theme": "Sami",
                      "Mode": "FindPlaytime",
                      "FindPlaytimeTarget": "Shu"
                    }
                  ]
                }
              }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.GuiNewOnly, manualImport: false);

        Assert.True(report.Success);
        var tasks = service.CurrentConfig.Profiles["Default"].TaskQueue;

        // Frozen WPF omits expected_collapsal_paradigms for empty text; Core would
        // otherwise treat a present-but-empty array as an explicit empty expectation.
        var collapseEmpty = tasks.Single(task => task.Name == "CollapseEmpty");
        Assert.Equal(5, collapseEmpty.Params["mode"]?.GetValue<int>());
        Assert.False(collapseEmpty.Params.ContainsKey("expected_collapsal_paradigms"));

        var collapseFilled = tasks.Single(task => task.Name == "CollapseFilled");
        Assert.Equal(5, collapseFilled.Params["mode"]?.GetValue<int>());
        Assert.Equal(
            ["范式A", "范式B"],
            ReadStringArray((JsonArray)collapseFilled.Params["expected_collapsal_paradigms"]!));

        // Frozen WPF keeps the stored theme and clamps FindPlaytime back to Exp.
        var findPlaytime = tasks.Single(task => task.Name == "FindPlaytimeSami");
        Assert.Equal(0, findPlaytime.Params["mode"]?.GetValue<int>());
        Assert.Equal("Sami", findPlaytime.Params["theme"]?.GetValue<string>());
        Assert.Equal(2, findPlaytime.Params["find_playTime_target"]?.GetValue<int>());
    }

    [Fact]
    public async Task GuiImport_FlatRoguelikeIterationFlagsMissing_ShouldDefaultFalse()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));

        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Monthly": {
                  "TaskQueue.AutoRoguelike.IsChecked": true,
                  "TaskQueue.Order.AutoRoguelike": 0,
                  "Roguelike.Mode": "MonthlySquad"
                },
                "Deep": {
                  "TaskQueue.AutoRoguelike.IsChecked": true,
                  "TaskQueue.Order.AutoRoguelike": 0,
                  "Roguelike.Mode": "DeepExploration"
                }
              }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.GuiOnly, manualImport: false);

        Assert.True(report.Success);
        var monthly = service.CurrentConfig.Profiles["Monthly"].TaskQueue.Single(task => task.Type == TaskModuleTypes.Roguelike);
        Assert.Equal(6, monthly.Params["mode"]?.GetValue<int>());
        Assert.False(monthly.Params["monthly_squad_auto_iterate"]?.GetValue<bool>());
        Assert.False(monthly.Params["monthly_squad_check_comms"]?.GetValue<bool>());

        var deep = service.CurrentConfig.Profiles["Deep"].TaskQueue.Single(task => task.Type == TaskModuleTypes.Roguelike);
        Assert.Equal(7, deep.Params["mode"]?.GetValue<int>());
        Assert.False(deep.Params["deep_exploration_auto_iterate"]?.GetValue<bool>());
    }

    [Fact]
    public async Task GuiNewImport_RoguelikeIterationFlagsMissing_ShouldDefaultTrue()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));

        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.new.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Monthly": {
                  "TaskQueue": [
                    {
                      "$type": "RoguelikeTask",
                      "Name": "MonthlySquad",
                      "IsEnable": true,
                      "Mode": "MonthlySquad"
                    }
                  ]
                },
                "Deep": {
                  "TaskQueue": [
                    {
                      "$type": "RoguelikeTask",
                      "Name": "DeepExploration",
                      "IsEnable": true,
                      "Mode": "DeepExploration"
                    }
                  ]
                }
              }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.GuiNewOnly, manualImport: false);

        Assert.True(report.Success);
        var monthly = service.CurrentConfig.Profiles["Monthly"].TaskQueue.Single(task => task.Type == TaskModuleTypes.Roguelike);
        Assert.Equal(6, monthly.Params["mode"]?.GetValue<int>());
        Assert.True(monthly.Params["monthly_squad_auto_iterate"]?.GetValue<bool>());
        Assert.True(monthly.Params["monthly_squad_check_comms"]?.GetValue<bool>());

        var deep = service.CurrentConfig.Profiles["Deep"].TaskQueue.Single(task => task.Type == TaskModuleTypes.Roguelike);
        Assert.Equal(7, deep.Params["mode"]?.GetValue<int>());
        Assert.True(deep.Params["deep_exploration_auto_iterate"]?.GetValue<bool>());
    }

    [Fact]
    public async Task GuiNewImport_RoguelikeGatedFields_ImportUnconditionallyLikeFrozenWpf()
    {
        // Frozen WPF stores every RoguelikeTask field per task and gates only at
        // serialization, so import must keep mode/investment/theme-gated intent even
        // when the current mode/theme does not use it.
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));

        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.new.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "TaskQueue": [
                    {
                      "$type": "RoguelikeTask",
                      "Name": "RoguelikeGated",
                      "IsEnable": true,
                      "Theme": "Sami",
                      "Mode": "Investment",
                      "Investment": false,
                      "InvestCount": 50,
                      "InvestWithMoreScore": true,
                      "StopWhenDepositFull": true,
                      "RefreshTraderWithDice": true,
                      "StopAtFinalBoss": true,
                      "StopWhenLevelMax": true,
                      "CollectibleShopping": true,
                      "SquadCollectible": "生活至上分队",
                      "StartWithEliteTwo": true,
                      "StartWithEliteTwoOnly": true,
                      "CollectibleStartAwards": "shield",
                      "MonthlySquadAutoIterate": false,
                      "MonthlySquadCheckComms": false,
                      "DeepExplorationAutoIterate": false,
                      "FindPlaytimeTarget": "Shu"
                    }
                  ]
                }
              }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.GuiNewOnly, manualImport: false);

        Assert.True(report.Success);
        var task = service.CurrentConfig.Profiles["Default"].TaskQueue.Single(task => task.Type == TaskModuleTypes.Roguelike);
        Assert.Equal(1, task.Params["mode"]?.GetValue<int>());
        Assert.False(task.Params["investment_enabled"]?.GetValue<bool>());
        Assert.True(task.Params["investment_with_more_score"]?.GetValue<bool>());
        Assert.Equal(50, task.Params["investments_count"]?.GetValue<int>());
        Assert.True(task.Params["stop_when_investment_full"]?.GetValue<bool>());
        Assert.True(task.Params["refresh_trader_with_dice"]?.GetValue<bool>());
        Assert.True(task.Params["stop_at_final_boss"]?.GetValue<bool>());
        Assert.True(task.Params["stop_at_max_level"]?.GetValue<bool>());
        Assert.True(task.Params["collectible_mode_shopping"]?.GetValue<bool>());
        Assert.Equal("生活至上分队", task.Params["collectible_mode_squad"]?.GetValue<string>());
        Assert.True(task.Params["start_with_elite_two"]?.GetValue<bool>());
        Assert.True(task.Params["only_start_with_elite_two"]?.GetValue<bool>());
        Assert.Equal(["shield"], ReadEnabledCollectibleAwards(task.Params["collectible_mode_start_list"]));
        Assert.False(task.Params["monthly_squad_auto_iterate"]?.GetValue<bool>());
        Assert.False(task.Params["monthly_squad_check_comms"]?.GetValue<bool>());
        Assert.False(task.Params["deep_exploration_auto_iterate"]?.GetValue<bool>());
        Assert.Equal(2, task.Params["find_playTime_target"]?.GetValue<int>());

        // Run-form gating still lives in the compiler: investment is disabled, so
        // its sub-params are withheld like frozen WPF serialization, and the
        // mode-0/4/6/7 keys are omitted for the imported mode-1 task.
        var (dto, _) = TaskParamCompiler.ReadRoguelike(task, strict: false);
        var compiled = TaskParamCompiler.CompileRoguelike(dto, service.CurrentConfig.Profiles["Default"], service.CurrentConfig);
        Assert.False(compiled.Params["investment_enabled"]?.GetValue<bool>());
        Assert.False(compiled.Params.ContainsKey("investment_with_more_score"));
        Assert.False(compiled.Params.ContainsKey("investments_count"));
        Assert.False(compiled.Params.ContainsKey("stop_at_final_boss"));
        Assert.False(compiled.Params.ContainsKey("collectible_mode_shopping"));
        Assert.False(compiled.Params.ContainsKey("monthly_squad_auto_iterate"));
        Assert.False(compiled.Params.ContainsKey("deep_exploration_auto_iterate"));
        Assert.False(compiled.Params["refresh_trader_with_dice"]?.GetValue<bool>());
    }

    [Fact]
    public async Task GuiNewImport_RoguelikeDisabledFoldartalAndSeedToggles_PreserveContentLikeFrozenWpf()
    {
        // Frozen WPF stores foldartal/seed toggles independently of their content, so
        // import must keep toggle-off text instead of dropping it.
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));

        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.new.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "TaskQueue": [
                    {
                      "$type": "RoguelikeTask",
                      "Name": "RoguelikeToggles",
                      "IsEnable": true,
                      "Theme": "Sami",
                      "Mode": "Collectible",
                      "Squad": "生活至上分队",
                      "SamiFirstFloorFoldartal": false,
                      "SamiFirstFloorFoldartals": "号角",
                      "SamiNewSquad2StartingFoldartal": false,
                      "SamiNewSquad2StartingFoldartals": "火炬;涟漪",
                      "StartWithSeed": false,
                      "Seed": "seed,rogue_1,3"
                    }
                  ]
                }
              }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.GuiNewOnly, manualImport: false);

        Assert.True(report.Success);
        var task = service.CurrentConfig.Profiles["Default"].TaskQueue.Single(task => task.Type == TaskModuleTypes.Roguelike);
        Assert.False(task.Params["first_floor_foldartal_enabled"]?.GetValue<bool>());
        Assert.Equal("号角", task.Params["first_floor_foldartal"]?.GetValue<string>());
        Assert.False(task.Params["start_foldartal_list_enabled"]?.GetValue<bool>());
        Assert.Equal(["火炬", "涟漪"], ReadStringArray((JsonArray)task.Params["start_foldartal_list"]!));
        Assert.False(task.Params["start_with_seed_enabled"]?.GetValue<bool>());
        Assert.Equal("seed,rogue_1,3", task.Params["start_with_seed"]?.GetValue<string>());

        // Toggle-off content is never emitted to Core, matching frozen WPF serialize.
        var (dto, _) = TaskParamCompiler.ReadRoguelike(task, strict: false);
        var compiled = TaskParamCompiler.CompileRoguelike(dto, service.CurrentConfig.Profiles["Default"], service.CurrentConfig);
        Assert.False(compiled.Params.ContainsKey("first_floor_foldartal"));
        Assert.False(compiled.Params.ContainsKey("start_foldartal_list"));
        Assert.False(compiled.Params.ContainsKey("start_with_seed"));
    }

    [Fact]
    public async Task GuiNewImport_PenguinAndYituliuLegacyKeys_ShouldFeedTaskReportParams()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));

        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.new.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "Penguin.Id": "penguin-user",
                  "Penguin.EnablePenguin": true,
                  "Yituliu.EnableYituliu": true,
                  "TaskQueue": [
                    {
                      "$type": "FightTask",
                      "Name": "Fight",
                      "IsEnable": true,
                      "StagePlan": ["1-7"]
                    },
                    {
                      "$type": "RecruitTask",
                      "Name": "Recruit",
                      "IsEnable": true
                    }
                  ]
                }
              }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.GuiNewOnly, manualImport: false);

        Assert.True(report.Success);
        var profile = service.CurrentConfig.Profiles["Default"];
        Assert.True(profile.Values["EnablePenguin"]?.GetValue<bool>());
        Assert.True(profile.Values["EnableYituliu"]?.GetValue<bool>());
        Assert.Equal("penguin-user", profile.Values["PenguinId"]?.GetValue<string>());

        var fight = profile.TaskQueue.Single(task => task.Name == "Fight");
        Assert.True(fight.Params["report_to_penguin"]?.GetValue<bool>());
        Assert.True(fight.Params["report_to_yituliu"]?.GetValue<bool>());
        Assert.Equal("penguin-user", fight.Params["penguin_id"]?.GetValue<string>());
        Assert.Equal("penguin-user", fight.Params["yituliu_id"]?.GetValue<string>());

        var recruit = profile.TaskQueue.Single(task => task.Name == "Recruit");
        Assert.True(recruit.Params["report_to_penguin"]?.GetValue<bool>());
        Assert.True(recruit.Params["report_to_yituliu"]?.GetValue<bool>());
        Assert.Equal("penguin-user", recruit.Params["penguin_id"]?.GetValue<string>());
        Assert.Equal("penguin-user", recruit.Params["yituliu_id"]?.GetValue<string>());
    }

    [Fact]
    public async Task GuiNewImport_UserDataUpdateTask_ShouldMapExistingTaskSchema()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));

        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.new.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "TaskQueue": [
                    {
                      "$type": "UserDataUpdateTask",
                      "Name": "UpdateData",
                      "IsEnable": true,
                      "UpdateOperBox": false,
                      "UpdateDepot": true,
                      "TriggerInterval": 2
                    }
                  ]
                }
              }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.GuiNewOnly, manualImport: false);

        Assert.True(report.Success);
        var task = Assert.Single(service.CurrentConfig.Profiles["Default"].TaskQueue);
        Assert.Equal(TaskModuleTypes.UserDataUpdate, task.Type);
        Assert.False(task.Params["update_oper_box"]?.GetValue<bool>());
        Assert.True(task.Params["update_depot"]?.GetValue<bool>());
        Assert.Equal(UserDataUpdateTaskParamsDto.TriggerWeekly, task.Params["trigger_interval"]?.GetValue<string>());
    }

    [Fact]
    public async Task GuiNewImport_SingleStepTaskMarker_ShouldImportButValidateAsMissingRuntimeParams()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));

        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.new.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "TaskQueue": [
                    {
                      "$type": "SingleStepTask",
                      "Name": "Single step",
                      "IsEnable": true
                    },
                    {
                      "$type": "SingleStepTask",
                      "Name": "Single step explicit",
                      "IsEnable": true,
                      "type": "copilot",
                      "subtype": "stage",
                      "details": {
                        "stage_name": "1-7"
                      }
                    }
                  ]
                }
              }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.GuiNewOnly, manualImport: false);

        Assert.True(report.Success);
        Assert.Empty(report.Errors);
        var tasks = service.CurrentConfig.Profiles["Default"].TaskQueue;
        Assert.Equal(2, tasks.Count);

        var task = tasks.Single(task => task.Name == "Single step");
        Assert.Equal(TaskModuleTypes.SingleStep, task.Type);
        Assert.True(task.IsEnabled);
        Assert.Empty(task.Params);

        var compiled = TaskParamCompiler.CompileTask(
            task,
            service.CurrentConfig.Profiles["Default"],
            service.CurrentConfig,
            strict: true);

        Assert.Equal(TaskModuleTypes.SingleStep, compiled.NormalizedType);
        // WPF parity: a bare imported SingleStep marker is skipped (non-blocking)
        // instead of blocking the whole queue.
        Assert.False(compiled.HasBlockingIssues);
        Assert.Contains(compiled.Issues, issue => issue.Code == "TaskCompileSkipAppend" && !issue.Blocking);
        Assert.DoesNotContain(compiled.Issues, issue => issue.Code == "SingleStepTypeMissing");

        var explicitTask = tasks.Single(task => task.Name == "Single step explicit");
        Assert.Equal(TaskModuleTypes.SingleStep, explicitTask.Type);
        Assert.Equal("copilot", explicitTask.Params["type"]?.GetValue<string>());
        Assert.Equal("stage", explicitTask.Params["subtype"]?.GetValue<string>());
        Assert.Equal("1-7", explicitTask.Params["details"]?["stage_name"]?.GetValue<string>());

        var explicitCompiled = TaskParamCompiler.CompileTask(
            explicitTask,
            service.CurrentConfig.Profiles["Default"],
            service.CurrentConfig,
            strict: true);

        Assert.False(explicitCompiled.HasBlockingIssues);
        Assert.Equal("copilot", explicitCompiled.Params["type"]?.GetValue<string>());
        Assert.Equal("stage", explicitCompiled.Params["subtype"]?.GetValue<string>());
        Assert.Equal("1-7", explicitCompiled.Params["details"]?["stage_name"]?.GetValue<string>());
    }

    [Fact]
    public async Task GuiNewImport_FightMedicineExpireDays_ShouldPreserveExpiringMedicineValue()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));

        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.new.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "TaskQueue": [
                    {
                      "$type": "FightTask",
                      "Name": "Fight",
                      "IsEnable": true,
                      "UseExpiringMedicine": true,
                      "MedicineExpireDays": 3
                    }
                  ]
                }
              }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.GuiNewOnly, manualImport: false);

        Assert.True(report.Success);
        var task = service.CurrentConfig.Profiles["Default"].TaskQueue.Single(task => task.Type == TaskModuleTypes.Fight);
        Assert.Equal(9999, task.Params["expiring_medicine"]?.GetValue<int>());

        var (dto, issues) = TaskParamCompiler.ReadFight(task, strict: true);
        Assert.Empty(issues);
        Assert.True(dto.UseExpiringMedicine);
        Assert.Equal(9999, dto.ExpiringMedicine);
        Assert.Equal(3, dto.MedicineExpireDays);

        var compiled = TaskParamCompiler.CompileFight(dto, service.CurrentConfig.Profiles["Default"], service.CurrentConfig);
        Assert.Equal(9999, compiled.Params["expiring_medicine"]?.GetValue<int>());
        Assert.Equal(3, compiled.Params["medicine_expire_days"]?.GetValue<int>());
    }

    [Fact]
    public async Task GuiNewImport_FightMedicineExpireDaysMissing_ShouldDefaultToTwoDays()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));

        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.new.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "TaskQueue": [
                    {
                      "$type": "FightTask",
                      "Name": "Fight",
                      "IsEnable": true,
                      "UseExpiringMedicine": true
                    }
                  ]
                }
              }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.GuiNewOnly, manualImport: false);

        Assert.True(report.Success);
        var task = Assert.Single(service.CurrentConfig.Profiles["Default"].TaskQueue);
        Assert.Equal(9999, task.Params["expiring_medicine"]?.GetValue<int>());
        Assert.Equal(2, task.Params["medicine_expire_days"]?.GetValue<int>());
    }

    [Fact]
    public async Task GuiNewImport_FightNullableTriStateFieldsMissing_ShouldUseFalseUiFlags()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));

        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.new.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "TaskQueue": [
                    {
                      "$type": "FightTask",
                      "Name": "Fight",
                      "IsEnable": true,
                      "StagePlan": ["1-7"]
                    }
                  ]
                }
              }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.GuiNewOnly, manualImport: false);

        Assert.True(report.Success);
        var task = service.CurrentConfig.Profiles["Default"].TaskQueue.Single(task => task.Type == TaskModuleTypes.Fight);
        Assert.False(task.Params["_ui_use_medicine"]?.GetValue<bool>());
        Assert.False(task.Params["_ui_use_stone"]?.GetValue<bool>());
        Assert.False(task.Params["_ui_enable_times_limit"]?.GetValue<bool>());
        Assert.False(task.Params["_ui_enable_target_drop"]?.GetValue<bool>());

        var (dto, issues) = TaskParamCompiler.ReadFight(task, strict: true);
        Assert.Empty(issues);
        Assert.Equal(false, dto.UseMedicine);
        Assert.Equal(false, dto.UseStone);
        Assert.Equal(false, dto.EnableTimesLimit);
        Assert.Equal(false, dto.EnableTargetDrop);
    }

    [Fact]
    public async Task GuiNewImport_FightStoneShouldBeDisabledWithoutPermission()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));

        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.new.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "TaskQueue": [
                    {
                      "$type": "FightTask",
                      "Name": "Fight",
                      "IsEnable": true,
                      "UseStoneAllowSave": false,
                      "UseStone": true,
                      "StoneCount": 3,
                      "StagePlan": ["1-7"]
                    }
                  ]
                }
              }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.GuiNewOnly, manualImport: false);

        Assert.True(report.Success);
        var task = Assert.Single(service.CurrentConfig.Profiles["Default"].TaskQueue);
        Assert.False(task.Params["_ui_allow_use_stone_save"]?.GetValue<bool>());
        Assert.False(task.Params["_ui_use_stone"]?.GetValue<bool>());
        Assert.Equal(0, task.Params["stone"]?.GetValue<int>());
    }

    [Fact]
    public async Task GuiNewImport_FightInventoryTargetAndActivityMedicine_ShouldPreserveParityMetadata()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));

        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.new.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "TaskQueue": [
                    {
                      "$type": "FightTask",
                      "Name": "Fight",
                      "IsEnable": true,
                      "EnableTargetDrop": true,
                      "DropId": "30012",
                      "DropCount": 300,
                      "IsInventoryTarget": true,
                      "UseExpireMedicineForActivity": true
                    }
                  ]
                }
              }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.GuiNewOnly, manualImport: false);

        Assert.True(report.Success);
        var task = service.CurrentConfig.Profiles["Default"].TaskQueue.Single(task => task.Type == TaskModuleTypes.Fight);
        Assert.True(task.Params["_ui_is_inventory_target"]?.GetValue<bool>());
        Assert.True(task.Params["_ui_use_expire_medicine_for_activity"]?.GetValue<bool>());

        var (dto, issues) = TaskParamCompiler.ReadFight(task, strict: true);
        Assert.Empty(issues);
        Assert.True(dto.IsInventoryTarget);
        Assert.True(dto.UseExpireMedicineForActivity);
        Assert.Equal("30012", dto.DropId);
        Assert.Equal(300, dto.DropCount);
    }

    [Fact]
    public void GuiNewImport_ReclamationRelaunchAnchorModes_ShouldPreserveRaThemeAndModes()
    {
        // The import migration keeps the first Reclamation entry only (WPF
        // ToolboxSectionMigrationConverter), so RA stage mapping is asserted on the converter.
        var config = new UnifiedConfig();
        var profile = config.Profiles["Default"];
        foreach (var (name, mode) in new[] { ("RA1", 16), ("RA15", 32), ("RA4", 48) })
        {
            var legacyTask = new JsonObject
            {
                ["$type"] = "ReclamationTask",
                ["Name"] = name,
                ["IsEnable"] = true,
                ["Theme"] = "RelaunchAnchor",
                ["Mode"] = mode,
            };

            Assert.True(
                LegacyTaskSchemaConverter.TryConvertLegacyTask(
                    legacyTask,
                    profile,
                    config,
                    out var converted,
                    out var error,
                    LegacyTaskSchemaConversionOptions.GuiNewJson),
                error);

            Assert.Equal(TaskModuleTypes.Reclamation, converted.Type);
            Assert.Equal("RelaunchAnchor", converted.Params["theme"]?.GetValue<string>());
            Assert.Equal(mode, converted.Params["mode"]?.GetValue<int>());

            // Migration keeps the theme/mode pair intact in the toolbox settings.
            var migrated = new UnifiedConfig();
            ToolboxReclamationSettingsMigration.ApplyTaskParams(converted, migrated);
            Assert.Equal("RelaunchAnchor", migrated.GlobalValues[ToolboxSettingKeys.ReclamationTheme]?.GetValue<string>());
            Assert.Equal(mode, migrated.GlobalValues[ToolboxSettingKeys.ReclamationMode]?.GetValue<int>());
        }
    }

    [Fact]
    public async Task GuiNewImport_DevV2ToolboxSection_ShouldMapKnownKeysAndPreserveUnknown()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));

        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.new.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "Toolbox": {
                    "Recruitment": {
                      "ChooseLevel3": false,
                      "ChooseLevel3Time": 300,
                      "AutoSetTime": false,
                      "ShowPotential": false
                    },
                    "OperBox": {
                      "OperBoxExportFormat": "Markdown",
                      "OperBoxAvatarMode": true
                    },
                    "Gacha": {
                      "GachaShowDisclaimerNoMore": true
                    },
                    "Peep": {
                      "PeepTargetFps": 45
                    },
                    "MiniGame": {
                      "EventShopBlackList": "商品A;商品B",
                      "EventShopBlackListLmd": true,
                      "Reclamation": {
                        "Theme": "RelaunchAnchor",
                        "Mode": 32,
                        "IncrementMode": 1,
                        "MaxCraftCountPerRound": 8,
                        "ToolToCraft": "荧光棒;工具A",
                        "ClearStore": false
                      }
                    }
                  }
                }
              }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.GuiNewOnly, manualImport: false);

        Assert.True(report.Success);
        var globals = service.CurrentConfig.GlobalValues;

        // Known per-section keys land on the flat keys MAAUnified still reads.
        Assert.False(globals[LegacyConfigurationKeys.ChooseLevel3]?.GetValue<bool>());
        Assert.Equal(300, globals[LegacyConfigurationKeys.ToolBoxChooseLevel3Time]?.GetValue<int>());
        Assert.False(globals[LegacyConfigurationKeys.AutoSetTime]?.GetValue<bool>());
        Assert.False(globals[LegacyConfigurationKeys.RecruitmentShowPotential]?.GetValue<bool>());
        Assert.Equal(2, globals[OperBoxSelectedExportValueKey]?.GetValue<int>());
        Assert.True(globals[LegacyConfigurationKeys.GachaShowDisclaimerNoMore]?.GetValue<bool>());
        Assert.Equal(45, globals[LegacyConfigurationKeys.PeepTargetFps]?.GetValue<int>());

        // MiniGame keeps dev-v2 names so the toolbox page reads them directly.
        Assert.Equal("商品A;商品B", globals[ToolboxSettingKeys.EventShopBlackList]?.GetValue<string>());
        Assert.True(globals[ToolboxSettingKeys.EventShopBlackListLmd]?.GetValue<bool>());
        Assert.Equal("RelaunchAnchor", globals[ToolboxSettingKeys.ReclamationTheme]?.GetValue<string>());
        Assert.Equal(32, globals[ToolboxSettingKeys.ReclamationMode]?.GetValue<int>());
        Assert.Equal(1, globals[ToolboxSettingKeys.ReclamationIncrementMode]?.GetValue<int>());
        Assert.Equal(8, globals[ToolboxSettingKeys.ReclamationMaxCraftCountPerRound]?.GetValue<int>());
        Assert.Equal("荧光棒;工具A", globals[ToolboxSettingKeys.ReclamationToolToCraft]?.GetValue<string>());
        Assert.False(globals[ToolboxSettingKeys.ReclamationClearStore]?.GetValue<bool>());

        // Keys MAAUnified has no consumer for are preserved verbatim (no silent data loss)
        // and reported as warnings instead of being dropped with the whole Toolbox object.
        Assert.True(globals["Toolbox.OperBox.OperBoxAvatarMode"]?.GetValue<bool>());
        Assert.Contains(
            report.Warnings,
            warning => warning.Contains("Toolbox.OperBox.OperBoxAvatarMode", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GuiNewImport_ReclamationQueueEntry_ShouldRoundTripIntoToolboxSettings()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));

        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.new.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "TaskQueue": [
                    {
                      "$type": "ReclamationTask",
                      "Name": "Reclamation",
                      "IsEnable": true,
                      "Theme": "Tales",
                      "Mode": 0,
                      "IncrementMode": 1,
                      "MaxCraftCountPerRound": 6,
                      "ToolToCraft": "荧光棒;工具A",
                      "ClearStore": false
                    }
                  ]
                }
              }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.GuiNewOnly, manualImport: false);

        Assert.True(report.Success);
        var tasks = service.CurrentConfig.Profiles["Default"].TaskQueue;
        Assert.DoesNotContain(tasks, task => string.Equals(task.Type, TaskModuleTypes.Reclamation, StringComparison.OrdinalIgnoreCase));

        // Round-trip: migrated settings compile back to the same Reclamation payload.
        var profile = service.CurrentConfig.Profiles["Default"];
        var settings = ToolboxReclamationSettingsMigration.ReadSettings(service.CurrentConfig);
        Assert.Equal("Tales", settings.Theme);
        Assert.Equal(0, settings.Mode);
        Assert.Equal(1, settings.IncrementMode);
        Assert.Equal(6, settings.NumCraftBatches);
        Assert.Equal(["荧光棒", "工具A"], settings.ToolsToCraft);
        Assert.False(settings.ClearStore);

        var compiled = TaskParamCompiler.CompileReclamation(settings, profile, service.CurrentConfig);
        Assert.Equal("Reclamation", compiled.NormalizedType);
        Assert.Equal("Tales", compiled.Params["theme"]?.GetValue<string>());
        Assert.Equal(0, compiled.Params["mode"]?.GetValue<int>());
        Assert.Equal(1, compiled.Params["increment_mode"]?.GetValue<int>());
        Assert.Equal(6, compiled.Params["num_craft_batches"]?.GetValue<int>());
        Assert.Equal(
            ["荧光棒", "工具A"],
            ReadStringArray(Assert.IsType<JsonArray>(compiled.Params["tools_to_craft"])));
        Assert.False(compiled.Params["clear_store"]?.GetValue<bool>());

        // The migrated values survive a save/reload cycle and the second import pass.
        await service.SaveAsync();
        var reloaded = CreateService(root);
        await reloaded.LoadOrBootstrapAsync();
        var reloadedSettings = ToolboxReclamationSettingsMigration.ReadSettings(reloaded.CurrentConfig);
        Assert.Equal(settings.Theme, reloadedSettings.Theme);
        Assert.Equal(settings.Mode, reloadedSettings.Mode);
        Assert.Equal(settings.IncrementMode, reloadedSettings.IncrementMode);
        Assert.Equal(settings.NumCraftBatches, reloadedSettings.NumCraftBatches);
        Assert.Equal(settings.ToolsToCraft, reloadedSettings.ToolsToCraft);
        Assert.Equal(settings.ClearStore, reloadedSettings.ClearStore);
    }

    [Fact]
    public async Task GuiImport_FlatReclamationSettings_ShouldMigrateIntoToolboxSettings()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));

        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "TaskQueue.Reclamation.IsChecked": true,
                  "Reclamation.Theme": "Tales",
                  "Reclamation.Mode": "1",
                  "Reclamation.ToolToCraft": "荧光棒;工具A",
                  "Reclamation.ReclamationIncrementMode": "1",
                  "Reclamation.ReclamationMaxCraftCountPerRound": "5",
                  "Reclamation.ReclamationClearStore": "False"
                }
              }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.GuiOnly, manualImport: false);

        Assert.True(report.Success);
        var tasks = service.CurrentConfig.Profiles["Default"].TaskQueue;
        Assert.DoesNotContain(tasks, task => string.Equals(task.Type, TaskModuleTypes.Reclamation, StringComparison.OrdinalIgnoreCase));

        var settings = ToolboxReclamationSettingsMigration.ReadSettings(service.CurrentConfig);
        Assert.Equal("Tales", settings.Theme);
        Assert.Equal(1, settings.Mode);
        Assert.Equal(1, settings.IncrementMode);
        Assert.Equal(5, settings.NumCraftBatches);
        Assert.Equal(["荧光棒", "工具A"], settings.ToolsToCraft);
        Assert.False(settings.ClearStore);
    }

    [Fact]
    public async Task GuiNewImport_RecruitLevel1NotChoose_MigratesToPreserveTagEnabled_LikeDevV2Converter()
    {
        // WPF dev-v2 RecruitTaskHoldTagsConverter turns a stale Level1NotChoose=true
        // into PreserveTagEnabled=true while keeping the stored tag list.
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));

        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.new.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "TaskQueue": [
                    {
                      "$type": "RecruitTask",
                      "Name": "Recruit",
                      "IsEnable": true,
                      "Level1NotChoose": true,
                      "PreserveTagEnabled": false,
                      "PreserveTagList": ["支援机械", "高级资深干员"]
                    }
                  ]
                }
              }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.GuiNewOnly, manualImport: false);

        Assert.True(report.Success);
        var task = service.CurrentConfig.Profiles["Default"].TaskQueue.Single(task => task.Type == TaskModuleTypes.Recruit);
        Assert.True(task.Params["_ui_preserve_tags_enabled"]?.GetValue<bool>());
        Assert.Equal(
            ["支援机械", "高级资深干员"],
            ReadStringArray((JsonArray)task.Params["preserve_tags"]!));
        Assert.DoesNotContain("skip_robot", task.Params.Select(pair => pair.Key), StringComparer.Ordinal);
        Assert.DoesNotContain(1, ((JsonArray)task.Params["confirm"]!).Select(item => item?.GetValue<int>()));
    }

    [Fact]
    public async Task GuiNewImport_RecruitUseExpedited_ShouldDefaultFalseAndPreserveExplicitTrue()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));

        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.new.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "TaskQueue": [
                    {
                      "$type": "RecruitTask",
                      "Name": "Recruit default",
                      "IsEnable": true,
                      "MaxTimes": 2
                    },
                    {
                      "$type": "RecruitTask",
                      "Name": "Recruit expedited",
                      "IsEnable": true,
                      "MaxTimes": 3,
                      "UseExpedited": true
                    }
                  ]
                }
              }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.GuiNewOnly, manualImport: false);

        Assert.True(report.Success);
        var tasks = service.CurrentConfig.Profiles["Default"].TaskQueue;
        var defaultRecruit = tasks.Single(task => task.Name == "Recruit default");
        Assert.False(defaultRecruit.Params["expedite"]?.GetValue<bool>());
        Assert.Null(defaultRecruit.Params["expedite_times"]);

        var expeditedRecruit = tasks.Single(task => task.Name == "Recruit expedited");
        Assert.True(expeditedRecruit.Params["expedite"]?.GetValue<bool>());
        Assert.Equal(3, expeditedRecruit.Params["expedite_times"]?.GetValue<int>());

        var persisted = Assert.IsType<JsonObject>(
            JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, "config", "avalonia.json"))));
        var persistedProfiles = Assert.IsType<JsonObject>(persisted["Profiles"]);
        var persistedDefault = Assert.IsType<JsonObject>(persistedProfiles["Default"]);
        var persistedQueue = Assert.IsType<JsonArray>(persistedDefault["TaskQueue"]);
        var persistedDefaultRecruit = Assert.IsType<JsonObject>(persistedQueue[0]);
        var persistedDefaultParams = Assert.IsType<JsonObject>(persistedDefaultRecruit["Params"]);
        Assert.False(persistedDefaultParams.ContainsKey("expedite"));
        Assert.False(persistedDefaultParams.ContainsKey("expedite_times"));
        var persistedExpeditedRecruit = Assert.IsType<JsonObject>(persistedQueue[1]);
        var persistedExpeditedParams = Assert.IsType<JsonObject>(persistedExpeditedRecruit["Params"]);
        Assert.False(persistedExpeditedParams.ContainsKey("expedite"));
        Assert.False(persistedExpeditedParams.ContainsKey("expedite_times"));
    }

    [Fact]
    public async Task GuiImport_FlatLegacyTaskKeys_ShouldBuildTaskQueueWhenMissing()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));

        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.new.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "Connect.Address": "127.0.0.1:7555"
                }
              }
            }
            """);

        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "TaskQueue.WakeUp.IsChecked": true,
                  "TaskQueue.Order.WakeUp": 0,
                  "TaskQueue.Combat.IsChecked": true,
                  "TaskQueue.Order.Combat": 1,
                  "TaskQueue.Recruiting.IsChecked": true,
                  "TaskQueue.Order.Recruiting": 2,
                  "TaskQueue.Base.IsChecked": true,
                  "TaskQueue.Order.Base": 3,
                  "Start.AccountName": "doctor",
                  "MainFunction.Stage1": "1-7",
                  "MainFunction.UseMedicine": true,
                  "MainFunction.UseMedicine.Quantity": 4,
                  "MainFunction.TimesLimited": true,
                  "MainFunction.TimesLimited.Quantity": 12,
                  "MainFunction.Series.Quantity": 3,
                  "Fight.UseExpiringMedicine": true,
                  "MainFunction.Drops.Enable": true,
                  "MainFunction.Drops.ItemId": "30012",
                  "MainFunction.Drops.Quantity": 300,
                  "Fight.IsInventoryTarget": true,
                  "Fight.UseExpireMedicineForActivity": true,
                  "AutoRecruit.MaxTimes": 2,
                  "AutoRecruit.ChooseLevel5": true,
                  "AutoRecruit.AutoRecruitFirstList": "资深干员;高级资深干员",
                  "AutoRecruit.NotChooseLevel1": true,
                  "AutoRecruit.PreserveTagEnabled": true,
                  "AutoRecruit.PreserveTagList": ["支援机械", "高级资深干员"],
                  "Infrast.InfrastMode": "Custom",
                  "Infrast.CustomInfrastFile": "/tmp/infrast.json",
                  "Infrast.CustomInfrastPlanSelect": 1
                }
              }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.Auto, manualImport: false);

        Assert.True(report.Success);
        Assert.Contains(report.Warnings, warning => warning.Contains("flat task settings", StringComparison.OrdinalIgnoreCase));

        var tasks = service.CurrentConfig.Profiles["Default"].TaskQueue;
        Assert.Contains(tasks, task => task.Type == TaskModuleTypes.StartUp);
        Assert.Contains(tasks, task => task.Type == TaskModuleTypes.Fight);
        Assert.Contains(tasks, task => task.Type == TaskModuleTypes.Recruit);
        Assert.Contains(tasks, task => task.Type == TaskModuleTypes.Infrast);

        var startUp = tasks.Single(task => task.Type == TaskModuleTypes.StartUp);
        Assert.True(startUp.IsEnabled);
        Assert.Equal("doctor", startUp.Params["account_name"]?.GetValue<string>());

        var fight = tasks.Single(task => task.Type == TaskModuleTypes.Fight && task.Name == "Fight");
        Assert.True(fight.IsEnabled);
        Assert.Equal("1-7", fight.Params["stage"]?.GetValue<string>());
        Assert.Equal(4, fight.Params["medicine"]?.GetValue<int>());
        Assert.Equal(12, fight.Params["times"]?.GetValue<int>());
        Assert.Equal(3, fight.Params["series"]?.GetValue<int>());
        Assert.Equal(9999, fight.Params["expiring_medicine"]?.GetValue<int>());
        Assert.True(fight.Params["medicine_expire_days"]?.GetValue<int>() >= 2);
        Assert.True(fight.Params["_ui_is_inventory_target"]?.GetValue<bool>());
        Assert.True(fight.Params["_ui_use_expire_medicine_for_activity"]?.GetValue<bool>());
        Assert.Equal("30012", fight.Params["_ui_drop_id"]?.GetValue<string>());
        Assert.Equal(300, fight.Params["_ui_drop_count"]?.GetValue<int>());

        var recruit = tasks.Single(task => task.Type == TaskModuleTypes.Recruit);
        Assert.True(recruit.IsEnabled);
        Assert.Equal(2, recruit.Params["times"]?.GetValue<int>());
        Assert.Contains(5, ((JsonArray)recruit.Params["select"]!).Select(item => item?.GetValue<int>()));
        Assert.Equal(["资深干员", "高级资深干员"], ReadStringArray((JsonArray)recruit.Params["first_tags"]!));
        // Level1NotChoose=true migrates to preserving the default 支援机械 tag.
        Assert.True(recruit.Params["_ui_preserve_tags_enabled"]?.GetValue<bool>());
        Assert.Equal(["支援机械"], ReadStringArray((JsonArray)recruit.Params["preserve_tags"]!));
        Assert.DoesNotContain("skip_robot", recruit.Params.Select(pair => pair.Key), StringComparer.Ordinal);

        var infrast = tasks.Single(task => task.Type == TaskModuleTypes.Infrast);
        Assert.True(infrast.IsEnabled);
        Assert.Equal(10000, infrast.Params["mode"]?.GetValue<int>());
        Assert.Equal("/tmp/infrast.json", infrast.Params["filename"]?.GetValue<string>());
        Assert.Equal(1, infrast.Params["plan_index"]?.GetValue<int>());
    }

    [Fact]
    public async Task GuiImport_FlatFightMedicineExpireDays_ShouldMapExplicitValue()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));

        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "TaskQueue.Combat.IsChecked": true,
                  "TaskQueue.Order.Combat": 0,
                  "MainFunction.Stage1": "1-7",
                  "Fight.UseExpiringMedicine": true,
                  "Fight.MedicineExpireDays": 5
                }
              }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.GuiOnly, manualImport: false);

        Assert.True(report.Success);
        var task = service.CurrentConfig.Profiles["Default"].TaskQueue.Single(task => task.Type == TaskModuleTypes.Fight && task.Name == "Fight");
        Assert.Equal(9999, task.Params["expiring_medicine"]?.GetValue<int>());
        Assert.Equal(5, task.Params["medicine_expire_days"]?.GetValue<int>());
    }

    [Fact]
    public async Task GuiImport_FlatFightDefaultsAndDisabledRemainingSanityTask_ShouldMatchWpf()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));

        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "TaskQueue.Combat.IsChecked": true,
                  "MainFunction.Stage1": "1-7",
                  "MainFunction.UseMedicine": true,
                  "MainFunction.TimesLimited": true,
                  "MainFunction.Drops.Enable": true,
                  "MainFunction.Drops.ItemId": "30012",
                  "Fight.UseRemainingSanityStage": false,
                  "Fight.RemainingSanityStage": "CE-6"
                }
              }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.GuiOnly, manualImport: false);

        Assert.True(report.Success);
        var fights = service.CurrentConfig.Profiles["Default"].TaskQueue
            .Where(task => task.Type == TaskModuleTypes.Fight)
            .ToArray();
        Assert.Equal(2, fights.Length);

        var fight = Assert.Single(fights, task => task.Name == "Fight");
        Assert.True(fight.IsEnabled);
        Assert.Equal(999, fight.Params["medicine"]?.GetValue<int>());
        Assert.Equal(5, fight.Params["times"]?.GetValue<int>());
        Assert.Equal(0, fight.Params["series"]?.GetValue<int>());
        Assert.Equal(5, fight.Params["drops"]?["30012"]?.GetValue<int>());
        Assert.Equal(5, fight.Params["_ui_drop_count"]?.GetValue<int>());
        Assert.True(fight.Params["_ui_hide_unavailable_stage"]?.GetValue<bool>());

        var remaining = Assert.Single(fights, task => task.Name == "RemainingSanityStage");
        Assert.False(remaining.IsEnabled);
        Assert.Equal("CE-6", remaining.Params["stage"]?.GetValue<string>());
        Assert.Equal(0, remaining.Params["medicine"]?.GetValue<int>());
        Assert.Equal(int.MaxValue, remaining.Params["times"]?.GetValue<int>());
        Assert.Equal(0, remaining.Params["series"]?.GetValue<int>());
        Assert.Equal(5, remaining.Params["_ui_drop_count"]?.GetValue<int>());
        Assert.True(remaining.Params["_ui_hide_unavailable_stage"]?.GetValue<bool>());
    }

    [Fact]
    public async Task GuiImport_FlatFightNullableTriStateFieldsMissing_ShouldUseFalseUiFlags()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));

        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "TaskQueue.Combat.IsChecked": true,
                  "TaskQueue.Order.Combat": 0,
                  "MainFunction.Stage1": "1-7"
                }
              }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.GuiOnly, manualImport: false);

        Assert.True(report.Success);
        var task = service.CurrentConfig.Profiles["Default"].TaskQueue.Single(task => task.Type == TaskModuleTypes.Fight && task.Name == "Fight");
        Assert.False(task.Params["_ui_use_medicine"]?.GetValue<bool>());
        Assert.False(task.Params["_ui_use_stone"]?.GetValue<bool>());
        Assert.False(task.Params["_ui_enable_times_limit"]?.GetValue<bool>());
        Assert.False(task.Params["_ui_enable_target_drop"]?.GetValue<bool>());

        var (dto, issues) = TaskParamCompiler.ReadFight(task, strict: true);
        Assert.Empty(issues);
        Assert.Equal(false, dto.UseMedicine);
        Assert.Equal(false, dto.UseStone);
        Assert.Equal(false, dto.EnableTimesLimit);
        Assert.Equal(false, dto.EnableTargetDrop);
    }

    [Fact]
    public async Task GuiImport_FlatFightBlankAnnihilationStage_ShouldPreserveBlankAndKeepCustomFlagLikeWpf()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));

        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "TaskQueue.Combat.IsChecked": true,
                  "TaskQueue.Order.Combat": 0,
                  "MainFunction.Stage1": "Annihilation",
                  "MainFunction.Annihilation.UseCustom": true,
                  "MainFunction.Annihilation.Stage": ""
                }
              }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.GuiOnly, manualImport: false);

        Assert.True(report.Success);
        var task = service.CurrentConfig.Profiles["Default"].TaskQueue.Single(task => task.Type == TaskModuleTypes.Fight && task.Name == "Fight");
        Assert.True(task.Params["_ui_use_custom_annihilation"]?.GetValue<bool>());
        Assert.Equal(string.Empty, task.Params["_ui_annihilation_stage"]?.GetValue<string>());

        // WPF assigns the blank override unconditionally at serialize time, so the
        // Core stage becomes empty instead of "Annihilation".
        var (dto, issues) = TaskParamCompiler.ReadFight(task, strict: true);
        Assert.Empty(issues);
        var compiled = TaskParamCompiler.CompileFight(
            dto,
            service.CurrentConfig.Profiles["Default"],
            service.CurrentConfig,
            new DateTime(2025, 1, 6, 12, 0, 0, DateTimeKind.Utc));
        var coreParams = TaskParamCompiler.BuildCoreParams(TaskModuleTypes.Fight, compiled.Params);
        Assert.Equal(string.Empty, coreParams["stage"]?.GetValue<string>());
    }

    [Fact]
    public async Task GuiNewImport_FightAnnihilationStageMigration_ShouldMatchWpfCustomFlagGate()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));

        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.new.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "TaskQueue": [
                    {
                      "$type": "FightTask",
                      "Name": "FightBlankAnnihilation",
                      "IsEnable": true,
                      "StagePlan": ["Annihilation"],
                      "UseCustomAnnihilation": true,
                      "AnnihilationStage": ""
                    },
                    {
                      "$type": "FightTask",
                      "Name": "FightDefaultAnnihilation",
                      "IsEnable": true,
                      "StagePlan": ["Annihilation"],
                      "UseCustomAnnihilation": true,
                      "AnnihilationStage": "Annihilation"
                    }
                  ]
                }
              }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.GuiNewOnly, manualImport: false);

        Assert.True(report.Success);
        var tasks = service.CurrentConfig.Profiles["Default"].TaskQueue;

        // A hand-edited blank stage survives migration verbatim and keeps the custom
        // flag, so the Core stage compiles to empty like WPF.
        var blank = tasks.Single(task => task.Name == "FightBlankAnnihilation");
        Assert.True(blank.Params["_ui_use_custom_annihilation"]?.GetValue<bool>());
        Assert.Equal(string.Empty, blank.Params["_ui_annihilation_stage"]?.GetValue<string>());
        var (blankDto, blankIssues) = TaskParamCompiler.ReadFight(blank, strict: true);
        Assert.Empty(blankIssues);
        var blankCompiled = TaskParamCompiler.CompileFight(
            blankDto,
            service.CurrentConfig.Profiles["Default"],
            service.CurrentConfig,
            new DateTime(2025, 1, 6, 12, 0, 0, DateTimeKind.Utc));
        var blankCoreParams = TaskParamCompiler.BuildCoreParams(TaskModuleTypes.Fight, blankCompiled.Params);
        Assert.Equal(string.Empty, blankCoreParams["stage"]?.GetValue<string>());

        // A stored stage equal to the default clears the custom flag on migration,
        // matching WPF ConfigConverter's `stored && AnnihilationStage != "Annihilation"`.
        var defaulted = tasks.Single(task => task.Name == "FightDefaultAnnihilation");
        Assert.False(defaulted.Params["_ui_use_custom_annihilation"]?.GetValue<bool>());
        Assert.Equal("Annihilation", defaulted.Params["_ui_annihilation_stage"]?.GetValue<string>());
        Assert.Equal("Annihilation", defaulted.Params["stage"]?.GetValue<string>());
    }

    [Fact]
    public async Task GuiImport_FlatRecruitNotChooseLevel1_ShouldControlPreserveTagEnabled()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));

        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "TaskQueue.Recruiting.IsChecked": true,
                  "TaskQueue.Order.Recruiting": 0,
                  "AutoRecruit.NotChooseLevel1": false,
                  "AutoRecruit.PreserveTagEnabled": true,
                  "AutoRecruit.PreserveTagList": ["支援机械"]
                }
              }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.GuiOnly, manualImport: false);

        Assert.True(report.Success);
        var task = service.CurrentConfig.Profiles["Default"].TaskQueue.Single(task => task.Type == TaskModuleTypes.Recruit);
        Assert.False(task.Params["_ui_preserve_tags_enabled"]?.GetValue<bool>());
        Assert.Equal(["支援机械"], ReadStringArray((JsonArray)task.Params["preserve_tags"]!));
        Assert.DoesNotContain("skip_robot", task.Params.Select(pair => pair.Key), StringComparer.Ordinal);
    }

    [Fact]
    public async Task GuiImport_FlatRecruitUseExpedited_ShouldImportExplicitTrue()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));

        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "TaskQueue.Recruiting.IsChecked": true,
                  "TaskQueue.Order.Recruiting": 0,
                  "AutoRecruit.MaxTimes": 3,
                  "AutoRecruit.UseExpedited": true
                }
              }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.GuiOnly, manualImport: false);

        Assert.True(report.Success);
        var task = service.CurrentConfig.Profiles["Default"].TaskQueue.Single(task => task.Type == TaskModuleTypes.Recruit);
        Assert.True(task.Params["expedite"]?.GetValue<bool>());
        Assert.Equal(3, task.Params["expedite_times"]?.GetValue<int>());
    }

    [Fact]
    public async Task AutoImport_StartUpRead_ShouldUseGuiLegacyClientTypeWhenTaskParamsAreStale()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));

        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.new.json"),
            """
            {
              "Current": "BAccount",
              "Configurations": {
                "BAccount": {
                  "TaskQueue": [
                    { "$type": "StartUpTask", "Name": "StartUp", "IsEnable": true }
                  ]
                }
              }
            }
            """);

        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.json"),
            """
            {
              "Current": "BAccount",
              "Configurations": {
                "BAccount": {
                  "Start.ClientType": "Bilibili",
                  "Start.StartGame": "True"
                }
              }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.Auto, manualImport: false);

        Assert.True(report.Success);
        var profile = service.CurrentConfig.Profiles["BAccount"];
        var task = Assert.Single(profile.TaskQueue);
        var (dto, issues) = TaskParamCompiler.ReadStartUp(task, profile, service.CurrentConfig, strict: true);
        Assert.Empty(issues);
        Assert.Equal("Bilibili", dto.ClientType);
        Assert.True(dto.StartGameEnabled);
    }

    [Fact]
    public async Task ExistingAvaloniaConfig_SkipsLegacyRead()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));

        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "avalonia.json"),
            """
            {
              "SchemaVersion": 1,
              "CurrentProfile": "Default",
              "Profiles": {
                "Default": { "Values": { "ConnectAddress": "1.1.1.1:5555" }, "TaskQueue": [] }
              },
              "GlobalValues": {},
              "Migration": { "ImportedBy": "test" }
            }
            """);

        await File.WriteAllTextAsync(Path.Combine(root, "config", "gui.new.json"), "{\"Current\":\"Other\"}");

        var service = CreateService(root);
        var result = await service.LoadOrBootstrapAsync();

        Assert.True(result.LoadedFromExistingConfig);
        Assert.Equal("Default", service.CurrentConfig.CurrentProfile);
        Assert.Equal(UnifiedConfig.LatestSchemaVersion, service.CurrentConfig.SchemaVersion);
        var schemaBackupExists = Directory.EnumerateFiles(Path.Combine(root, "config"), "avalonia.json.schema-v1.bak.*").Any();
        Assert.True(schemaBackupExists);
    }

    [Fact]
    public async Task ExistingAvaloniaConfig_LegacyEmptyFightStage_ShouldNormalizeAndPersistSentinel()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));

        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "avalonia.json"),
            """
            {
              "SchemaVersion": 2,
              "CurrentProfile": "Default",
              "Profiles": {
                "Default": {
                  "Values": {},
                  "TaskQueue": [
                    {
                      "Type": "Fight",
                      "Name": "Fight",
                      "IsEnabled": true,
                      "Params": {
                        "stage": "",
                        "medicine": 0,
                        "stone": 0,
                        "times": 1,
                        "series": 1
                      }
                    }
                  ]
                }
              },
              "GlobalValues": {},
              "Migration": {}
            }
            """);

        var service = CreateService(root);
        var result = await service.LoadOrBootstrapAsync();

        Assert.True(result.LoadedFromExistingConfig);
        Assert.Equal(
            FightStageSelection.CurrentOrLast,
            service.CurrentConfig.Profiles["Default"].TaskQueue[0].Params["stage"]?.GetValue<string>());

        var persisted = Assert.IsType<JsonObject>(
            JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, "config", "avalonia.json"))));
        var stage = persisted["Profiles"]?["Default"]?["TaskQueue"]?[0]?["Params"]?["stage"]?.GetValue<string>();
        Assert.Equal(FightStageSelection.CurrentOrLast, stage);
    }

    [Fact]
    public async Task ExistingAvaloniaConfig_ExplicitAchievementAutoCloseFalse_ShouldBePreserved()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));

        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "avalonia.json"),
            """
            {
              "SchemaVersion": 2,
              "CurrentProfile": "Default",
              "Profiles": {
                "MissingSetting": {
                  "Values": {},
                  "TaskQueue": []
                },
                "Default": {
                  "Values": {
                    "Achievement.PopupDisabled": "False",
                    "Achievement.PopupAutoClose": "False"
                  },
                  "TaskQueue": []
                },
                "Alt": {
                  "Values": {
                    "Achievement.PopupDisabled": "False",
                    "Achievement.PopupAutoClose": "False"
                  },
                  "TaskQueue": []
                }
              },
              "GlobalValues": {},
              "Migration": {}
            }
            """);

        var service = CreateService(root);
        var result = await service.LoadOrBootstrapAsync();

        // Frozen WPF preserves an explicit Achievement.PopupAutoClose=false; the load path must not rewrite it.
        Assert.True(result.LoadedFromExistingConfig);
        Assert.Equal("False", service.CurrentConfig.Profiles["Default"].Values["Achievement.PopupAutoClose"]?.GetValue<string>());
        Assert.Equal("False", service.CurrentConfig.Profiles["Alt"].Values["Achievement.PopupAutoClose"]?.GetValue<string>());
        Assert.False(service.CurrentConfig.Profiles["MissingSetting"].Values.ContainsKey("Achievement.PopupAutoClose"));
        Assert.DoesNotContain(
            service.LogService.Snapshot,
            log => log.Message.Contains("achievement popup auto-close", StringComparison.OrdinalIgnoreCase));

        var persisted = Assert.IsType<JsonObject>(
            JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, "config", "avalonia.json"))));
        Assert.Equal("False", persisted["Profiles"]?["Default"]?["Values"]?["Achievement.PopupAutoClose"]?.GetValue<string>());

        AchievementUnlockedEvent? unlocked = null;
        var tracker = new AchievementTrackerService(service, root);
        tracker.AchievementUnlocked += (_, e) => unlocked = e;

        var unlockResult = tracker.Unlock("Linguist");
        Assert.True(unlockResult.Success);
        Assert.NotNull(unlocked);
        Assert.False(unlocked!.AutoClose);
    }

    [Fact]
    public async Task AchievementAutoClosePolicy_ShouldMatchFrozenWpfDefaults()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));

        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "avalonia.json"),
            """
            {
              "SchemaVersion": 2,
              "CurrentProfile": "Default",
              "Profiles": {
                "Default": {
                  "Values": {
                    "Achievement.PopupAutoClose": "True"
                  },
                  "TaskQueue": []
                },
                "NoSetting": {
                  "Values": {},
                  "TaskQueue": []
                }
              },
              "GlobalValues": {},
              "Migration": {}
            }
            """);

        var service = CreateService(root);
        await service.LoadOrBootstrapAsync();

        var policyService = new AchievementFeatureService(service);

        // Frozen WPF default is false: a profile without the key resolves to false.
        service.CurrentConfig.CurrentProfile = "NoSetting";
        var defaultPolicy = await policyService.LoadPolicyAsync();
        Assert.True(defaultPolicy.Success);
        Assert.NotNull(defaultPolicy.Value);
        Assert.False(defaultPolicy.Value!.PopupAutoClose);

        AchievementUnlockedEvent? defaultUnlock = null;
        var tracker = new AchievementTrackerService(service, root);
        tracker.AchievementUnlocked += (_, e) => defaultUnlock = e;
        Assert.True(tracker.Unlock("Linguist").Success);
        Assert.NotNull(defaultUnlock);
        Assert.False(defaultUnlock!.AutoClose);

        // An explicit true keeps the WPF auto-close behavior.
        service.CurrentConfig.CurrentProfile = "Default";
        var explicitPolicy = await policyService.LoadPolicyAsync();
        Assert.True(explicitPolicy.Success);
        Assert.True(explicitPolicy.Value!.PopupAutoClose);

        AchievementUnlockedEvent? explicitUnlock = null;
        tracker.AchievementUnlocked += (_, e) => explicitUnlock = e;
        Assert.True(tracker.Unlock("Lucky").Success);
        Assert.NotNull(explicitUnlock);
        Assert.True(explicitUnlock!.AutoClose);
    }

    [Fact]
    public async Task LoadOrBootstrapAsync_UnreadableFieldValue_RemovesFieldAndKeepsProfiles()
    {
        // WPF dev-v2 8c72cac105 deletes the property named by JsonException.Path and retries
        // instead of dropping the whole configuration.
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));

        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "avalonia.json"),
            """
            {
              "SchemaVersion": 3,
              "CurrentProfile": "Default",
              "Profiles": {
                "Default": {
                  "Values": { "ConnectAddress": "1.1.1.1:5555" },
                  "TaskQueue": "not-an-array"
                }
              },
              "GlobalValues": { "Theme.Mode": "Dark" },
              "Migration": {}
            }
            """);

        var service = CreateService(root);
        var result = await service.LoadOrBootstrapAsync();

        Assert.True(result.LoadedFromExistingConfig);
        var notice = Assert.IsType<ConfigRepairNotice>(result.ConfigRepairNotice);
        Assert.False(notice.RebuiltDefaults);
        Assert.Null(notice.BrokenConfigBackupPath);
        Assert.Contains("$.Profiles.Default.TaskQueue", notice.RemovedJsonPaths);

        Assert.Equal("Default", service.CurrentConfig.CurrentProfile);
        Assert.Equal(
            "1.1.1.1:5555",
            service.CurrentConfig.Profiles["Default"].Values["ConnectAddress"]?.GetValue<string>());
        Assert.Empty(service.CurrentConfig.Profiles["Default"].TaskQueue);
        Assert.Equal("Dark", service.CurrentConfig.GlobalValues["Theme.Mode"]?.GetValue<string>());

        // The repaired document is persisted so the same value is not repaired on every start.
        var persisted = Assert.IsType<JsonObject>(
            JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, "config", "avalonia.json"))));
        Assert.Equal(JsonValueKind.Array, persisted["Profiles"]?["Default"]?["TaskQueue"]?.GetValueKind());
        Assert.Equal(
            "1.1.1.1:5555",
            persisted["Profiles"]?["Default"]?["Values"]?["ConnectAddress"]?.GetValue<string>());
        Assert.Contains(
            service.LogService.Snapshot,
            log => log.Message.Contains("ConfigRepair.Recovered", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoadOrBootstrapAsync_UnreadableFile_PreservesBrokenCopyAndReportsRecovery()
    {
        // 8ad75a3760 surfaces the recovery to the user; MAAUnified reuses the startup notice pipeline.
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));
        var configPath = Path.Combine(root, "config", "avalonia.json");
        const string brokenJson = "{ \"SchemaVersion\": 3, \"Profiles\": { \"Default\": ";
        await File.WriteAllTextAsync(configPath, brokenJson);

        var service = CreateService(root);
        var result = await service.LoadOrBootstrapAsync();

        Assert.True(result.LoadedFromExistingConfig);
        var notice = Assert.IsType<ConfigRepairNotice>(result.ConfigRepairNotice);
        Assert.True(notice.RebuiltDefaults);
        Assert.NotNull(notice.BrokenConfigBackupPath);
        Assert.True(File.Exists(notice.BrokenConfigBackupPath));
        Assert.Equal(brokenJson, await File.ReadAllTextAsync(notice.BrokenConfigBackupPath!));
        var brokenCopy = Assert.Single(
            Directory.EnumerateFiles(Path.Combine(root, "config"), "avalonia.json.broken.*"));
        Assert.Equal(notice.BrokenConfigBackupPath, brokenCopy);

        // Defaults are rebuilt and the file stays loadable.
        Assert.Equal("Default", service.CurrentConfig.CurrentProfile);
        Assert.Equal(UnifiedConfig.LatestSchemaVersion, service.CurrentConfig.SchemaVersion);
        var reloaded = await new AvaloniaJsonConfigStore(root).LoadAsync();
        Assert.NotNull(reloaded);
        Assert.Contains(
            service.LogService.Snapshot,
            log => log.Message.Contains("ConfigRepair.Recovered", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CorruptedAvaloniaConfig_RebuildsDefaults_AndDoesNotCrash()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));
        await File.WriteAllTextAsync(Path.Combine(root, "config", "avalonia.json"), "{ invalid json");

        var service = CreateService(root);
        var result = await service.LoadOrBootstrapAsync();

        Assert.True(result.LoadedFromExistingConfig);
        Assert.Equal(UnifiedConfig.LatestSchemaVersion, service.CurrentConfig.SchemaVersion);
        Assert.Equal("Default", service.CurrentConfig.CurrentProfile);
        Assert.True(File.Exists(Path.Combine(root, "config", "avalonia.json")));
    }

    [Fact]
    public async Task CorruptedAvaloniaConfig_EmitsWarningLog()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));
        await File.WriteAllTextAsync(Path.Combine(root, "config", "avalonia.json"), "{ invalid json");

        var service = CreateService(root);
        await service.LoadOrBootstrapAsync();

        Assert.Contains(
            service.LogService.Snapshot,
            log => string.Equals(log.Level, "WARN", StringComparison.Ordinal) &&
                   log.Message.Contains("ConfigRepair.DeserializeException", StringComparison.Ordinal));
    }

    [Fact]
    public async Task NullAvaloniaConfig_RebuildsDefaults_AndEmitsParseNullWarning()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));
        await File.WriteAllTextAsync(Path.Combine(root, "config", "avalonia.json"), "null");

        var service = CreateService(root);
        var result = await service.LoadOrBootstrapAsync();

        Assert.True(result.LoadedFromExistingConfig);
        Assert.Equal(UnifiedConfig.LatestSchemaVersion, service.CurrentConfig.SchemaVersion);
        Assert.Equal("Default", service.CurrentConfig.CurrentProfile);
        Assert.True(File.Exists(Path.Combine(root, "config", "avalonia.json")));
        Assert.Contains(
            service.LogService.Snapshot,
            log => string.Equals(log.Level, "WARN", StringComparison.Ordinal)
                   && log.Message.Contains("ConfigRepair.DeserializeNull", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OutdatedSchema_LoadMigratesAndCreatesSanitizedBackup()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));
        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "avalonia.json"),
            """
            {
              "SchemaVersion": 1,
              "CurrentProfile": "Default",
              "Profiles": {
                "Default": { "Values": {}, "TaskQueue": [] }
              },
              "GlobalValues": {},
              "Migration": { "ImportedBy": "test" }
            }
            """);

        var service = CreateService(root);
        var result = await service.LoadOrBootstrapAsync();

        Assert.Equal(UnifiedConfig.LatestSchemaVersion, result.Config.SchemaVersion);
        Assert.Null(result.SchemaMigrationNotice);
        Assert.DoesNotContain(result.ValidationIssues, issue =>
            string.Equals(issue.Scope, "ConfigMigration", StringComparison.Ordinal)
            && string.Equals(issue.Code, "SchemaOutdated", StringComparison.Ordinal));
        Assert.True(Directory.EnumerateFiles(Path.Combine(root, "config"), "avalonia.json.schema-v1.bak.*").Any());
    }

    [Fact]
    public async Task OutdatedSchema_SaveCreatesSchemaBackup()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));
        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "avalonia.json"),
            """
            {
              "SchemaVersion": 1,
              "CurrentProfile": "Default",
              "Profiles": {
                "Default": { "Values": {}, "TaskQueue": [] }
              },
              "GlobalValues": {},
              "Migration": { "ImportedBy": "test" }
            }
            """);

        var service = CreateService(root);
        await service.LoadOrBootstrapAsync();
        await service.SaveAsync();

        var backupExists = Directory
            .EnumerateFiles(Path.Combine(root, "config"), "avalonia.json.schema-v1.bak.*")
            .Any();
        Assert.True(backupExists);
        Assert.Equal(UnifiedConfig.LatestSchemaVersion, service.CurrentConfig.SchemaVersion);
    }

    [Fact]
    public async Task LoadOrBootstrapAsync_ShouldSyncValidationIssues_WithServiceState()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));

        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "avalonia.json"),
            """
            {
              "SchemaVersion": 2,
              "CurrentProfile": "Default",
              "Profiles": {
                "Default": {
                  "Values": {},
                  "TaskQueue": [
                    {
                      "Type": "Recruit",
                      "Name": "Recruit",
                      "IsEnabled": true,
                      "Params": {
                        "times": 4
                      }
                    }
                  ]
                }
              },
              "GlobalValues": {},
              "Migration": {}
            }
            """);

        var service = CreateService(root);
        var load = await service.LoadOrBootstrapAsync();

        Assert.NotEmpty(load.ValidationIssues);
        Assert.Equal(service.CurrentValidationIssues.Count, load.ValidationIssues.Count);
        Assert.Equal(service.HasBlockingValidationIssues, load.HasBlockingValidationIssues);
        Assert.True(service.HasBlockingValidationIssues);
    }

    [Fact]
    public async Task LoadOrBootstrapAsync_CurrentProfileMissing_ShouldRepairSelectionAndPersist()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));
        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "avalonia.json"),
            """
            {
              "SchemaVersion": 3,
              "CurrentProfile": "Default",
              "Profiles": {
                "Alt": { "Values": { "ConnectAddress": "10.0.0.1:5555" }, "TaskQueue": [] }
              },
              "GlobalValues": {},
              "Migration": {}
            }
            """);

        var service = CreateService(root);
        var load = await service.LoadOrBootstrapAsync();

        Assert.DoesNotContain(load.ValidationIssues, i => i.Blocking);
        Assert.False(load.HasBlockingValidationIssues);
        Assert.False(service.HasBlockingValidationIssues);
        Assert.Equal("Alt", service.CurrentConfig.CurrentProfile);
        Assert.Equal(
            "10.0.0.1:5555",
            service.CurrentConfig.Profiles["Alt"].Values["ConnectAddress"]?.GetValue<string>());

        using var persisted = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(root, "config", "avalonia.json")));
        Assert.Equal("Alt", persisted.RootElement.GetProperty("CurrentProfile").GetString());
        Assert.True(persisted.RootElement.GetProperty("Profiles").TryGetProperty("Alt", out _));
    }

    [Fact]
    public async Task LoadOrBootstrapAsync_ProfilesNull_ShouldRecreateDefaultProfileAndPersist()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));
        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "avalonia.json"),
            """
            {
              "SchemaVersion": 3,
              "CurrentProfile": "Default",
              "Profiles": null,
              "GlobalValues": {},
              "Migration": {}
            }
            """);

        var service = CreateService(root);
        var load = await service.LoadOrBootstrapAsync();

        Assert.True(load.LoadedFromExistingConfig);
        Assert.False(load.HasBlockingValidationIssues);
        Assert.False(service.HasBlockingValidationIssues);
        Assert.Equal("Default", service.CurrentConfig.CurrentProfile);
        Assert.True(service.CurrentConfig.Profiles.ContainsKey("Default"));
        Assert.True(service.TryGetCurrentProfile(out _));

        using var persisted = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(root, "config", "avalonia.json")));
        Assert.Equal("Default", persisted.RootElement.GetProperty("CurrentProfile").GetString());
        Assert.True(persisted.RootElement.GetProperty("Profiles").TryGetProperty("Default", out _));
    }

    [Fact]
    public async Task LoadOrBootstrapAsync_ProfilesEmpty_ShouldRecreateDefaultProfileAndPersist()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));
        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "avalonia.json"),
            """
            {
              "SchemaVersion": 3,
              "CurrentProfile": "Default",
              "Profiles": {},
              "GlobalValues": {},
              "Migration": {}
            }
            """);

        var service = CreateService(root);
        var load = await service.LoadOrBootstrapAsync();

        Assert.True(load.LoadedFromExistingConfig);
        Assert.False(load.HasBlockingValidationIssues);
        Assert.False(service.HasBlockingValidationIssues);
        Assert.Equal("Default", service.CurrentConfig.CurrentProfile);
        Assert.True(service.CurrentConfig.Profiles.ContainsKey("Default"));
        Assert.True(service.TryGetCurrentProfile(out _));

        using var persisted = JsonDocument.Parse(
            await File.ReadAllTextAsync(Path.Combine(root, "config", "avalonia.json")));
        Assert.Equal("Default", persisted.RootElement.GetProperty("CurrentProfile").GetString());
        Assert.True(persisted.RootElement.GetProperty("Profiles").TryGetProperty("Default", out _));
    }

    [Fact]
    public async Task SaveAsync_ShouldRefreshValidationStateAndBlockingFlag()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));

        var service = CreateService(root);
        await service.LoadOrBootstrapAsync();
        Assert.False(service.HasBlockingValidationIssues);

        service.CurrentConfig.Profiles.Clear();
        await service.SaveAsync();

        Assert.True(service.HasBlockingValidationIssues);
        Assert.Contains(service.CurrentValidationIssues, issue => issue.Code == "ProfileMissing");
    }

    [Fact]
    public async Task SaveAsync_UnchangedLoadedConfig_ShouldNotWriteOrRaiseConfigChanged()
    {
        var root = CreateTempRoot();
        var store = new CountingConfigStore(root, new UnifiedConfig());
        var service = CreateService(root, store);
        await service.LoadOrBootstrapAsync();

        var configChangedCount = 0;
        service.ConfigChanged += _ => configChangedCount += 1;

        await service.SaveAsync();

        Assert.Equal(0, store.SaveCount);
        Assert.Equal(0, configChangedCount);
        Assert.DoesNotContain(
            service.LogService.Snapshot,
            log => string.Equals(log.Message, "Saved config/avalonia.json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SaveAsync_ChangedLoadedConfig_ShouldWriteAndRaiseConfigChanged()
    {
        var root = CreateTempRoot();
        var store = new CountingConfigStore(root, new UnifiedConfig());
        var service = CreateService(root, store);
        await service.LoadOrBootstrapAsync();

        var configChangedCount = 0;
        service.ConfigChanged += _ => configChangedCount += 1;
        service.CurrentConfig.GlobalValues["GUI.Localization"] = JsonValue.Create("en-us");

        await service.SaveAsync();

        Assert.Equal(1, store.SaveCount);
        Assert.Equal(1, configChangedCount);
    }

    [Fact]
    public async Task SaveAsync_TokenCancelledAfterDurableWrite_ShouldCompleteAndKeepSavedValues()
    {
        var root = CreateTempRoot();
        var store = new PostSaveCallbackConfigStore(root, new UnifiedConfig());
        var service = CreateService(root, store);
        await service.LoadOrBootstrapAsync();

        var configDirectory = Path.Combine(root, "config");
        Directory.CreateDirectory(configDirectory);
        var backupPath = Path.Combine(configDirectory, "avalonia.json.bak.20260101000000");
        await File.WriteAllTextAsync(backupPath, "{}");

        var configChangedCount = 0;
        service.ConfigChanged += _ => configChangedCount += 1;
        service.CurrentConfig.GlobalValues["GUI.Localization"] = JsonValue.Create("en-us");

        using var cts = new CancellationTokenSource();
        store.AfterSave = cts.Cancel;

        // Cancellation after the durable write must not surface as a save failure:
        // callers roll back live edits on any error and would diverge from the file.
        await service.SaveAsync(cts.Token);

        Assert.Equal(1, store.SaveCount);
        Assert.Equal(1, configChangedCount);
        Assert.Equal("en-us", service.CurrentConfig.GlobalValues["GUI.Localization"]?.GetValue<string>());
        Assert.Equal("{}", (await File.ReadAllTextAsync(backupPath)).Trim());
    }

    [Fact]
    public async Task SaveAsync_TokenCancelledBeforeDurableWrite_ShouldThrowAndNotPersist()
    {
        var root = CreateTempRoot();
        var store = new CountingConfigStore(root, new UnifiedConfig());
        var service = CreateService(root, store);
        await service.LoadOrBootstrapAsync();

        service.CurrentConfig.GlobalValues["GUI.Localization"] = JsonValue.Create("en-us");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.SaveAsync(cts.Token));

        Assert.Equal(0, store.SaveCount);
        Assert.Equal("en-us", service.CurrentConfig.GlobalValues["GUI.Localization"]?.GetValue<string>());
    }

    private async Task SaveAsync_ReclamationArchiveClearStoreChange_ShouldPersist()
    {
        var root = CreateTempRoot();
        var loadedConfig = new UnifiedConfig();
        var task = new UnifiedTaskItem
        {
            Type = TaskModuleTypes.Reclamation,
            Name = "Reclamation",
            Params = new JsonObject
            {
                ["mode"] = 1,
                ["clear_store"] = false,
            },
        };
        var compiled = TaskParamCompiler.CompileTask(task, loadedConfig.Profiles["Default"], loadedConfig, strict: true);
        task.Type = compiled.NormalizedType;
        task.Params = compiled.Params.DeepClone() as JsonObject ?? [];
        loadedConfig.Profiles["Default"].TaskQueue.Add(task);

        var store = new CountingConfigStore(root, loadedConfig);
        var service = CreateService(root, store);
        await service.LoadOrBootstrapAsync();

        var configChangedCount = 0;
        service.ConfigChanged += _ => configChangedCount += 1;
        service.CurrentConfig.Profiles["Default"].TaskQueue[0].Params["clear_store"] = true;

        await service.SaveAsync();

        Assert.Equal(1, store.SaveCount);
        Assert.Equal(1, configChangedCount);
        Assert.True(service.CurrentConfig.Profiles["Default"].TaskQueue[0].Params["clear_store"]?.GetValue<bool>());
    }

    [Fact]
    public async Task SaveAsync_ThrowingConfigChangedSubscriber_ShouldNotAffectSaveOutcomeOrOtherSubscribers()
    {
        var root = CreateTempRoot();
        var store = new CountingConfigStore(root, new UnifiedConfig());
        var service = CreateService(root, store);
        await service.LoadOrBootstrapAsync();

        var healthySubscriberCount = 0;
        service.ConfigChanged += _ => throw new InvalidOperationException("Subscriber failure during save notification.");
        service.ConfigChanged += _ => healthySubscriberCount += 1;
        service.CurrentConfig.GlobalValues["GUI.Localization"] = JsonValue.Create("en-us");

        await service.SaveAsync();

        Assert.Equal(1, store.SaveCount);
        Assert.Equal(1, healthySubscriberCount);
        Assert.Equal("en-us", service.CurrentConfig.GlobalValues["GUI.Localization"]?.GetValue<string>());
        Assert.Contains(
            service.LogService.Snapshot,
            log => log.Message.Contains("ConfigChanged subscriber", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoadOrBootstrapAsync_ThrowingConfigChangedSubscriber_ShouldNotAffectLoadOrOtherSubscribers()
    {
        var root = CreateTempRoot();
        var store = new CountingConfigStore(root, new UnifiedConfig());
        var service = CreateService(root, store);

        var healthySubscriberCount = 0;
        service.ConfigChanged += _ => throw new InvalidOperationException("Subscriber failure during load notification.");
        service.ConfigChanged += _ => healthySubscriberCount += 1;

        var result = await service.LoadOrBootstrapAsync();

        Assert.True(result.LoadedFromExistingConfig);
        Assert.Equal(1, healthySubscriberCount);
        Assert.Contains(
            service.LogService.Snapshot,
            log => log.Message.Contains("ConfigChanged subscriber", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LoadOrBootstrapAsync_ShouldClearPersistedRecruitUseExpedited()
    {
        var root = CreateTempRoot();
        var loadedConfig = new UnifiedConfig();
        loadedConfig.Profiles["Default"].TaskQueue.Add(new UnifiedTaskItem
        {
            Type = TaskModuleTypes.Recruit,
            Name = "Recruit",
            Params = new JsonObject
            {
                ["times"] = 2,
                ["expedite"] = true,
                ["expedite_times"] = 2,
            },
        });

        var store = new CountingConfigStore(root, loadedConfig);
        var service = CreateService(root, store);

        var load = await service.LoadOrBootstrapAsync();

        Assert.True(load.LoadedFromExistingConfig);
        Assert.Equal(1, store.SaveCount);
        var task = Assert.Single(service.CurrentConfig.Profiles["Default"].TaskQueue);
        Assert.False(task.Params.ContainsKey("expedite"));
        Assert.False(task.Params.ContainsKey("expedite_times"));
        var recruit = TaskParamCompiler.ReadRecruit(task, strict: true).Dto;
        Assert.False(recruit.UseExpedited);
        Assert.Equal(2, recruit.Times);
    }

    [Fact]
    public async Task LoadOrBootstrapAsync_PartialSchemaV2InfrastParams_ShouldPersistWpfDefaults()
    {
        var root = CreateTempRoot();
        var loadedConfig = new UnifiedConfig();
        loadedConfig.Profiles["Default"].TaskQueue.Add(new UnifiedTaskItem
        {
            Type = TaskModuleTypes.Infrast,
            Name = "Infrast",
            Params = new JsonObject { ["mode"] = 0 },
        });

        var store = new CountingConfigStore(root, loadedConfig);
        var service = CreateService(root, store);

        var load = await service.LoadOrBootstrapAsync();

        Assert.True(load.LoadedFromExistingConfig);
        Assert.Equal(1, store.SaveCount);
        var parameters = Assert.Single(service.CurrentConfig.Profiles["Default"].TaskQueue).Params;
        Assert.Equal(
            ["Mfg", "Trade", "Control", "Power", "Reception", "Office", "Dorm", "Processing", "Training"],
            Assert.IsType<JsonArray>(parameters["facility"]).Select(node => node!.GetValue<string>()));
        Assert.Equal("Money", parameters["drones"]?.GetValue<string>());
        Assert.False(parameters["continue_training"]?.GetValue<bool>());
        Assert.Equal(0.3, parameters["threshold"]?.GetValue<double>());
        Assert.True(parameters["dorm_notstationed_enabled"]?.GetValue<bool>());
        Assert.True(parameters["dorm_trust_enabled"]?.GetValue<bool>());
        Assert.True(parameters["replenish"]?.GetValue<bool>());
        Assert.True(parameters["reception_message_board"]?.GetValue<bool>());
        Assert.True(parameters["reception_clue_exchange"]?.GetValue<bool>());
        Assert.True(parameters["reception_send_clue"]?.GetValue<bool>());
    }

    [Fact]
    public async Task LoadOrBootstrap_AutoImportFailure_WritesDebugReport()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));
        await File.WriteAllTextAsync(Path.Combine(root, "config", "gui.new.json"), "{ invalid json");

        var service = CreateService(root);
        var result = await service.LoadOrBootstrapAsync();

        Assert.False(result.LoadedFromExistingConfig);
        var report = Assert.IsType<ImportReport>(result.ImportReport);
        Assert.False(report.Success);
        Assert.True(report.AppliedConfig);
        Assert.NotEmpty(report.Errors);
        var reportPath = Path.Combine(root, "debug", "config-import-report.json");
        Assert.True(File.Exists(reportPath));
        var reportJson = await File.ReadAllTextAsync(reportPath);
        Assert.Contains("errors", reportJson, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task LoadOrBootstrap_AutoImportFailure_DoesNotCrash_AndCanSave()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));
        await File.WriteAllTextAsync(Path.Combine(root, "config", "gui.new.json"), "{ invalid json");

        var service = CreateService(root);
        var result = await service.LoadOrBootstrapAsync();

        Assert.NotNull(result.ImportReport);
        Assert.False(result.ImportReport!.Success);
        Assert.True(result.ImportReport.AppliedConfig);
        await service.SaveAsync();
        Assert.True(File.Exists(Path.Combine(root, "config", "avalonia.json")));
    }

    [Fact]
    public async Task LoadOrBootstrap_WhenNoLegacyFileExists_ShouldCreateDefaultConfigAndReportIt()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));

        var service = CreateService(root);
        var result = await service.LoadOrBootstrapAsync();

        var report = Assert.IsType<ImportReport>(result.ImportReport);
        Assert.True(report.AppliedConfig);
        Assert.True(report.CreatedDefaultConfig);
        Assert.Contains("gui.new.json", report.MissingFiles, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("gui.json", report.MissingFiles, StringComparer.OrdinalIgnoreCase);
        Assert.Contains(
            service.LogService.Snapshot,
            log => log.Message.Contains("已自动创建默认配置 avalonia.json", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ImportLegacy_Auto_WhenOnlyGuiExists_ShouldImportAndProduceCorrectReportFlags()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));
        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "ConnectAddress": "10.0.0.7:5555",
                  "TouchMode": "maatouch"
                }
              },
              "Global": {
                "GUI.Localization": "en-us"
              }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.Auto, manualImport: false);

        Assert.True(report.Success);
        Assert.True(report.ImportedGui);
        Assert.False(report.ImportedGuiNew);
        Assert.Empty(report.Errors);
        var profile = service.CurrentConfig.Profiles["Default"];
        Assert.Equal("10.0.0.7:5555", profile.Values["ConnectAddress"]?.GetValue<string>());
        Assert.Equal("maatouch", profile.Values["TouchMode"]?.GetValue<string>());
    }

    [Fact]
    public async Task ImportLegacy_GuiPostActionsLegacyValue_RemainsReadable_AndMigratesOnLoad()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));
        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "ConnectAddress": "10.0.0.7:5555"
                }
              },
              "Global": {
                "MainFunction.PostActions": "136"
              }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.GuiOnly, manualImport: false);

        Assert.True(report.Success);
        var migratedProfile = service.CurrentConfig.Profiles["Default"];
        var migratedStructured = PostActionConfig.FromJson(migratedProfile.Values["TaskQueue.PostAction"]);
        Assert.True(migratedProfile.Values.ContainsKey("TaskQueue.PostAction"));
        Assert.True(migratedStructured.ExitSelf);
        Assert.True(migratedStructured.Sleep);
        Assert.False(migratedProfile.Values.ContainsKey("MainFunction.PostActions"));
        Assert.False(service.CurrentConfig.GlobalValues.ContainsKey("MainFunction.PostActions"));
        var diagnostics = new UiDiagnosticsService(root, service.LogService);
        var feature = new PostActionFeatureService(service, diagnostics, new NoOpPostActionExecutorService());

        var load = await feature.LoadAsync();

        Assert.True(load.Success);
        Assert.NotNull(load.Value);
        Assert.True(load.Value!.ExitSelf);
        Assert.True(load.Value.Sleep);
    }

    [Theory]
    [InlineData("1", true, false, false, false)]
    [InlineData("2", false, true, false, false)]
    [InlineData("4", false, false, true, false)]
    [InlineData("16", false, false, false, true)]
    public async Task ImportLegacy_GuiPostActionsLegacyBitmask_MapsWpfPostActionsBits(
        string legacyValue,
        bool expectedExitArknights,
        bool expectedBackToAndroidHome,
        bool expectedExitEmulator,
        bool expectedIfNoOtherMaa)
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));
        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.json"),
            $$"""
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "ConnectAddress": "10.0.0.7:5555"
                }
              },
              "Global": {
                "MainFunction.PostActions": "{{legacyValue}}"
              }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.GuiOnly, manualImport: false);

        Assert.True(report.Success);
        var migratedProfile = service.CurrentConfig.Profiles["Default"];
        Assert.True(migratedProfile.Values.ContainsKey("TaskQueue.PostAction"));
        var migrated = PostActionConfig.FromJson(migratedProfile.Values["TaskQueue.PostAction"]);
        Assert.Equal(expectedExitArknights, migrated.ExitArknights);
        Assert.Equal(expectedBackToAndroidHome, migrated.BackToAndroidHome);
        Assert.Equal(expectedExitEmulator, migrated.ExitEmulator);
        Assert.Equal(expectedIfNoOtherMaa, migrated.IfNoOtherMaa);
        Assert.False(migrated.ExitSelf);
        Assert.False(migrated.Hibernate);
        Assert.False(migrated.Shutdown);
        Assert.False(migrated.Sleep);

        var diagnostics = new UiDiagnosticsService(root, service.LogService);
        var feature = new PostActionFeatureService(service, diagnostics, new NoOpPostActionExecutorService());
        var load = await feature.LoadAsync();
        Assert.True(load.Success);
        Assert.Equal(expectedExitEmulator, load.Value!.ExitEmulator);
        Assert.Equal(expectedIfNoOtherMaa, load.Value.IfNoOtherMaa);
    }

    [Fact]
    public async Task GuiNewImport_DevV2PostActions_ShouldMapLockScreenAndExitPcClientBits_AndKeepWarnOnSkipOnError()
    {
        // WPF dev-v2 d58cb0825b adds LockScreen = 1 << 9 and 3b4f06dd86 adds ExitPCClient = 1 << 10
        // to Gui.PostActions. Bit 1<<8 (SkipOnError, P1-03 cda1c3b07f) is still unmapped and must
        // keep reporting a warning instead of being dropped silently.
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));
        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.new.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "TaskQueue": [],
                  "Gui": {
                    "PostActions": 1792
                  }
                }
              }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.GuiNewOnly, manualImport: false);

        Assert.True(report.Success);
        var profile = service.CurrentConfig.Profiles["Default"];
        Assert.True(profile.Values.ContainsKey("TaskQueue.PostAction"));
        Assert.False(profile.Values.ContainsKey("MainFunction.PostActions"));
        var migrated = PostActionConfig.FromJson(profile.Values["TaskQueue.PostAction"]);
        Assert.True(migrated.LockScreen);
        Assert.True(migrated.ExitPCClient);
        Assert.False(migrated.ExitArknights);
        Assert.False(migrated.ExitEmulator);
        Assert.False(migrated.ExitSelf);
        Assert.Contains(
            report.Warnings,
            warning => warning.Contains("unsupported flags 0x100", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ImportLegacy_GuiPostActionsLegacyBitmask_KeepsOldBits_WhileAddingP1_04Bits()
    {
        // Old v6.3.7 bits must keep their meaning: 5 = ExitArknights | ExitEmulator, plus
        // 1 << 9 (LockScreen) and 1 << 10 (ExitPCClient).
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));
        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "ConnectAddress": "10.0.0.7:5555"
                }
              },
              "Global": {
                "MainFunction.PostActions": "1541"
              }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.GuiOnly, manualImport: false);

        Assert.True(report.Success);
        var profile = service.CurrentConfig.Profiles["Default"];
        var migrated = PostActionConfig.FromJson(profile.Values["TaskQueue.PostAction"]);
        Assert.True(migrated.ExitArknights);
        Assert.True(migrated.ExitEmulator);
        Assert.True(migrated.LockScreen);
        Assert.True(migrated.ExitPCClient);
        Assert.False(migrated.BackToAndroidHome);
        Assert.False(migrated.ExitSelf);
        Assert.False(profile.Values.ContainsKey("MainFunction.PostActions"));
    }

    [Fact]
    public async Task ImportLegacy_GuiActionAfterCompletedWithoutPostActions_RemainsReadable_AndMigratesOnLoad()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));
        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "MainFunction.ActionAfterCompleted": "StopGame"
                }
              }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.GuiOnly, manualImport: false);

        Assert.True(report.Success);
        var migratedProfile = service.CurrentConfig.Profiles["Default"];
        var migratedStructured = PostActionConfig.FromJson(migratedProfile.Values["TaskQueue.PostAction"]);
        Assert.True(migratedProfile.Values.ContainsKey("TaskQueue.PostAction"));
        Assert.True(migratedStructured.ExitArknights);
        Assert.False(migratedProfile.Values.ContainsKey("MainFunction.ActionAfterCompleted"));
        var diagnostics = new UiDiagnosticsService(root, service.LogService);
        var feature = new PostActionFeatureService(service, diagnostics, new NoOpPostActionExecutorService());

        var load = await feature.LoadAsync();

        Assert.True(load.Success);
        Assert.NotNull(load.Value);
        Assert.True(load.Value!.ExitArknights);
    }

    [Fact]
    public async Task ImportLegacy_GuiPostActionsZero_ShouldFallBackToActionAfterCompletedOnLoad()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));
        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "MainFunction.ActionAfterCompleted": "StopGame",
                  "MainFunction.PostActions": "0"
                }
              }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.GuiOnly, manualImport: false);

        Assert.True(report.Success);
        var migratedProfile = service.CurrentConfig.Profiles["Default"];
        var migratedStructured = PostActionConfig.FromJson(migratedProfile.Values["TaskQueue.PostAction"]);
        Assert.True(migratedProfile.Values.ContainsKey("TaskQueue.PostAction"));
        Assert.True(migratedStructured.ExitArknights);
        Assert.False(migratedProfile.Values.ContainsKey("MainFunction.PostActions"));
        Assert.False(migratedProfile.Values.ContainsKey("MainFunction.ActionAfterCompleted"));
        var diagnostics = new UiDiagnosticsService(root, service.LogService);
        var feature = new PostActionFeatureService(service, diagnostics, new NoOpPostActionExecutorService());

        var load = await feature.LoadAsync();

        Assert.True(load.Success);
        Assert.NotNull(load.Value);
        Assert.True(load.Value!.ExitArknights);
    }

    [Fact]
    public async Task ImportLegacy_GuiActionAfterCompletedCompositeValue_ShouldMapToStructuredPostActions()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));
        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "MainFunction.ActionAfterCompleted": "ExitEmulatorAndSelf"
                }
              }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.GuiOnly, manualImport: false);

        Assert.True(report.Success);
        var migratedProfile = service.CurrentConfig.Profiles["Default"];
        var migratedStructured = PostActionConfig.FromJson(migratedProfile.Values["TaskQueue.PostAction"]);
        Assert.True(migratedProfile.Values.ContainsKey("TaskQueue.PostAction"));
        Assert.True(migratedStructured.ExitEmulator);
        Assert.True(migratedStructured.ExitSelf);
        Assert.False(migratedProfile.Values.ContainsKey("MainFunction.ActionAfterCompleted"));
        var diagnostics = new UiDiagnosticsService(root, service.LogService);
        var feature = new PostActionFeatureService(service, diagnostics, new NoOpPostActionExecutorService());

        var load = await feature.LoadAsync();

        Assert.True(load.Success);
        Assert.NotNull(load.Value);
        Assert.True(load.Value!.ExitEmulator);
        Assert.True(load.Value.ExitSelf);
        Assert.False(load.Value.Hibernate);
        var persisted = PostActionConfig.FromJson(migratedProfile.Values["TaskQueue.PostAction"]);
        Assert.True(persisted.ExitEmulator);
        Assert.True(persisted.ExitSelf);
    }

    [Fact]
    public async Task ImportLegacy_Auto_GuiNewValidAndGuiCorrupted_ShouldKeepImportResultSaveable_AndReportError()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));
        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.new.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "TaskQueue": [
                    { "$type": "FightTask", "Name": "Fight", "IsEnable": true }
                  ],
                  "ConnectAddress": "127.0.0.1:6000"
                }
              }
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(root, "config", "gui.json"), "{ invalid json");

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.Auto, manualImport: false);

        Assert.False(report.Success);
        Assert.NotEmpty(report.Errors);
        var profile = service.CurrentConfig.Profiles["Default"];
        Assert.Single(profile.TaskQueue);
        Assert.Equal("127.0.0.1:6000", profile.Values["ConnectAddress"]?.GetValue<string>());
        await service.SaveAsync();
        Assert.True(File.Exists(Path.Combine(root, "config", "avalonia.json")));
    }

    [Fact]
    public async Task GuiNewImport_TaskQueueWithNonObjectEntries_ShouldSkipInvalidRowsAndWarnWithoutBlockingImport()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));
        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.new.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "TaskQueue": [
                    1,
                    "invalid",
                    { "$type": "FightTask", "Name": "Fight", "IsEnable": true }
                  ]
                }
              }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.GuiNewOnly, manualImport: false);

        Assert.True(report.Success);
        Assert.Empty(report.Errors);
        Assert.Contains(report.Warnings, warning => warning.Contains("non-object entry", StringComparison.OrdinalIgnoreCase));
        var profile = service.CurrentConfig.Profiles["Default"];
        Assert.Single(profile.TaskQueue);
    }

    [Fact]
    public async Task ManualImport_CreatesBackupAndReport()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));

        await File.WriteAllTextAsync(Path.Combine(root, "config", "avalonia.json"), "{\"SchemaVersion\":1,\"CurrentProfile\":\"Default\",\"Profiles\":{\"Default\":{\"Values\":{},\"TaskQueue\":[]}},\"GlobalValues\":{},\"Migration\":{}}");
        await File.WriteAllTextAsync(Path.Combine(root, "config", "gui.json"), "{\"Current\":\"Default\",\"Configurations\":{\"Default\":{\"TouchMode\":\"maatouch\"}},\"Global\":{}}");

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.GuiOnly, manualImport: true);

        Assert.True(report.Success);
        Assert.True(report.AppliedConfig);
        var bakExists = Directory.EnumerateFiles(Path.Combine(root, "config"), "avalonia.json.bak.*").Any();
        Assert.True(bakExists);
        Assert.Equal(UnifiedConfig.LatestSchemaVersion, service.CurrentConfig.SchemaVersion);
        Assert.True(File.Exists(Path.Combine(root, "debug", "config-import-report.json")));
    }

    [Fact]
    public async Task ManualImport_SingleGuiOnly_WithForceImport_ShouldStartFromDefaults()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));
        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "TouchMode": "maatouch"
                }
              },
              "Global": {}
            }
            """);

        var service = CreateService(root);
        service.CurrentConfig.GlobalValues["Legacy.Leftover"] = JsonValue.Create("should-be-removed");
        await service.SaveAsync();

        var report = await service.ImportLegacyAsync(
            new LegacyImportRequest(
                LegacyConfigSnapshot.FromPaths(null, Path.Combine(root, "config", "gui.json")),
                ImportSource.GuiOnly,
                ManualImport: true,
                AllowPartialImport: true));

        Assert.True(report.AppliedConfig);
        Assert.True(report.Success);
        Assert.Contains("gui.new.json", report.MissingFiles, StringComparer.OrdinalIgnoreCase);
        Assert.False(service.CurrentConfig.GlobalValues.ContainsKey("Legacy.Leftover"));
        Assert.Equal("maatouch", service.CurrentConfig.Profiles["Default"].Values["TouchMode"]?.GetValue<string>());
    }

    [Fact]
    public async Task ManualImport_DamagedGuiJson_WithAllowPartialFalse_ShouldNotApplyConfig()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));
        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.new.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "ConnectAddress": "10.1.2.3:5555"
                }
              }
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(root, "config", "gui.json"), "{ invalid json");
        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "avalonia.json"),
            """
            {
              "SchemaVersion": 2,
              "CurrentProfile": "Default",
              "Profiles": {
                "Default": { "Values": { "ConnectAddress": "1.1.1.1:5555" }, "TaskQueue": [] }
              },
              "GlobalValues": {},
              "Migration": {}
            }
            """);

        var service = CreateService(root);
        await service.LoadOrBootstrapAsync();

        var report = await service.ImportLegacyAsync(
            new LegacyImportRequest(
                LegacyConfigSnapshot.FromPaths(
                    Path.Combine(root, "config", "gui.new.json"),
                    Path.Combine(root, "config", "gui.json")),
                ImportSource.Auto,
                ManualImport: true,
                AllowPartialImport: false));

        Assert.False(report.AppliedConfig);
        Assert.False(report.Success);
        Assert.Contains("gui.new.json", report.ImportedFiles, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("gui.json", report.DamagedFiles, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("1.1.1.1:5555", service.CurrentConfig.Profiles["Default"].Values["ConnectAddress"]?.GetValue<string>());
    }

    [Fact]
    public async Task ManualImport_DamagedGuiJson_WithAllowPartialTrue_ShouldApplyUsableContent()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));
        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.new.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "ConnectAddress": "10.5.6.7:5555"
                }
              }
            }
            """);
        await File.WriteAllTextAsync(Path.Combine(root, "config", "gui.json"), "{ invalid json");

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(
            new LegacyImportRequest(
                LegacyConfigSnapshot.FromPaths(
                    Path.Combine(root, "config", "gui.new.json"),
                    Path.Combine(root, "config", "gui.json")),
                ImportSource.Auto,
                ManualImport: true,
                AllowPartialImport: true));

        Assert.True(report.AppliedConfig);
        Assert.False(report.Success);
        Assert.Contains("gui.json", report.DamagedFiles, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("10.5.6.7:5555", service.CurrentConfig.Profiles["Default"].Values["ConnectAddress"]?.GetValue<string>());
    }

    [Fact]
    public async Task ManualImport_WhenNoUsableLegacyContentExists_ShouldNotOverwriteExistingConfig()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));
        await File.WriteAllTextAsync(Path.Combine(root, "config", "gui.json"), "{ invalid json");
        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "avalonia.json"),
            """
            {
              "SchemaVersion": 2,
              "CurrentProfile": "Default",
              "Profiles": {
                "Default": { "Values": { "TouchMode": "adb" }, "TaskQueue": [] }
              },
              "GlobalValues": {},
              "Migration": {}
            }
            """);

        var service = CreateService(root);
        await service.LoadOrBootstrapAsync();
        var before = await File.ReadAllTextAsync(Path.Combine(root, "config", "avalonia.json"));

        var report = await service.ImportLegacyAsync(
            new LegacyImportRequest(
                LegacyConfigSnapshot.FromPaths(null, Path.Combine(root, "config", "gui.json")),
                ImportSource.GuiOnly,
                ManualImport: true,
                AllowPartialImport: true));

        Assert.False(report.AppliedConfig);
        Assert.False(report.Success);
        var after = await File.ReadAllTextAsync(Path.Combine(root, "config", "avalonia.json"));
        Assert.Equal(before, after);
        Assert.Equal("adb", service.CurrentConfig.Profiles["Default"].Values["TouchMode"]?.GetValue<string>());
    }

    [Fact]
    public async Task UnsupportedLegacyTask_IsDisabledAndReported()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));

        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.new.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "TaskQueue": [
                    { "$type": "UnknownLegacyTask", "Name": "Unsupported", "IsEnable": true }
                  ]
                }
              }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.GuiNewOnly, manualImport: false);

        Assert.False(report.Success);
        var task = service.CurrentConfig.Profiles["Default"].TaskQueue.Single();
        Assert.False(task.IsEnabled);
        Assert.Equal("UnknownLegacy", task.Type);
        Assert.NotEmpty(report.Errors);
    }

    [Fact]
    public async Task GuiNewImport_CopilotTaskMarker_ShouldRemainDisabledRawAndReported()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));

        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.new.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "TaskQueue": [
                    { "$type": "CopilotTask", "Name": "Copilot", "IsEnable": true }
                  ]
                }
              }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.GuiNewOnly, manualImport: false);

        Assert.False(report.Success);
        Assert.Contains(report.Errors, error => error.Contains("Unsupported legacy task type `CopilotTask`", StringComparison.Ordinal));
        var task = Assert.Single(service.CurrentConfig.Profiles["Default"].TaskQueue);
        Assert.False(task.IsEnabled);
        Assert.Equal("Copilot", task.Type);
        Assert.NotNull(task.LegacyRawTask);
    }

    [Fact]
    public async Task CorruptedGuiFile_FallsBackToDefaultsWithErrorInReport()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));

        await File.WriteAllTextAsync(Path.Combine(root, "config", "gui.json"), "{invalid json");

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.GuiOnly, manualImport: false);

        Assert.False(report.Success);
        Assert.NotEmpty(report.Errors);
    }

    [Fact]
    public async Task AvaloniaJsonConfigStore_SaveAsync_ShouldSucceedWhileExistingConfigIsOpenForRead()
    {
        var root = CreateTempRoot();
        var configDirectory = Path.Combine(root, "config");
        Directory.CreateDirectory(configDirectory);
        var configPath = Path.Combine(configDirectory, "avalonia.json");
        await File.WriteAllTextAsync(
            configPath,
            """
            {
              "SchemaVersion": 2,
              "CurrentProfile": "Default",
              "Profiles": {
                "Default": { "Values": {}, "TaskQueue": [] }
              },
              "GlobalValues": {
                "GUI.Localization": "zh-cn"
              },
              "Migration": {}
            }
            """);

        var store = new AvaloniaJsonConfigStore(root);
        var config = new UnifiedConfig();
        config.GlobalValues["GUI.Localization"] = "en-us";

        await using var reader = new FileStream(
            configPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 1,
            FileOptions.None);

        await store.SaveAsync(config);

        var saved = await File.ReadAllTextAsync(configPath);
        Assert.Contains("\"GUI.Localization\": \"en-us\"", saved, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AutoImport_CaseInsensitiveRoots_PreservesGuiNewCurrentAndMovesInverseClearModeGlobal()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));
        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.new.json"),
            """
            {
              "current": "NewProfile",
              "configurations": {
                "NewProfile": { "GUI.InverseClearMode": "Inverse" }
              },
              "gui": { "DarkMode": "SyncWithOs" }
            }
            """);
        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.json"),
            """
            {
              "CURRENT": "OldProfile",
              "CONFIGURATIONS": { "OldProfile": { "TouchMode": "adb" } }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.Auto, manualImport: false);

        Assert.True(report.Success);
        Assert.Equal("NewProfile", service.CurrentConfig.CurrentProfile);
        Assert.Equal("Inverse", service.CurrentConfig.GlobalValues["GUI.InverseClearMode"]?.GetValue<string>());
        Assert.Equal("SyncWithOs", service.CurrentConfig.GlobalValues["Theme.Mode"]?.GetValue<string>());
        Assert.False(service.CurrentConfig.Profiles["NewProfile"].Values.ContainsKey("GUI.InverseClearMode"));
    }

    [Fact]
    public async Task GuiNewImport_NonCustomInfrast_PreservesFilenameAndPlanSelect()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));
        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.new.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "TaskQueue": [
                    {
                      "$type": "InfrastTask",
                      "Name": "Infrast",
                      "Mode": "Normal",
                      "Filename": "/tmp/infrast.json",
                      "PlanSelect": 2
                    }
                  ]
                }
              }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.GuiNewOnly, manualImport: false);
        var parameters = service.CurrentConfig.Profiles["Default"].TaskQueue.Single().Params;

        Assert.True(report.Success);
        Assert.Equal(0, parameters["mode"]?.GetValue<int>());
        Assert.Equal("/tmp/infrast.json", parameters["filename"]?.GetValue<string>());
        Assert.Equal(2, parameters["plan_index"]?.GetValue<int>());
    }

    private static UnifiedConfigurationService CreateService(string baseDirectory)
    {
        var store = new AvaloniaJsonConfigStore(baseDirectory);
        return CreateService(baseDirectory, store);
    }

    private static UnifiedConfigurationService CreateService(string baseDirectory, IUnifiedConfigStore store)
    {
        var log = new UiLogService();
        return new UnifiedConfigurationService(store, new GuiNewJsonConfigImporter(), new GuiJsonConfigImporter(), log, baseDirectory);
    }

    private static string CreateTempRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "maa-unified-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static string[] ReadEnabledCollectibleAwards(JsonNode? node)
    {
        var obj = Assert.IsType<JsonObject>(node);
        return obj
            .Where(prop => prop.Value?.GetValue<bool>() == true)
            .Select(prop => prop.Key)
            .ToArray();
    }

    private static string[] ReadStringArray(JsonArray array)
    {
        return array
            .Select(item => item?.GetValue<string>() ?? string.Empty)
            .ToArray();
    }

    private static UnifiedTaskItem AssertRaTask(IReadOnlyList<UnifiedTaskItem> tasks, string name)
    {
        var task = tasks.Single(task => task.Name == name);
        Assert.Equal(TaskModuleTypes.Reclamation, task.Type);
        Assert.Equal("RelaunchAnchor", task.Params["theme"]?.GetValue<string>());
        return task;
    }

    private sealed class PostSaveCallbackConfigStore : IUnifiedConfigStore
    {
        private UnifiedConfig? _config;

        public PostSaveCallbackConfigStore(string baseDirectory, UnifiedConfig? config)
        {
            ConfigPath = Path.Combine(baseDirectory, "config", "avalonia.json");
            _config = config;
        }

        public string ConfigPath { get; }

        public int SaveCount { get; private set; }

        public Action? AfterSave { get; set; }

        public bool Exists() => _config is not null;

        public Task<UnifiedConfig?> LoadAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_config);
        }

        public Task SaveAsync(UnifiedConfig config, CancellationToken cancellationToken = default)
        {
            SaveCount += 1;
            _config = config;
            AfterSave?.Invoke();
            return Task.CompletedTask;
        }

        public Task BackupAsync(string suffix, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }


    [Fact]
    public async Task GuiNewImport_RecruitPreserveEnabledWithoutLevel1NotChoose_KeepsStoredTagList()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));

        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.new.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "TaskQueue": [
                    {
                      "$type": "RecruitTask",
                      "Name": "Recruit",
                      "IsEnable": true,
                      "PreserveTagEnabled": true,
                      "PreserveTagList": ["支援机械", "高级资深干员"],
                      "Level3RecruitmentPermitReserveEnabled": true,
                      "Level3RecruitmentPermitReserve": 12,
                      "Level6Choose": true
                    }
                  ]
                }
              }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.GuiNewOnly, manualImport: false);

        Assert.True(report.Success);
        var task = service.CurrentConfig.Profiles["Default"].TaskQueue.Single(task => task.Type == TaskModuleTypes.Recruit);
        Assert.True(task.Params["_ui_preserve_tags_enabled"]?.GetValue<bool>());
        Assert.Equal(
            ["支援机械", "高级资深干员"],
            ReadStringArray((JsonArray)task.Params["preserve_tags"]!));
        Assert.Equal(12, task.Params["level3_recruitment_permit_reserve"]?.GetValue<int>());
        Assert.Contains(6, ((JsonArray)task.Params["select"]!).Select(item => item?.GetValue<int>()));
        Assert.Contains(6, ((JsonArray)task.Params["confirm"]!).Select(item => item?.GetValue<int>()));
        var recruitmentTime = Assert.IsType<JsonObject>(task.Params["recruitment_time"]);
        Assert.Equal(540, recruitmentTime["5"]?.GetValue<int>());
        Assert.Equal(540, recruitmentTime["6"]?.GetValue<int>());
        Assert.DoesNotContain("skip_robot", task.Params.Select(pair => pair.Key), StringComparer.Ordinal);
    }

    [Fact]
    public async Task GuiNewImport_RecruitDisabledPreserveTags_KeepsEmptyCoreList()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));

        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.new.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "TaskQueue": [
                    {
                      "$type": "RecruitTask",
                      "Name": "Recruit",
                      "IsEnable": true,
                      "PreserveTagEnabled": false,
                      "PreserveTagList": []
                    }
                  ]
                }
              }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.GuiNewOnly, manualImport: false);

        Assert.True(report.Success);
        var task = service.CurrentConfig.Profiles["Default"].TaskQueue.Single(task => task.Type == TaskModuleTypes.Recruit);
        Assert.False(task.Params["_ui_preserve_tags_enabled"]?.GetValue<bool>());
        Assert.Empty(ReadStringArray((JsonArray)task.Params["preserve_tags"]!));
        Assert.Equal(0, task.Params["level3_recruitment_permit_reserve"]?.GetValue<int>());
        Assert.DoesNotContain("skip_robot", task.Params.Select(pair => pair.Key), StringComparer.Ordinal);
    }

    [Fact]
    public async Task ExistingAvaloniaConfig_AchievementAutoCloseFalseAndMissingValue_ShouldRemainNonAutoClosing()
    {
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));

        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "avalonia.json"),
            """
            {
              "SchemaVersion": 2,
              "CurrentProfile": "MissingSetting",
              "Profiles": {
                "MissingSetting": {
                  "Values": {},
                  "TaskQueue": []
                },
                "Default": {
                  "Values": {
                    "Achievement.PopupDisabled": "False",
                    "Achievement.PopupAutoClose": "False"
                  },
                  "TaskQueue": []
                },
                "Alt": {
                  "Values": {
                    "Achievement.PopupDisabled": "False",
                    "Achievement.PopupAutoClose": "False"
                  },
                  "TaskQueue": []
                }
              },
              "GlobalValues": {},
              "Migration": {}
            }
            """);

        var service = CreateService(root);
        var result = await service.LoadOrBootstrapAsync();

        Assert.True(result.LoadedFromExistingConfig);
        Assert.False(service.CurrentConfig.Profiles["MissingSetting"].Values.ContainsKey("Achievement.PopupAutoClose"));
        Assert.Equal("False", service.CurrentConfig.Profiles["Default"].Values["Achievement.PopupAutoClose"]?.GetValue<string>());
        Assert.Equal("False", service.CurrentConfig.Profiles["Alt"].Values["Achievement.PopupAutoClose"]?.GetValue<string>());

        var policy = await new AchievementFeatureService(service).LoadPolicyAsync();
        Assert.True(policy.Success);
        Assert.False(policy.Value!.PopupAutoClose);

        var persisted = Assert.IsType<JsonObject>(
            JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, "config", "avalonia.json"))));
        Assert.Equal("False", persisted["Profiles"]?["Default"]?["Values"]?["Achievement.PopupAutoClose"]?.GetValue<string>());

        AchievementUnlockedEvent? unlocked = null;
        var tracker = new AchievementTrackerService(service, root);
        tracker.AchievementUnlocked += (_, e) => unlocked = e;

        var unlockResult = tracker.Unlock("Linguist");
        Assert.True(unlockResult.Success);
        Assert.NotNull(unlocked);
        Assert.False(unlocked!.AutoClose);
    }

    [Fact]
    public async Task AchievementRestore_PartialBackup_ShouldPreserveCurrentStatesMissingFromBackup()
    {
        var root = CreateTempRoot();
        try
        {
            var tracker = new AchievementTrackerService(configService: null, root);
            Assert.True(tracker.Unlock("Linguist").Success);
            Assert.True(tracker.SetProgress("MissionStartCount", 3).Success);
            tracker.SetCustomData("MissionStartCount", "marker", JsonValue.Create("current"));

            var restorePath = Path.Combine(root, "partial-achievement-backup.json");
            await File.WriteAllTextAsync(
                restorePath,
                """
                {
                  "FirstLaunch": {
                    "Id": "FirstLaunch",
                    "IsUnlocked": true,
                    "Progress": 0
                  }
                }
                """);

            var restored = await tracker.RestoreAsync(restorePath);

            Assert.True(restored.Success);
            var snapshot = await tracker.GetSnapshotAsync("en-us");
            Assert.True(snapshot.Success);
            Assert.Contains(snapshot.Value!.Items, item => item.Id == "FirstLaunch" && item.IsUnlocked);
            Assert.Contains(snapshot.Value.Items, item => item.Id == "Linguist" && item.IsUnlocked);
            Assert.Equal(3, tracker.GetProgress("MissionStartCount"));
            Assert.Equal("current", tracker.GetCustomData("MissionStartCount", "marker")?.GetValue<string>());
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private async Task MissionStartCount_ShouldUseClientYjDayBoundary(
        string clientType,
        string utcNowText,
        string expectedYjDate)
    {
        var root = CreateTempRoot();
        try
        {
            var service = CreateService(root);
            await service.LoadOrBootstrapAsync();
            SetClientType(service, clientType);

            var tracker = new AchievementTrackerService(service, root);
            tracker.MissionStartCountAdd(DateTime.Parse(
                utcNowText,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal));

            var marker = tracker.GetCustomData("MissionStartCount", "LastStartDate");
            Assert.NotNull(marker);
            Assert.Equal(expectedYjDate, marker!.GetValue<DateTime>().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MissionStartCount_ShouldHonorLegacyClientTypeKeyAndDefaultToOfficial()
    {
        var root = CreateTempRoot();
        try
        {
            var service = CreateService(root);
            await service.LoadOrBootstrapAsync();
            Assert.True(service.TryGetCurrentProfile(out var profile));
            // Legacy WPF key only (`Start.ClientType`), no unified `ClientType`.
            profile.Values.Remove("ClientType");
            profile.Values["Start.ClientType"] = JsonValue.Create("YoStarJP");

            var tracker = new AchievementTrackerService(service, root);
            tracker.MissionStartCountAdd(Utc("2026-10-10T18:59:59Z")); // YoStarJP 03:59:59 -> YJ 10-10
            Assert.Equal(
                "2026-10-10",
                tracker.GetCustomData("MissionStartCount", "LastStartDate")!.GetValue<DateTime>()
                    .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

            profile.Values.Remove("Start.ClientType");
            tracker.MissionStartCountAdd(Utc("2026-10-10T19:30:00Z")); // Official 03:30 on 10-11 -> YJ 10-10
            Assert.Equal(
                "2026-10-10",
                tracker.GetCustomData("MissionStartCount", "LastStartDate")!.GetValue<DateTime>()
                    .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

            tracker.MissionStartCountAdd(Utc("2026-10-10T20:30:00Z")); // Official 04:30 on 10-11 -> YJ 10-11
            Assert.Equal(
                "2026-10-11",
                tracker.GetCustomData("MissionStartCount", "LastStartDate")!.GetValue<DateTime>()
                    .ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MissionStartCount_SameYjDayAcrossUtcMidnight_ShouldAccumulate()
    {
        var root = CreateTempRoot();
        try
        {
            var service = CreateService(root);
            await service.LoadOrBootstrapAsync();
            SetClientType(service, "Official");

            var tracker = new AchievementTrackerService(service, root);
            tracker.MissionStartCountAdd(Utc("2026-10-10T20:30:00Z")); // local 10-11 04:30
            tracker.MissionStartCountAdd(Utc("2026-10-11T01:00:00Z")); // local 10-11 09:00
            tracker.MissionStartCountAdd(Utc("2026-10-11T02:00:00Z")); // local 10-11 10:00

            Assert.Equal(3, tracker.GetProgress("MissionStartCount"));
            Assert.Contains(
                (await tracker.GetSnapshotAsync("en-us")).Value!.Items,
                item => item.Id == "MissionStartCount" && item.IsUnlocked);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MissionStartCount_NewYjDay_ShouldResetProgress()
    {
        var root = CreateTempRoot();
        try
        {
            var service = CreateService(root);
            await service.LoadOrBootstrapAsync();
            SetClientType(service, "Official");

            var tracker = new AchievementTrackerService(service, root);
            tracker.MissionStartCountAdd(Utc("2026-10-11T09:00:00Z")); // YJ 10-11
            tracker.MissionStartCountAdd(Utc("2026-10-11T19:30:00Z")); // local 10-12 03:30 -> still YJ 10-11
            tracker.MissionStartCountAdd(Utc("2026-10-11T20:00:00Z")); // local 10-12 04:00 -> YJ 10-12

            Assert.Equal(1, tracker.GetProgress("MissionStartCount"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task UseDailyAdd_ShouldAdvanceWithinOneYjDayAndResetAfterGap()
    {
        var root = CreateTempRoot();
        try
        {
            var service = CreateService(root);
            await service.LoadOrBootstrapAsync();
            SetClientType(service, "Official");

            var tracker = new AchievementTrackerService(service, root);
            tracker.UseDailyAdd(Utc("2026-10-10T20:30:00Z")); // YJ 10-11
            tracker.UseDailyAdd(Utc("2026-10-11T02:00:00Z")); // same YJ 10-11 -> unchanged (delta 0)
            Assert.Equal(1, tracker.GetProgressToGroup("UseDaily"));

            tracker.UseDailyAdd(Utc("2026-10-11T20:30:00Z")); // YJ 10-12 -> delta 1
            Assert.Equal(2, tracker.GetProgressToGroup("UseDaily"));

            tracker.UseDailyAdd(Utc("2026-10-14T09:00:00Z")); // YJ 10-14 -> delta 2
            Assert.Equal(1, tracker.GetProgressToGroup("UseDaily"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ClueObsessionAdd_ShouldUseClientYjDayDelta()
    {
        var root = CreateTempRoot();
        try
        {
            var service = CreateService(root);
            await service.LoadOrBootstrapAsync();
            SetClientType(service, "YoStarEN");

            var tracker = new AchievementTrackerService(service, root);
            tracker.ClueObsessionAdd(Utc("2026-10-11T11:30:00Z")); // YJ 10-11
            tracker.ClueObsessionAdd(Utc("2026-10-11T15:00:00Z")); // same YJ 10-11
            Assert.Equal(1, tracker.GetProgress("ClueObsession"));

            tracker.ClueObsessionAdd(Utc("2026-10-12T11:30:00Z")); // YJ 10-12 -> delta 1
            Assert.Equal(2, tracker.GetProgress("ClueObsession"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task AchievementRestore_ShouldNotOverwriteDailyMarkers()
    {
        var root = CreateTempRoot();
        try
        {
            var service = CreateService(root);
            await service.LoadOrBootstrapAsync();
            SetClientType(service, "Official");

            var tracker = new AchievementTrackerService(service, root);
            tracker.UseDailyAdd(Utc("2026-10-13T09:00:00Z")); // live marker = YJ 10-13
            var liveMarker = tracker.GetCustomData("UseDaily1", "LastLaunchDate");
            Assert.NotNull(liveMarker);

            var restorePath = Path.Combine(root, "stale-achievement-backup.json");
            await File.WriteAllTextAsync(
                restorePath,
                """
                {
                  "UseDaily1": {
                    "Id": "UseDaily1",
                    "IsUnlocked": false,
                    "Progress": 3,
                    "CustomData": { "LastLaunchDate": "2026-10-11T00:00:00" }
                  },
                  "MissionStartCount": {
                    "Id": "MissionStartCount",
                    "IsUnlocked": false,
                    "Progress": 2
                  }
                }
                """);

            var restored = await tracker.RestoreAsync(restorePath);

            Assert.True(restored.Success);
            // Progress is restored (WPF parity), but the live day marker must survive the restore.
            Assert.Equal(3, tracker.GetProgress("UseDaily1"));
            Assert.Equal(
                liveMarker!.GetValue<DateTime>(),
                tracker.GetCustomData("UseDaily1", "LastLaunchDate")!.GetValue<DateTime>());

            // A launch on the same YJ day must not reset the streak (delta 0), matching WPF's live marker.
            tracker.UseDailyAdd(Utc("2026-10-13T15:00:00Z"));
            Assert.Equal(3, tracker.GetProgressToGroup("UseDaily"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private async Task RecordStartup_ShouldUnlockMartianFromBuildOrResourceAge(
        double? buildAgeDays,
        double? resourceAgeDays,
        bool expectMartian)
    {
        var root = CreateTempRoot();
        try
        {
            var tracker = new AchievementTrackerService(configService: null, root);
            var now = DateTimeOffset.UtcNow;
            var context = new AchievementStartupContext(
                now,
                now,
                BuildTimestampUtc: buildAgeDays is { } buildDays ? now.AddDays(-buildDays) : null,
                ResourceTimestampUtc: resourceAgeDays is { } resourceDays ? now.AddDays(-resourceDays) : null);

            var result = tracker.RecordStartup(context);

            Assert.True(result.Success);
            var snapshot = await tracker.GetSnapshotAsync("en-us");
            Assert.True(snapshot.Success);
            Assert.Equal(expectMartian, snapshot.Value!.Items.Any(item => item.Id == "Martian" && item.IsUnlocked));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private async Task RecordStartup_ShouldUnlockUpdateAchievementsFromBuildAge(
        double buildAgeHours,
        bool expectEarlyBird,
        bool expectObsession)
    {
        var root = CreateTempRoot();
        try
        {
            var tracker = new AchievementTrackerService(configService: null, root);
            var now = DateTimeOffset.UtcNow;
            var context = new AchievementStartupContext(
                now,
                now,
                BuildTimestampUtc: now.AddHours(-buildAgeHours));

            var result = tracker.RecordStartup(context);

            Assert.True(result.Success);
            var snapshot = await tracker.GetSnapshotAsync("en-us");
            Assert.True(snapshot.Success);
            Assert.Equal(expectEarlyBird, snapshot.Value!.Items.Any(item => item.Id == "UpdateEarlyBird" && item.IsUnlocked));
            Assert.Equal(expectObsession, snapshot.Value.Items.Any(item => item.Id == "UpdateObsession" && item.IsUnlocked));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RecordStartup_WithoutTimestamps_ShouldNotUnlockAgeAchievements()
    {
        var root = CreateTempRoot();
        try
        {
            var tracker = new AchievementTrackerService(configService: null, root);

            var result = tracker.RecordStartup(new AchievementStartupContext(DateTimeOffset.UtcNow, DateTimeOffset.Now));

            Assert.True(result.Success);
            var snapshot = await tracker.GetSnapshotAsync("en-us");
            Assert.True(snapshot.Success);
            Assert.DoesNotContain(snapshot.Value!.Items, item => item.Id == "Martian" && item.IsUnlocked);
            Assert.DoesNotContain(snapshot.Value.Items, item => item.Id == "UpdateEarlyBird" && item.IsUnlocked);
            Assert.DoesNotContain(snapshot.Value.Items, item => item.Id == "UpdateObsession" && item.IsUnlocked);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private async Task RecordStartup_ShouldUnlockPioneerTierFromCoreVersion(string version, string? expectedId)
    {
        var root = CreateTempRoot();
        try
        {
            var tracker = new AchievementTrackerService(configService: null, root);
            var now = DateTimeOffset.UtcNow;

            var result = tracker.RecordStartup(new AchievementStartupContext(
                now,
                now,
                VersionString: version));

            Assert.True(result.Success);
            var snapshot = await tracker.GetSnapshotAsync("en-us");
            Assert.True(snapshot.Success);
            foreach (var pioneerId in new[] { "Pioneer1", "Pioneer2", "Pioneer3" })
            {
                var isUnlocked = snapshot.Value!.Items.Any(item => item.Id == pioneerId && item.IsUnlocked);
                Assert.Equal(string.Equals(pioneerId, expectedId, StringComparison.Ordinal), isUnlocked);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task RecordStartup_ShouldOnlyRecordOnce()
    {
        var root = CreateTempRoot();
        try
        {
            var tracker = new AchievementTrackerService(configService: null, root);
            var now = DateTimeOffset.UtcNow;

            Assert.True(tracker.RecordStartup(new AchievementStartupContext(now, now)).Success);
            Assert.True(tracker.RecordStartup(new AchievementStartupContext(
                now,
                now,
                BuildTimestampUtc: now.AddHours(-1))).Success);

            var snapshot = await tracker.GetSnapshotAsync("en-us");
            Assert.True(snapshot.Success);
            Assert.DoesNotContain(snapshot.Value!.Items, item => item.Id == "UpdateEarlyBird" && item.IsUnlocked);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }



    private static DateTime Utc(string text)
        => DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);

    [Fact]
    public async Task GuiNewImport_RoguelikeStartingOpers_ImportsFirstThreePositions()
    {
        // WPF dev-v2 RoguelikeStartingOpersConverter keeps StartingOpers as-is;
        // Core only consumes name/use_support, and the UI exposes three positions.
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));

        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.new.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "TaskQueue": [
                    {
                      "$type": "RoguelikeTask",
                      "Name": "Roguelike",
                      "IsEnable": true,
                      "Theme": "Sarkaz",
                      "Mode": "Exp",
                      "StartingOpers": [
                        { "Name": "Amiya", "UseSupport": true },
                        { "Name": "Kal'tsit", "UseSupport": false },
                        { "Name": "Exusiai", "UseSupport": true },
                        { "Name": "FourthOper", "UseSupport": true }
                      ]
                    }
                  ]
                }
              }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.GuiNewOnly, manualImport: false);

        Assert.True(report.Success);
        var task = service.CurrentConfig.Profiles["Default"].TaskQueue.Single(task => task.Type == TaskModuleTypes.Roguelike);
        var opers = Assert.IsType<JsonArray>(task.Params["core_char_list"]);
        Assert.Equal(3, opers.Count);
        var first = Assert.IsType<JsonObject>(opers[0]);
        Assert.Equal("Amiya", first["name"]?.GetValue<string>());
        Assert.True(first["use_support"]?.GetValue<bool>());
        var second = Assert.IsType<JsonObject>(opers[1]);
        Assert.Equal("Kal'tsit", second["name"]?.GetValue<string>());
        Assert.False(second["use_support"]?.GetValue<bool>());
        Assert.Equal("Exusiai", Assert.IsType<JsonObject>(opers[2])["name"]?.GetValue<string>());
        Assert.DoesNotContain("core_char", task.Params.Select(pair => pair.Key), StringComparer.Ordinal);
        Assert.DoesNotContain("use_support", task.Params.Select(pair => pair.Key), StringComparer.Ordinal);
    }

    [Fact]
    public async Task GuiNewImport_RoguelikeLegacyCoreChar_MigratesToFirstStartingOper()
    {
        // v6.3.7 entries only carry CoreChar/UseSupport; they become position 1.
        var root = CreateTempRoot();
        Directory.CreateDirectory(Path.Combine(root, "config"));

        await File.WriteAllTextAsync(
            Path.Combine(root, "config", "gui.new.json"),
            """
            {
              "Current": "Default",
              "Configurations": {
                "Default": {
                  "TaskQueue": [
                    {
                      "$type": "RoguelikeTask",
                      "Name": "Roguelike",
                      "IsEnable": true,
                      "Theme": "Mizuki",
                      "Mode": "Exp",
                      "CoreChar": "Amiya",
                      "UseSupport": true
                    },
                    {
                      "$type": "RoguelikeTask",
                      "Name": "RoguelikeEmpty",
                      "IsEnable": true,
                      "Theme": "Mizuki",
                      "Mode": "Exp",
                      "CoreChar": "",
                      "UseSupport": false
                    }
                  ]
                }
              }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.GuiNewOnly, manualImport: false);

        Assert.True(report.Success);
        var tasks = service.CurrentConfig.Profiles["Default"].TaskQueue;
        var migrated = tasks.Single(task => task.Name == "Roguelike");
        var opers = Assert.IsType<JsonArray>(migrated.Params["core_char_list"]);
        var first = Assert.IsType<JsonObject>(Assert.Single(opers));
        Assert.Equal("Amiya", first["name"]?.GetValue<string>());
        Assert.True(first["use_support"]?.GetValue<bool>());

        var empty = tasks.Single(task => task.Name == "RoguelikeEmpty");
        Assert.Null(empty.Params["core_char_list"]);
        Assert.DoesNotContain("core_char", empty.Params.Select(pair => pair.Key), StringComparer.Ordinal);
    }

    private static void SetClientType(UnifiedConfigurationService service, string clientType)
    {
        Assert.True(service.TryGetCurrentProfile(out var profile));
        profile.Values["ClientType"] = JsonValue.Create(clientType);
    }

    private sealed class CountingConfigStore : IUnifiedConfigStore
    {
        private UnifiedConfig? _config;

        public CountingConfigStore(string baseDirectory, UnifiedConfig? config)
        {
            ConfigPath = Path.Combine(baseDirectory, "config", "avalonia.json");
            _config = config;
        }

        public string ConfigPath { get; }

        public int SaveCount { get; private set; }

        public bool Exists() => _config is not null;

        public Task<UnifiedConfig?> LoadAsync(CancellationToken cancellationToken = default)
        {
            return Task.FromResult(_config);
        }

        public Task SaveAsync(UnifiedConfig config, CancellationToken cancellationToken = default)
        {
            SaveCount += 1;
            _config = config;
            return Task.CompletedTask;
        }

        public Task BackupAsync(string suffix, CancellationToken cancellationToken = default)
        {
            return Task.CompletedTask;
        }
    }
}
