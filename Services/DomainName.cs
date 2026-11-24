using System.Globalization;
using System.Text.RegularExpressions;

namespace GigaCleaner.Services;

public static class DomainName
{
    private static readonly Regex HostPattern = new(
        @"^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$",
        RegexOptions.Compiled);

    public static string? TryNormalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        var text = input.Trim();
        var withScheme = text.Contains("://", StringComparison.Ordinal) ? text : "https://" + text;
        if (!Uri.TryCreate(withScheme, UriKind.Absolute, out var uri) || string.IsNullOrWhiteSpace(uri.Host))
            return null;

        text = uri.Host.Trim('.').ToLowerInvariant();
        if (text.StartsWith("www.", StringComparison.Ordinal)) text = text[4..];

        try
        {
            text = new IdnMapping().GetAscii(text);
        }
        catch (ArgumentException)
        {
            return null;
        }

        return HostPattern.IsMatch(text) ? text : null;
    }

    public static bool Matches(string hostKey, string domain)
    {
        var host = hostKey.Trim().TrimStart('.').ToLowerInvariant();
        var colon = host.IndexOf(':');
        if (colon >= 0) host = host[..colon];
        return host.Equals(domain, StringComparison.Ordinal) ||
               host.EndsWith("." + domain, StringComparison.Ordinal);
    }
}
