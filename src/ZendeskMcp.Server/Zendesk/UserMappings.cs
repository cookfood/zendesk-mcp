using System.Diagnostics.CodeAnalysis;

namespace ZendeskMcp.Server.Zendesk;

/// <summary>
/// Maps the e-mail address a person signs in with to the Zendesk agent e-mail their
/// requests are sent as. Used when <see cref="UserLoginMode.Mapped"/> is on.
///
/// Entries look like <c>person@example.com=agent@example.com</c>. An entry with no
/// <c>=</c> maps the address to itself. Entries are separated by <c>;</c>, <c>,</c> or
/// new lines. Addresses are matched without regard to case.
/// </summary>
public sealed class UserMappings
{
    private static readonly char[] EntrySeparators = [';', ',', '\n', '\r'];

    private readonly Dictionary<string, string> _map;

    private UserMappings(Dictionary<string, string> map) => _map = map;

    public static UserMappings Empty { get; } = new(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    /// <summary>The number of signed-in addresses that are allowed to use the server.</summary>
    public int Count => _map.Count;

    /// <summary>
    /// Looks up the Zendesk agent e-mail for a signed-in address. Returns false when the
    /// address is blank or not in the mapping.
    /// </summary>
    public bool TryGetZendeskEmail(string? signInEmail, [NotNullWhen(true)] out string? zendeskEmail)
    {
        zendeskEmail = null;
        return !string.IsNullOrWhiteSpace(signInEmail)
            && _map.TryGetValue(signInEmail.Trim(), out zendeskEmail);
    }

    /// <summary>
    /// Parses one or more configuration values, each holding one or more entries.
    /// </summary>
    /// <exception cref="FormatException">An entry is malformed, or one address is mapped to two different agents.</exception>
    public static UserMappings Parse(IEnumerable<string?> values)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var value in values)
        {
            if (string.IsNullOrWhiteSpace(value)) continue;

            var entries = value.Split(EntrySeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            foreach (var entry in entries)
            {
                var parts = entry.Split('=', StringSplitOptions.TrimEntries);
                if (parts.Length > 2)
                {
                    throw new FormatException(
                        $"User mapping '{entry}' has more than one '='. Use 'person@example.com=agent@example.com'.");
                }

                var signInEmail = parts[0];
                var zendeskEmail = parts.Length == 2 ? parts[1] : parts[0];

                if (!LooksLikeEmail(signInEmail) || !LooksLikeEmail(zendeskEmail))
                {
                    throw new FormatException(
                        $"User mapping '{entry}' is not valid. Use 'person@example.com=agent@example.com', " +
                        "or just 'person@example.com' to map an address to itself.");
                }

                if (map.TryGetValue(signInEmail, out var existing)
                    && !string.Equals(existing, zendeskEmail, StringComparison.OrdinalIgnoreCase))
                {
                    throw new FormatException(
                        $"'{signInEmail}' is mapped twice, to '{existing}' and to '{zendeskEmail}'. Remove one of the entries.");
                }

                map[signInEmail] = zendeskEmail;
            }
        }

        return new UserMappings(map);
    }

    /// <inheritdoc cref="Parse(IEnumerable{string?})"/>
    public static UserMappings Parse(string? value) => Parse([value]);

    // A light check that catches typos (a missing '@', a stray space), not a full RFC 5322 parser.
    private static bool LooksLikeEmail(string value)
    {
        var at = value.IndexOf('@');
        return at > 0
            && at == value.LastIndexOf('@')
            && at < value.Length - 1
            && !value.Any(char.IsWhiteSpace);
    }
}
