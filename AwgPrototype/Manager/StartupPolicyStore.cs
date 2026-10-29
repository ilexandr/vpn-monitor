using System.IO;
using System.Text.Json;
using VpnMonitor.Client.Services;
namespace AwgPrototype;
public static class StartupPolicyStore
{
    private static string Marker(string directory)=>Path.Combine(directory,"startup-configured");
    public static void Initialize(string directory,bool hasSelected,Func<AwgStartupSnapshot> read,Action validate,Action<bool> configure,bool newService=false)
    {
        if(File.Exists(Marker(directory))) {
            if(newService) {
                using var policy=JsonDocument.Parse(File.ReadAllText(Marker(directory)));
                if(policy.RootElement.GetProperty("Version").GetInt32()!=1)throw new InvalidDataException();
                var automatic=policy.RootElement.GetProperty("Automatic").GetBoolean();
                if(automatic){if(!hasSelected)throw new InvalidDataException();validate();}
                configure(automatic);Mark(directory,automatic);
            } else Mark(directory,read().Automatic); // SCM remains authoritative on upgrades.
            return;
        }
        // Before 0.1.77 every service was demand-start; migrate configured profiles once.
        // An externally disabled service is never enabled implicitly.
        var current=read();
        if(hasSelected&&current.Mode==3) {validate();configure(true);Mark(directory,true);}
        else Mark(directory,current.Automatic);
    }
    public static void Set(string directory,bool automatic,Action validate,Action<bool> configure)
    {
        if(automatic)validate();configure(automatic);Mark(directory,automatic);
    }
    private static void Mark(string directory,bool automatic)
    {
        var marker=Marker(directory);File.WriteAllText(marker+".new",JsonSerializer.Serialize(new {Version=1,Automatic=automatic}));File.Move(marker+".new",marker,true);
    }
}
