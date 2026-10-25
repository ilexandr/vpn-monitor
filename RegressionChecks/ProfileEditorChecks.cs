using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using AwgPrototype;
using VpnMonitor.Client.Models;
using VpnMonitor.Client.Services;

internal static class ProfileEditorChecks
{
    internal static string Fixture => "# keep comment\r\n[Interface]\r\nPrivateKey = " + Convert.ToBase64String(Enumerable.Repeat((byte)1,32).ToArray()) +
        "\r\nAddress = 10.40.0.2/32\r\nJc = 4\r\n[Peer]\r\nPublicKey = " + Convert.ToBase64String(Enumerable.Repeat((byte)2,32).ToArray()) +
        "\r\nPresharedKey = " + Convert.ToBase64String(Enumerable.Repeat((byte)3,32).ToArray()) + "\r\nAllowedIPs = 10.40.0.0/24\r\nEndpoint = 192.0.2.10:51820\r\n";
    public static void Run()
    {
        var config = Fixture; var document = new AwgEditableConfig(config);
        Require(!document.Text.Contains(Convert.ToBase64String(Enumerable.Repeat((byte)1,32).ToArray())), "Editor must not expose existing private keys.");
        Require(document.Restore(document.Text) == config, "An untouched masked profile must round-trip exactly, including CRLF, comments and AWG options.");
        var changed = document.Restore(document.Text.Replace("192.0.2.10", "192.0.2.11"));
        Require(changed == config.Replace("192.0.2.10", "192.0.2.11"), "Endpoint edits must preserve all keys and unknown AWG fields.");
        var token = Regex.Match(document.Text, @"<скрыто:[^>]+>").Value;
        try { document.Restore(document.Text.Replace("PrivateKey", "DNS")); throw new Exception("Moved key placeholder accepted"); } catch(ArgumentException) { }
        var replacement = Convert.ToBase64String(Enumerable.Repeat((byte)4,32).ToArray());
        Require(document.Restore(document.Text.Replace(token,replacement)).Contains("PrivateKey = " + replacement), "Users must be able to rotate a key explicitly.");
        var directory = Path.Combine(Path.GetTempPath(), "AwgEditor-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var id = Guid.NewGuid().ToString("N"); var path = Path.Combine(directory, id + ".dpapi"); var catalogPath = Path.Combine(directory,"catalog.json");
            File.WriteAllText(catalogPath, JsonSerializer.Serialize(new AwgProfileCatalog { SelectedId=id, Profiles=[new(id,"Fixture")] }));
            ProfileProtector.Write(config,path); var before = File.ReadAllBytes(path);
            int Save(uint state, string original, string next, Action<string>? validator=null) => ProfileEditorStore.Save(directory,catalogPath,id,original,next,()=>state,validator ?? (c => { _=AwgConfigPreview.Parse(c); }));
            Require(Save(4,config,changed)==12 && File.ReadAllBytes(path).SequenceEqual(before), "A running tunnel must block saving without changing ciphertext.");
            Require(Save(1,config,changed,_=>throw new ArgumentException())==16 && File.ReadAllBytes(path).SequenceEqual(before), "Engine validation failure must preserve original ciphertext.");
            File.WriteAllText(Path.Combine(directory,id+".connection.json"),JsonSerializer.Serialize(new AwgConnectionOptions { ProbeHost="10.40.0.1" }));
            Require(Save(1,config,changed.Replace("10.40.0.0/24","10.50.0.0/24"))==16 && File.ReadAllBytes(path).SequenceEqual(before), "Editor must validate the current recovery policy against changed routes.");
            using(var gate = new FileStream(Path.Combine(directory,"manager.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None))
                Require(Save(1,config,changed)==15, "Another session's privileged operation must serialize editor writes.");
            Require(Save(1,config,changed)==0 && MachineProfile.Read(path)==changed, "A valid stopped profile must save encrypted and decrypt with its new contents.");
            Require(!System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains("PrivateKey") && !File.Exists(path+".new"), "No plaintext or staging file may remain after saving.");
            Require(Save(1,config,config)==18 && MachineProfile.Read(path)==changed, "A stale RDP editor must not overwrite a concurrent change.");
            Require(MachineProfile.Read(Path.Combine(directory,"profile.dpapi"))==changed, "Selected profile edits must also apply to service autostart after a Windows reboot.");
            File.WriteAllText(catalogPath, JsonSerializer.Serialize(new AwgProfileCatalog { Profiles=[new(id,"Fixture")] }));
            Require(Save(1,changed,config)==0 && MachineProfile.Read(Path.Combine(directory,"profile.dpapi"))==changed, "Editing an inactive profile must preserve the active profile.");
            File.WriteAllText(catalogPath, JsonSerializer.Serialize(new AwgProfileCatalog { SelectedId=id, Profiles=[new(id,"Fixture")] }));
            File.SetAttributes(path, FileAttributes.ReadOnly);
            try
            {
                Require(Save(1,config,changed.Replace("192.0.2.11","192.0.2.12"))!=0 && MachineProfile.Read(path)==config && MachineProfile.Read(Path.Combine(directory,"profile.dpapi"))==changed,
                    "A failed stored-profile replacement must roll back the active encrypted copy.");
            }
            finally { File.SetAttributes(path,FileAttributes.Normal); }

        }
        finally { foreach(var file in Directory.GetFiles(directory)) File.Delete(file); Directory.Delete(directory); }
        var at = DateTimeOffset.Now;
        var health = new AwgConnectionState("Backup",true,at.ToUnixTimeSeconds(),at,at,1,"192.0.2.11:51820",true,true);
        Require(AwgEndpointHealth.Describe("192.0.2.11:51820",health).Contains("подтверждено"), "Only own-tunnel proof may confirm active backup health.");
        Require(AwgEndpointHealth.Describe("192.0.2.12:51820",health).Contains("не подтверждён"), "Another endpoint's success must not certify the selected backup.");
        Require(!AwgEndpointHealth.Describe("192.0.2.11:51820",health with { Healthy=false }).Contains("подтверждено"), "Unhealthy backup must not display success.");
        Console.WriteLine("PASS: lossless masked profile editing, DPAPI atomic saves, engine/policy rejection, session conflicts and honest reserve health.");
    }
    private static void Require(bool value,string message) { if(!value) throw new Exception(message); }
}
