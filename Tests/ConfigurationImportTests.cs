using System.Text.Json.Nodes;
using MAAUnified.Application.Configuration;
using MAAUnified.Application.Models;
using MAAUnified.Application.Models.TaskParams;
using MAAUnified.Application.Services;
using MAAUnified.Application.Services.Features;
using MAAUnified.Application.Services.TaskParams;
using MAAUnified.Platform;

namespace MAAUnified.Tests;

public sealed class ConfigurationImportTests
{
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
                      "ToolToCraft": "荧光棒\r\n工具A；工具B,工具C|工具D"
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
                      "CustomTaskName": "Fight\r\nRecruit；Roguelike|Reclamation"
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
        Assert.Equal(5, roguelikeCollapse.Params["mode"]?.GetValue<int>());

        var roguelikeCollectible = tasks.Single(task => task.Name == "RoguelikeCollectible");
        Assert.Equal("Mizuki", roguelikeCollectible.Params["theme"]?.GetValue<string>());
        Assert.Equal(4, roguelikeCollectible.Params["mode"]?.GetValue<int>());
        Assert.Equal(["hot_water", "shield", "ideas", "ticket"], ReadEnabledCollectibleAwards(roguelikeCollectible.Params["collectible_mode_start_list"]));

        var roguelikeFindPlaytime = tasks.Single(task => task.Name == "RoguelikeFindPlaytime");
        Assert.Equal(20001, roguelikeFindPlaytime.Params["mode"]?.GetValue<int>());
        Assert.Equal(2, roguelikeFindPlaytime.Params["find_playTime_target"]?.GetValue<int>());

        var reclamationNoSave = tasks.Single(task => task.Name == "ReclamationNoSave");
        Assert.Equal("Fire", reclamationNoSave.Params["theme"]?.GetValue<string>());
        Assert.Equal(0, reclamationNoSave.Params["mode"]?.GetValue<int>());
        Assert.Equal(1, reclamationNoSave.Params["increment_mode"]?.GetValue<int>());

        var reclamationArchive = tasks.Single(task => task.Name == "ReclamationArchive");
        Assert.Equal(1, reclamationArchive.Params["mode"]?.GetValue<int>());
        Assert.Equal(
            ["荧光棒", "工具A", "工具B", "工具C", "工具D"],
            ReadStringArray((JsonArray)reclamationArchive.Params["tools_to_craft"]!));

        var reclamationUnsupported = tasks.Single(task => task.Name == "ReclamationUnsupported");
        Assert.Equal("RelaunchAnchor", reclamationUnsupported.Params["theme"]?.GetValue<string>());
        Assert.Equal(16, reclamationUnsupported.Params["mode"]?.GetValue<int>());
        Assert.DoesNotContain(report.Warnings, warning => warning.Contains("RA/RelaunchAnchor", StringComparison.Ordinal));

        var recruit = tasks.Single(task => task.Name == "Recruit");
        Assert.Contains(6, ((JsonArray)recruit.Params["select"]!).Select(item => item?.GetValue<int>()));
        Assert.Contains(6, ((JsonArray)recruit.Params["confirm"]!).Select(item => item?.GetValue<int>()));
        Assert.Equal(["支援机械", "高级资深干员"], ReadStringArray((JsonArray)recruit.Params["preserve_tags"]!));

        var custom = tasks.Single(task => task.Name == "Custom");
        Assert.Equal(
            ["Fight", "Recruit", "Roguelike", "Reclamation"],
            ReadStringArray((JsonArray)custom.Params["task_names"]!));
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
        Assert.True(compiled.HasBlockingIssues);
        Assert.Contains(compiled.Issues, issue => issue.Code == "SingleStepTypeMissing");

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
        var task = Assert.Single(service.CurrentConfig.Profiles["Default"].TaskQueue);
        Assert.Equal(3, task.Params["expiring_medicine"]?.GetValue<int>());

        var (dto, issues) = TaskParamCompiler.ReadFight(task, strict: true);
        Assert.Empty(issues);
        Assert.True(dto.UseExpiringMedicine);
        Assert.Equal(3, dto.ExpiringMedicine);

