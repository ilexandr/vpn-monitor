using System.ComponentModel;

namespace VpnMonitor.Client.Models;

public sealed class AwgDataState : INotifyPropertyChanged
{
    private bool _isCurrent;
    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent == value) return;
            _isCurrent = value;
            PropertyChanged?.Invoke(this, new(nameof(IsCurrent)));
            PropertyChanged?.Invoke(this, new(nameof(Message)));
        }
    }
    public string Message => IsCurrent ? "AWG: данные актуальны" : "AWG: нет актуальных данных — показан последний список";
    public event PropertyChangedEventHandler? PropertyChanged;
}
