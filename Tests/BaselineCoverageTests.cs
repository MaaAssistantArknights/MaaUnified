using MAAUnified.App.ViewModels;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MAAUnified.Tests;

public sealed class BaselineCoverageTests
{
    [Fact]
    public void BaselineCoverage_ShouldMatchFeatureManifest38()
    {
        var baseline = BaselineTestSupport.LoadBaseline();

        var featureItems = baseline.Items
            .Where(i => i.Kind == "Feature")
            .Select(i => i.ItemId)
            .ToList();

        const int expectedFeatureCount = 38;

        // This count is intentionally pinned: if it changes, baseline.freeze + FeatureManifest must be reviewed together.
        Assert.Equal(expectedFeatureCount, featureItems.Count);

        var duplicates = featureItems.GroupBy(i => i).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.Empty(duplicates);

        var manifestKeys = FeatureManifest.All.Select(m => m.Key).ToHashSet(StringComparer.Ordinal);
        var baselineKeys = featureItems.ToHashSet(StringComparer.Ordinal);

        Assert.Equal(expectedFeatureCount, manifestKeys.Count);
        Assert.Equal(expectedFeatureCount, baselineKeys.Count);

        Assert.True(manifestKeys.SetEquals(baselineKeys),
            $"Baseline feature keys mismatch. Missing: {string.Join(", ", manifestKeys.Except(baselineKeys))}; Extra: {string.Join(", ", baselineKeys.Except(manifestKeys))}");
    }