        var compiled = TaskParamCompiler.CompileFight(dto, service.CurrentConfig.Profiles["Default"], service.CurrentConfig);
        Assert.Equal(3, compiled.Params["expiring_medicine"]?.GetValue<int>());
        Assert.Equal(3, compiled.Params["medicine_expire_days"]?.GetValue<int>());
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
        var task = Assert.Single(service.CurrentConfig.Profiles["Default"].TaskQueue);
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
    public async Task GuiNewImport_ReclamationRelaunchAnchorModes_ShouldPreserveRaThemeAndModes()
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
                      "Name": "RA1",
                      "IsEnable": true,
                      "Theme": "RelaunchAnchor",
                      "Mode": 16
                    },
                    {
                      "$type": "ReclamationTask",
                      "Name": "RA15",
                      "IsEnable": true,
                      "Theme": "RelaunchAnchor",
                      "Mode": 32
                    },
                    {
                      "$type": "ReclamationTask",
                      "Name": "RA4",
                      "IsEnable": true,
                      "Theme": "RelaunchAnchor",
                      "Mode": 48
                    }
                  ]
                }
              }
            }
            """);

        var service = CreateService(root);
        var report = await service.ImportLegacyAsync(ImportSource.GuiNewOnly, manualImport: false);

        Assert.True(report.Success);
        Assert.DoesNotContain(report.Warnings, warning => warning.Contains("RA/RelaunchAnchor", StringComparison.Ordinal));

        var tasks = service.CurrentConfig.Profiles["Default"].TaskQueue;
        Assert.Equal(16, AssertRaTask(tasks, "RA1").Params["mode"]?.GetValue<int>());
        Assert.Equal(32, AssertRaTask(tasks, "RA15").Params["mode"]?.GetValue<int>());
        Assert.Equal(48, AssertRaTask(tasks, "RA4").Params["mode"]?.GetValue<int>());
    }

    [Fact]
    public async Task GuiNewImport_RecruitSkipRobotDoesNotEnablePreserveTagsWhenPreserveDisabled()
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
        var task = Assert.Single(service.CurrentConfig.Profiles["Default"].TaskQueue);
        Assert.True(task.Params["skip_robot"]?.GetValue<bool>());
        Assert.False(task.Params["_ui_preserve_tags_enabled"]?.GetValue<bool>());
        Assert.Empty(ReadStringArray((JsonArray)task.Params["preserve_tags"]!));
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

        var fight = tasks.Single(task => task.Type == TaskModuleTypes.Fight);
        Assert.True(fight.IsEnabled);
        Assert.Equal("1-7", fight.Params["stage"]?.GetValue<string>());
        Assert.Equal(4, fight.Params["medicine"]?.GetValue<int>());
        Assert.Equal(12, fight.Params["times"]?.GetValue<int>());
        Assert.Equal(3, fight.Params["series"]?.GetValue<int>());
        Assert.Equal(9999, fight.Params["expiring_medicine"]?.GetValue<int>());
        Assert.True(fight.Params["_ui_is_inventory_target"]?.GetValue<bool>());
        Assert.True(fight.Params["_ui_use_expire_medicine_for_activity"]?.GetValue<bool>());
        Assert.Equal("30012", fight.Params["_ui_drop_id"]?.GetValue<string>());
        Assert.Equal(300, fight.Params["_ui_drop_count"]?.GetValue<int>());

        var recruit = tasks.Single(task => task.Type == TaskModuleTypes.Recruit);
        Assert.True(recruit.IsEnabled);
        Assert.Equal(2, recruit.Params["times"]?.GetValue<int>());
        Assert.Contains(5, ((JsonArray)recruit.Params["select"]!).Select(item => item?.GetValue<int>()));
        Assert.Equal(["资深干员", "高级资深干员"], ReadStringArray((JsonArray)recruit.Params["first_tags"]!));
        Assert.Equal(["支援机械", "高级资深干员"], ReadStringArray((JsonArray)recruit.Params["preserve_tags"]!));

        var infrast = tasks.Single(task => task.Type == TaskModuleTypes.Infrast);
        Assert.True(infrast.IsEnabled);
        Assert.Equal(10000, infrast.Params["mode"]?.GetValue<int>());
        Assert.Equal("/tmp/infrast.json", infrast.Params["filename"]?.GetValue<string>());
        Assert.Equal(1, infrast.Params["plan_index"]?.GetValue<int>());
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
        Assert.Equal(1, service.CurrentConfig.SchemaVersion);
        var schemaBackupExists = Directory.EnumerateFiles(Path.Combine(root, "config"), "avalonia.json.schema-v1.bak.*").Any();
        Assert.False(schemaBackupExists);
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
    public async Task ExistingAvaloniaConfig_BackfilledAchievementAutoCloseFalse_ShouldRepairAndPersist()
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

        Assert.True(result.LoadedFromExistingConfig);
        Assert.True(service.CurrentConfig.Profiles["MissingSetting"].Values["Achievement.PopupAutoClose"]?.GetValue<bool>());
        Assert.True(service.CurrentConfig.Profiles["Default"].Values["Achievement.PopupAutoClose"]?.GetValue<bool>());
        Assert.True(service.CurrentConfig.Profiles["Alt"].Values["Achievement.PopupAutoClose"]?.GetValue<bool>());
        Assert.Contains(
            service.LogService.Snapshot,
            log => string.Equals(log.Level, "INFO", StringComparison.Ordinal)
                   && log.Message.Contains("achievement popup auto-close", StringComparison.OrdinalIgnoreCase));

        var persisted = Assert.IsType<JsonObject>(
            JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(root, "config", "avalonia.json"))));
        Assert.True(persisted["Profiles"]?["Default"]?["Values"]?["Achievement.PopupAutoClose"]?.GetValue<bool>());

        AchievementUnlockedEvent? unlocked = null;
        var tracker = new AchievementTrackerService(service, root);
        tracker.AchievementUnlocked += (_, e) => unlocked = e;

        var unlockResult = tracker.Unlock("Linguist");
        Assert.True(unlockResult.Success);
        Assert.NotNull(unlocked);
        Assert.True(unlocked!.AutoClose);
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
    public async Task OutdatedSchema_LoadsWithMigrationWarningIssue()
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

        var issue = Assert.Single(result.ValidationIssues.Where(i =>
            string.Equals(i.Scope, "ConfigMigration", StringComparison.Ordinal) &&
            string.Equals(i.Code, "SchemaOutdated", StringComparison.Ordinal)));
        Assert.False(issue.Blocking);
        Assert.Equal("schema_version", issue.Field);
        Assert.NotNull(result.SchemaMigrationNotice);
        Assert.Equal(1, result.SchemaMigrationNotice!.CurrentSchemaVersion);
        Assert.Equal(UnifiedConfig.LatestSchemaVersion, result.SchemaMigrationNotice.LatestSchemaVersion);
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
    public async Task LoadOrBootstrapAsync_CurrentProfileMissing_ShouldBeBlockingAndSynced()
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
                "Alt": { "Values": {}, "TaskQueue": [] }
              },
              "GlobalValues": {},
              "Migration": {}
            }
            """);

        var service = CreateService(root);
        var load = await service.LoadOrBootstrapAsync();

        var issue = Assert.Single(load.ValidationIssues, i =>
            string.Equals(i.Code, "CurrentProfileMissing", StringComparison.Ordinal));
        Assert.True(issue.Blocking);
        Assert.True(load.HasBlockingValidationIssues);
        Assert.True(service.HasBlockingValidationIssues);
        Assert.Contains(service.CurrentValidationIssues, i => string.Equals(i.Code, "CurrentProfileMissing", StringComparison.Ordinal));
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
    public async Task SaveAsync_NormalizedToLoadedSnapshot_ShouldNotWriteOrRaiseConfigChanged()
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

        Assert.Equal(0, store.SaveCount);
        Assert.Equal(0, configChangedCount);
        Assert.False(service.CurrentConfig.Profiles["Default"].TaskQueue[0].Params["clear_store"]?.GetValue<bool>());
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
