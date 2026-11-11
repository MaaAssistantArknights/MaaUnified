using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Threading;
using MAAUnified.Application.Services.Localization;
using MAAUnified.Application.Security;
using MAAUnified.SecureStorage.System;

namespace MAAUnified.App;

internal static class SensitiveStorageComposition
{
#if MAA_UNIFIED_LINUX_SECURE_STORAGE
    internal const string SqlCipherSmokeTestArgument = "--sensitive-storage-sqlcipher-smoke-test";

    public static bool IsSqlCipherSmokeTest(string[] args)
        => args.Length == 3 && string.Equals(args[0], SqlCipherSmokeTestArgument, StringComparison.Ordinal);

    public static int RunSqlCipherSmokeTest(string runtimeBaseDirectory, string xdgDataHome)
    {
        try
        {
            var backend = new MAAUnified.SecureStorage.Linux.SqlCipherStorageBackend(
                runtimeBaseDirectory,
                new MAAUnified.SecureStorage.Linux.LinuxMasterKeyStore(xdgDataHome));
            var cipherVersion = backend.GetCipherVersionAsync().GetAwaiter().GetResult();
            if (string.IsNullOrWhiteSpace(cipherVersion))
            {
                Console.Error.WriteLine("SQLCipher smoke test returned an empty cipher_version.");
                return 1;
            }

            Console.WriteLine($"cipher_version={cipherVersion}");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"SQLCipher smoke test failed: {ex.GetType().Name}: {ex.Message}");
            return 1;
        }
    }
#endif

    public static SensitiveSettingsService Create(
        string runtimeBaseDirectory,
        Func<string?>? languageProvider = null)
    {
        var backends = new List<ISensitiveSettingsBackend>
        {
            new MsalSystemStorageBackend(runtimeBaseDirectory),
            new PlaintextSensitiveSettingsBackend(runtimeBaseDirectory),
        };
#if MAA_UNIFIED_LINUX_SECURE_STORAGE
        backends.Add(new MAAUnified.SecureStorage.Linux.SqlCipherStorageBackend(runtimeBaseDirectory));
#endif
        return new SensitiveSettingsService(backends, new AvaloniaPlaintextConsent(languageProvider));
    }

    internal sealed record PlaintextConsentDialogTexts(
        string Title,
        string Message,
        string CancelButton,
        string ConfirmButton);

    internal sealed record PlaintextConsentDialogElements(
        Window Dialog,
        TextBlock MessageText,
        Button CancelButton,
        Button ConfirmButton);

    internal sealed class AvaloniaPlaintextConsent(Func<string?>? languageProvider = null)
        : IPlaintextSensitiveStorageConsent
    {
        public async Task<bool> ConfirmAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await RunOnUiThreadAsync(
                    () => ShowDialogAsync(cancellationToken),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                return false;
            }
        }

        internal static async Task<T> RunOnUiThreadAsync<T>(
            Func<Task<T>> action,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(action);
            cancellationToken.ThrowIfCancellationRequested();
            if (Dispatcher.UIThread.CheckAccess())
            {
                return await action();
            }

            return await await Dispatcher.UIThread.InvokeAsync(
                action,
                DispatcherPriority.Normal,
                cancellationToken);
        }

        internal static PlaintextConsentDialogTexts GetDialogTexts(string? language)
        {
            var normalizedLanguage = UiLanguageCatalog.Normalize(language);
            var localizer = UiLocalizer.Create(normalizedLanguage);
            return new PlaintextConsentDialogTexts(
                localizer.GetOrDefault(
                    "Settings.SensitiveStorage.PlaintextConsent.Title",
                    "Sensitive storage warning",
                    "SensitiveStorage.PlaintextConsent"),
                localizer.GetOrDefault(
                    "Settings.SensitiveStorage.PlaintextConsent.Message",
                    "System-protected storage is unavailable. Plaintext mode stores sensitive settings, including credentials, unencrypted in a separate local file. Copying or syncing the configuration directory can expose those secrets.",
                    "SensitiveStorage.PlaintextConsent"),
                localizer.GetOrDefault(
                    "Settings.SensitiveStorage.PlaintextConsent.Cancel",
                    "Cancel",
                    "SensitiveStorage.PlaintextConsent"),
                localizer.GetOrDefault(
                    "Settings.SensitiveStorage.PlaintextConsent.UsePlaintext",
                    "Use plaintext",
                    "SensitiveStorage.PlaintextConsent"));
        }

        internal static PlaintextConsentDialogElements CreateDialog(string? language)
        {
            var texts = GetDialogTexts(language);
            var dialog = new Window
            {
                Title = texts.Title,
                Width = 520,
                SizeToContent = SizeToContent.Height,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
            };
            var message = new TextBlock
            {
                Name = "PlaintextConsentMessage",
                Text = texts.Message,
                TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            };
            var cancel = new Button
            {
                Name = "PlaintextConsentCancelButton",
                Content = texts.CancelButton,
                MinWidth = 88,
                IsCancel = true,
                IsDefault = true,
            };
            var confirm = new Button
            {
                Name = "PlaintextConsentConfirmButton",
                Content = texts.ConfirmButton,
                MinWidth = 116,
            };
            cancel.Click += (_, _) => dialog.Close(false);
            confirm.Click += (_, _) => dialog.Close(true);
            dialog.Opened += (_, _) => cancel.Focus();
            dialog.Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 16,
                Children =
                {
                    message,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { cancel, confirm },
                    },
                },
            };
            return new PlaintextConsentDialogElements(dialog, message, cancel, confirm);
        }

        private string ResolveCurrentLanguage()
        {
            var providedLanguage = languageProvider?.Invoke();
            if (!string.IsNullOrWhiteSpace(providedLanguage))
            {
                return providedLanguage.Trim();
            }

            return MAAUnified.App.App.Runtime is { } runtime
                ? runtime.UiLanguageCoordinator.CurrentLanguage
                : UiLanguageCatalog.DefaultLanguage;
        }

        private async Task<bool> ShowDialogAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
                || desktop.MainWindow is not { } owner)
            {
                return false;
            }

            var dialog = CreateDialog(ResolveCurrentLanguage()).Dialog;
            using var cancellationRegistration = cancellationToken.Register(
                static state => Dispatcher.UIThread.Post(
                    () => ((Window)state!).Close(false),
                    DispatcherPriority.Send),
                dialog);
            var result = await dialog.ShowDialog<bool>(owner);
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
    }
}
