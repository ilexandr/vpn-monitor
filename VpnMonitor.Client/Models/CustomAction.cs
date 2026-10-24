using System.ComponentModel;
namespace VpnMonitor.Client.Models;
public sealed class CustomAction : INotifyPropertyChanged
{
    private string _name = "";
    public string Name
    {
        get => _name;
        set { if (_name == value) return; _name = value; PropertyChanged?.Invoke(this, new(nameof(Name))); }
    }
    public string Group { get; set; } = "";
    public string Executable { get; set; } = "";
    public List<string> Arguments { get; set; } = [];
    public event PropertyChangedEventHandler? PropertyChanged;
}