using System.Text.RegularExpressions;

namespace VpnMonitor.Client.Services;

public sealed class DiagnosticRedactor
{
    private readonly string[] _secrets;
    private const string Mask = "[скрыто]";
    private const string Fields = @"PrivateKey|PresharedKey|ProtectedPassword|Password|passwd|pwd|пароль|Access_?Token|Refresh_?Token|Api_?Key|Client_?Secret|token|secret|Authorization|Proxy-Authorization";
    public DiagnosticRedactor(IEnumerable<string>? secrets = null) =>
        _secrets = (secrets ?? []).Where(s => !string.IsNullOrEmpty(s)).Distinct().OrderByDescending(s => s.Length).ToArray();

    public string Redact(string text)
    {
        text = Replace(text, @"https?://[^\s<>""']+", match =>
        {
            if (!Uri.TryCreate(match.Value, UriKind.Absolute, out var uri)) return Mask;
            var clean = new UriBuilder(uri) { UserName = "", Password = "", Query = "", Fragment = "" }.Uri.AbsoluteUri;
            return clean + (uri.Query.Length + uri.Fragment.Length > 0 ? "?[скрыто]" : "");
        });
        text = Replace(text, @"(?im)\b(?:Proxy-)?Authorization\s*[:=][^\r\n]*", _ => "Authorization: " + Mask);
        text = Replace(text, @"(?i)\b(Bearer|Basic)\s+[A-Za-z0-9._~+/-]+=*", m => m.Groups[1].Value + " " + Mask);
        text = Replace(text, "(?i)\"(" + Fields + ")\"\\s*:\\s*\"(?:\\\\.|[^\"\\\\])*\"", m => "\"" + m.Groups[1].Value + "\":\"" + Mask + "\"");
        text = Replace(text, @"(?im)\b(" + Fields + @")\b\s*[:=]\s*(?:""[^""\r\n]*""|'[^'\r\n]*'|[^\s,;}\r\n]+)", m => m.Groups[1].Value + "=" + Mask);
        text = Replace(text, @"(?<![A-Za-z0-9+/])[A-Za-z0-9+/]{43}=(?![A-Za-z0-9+/=])", _ => Mask);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile)) text = text.Replace(profile, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);
        text = Replace(text, @"(?i)[A-Z]:\\Users\\[^\\\r\n""<>]+", _ => "%USERPROFILE%");
        foreach (var secret in _secrets) text = text.Replace(secret, Mask, StringComparison.Ordinal);
        return text;
    }

    private static string Replace(string text, string pattern, MatchEvaluator evaluator) =>
        Regex.Replace(text, pattern, evaluator, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
}
