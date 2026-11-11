using System.Text.Json.Nodes;
using MAAUnified.Application.Configuration;
using MAAUnified.Application.Models;
using MAAUnified.Compat.Constants;

namespace MAAUnified.Application.Services.Localization;

public interface IUiLanguageCoordinator
{
    string CurrentLanguage { get; }

    /// <summary>
    /// Gets a value indicating whether the Pallas easter-egg language is unlocked (WPF <c>GUI.Cheers</c>).
    /// </summary>
    bool IsPallasUnlocked { get; }

    event EventHandler<UiLanguageChangedEventArgs>? LanguageChanged;

    Task<UiOperationResult<string>> ChangeLanguageAsync(string targetLanguage, CancellationToken cancellationToken = default);

    /// <summary>
    /// Completes a pending Pallas hangover (WPF <c>HangoverEnd</c>): clears <c>GUI.Hangover</c> once at startup.
    /// </summary>
    /// <returns>True when a pending hangover was completed and the caller should surface the hangover notice.</returns>
    Task<bool> CompleteHangoverIfNeededAsync(CancellationToken cancellationToken = default);
}

public sealed class UiLanguageChangedEventArgs : EventArgs
{
    public UiLanguageChangedEventArgs(string previousLanguage, string currentLanguage)
    {
        PreviousLanguage = UiLanguageCatalog.Normalize(previousLanguage);
        CurrentLanguage = UiLanguageCatalog.Normalize(currentLanguage);
    }

    public string PreviousLanguage { get; }

    public string CurrentLanguage { get; }
}

public sealed class UiLanguageCoordinator : IUiLanguageCoordinator
{
    private const string PallasLanguage = "pallas";

    private readonly UnifiedConfigurationService _configurationService;
    private readonly SemaphoreSlim _changeSemaphore = new(1, 1);
    private string _currentLanguage;

    public UiLanguageCoordinator(UnifiedConfigurationService configurationService)
    {
        _configurationService = configurationService ?? throw new ArgumentNullException(nameof(configurationService));
        _currentLanguage = ReadPersistedLanguage(configurationService.CurrentConfig);
    }

    public string CurrentLanguage => _currentLanguage;

    public bool IsPallasUnlocked => ReadGlobalBool(_configurationService.CurrentConfig, ConfigurationKeys.Cheers, false);

    public event EventHandler<UiLanguageChangedEventArgs>? LanguageChanged;

    public async Task<UiOperationResult<string>> ChangeLanguageAsync(string targetLanguage, CancellationToken cancellationToken = default)
    {
        var normalizedTarget = UiLanguageCatalog.Normalize(targetLanguage);
        await _changeSemaphore.WaitAsync(cancellationToken);
        try
        {
            var previousLanguage = _currentLanguage;
            if (string.Equals(previousLanguage, normalizedTarget, StringComparison.OrdinalIgnoreCase))
            {
                return UiOperationResult<string>.Ok(previousLanguage, $"Language already set to {previousLanguage}.");
            }

            var config = _configurationService.CurrentConfig;
            var persistedLanguage = ReadPersistedLanguage(config);
            if (string.Equals(persistedLanguage, normalizedTarget, StringComparison.OrdinalIgnoreCase))
            {
                _currentLanguage = normalizedTarget;
                LanguageChanged?.Invoke(this, new UiLanguageChangedEventArgs(previousLanguage, normalizedTarget));
                return UiOperationResult<string>.Ok(normalizedTarget, $"Language synchronized to persisted setting {normalizedTarget}.");
            }

            // WPF parity (GuiSettingsUserControlModel.Language): leaving Pallas marks a hangover and
            // ends Cheers; any non-Pallas choice is remembered as the sober language. Persist the whole
            // transition atomically with the language change and roll every touched key back on failure.
            var rollbackKeys = new List<string> { ConfigurationKeys.Localization };
            var rollbackNodes = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
            if (!IsPallas(normalizedTarget))
            {
                rollbackKeys.Add(ConfigurationKeys.SoberLanguage);
                if (IsPallas(previousLanguage))
                {
                    rollbackKeys.Add(ConfigurationKeys.Hangover);
                    rollbackKeys.Add(ConfigurationKeys.Cheers);
                }
            }

            foreach (var key in rollbackKeys)
            {
                config.GlobalValues.TryGetValue(key, out var node);
                rollbackNodes[key] = node?.DeepClone();
            }

            try
            {
                config.GlobalValues[ConfigurationKeys.Localization] = JsonValue.Create(normalizedTarget);
                if (!IsPallas(normalizedTarget))
                {
                    config.GlobalValues[ConfigurationKeys.SoberLanguage] = JsonValue.Create(normalizedTarget);
                    if (IsPallas(previousLanguage))
                    {
                        config.GlobalValues[ConfigurationKeys.Hangover] = JsonValue.Create(true);
                        config.GlobalValues[ConfigurationKeys.Cheers] = JsonValue.Create(false);
                    }
                }

                await _configurationService.SaveAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                foreach (var key in rollbackKeys)
                {
                    if (rollbackNodes[key] is not null)
                    {
                        config.GlobalValues[key] = rollbackNodes[key]!.DeepClone();
                    }
                    else
                    {
                        config.GlobalValues.Remove(key);
                    }
                }

                _configurationService.RevalidateCurrentConfig(logIssues: false);
                return UiOperationResult<string>.Fail(
                    UiErrorCode.SettingsSaveFailed,
                    $"Failed to save language setting: {ex.Message}",
                    ex.ToString());
            }

            _currentLanguage = normalizedTarget;
            LanguageChanged?.Invoke(this, new UiLanguageChangedEventArgs(previousLanguage, normalizedTarget));
            return UiOperationResult<string>.Ok(normalizedTarget, $"Language switched to {normalizedTarget}.");
        }
        finally
        {
            _changeSemaphore.Release();
        }
    }

