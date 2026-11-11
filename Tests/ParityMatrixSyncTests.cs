using System.Globalization;
using System.Text.RegularExpressions;

namespace MAAUnified.Tests;

public sealed class ParityMatrixSyncTests
{
    private static readonly string[] AllowedMatrixStatus =
    [
        "Implemented",
        "InProgress",
        "Pending",
    ];

    [Fact]
    public void ParityMatrix_ShouldDeclareBaselineFreezeAsSyncSource()
    {
        var doc = BaselineTestSupport.ReadDoc("avalonia-parity-matrix.md");
        Assert.Contains("Docs/testing/baseline.freeze.v1.md", doc, StringComparison.Ordinal);
    }

    [Fact]
    public void ParityMatrix_DopTargets_ShouldMatchBaselineParity()
    {
        var baseline = BaselineTestSupport.LoadBaseline();
        var matrixStatus = ParseMatrixStatus(BaselineTestSupport.ReadDoc("avalonia-parity-matrix.md"));

        var targets = new (string ItemId, string MatrixModule)[]
        {
            ("TaskQueueRoot", "任务队列"),
            ("Advanced.Copilot", "Copilot"),
            ("Advanced.Toolbox", "Toolbox"),
            ("Advanced.StageManager", "StageManager"),
            ("Advanced.WebApi", "Web API"),
            ("Dialog.Announcement", "AnnouncementDialog"),
            ("Dialog.VersionUpdate", "VersionUpdateDialog"),
            ("Dialog.ProcessPicker", "ProcessPickerDialog"),
            ("Dialog.EmulatorPath", "EmulatorPathSelectionDialog"),
            ("Dialog.Error", "ErrorDialog"),
            ("Dialog.AchievementList", "AchievementListDialog"),
            ("Dialog.TextDialog", "TextDialog"),
        };

        foreach (var (itemId, matrixModule) in targets)
        {
            var item = Assert.Single(
                baseline.Items,
                i => string.Equals(i.ItemId, itemId, StringComparison.Ordinal));
            var expectedStatus = ToMatrixStatus(item.ParityStatus);

            Assert.True(
                matrixStatus.TryGetValue(matrixModule, out var actualStatus),
                $"Parity matrix row `{matrixModule}` is missing.");
            Assert.Contains(actualStatus, AllowedMatrixStatus);
            Assert.Equal(
                expectedStatus,
                actualStatus);
        }
    }

    // M01-R13-02: matrix note prose is free text outside ParityMatrix_DopTargets (which pins
    // only the 12 status cells), and the Toolbox note drifted from the baseline per-owner counts
    // ("大部分仍为 Gap" while 10/11 owned keys were Aligned). Any `<Module> note:` line that
    // mentions 配置键 must carry a machine-checkable declaration
    // `配置键归属计数：<owner_item_id> Aligned=<a>，Gap=<g>，Waived=<w>` whose counts equal the
    // baseline per-owner parity counts. Notes without config-key claims stay exempt prose.
    [Fact]
    public void ParityMatrix_ModuleNotes_ShouldMatchBaselineConfigOwnershipCounts()
    {
        var baseline = BaselineTestSupport.LoadBaseline();
        var doc = BaselineTestSupport.ReadDoc("avalonia-parity-matrix.md");

        var notePattern = new Regex(@"^(\S.*?) note: (.+)$", RegexOptions.Multiline | RegexOptions.CultureInvariant);
        var declarationPattern = new Regex(
            @"配置键归属计数：([A-Za-z0-9.]+) Aligned=(\d+)，Gap=(\d+)，Waived=(\d+)",
            RegexOptions.CultureInvariant);

        var notes = notePattern.Matches(doc);
        Assert.NotEmpty(notes);

        var gatedModules = new List<string>();
        foreach (Match note in notes)
        {
            var module = note.Groups[1].Value;
            var text = note.Groups[2].Value;
            var declaration = declarationPattern.Match(text);
            if (text.Contains("配置键", StringComparison.Ordinal))
            {
                Assert.True(
                    declaration.Success,
                    $"Matrix note `{module}` makes a config-key claim without a machine-checkable `配置键归属计数` declaration.");
            }

            if (!declaration.Success)
            {
                continue;
            }

            gatedModules.Add(module);
            var owner = declaration.Groups[1].Value;
            Assert.True(
                baseline.Items.Any(item => string.Equals(item.ItemId, owner, StringComparison.Ordinal)),
                $"Matrix note `{module}` declares config counts for unknown baseline item `{owner}`.");

            var owned = baseline.ConfigKeyMappings
                .Where(mapping => string.Equals(mapping.OwnerItemId, owner, StringComparison.Ordinal))
                .ToList();
            var aligned = owned.Count(mapping => mapping.ParityStatus == "Aligned");
            var gap = owned.Count(mapping => mapping.ParityStatus == "Gap");
            var waived = owned.Count(mapping => mapping.ParityStatus == "Waived");

            Assert.True(
                int.Parse(declaration.Groups[2].Value, CultureInfo.InvariantCulture) == aligned
                && int.Parse(declaration.Groups[3].Value, CultureInfo.InvariantCulture) == gap
                && int.Parse(declaration.Groups[4].Value, CultureInfo.InvariantCulture) == waived,
                $"Matrix note `{module}` declares Aligned={declaration.Groups[2].Value}/Gap={declaration.Groups[3].Value}/Waived={declaration.Groups[4].Value} "
                + $"for `{owner}`, but the baseline records Aligned={aligned}/Gap={gap}/Waived={waived}.");
        }

        // Non-vacuity: the drifted Toolbox note and the Remote Control Center note are gated today.
        Assert.Contains("Toolbox", gatedModules);
        Assert.Contains("Remote Control Center", gatedModules);
    }

    private static string ToMatrixStatus(string parityStatus)
    {
        return parityStatus switch
        {
            "Aligned" => "Implemented",
            "Gap" => "InProgress",
            "Waived" => "Pending",
            _ => throw new InvalidOperationException($"Unsupported baseline parity status `{parityStatus}`."),
        };
    }

    private static IReadOnlyDictionary<string, string> ParseMatrixStatus(string markdown)
    {
        var rows = new Dictionary<string, string>(StringComparer.Ordinal);
        var lines = BaselineTestSupport.NormalizeLineEndings(markdown).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        foreach (var rawLine in lines)
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("|", StringComparison.Ordinal) || !line.EndsWith("|", StringComparison.Ordinal))
            {
                continue;
            }

            var cells = line.Split('|', StringSplitOptions.TrimEntries);
            if (cells.Length < 4)
            {
                continue;
            }

            var module = cells[1];
            var status = cells[2];
            if (string.IsNullOrWhiteSpace(module) || string.IsNullOrWhiteSpace(status))
            {
                continue;
            }

            if (string.Equals(module, "模块", StringComparison.Ordinal) || string.Equals(module, "---", StringComparison.Ordinal))
            {
                continue;
            }

            rows[module] = status;
        }

        return rows;
    }
}
