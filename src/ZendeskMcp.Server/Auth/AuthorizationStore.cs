using System.Collections.Concurrent;

namespace ZendeskMcp.Server.Auth;

public class AuthSession
{
    public required string ClientRedirectUri { get; init; }
    public required string ClientState { get; init; }
    public required string ClientCodeChallenge { get; init; }
    public required string ClientCodeChallengeMethod { get; init; }
    public required string InternalCodeVerifier { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

public class IssuedCode
{
    public required string TokenResponseJson { get; init; }
    public required string ClientCodeChallenge { get; init; }
    public required string ClientCodeChallengeMethod { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool Redeemed { get; set; }
}

public class ClientRegistration
{
    public required string ClientId { get; init; }
    public required string ClientName { get; init; }
    public required string[] RedirectUris { get; init; }
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// In-memory store for the short-lived OAuth bridge state: pending authorisation
/// sessions, issued authorisation codes, and dynamically registered clients.
/// Entries are swept by <see cref="AuthorizationStoreCleanup"/>.
/// </summary>
public class AuthorizationStore
{
    private readonly ConcurrentDictionary<string, AuthSession> _sessions = new();
    private readonly ConcurrentDictionary<string, IssuedCode> _codes = new();
    private readonly ConcurrentDictionary<string, ClientRegistration> _registrations = new();

    public void StoreSession(string internalState, AuthSession session) =>
        _sessions[internalState] = session;

    public AuthSession? GetAndRemoveSession(string internalState) =>
        _sessions.TryRemove(internalState, out var session) ? session : null;

    public void StoreCode(string code, IssuedCode issuedCode) =>
        _codes[code] = issuedCode;

    public IssuedCode? RedeemCode(string code)
    {
        if (!_codes.TryRemove(code, out var issuedCode)) return null;
        if (issuedCode.Redeemed) return null;
        issuedCode.Redeemed = true;
        return issuedCode;
    }

    public void StoreRegistration(ClientRegistration registration) =>
        _registrations[registration.ClientId] = registration;

    public ClientRegistration? GetRegistration(string clientId) =>
        _registrations.TryGetValue(clientId, out var reg) ? reg : null;

    public void Cleanup(TimeSpan sessionTtl, TimeSpan codeTtl, TimeSpan registrationTtl)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var (key, session) in _sessions)
            if (now - session.CreatedAt > sessionTtl) _sessions.TryRemove(key, out _);
        foreach (var (key, code) in _codes)
            if (now - code.CreatedAt > codeTtl) _codes.TryRemove(key, out _);
        foreach (var (key, reg) in _registrations)
            if (now - reg.CreatedAt > registrationTtl) _registrations.TryRemove(key, out _);
    }
}

public class AuthorizationStoreCleanup(AuthorizationStore store) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            store.Cleanup(
                sessionTtl: TimeSpan.FromMinutes(10),
                codeTtl: TimeSpan.FromMinutes(5),
                registrationTtl: TimeSpan.FromHours(24));
        }
    }
}
