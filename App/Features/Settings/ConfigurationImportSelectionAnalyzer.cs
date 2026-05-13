using System.Globalization;
using System.Text.Json;
using MAAUnified.App.ViewModels.Infrastructure;
using MAAUnified.Application.Configuration;
using MAAUnified.Application.Models;

namespace MAAUnified.App.Features.Settings;

internal static class ConfigurationImportSelectionAnalyzer
{
    private static readonly RootLocalizationTextMap Texts = new("Root.Localization.Settings.ConfigurationImportSelectionAnalyzer");
    private const string AvaloniaConfigFileName = "avalonia.json";
    private const string GuiNewConfigFileName = "gui.new.json";
    private const string GuiConfigFileName = "gui.json";

    public static ConfigurationImportSelectionAnalysis Analyze(
        IEnumerable<string> filePaths,
        Func<string, string>? textResolver = null)
    {
        textResolver ??= ResolveDefaultText;
        var normalized = filePaths
            .Where(static path => !string.IsNullOrWhiteSpace(path))
            .Select(static path => Path.GetFullPath(path.Trim()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (normalized.Length == 0)
        {
            return ConfigurationImportSelectionAnalysis.Invalid(textResolver("Settings.ConfigurationManager.Import.NoFilesSelected"));
        }

        if (normalized.Length > 2)
        {
            return ConfigurationImportSelectionAnalysis.Invalid(textResolver("Settings.ConfigurationManager.Import.TooManyFiles"));
        }

        if (normalized.Length == 1)
        {
            var singlePath = normalized[0];
            var fileName = Path.GetFileName(singlePath);
            if (IsGuiNewFile(fileName))
            {
                return ConfigurationImportSelectionAnalysis.Legacy(singlePath, null, hasInvalidFiles: false);
            }

            if (IsGuiFile(fileName))
            {
                return ConfigurationImportSelectionAnalysis.Legacy(null, singlePath, hasInvalidFiles: false);
            }

            return InspectSingleFile(singlePath, textResolver);
        }

        var guiNewPath = normalized.FirstOrDefault(path => IsGuiNewFile(Path.GetFileName(path)));
        var guiPath = normalized.FirstOrDefault(path => IsGuiFile(Path.GetFileName(path)));
        if (!string.IsNullOrWhiteSpace(guiNewPath) || !string.IsNullOrWhiteSpace(guiPath))
        {
            var invalidCount = normalized.Length
                               - (string.IsNullOrWhiteSpace(guiNewPath) ? 0 : 1)
                               - (string.IsNullOrWhiteSpace(guiPath) ? 0 : 1);
            return ConfigurationImportSelectionAnalysis.Legacy(guiNewPath, guiPath, invalidCount > 0, textResolver);
        }

        if (normalized.Length == 2 && normalized.Any(path => InspectJsonShape(path) == ConfigurationImportJsonShape.UnifiedConfig))
        {
            return ConfigurationImportSelectionAnalysis.Invalid(textResolver("Settings.ConfigurationManager.Import.UnifiedSingleFileOnly"));
        }

        return ConfigurationImportSelectionAnalysis.Invalid(textResolver("Settings.ConfigurationManager.Import.UnrecognizedFiles"));
    }

    public static ConfigurationImportSelectionAnalysis AnalyzeLegacyDirectory(
        string directoryPath,
        Func<string, string>? textResolver = null)
    {
        textResolver ??= ResolveDefaultText;
        if (!TryResolveDirectory(directoryPath, out var directory))
        {
            return ConfigurationImportSelectionAnalysis.Invalid(textResolver("Settings.ConfigurationManager.Import.DirectoryInvalid"));
        }

        var configDirectory = ResolveConfigDirectory(directory);
        var guiNewPath = Path.Combine(configDirectory, GuiNewConfigFileName);
        var guiPath = Path.Combine(configDirectory, GuiConfigFileName);
        return ConfigurationImportSelectionAnalysis.Legacy(
            File.Exists(guiNewPath) ? guiNewPath : null,
            File.Exists(guiPath) ? guiPath : null,
            hasInvalidFiles: false,
            textResolver);
    }

    public static ConfigurationImportSelectionAnalysis AnalyzeUnifiedDirectory(
        string directoryPath,
        Func<string, string>? textResolver = null)
    {
        textResolver ??= ResolveDefaultText;
        if (!TryResolveDirectory(directoryPath, out var directory))
        {
            return ConfigurationImportSelectionAnalysis.Invalid(textResolver("Settings.ConfigurationManager.Import.DirectoryInvalid"));
        }

        var candidates = new[]
        {
            Path.Combine(directory, AvaloniaConfigFileName),
            Path.Combine(directory, "config", AvaloniaConfigFileName),
        };
        var configPath = candidates.FirstOrDefault(File.Exists);
        return string.IsNullOrWhiteSpace(configPath)
            ? ConfigurationImportSelectionAnalysis.Invalid(textResolver("Settings.ConfigurationManager.Import.UnifiedDirectoryMissing"))
            : ConfigurationImportSelectionAnalysis.Unified(configPath);
    }

    private static ConfigurationImportSelectionAnalysis InspectSingleFile(
        string path,
        Func<string, string> textResolver)
    {
        return InspectJsonShape(path) switch
        {
            ConfigurationImportJsonShape.UnifiedConfig => ConfigurationImportSelectionAnalysis.Unified(path),
            ConfigurationImportJsonShape.LegacyConfig => ConfigurationImportSelectionAnalysis.Invalid(
                textResolver("Settings.ConfigurationManager.Import.LegacyFilenameInvalid")),
            _ => ConfigurationImportSelectionAnalysis.Invalid(
                textResolver("Settings.ConfigurationManager.Import.UnrecognizedFiles")),
        };
    }

    internal static string ResolveDefaultText(string key)
    {
        Texts.Language = App.Runtime.UiLanguageCoordinator.CurrentLanguage;
        return Texts[key];
    }

    private static ConfigurationImportJsonShape InspectJsonShape(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return ConfigurationImportJsonShape.Unknown;
            }

            var looksUnified =
                root.TryGetProperty("Profiles", out _)
                && root.TryGetProperty("CurrentProfile", out _);
            if (looksUnified)
            {
                return ConfigurationImportJsonShape.UnifiedConfig;
            }

            var looksLegacy =
                root.TryGetProperty("Configurations", out _)
                || root.TryGetProperty("Current", out _)
                || root.TryGetProperty("GUI", out _)
                || root.TryGetProperty("Global", out _);
            return looksLegacy
                ? ConfigurationImportJsonShape.LegacyConfig
                : ConfigurationImportJsonShape.Unknown;
        }
        catch
        {
            return ConfigurationImportJsonShape.Unknown;
        }
    }

