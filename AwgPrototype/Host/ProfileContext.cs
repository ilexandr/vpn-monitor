using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
namespace AwgPrototype;
public sealed record ActiveProfileContext(int Version,string ProfileId,string ProfileName,string Fingerprint);
public static class ProfileContext
{
    public static ActiveProfileContext Create(string config,string catalogFile,Func<string,string> readProfile)
    {
        var bytes=Encoding.UTF8.GetBytes(config);string fingerprint;
        try{fingerprint=Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();}finally{Array.Clear(bytes);}
        var unknown=new ActiveProfileContext(1,"","",fingerprint);
        try {
            if(new FileInfo(catalogFile).Length>256*1024)return unknown;
            using var json=JsonDocument.Parse(File.ReadAllText(catalogFile));var root=json.RootElement;
            if(root.GetProperty("Version").GetInt32()!=1)return unknown;
            var id=root.GetProperty("SelectedId").GetString();
            if(!Guid.TryParseExact(id,"N",out var parsed))return unknown;
            var rows=root.GetProperty("Profiles").EnumerateArray().Where(p=>p.GetProperty("Id").GetString()==id).ToArray();
            if(rows.Length!=1)return unknown;
            var name=rows[0].GetProperty("Name").GetString();
            if(string.IsNullOrWhiteSpace(name)||name.Length>80||name.Any(char.IsControl)||!string.Equals(config,readProfile(parsed.ToString("N")),StringComparison.Ordinal))return unknown;
            return new(1,parsed.ToString("N"),name,fingerprint);
        } catch {return unknown;}
    }
    public static void Write(string directory,string catalogFile,string config)
    {
        var context=Create(config,catalogFile,id=>MachineProfile.Read(Path.Combine(directory,id+".dpapi")));
        var path=Path.Combine(directory,"active-context.json");File.WriteAllText(path+".new",JsonSerializer.Serialize(context));File.Move(path+".new",path,true);
    }
}
