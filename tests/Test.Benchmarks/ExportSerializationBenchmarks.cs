using BenchmarkDotNet.Attributes;
using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;
using TaskFlow.Application.Models.Reads;
using TaskFlow.Application.Models.Serialization;
using TaskFlow.Domain.Shared.Enums;

namespace Test.Benchmarks;

/// <summary>
/// The NDJSON export hot path (D-047/D-048): what the endpoint used to do - one
/// <c>JsonSerializer.SerializeAsync</c> per row against the response stream, with reflection metadata -
/// against what it does now: one <see cref="Utf8JsonWriter"/> reused with <c>Reset</c> over the response
/// <c>IBufferWriter</c>, using the source-generated <c>TaskItemExportDto</c> metadata.
///
/// The two write to different destinations (Stream vs IBufferWriter) because that IS the change: an
/// <c>IBufferWriter</c> is what lets the writer be synchronous and reused, and the response PipeWriter is
/// one. 1,000 rows is a small export; the endpoint streams a whole tenant.
///
/// Benchmark tier (BenchmarkDotNet only) - runs from <c>Program.Main</c> via <c>BenchmarkSwitcher</c>;
/// not part of the MSTest run. Run from repo root with:
/// <c>dotnet run -c Release --project tests\Test.Benchmarks\Test.Benchmarks.csproj -- --filter *ExportSerializationBenchmarks*</c>.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class ExportSerializationBenchmarks
{
    private const int RowCount = 1_000;
    private static readonly byte[] NewLine = [(byte)'\n'];

    private TaskItemExportDto[] _rows = null!;
    private JsonSerializerOptions _reflectionOptions = null!;
    private MemoryStream _stream = null!;
    private ArrayBufferWriter<byte> _bufferWriter = null!;

    /// <summary>Builds the export rows and the two destinations once, outside measurement.</summary>
    [GlobalSetup]
    public void Setup()
    {
        var tenantId = Guid.CreateVersion7();
        _rows = [.. Enumerable.Range(0, RowCount).Select(i => new TaskItemExportDto
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Title = $"Benchmark export row {i}",
            Description = i % 3 == 0 ? null : $"Description for row {i} with enough text to be realistic.",
            Priority = (Priority)(i % 3),
            Status = (TaskItemStatus)(i % 4),
            EstimatedEffort = i % 5 == 0 ? null : 3.5m,
            ActualEffort = i % 7 == 0 ? null : 4.25m,
            StartDate = DateTimeOffset.UtcNow.AddDays(-i % 30),
            DueDate = DateTimeOffset.UtcNow.AddDays(i % 30),
            CompletedDate = i % 4 == 0 ? DateTimeOffset.UtcNow : null,
            CategoryId = Guid.CreateVersion7(),
            ParentTaskItemId = i % 10 == 0 ? Guid.CreateVersion7() : null,
            Version = i,
            CreatedAtUtc = DateTimeOffset.UtcNow.AddDays(-60),
            ModifiedAtUtc = DateTimeOffset.UtcNow
        })];

        // Web defaults plus the string enum converter: the Api's HTTP JSON options before the generated
        // resolver was inserted, i.e. the reflection path this replaced.
        _reflectionOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        _reflectionOptions.Converters.Add(new JsonStringEnumConverter());

        _stream = new MemoryStream(capacity: 1 << 20);
        _bufferWriter = new ArrayBufferWriter<byte>(initialCapacity: 1 << 20);
    }

    /// <summary>Releases the destinations.</summary>
    [GlobalCleanup]
    public void Cleanup() => _stream.Dispose();

    /// <summary>The previous implementation: SerializeAsync per row against the response stream.</summary>
    [Benchmark(Baseline = true, Description = "SerializeAsync per row (reflection)")]
    public async Task<long> PerRowSerializeAsync()
    {
        _stream.SetLength(0);
        foreach (var row in _rows)
        {
            await JsonSerializer.SerializeAsync(_stream, row, _reflectionOptions);
            await _stream.WriteAsync(NewLine);
        }

        return _stream.Length;
    }

    /// <summary>The current implementation: one reused Utf8JsonWriter plus generated metadata.</summary>
    [Benchmark(Description = "Reused Utf8JsonWriter (source-generated)")]
    public long ReusedWriter()
    {
        _bufferWriter.Clear();
        using var writer = new Utf8JsonWriter(_bufferWriter, new JsonWriterOptions { SkipValidation = true });
        foreach (var row in _rows)
        {
            writer.Reset(_bufferWriter);
            JsonSerializer.Serialize(writer, row, TaskFlowJsonContext.Default.TaskItemExportDto);
            _bufferWriter.Write(NewLine);
        }

        writer.Flush();
        return _bufferWriter.WrittenCount;
    }
}