    private static bool IsGuiNewFile(string? fileName)
        => string.Equals(fileName, GuiNewConfigFileName, StringComparison.OrdinalIgnoreCase);

    private static bool IsGuiFile(string? fileName)
        => string.Equals(fileName, GuiConfigFileName, StringComparison.OrdinalIgnoreCase);

    private static bool TryResolveDirectory(string directoryPath, out string directory)
    {
        directory = string.Empty;
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            return false;
        }

        var normalized = Path.GetFullPath(directoryPath.Trim());
        if (!Directory.Exists(normalized))
        {
            return false;
        }

        directory = normalized;
        return true;
    }

    private static string ResolveConfigDirectory(string directory)
    {
        if (string.Equals(Path.GetFileName(directory), "config", StringComparison.OrdinalIgnoreCase))
        {
            return directory;
        }

        var childConfigDirectory = Path.Combine(directory, "config");
        return Directory.Exists(childConfigDirectory) ? childConfigDirectory : directory;
    }
}

internal sealed record ConfigurationImportSelectionAnalysis(
    ConfigurationImportSelectionKind Kind,
    string? UnifiedConfigPath,
    string? GuiNewPath,
    string? GuiPath,
    bool HasInvalidFiles,
    string Message)
{
    public bool HasUsableLegacyFile => !string.IsNullOrWhiteSpace(GuiNewPath) || !string.IsNullOrWhiteSpace(GuiPath);

    public ImportSource LegacyImportSource => (!string.IsNullOrWhiteSpace(GuiNewPath), !string.IsNullOrWhiteSpace(GuiPath)) switch
    {
        (true, true) => ImportSource.Auto,
        (true, false) => ImportSource.GuiNewOnly,
        (false, true) => ImportSource.GuiOnly,
        _ => ImportSource.Auto,
    };

    public static ConfigurationImportSelectionAnalysis Unified(string filePath)
        => new(
            ConfigurationImportSelectionKind.UnifiedConfig,
            filePath,
            null,
            null,
            false,
            string.Empty);

    public static ConfigurationImportSelectionAnalysis Legacy(
        string? guiNewPath,
        string? guiPath,
        bool hasInvalidFiles,
        Func<string, string>? textResolver = null)
    {
        textResolver ??= ConfigurationImportSelectionAnalyzer.ResolveDefaultText;
        var missingParts = new List<string>();
        if (string.IsNullOrWhiteSpace(guiNewPath))
        {
            missingParts.Add("gui.new.json");
        }

        if (string.IsNullOrWhiteSpace(guiPath))
        {
            missingParts.Add("gui.json");
        }

        var message = missingParts.Count > 0
            ? string.Format(
                CultureInfo.CurrentCulture,
                textResolver("Settings.ConfigurationManager.Import.MissingLegacyParts"),
                string.Join(textResolver("Settings.Common.JoinAnd"), missingParts))
            : string.Empty;
        if (hasInvalidFiles)
        {
            message = string.IsNullOrWhiteSpace(message)
                ? textResolver("Settings.ConfigurationManager.Import.LegacyInvalidFilesOnly")
                : string.Format(
                    CultureInfo.CurrentCulture,
                    textResolver("Settings.ConfigurationManager.Import.LegacyInvalidFilesWithMissing"),
                    message);
        }

        return new ConfigurationImportSelectionAnalysis(
            missingParts.Count == 0 && !hasInvalidFiles
                ? ConfigurationImportSelectionKind.LegacyReady
                : ConfigurationImportSelectionKind.LegacyPartial,
            null,
            guiNewPath,
            guiPath,
            hasInvalidFiles,
            message);
    }

    public static ConfigurationImportSelectionAnalysis Invalid(string message)
        => new(
            ConfigurationImportSelectionKind.Invalid,
            null,
            null,
            null,
            false,
            message);
}

internal enum ConfigurationImportSelectionKind
{
    UnifiedConfig = 0,
    LegacyReady = 1,
    LegacyPartial = 2,
    Invalid = 3,
}

internal enum ConfigurationImportJsonShape
{
    Unknown = 0,
    UnifiedConfig = 1,
    LegacyConfig = 2,
}
