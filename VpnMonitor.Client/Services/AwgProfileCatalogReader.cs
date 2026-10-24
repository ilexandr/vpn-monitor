using System.IO;
using System.Text.Json;
using VpnMonitor.Client.Models;
namespace VpnMonitor.Client.Services;
public static class AwgProfileCatalogReader
{
    public static string CatalogPath=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),"VpnMonitor","AwgStatus","profiles.json");
    public static string ManagerPath=>Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),"VpnMonitor","AwgPrototype","AwgPrototype.Manager.exe");
    public static AwgProfileCatalog Read()
    {
        if(!File.Exists(CatalogPath))return new();
        if(new FileInfo(CatalogPath).Length>256*1024)throw new InvalidDataException("Некорректный список профилей.");
        return JsonSerializer.Deserialize<AwgProfileCatalog>(File.ReadAllText(CatalogPath))??new();
    }
}