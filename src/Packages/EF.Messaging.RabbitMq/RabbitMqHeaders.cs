using System.Globalization;
using System.Text;

namespace EF.Messaging.RabbitMq;

/// <summary>
/// Header conversion between the application's <c>object?</c> values and the AMQP field-table shapes the client
/// produces, plus <c>x-death</c> parsing.
/// </summary>
internal static class RabbitMqHeaders
{
    /// <summary>The <c>x-death</c> header the broker adds each time a message is dead-lettered.</summary>
    private const string DeathHeader = "x-death";

    /// <summary>
    /// Rejects header values the AMQP field-table encoder cannot carry, before anything is published.
    /// </summary>
    /// <exception cref="ArgumentException">A value has an unsupported type.</exception>
    internal static void Validate(IReadOnlyDictionary<string, object?>? headers)
    {
        if (headers is null)
            return;

        foreach ((string key, object? value) in headers)
        {
            if (value is null or string or int or long or bool or byte[])
                continue;

            throw new ArgumentException(
                $"Header '{key}' has unsupported type {value.GetType().FullName}. Supported types are string, int, long, bool, byte[] and null.",
                nameof(headers));
        }
    }

    /// <summary>Copies application headers into the mutable dictionary the client publishes.</summary>
    internal static IDictionary<string, object?> ToBrokerHeaders(IReadOnlyDictionary<string, object?> headers) =>
        new Dictionary<string, object?>(headers, StringComparer.Ordinal);

    /// <summary>Copies broker arguments into the mutable dictionary the client declares with.</summary>
    internal static Dictionary<string, object?>? ToArguments(IReadOnlyDictionary<string, object?>? arguments) =>
        arguments is null ? null : new Dictionary<string, object?>(arguments, StringComparer.Ordinal);

    /// <summary>Exposes the delivered headers as read-only, substituting an empty map when there are none.</summary>
    internal static IReadOnlyDictionary<string, object?> ToDeliveryHeaders(IDictionary<string, object?>? headers) =>
        headers is null
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : new Dictionary<string, object?>(headers, StringComparer.Ordinal);

    /// <summary>
    /// Sums the <c>count</c> fields of the <c>x-death</c> entries whose <c>queue</c> is <paramref name="queue"/>.
    /// The broker encodes the header as a list of field tables and queue names as UTF-8 byte arrays.
    /// </summary>
    internal static int DeathCount(IReadOnlyDictionary<string, object?> headers, string queue)
    {
        if (!headers.TryGetValue(DeathHeader, out object? raw) || raw is not System.Collections.IEnumerable entries || raw is string)
            return 0;

        long total = 0;
        foreach (object? entry in entries)
        {
            if (entry is not IDictionary<string, object?> table)
                continue;

            if (!table.TryGetValue("queue", out object? entryQueue) || !string.Equals(AsString(entryQueue), queue, StringComparison.Ordinal))
                continue;

            if (table.TryGetValue("count", out object? count) && count is not null)
                total += Convert.ToInt64(count, CultureInfo.InvariantCulture);
        }

        return total > int.MaxValue ? int.MaxValue : (int)total;
    }

    /// <summary>Decodes an AMQP field-table value that carries text; the client delivers strings as UTF-8 bytes.</summary>
    internal static string? AsString(object? value) => value switch
    {
        null => null,
        string text => text,
        byte[] utf8 => Encoding.UTF8.GetString(utf8),
        ReadOnlyMemory<byte> utf8 => Encoding.UTF8.GetString(utf8.Span),
        _ => value.ToString()
    };
}
