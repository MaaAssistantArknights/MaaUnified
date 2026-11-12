using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using MAAUnified.App.Controls;
using MAAUnified.Application.Services.Localization;

namespace MAAUnified.App.ViewModels.Infrastructure;

/// <summary>
/// 本地化下拉列表的条目视图模型。非泛型基类供 XAML 模板类型提示使用。
/// <see cref="Display"/>、<see cref="Tip"/> 与 <see cref="IsEnabled"/> 由所属列表在
/// <see cref="LocalizedObservableList{TValue}.RefreshLocalization"/> 时重算。
/// </summary>
public abstract class LocalizedOptionItem : ObservableObject, IAppSelectItemEnablement
{
    private string _display = string.Empty;
    private string? _tip;
    private bool _isEnabled = true;

    /// <summary>
    /// Gets 当前语言的显示文本（含附加 key 与后缀）。
    /// </summary>
    public string Display
    {
        get => _display;
        private set => SetProperty(ref _display, value);
    }

    /// <summary>
    /// Gets 当前语言的悬浮提示，无提示时为 null。
    /// </summary>
    public string? Tip
    {
        get => _tip;
        private set => SetProperty(ref _tip, value);
    }

    /// <summary>
    /// Gets a value indicating whether 条目可选。
    /// </summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        private set => SetProperty(ref _isEnabled, value);
    }

    internal void Update(string display, string? tip, bool isEnabled)
    {
        Display = display;
        Tip = tip;
        IsEnabled = isEnabled;
    }
}

/// <summary>
/// 本地化下拉列表的条目视图模型。
/// </summary>
/// <typeparam name="TValue">条目的值类型。</typeparam>
public sealed class LocalizedOptionItem<TValue> : LocalizedOptionItem
{
    internal LocalizedOptionItem(TValue value, string display, string? tip, bool isEnabled)
    {
        Value = value;
        Update(display, tip, isEnabled);
    }

    /// <summary>
    /// Gets 业务值。
    /// </summary>
    public TValue Value { get; }
}

/// <summary>
/// 支持语言热切换的本地化可观察列表。
/// <para>每个条目由 <see cref="LocalizedEntry{TValue}"/> 描述，<see cref="LocalizedOptionItem{TValue}.Display"/> 与
/// <see cref="LocalizedOptionItem{TValue}.Tip"/> 在初始化和 <see cref="RefreshLocalization"/> 时从 key 获取。
/// 条目直接写 (值, 本地化 key) 二元组，需要附加 key、悬浮提示、初始不可选或显示后缀时链式调用
/// <c>WithSecondaryKey</c>/<c>WithTip</c>/<c>WithEnabled</c>/<c>WithSuffix</c>。</para>
/// <para>增删请使用 <see cref="Add(LocalizedEntry{TValue})"/>/<see cref="Remove"/> 等方法，它们会同步维护内部条目表；
/// 不要直接操作 <see cref="Items"/>，否则刷新时该项不会被更新。</para>
/// <para>WPF parity: MaaWpfGui.Utilities.ValueType.LocalizedObservableList（dev-v2 96c2c4ee）。</para>
/// </summary>
/// <typeparam name="TValue">条目的值类型。</typeparam>
public sealed class LocalizedObservableList<TValue> : IEnumerable<LocalizedOptionItem<TValue>>, INotifyCollectionChanged
{
    private readonly IUiLocalizer _localizer;
    private readonly string _scope;

    // 与 Items 一一对应的条目表；增删操作必须同步维护两者。
    private readonly List<LocalizedEntry<TValue>> _entries;

    /// <summary>
    /// Initializes a new instance of the <see cref="LocalizedObservableList{TValue}"/> class.
    /// </summary>
    /// <param name="localizer">取文案的本地化器，语言热切换时由调用方更新其 <see cref="IUiLocalizer.Language"/> 后刷新。</param>
    /// <param name="scope">本地化回退上报的作用域。</param>
    /// <param name="entries">条目数组，元素可直接写 (值, 本地化 key) 二元组。</param>
    public LocalizedObservableList(IUiLocalizer localizer, string scope, params LocalizedEntry<TValue>[] entries)
    {
        ArgumentNullException.ThrowIfNull(localizer);
        _localizer = localizer;
        _scope = string.IsNullOrWhiteSpace(scope) ? "Ui.Localization" : scope;
        _entries = [.. entries];
        Items = new(entries.Select(CreateItem));
        Items.CollectionChanged += OnItemsCollectionChanged;
    }

