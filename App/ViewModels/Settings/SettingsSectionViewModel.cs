using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MAAUnified.App.ViewModels.Settings;

/// <summary>
/// One settings-page navigation entry. <see cref="Key"/> is the MAAUnified section key, which is
/// also used for the anchor and localization lookups. <see cref="IsCollapsed"/> mirrors the
/// persisted <c>GUI.CollapesStates</c> state (WPF dev-v2 <c>0bc04350c0</c>).
/// </summary>
public sealed class SettingsSectionViewModel : INotifyPropertyChanged
{
    private bool _isCollapsed;

    public SettingsSectionViewModel(string key, string displayName)
    {
        Key = key;
        DisplayName = displayName;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Key { get; }

    public string DisplayName { get; }

    public bool IsCollapsed
    {
        get => _isCollapsed;
        internal set
        {
            if (_isCollapsed == value)
            {
                return;
            }

            _isCollapsed = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CollapseGlyph));
        }
    }

    /// <summary>Gets the chevron shown by the navigation rail; collapsed sections point right.</summary>
    public string CollapseGlyph => IsCollapsed ? "▸" : "▾";

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