    [Fact]
    public void BaselineConfigKeys_ShouldSeparateFrozenWpfKeysFromAvaloniaExtensions()
    {
        var baseline = BaselineTestSupport.LoadBaseline();

        var legacyKeys = BaselineTestSupport.GetLegacyConfigurationKeys();
        var baselineKeys = baseline.ConfigKeyMappings.Select(k => k.Key).ToList();

        Assert.Equal(324, legacyKeys.Count);
        Assert.Equal(324, baselineKeys.Count);

        var legacySet = legacyKeys.ToHashSet(StringComparer.Ordinal);
        var baselineSet = baselineKeys.ToHashSet(StringComparer.Ordinal);

        Assert.True(legacySet.SetEquals(baselineSet),
            $"Config key coverage mismatch. Missing: {string.Join(", ", legacySet.Except(baselineSet))}; Extra: {string.Join(", ", baselineSet.Except(legacySet))}");

        var duplicates = baselineKeys.GroupBy(k => k).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.Empty(duplicates);

        // Derived from the full frozen WPF configuration surface at the frozen SHA (legacy
        // ConfigurationKeys constants + ConfigFactory Configuration/** models + raw
        // ConfigurationHelper string keys); see BaselineTestSupport.AvaloniaExtensionConfigKeys.
        var avaloniaExtensions = BaselineTestSupport.AvaloniaExtensionConfigKeys;

        var frozenWpfKeys = baselineKeys
            .Where(key => !avaloniaExtensions.Contains(key))
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();
        var serializedKeySet = string.Join('\n', frozenWpfKeys) + "\n";
        var keySetHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(serializedKeySet))).ToLowerInvariant();

        Assert.Equal(292, baseline.Metadata.WpfConfigKeyCount);
        Assert.Equal(32, baseline.Metadata.AvaloniaExtensionConfigKeyCount);
        Assert.Equal(baseline.Metadata.WpfConfigKeyCount, frozenWpfKeys.Count);
        Assert.Equal(baseline.Metadata.AvaloniaExtensionConfigKeyCount, avaloniaExtensions.Count);
        Assert.Equal("566082d9e031b813dd5806d73baa6c319b0e44db24a2a6001be3b16eae94a0cd", keySetHash);
        Assert.Equal(keySetHash, baseline.Metadata.WpfConfigKeySetSha256);
    }

    [Fact]
    public void BaselineConfigRowCaseIds_ShouldBeUniqueAndWellFormed()
    {
        var baseline = BaselineTestSupport.LoadBaseline();

        // M01-R7-02: the acceptance template requires failures to be locatable by scope/case id,
        // so duplicated config-row case ids weaken log-to-row locatability.
        var collisions = baseline.ConfigKeyMappings
            .GroupBy(mapping => mapping.Evidence.CaseId, StringComparer.Ordinal)
            .Where(group => group.Count() > 1)
            .Select(group => $"{group.Key}: {string.Join(", ", group.Select(mapping => mapping.Key))}")
            .ToList();
        Assert.True(collisions.Count == 0, $"Duplicate config-row case ids: {string.Join(" | ", collisions)}");

        Assert.All(
            baseline.ConfigKeyMappings,
            mapping => Assert.Matches("^CFG-[0-9]{3}[A-Z]?$", mapping.Evidence.CaseId));
    }

    [Fact]
    public void AlignedConfigKeys_ShouldDeclareConcreteRuntimeLocators()
    {
        var baseline = BaselineTestSupport.LoadBaseline();

        var unsupportedAligned = baseline.ConfigKeyMappings
            .Where(mapping => string.Equals(mapping.ParityStatus, "Aligned", StringComparison.Ordinal))
            .Where(mapping => string.IsNullOrWhiteSpace(mapping.MappingTarget)
                              || string.IsNullOrWhiteSpace(mapping.ReadPath)
                              || string.IsNullOrWhiteSpace(mapping.WritePath))
            .Select(mapping => mapping.Key)
            .ToList();

        Assert.Empty(unsupportedAligned);
        Assert.All(
            baseline.ConfigKeyMappings.Where(mapping => mapping.ParityStatus == "Aligned"),
            mapping =>
            {
                AssertRuntimeLocator(mapping.Key, mapping.MappingTarget, mapping.ReadPath!);
                AssertRuntimeLocator(mapping.Key, mapping.MappingTarget, mapping.WritePath!);
            });
        Assert.Equal(67, baseline.ConfigKeyMappings.Count(mapping => mapping.ParityStatus == "Aligned"));
        Assert.Equal(257, baseline.ConfigKeyMappings.Count(mapping => mapping.ParityStatus == "Gap"));
    }

    [Fact]
    public void AlignedBaselineItems_ShouldNotOwnUnexplainedFrozenWpfConfigGaps()
    {
        var baseline = BaselineTestSupport.LoadBaseline();

        var alignedItemIds = baseline.Items
            .Where(item => string.Equals(item.ParityStatus, "Aligned", StringComparison.Ordinal) && item.Waiver is null)
            .Select(item => item.ItemId)
            .ToHashSet(StringComparer.Ordinal);

        // M01-R3-02: a feature or system row may only claim Aligned when every frozen-WPF P0
        // configuration key it owns is itself Aligned (or covered by an explicit scoped waiver).
        // Avalonia extension keys are excluded: they have no frozen WPF counterpart, so their Gap
        // rows cannot contradict a frozen-WPF parity claim.
        var offenders = baseline.ConfigKeyMappings
            .Where(mapping => alignedItemIds.Contains(mapping.OwnerItemId))
            .Where(mapping => string.Equals(mapping.ParityStatus, "Gap", StringComparison.Ordinal) && mapping.Waiver is null)
            .Where(mapping => !BaselineTestSupport.AvaloniaExtensionConfigKeys.Contains(mapping.Key))
            .Select(mapping => $"{mapping.OwnerItemId}:{mapping.Key}")
            .OrderBy(entry => entry, StringComparer.Ordinal)
            .ToList();

        Assert.Empty(offenders);
    }

    [Fact]
    public void ConfigKeysWithoutRuntimeConsumers_ShouldRemainExplicitGaps()
    {
        var baseline = BaselineTestSupport.LoadBaseline();
        var mappings = baseline.ConfigKeyMappings.ToDictionary(mapping => mapping.Key, StringComparer.Ordinal);
        var keysWithoutRuntimeConsumers = new[]
        {
            "Data",
            "Cron",
            "TimeOut.Timer.TaskTimeoutMinutes",
            "Bluestacks.Config.Path",
            "Bluestacks.Config.Keyword",
            "Bluestacks.Config.Error",
            "Debug.TaskName",
        };

        Assert.All(keysWithoutRuntimeConsumers, key =>
        {
            var mapping = mappings[key];
            Assert.Equal("Gap", mapping.ParityStatus);
            Assert.Null(mapping.MappingTarget);
            Assert.Null(mapping.ReadPath);
            Assert.Null(mapping.WritePath);
            Assert.False(string.IsNullOrWhiteSpace(mapping.Notes));
        });
    }

    [Fact]
    public void BaselineFallback_ShouldCover5CapabilitiesAcross3Platforms()
    {
        var baseline = BaselineTestSupport.LoadBaseline();

        var expectedCapabilities = new HashSet<string>(StringComparer.Ordinal)
        {
            "Tray",
            "Notification",
            "Hotkey",
            "Autostart",
            "Overlay",
        };

        var expectedPlatforms = new HashSet<string>(StringComparer.Ordinal)
        {
            "windows",
            "macos",
            "linux",
        };

        var capabilities = baseline.FallbackCapabilities
            .Select(f => f.CapabilityId)
            .ToHashSet(StringComparer.Ordinal);

        Assert.True(expectedCapabilities.SetEquals(capabilities));

        foreach (var capability in expectedCapabilities)
        {
            var platforms = baseline.FallbackCapabilities
                .Where(f => f.CapabilityId == capability)
                .Select(f => f.Platform)
                .ToHashSet(StringComparer.Ordinal);

            Assert.True(expectedPlatforms.SetEquals(platforms),
                $"Capability `{capability}` platform coverage mismatch: {string.Join(", ", platforms)}");
        }

        Assert.Equal(15, baseline.FallbackCapabilities.Count);
    }

    [Fact]
    public void BaselineLocaleTheme_ShouldCoverRequiredMatrix()
    {
        var baseline = BaselineTestSupport.LoadBaseline();

        var expectedThemes = new[] { "Light", "Dark" };
        var expectedLocales = new[] { "zh-cn", "zh-tw", "en-us", "ja-jp", "ko-kr", "pallas" };

        Assert.Equal(expectedThemes, baseline.Themes);
        Assert.Equal(expectedLocales, baseline.Locales);
    }

    [Fact]
    public void AcceptanceTemplate_ShouldReferenceExistingBaselineItems()
    {
        var baseline = BaselineTestSupport.LoadBaseline();
        var acceptance = BaselineTestSupport.LoadAcceptanceTemplate();

        var itemIds = baseline.Items.Select(i => i.ItemId).ToHashSet(StringComparer.Ordinal);

        var duplicateCaseIds = acceptance.Cases.GroupBy(c => c.CaseId).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.Empty(duplicateCaseIds);

        Assert.All(acceptance.Cases, testCase =>
        {
            Assert.True(itemIds.Contains(testCase.ItemId), $"Case `{testCase.CaseId}` references unknown item `{testCase.ItemId}`.");
            Assert.NotEmpty(testCase.Platforms);
            Assert.NotEmpty(testCase.Themes);
            Assert.NotEmpty(testCase.Locales);
            Assert.NotEmpty(testCase.Steps);
            Assert.NotEmpty(testCase.Expected);
            Assert.True(testCase.NonCrashRequired);
        });

        var covered = acceptance.Cases.Select(c => c.ItemId).ToHashSet(StringComparer.Ordinal);
        Assert.True(itemIds.SetEquals(covered),
            $"Acceptance case coverage mismatch. Missing: {string.Join(", ", itemIds.Except(covered))}");
    }

    // Whole-config persistence boundary: AvaloniaJsonConfigStore.SaveAsync atomically serializes the
    // entire UnifiedConfig document, so no per-key token appears in its body. Rows persisted through
    // it are covered by the configuration store round-trip tests (ConfigurationImportTests); every
    // other locator must resolve to a real method body that references the mapped key, either directly,
    // through one delegation hop, or through a keyed call site (M01-R3-03).
    private static readonly HashSet<string> WholeConfigPersistenceLocators = new(StringComparer.Ordinal)
    {
        "Application/Configuration/AvaloniaJsonConfigStore.cs::SaveAsync",
    };

    private static readonly ConcurrentDictionary<(string Path, string Method), IReadOnlyList<string>> MethodBodyCache = new();
    private static readonly ConcurrentDictionary<string, string> TypeDeclarationFileCache = new(StringComparer.Ordinal);

    private static void AssertRuntimeLocator(string key, string? mappingTarget, string locator)
    {
        var parts = locator.Split("::", 2, StringSplitOptions.None);
        var root = BaselineTestSupport.GetMaaUnifiedRoot();
        var path = Path.GetFullPath(Path.Combine(root, parts[0]));
        Assert.StartsWith(root, path, StringComparison.Ordinal);
        Assert.True(File.Exists(path), $"Runtime locator for `{key}` does not exist: {locator}");

        var source = File.ReadAllText(path);
        var tokens = GetLocatorTokens(key, mappingTarget);

        if (parts.Length == 1)
        {
            Assert.True(
                ContainsAnyToken(source, tokens),
                $"File locator for `{key}` must reference the mapped key or target: {locator}");
            return;
        }

        var method = parts[1];
        var bodies = ExtractMethodBodies(path, method);
        Assert.True(
            bodies.Count > 0,
            $"Runtime locator for `{key}` does not name a method declaration: {locator}");

        if (WholeConfigPersistenceLocators.Contains(locator))
        {
            return;
        }

        if (bodies.Any(body => ContainsAnyToken(body, tokens)))
        {
            return;
        }

        // One delegation hop: the located method may call a same-file helper (for example
        // PersistBridgeSettingsForToolAsync -> BuildBridgeUpdates) or a member of the mapping
        // target's declaring type (for example SavePolicyAsync -> AchievementPolicy.ToProfileSettingUpdates).
        var calleeNames = ExtractCalledMethodNames(bodies);
        var hopBodies = new List<string>();
        foreach (var callee in calleeNames)
        {
            if (!string.Equals(callee, method, StringComparison.Ordinal))
            {
                hopBodies.AddRange(ExtractMethodBodies(path, callee));
            }
        }

        var targetTypeFile = FindTypeDeclarationFile(root, GetMappingTargetRootType(mappingTarget));
        if (targetTypeFile is not null && !string.Equals(targetTypeFile, path, StringComparison.Ordinal))
        {
            foreach (var callee in calleeNames)
            {
                hopBodies.AddRange(ExtractMethodBodies(targetTypeFile, callee));
            }
        }

        if (hopBodies.Any(body => ContainsAnyToken(body, tokens)))
        {
            return;
        }

        // Generic keyed persistence helpers (for example PersistGlobalSettingCoreAsync(key, value))
        // touch the key at their call sites: require a direct call or a same-file wrapper call that
        // passes the mapped ConfigurationKeys constant.
        if (BaselineTestSupport.LegacyConfigurationKeyConstantNames.TryGetValue(key, out var constantName)
            && HasKeyedCallSite(source, path, method, constantName))
        {
            return;
        }

        Assert.Fail($"Runtime locator for `{key}` does not resolve to code that references the key: {locator}");
    }

    private static IReadOnlyList<string> GetLocatorTokens(string key, string? mappingTarget)
    {
        var tokens = new List<string> { key };
        if (BaselineTestSupport.LegacyConfigurationKeyConstantNames.TryGetValue(key, out var constantName))
        {
            tokens.Add(constantName);
        }

        if (!string.IsNullOrWhiteSpace(mappingTarget))
        {
            var member = mappingTarget.Split('.')[^1];
            var bracket = member.IndexOfAny(['[', '(']);
            if (bracket >= 0)
            {
                member = member[..bracket];
            }

            tokens.Add(member);
        }

        return tokens.Where(token => token.Length > 2).Distinct(StringComparer.Ordinal).ToList();
    }

    private static bool ContainsAnyToken(string text, IReadOnlyList<string> tokens)
    {
        return tokens.Any(token => text.Contains(token, StringComparison.Ordinal));
    }

    private static IReadOnlyList<string> ExtractMethodBodies(string path, string method)
    {
        return MethodBodyCache.GetOrAdd((path, method), static key => ExtractMethodBodiesUncached(key.Path, key.Method));
    }

    private static IReadOnlyList<string> ExtractMethodBodiesUncached(string path, string method)
    {
        var source = File.ReadAllText(path);
        var bodies = new List<string>();
        var declaration = new Regex(
            @"(?:public|private|internal|protected)[\w\s<>\[\],.?]*?\b" + Regex.Escape(method) + @"\s*\(",
            RegexOptions.CultureInvariant);

        foreach (Match match in declaration.Matches(source))
        {
            var openParen = match.Index + match.Length - 1;
            var depth = 0;
            var index = openParen;
            while (index < source.Length)
            {
                var c = source[index];
                if (c == '(')
                {
                    depth++;
                }
                else if (c == ')')
                {
                    depth--;
                    if (depth == 0)
                    {
                        break;
                    }
                }

                index++;
            }

            if (index >= source.Length)
            {
                continue;
            }

            var bodyStart = index + 1;
            while (bodyStart < source.Length && char.IsWhiteSpace(source[bodyStart]))
            {
                bodyStart++;
            }

            if (bodyStart >= source.Length || source[bodyStart] != '{')
            {
                continue;
            }

            depth = 0;
            var bodyEnd = bodyStart;
            while (bodyEnd < source.Length)
            {
                var c = source[bodyEnd];
                if (c == '{')
                {
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        break;
                    }
                }

                bodyEnd++;
            }

            if (bodyEnd >= source.Length)
            {
                continue;
            }

            bodies.Add(source.Substring(bodyStart, bodyEnd - bodyStart + 1));
        }

        return bodies;
    }

    private static IReadOnlyList<string> ExtractCalledMethodNames(IEnumerable<string> bodies)
    {
        var callPattern = new Regex(@"\b(\w+)\s*\(", RegexOptions.CultureInvariant);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var body in bodies)
        {
            foreach (Match match in callPattern.Matches(body))
            {
                names.Add(match.Groups[1].Value);
            }
        }

        return names.ToList();
    }

    private static string? GetMappingTargetRootType(string? mappingTarget)
    {
        if (string.IsNullOrWhiteSpace(mappingTarget))
        {
            return null;
        }

        var root = mappingTarget.Split('.')[0];
        var bracket = root.IndexOfAny(['[', '(']);
        if (bracket >= 0)
        {
            root = root[..bracket];
        }

        return root.Length > 0 ? root : null;
    }

    private static string? FindTypeDeclarationFile(string root, string? typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
        {
            return null;
        }

        var cached = TypeDeclarationFileCache.GetOrAdd(typeName, name => FindTypeDeclarationFileUncached(root, name) ?? string.Empty);
        return cached.Length == 0 ? null : cached;
    }

    private static string? FindTypeDeclarationFileUncached(string root, string typeName)
    {
        var declaration = new Regex(
            @"\b(?:class|record|struct|interface)\s+" + Regex.Escape(typeName) + @"\b",
            RegexOptions.CultureInvariant);
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

                if (declaration.IsMatch(File.ReadAllText(file)))
                {
                    return file;
                }
            }
        }

        return null;
    }

    private static bool HasKeyedCallSite(string source, string path, string method, string constantName)
    {
        var constantPattern = @"(?:\w+\.)*\w*ConfigurationKeys\." + Regex.Escape(constantName) + @"\b";
        var directCall = new Regex(@"\b" + Regex.Escape(method) + @"\s*\(\s*(?:this\.)?" + constantPattern, RegexOptions.CultureInvariant);
        if (directCall.IsMatch(source))
        {
            return true;
        }

        // One-hop wrappers: methods declared in the same file whose body invokes the located method.
        var declaredMethods = new Regex(
            @"(?:public|private|internal|protected)[\w\s<>\[\],.?]*?\b(\w+)\s*\(",
            RegexOptions.CultureInvariant);
        foreach (Match match in declaredMethods.Matches(source))
        {
            var wrapper = match.Groups[1].Value;
            if (string.Equals(wrapper, method, StringComparison.Ordinal))
            {
                continue;
            }

            var wrapperBodies = ExtractMethodBodies(path, wrapper);
            if (!wrapperBodies.Any(body => Regex.IsMatch(body, @"\b" + Regex.Escape(method) + @"\s*\(")))
            {
                continue;
            }

            var wrapperCall = new Regex(@"\b" + Regex.Escape(wrapper) + @"\s*\(\s*(?:this\.)?" + constantPattern, RegexOptions.CultureInvariant);
            if (wrapperCall.IsMatch(source))
            {
                return true;
            }
        }

        return false;
    }
}
