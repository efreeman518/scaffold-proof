using System.Diagnostics;
using System.Text.Json;
using EF.AspNetCore.ExceptionHandling;
using EF.Common.Contracts;
using EF.Common.Exceptions;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using TaskFlow.Api;
using TaskFlow.Infrastructure.Data.Provider;

namespace Test.Endpoints;

/// <summary>
/// Guards the exception-to-status contract and the correlation identifiers emitted in ProblemDetails, as the Api
/// registers them: the EF.AspNetCore problem-details handler plus TaskFlow's exception classifier mappings.
/// </summary>
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
        using var provider = BuildProvider();
        var context = NewContext(provider);
        context.TraceIdentifier = "request-123";

        var handled = await Handler(provider).TryHandleAsync(context, new Exception("failure"), CancellationToken.None);

        var root = await ReadBodyAsync(context);
        Assert.IsTrue(handled);
        Assert.AreEqual(activity.TraceId.ToHexString(), root.GetProperty("traceId").GetString());
        Assert.AreEqual(activity.SpanId.ToHexString(), root.GetProperty("spanId").GetString());
        Assert.AreEqual("request-123", root.GetProperty("requestId").GetString());
        Assert.AreEqual("GET /failure", root.GetProperty("instance").GetString());
        Assert.IsFalse(root.TryGetProperty("activityId", out _));
    }

    /// <summary>A cancellation the caller caused is 499 with no body; the client is gone.</summary>
    [TestMethod]
    public async Task TryHandleAsync_ClientAbort_Returns499WithoutBody()
    {
        using var aborted = new CancellationTokenSource();
        await aborted.CancelAsync();
        using var provider = BuildProvider();
        var context = NewContext(provider);
        context.RequestAborted = aborted.Token;

        await Handler(provider).TryHandleAsync(context, new OperationCanceledException(aborted.Token), CancellationToken.None);

        Assert.AreEqual(499, context.Response.StatusCode);
        Assert.AreEqual(0, context.Response.Body.Length);
    }

    /// <summary>
    /// A cancellation the caller did not cause is a server-side timeout (EF.AspNetCore 2.0: 504, was 500), and an
    /// HttpClient timeout (TaskCanceledException over TimeoutException) is the same 504.
    /// </summary>
    [TestMethod]
    public async Task TryHandleAsync_NonClientCancellationOrHttpClientTimeout_Returns504()
    {
        Exception[] timeouts =
        [
            new OperationCanceledException(),
            new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.",
                new TimeoutException("The operation was canceled."))
        ];

        using var provider = BuildProvider();
        foreach (var timeout in timeouts)
        {
            var context = NewContext(provider);
            await Handler(provider).TryHandleAsync(context, timeout, CancellationToken.None);
            Assert.AreEqual(StatusCodes.Status504GatewayTimeout, context.Response.StatusCode, timeout.GetType().Name);
        }
    }

    /// <summary>Outside Development a 5xx carries no exception text; Staging included.</summary>
    [TestMethod]
    [DataRow("Production")]
    [DataRow("Staging")]
    public async Task TryHandleAsync_ServerFaultOutsideDevelopment_HasNoDetail(string environmentName)
    {
        using var provider = BuildProvider(environmentName);
        var context = NewContext(provider);

        await Handler(provider).TryHandleAsync(
            context, new Exception("Login failed for user 'sa' on server sql-internal"), CancellationToken.None);

        var root = await ReadBodyAsync(context);
        Assert.AreEqual(StatusCodes.Status500InternalServerError, context.Response.StatusCode);
        Assert.IsFalse(root.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String,
            "A 5xx must not carry exception text outside Development.");
    }

    /// <summary>An ArgumentException the app throws for caller input is a 400 with its message.</summary>
    [TestMethod]
    public async Task TryHandleAsync_AppThrownArgumentException_Returns400()
    {
        using var provider = BuildProvider();
        var context = NewContext(provider);
        var exception = Capture(() => TaskFlowDbProviderSelector.MigrationsAssembly((TaskFlowDbProvider)99));

        await Handler(provider).TryHandleAsync(context, exception, CancellationToken.None);

        var root = await ReadBodyAsync(context);
        Assert.AreEqual(StatusCodes.Status400BadRequest, context.Response.StatusCode);
        Assert.AreEqual(exception.Message, root.GetProperty("detail").GetString());
    }

    /// <summary>
    /// The TaskFlow mappings on the shared classifier: a stale If-Match and a lost update are 412, a conflicting
    /// idempotent create is 409, and a missing key is 404 (EF.AspNetCore 2.0 default; was 500). Any
    /// ArgumentException is 400 - the classifier cannot tell an app-thrown one from a framework one, so a BCL
    /// ArgumentException is now 400 too (was 500).
    /// </summary>
    [TestMethod]
    public async Task TryHandleAsync_MappedExceptions_ReturnTheirStatus()
    {
        (Exception Exception, int Status)[] cases =
        [
            (new PreconditionFailedException("TaskItem", Guid.CreateVersion7().ToString(), 1, 2), StatusCodes.Status412PreconditionFailed),
            (new DbUpdateConcurrencyException(), StatusCodes.Status412PreconditionFailed),
            (new ConflictException("TaskItem", Guid.CreateVersion7().ToString()), StatusCodes.Status409Conflict),
            (Capture(() => _ = new Dictionary<int, int>()[1]), StatusCodes.Status404NotFound),
            (Capture(() => new Dictionary<int, int> { [1] = 1 }.Add(1, 1)), StatusCodes.Status400BadRequest)
        ];

        using var provider = BuildProvider();
        foreach (var (exception, status) in cases)
        {
            var context = NewContext(provider);
            await Handler(provider).TryHandleAsync(context, exception, CancellationToken.None);
            Assert.AreEqual(status, context.Response.StatusCode, exception.GetType().Name);
        }
    }

    /// <summary>FormatException and InvalidOperationException are server bugs, not caller mistakes: they stay 500.</summary>
    [TestMethod]
    public async Task TryHandleAsync_FrameworkFaults_Return500()
    {
        Exception[] faults =
        [
            Capture(() => int.Parse("x", System.Globalization.CultureInfo.InvariantCulture)),
            Capture(() => Array.Empty<int>().First())
        ];

        using var provider = BuildProvider();
        foreach (var fault in faults)
        {
            var context = NewContext(provider);
            await Handler(provider).TryHandleAsync(context, fault, CancellationToken.None);
            Assert.AreEqual(StatusCodes.Status500InternalServerError, context.Response.StatusCode, fault.GetType().Name);
        }
    }

    /// <summary>The Api's exception-handling registration, without the rest of the host.</summary>
    private static ServiceProvider BuildProvider(string environmentName = "Production")
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment { EnvironmentName = environmentName });
        services.AddEfProblemDetails();
        services.AddExceptionClassifier(RegisterApiServices.MapExceptions);
        return services.BuildServiceProvider();
    }

    private static IExceptionHandler Handler(IServiceProvider provider) =>
        provider.GetServices<IExceptionHandler>().OfType<ProblemDetailsExceptionHandler>().Single();

    private static DefaultHttpContext NewContext(IServiceProvider provider)
    {
        var context = new DefaultHttpContext { RequestServices = provider };
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
