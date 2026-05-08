using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace MAAUnified.App.Features.Advanced;

public partial class ToolboxView : UserControl
{
    private const int PeepTabIndex = 4;

    public ToolboxView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => SyncToolboxContentScrollMode();
    }

    private void OnToolboxTabSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        SyncToolboxContentScrollMode();
    }

    private void SyncToolboxContentScrollMode()
    {
        if (VisualRoot is null)
        {
            return;
        }

        var scrollViewer = ToolboxTabs
            .GetVisualDescendants()
            .OfType<ScrollViewer>()
            .FirstOrDefault(viewer => viewer.Classes.Contains("toolbox-nav-content-scroll"));
        if (scrollViewer is null)
        {
            return;
        }

        scrollViewer.VerticalScrollBarVisibility = ToolboxTabs.SelectedIndex == PeepTabIndex
            ? ScrollBarVisibility.Disabled
            : ScrollBarVisibility.Auto;
    }
}
