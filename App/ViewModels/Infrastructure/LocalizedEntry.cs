namespace MAAUnified.App.ViewModels.Infrastructure;

/// <summary>
/// <see cref="LocalizedObservableList{TValue}"/> 的条目定义。
/// <para>(值, 本地化 key) 二元组可隐式转换为本类型；附加 key、悬浮提示、可选状态与显示后缀通过
/// <c>WithSecondaryKey</c>/<c>WithTip</c>/<c>WithEnabled</c>/<c>WithSuffix</c> 链式补充。</para>
/// <para>WPF parity: MaaWpfGui.Utilities.ValueType.LocalizedEntry（dev-v2 96c2c4ee）。</para>
/// </summary>
/// <typeparam name="TValue">条目的值类型。</typeparam>
/// <param name="Value">业务值。</param>
/// <param name="LocalizationKey">本地化 key，为 null 时直接显示 <paramref name="Literal"/>。</param>
/// <param name="Literal">本地化 key 缺失时的回退文案，也可用于纯数字等无需翻译的条目。</param>
public readonly record struct LocalizedEntry<TValue>(
    TValue Value,
    string? LocalizationKey = null,
    string? Literal = null)
{
    /// <summary>
    /// Gets 附加本地化 key，显示时 Display 为 "{主 key} ({附加 key})"。
    /// </summary>
    public string? SecondaryKey { get; init; }

    /// <summary>
    /// Gets 附加 key 的显示条件。为 null 时恒显示；否则每次刷新文本时重新求值，返回 true 才显示。
    /// </summary>
    public Func<bool>? ShowSecondary { get; init; }

    /// <summary>
    /// Gets 悬浮提示本地化 key。
    /// </summary>
    public string? TipKey { get; init; }

    /// <summary>
    /// Gets a value indicating whether 条目可选，false 时下拉项置灰。
    /// </summary>
    public bool IsEnabled { get; init; } = true;

    /// <summary>
    /// Gets 显示后缀，追加在完整显示文本之后（如难度档位的 "(-1)"）。
    /// </summary>
    public string? Suffix { get; init; }

    /// <summary>
    /// 由 (值, 本地化 key) 二元组构造条目。
    /// </summary>
    /// <param name="entry">(值, 本地化 key)。</param>
    public static implicit operator LocalizedEntry<TValue>((TValue Value, string LocalizationKey) entry)
        => new(entry.Value, entry.LocalizationKey);

    /// <summary>
    /// 设置附加本地化 key 及其显示条件。
    /// </summary>
    /// <param name="secondaryKey">附加本地化 key。</param>
    /// <param name="showSecondary">显示条件，为 null 时恒显示；条件依赖的状态变化后需由调用方触发列表的 <see cref="LocalizedObservableList{TValue}.RefreshLocalization"/>。</param>
    /// <returns>设置后的新条目。</returns>
    public LocalizedEntry<TValue> WithSecondaryKey(string secondaryKey, Func<bool>? showSecondary = null)
        => this with { SecondaryKey = secondaryKey, ShowSecondary = showSecondary };

    /// <summary>
    /// 设置悬浮提示本地化 key。
    /// </summary>
    /// <param name="tipKey">悬浮提示本地化 key。</param>
    /// <returns>设置后的新条目。</returns>
    public LocalizedEntry<TValue> WithTip(string tipKey)
        => this with { TipKey = tipKey };

    /// <summary>
    /// 设置条目是否可选。
    /// </summary>
    /// <param name="isEnabled">是否可选。</param>
    /// <returns>设置后的新条目。</returns>
    public LocalizedEntry<TValue> WithEnabled(bool isEnabled)
        => this with { IsEnabled = isEnabled };

    /// <summary>
    /// 设置显示后缀。
    /// </summary>
    /// <param name="suffix">追加在显示文本之后的固定后缀。</param>
    /// <returns>设置后的新条目。</returns>
    public LocalizedEntry<TValue> WithSuffix(string suffix)
        => this with { Suffix = suffix };
}

/// <summary>
/// 让 (值, 本地化 key) 二元组直接链式调用 <see cref="LocalizedEntry{TValue}"/> 的设置方法。
/// </summary>
public static class LocalizedEntryExtensions
{
    /// <summary>
    /// 由二元组构造条目并设置附加本地化 key 及其显示条件。
    /// </summary>
    /// <typeparam name="TValue">条目的值类型。</typeparam>
    /// <param name="entry">(值, 本地化 key)。</param>
    /// <param name="secondaryKey">附加本地化 key。</param>
    /// <param name="showSecondary">显示条件，为 null 时恒显示；条件依赖的状态变化后需由调用方触发列表的 RefreshLocalization。</param>
    /// <returns>构造出的条目。</returns>
    public static LocalizedEntry<TValue> WithSecondaryKey<TValue>(
        this (TValue Value, string LocalizationKey) entry,
        string secondaryKey,
        Func<bool>? showSecondary = null)
        => ((LocalizedEntry<TValue>)entry).WithSecondaryKey(secondaryKey, showSecondary);

    /// <summary>
    /// 由二元组构造条目并设置悬浮提示本地化 key。
    /// </summary>
    /// <typeparam name="TValue">条目的值类型。</typeparam>
    /// <param name="entry">(值, 本地化 key)。</param>
    /// <param name="tipKey">悬浮提示本地化 key。</param>
    /// <returns>构造出的条目。</returns>
    public static LocalizedEntry<TValue> WithTip<TValue>(
        this (TValue Value, string LocalizationKey) entry,
        string tipKey)
        => ((LocalizedEntry<TValue>)entry).WithTip(tipKey);

    /// <summary>
    /// 由二元组构造条目并设置是否可选。
    /// </summary>
    /// <typeparam name="TValue">条目的值类型。</typeparam>
    /// <param name="entry">(值, 本地化 key)。</param>
    /// <param name="isEnabled">是否可选。</param>
    /// <returns>构造出的条目。</returns>
    public static LocalizedEntry<TValue> WithEnabled<TValue>(
        this (TValue Value, string LocalizationKey) entry,
        bool isEnabled)
        => ((LocalizedEntry<TValue>)entry).WithEnabled(isEnabled);
}
