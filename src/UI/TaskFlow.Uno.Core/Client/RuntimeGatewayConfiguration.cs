using System.Text.Json;

namespace TaskFlow.Uno.Core.Client;

/// <summary>Loads and validates the deployment-supplied gateway origin for static WASM hosting.</summary>
public static class RuntimeGatewayConfiguration
{
    /// <summary>Parses the lane-neutral <c>/app-config.json</c> contract.</summary>
    public static string Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("gatewayBaseUrl", out var gateway)
                || gateway.ValueKind != JsonValueKind.String)
            {
                throw new InvalidOperationException("Runtime configuration must define gatewayBaseUrl.");
            }

            var value = gateway.GetString();
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new InvalidOperationException("Runtime configuration gatewayBaseUrl must be an absolute HTTP(S) URL.");
            }

            return uri.ToString().TrimEnd('/');
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Runtime configuration is not valid JSON.", exception);
        }
    }

    /// <summary>Fetches and validates the runtime configuration before the UI host starts.</summary>
    public static async Task<string> LoadAsync(HttpClient client, CancellationToken cancellationToken = default)
    {
        using var response = await client.GetAsync("app-config.json", cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Runtime configuration request failed with {(int)response.StatusCode}.");
        }

        return Parse(await response.Content.ReadAsStringAsync(cancellationToken));
    }
}