    /// <summary>
    /// Gets 供 ItemsSource 绑定的可观察集合。增删请走列表方法。
    /// </summary>
    public ObservableCollection<LocalizedOptionItem<TValue>> Items { get; }

    /// <inheritdoc/>
    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    /// <summary>
    /// Gets 条目数。
    /// </summary>
    public int Count => Items.Count;

    /// <summary>
    /// Gets 指定索引处的条目。
    /// </summary>
    /// <param name="index">从零开始的索引。</param>
    public LocalizedOptionItem<TValue> this[int index] => Items[index];

    /// <summary>
    /// 刷新所有条目的显示文本、悬浮提示与可选状态，并重新求值附加 key 的显示条件。
    /// 语言切换时，以及显示条件依赖的状态变化后调用。
    /// </summary>
    public void RefreshLocalization()
    {
        for (var i = 0; i < Items.Count && i < _entries.Count; i++)
        {
            var entry = _entries[i];
            Items[i].Update(FormatDisplay(entry), FormatTip(entry), entry.IsEnabled);
        }
    }

    /// <summary>
    /// 添加一个条目（同步扩展条目表）。
    /// </summary>
    /// <param name="entry">条目，可直接写 (值, 本地化 key) 二元组。</param>
    public void Add(LocalizedEntry<TValue> entry)
    {
        // 先创建 item：ShowSecondary 回调抛异常时两表都未改动，避免错位。
        var item = CreateItem(entry);
        _entries.Add(entry);
        Items.Add(item);
    }

    /// <summary>
    /// 添加一个只带本地化 key 的条目。
    /// </summary>
    /// <param name="value">业务值。</param>
    /// <param name="localizationKey">本地化 key。</param>
    public void Add(TValue value, string localizationKey) => Add(new LocalizedEntry<TValue>(value, localizationKey));

    /// <summary>
    /// 移除第一个值匹配的条目（同步移除条目表中的对应项）。
    /// </summary>
    /// <param name="value">要移除的业务值。</param>
    /// <returns>成功移除返回 true。</returns>
    public bool Remove(TValue value)
    {
        var index = _entries.FindIndex(entry => EqualityComparer<TValue>.Default.Equals(entry.Value, value));
        if (index < 0)
        {
            return false;
        }

        RemoveAt(index);
        return true;
    }

    /// <summary>
    /// 移除指定索引处的条目（同步移除条目表中的对应项）。
    /// </summary>
    /// <param name="index">从零开始的索引。</param>
    public void RemoveAt(int index)
    {
        _entries.RemoveAt(index);
        Items.RemoveAt(index);
    }

    /// <summary>
    /// 在指定索引处插入一个条目（同步扩展条目表）。
    /// </summary>
    /// <param name="index">从零开始的插入位置。</param>
    /// <param name="entry">条目，可直接写 (值, 本地化 key) 二元组。</param>
    public void Insert(int index, LocalizedEntry<TValue> entry)
    {
        var item = CreateItem(entry);
        _entries.Insert(index, entry);
        Items.Insert(index, item);
    }

    /// <summary>
    /// 移除所有条目（同步清空条目表）。
    /// </summary>
    public void Clear()
    {
        _entries.Clear();
        Items.Clear();
    }

    /// <inheritdoc/>
    public IEnumerator<LocalizedOptionItem<TValue>> GetEnumerator() => Items.GetEnumerator();

    /// <inheritdoc/>
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    private LocalizedOptionItem<TValue> CreateItem(LocalizedEntry<TValue> entry)
        => new(entry.Value, FormatDisplay(entry), FormatTip(entry), entry.IsEnabled);

    private string FormatDisplay(LocalizedEntry<TValue> entry)
    {
        var primary = string.IsNullOrEmpty(entry.LocalizationKey)
            ? entry.Literal ?? string.Empty
            : _localizer.GetOrDefault(entry.LocalizationKey, entry.Literal ?? entry.LocalizationKey, _scope);
        if (entry.SecondaryKey is { Length: > 0 } secondaryKey && (entry.ShowSecondary?.Invoke() ?? true))
        {
            primary = $"{primary} ({_localizer.GetOrDefault(secondaryKey, secondaryKey, _scope)})";
        }

        return entry.Suffix is { Length: > 0 } suffix ? primary + suffix : primary;
    }

    private string? FormatTip(LocalizedEntry<TValue> entry)
        => entry.TipKey is { Length: > 0 } tipKey ? _localizer.GetOrDefault(tipKey, tipKey, _scope) : null;

    private void OnItemsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        => CollectionChanged?.Invoke(this, e);
}
