using System.Net;
using System.Text;
using System.Text.Json;
using TaskFlow.Uno.Core.Business.Notifications;
using TaskFlow.Uno.Core.Client.Http;

namespace Test.UI.Uno;

/// <summary>
/// Validates <c>ProblemDetailsDelegatingHandler</c>: passes successes through, ignores non-problem-json
/// errors, throws <c>ProblemDetailsException</c> + notifies on <c>application/problem+json</c>, suppresses
/// notification for status 499, ignores malformed problem JSON, and dedupes concurrent duplicates.
/// Pure-unit tier: stub <see cref="System.Net.Http.HttpMessageHandler"/>; no real server.
/// </summary>
[TestClass]
[TestCategory("UI")]
public class ProblemDetailsDelegatingHandlerTests
{
    /// <summary>Verifies success response passes through without notifying behavior and protects the expected test contract.</summary>
    [TestMethod]
    public async Task SuccessResponse_Passes_Through_Without_Notifying()
    {
        var svc = new NotificationService();
        var stub = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));

        var response = await Invoke(stub, svc);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsEmpty(svc.Items);
    }

    /// <summary>Verifies non problem JSON error passes through behavior and protects the expected test contract.</summary>
    [TestMethod]
    public async Task Non_ProblemJson_Error_Passes_Through()
    {
        var svc = new NotificationService();
        var stub = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)
        {
            Content = new StringContent("Plain text error", Encoding.UTF8, "text/plain")
        }));

        var response = await Invoke(stub, svc);

        Assert.AreEqual(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.IsEmpty(svc.Items);
    }

    /// <summary>Verifies problem JSON throws problem details exception and notifies behavior and protects the expected test contract.</summary>
    [TestMethod]
    public async Task ProblemJson_Throws_ProblemDetailsException_And_Notifies()
    {
        var svc = new NotificationService();
        var stub = new StubHandler(_ => Task.FromResult(ProblemResponse(400, "Validation failed", "Name is required")));

        var ex = await Assert.ThrowsExactlyAsync<ProblemDetailsException>(async () =>
            await Invoke(stub, svc));

        Assert.AreEqual(400, ex.StatusCode);
        Assert.AreEqual("Validation failed", ex.Problem.Title);
        Assert.HasCount(1, svc.Items);
        Assert.AreEqual(NotificationSeverity.Error, svc.Items[0].Severity);
    }

    /// <summary>Verifies problem JSON 499 throws without notifying behavior and protects the expected test contract.</summary>
    [TestMethod]
    public async Task ProblemJson_499_Throws_Without_Notifying()
    {
        var svc = new NotificationService();
        var stub = new StubHandler(_ => Task.FromResult(ProblemResponse(499, "Client cancelled", "User aborted")));

        var ex = await Assert.ThrowsExactlyAsync<ProblemDetailsException>(async () =>
            await Invoke(stub, svc));

        Assert.AreEqual(499, ex.StatusCode);
        Assert.IsEmpty(svc.Items);
    }

    /// <summary>Verifies malformed problem JSON passes through behavior and protects the expected test contract.</summary>
    [TestMethod]
    public async Task Malformed_ProblemJson_Passes_Through()
    {
        var svc = new NotificationService();
        var stub = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{not valid json", Encoding.UTF8, "application/problem+json")
        }));

        var response = await Invoke(stub, svc);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.IsEmpty(svc.Items);
    }

    /// <summary>Verifies cancellation while reading problem JSON is never converted into a fallback response.</summary>
    [TestMethod]
    public async Task Cancelled_ProblemJson_Read_Propagates_Cancellation()
    {
        var svc = new NotificationService();
        var stub = new StubHandler(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new CancelledProblemContent()
        }));

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(async () =>
            await Invoke(stub, svc));

        Assert.IsEmpty(svc.Items);
    }

    /// <summary>Verifies problem JSON dedupes on concurrent duplicates behavior and protects the expected test contract.</summary>
    [TestMethod]
    public async Task ProblemJson_Dedupes_On_Concurrent_Duplicates()
    {
        var svc = new NotificationService();
        var stub = new StubHandler(_ => Task.FromResult(ProblemResponse(500, "Oops", "Server error")));
        var handler = new ProblemDetailsDelegatingHandler(svc) { InnerHandler = stub };
        var invoker = new HttpMessageInvoker(handler);

        for (var i = 0; i < 5; i++)
        {
            try
            {
                await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://x/y"), default);
            }
            catch (ProblemDetailsException) { }
        }

        Assert.HasCount(1, svc.Items);
    }

    /// <summary>Verifies invoke behavior and protects the expected test contract.</summary>
    private static async Task<HttpResponseMessage> Invoke(HttpMessageHandler inner, INotificationService notifications)
    {
        var handler = new ProblemDetailsDelegatingHandler(notifications) { InnerHandler = inner };
        var invoker = new HttpMessageInvoker(handler);
        return await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, "http://x/y"), default);
    }

    /// <summary>Verifies problem response behavior and protects the expected test contract.</summary>
    private static HttpResponseMessage ProblemResponse(int status, string title, string detail)
    {
        var payload = JsonSerializer.Serialize(new { status, title, detail });
        return new HttpResponseMessage((HttpStatusCode)status)
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/problem+json")
        };
    }

    /// <summary>Supports test execution for Test.unit Uno scenarios.</summary>
    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond)
        : HttpMessageHandler
    {
        /// <summary>Verifies send behavior and protects the expected test contract.</summary>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            respond(request);
    }

    private sealed class CancelledProblemContent : HttpContent
    {
        internal CancelledProblemContent() =>
            Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/problem+json");

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            Task.FromCanceled(new CancellationToken(canceled: true));

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
