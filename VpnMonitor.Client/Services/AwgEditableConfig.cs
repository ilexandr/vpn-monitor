using System.Text.RegularExpressions;
namespace VpnMonitor.Client.Services;

/// <summary>A lossless editor view: existing keys stay in memory, never in placeholders or error messages.</summary>
public sealed class AwgEditableConfig
{
    private readonly Dictionary<string, (string Field, string Value)> _secrets = new();
    public string Text { get; }
    public AwgEditableConfig(string config)
    {
        if (config.Length > 256 * 1024) throw new ArgumentException("Профиль превышает 256 КБ.");
        Text = Regex.Replace(config, @"(?im)^(\s*(?:PrivateKey|PublicKey|PresharedKey)\s*=\s*)([^\r\n]*)", m =>
        {
            var token = "<скрыто:" + Guid.NewGuid().ToString("N") + ">";
            _secrets.Add(token, (m.Groups[1].Value.Split('=')[0].Trim(), m.Groups[2].Value));
            return m.Groups[1].Value + token;
        });
    }
    public string Restore(string text)
    {
        if (text.Length > 256 * 1024) throw new ArgumentException("Профиль превышает 256 КБ.");
        foreach (var pair in _secrets)
        {
            // A placeholder may remain only as an entire key value, never in another field or comment.
            var pattern = @"(?im)^(\s*" + Regex.Escape(pair.Value.Field) + @"\s*=\s*)" + Regex.Escape(pair.Key) + @"(?=\r?$)";
            text = Regex.Replace(text, pattern, m => m.Groups[1].Value + pair.Value.Value);
        }
        if (text.Contains("<скрыто:", StringComparison.Ordinal)) throw new ArgumentException("Метка скрытого ключа повреждена или перенесена в другой параметр.");
        return text;
    }
    public static string Endpoints(string config) => string.Join(", ", Regex.Matches(config, @"(?im)^\s*Endpoint\s*=\s*([^\r\n]+)")
        .Select(m => m.Groups[1].Value.Trim()));
}