    public async Task<bool> CompleteHangoverIfNeededAsync(CancellationToken cancellationToken = default)
    {
        await _changeSemaphore.WaitAsync(cancellationToken);
        try
        {
            var config = _configurationService.CurrentConfig;
            if (!ReadGlobalBool(config, ConfigurationKeys.Hangover, false))
            {
                return false;
            }

            var hadPreviousNode = config.GlobalValues.TryGetValue(ConfigurationKeys.Hangover, out var previousNode);
            try
            {
                config.GlobalValues[ConfigurationKeys.Hangover] = JsonValue.Create(false);
                await _configurationService.SaveAsync(cancellationToken);
                return true;
            }
            catch
            {
                if (hadPreviousNode)
                {
                    config.GlobalValues[ConfigurationKeys.Hangover] = previousNode?.DeepClone();
                }
                else
                {
                    config.GlobalValues.Remove(ConfigurationKeys.Hangover);
                }

                _configurationService.RevalidateCurrentConfig(logIssues: false);
                throw;
            }
        }
        finally
        {
            _changeSemaphore.Release();
        }
    }

    /// <summary>
    /// Mirrors the frozen WPF Bootstrapper.Release -> SettingsViewModel.Sober flow: when the app
    /// exits while drunk (Cheers + pallas), restore the sober language so the next launch starts
    /// sober. Concrete-class API so test fakes of <see cref="IUiLanguageCoordinator"/> stay valid.
    /// </summary>
    public async Task SoberOnExitAsync(CancellationToken cancellationToken = default)
    {
        await _changeSemaphore.WaitAsync(cancellationToken);
        try
        {
            var config = _configurationService.CurrentConfig;
            if (!PallasLanguageState.ApplySoberOnExit(config))
            {
                return;
            }

            await _configurationService.SaveAsync(cancellationToken);
            var restored = ReadPersistedLanguage(config);
            _currentLanguage = restored;
        }
        finally
        {
            _changeSemaphore.Release();
        }
    }

    /// <summary>
    /// Mirrors the frozen WPF AsstProxy all-tasks-complete wine trigger: records the purchase day,
    /// unlocks Cheers and switches the UI language to pallas. Avalonia applies the language live,
    /// so no WPF-style restart prompt is needed. Concrete-class API so test fakes stay valid.
    /// </summary>
    public async Task<UiOperationResult<string>> DrinkWineAsync(string? clientType, CancellationToken cancellationToken = default)
    {
        var config = _configurationService.CurrentConfig;
        PallasLanguageState.MarkWineBought(config, clientType, DateTime.UtcNow);
        config.GlobalValues[ConfigurationKeys.Cheers] = JsonValue.Create(true);
        await _configurationService.SaveAsync(cancellationToken);
        return await ChangeLanguageAsync(UiLanguageCatalog.PallasLanguage, cancellationToken);
    }

    private static bool IsPallas(string language)
    {
        return string.Equals(language, PallasLanguage, StringComparison.OrdinalIgnoreCase);
    }

    private static bool ReadGlobalBool(UnifiedConfig config, string key, bool fallback)
    {
        if (!config.GlobalValues.TryGetValue(key, out var node) || node is null)
        {
            return fallback;
        }

        if (node is JsonValue value)
        {
            if (value.TryGetValue(out bool boolValue))
            {
                return boolValue;
            }

            if (value.TryGetValue(out string? text) && bool.TryParse(text, out var parsed))
            {
                return parsed;
            }
        }

        return fallback;
    }

    private static string ReadPersistedLanguage(UnifiedConfig config)
    {
        if (config.GlobalValues.TryGetValue(ConfigurationKeys.Localization, out var node)
            && node is JsonValue value
            && value.TryGetValue(out string? language)
            && !string.IsNullOrWhiteSpace(language))
        {
            return UiLanguageCatalog.Normalize(language);
        }

        return UiLanguageCatalog.DefaultLanguage;
    }
}
