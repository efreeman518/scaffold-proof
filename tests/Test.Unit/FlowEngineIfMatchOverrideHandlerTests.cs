using Microsoft.Extensions.Logging.Abstractions;
using System.Net;
using System.Net.Http.Headers;
using TaskFlow.Bootstrapper;

namespace Test.Unit;

/// <summary>
/// Validates <c>FlowEngineIfMatchOverrideHandler</c> (D-032 trusted-automation override for the
/// FlowEngine "taskflow-api" self-call client): adds the wildcard <c>If-Match</c> to mutating requests
/// that carry none, leaves an explicit <c>If-Match</c> untouched, and never touches GET/POST.
/// Pure-unit tier: a stub inner handler short-circuits the pipeline - no real socket, no real server.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public class FlowEngineIfMatchOverrideHandlerTests
{
    /// <summary>Verifies the wildcard override is added to a PATCH request with no If-Match header.</summary>
    [TestMethod]
    public async Task SendAsync_Adds_Wildcard_When_Patch_Has_No_IfMatch()
    {
        HttpRequestMessage? seen = null;
        var stub = new StubHandler(req => { seen = req; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); });
        var handler = new FlowEngineIfMatchOverrideHandler(NullLogger<FlowEngineIfMatchOverrideHandler>.Instance) { InnerHandler = stub };
        var invoker = new HttpMessageInvoker(handler);

        await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Patch, "http://x/api/v1/task-items/1"), default);

        Assert.IsNotNull(seen);
        Assert.HasCount(1, seen!.Headers.IfMatch);
        Assert.AreEqual(EntityTagHeaderValue.Any, seen.Headers.IfMatch.Single());
    }

    /// <summary>Verifies an explicit If-Match on a mutating request is left untouched.</summary>
    [TestMethod]
    public async Task SendAsync_Leaves_Explicit_IfMatch_Untouched()
    {
        HttpRequestMessage? seen = null;
        var stub = new StubHandler(req => { seen = req; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); });
        var handler = new FlowEngineIfMatchOverrideHandler(NullLogger<FlowEngineIfMatchOverrideHandler>.Instance) { InnerHandler = stub };
        var invoker = new HttpMessageInvoker(handler);

        var request = new HttpRequestMessage(HttpMethod.Put, "http://x/api/v1/task-items/1");
        request.Headers.IfMatch.Add(new EntityTagHeaderValue("\"7\""));

        await invoker.SendAsync(request, default);

        Assert.IsNotNull(seen);
        Assert.HasCount(1, seen!.Headers.IfMatch);
        Assert.AreEqual("\"7\"", seen.Headers.IfMatch.Single().Tag.ToString());
    }

    /// <summary>Verifies GET requests are never given an If-Match header.</summary>
    [TestMethod]
    public async Task SendAsync_Never_Adds_IfMatch_To_Get()
    {
        HttpRequestMessage? seen = null;
        var stub = new StubHandler(req => { seen = req; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); });
        var handler = new FlowEngineIfMatchOverrideHandler(NullLogger<FlowEngineIfMatchOverrideHandler>.Instance) { InnerHandler = stub };
        var invoker = new HttpMessageInvoker(handler);

        await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://x/api/v1/task-items/1"), default);

        Assert.IsNotNull(seen);
        Assert.IsEmpty(seen!.Headers.IfMatch);
    }

    /// <summary>Verifies POST requests are never given an If-Match header (comment/create nodes are not mutating-by-precondition).</summary>
    [TestMethod]
    public async Task SendAsync_Never_Adds_IfMatch_To_Post()
    {
        HttpRequestMessage? seen = null;
        var stub = new StubHandler(req => { seen = req; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)); });
        var handler = new FlowEngineIfMatchOverrideHandler(NullLogger<FlowEngineIfMatchOverrideHandler>.Instance) { InnerHandler = stub };
        var invoker = new HttpMessageInvoker(handler);

        await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Post, "http://x/api/v1/task-items/1/comments"), default);

        Assert.IsNotNull(seen);
        Assert.IsEmpty(seen!.Headers.IfMatch);
    }

    /// <summary>Supports test execution by short-circuiting the handler pipeline.</summary>
    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        /// <summary>Returns the stubbed response without hitting the network.</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            respond(request);
    }
}
