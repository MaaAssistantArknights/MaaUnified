using System.Text.RegularExpressions;

namespace MAAUnified.Tests;

// M01-R7-01: the hand-written field-mapping doc is outside the render-sync gate, so its
// parity-status cells drifted from baseline.freeze.v1.json once rows were promoted. Pin the
// doc's config-key status cells against the machine-readable baseline.
public sealed class BaselineFieldMappingDocConsistencyTests
{
    [Fact]
    public void FieldMappingDoc_ConfigKeyStatusCells_ShouldMatchBaseline()
    {
        var baseline = BaselineTestSupport.LoadBaseline();
        var doc = BaselineTestSupport.ReadDoc("wpf-avalonia-field-mapping.md");

        const string sectionHeader = "## 配置键定位示例";
        var sectionStart = doc.IndexOf(sectionHeader, StringComparison.Ordinal);
        Assert.True(sectionStart >= 0, "Field-mapping doc is missing the config-key locator example section.");
        var sectionEnd = doc.IndexOf("\n## ", sectionStart + sectionHeader.Length, StringComparison.Ordinal);
        var section = sectionEnd < 0 ? doc[sectionStart..] : doc[sectionStart..sectionEnd];

        var rows = new List<(string Key, string Status)>();
        foreach (var line in section.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("| `", StringComparison.Ordinal))
            {
                continue;
            }

            var cells = trimmed.Split('|').Select(cell => cell.Trim()).Where(cell => cell.Length > 0).ToList();
            Assert.True(cells.Count == 3, $"Unexpected table row shape: {trimmed}");
            var key = cells[0].Trim('`');
            var status = cells[2];
            rows.Add((key, status));
        }

        Assert.NotEmpty(rows);

