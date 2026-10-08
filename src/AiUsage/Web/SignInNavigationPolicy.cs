namespace AiUsage.Web;

/// <summary>
/// Which hosts a sign-in window's embedded session may navigate to. The user is on the provider's
/// own login flow; nothing should be able to steer the session anywhere else. The allow-list itself
/// lives on each provider's own <see cref="WebSessionDescriptor"/> (e.g. Claude's also allows
/// accounts.google.com, since its own "Sign in with Google" button leaves the provider's site for
/// Google's real login/consent flow before returning) - this class only holds the shared matching
/// rules, never a provider name.
/// </summary>
public static class SignInNavigationPolicy
{
    /// <summary>An allow-list entry that stands for Google's account host in each country domain
    /// (<c>accounts.google.de</c>, <c>accounts.google.co.uk</c>, ...). After a login Google hands the
    /// session on through the account host of the user's own country to set its cookies, and a plain
    /// host entry cannot name them all. It matches the exact host only (no subdomains) and only the
    /// country domains Google itself runs, so a look-alike such as <c>accounts.google.de.evil.com</c>
    /// or a country domain Google does not own never matches.</summary>
    public const string GoogleCountryAccounts = "accounts.google.*";

    private const string GoogleAccountsPrefix = "accounts.google.";

    // The country suffixes of Google's own search domains (google.com/supported_domains), without the
    // leading dot. Kept as a closed list: a pattern like "any two letters" would also admit country
    // domains an outsider can register.
    private static readonly HashSet<string> GoogleCountrySuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "ad", "ae", "al", "am", "as", "at", "az", "ba", "be", "bf", "bg", "bi", "bj", "bs", "bt", "by", "ca", "cat",
        "cd", "cf", "cg", "ch", "ci", "cl", "cm", "cn", "cv", "cz", "de", "dj", "dk", "dm", "dz", "ee", "es", "fi",
        "fm", "fr", "ga", "ge", "gg", "gl", "gm", "gr", "gy", "hn", "hr", "ht", "hu", "ie", "im", "iq", "is", "it",
        "je", "jo", "kg", "ki", "kz", "la", "li", "lk", "lt", "lu", "lv", "md", "me", "mg", "mk", "ml", "mn", "ms",
        "mu", "mv", "mw", "ne", "nl", "no", "nr", "nu", "pl", "pn", "ps", "pt", "ro", "rs", "ru", "rw", "sc", "se",
        "sh", "si", "sk", "sm", "sn", "so", "sr", "st", "td", "tg", "tl", "tm", "tn", "to", "tt", "vg", "vu", "ws",
        "co.bw", "co.ck", "co.cr", "co.id", "co.il", "co.in", "co.jp", "co.ke", "co.kr", "co.ls", "co.ma", "co.mz",
        "co.nz", "co.th", "co.tz", "co.ug", "co.uk", "co.uz", "co.ve", "co.vi", "co.za", "co.zm", "co.zw",
        "com.af", "com.ag", "com.ai", "com.ar", "com.au", "com.bd", "com.bh", "com.bn", "com.bo", "com.br", "com.bz",
        "com.co", "com.cu", "com.cy", "com.do", "com.ec", "com.eg", "com.et", "com.fj", "com.gh", "com.gi", "com.gt",
        "com.hk", "com.jm", "com.kh", "com.kw", "com.lb", "com.ly", "com.mm", "com.mt", "com.mx", "com.my", "com.na",
        "com.ng", "com.ni", "com.np", "com.om", "com.pa", "com.pe", "com.pg", "com.ph", "com.pk", "com.pr", "com.py",
        "com.qa", "com.sa", "com.sb", "com.sg", "com.sl", "com.sv", "com.tj", "com.tr", "com.tw", "com.ua", "com.uy",
        "com.vc", "com.vn",
    };

    /// <summary>True for an allowed host or any of its subdomains (e.g. <c>www.example.com</c>,
    /// <c>login.example.com</c>); false for anything else, including a look-alike such as
    /// <c>example.com.attacker.test</c>. The <see cref="GoogleCountryAccounts"/> entry is matched by
    /// its own rule instead.</summary>
    public static bool IsAllowedHost(string host, IReadOnlyList<string> allowedHosts)
    {
        foreach (var allowed in allowedHosts)
        {
            if (allowed == GoogleCountryAccounts)
            {
                if (IsGoogleCountryAccountsHost(host))
                    return true;
                continue;
            }

            if (host.Equals(allowed, StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith("." + allowed, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool IsGoogleCountryAccountsHost(string host) =>
        host.StartsWith(GoogleAccountsPrefix, StringComparison.OrdinalIgnoreCase)
        && GoogleCountrySuffixes.Contains(host[GoogleAccountsPrefix.Length..]);

    /// <summary>The scheme check <see cref="IsAllowedHost"/> alone cannot make: an allowed host
    /// reached over plain http would still carry the session's cookies unencrypted on the way to
    /// whatever upgrade redirect follows. False for anything that fails to parse as an absolute URI,
    /// is not https, or fails the host check.</summary>
    public static bool IsAllowedUri(string url, IReadOnlyList<string> allowedHosts) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && IsAllowedHost(uri.Host, allowedHosts);

    /// <summary>Stricter than <see cref="IsAllowedUri"/>: gates a hidden session's usage fetch, which
    /// only ever legitimately talks to the provider's own <paramref name="expectedOrigin"/> host -
    /// unlike the sign-in flow, it never visits any other host on the sign-in allow-list (e.g. a
    /// Google sign-in redirect). True only for an absolute https URI whose host is exactly
    /// <paramref name="expectedOrigin"/> or one of its subdomains.</summary>
    public static bool IsUsageOrigin(string url, string expectedOrigin) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
        && uri.Scheme == Uri.UriSchemeHttps
        && (uri.Host.Equals(expectedOrigin, StringComparison.OrdinalIgnoreCase)
            || uri.Host.EndsWith("." + expectedOrigin, StringComparison.OrdinalIgnoreCase));
}
