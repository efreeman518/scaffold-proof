using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using TaskFlow.Api.Middleware;

namespace Test.Endpoints;

/// <summary>Guards exception correlation identifiers emitted in ProblemDetails.</summary>
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

    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = Environments.Production;
        public string ApplicationName { get; set; } = nameof(Test.Endpoints);
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
