using EF.Testing.Http;
using System.Net;
using TaskFlow.Uno.Core.Business.Notifications;
using TaskFlow.Uno.Core.Client.Http;

namespace Test.UI.Uno;

/// <summary>
/// Validates <c>BusyDelegatingHandler</c> increments <c>BusyTracker.Pending</c> while a request is
/// in-flight, decrements after success, and decrements after the inner handler throws.
/// Pure-unit tier: a stub <see cref="System.Net.Http.HttpMessageHandler"/> short-circuits the pipeline  - 
/// no real socket, no real server.
/// </summary>
[TestClass]
[TestCategory("UI")]
public class BusyDelegatingHandlerTests
{
    /// <summary>Verifies send increments during request decrements after behavior and protects the expected test contract.</summary>
    [TestMethod]
    public async Task SendAsync_Increments_During_Request_Decrements_After()
    {
        var tracker = new BusyTracker();
        var gate = new TaskCompletionSource<int>();

        var stub = new StubHttpMessageHandler(async (_, _, _) =>
        {
            // Release the caller once the handler has entered - proves Pending
            // reaches 1 during the request.
            gate.SetResult(tracker.Pending);
            await Task.Yield();
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var handler = new BusyDelegatingHandler(tracker) { InnerHandler = stub };
        var invoker = new HttpMessageInvoker(handler);

        var response = await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://x/y"), default);

        Assert.AreEqual(1, await gate.Task);
        Assert.AreEqual(0, tracker.Pending);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>Verifies send decrements on inner exception behavior and protects the expected test contract.</summary>
    [TestMethod]
    public async Task SendAsync_Decrements_On_Inner_Exception()
    {
        var tracker = new BusyTracker();
        var stub = new StubHttpMessageHandler((_, _, _) => throw new HttpRequestException("network fail"));
        var handler = new BusyDelegatingHandler(tracker) { InnerHandler = stub };
        var invoker = new HttpMessageInvoker(handler);

        await Assert.ThrowsExactlyAsync<HttpRequestException>(async () =>
            await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://x/y"), default));

        Assert.AreEqual(0, tracker.Pending);
    }
}