        foreach (var (key, status) in rows)
        {
            var mapping = baseline.ConfigKeyMappings.SingleOrDefault(m => string.Equals(m.Key, key, StringComparison.Ordinal));
            Assert.True(mapping is not null, $"Field-mapping doc names `{key}`, which is not a baseline config row.");
            Assert.True(
                string.Equals(mapping!.ParityStatus, status, StringComparison.Ordinal),
                $"Field-mapping doc marks `{key}` as `{status}` but baseline.freeze.v1.json records `{mapping.ParityStatus}`.");
        }
    }

    // M01-R11-02: the 命令映射 table's left column claims frozen-WPF commands. Pin the table
    // closed-world and require every claimed Windows command to carry a frozen file:line citation
    // (verified read-only against frozen commit 23ddd271b40a3d0c18de8a6ae8305357cc032e19; the
    // frozen tree is not resolvable from this worktree, so citations are asserted for shape).
    // Rows without a frozen WPF counterpart must say so explicitly (无对应 Windows 命令).
    private static readonly IReadOnlyDictionary<string, string> FrozenWpfCommandEvidence =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["LinkStart"] = "src/MaaWpfGui/ViewModels/UI/TaskQueueViewModel.cs:1656",
            ["Stop"] = "src/MaaWpfGui/ViewModels/UI/TaskQueueViewModel.cs:1837",
            ["WaitAndStop"] = "src/MaaWpfGui/ViewModels/UI/TaskQueueViewModel.cs:1860",
            ["生成支持包"] = "src/MaaWpfGui/ViewModels/UserControl/Settings/IssueReportUserControlModel.cs:161 (GenerateSupportPayload)",
            ["Overlay 目标选择"] = "src/MaaWpfGui/ViewModels/UI/TaskQueueViewModel.cs:194 (ChooseOverlayTarget)",
        };

    [Fact]
    public void FieldMappingDoc_CommandTable_ShouldNotClaimFabricatedWpfCommands()
    {
        var doc = BaselineTestSupport.ReadDoc("wpf-avalonia-field-mapping.md");
        var rows = ParseCommandTableRows(doc);

        // Non-vacuity: the table is pinned to the current six rows; adding or dropping a row
        // requires updating this gate together with frozen evidence.
        Assert.Equal(6, rows.Count);

        foreach (var (windowsCommand, avaloniaTarget) in rows)
        {
            Assert.False(string.IsNullOrWhiteSpace(avaloniaTarget), $"Command row `{windowsCommand}` has no Avalonia target.");
            if (windowsCommand.Contains("无对应", StringComparison.Ordinal))
            {
                // Explicit no-WPF-counterpart row: must not silently claim a frozen command.
                Assert.Contains("ConfigConverter", windowsCommand, StringComparison.Ordinal);
                continue;
            }

            Assert.True(
                FrozenWpfCommandEvidence.ContainsKey(windowsCommand),
                $"Command table claims Windows command `{windowsCommand}` without an audited frozen file:line citation.");
        }

        // Closed world in both directions: stale allow-list entries are not allowed to linger.
        var claimed = rows
            .Select(row => row.WindowsCommand)
            .Where(command => !command.Contains("无对应", StringComparison.Ordinal))
            .OrderBy(command => command, StringComparer.Ordinal);
        Assert.Equal(FrozenWpfCommandEvidence.Keys.OrderBy(key => key, StringComparer.Ordinal), claimed);

        foreach (var (command, citation) in FrozenWpfCommandEvidence)
        {
            Assert.Contains("src/MaaWpfGui/", citation, StringComparison.Ordinal);
            Assert.Matches(@"src/MaaWpfGui/\S+:\d+", citation);
        }
    }

    // M01-R13-01: the round-12 gate above audits only the Windows (left) column; the
    // Avalonia-target column was asserted non-empty but its symbols were never resolved, so the
    // Overlay row named `TaskQueuePageViewModel.SelectOverlayTargetAsync`, a member that never
    // existed (the real chain is SelectAndPersistOverlayTargetAsync ->
    // OverlayFeatureService.SelectOverlayTargetAsync). Resolve every class-qualified
    // `ClassName.MemberName` symbol in the Avalonia cell against runtime sources. Closed world:
    // every row must name at least one class-qualified symbol, and every named symbol must
    // resolve, so a stale or fabricated target fails.
    [Fact]
    public void FieldMappingDoc_CommandTable_AvaloniaTargets_ShouldResolveToRuntimeSource()
    {
        var doc = BaselineTestSupport.ReadDoc("wpf-avalonia-field-mapping.md");
        var rows = ParseCommandTableRows(doc);
        Assert.NotEmpty(rows);

        var symbolPattern = new Regex(
            @"\b([A-Z][A-Za-z0-9_]*)\.([A-Z][A-Za-z0-9_]*)\b",
            RegexOptions.CultureInvariant);

        foreach (var (windowsCommand, avaloniaTarget) in rows)
        {
            var symbols = symbolPattern.Matches(avaloniaTarget)
                .Select(match => (Type: match.Groups[1].Value, Member: match.Groups[2].Value))
                .Distinct()
                .ToList();
            Assert.NotEmpty(symbols);

            foreach (var (type, member) in symbols)
            {
                AssertRuntimeMember(windowsCommand, type, member);
            }
        }
    }

    private static List<(string WindowsCommand, string AvaloniaTarget)> ParseCommandTableRows(string doc)
    {
        const string sectionHeader = "## 命令映射";
        var sectionStart = doc.IndexOf(sectionHeader, StringComparison.Ordinal);
        Assert.True(sectionStart >= 0, "Field-mapping doc is missing the command-mapping section.");
        var sectionEnd = doc.IndexOf("\n## ", sectionStart + sectionHeader.Length, StringComparison.Ordinal);
        var section = sectionEnd < 0 ? doc[sectionStart..] : doc[sectionStart..sectionEnd];

        var rows = new List<(string WindowsCommand, string AvaloniaTarget)>();
        foreach (var line in section.Split('\n'))
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("| ", StringComparison.Ordinal)
                || trimmed.StartsWith("| ---", StringComparison.Ordinal)
                || trimmed.StartsWith("| Windows", StringComparison.Ordinal))
            {
                continue;
            }

            var cells = trimmed.Split('|').Select(cell => cell.Trim()).Where(cell => cell.Length > 0).ToList();
            Assert.True(cells.Count == 2, $"Unexpected command-table row shape: {trimmed}");
            rows.Add((cells[0], cells[1]));
        }

        return rows;
    }

    private static void AssertRuntimeMember(string windowsCommand, string typeName, string memberName)
    {
        var root = BaselineTestSupport.GetMaaUnifiedRoot();
        var typeDeclaration = new Regex(
            @"\b(?:class|record|struct|interface)\s+" + Regex.Escape(typeName) + @"\b",
            RegexOptions.CultureInvariant);
        var memberDeclaration = new Regex(
            @"(?:public|private|internal|protected)[\w\s<>\[\],.?]*?\b" + Regex.Escape(memberName) + @"\s*[(={]",
            RegexOptions.CultureInvariant);

        var declaringFiles = new List<string>();
        foreach (var dir in new[] { "App", "Application", "Compat" })
        {
            var fullDir = Path.Combine(root, dir);
            if (!Directory.Exists(fullDir))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(fullDir, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                    || file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
                {
                    continue;
                }

                if (typeDeclaration.IsMatch(File.ReadAllText(file)))
                {
                    declaringFiles.Add(file);
                }
            }
        }

        Assert.True(
            declaringFiles.Count > 0,
            $"Command row `{windowsCommand}` names `{typeName}.{memberName}`, but no runtime source declares type `{typeName}`.");
        Assert.True(
            declaringFiles.Any(file => memberDeclaration.IsMatch(File.ReadAllText(file))),
            $"Command row `{windowsCommand}` names `{typeName}.{memberName}`, but no declaring file declares that member: {string.Join(", ", declaringFiles)}");
    }
}
