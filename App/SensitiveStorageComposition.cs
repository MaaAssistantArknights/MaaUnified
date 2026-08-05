using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Threading;
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

    public static SensitiveSettingsService Create(string runtimeBaseDirectory)
    {
        var backends = new List<ISensitiveSettingsBackend>
        {
            new MsalSystemStorageBackend(runtimeBaseDirectory),
            new PlaintextSensitiveSettingsBackend(runtimeBaseDirectory),
        };
#if MAA_UNIFIED_LINUX_SECURE_STORAGE
        backends.Add(new MAAUnified.SecureStorage.Linux.SqlCipherStorageBackend(runtimeBaseDirectory));
#endif
        return new SensitiveSettingsService(backends, new AvaloniaPlaintextConsent());
    }

    internal sealed class AvaloniaPlaintextConsent : IPlaintextSensitiveStorageConsent
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

        private static async Task<bool> ShowDialogAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop
                || desktop.MainWindow is not { } owner)
            {
                return false;
            }

            var dialog = new Window
            {
                Title = "Sensitive storage warning",
                Width = 520,
                SizeToContent = SizeToContent.Height,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
            };
            var cancel = new Button { Content = "Cancel", MinWidth = 88 };
            var confirm = new Button { Content = "Use plaintext", MinWidth = 116 };
            cancel.Click += (_, _) => dialog.Close(false);
            confirm.Click += (_, _) => dialog.Close(true);
            dialog.Content = new StackPanel
            {
                Margin = new Thickness(20),
                Spacing = 16,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Secure storage is unavailable. Plaintext mode stores sensitive settings in a separate local file. Copying the configuration directory can expose those settings.",
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { cancel, confirm },
                    },
                },
            };
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
