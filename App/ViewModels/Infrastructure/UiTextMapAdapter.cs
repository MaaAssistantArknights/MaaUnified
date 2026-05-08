using MAAUnified.Application.Services.Localization;

namespace MAAUnified.App.ViewModels.Infrastructure;

public abstract class UiTextMapAdapter : ObservableObject
{
    private readonly IUiLocalizer _localizer;
    private string _language = UiLanguageCatalog.DefaultLanguage;

    protected UiTextMapAdapter(string scope, IUiLocalizer? localizer = null)
    {
        Scope = string.IsNullOrWhiteSpace(scope) ? "Ui.Localization" : scope;
        _localizer = localizer ?? new UiLocalizer();
    }

    public event Action<LocalizationFallbackInfo>? FallbackReported;

    protected string Scope { get; }

    public string Language
    {
        get => _language;
        set
        {
            var normalized = UiLanguageCatalog.Normalize(value);
            if (!SetProperty(ref _language, normalized))
            {
                return;
            }

            _localizer.Language = normalized;
            OnPropertyChanged(string.Empty);
            OnPropertyChanged("Item");
            OnPropertyChanged("Item[]");
        }
    }

    public string this[string key] => _localizer.GetText(key, Scope, ReportFallback);

    public string GetOrDefault(string key, string fallback)
    {
        return _localizer.GetOrDefault(key, fallback, Scope, ReportFallback);
    }

    public string GetOrDefaultForLanguage(string language, string key, string fallback)
    {
        return _localizer.GetOrDefaultForLanguage(language, key, fallback, Scope, ReportFallback);
    }

    private void ReportFallback(LocalizationFallbackInfo info)
    {
        FallbackReported?.Invoke(info);
    }
}
