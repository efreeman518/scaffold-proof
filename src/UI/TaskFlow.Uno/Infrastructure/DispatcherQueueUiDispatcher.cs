using EF.UI.Client;
using Microsoft.UI.Dispatching;

namespace TaskFlow.Uno.Infrastructure;

/// <summary>Adapts dispatcher queue UI infrastructure behavior for the Uno client.</summary>
internal sealed class DispatcherQueueUiDispatcher(DispatcherQueue queue) : IUiDispatcher
{
    public bool HasThreadAccess => queue.HasThreadAccess;
    /// <summary>
    /// Queues <paramref name="action"/> on the UI thread. A queue that has shut down rejects the work; that is
    /// raised rather than dropped, or busy and notification updates would silently stop reaching the UI.
    /// </summary>
    public void Post(Action action)
    {
        if (!queue.TryEnqueue(() => action()))
            throw new InvalidOperationException("UI dispatcher queue rejected work (shut down).");
    }
}
