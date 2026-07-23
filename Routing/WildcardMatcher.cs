namespace Pako.SNIProxy.Routing;

public static class WildcardMatcher
{
    public static bool IsMatch(string pattern, string domain)
    {
        if (string.IsNullOrEmpty(pattern) || string.IsNullOrEmpty(domain))
            return false;

        if (pattern.StartsWith("*.", StringComparison.Ordinal))
        {
            // "*.example.com" matches "www.example.com" and "a.b.example.com",
            // but NOT "example.com" itself.
            string suffix = pattern[1..]; // ".example.com"
            return domain.Length > suffix.Length
                   && domain.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
        }

        return string.Equals(pattern, domain, StringComparison.OrdinalIgnoreCase);
    }
}
