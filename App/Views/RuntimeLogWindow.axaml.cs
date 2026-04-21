using Avalonia.Controls;
using Avalonia.Interactivity;
using MAAUnified.App.Infrastructure;

namespace MAAUnified.App.Views;

public partial class RuntimeLogWindow : Window
{
    public RuntimeLogWindow()
    {
        InitializeComponent();
        WindowVisuals.ApplyDefaultIcon(this);
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnShellCloseRequested(object? sender, EventArgs e)
    {
        Close();
    }
}
