using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using TaskFlow.Api.Middleware;
using TaskFlow.Infrastructure.Data.Provider;

namespace Test.Endpoints;

/// <summary>Guards the exception-to-status contract and the correlation identifiers emitted in ProblemDetails.</summary>
[TestClass]
[TestCategory("Endpoint")]
public sealed class GlobalExceptionHandlerTests
{
    [TestMethod]
    public async Task TryHandleAsync_SeparatesW3CTraceAndSpanFromRequestIdentifier()
    {
        using var activity = new Activity("exception-test")
            .SetIdFormat(ActivityIdFormat.W3C)
            .Start();
        var context = new DefaultHttpContext
        {
            TraceIdentifier = "request-123"
        };
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/failure";
        context.Response.Body = new MemoryStream();
        var handler = new DefaultExceptionHandler(
            NullLogger<DefaultExceptionHandler>.Instance,
            new TestHostEnvironment());

        var handled = await handler.TryHandleAsync(
            context,
            new Exception("failure"),
            CancellationToken.None);

        context.Response.Body.Position = 0;
        using var response = await JsonDocument.ParseAsync(
            context.Response.Body,
            cancellationToken: CancellationToken.None);
        var root = response.RootElement;

        Assert.IsTrue(handled);
        Assert.AreEqual(activity.TraceId.ToHexString(), root.GetProperty("traceId").GetString());
        Assert.AreEqual(activity.SpanId.ToHexString(), root.GetProperty("spanId").GetString());
        Assert.AreEqual("request-123", root.GetProperty("requestId").GetString());
        Assert.IsFalse(root.TryGetProperty("activityId", out _));
    }

    /// <summary>A cancellation the caller caused is 499 with no body; the client is gone.</summary>
    [TestMethod]
    public async Task TryHandleAsync_ClientAbort_Returns499WithoutBody()
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        var context = NewContext();
        context.RequestAborted = aborted.Token;

        await NewHandler().TryHandleAsync(context, new OperationCanceledException(aborted.Token), CancellationToken.None);

        Assert.AreEqual(499, context.Response.StatusCode);
        Assert.AreEqual(0, context.Response.Body.Length);
    }

    /// <summary>A cancellation the caller did not cause is a server fault, not a client disconnect.</summary>
    [TestMethod]
    public async Task TryHandleAsync_NonClientCancellation_Returns500()
    {
        var context = NewContext();

        await NewHandler().TryHandleAsync(context, new OperationCanceledException(), CancellationToken.None);

        Assert.AreEqual(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
    }

    /// <summary>An HttpClient timeout (TaskCanceledException over TimeoutException) is a 504.</summary>
    [TestMethod]
    public async Task TryHandleAsync_HttpClientTimeout_Returns504()
    {
        var context = NewContext();
        var timeout = new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.",
            new TimeoutException("The operation was canceled."));

        await NewHandler().TryHandleAsync(context, timeout, CancellationToken.None);

        Assert.AreEqual(StatusCodes.Status504GatewayTimeout, context.Response.StatusCode);
    }

    /// <summary>Outside Development a 5xx carries no exception text; Staging included.</summary>
    [TestMethod]
    [DataRow("Production")]
    [DataRow("Staging")]
    public async Task TryHandleAsync_ServerFaultOutsideDevelopment_HasNoDetail(string environmentName)
    {
        var context = NewContext();

        await NewHandler(environmentName).TryHandleAsync(
            context, new Exception("Login failed for user 'sa' on server sql-internal"), CancellationToken.None);

        var root = await ReadBodyAsync(context);
        Assert.AreEqual(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.IsFalse(root.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String,
            "A 5xx must not carry exception text outside Development.");
    }

    /// <summary>An ArgumentException the app throws for caller input stays a 400 with its message.</summary>
    [TestMethod]
    public async Task TryHandleAsync_AppThrownArgumentException_Returns400()
    {
        var context = NewContext();
        var exception = Capture(() => TaskFlowDbProviderSelector.MigrationsAssembly((TaskFlowDbProvider)99));

        await NewHandler().TryHandleAsync(context, exception, CancellationToken.None);

        var root = await ReadBodyAsync(context);
        Assert.AreEqual(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.AreEqual(exception.Message, root.GetProperty("detail").GetString());
    }

    /// <summary>
    /// Framework faults are server bugs, not caller mistakes: an ArgumentException from the BCL, a
    /// KeyNotFoundException, FormatException or InvalidOperationException all stay 500.
    /// </summary>
    [TestMethod]
    public async Task TryHandleAsync_FrameworkFaults_Return500()
    {
        Exception[] faults =
        [
            Capture(() => new Dictionary<int, int> { [1] = 1 }.Add(1, 1)),
            Capture(() => _ = new Dictionary<int, int>()[1]),
            Capture(() => int.Parse("x", System.Globalization.CultureInfo.InvariantCulture)),
            Capture(() => Array.Empty<int>().First())
        ];

        foreach (var fault in faults)
        {
            var context = NewContext();
            await NewHandler().TryHandleAsync(context, fault, CancellationToken.None);
            Assert.AreEqual(StatusCodes.Status500InternalServerError, context.Response.StatusCode, fault.GetType().Name);
        }
    }

    private static DefaultExceptionHandler NewHandler(string environmentName = "Production") =>
        new(NullLogger<DefaultExceptionHandler>.Instance, new TestHostEnvironment { EnvironmentName = environmentName });

    private static DefaultHttpContext NewContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/failure";
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task<JsonElement> ReadBodyAsync(DefaultHttpContext context)
    {
        context.Response.Body.Position = 0;
        using var document = await JsonDocument.ParseAsync(context.Response.Body);
        return document.RootElement.Clone();
    }

    /// <summary>Returns a genuinely thrown exception, so TargetSite and the stack trace are populated.</summary>
    private static Exception Capture(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            return ex;
        }

        throw new AssertFailedException("The action did not throw.");
    }

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = nameof(Test.Endpoints);
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
