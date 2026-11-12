using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MAAUnified.App.Services.PixelPaint;
using MAAUnified.App.ViewModels.Toolbox;
using MAAUnified.Application.Models;
using MAAUnified.Application.Services.PixelPaint;

namespace MAAUnified.App.Features.Advanced;

public partial class ToolboxPixelPaintView : UserControl
{
    /// <summary>像素画支持的图片扩展名，文件对话框过滤器与拖放/剪贴板文件判断共用。</summary>
    private static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg", ".bmp", ".webp", ".gif"];

    private Point? _dragStart;

    public ToolboxPixelPaintView()
    {
        InitializeComponent();
    }

    private ToolboxPageViewModel? VM => DataContext as ToolboxPageViewModel;

    private async void OnPickImageClick(object? sender, RoutedEventArgs e)
    {
        var vm = VM;
        if (vm is null || !vm.PixelPaint.CanEdit)
        {
            return;
        }

        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanOpen: true } storageProvider)
        {
            return;
        }

        var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = vm.PixelPaint.PickImageText,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Image")
                {
                    Patterns = ImageExtensions.Select(extension => "*" + extension).ToArray(),
                },
            ],
        });
        if (files.Count == 0)
        {
            return;
        }

        var path = files[0].TryGetLocalPath();
        if (!string.IsNullOrEmpty(path))
        {
            vm.PixelPaint.LoadImageFromFile(path);
        }
    }

    private async void OnPasteClick(object? sender, RoutedEventArgs e)
    {
        await PasteFromClipboardAsync();
    }

    /// <summary>
    /// 粘贴：优先剪贴板位图（截图、浏览器 ｢复制图片｣），其次已复制的图片文件，
    /// 最后剪贴板文字（渲染成最多 4 字的点阵图），与 WPF <c>PixelPaintKeyDown</c> 顺序一致。
    /// </summary>
    private async Task PasteFromClipboardAsync()
    {
        var vm = VM;
        if (vm is null || !vm.PixelPaint.CanEdit)
        {
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null)
        {
            return;
        }

        try
        {
            // Avalonia 11.3：IClipboard 只暴露 TryGetDataAsync，位图/文件/文本统一从
            // IAsyncDataTransfer 读取；该实例必须由调用方尽快释放。
            var data = await clipboard.TryGetDataAsync();
            if (data is not null)
            {
                using (data)
                {
                    if (await data.TryGetBitmapAsync() is { } bitmap)
                    {
                        vm.PixelPaint.LoadImage(PixelPaintImaging.FromBitmap(bitmap));
                        return;
                    }

                    if (await data.TryGetFilesAsync() is { Length: > 0 } files
                        && files[0].TryGetLocalPath() is { Length: > 0 } path
                        && ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
                    {
                        vm.PixelPaint.LoadImageFromFile(path);
                        return;
                    }

                    if (await data.TryGetTextAsync() is { } text && !string.IsNullOrWhiteSpace(text))
                    {
                        vm.PixelPaint.LoadImageFromText(text.Trim());
                    }
                }
            }
        }
        catch (Exception)
        {
            vm.PixelPaint.NotifyLoadFailed();
        }
    }

    private void OnPixelPaintDragOver(object? sender, DragEventArgs e)
    {
        var vm = VM;
        e.DragEffects = vm is not null && vm.PixelPaint.CanEdit && e.DataTransfer?.Contains(DataFormat.File) == true
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnPixelPaintDrop(object? sender, DragEventArgs e)
    {
        var vm = VM;
        if (vm is null || !vm.PixelPaint.CanEdit || e.DataTransfer?.TryGetFile()?.TryGetLocalPath() is not { Length: > 0 } path)
        {
            return;
        }

        if (ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
        {
            vm.PixelPaint.LoadImageFromFile(path);
        }

        e.Handled = true;
    }

    private void OnPixelPaintPreviewWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        var vm = VM;
        if (vm is null)
        {
            return;
        }

        // 滚轮缩放取景：向上放大（缩小 view），向下缩小（放大 view）
        var factor = e.Delta.Y > 0 ? 0.9 : 1.0 / 0.9;
        vm.PixelPaint.Zoom(factor);
        e.Handled = true;
    }

    private void OnPixelPaintPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var vm = VM;
        if (vm is null || !vm.PixelPaint.CanEdit || sender is not Control control)
        {
            return;
        }

        _dragStart = e.GetPosition(control);
        e.Pointer.Capture(control);
        e.Handled = true;
    }

    private void OnPixelPaintPreviewPointerMoved(object? sender, PointerEventArgs e)
    {
        var vm = VM;
        if (vm is null || _dragStart is not { } start || sender is not Control control)
        {
            return;
        }

        var position = e.GetPosition(control);
        var deltaX = (position.X - start.X) / Math.Max(1.0, control.Bounds.Width);
        var deltaY = (position.Y - start.Y) / Math.Max(1.0, control.Bounds.Height);
        vm.PixelPaint.Pan(deltaX, deltaY);
        _dragStart = position;
        e.Handled = true;
    }

    private void OnPixelPaintPreviewPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_dragStart is not null)
        {
            _dragStart = null;
            e.Pointer.Capture(null);
            e.Handled = true;
        }
    }

    private async void OnPixelPaintCommandClick(object? sender, RoutedEventArgs e)
    {
        var vm = VM;
        if (vm is null)
        {
            return;
        }

        if (vm.IsPixelPaintRunning)
        {
            await vm.StopActiveToolAsync();
            return;
        }

        await vm.StartPixelPaintAsync();
    }

    private void OnPixelPaintCommandPointerEntered(object? sender, PointerEventArgs e)
    {
        VM?.SetToolActionHover(ToolboxToolKind.PixelPaint, hovering: true);
    }

    private void OnPixelPaintCommandPointerExited(object? sender, PointerEventArgs e)
    {
        VM?.SetToolActionHover(ToolboxToolKind.PixelPaint, hovering: false);
    }

    private void OnResetViewClick(object? sender, RoutedEventArgs e)
    {
        VM?.PixelPaint.ResetView();
    }

    private void OnResetParametersClick(object? sender, RoutedEventArgs e)
    {
        VM?.PixelPaint.ResetParameters();
    }
}
