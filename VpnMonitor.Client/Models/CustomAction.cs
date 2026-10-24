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
    private string _group = "";
    public string Group
    {
        get => _group;
        set { if (_group == value) return; _group = value; PropertyChanged?.Invoke(this, new(nameof(Group))); }
    }
    private bool _enabled = true;
    public bool Enabled
    {
        get => _enabled;
        set { if (_enabled == value) return; _enabled = value; PropertyChanged?.Invoke(this, new(nameof(Enabled))); }
    }
    [System.Text.Json.Serialization.JsonIgnore]
    public string? BuiltinId { get; set; }
    public string Executable { get; set; } = "";
    public List<string> Arguments { get; set; } = [];
    public event PropertyChangedEventHandler? PropertyChanged;
}
