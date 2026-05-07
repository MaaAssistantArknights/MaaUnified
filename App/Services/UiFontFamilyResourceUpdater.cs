using Avalonia.Controls;
using Avalonia.Threading;
using MAAUnified.Application.Services.Localization;

namespace MAAUnified.App.Services;

public sealed class UiFontFamilyResourceUpdater : IDisposable
{
    private readonly IResourceDictionary _resources;
    private readonly IUiLanguageCoordinator _languageCoordinator;
    private readonly UiFontFamilyResolver _resolver;
    private readonly Action<UiFontFamilyResolution> _recordDiagnostics;
    private readonly HashSet<string> _reportedDiagnostics = new(StringComparer.Ordinal);
    private bool _disposed;

    public UiFontFamilyResourceUpdater(
        IResourceDictionary resources,
        IUiLanguageCoordinator languageCoordinator,
        UiFontFamilyResolver resolver,
        Action<UiFontFamilyResolution> recordDiagnostics)
    {
        _resources = resources ?? throw new ArgumentNullException(nameof(resources));
        _languageCoordinator = languageCoordinator ?? throw new ArgumentNullException(nameof(languageCoordinator));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _recordDiagnostics = recordDiagnostics ?? throw new ArgumentNullException(nameof(recordDiagnostics));
        _languageCoordinator.LanguageChanged += OnLanguageChanged;
    }

    public UiFontFamilyResolution ApplyLanguage(string language)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var resolution = _resolver.Resolve(language);
        _resources[UiFontFamilyResolver.ResourceKey] = resolution.FontFamily;
        RecordDiagnosticsOnce(resolution);
        return resolution;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _languageCoordinator.LanguageChanged -= OnLanguageChanged;
        _disposed = true;
    }

    private void OnLanguageChanged(object? sender, UiLanguageChangedEventArgs e)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            ApplyLanguage(e.CurrentLanguage);
            return;
        }

        Dispatcher.UIThread.Post(
            () =>
            {
                if (!_disposed)
                {
                    ApplyLanguage(e.CurrentLanguage);
                }
            },
            DispatcherPriority.Send);
    }

    private void RecordDiagnosticsOnce(UiFontFamilyResolution resolution)
    {
        if (!resolution.RequiresDiagnostics || !_reportedDiagnostics.Add(resolution.DiagnosticsSignature))
        {
            return;
        }

        _recordDiagnostics(resolution);
    }
}
