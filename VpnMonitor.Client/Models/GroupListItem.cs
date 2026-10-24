namespace VpnMonitor.Client.Models;

public sealed class GroupListItem : System.ComponentModel.INotifyPropertyChanged
{
    public string Name { get; init; } = "";
    public int Online { get; init; }
    public int Total { get; init; }
    private bool _isCurrent = true;
    public bool IsCurrent
    {
        get => _isCurrent;
        set { if (_isCurrent == value) return; _isCurrent = value; PropertyChanged?.Invoke(this,new(nameof(CountText))); }
    }
    public string CountText => IsCurrent ? $"{Online}/{Total}" : $"—/{Total}";
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}
