using Azure.Core;
using System.Collections.Concurrent;

namespace TaskFlow.Gateway;

/// <summary>
/// Acquires and caches client-credential tokens per downstream cluster.
/// Injects TokenCredential (DefaultAzureCredential) for real token acquisition.
/// Falls back to scaffold stub tokens when no credential is configured.
/// <para>
/// Single-flight: the cache holds the in-flight acquisition, not the finished token, so a burst of requests
/// for an expired token produces one call to the identity provider instead of one per request. Process-local
/// by design - the gateway runs behind the load balancer, so per-replica is the right scope, and a token is
/// not worth a distributed lock.
/// </para>
/// </summary>
public sealed class TokenService
{
    /// <summary>Refresh this far ahead of expiry so an in-flight downstream call cannot outlive its token.</summary>
    private static readonly TimeSpan RefreshWindow = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<string, Lazy<Task<AccessToken>>> _cache = new();
    private readonly ILogger<TokenService> _logger;
    private readonly TokenCredential _credential;
    private readonly IConfiguration _config;

    /// <summary>Converts the current value to ken service.</summary>
    public TokenService(ILogger<TokenService> logger, TokenCredential credential, IConfiguration config)
    {
        _logger = logger;
        _credential = credential;
        _config = config;
    }

    /// <summary>Loads requested data and maps missing records to the expected response.</summary>
    public async Task<string> GetAccessTokenAsync(string clusterId, CancellationToken ct = default)
    {
        while (true)
        {
            // ExecutionAndPublication: exactly one thread runs the factory, everyone else awaits its task.
            var pending = _cache.GetOrAdd(
                clusterId,
                key => new Lazy<Task<AccessToken>>(
                    () => AcquireAsync(key, ct), LazyThreadSafetyMode.ExecutionAndPublication));

            AccessToken token;
            try
            {
                token = await pending.Value.ConfigureAwait(false);
            }
            catch
            {
                // A faulted Lazy would otherwise be cached forever and every later caller would replay the
                // same failure. Remove this exact instance (never a newer one) and let the caller see the error.
                RemoveIfSame(clusterId, pending);
                throw;
            }

            if (token.ExpiresOn > DateTimeOffset.UtcNow.Add(RefreshWindow))
                return token.Token;

            // Near expiry: evict this entry and loop. Compare-and-remove so a refresh started by another
            // thread is not discarded, which would make every caller in the window acquire its own token.
            RemoveIfSame(clusterId, pending);
        }
    }

    /// <summary>Acquires one token, or issues a local stub when the cluster has no configured scope.</summary>
    private async Task<AccessToken> AcquireAsync(string clusterId, CancellationToken ct)
    {
        var scope = _config.GetSection($"ReverseProxy:Clusters:{clusterId}:TokenScope").Value;

        if (!string.IsNullOrWhiteSpace(scope))
        {
            var tokenResult = await _credential.GetTokenAsync(new TokenRequestContext([scope]), ct)
                .ConfigureAwait(false);
            _logger.TokenAcquired(clusterId, tokenResult.ExpiresOn);
            return tokenResult;
        }

        // Scaffold stub: return a fixed token for local development
        var expiry = DateTimeOffset.UtcNow.AddHours(1);
        _logger.ScaffoldTokenIssued(clusterId, expiry);
        return new AccessToken($"scaffold-token-{clusterId}-{Guid.NewGuid():N}", expiry);
    }

    /// <summary>Removes the entry only while it is still the one this caller observed.</summary>
    private void RemoveIfSame(string clusterId, Lazy<Task<AccessToken>> observed) =>
        ((ICollection<KeyValuePair<string, Lazy<Task<AccessToken>>>>)_cache)
            .Remove(new KeyValuePair<string, Lazy<Task<AccessToken>>>(clusterId, observed));
}
