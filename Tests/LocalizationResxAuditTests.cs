using MAAUnified.Application.Services.Localization;

namespace MAAUnified.Tests;

public sealed class LocalizationResxAuditTests
{
    private static string LocalizationDirectory
        => Path.Combine(TestRepoLayout.GetMaaUnifiedRoot(), "Application", "Resources", "Localization");

    private static string BaselinePath
        => Path.Combine(TestRepoLayout.GetMaaUnifiedRoot(), "Tests", "Localization", "LocalizationResxBaseline.json");

    [Fact]
    public void ResxFiles_ShouldSatisfyDottedKeyContractAndFiveLanguageSets()
    {
        var documents = LocalizationResxAudit.LoadDocuments(LocalizationDirectory);
        var baseline = LocalizationAuditBaseline.Load(BaselinePath);

        var violations = LocalizationResxAudit.FindViolations(documents, baseline);

        Assert.Empty(violations);
    }

    [Fact]
    public void CommonNotAdapted_ShouldResolveInAllFiveLanguagesWithoutFallback()
    {
        var resourceManager = UiLocalizationResourceManager.Shared;
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["zh-cn"] = "未适配",
            ["zh-tw"] = "未適配",
            ["en-us"] = "Not yet adapted",
            ["ja-jp"] = "未対応",
            ["ko-kr"] = "미지원",
        };

        foreach (var (language, text) in expected)
        {
            var result = resourceManager.Lookup(language, "Common.NotAdapted");
            Assert.Equal(text, result.Value);
            Assert.Equal("none", result.FallbackSource);
        }
    }

    [Fact]
    public void NewFlatKey_ShouldBeRejectedByNamingRule()
    {
        var documents = BuildDocuments(["Common.A", "Common.B", "BrandNewFlatKey"]);
        var violations = LocalizationResxAudit.FindViolations(documents, new LocalizationAuditBaseline());

        Assert.Contains(violations, violation => violation.Contains("BrandNewFlatKey", StringComparison.Ordinal));
    }

    [Fact]
    public void LegacyNonConformingKey_ShouldBeAllowedByBaseline()
    {
        var documents = BuildDocuments(["BrandNewFlatKey", "Common.A"]);
        var baseline = new LocalizationAuditBaseline
        {
            LegacyNonConformingKeys = ["BrandNewFlatKey"],
        };

        Assert.Empty(LocalizationResxAudit.FindViolations(documents, baseline));
    }

    [Fact]
    public void NewKeyMissingFromOneLanguage_ShouldBeRejected()
    {
        var documents = BuildDocuments(["Common.A", "Common.B"]);
        documents["ja-jp"] = ["Common.A"];
        var violations = LocalizationResxAudit.FindViolations(documents, new LocalizationAuditBaseline());

        Assert.Contains(
            violations,
            violation => violation.Contains("ja-jp: missing key 'Common.B'", StringComparison.Ordinal));
    }

    [Fact]
    public void KnownMissingKey_ShouldBeAllowedByBaseline()
    {
        var documents = BuildDocuments(["Common.A", "Common.B"]);
        documents["ja-jp"] = ["Common.A"];
        var baseline = new LocalizationAuditBaseline
        {
            AllowedMissingKeys = new Dictionary<string, List<string>>(StringComparer.Ordinal)
            {
                ["ja-jp"] = ["Common.B"],
            },
        };

        Assert.Empty(LocalizationResxAudit.FindViolations(documents, baseline));
    }

    [Fact]
    public void PallasExtraKey_ShouldBeRejected()
    {
        var documents = BuildDocuments(["Common.A"]);
        documents[LocalizationResxAudit.PallasLanguage] = ["Common.A", "Common.OnlyPseudo"];
        var violations = LocalizationResxAudit.FindViolations(documents, new LocalizationAuditBaseline());

        Assert.Contains(
            violations,
            violation => violation.Contains("Common.OnlyPseudo", StringComparison.Ordinal));
    }

    [Fact]
    public void PallasSubset_ShouldBeAllowed()
    {
        var documents = BuildDocuments(["Common.A", "Common.B"]);
        documents[LocalizationResxAudit.PallasLanguage] = ["Common.A"];

        Assert.Empty(LocalizationResxAudit.FindViolations(documents, new LocalizationAuditBaseline()));
    }

    [Fact]
    public void NewOutOfOrderKey_ShouldExceedFrozenInversionBudget()
    {
        var documents = BuildDocuments(["Common.A", "Common.C", "Common.B"]);
        var violations = LocalizationResxAudit.FindViolations(documents, new LocalizationAuditBaseline());

        Assert.Contains(violations, violation => violation.Contains("out-of-order", StringComparison.Ordinal));
    }

    [Fact]
    public void FrozenInversions_ShouldBeTolerated()
    {
        var documents = BuildDocuments(["Common.C", "Common.B", "Common.A"]);
        var baseline = new LocalizationAuditBaseline
        {
            AllowedInversions = LocalizationResxAudit.FiveLanguages
                .ToDictionary(language => language, _ => 2, StringComparer.Ordinal),
        };
        baseline.AllowedInversions[LocalizationResxAudit.PallasLanguage] = 2;

        Assert.Empty(LocalizationResxAudit.FindViolations(documents, baseline));
    }

    [Fact]
    public void DuplicateKeys_ShouldBeRejected()
    {
        var documents = BuildDocuments(["Common.A", "Common.A"]);
        var violations = LocalizationResxAudit.FindViolations(documents, new LocalizationAuditBaseline());

        Assert.Contains(violations, violation => violation.Contains("duplicate", StringComparison.Ordinal));
    }

    private static Dictionary<string, IReadOnlyList<string>> BuildDocuments(IReadOnlyList<string> orderedKeys)
    {
        var documents = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var language in LocalizationResxAudit.FiveLanguages)
        {
            documents[language] = orderedKeys;
        }

        documents[LocalizationResxAudit.PallasLanguage] = [];
        return documents;
    }
}
