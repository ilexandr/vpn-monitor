using System.IO;
using System.Text.Json;
using AwgPrototype;
using VpnMonitor.Client.Models;

internal static class ProfileEditorStore
{
    internal static int Save(string data, string catalogFile, string id, string original, string config, Func<uint> state, Action<string> validate)
    {
        try
        {
            using var gate = new FileStream(Path.Combine(data, "manager.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            // Recheck under the common lock: another RDP session may have connected while the editor was open.
            if(state() != 1) return 12;
            if(!Guid.TryParseExact(id, "N", out _)) return 16;
            var catalog = JsonSerializer.Deserialize<AwgProfileCatalog>(File.ReadAllText(catalogFile)) ?? throw new InvalidOperationException();
            _ = catalog.Profiles.Single(p => p.Id == id);
            var target = Path.Combine(data, id + ".dpapi");
            if(!string.Equals(MachineProfile.Read(target), original, StringComparison.Ordinal)) return 18;
            if(config.Length > 256 * 1024) return 16;
            validate(config);
            var policyPath = Path.Combine(data, id + ".connection.json");
            var policy = File.Exists(policyPath) ? JsonSerializer.Deserialize<AwgConnectionOptions>(File.ReadAllText(policyPath)) ?? throw new ArgumentException() : new();
            policy.Validate(config);
            if(state() != 1) return 12;
            var next = target + ".new";
            var active = Path.Combine(data, "profile.dpapi");
            var activeNext = active + ".new";
            byte[]? previousActive = null;
            var activeChanged = false;
            try
            {
                ProfileProtector.Write(config, next);
                if(catalog.SelectedId == id)
                {
                    previousActive = File.Exists(active) ? File.ReadAllBytes(active) : null;
                    File.Copy(next, activeNext, true);
                    File.Move(activeNext, active, true);
                    activeChanged = true;
                }
                try { File.Move(next, target, true); }
                catch
                {
                    // A failed second replacement restores the encrypted active copy, never plaintext.
                    if(activeChanged)
                    {
                        try
                        {
                            if(previousActive is null) File.Delete(active);
                            else { File.WriteAllBytes(activeNext, previousActive); File.Move(activeNext, active, true); }
                        }
                        catch { return 19; }
                    }
                    throw;
                }
            }
            finally
            {
                if(File.Exists(next)) File.Delete(next);
                if(File.Exists(activeNext)) File.Delete(activeNext);
            }
            return 0;
        }
        catch(IOException) { return 15; }
        catch { return 16; }
    }
}
