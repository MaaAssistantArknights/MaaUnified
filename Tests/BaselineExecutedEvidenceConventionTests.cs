using System.Text.RegularExpressions;

namespace MAAUnified.Tests;

// M01-R7-03: an Aligned config row must explicitly declare its executed-evidence state instead
// of staying silent. Rows either cite real executed tests (`Executed evidence: <Class>.<Method>`,
// verified to exist below) or carry an explicit `Executed evidence: pending` marker meaning the
// locator contract is verified but per-row executed evidence is not yet attached.
public sealed class BaselineExecutedEvidenceConventionTests
{
    private const string Marker = "Executed evidence:";

    [Fact]
    public void AlignedConfigRows_ShouldDeclareExecutedEvidenceStateExplicitly()
    {
        var baseline = BaselineTestSupport.LoadBaseline();
        var aligned = baseline.ConfigKeyMappings
            .Where(mapping => string.Equals(mapping.ParityStatus, "Aligned", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(aligned);

        foreach (var mapping in aligned)
        {
            var notes = mapping.Notes ?? string.Empty;
            var markerIndex = notes.IndexOf(Marker, StringComparison.Ordinal);
            Assert.True(
                markerIndex >= 0,
                $"Aligned config row `{mapping.Key}` must declare its executed-evidence state in notes " +
                "(`Executed evidence: <TestClass>.<TestMethod>` or `Executed evidence: pending`).");

            var declaration = notes[(markerIndex + Marker.Length)..].Trim();
            Assert.False(
                string.IsNullOrWhiteSpace(declaration),
                $"Aligned config row `{mapping.Key}` carries an empty executed-evidence declaration.");

            if (declaration.StartsWith("pending", StringComparison.Ordinal))
            {
                continue;
            }

            Assert.True(
                ExtractTestReferences(declaration).Count > 0,
                $"Aligned config row `{mapping.Key}` must cite at least one executed test as `<TestClass>.<TestMethod>` " +
                "or explicitly declare `Executed evidence: pending`.");
        }
    }

    [Fact]
    public void AlignedConfigRows_CitedExecutedTests_ShouldExist()
    {
        var baseline = BaselineTestSupport.LoadBaseline();
        var testsDirectory = Path.Combine(BaselineTestSupport.GetMaaUnifiedRoot(), "Tests");
        Assert.True(Directory.Exists(testsDirectory), $"Tests directory not found at {testsDirectory}.");

        foreach (var mapping in baseline.ConfigKeyMappings.Where(m => string.Equals(m.ParityStatus, "Aligned", StringComparison.Ordinal)))
        {
            foreach (var (className, methodName) in ExtractTestReferences(mapping.Notes ?? string.Empty))
            {
                var testFile = Path.Combine(testsDirectory, className + ".cs");
                Assert.True(
                    File.Exists(testFile),
                    $"Aligned config row `{mapping.Key}` cites `{className}.{methodName}`, but `{testFile}` does not exist.");
                var source = File.ReadAllText(testFile);
                Assert.True(
                    Regex.IsMatch(source, $@"\bvoid\s+{Regex.Escape(methodName)}\s*\(|\bTask\s+{Regex.Escape(methodName)}\s*\("),
                    $"Aligned config row `{mapping.Key}` cites `{className}.{methodName}`, but no such test method exists in `{testFile}`.");
            }
        }
    }

    private static IReadOnlyList<(string ClassName, string MethodName)> ExtractTestReferences(string text)
    {
        return Regex.Matches(text, @"\b(?<class>[A-Za-z0-9_]+Tests)\.(?<method>[A-Za-z0-9_]+)\b")
            .Select(match => (match.Groups["class"].Value, match.Groups["method"].Value))
            .ToList();
    }
}
