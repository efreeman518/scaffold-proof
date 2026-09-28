using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Net;

namespace Microsoft.Extensions.Hosting;

/// <summary>Configures forwarded request metadata at a controlled reverse-proxy boundary.</summary>
public static class ProxyForwardingExtensions
{
    private const string ForwardedHeadersSection = "Proxy:ForwardedHeaders";
    private const string PathBaseKey = "Proxy:PathBase";

    /// <summary>Registers explicit forwarded-header trust settings when proxy forwarding is enabled.</summary>
    public static IHostApplicationBuilder AddProxyForwarding(this IHostApplicationBuilder builder)
    {
        var configuration = builder.Configuration.GetSection(ForwardedHeadersSection);
        if (!configuration.GetValue<bool>("Enabled"))
        {
            return builder;
        }

        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor
                | ForwardedHeaders.XForwardedProto
                | ForwardedHeaders.XForwardedHost;

            var knownProxies = configuration.GetSection("KnownProxies").Get<string[]>() ?? [];
            var knownNetworks = configuration.GetSection("KnownNetworks").Get<string[]>() ?? [];
            var trustAllProxies = configuration.GetValue<bool>("TrustAllProxies");

            if (trustAllProxies)
            {
                if (knownProxies.Length > 0 || knownNetworks.Length > 0)
                {
                    throw new InvalidOperationException(
                        $"{ForwardedHeadersSection}:TrustAllProxies cannot be combined with proxy or network allowlists.");
                }

                // Use only when network policy prevents direct access to this process. A publicly
                // reachable host must use explicit KnownProxies/KnownNetworks instead. An unset ForwardLimit
                // reads the whole X-Forwarded-For chain; set it to the number of proxy hops in front of the
                // host (1 behind Container Apps ingress) so a client-supplied X-Forwarded-For entry cannot
                // become the remote address - per-IP rate-limit partitions depend on that.
                options.ForwardLimit = configuration.GetValue<int?>("ForwardLimit");
                if (options.ForwardLimit <= 0)
                {
                    throw new InvalidOperationException(
                        $"{ForwardedHeadersSection}:ForwardLimit must be greater than zero.");
                }

                options.KnownProxies.Clear();
                options.KnownIPNetworks.Clear();
            }
            else
            {
                options.ForwardLimit = configuration.GetValue<int?>("ForwardLimit") ?? 1;
                if (options.ForwardLimit <= 0)
                {
                    throw new InvalidOperationException(
                        $"{ForwardedHeadersSection}:ForwardLimit must be greater than zero.");
                }

                if (knownProxies.Length > 0 || knownNetworks.Length > 0)
                {
                    options.KnownProxies.Clear();
                    options.KnownIPNetworks.Clear();
                }

                foreach (var value in knownProxies)
                {
                    if (!IPAddress.TryParse(value, out var address))
                    {
                        throw new InvalidOperationException(
                            $"Invalid proxy address '{value}' in {ForwardedHeadersSection}:KnownProxies.");
                    }

                    options.KnownProxies.Add(address);
                }

                foreach (var value in knownNetworks)
                {
                    if (!System.Net.IPNetwork.TryParse(value, out var network))
                    {
                        throw new InvalidOperationException(
                            $"Invalid CIDR network '{value}' in {ForwardedHeadersSection}:KnownNetworks.");
                    }

                    options.KnownIPNetworks.Add(network);
                }
            }

            var allowedHosts = configuration.GetSection("AllowedHosts").Get<string[]>() ?? [];
            foreach (var host in allowedHosts)
            {
                options.AllowedHosts.Add(host);
            }
        });

        return builder;
    }

    /// <summary>Applies forwarded headers and an optional configured path base before host middleware.</summary>
    public static WebApplication UseProxyForwarding(this WebApplication app)
    {
        if (app.Configuration.GetValue<bool>($"{ForwardedHeadersSection}:Enabled"))
        {
            app.UseForwardedHeaders();
        }

        var pathBase = app.Configuration[PathBaseKey]?.Trim();
        if (string.IsNullOrEmpty(pathBase))
        {
            return app;
        }

        if (!pathBase.StartsWith('/') || pathBase.Length > 1 && pathBase.EndsWith('/'))
        {
            throw new InvalidOperationException(
                $"{PathBaseKey} must start with '/' and must not end with '/'.");
        }

        app.UsePathBase(pathBase);
        return app;
    }
}
