using System.Net;
using System.Text;
namespace VpnMonitor.Client.Services;

/// <summary>Read-only structural validation, not an engine compatibility guarantee.</summary>
public static class AwgConfigPreview
{
    public static string Parse(string text)
    {
        if (text.Length > 256 * 1024) throw new ArgumentException("Профиль превышает 256 КБ.");
        var sections = new List<(string Name, Dictionary<string, string> Values)>();
        Dictionary<string, string>? current = null;
        var lineNumber = 0;
        foreach (var raw in text.TrimStart('\uFEFF').Split('\n'))
        {
            lineNumber++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';')) continue;
            if (line.StartsWith('['))
            {
                if (line is not ("[Interface]" or "[Peer]")) throw Error(lineNumber, "неизвестная секция");
                current = new(StringComparer.OrdinalIgnoreCase);
                sections.Add((line[1..^1], current));
                continue;
            }
            var equal = line.IndexOf('=');
            if (current is null || equal < 1) throw Error(lineNumber, "ожидается параметр внутри секции");
            var key = line[..equal].Trim();
            var value = line[(equal + 1)..].Trim();
            if (!current.TryAdd(key, value)) throw Error(lineNumber, "повтор параметра");
        }
        if (sections.Count < 2 || sections[0].Name != "Interface" ||
            sections.Count(s => s.Name == "Interface") != 1 || !sections.Any(s => s.Name == "Peer"))
            throw new ArgumentException("Нужны одна секция Interface и хотя бы одна Peer.");
        var iface = sections[0].Values;
        ValidateKey(iface.GetValueOrDefault("PrivateKey"), "PrivateKey");
        var addresses = iface.GetValueOrDefault("Address") ?? "";
        ValidateNetworks(addresses, "Address");
        var output = new StringBuilder("Структура профиля проверена. Совместимость движка ещё не проверена.\nПодключение не выполняется, ключи не сохраняются.\n\n");
        output.AppendLine("INTERFACE").AppendLine($"Адреса: {addresses}");
        if (iface.TryGetValue("MTU", out var mtu))
        {
            if (!int.TryParse(mtu, out var n) || n is < 576 or > 65535) throw new ArgumentException("Некорректный MTU.");
            output.AppendLine($"MTU: {mtu}");
        }
        var scripts = new[] { "PreUp", "PostUp", "PreDown", "PostDown" };
        var known = new HashSet<string>(new[] { "PrivateKey", "Address", "DNS", "MTU", "ListenPort", "Table" }.Concat(scripts), StringComparer.OrdinalIgnoreCase);
        var awg = iface.Keys.Where(k => !known.Contains(k)).ToArray();
        output.AppendLine($"Дополнительных параметров AWG: {awg.Length}");
        if (awg.Length > 0) output.AppendLine("Обнаружены дополнительные параметры; для их проверки требуется движок AWG.");
        if (scripts.Any(iface.ContainsKey)) output.AppendLine("ВНИМАНИЕ: есть команды Pre/Post. Они не выполнялись; запуск такого профиля пока запрещён.");
        var number = 0;
        foreach (var section in sections.Skip(1))
        {
            var values = section.Values;
            ValidateKey(values.GetValueOrDefault("PublicKey"), "PublicKey");
            if (values.TryGetValue("PresharedKey", out var psk)) ValidateKey(psk, "PresharedKey");
            var networks = values.GetValueOrDefault("AllowedIPs") ?? "";
            ValidateNetworks(networks, "AllowedIPs");
            output.AppendLine().AppendLine($"PEER {++number}").AppendLine($"Маршруты: {networks}");
            if (values.TryGetValue("Endpoint", out var endpoint))
            {
                if (!Uri.TryCreate("udp://" + endpoint, UriKind.Absolute, out var uri) ||
                    string.IsNullOrEmpty(uri.Host) || uri.Port is < 1 or > 65535 ||
                    uri.UserInfo.Length > 0 || uri.AbsolutePath is not ("" or "/") || uri.Query.Length > 0 || uri.Fragment.Length > 0)
                    throw new ArgumentException("Некорректный Endpoint.");
                output.AppendLine($"Endpoint: {endpoint}");
            }
            if (values.TryGetValue("PersistentKeepalive", out var keepalive))
                if (!int.TryParse(keepalive, out var seconds) || seconds is < 0 or > 65535)
                    output.AppendLine("PersistentKeepalive требует проверки движком AWG.");
            if (networks.Split(',').Any(n => n.Trim() is "0.0.0.0/0" or "::/0"))
                output.AppendLine("ВНИМАНИЕ: профиль содержит маршрут всего трафика.");
        }
        output.AppendLine().AppendLine("PrivateKey, PublicKey, PresharedKey и значения дополнительных параметров скрыты.");
        return output.ToString();
    }
    private static ArgumentException Error(int line, string reason) => new($"Строка {line}: {reason}. Содержимое скрыто.");
    private static void ValidateKey(string? value, string field)
    {
        try { if (value is null || Convert.FromBase64String(value).Length != 32) throw new FormatException(); }
        catch (FormatException) { throw new ArgumentException($"Некорректный ключ {field}; значение скрыто."); }
    }
    private static void ValidateNetworks(string values, string field)
    {
        foreach (var value in values.Split(','))
        {
            var parts = value.Trim().Split('/');
            if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var address) ||
                !int.TryParse(parts[1], out var prefix) || prefix < 0 ||
                prefix > (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128))
                throw new ArgumentException($"Некорректный адрес или префикс в {field}; значение скрыто.");
        }
    }
}