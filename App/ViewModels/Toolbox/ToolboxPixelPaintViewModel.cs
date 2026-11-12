using System.Globalization;
using Avalonia.Media.Imaging;
using MAAUnified.App.Services.PixelPaint;
using MAAUnified.App.ViewModels.Infrastructure;
using MAAUnified.Application.Services.Localization;
using MAAUnified.Application.Services.PixelPaint;

namespace MAAUnified.App.ViewModels.Toolbox;

/// <summary>
/// 工具箱 ｢像素画｣ 页签的视图模型：图片/文字导入、构图与滤镜参数、预览取景、提交参数。
/// <para>WPF parity：<c>ToolboxViewModel</c> 的 <c>#region PixelPaint</c> 与
/// <c>PixelPaintUserControl.xaml</c>（dev-v2 <c>1f39c29587</c> 等）。文案在 MAAUnified 侧
/// 统一到 <c>Toolbox.PixelPaint.*</c>（WPF 为 <c>Toolbox.MiniGame.PixelPaint.*</c>），
/// 因为此处是独立页签而非小游戏列表的一项。</para>
/// </summary>
public sealed class ToolboxPixelPaintViewModel : ObservableObject
{
    private const string Scope = "Toolbox.PixelPaint";
    private const int MinFilterPercent = 50;
    private const int MaxFilterPercent = 200;
    private const int MaxGridDelay = PixelPaintTaskPayload.MaxGridDelay;

    private readonly ToolboxLocalizationTextMap _texts;
    private readonly UiLocalizer _optionLocalizer;

    private PixelPaintImage? _prepared;
    private PixelPaintConvertResult? _result;
    private PixelPaintViewRect _view = PixelPaintViewRect.Full;
    private WriteableBitmap? _preview;
    private string _statusText = string.Empty;
    private bool _isRunning;
    private PixelPaintFitMode _fit = PixelPaintFitMode.Crop;
    private PixelPaintDitherMode _dither = PixelPaintDitherMode.Illustration;
    private int _contrast = 100;
    private int _brightness = 100;
    private int _saturation = 100;
    private bool _paintWhite;
    private bool _swipeEnabled = true;
    private int _gridDelay;

    /// <summary>
    /// Initializes a new instance of the <see cref="ToolboxPixelPaintViewModel"/> class.
    /// </summary>
    /// <param name="texts">与工具箱页共享的本地化文本表，语言切换由页视图模型统一驱动。</param>
    public ToolboxPixelPaintViewModel(ToolboxLocalizationTextMap texts)
    {
        _texts = texts ?? throw new ArgumentNullException(nameof(texts));
        _optionLocalizer = new UiLocalizer(texts.Language);
        FitOptions = new LocalizedObservableList<PixelPaintFitMode>(
            _optionLocalizer,
            Scope,
            new LocalizedEntry<PixelPaintFitMode>(PixelPaintFitMode.Crop, "Toolbox.PixelPaint.FitCrop", "Crop"),
            new LocalizedEntry<PixelPaintFitMode>(PixelPaintFitMode.Contain, "Toolbox.PixelPaint.FitContain", "Contain"),
            new LocalizedEntry<PixelPaintFitMode>(PixelPaintFitMode.Stretch, "Toolbox.PixelPaint.FitStretch", "Stretch"));
        DitherOptions = new LocalizedObservableList<PixelPaintDitherMode>(
            _optionLocalizer,
            Scope,
            new LocalizedEntry<PixelPaintDitherMode>(PixelPaintDitherMode.Illustration, "Toolbox.PixelPaint.DitherIllustration", "Illustration"),
            new LocalizedEntry<PixelPaintDitherMode>(PixelPaintDitherMode.None, "Toolbox.PixelPaint.DitherNone", "No dithering"),
            new LocalizedEntry<PixelPaintDitherMode>(PixelPaintDitherMode.FloydSteinberg, "Toolbox.PixelPaint.DitherFS", "Dither - Photo"),
            new LocalizedEntry<PixelPaintDitherMode>(PixelPaintDitherMode.Atkinson, "Toolbox.PixelPaint.DitherAtkinson", "Dither - Soft"));
    }

    /// <summary>Gets 构图方式下拉条目。</summary>
    public LocalizedObservableList<PixelPaintFitMode> FitOptions { get; }

    /// <summary>Gets 抖动方式下拉条目。</summary>
    public LocalizedObservableList<PixelPaintDitherMode> DitherOptions { get; }

    /// <summary>Gets 预览位图；未导入图片时为 <see langword="null"/>。</summary>
    public WriteableBitmap? Preview
    {
        get => _preview;
        private set
        {
            var previous = _preview;
            if (!SetProperty(ref _preview, value))
            {
                return;
            }

            OnPropertyChanged(nameof(HasPreview));
            if (previous is not null && !ReferenceEquals(previous, value))
            {
                previous.Dispose();
            }
        }
    }

