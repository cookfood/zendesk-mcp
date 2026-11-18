namespace ZendeskMcp.Server.Zendesk;

/// <summary>
/// Chooses which Zendesk agent e-mail address requests are sent as.
/// </summary>
public enum UserLoginMode
{
    /// <summary>
    /// Every request is sent as the one agent e-mail in <see cref="ZendeskOptions.Email"/>.
    /// This is the default.
    /// </summary>
    Shared,

    /// <summary>
    /// Each signed-in user is mapped to their own agent e-mail through
    /// <see cref="ZendeskOptions.UserMappings"/>. Anyone who is not in the mapping is refused.
    /// Needs the HTTP transport with Microsoft Entra ID sign-in, so the server knows who is calling.
    /// </summary>
    Mapped,
}

public static class UserLoginModes
{
    /// <summary>
    /// Parses a configured mode, ignoring case. A blank value means <see cref="UserLoginMode.Shared"/>.
    /// </summary>
    /// <exception cref="FormatException">The value is not a recognised mode.</exception>
    public static UserLoginMode Parse(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return UserLoginMode.Shared;

        foreach (var mode in Enum.GetValues<UserLoginMode>())
        {
            if (string.Equals(mode.ToString(), value.Trim(), StringComparison.OrdinalIgnoreCase))
                return mode;
        }

        throw new FormatException(
            $"UserLoginMode '{value.Trim()}' is not recognised. Use 'Shared' (one Zendesk e-mail for everyone) " +
            "or 'Mapped' (each signed-in user is mapped to their own Zendesk e-mail).");
    }
}
