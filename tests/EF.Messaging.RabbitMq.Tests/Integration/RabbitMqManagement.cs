using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace EF.Messaging.RabbitMq.Tests.Integration;

/// <summary>
/// Thin client over the broker's management HTTP API (port 15672), used to observe connection and channel counts
/// and per-queue unacknowledged counts from outside the process under test.
/// </summary>
internal sealed class RabbitMqManagement : IDisposable
{
    private readonly HttpClient _http;

    internal RabbitMqManagement(string host, int port, string username, string password)
    {
        _http = new HttpClient { BaseAddress = new Uri($"http://{host}:{port}/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")));
    }

    /// <summary>True once the management plugin answers.</summary>
    internal async Task<bool> IsReadyAsync(CancellationToken ct)
    {
        try
        {
            using HttpResponseMessage response = await _http.GetAsync("api/overview", ct);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    /// <summary>Number of open connections whose client-provided name is <paramref name="clientProvidedName"/>.</summary>
    internal async Task<int> ConnectionCountAsync(string clientProvidedName, CancellationToken ct) =>
        (await ConnectionNamesAsync(clientProvidedName, ct)).Count;

    /// <summary>Number of open channels across the connections with the given client-provided name.</summary>
    internal async Task<int> ChannelCountAsync(string clientProvidedName, CancellationToken ct)
    {
        HashSet<string> connections = await ConnectionNamesAsync(clientProvidedName, ct);
        if (connections.Count == 0)
            return 0;

        using JsonDocument channels = await GetAsync("api/channels", ct);
        return channels.RootElement.EnumerateArray()
            .Count(channel => channel.TryGetProperty("connection_details", out JsonElement details)
                && details.TryGetProperty("name", out JsonElement name)
                && connections.Contains(name.GetString() ?? string.Empty));
    }

    /// <summary>Unacknowledged deliveries the broker currently holds for a queue on the default vhost.</summary>
    internal async Task<int> UnacknowledgedAsync(string queue, CancellationToken ct)
    {
        using JsonDocument document = await GetAsync($"api/queues/%2F/{Uri.EscapeDataString(queue)}", ct);
        return document.RootElement.TryGetProperty("messages_unacknowledged", out JsonElement value) ? value.GetInt32() : 0;
    }

    /// <summary>Messages ready plus unacknowledged for a queue on the default vhost, or -1 when it does not exist.</summary>
    internal async Task<int> MessageCountAsync(string queue, CancellationToken ct)
    {
        using HttpResponseMessage response = await _http.GetAsync($"api/queues/%2F/{Uri.EscapeDataString(queue)}", ct);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return -1;

        response.EnsureSuccessStatusCode();
        using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        return document.RootElement.TryGetProperty("messages", out JsonElement value) ? value.GetInt32() : 0;
    }

    private async Task<HashSet<string>> ConnectionNamesAsync(string clientProvidedName, CancellationToken ct)
    {
        using JsonDocument document = await GetAsync("api/connections", ct);
        return [.. document.RootElement.EnumerateArray()
            .Where(connection => connection.TryGetProperty("client_properties", out JsonElement properties)
                && properties.TryGetProperty("connection_name", out JsonElement name)
                && name.GetString() == clientProvidedName)
            .Select(connection => connection.GetProperty("name").GetString() ?? string.Empty)];
    }

    private async Task<JsonDocument> GetAsync(string path, CancellationToken ct)
    {
        using HttpResponseMessage response = await _http.GetAsync(path, ct);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }

    public void Dispose() => _http.Dispose();
}
