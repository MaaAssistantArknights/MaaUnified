using Avalonia;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace MAAUnified.App.Controls;

public partial class VerticalSpinNumberBox : UserControl
{
    public static readonly StyledProperty<int> MinimumProperty =
        AvaloniaProperty.Register<VerticalSpinNumberBox, int>(nameof(Minimum), 0);

    public static readonly StyledProperty<int> MaximumProperty =
        AvaloniaProperty.Register<VerticalSpinNumberBox, int>(nameof(Maximum), int.MaxValue);

    public static readonly StyledProperty<int> IncrementProperty =
        AvaloniaProperty.Register<VerticalSpinNumberBox, int>(nameof(Increment), 1);

    public static readonly StyledProperty<int> ValueProperty =
        AvaloniaProperty.Register<VerticalSpinNumberBox, int>(
            nameof(Value),
            defaultValue: 0,
            defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<int> EditorValueProperty =
        AvaloniaProperty.Register<VerticalSpinNumberBox, int>(nameof(EditorValue));

    public static readonly StyledProperty<string> FormatStringProperty =
        AvaloniaProperty.Register<VerticalSpinNumberBox, string>(nameof(FormatString), "F0");

    public static readonly StyledProperty<bool> WrapAroundStepProperty =
        AvaloniaProperty.Register<VerticalSpinNumberBox, bool>(nameof(WrapAroundStep), false);

    public static readonly StyledProperty<bool> CommitValueOnLostFocusProperty =
        AvaloniaProperty.Register<VerticalSpinNumberBox, bool>(nameof(CommitValueOnLostFocus), false);

    public static readonly StyledProperty<bool> DismissFocusOnEnterProperty =
        AvaloniaProperty.Register<VerticalSpinNumberBox, bool>(nameof(DismissFocusOnEnter), true);

    private bool _syncingEditorValue;
    private bool _syncingValue;
    private bool _hasPendingEditorValue;

    static VerticalSpinNumberBox()
    {
        MinimumProperty.Changed.AddClassHandler<VerticalSpinNumberBox>((box, _) => box.CoerceValueWithinRange());
        MaximumProperty.Changed.AddClassHandler<VerticalSpinNumberBox>((box, _) => box.CoerceValueWithinRange());
        ValueProperty.Changed.AddClassHandler<VerticalSpinNumberBox>((box, _) => box.OnValueChanged());
        EditorValueProperty.Changed.AddClassHandler<VerticalSpinNumberBox>((box, _) => box.OnEditorValueChanged());
        CommitValueOnLostFocusProperty.Changed.AddClassHandler<VerticalSpinNumberBox>((box, _) => box.SyncEditorValueFromValue());
    }

    public VerticalSpinNumberBox()
    {
        InitializeComponent();
        AddHandler(GotFocusEvent, OnFocusChanged, RoutingStrategies.Bubble);
        AddHandler(LostFocusEvent, OnFocusChanged, RoutingStrategies.Bubble);
    }

    public int Minimum
    {
        get => GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public int Maximum
    {
        get => GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public int Increment
    {
        get => GetValue(IncrementProperty);
        set => SetValue(IncrementProperty, value);
    }

    public int Value
    {
        get => GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public int EditorValue
    {
        get => GetValue(EditorValueProperty);
        set => SetValue(EditorValueProperty, value);
    }

    public string FormatString
    {
        get => GetValue(FormatStringProperty);
        set => SetValue(FormatStringProperty, value);
    }

    public bool WrapAroundStep
    {
        get => GetValue(WrapAroundStepProperty);
        set => SetValue(WrapAroundStepProperty, value);
    }

    public bool CommitValueOnLostFocus
    {
        get => GetValue(CommitValueOnLostFocusProperty);
        set => SetValue(CommitValueOnLostFocusProperty, value);
    }

    public bool DismissFocusOnEnter
    {
        get => GetValue(DismissFocusOnEnterProperty);
        set => SetValue(DismissFocusOnEnterProperty, value);
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && DismissFocusOnEnter)
        {
            TopLevel.GetTopLevel(this)?.FocusManager?.ClearFocus();
        }
    }

    private void OnIncreaseClick(object? sender, RoutedEventArgs e)
    {
        Step(+1);
    }

    private void OnDecreaseClick(object? sender, RoutedEventArgs e)
    {
        Step(-1);
    }

    private void OnFocusChanged(object? sender, RoutedEventArgs e)
    {
        UpdateFocusedState();
    }

    private void UpdateFocusedState()
    {
        PseudoClasses.Set(":focused", IsKeyboardFocusWithin);
        SpinRootBorder.Classes.Set("focused", IsKeyboardFocusWithin);
        if (!IsKeyboardFocusWithin)
        {
            CommitPendingEditorValue();
        }
    }

    private void Step(int direction)
    {
        if (!IsEnabled || direction == 0)
        {
            return;
        }

        var min = Math.Min(Minimum, Maximum);
        var max = Math.Max(Minimum, Maximum);
        var step = Increment <= 0 ? 1 : Increment;
        var stepped = (long)Value + ((long)step * direction);
        if (WrapAroundStep)
        {
            if (direction > 0 && stepped > max)
            {
                SetEditorValue(min);
                return;
            }

            if (direction < 0 && stepped < min)
            {
                SetEditorValue(max);
                return;
            }
        }

        SetEditorValue(ClampToRange(stepped));
    }

    private void SetEditorValue(int value)
    {
        SetCurrentValue(EditorValueProperty, value);
        if (CommitValueOnLostFocus && IsKeyboardFocusWithin)
        {
            _hasPendingEditorValue = true;
            return;
        }

        CommitEditorValue();
    }

    private void CoerceValueWithinRange()
    {
        var clamped = ClampToRange(Value);
        if (clamped != Value)
        {
            SetCurrentValue(ValueProperty, clamped);
        }

        var clampedEditor = ClampToRange(EditorValue);
        if (clampedEditor != EditorValue)
        {
            SetCurrentValue(EditorValueProperty, clampedEditor);
        }
    }

    private void OnValueChanged()
    {
        if (_syncingValue)
        {
            return;
        }

        CoerceValueWithinRange();
        SyncEditorValueFromValue();
    }

    private void OnEditorValueChanged()
    {
        if (_syncingEditorValue)
        {
            return;
        }

        var clamped = ClampToRange(EditorValue);
        if (clamped != EditorValue)
        {
            _syncingEditorValue = true;
            try
            {
                SetCurrentValue(EditorValueProperty, clamped);
            }
            finally
            {
                _syncingEditorValue = false;
            }

            return;
        }

        if (CommitValueOnLostFocus && IsKeyboardFocusWithin)
        {
            _hasPendingEditorValue = true;
            return;
        }

        CommitEditorValue();
    }

    private void SyncEditorValueFromValue()
    {
        if (_syncingValue || _syncingEditorValue)
        {
            return;
        }

        _hasPendingEditorValue = false;
        _syncingEditorValue = true;
        try
        {
            SetCurrentValue(EditorValueProperty, ClampToRange(Value));
        }
        finally
        {
            _syncingEditorValue = false;
        }
    }

    private void CommitPendingEditorValue()
    {
        if (!_hasPendingEditorValue)
        {
            return;
        }

        _hasPendingEditorValue = false;
        CommitEditorValue();
    }

    private void CommitEditorValue()
    {
        var clamped = ClampToRange(EditorValue);
        if (clamped == Value)
        {
            return;
        }

        _syncingValue = true;
        try
        {
            SetCurrentValue(ValueProperty, clamped);
        }
        finally
        {
            _syncingValue = false;
        }
    }

    private int ClampToRange(long value)
    {
        var min = Math.Min(Minimum, Maximum);
        var max = Math.Max(Minimum, Maximum);
        if (value < min)
        {
            return min;
        }

        if (value > max)
        {
            return max;
        }

        return (int)value;
    }
}