    /// <summary>Gets a value indicating whether 已有预览可显示。</summary>
    public bool HasPreview => _preview is not null;

    /// <summary>Gets 预览结果状态文案（识别到多少格、多少色）。</summary>
    public string StatusText
    {
        get => _statusText;
        private set
        {
            if (SetProperty(ref _statusText, value))
            {
                OnPropertyChanged(nameof(HasStatusText));
            }
        }
    }

    /// <summary>Gets a value indicating whether 有状态文案可显示。</summary>
    public bool HasStatusText => !string.IsNullOrEmpty(_statusText);

    /// <summary>Gets a value indicating whether 该页签的任务正在运行；运行中参数锁定。</summary>
    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(CanEdit));
            }
        }
    }

    /// <summary>Gets a value indicating whether 参数与取景可编辑（运行中锁定，与 WPF <c>Run.Idle</c> 一致）。</summary>
    public bool CanEdit => !_isRunning;

    /// <summary>Gets or sets 构图方式，默认裁剪铺满。</summary>
    public PixelPaintFitMode Fit
    {
        get => _fit;
        set
        {
            if (SetProperty(ref _fit, value))
            {
                Reconvert();
            }
        }
    }

    /// <summary>Gets or sets 抖动方式，默认插画优先。</summary>
    public PixelPaintDitherMode Dither
    {
        get => _dither;
        set
        {
            if (SetProperty(ref _dither, value))
            {
                Reconvert();
            }
        }
    }

    /// <summary>Gets or sets 对比度百分比，100 为原图。</summary>
    public int Contrast
    {
        get => _contrast;
        set
        {
            var normalized = Math.Clamp(value, MinFilterPercent, MaxFilterPercent);
            if (SetProperty(ref _contrast, normalized))
            {
                Reconvert();
            }
        }
    }

    /// <summary>Gets or sets 亮度百分比，100 为原图。</summary>
    public int Brightness
    {
        get => _brightness;
        set
        {
            var normalized = Math.Clamp(value, MinFilterPercent, MaxFilterPercent);
            if (SetProperty(ref _brightness, normalized))
            {
                Reconvert();
            }
        }
    }

    /// <summary>Gets or sets 饱和度百分比，100 为原图。</summary>
    public int Saturation
    {
        get => _saturation;
        set
        {
            var normalized = Math.Clamp(value, 0, MaxFilterPercent);
            if (SetProperty(ref _saturation, normalized))
            {
                Reconvert();
            }
        }
    }

    /// <summary>Gets or sets a value indicating whether 也画纯白格（默认跳过，与 WPF 勾选 ｢也画白色｣ 对应）。</summary>
    public bool PaintWhite
    {
        get => _paintWhite;
        set
        {
            if (SetProperty(ref _paintWhite, value))
            {
                Reconvert();
            }
        }
    }

    /// <summary>Gets or sets a value indicating whether 启用滑动绘制（同色同行连续格一次拖完）。</summary>
    public bool SwipeEnabled
    {
        get => _swipeEnabled;
        set => SetProperty(ref _swipeEnabled, value);
    }

    /// <summary>Gets or sets 每格额外等待（毫秒），默认 0。</summary>
    public int GridDelay
    {
        get => _gridDelay;
        set => SetProperty(ref _gridDelay, Math.Clamp(value, 0, MaxGridDelay));
    }

    /// <summary>Gets 当前分组点列；未生成预览时为空。</summary>
    public IReadOnlyList<PixelPaintColorGroup> Groups => _result?.Groups ?? [];

    /// <summary>Gets a value indicating whether 已有可绘制格子。</summary>
    public bool HasPaintableCells => _result is { PaintedCellCount: > 0 };

    /// <summary>Gets 当前语言下的页签标题。</summary>
    public string TabTitle => T("Toolbox.Tab.PixelPaint", "Pixel Paint");

    /// <summary>Gets 构图方式标签。</summary>
    public string FitLabel => T("Toolbox.PixelPaint.Fit", "Fit");

    /// <summary>Gets 抖动方式标签。</summary>
    public string DitherLabel => T("Toolbox.PixelPaint.Dither", "Dither");

    /// <summary>Gets 对比度标签。</summary>
    public string ContrastLabel => T("Toolbox.PixelPaint.Contrast", "Contrast");

    /// <summary>Gets 亮度标签。</summary>
    public string BrightnessLabel => T("Toolbox.PixelPaint.Brightness", "Brightness");

    /// <summary>Gets 饱和度标签。</summary>
    public string SaturationLabel => T("Toolbox.PixelPaint.Saturation", "Saturation");

    /// <summary>Gets ｢也画白色｣ 文案。</summary>
    public string PaintWhiteLabel => T("Toolbox.PixelPaint.PaintWhite", "Paint white too");

    /// <summary>Gets ｢启用滑动绘制｣ 文案。</summary>
    public string SwipeLabel => T("Toolbox.PixelPaint.Swipe", "Enable swipe drawing");

    /// <summary>Gets 滑动绘制提示。</summary>
    public string SwipeTip => T("Toolbox.PixelPaint.Swipe.Tip", "Draws consecutive cells of the same color in a single drag for much faster painting.");

    /// <summary>Gets 绘制间隔标签。</summary>
    public string GridDelayLabel => T("Toolbox.PixelPaint.GridDelay", "Paint delay (ms)");

    /// <summary>Gets 绘制间隔提示。</summary>
    public string GridDelayTip => T("Toolbox.PixelPaint.GridDelay.Tip", "Extra per-cell wait added to clicks and drag duration.");

    /// <summary>Gets ｢选择图片｣ 按钮文案。</summary>
    public string PickImageText => T("Toolbox.PixelPaint.PickImage", "Pick Image");

    /// <summary>Gets ｢粘贴｣ 按钮文案。</summary>
    public string PasteText => T("Toolbox.PixelPaint.Paste", "Paste");

    /// <summary>Gets ｢重置取景｣ 按钮文案。</summary>
    public string ResetViewText => T("Toolbox.PixelPaint.ResetView", "Reset View");

    /// <summary>Gets ｢重置参数｣ 按钮文案。</summary>
    public string ResetParamsText => T("Toolbox.PixelPaint.ResetParams", "Reset Params");

    /// <summary>Gets 预览占位提示（拖入图片 / 粘贴图片或文字）。</summary>
    public string DropHintText => T("Toolbox.PixelPaint.DropHint", "Drop or paste an image or text, or pick below");

    /// <summary>Gets 页面底部提示。</summary>
    public string TipText => T(
        "Toolbox.PixelPaint.Tip",
        "Enter the in-game 24×24 pixel editor first, then start. Drag/scroll the preview to reframe; white is skipped by default.");

    /// <summary>Gets ｢需要先导入图片｣ 提示。</summary>
    public string NeedImageText => T("Toolbox.PixelPaint.NeedImage", "Import an image and generate a preview first");

    /// <summary>
    /// 更新语言：刷新下拉条目与全部本地化属性。
    /// </summary>
    /// <param name="language">新语言代码。</param>
    public void RefreshLocalization(string language)
    {
        _optionLocalizer.Language = language;
        FitOptions.RefreshLocalization();
        DitherOptions.RefreshLocalization();
        RefreshStatusText();
        OnPropertyChanged(string.Empty);
    }

    /// <summary>
    /// 标记本页签任务是否在运行（运行中锁定参数，与 WPF <c>Run.Idle</c> 绑定等价）。
    /// </summary>
    /// <param name="running">是否运行中。</param>
    public void SetRunning(bool running) => IsRunning = running;

    /// <summary>
    /// 从图片文件导入源图。
    /// </summary>
    /// <param name="path">图片路径。</param>
    public void LoadImageFromFile(string path)
    {
        if (!CanEdit)
        {
            return;
        }

        try
        {
            LoadImage(PixelPaintImaging.LoadFromFile(path));
        }
        catch (Exception)
        {
            FailLoad();
        }
    }

    /// <summary>
    /// 从图片数据流导入源图（剪贴板等无路径场景）。
    /// </summary>
    /// <param name="stream">图片数据流。</param>
    public void LoadImageFromStream(Stream stream)
    {
        if (!CanEdit)
        {
            return;
        }

        try
        {
            LoadImage(PixelPaintImaging.LoadFromStream(stream));
        }
        catch (Exception)
        {
            FailLoad();
        }
    }

    /// <summary>
    /// 把最多 4 个字的文本渲染为源图并导入。
    /// </summary>
    /// <param name="text">文本内容，超出 4 个字素的部分忽略。</param>
    public void LoadImageFromText(string? text)
    {
        if (!CanEdit)
        {
            return;
        }

        try
        {
            if (PixelPaintImaging.RenderText(text) is { } image)
            {
                LoadImage(image);
            }
        }
        catch (Exception)
        {
            FailLoad();
        }
    }

    /// <summary>
    /// 导入已解码的源图：去边并重新生成预览。
    /// </summary>
    /// <param name="image">已解码的 BGRA 源图。</param>
    public void LoadImage(PixelPaintImage image)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (!CanEdit)
        {
            return;
        }

        _prepared = PixelPaintConverter.Prepare(image, trimEmptyBorder: true);
        _view = PixelPaintViewRect.Full;
        Reconvert();
    }

    /// <summary>重置取景到整张内容图。</summary>
    public void ResetView()
    {
        if (!CanEdit)
        {
            return;
        }

        _view = PixelPaintViewRect.Full;
        Reconvert();
    }

    /// <summary>重置全部参数与取景（WPF ｢重置参数｣）。</summary>
    public void ResetParameters()
    {
        if (!CanEdit)
        {
            return;
        }

        _view = PixelPaintViewRect.Full;
        _fit = PixelPaintFitMode.Crop;
        _dither = PixelPaintDitherMode.Illustration;
        _contrast = 100;
        _brightness = 100;
        _saturation = 100;
        _paintWhite = false;
        OnPropertyChanged(nameof(Fit));
        OnPropertyChanged(nameof(Dither));
        OnPropertyChanged(nameof(Contrast));
        OnPropertyChanged(nameof(Brightness));
        OnPropertyChanged(nameof(Saturation));
        OnPropertyChanged(nameof(PaintWhite));
        Reconvert();
    }

    /// <summary>
    /// 以中心为锚点缩放取景（滚轮），<paramref name="factor"/> &lt; 1 为放大。
    /// </summary>
    /// <param name="factor">缩放系数。</param>
    public void Zoom(double factor)
    {
        if (!CanEdit || _prepared is null)
        {
            return;
        }

        _view = _view.Zoom(factor);
        Reconvert();
    }

    /// <summary>
    /// 拖动平移取景。
    /// </summary>
    /// <param name="deltaX">指针水平位移占预览边长的比例（右为正）。</param>
    /// <param name="deltaY">指针垂直位移占预览边长的比例（下为正）。</param>
    public void Pan(double deltaX, double deltaY)
    {
        if (!CanEdit || _prepared is null)
        {
            return;
        }

        _view = _view.Pan(deltaX, deltaY);
        Reconvert();
    }

    /// <summary>清除当前源图与预览（停止运行后清理）。</summary>
    public void Clear()
    {
        _prepared = null;
        _result = null;
        _view = PixelPaintViewRect.Full;
        Preview = null;
        StatusText = string.Empty;
        OnPropertyChanged(nameof(Groups));
        OnPropertyChanged(nameof(HasPaintableCells));
    }

    /// <summary>
    /// 构造执行记录所需的参数摘要。
    /// </summary>
    /// <returns>参数摘要。</returns>
    public string BuildParameterSummary()
    {
        return PixelPaintTaskPayload.BuildParameterSummary(Groups, SwipeEnabled, GridDelay);
    }

    private string T(string key, string fallback) => _texts.GetOrDefault(key, fallback);

    /// <summary>
    /// 由视图层上报加载失败（剪贴板等异常），显示统一的加载失败文案。
    /// </summary>
    public void NotifyLoadFailed() => FailLoad();

    private void FailLoad()
    {
        StatusText = T("Toolbox.PixelPaint.LoadFailed", "Failed to load image");
    }

    private void Reconvert()
    {
        if (!CanEdit || _prepared is null)
        {
            return;
        }

        try
        {
            var options = new PixelPaintConvertOptions
            {
                Fit = _fit,
                Dither = _dither,
                ContrastPercent = _contrast,
                BrightnessPercent = _brightness,
                SaturationPercent = _saturation,
                ViewNormalized = _view,
                TrimEmptyBorder = true,
            };

            _result = PixelPaintConverter.Convert(_prepared, options, skipWhite: !_paintWhite);
            Preview = PixelPaintImaging.CreateBitmap(
                _result.PreviewBgra,
                PixelPaintPalette.PreviewPixelSize,
                PixelPaintPalette.PreviewPixelSize);
            RefreshStatusText();
            OnPropertyChanged(nameof(Groups));
            OnPropertyChanged(nameof(HasPaintableCells));
        }
        catch (Exception)
        {
            _result = null;
            Preview = null;
            StatusText = T("Toolbox.PixelPaint.ConvertFailed", "Conversion failed");
            OnPropertyChanged(nameof(Groups));
            OnPropertyChanged(nameof(HasPaintableCells));
        }
    }

    private void RefreshStatusText()
    {
        if (_result is null)
        {
            return;
        }

        StatusText = string.Format(
            System.Globalization.CultureInfo.CurrentCulture,
            T("Toolbox.PixelPaint.ReadyStatus", "{0} cells / {1} colors"),
            _result.PaintedCellCount,
            _result.Groups.Count);
    }
}
