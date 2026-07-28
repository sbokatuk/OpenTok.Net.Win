using Microsoft.UI.Dispatching;
using OpenTok;

namespace OpenTok.Net.Windows;

/// <summary>
/// An <see cref="IDispatcher"/> that delivers every OpenTok event on a WinUI UI thread.
/// </summary>
/// <remarks>
/// <para>
/// Pass one to <c>Context</c> and the whole SDK becomes safe to use directly from XAML code-behind
/// or a view model:
/// </para>
/// <code>
/// var context = new Context(new OpenTokDispatcher(DispatcherQueue.GetForCurrentThread()));
/// var session = new Session.Builder(context, apiKey, sessionId).Build();
/// session.StreamReceived += (s, e) => Subscribers.Add(new SubscriberTile(e.Stream));
/// </code>
/// <para>
/// Without it, the default <c>Context</c> raises events on the SDK's own threads. Touching any
/// XAML object from there throws <c>RPC_E_WRONG_THREAD</c>, and the handlers where that happens —
/// <c>StreamReceived</c>, <c>Error</c> — are precisely the ones that need to change the UI. The
/// alternative is a <c>TryEnqueue</c> in every single handler, which is the thing everyone forgets
/// in exactly one of them.
/// </para>
/// <para>
/// Ordering is preserved: <see cref="DispatcherQueue"/> is FIFO, so events arrive in the order the
/// SDK raised them. That matters more than it sounds — a <c>StreamDropped</c> overtaking its
/// <c>StreamReceived</c> leaves an orphaned tile on screen for the rest of the call. It is the
/// reason to prefer this over the SDK's own <c>ThreadPoolDispatcher</c>, whose documentation warns
/// that delivery order is not guaranteed.
/// </para>
/// </remarks>
/// <param name="dispatcherQueue">The UI thread's queue to deliver events on.</param>
public sealed class OpenTokDispatcher(DispatcherQueue dispatcherQueue) : IDispatcher
{
    private readonly DispatcherQueue _dispatcherQueue = dispatcherQueue
        ?? throw new ArgumentNullException(nameof(dispatcherQueue));

    /// <summary>Schedules a parameterless event onto the UI thread.</summary>
    public void DispatchEvent(object sender, EventHandler handler)
    {
        if (handler is null)
        {
            return;
        }

        // Dropped rather than thrown when the queue is shutting down. This runs on an SDK thread
        // during teardown, where an exception has nowhere to go but the SDK's own callback and
        // takes the process with it.
        _dispatcherQueue.TryEnqueue(() => handler(sender, EventArgs.Empty));
    }

    /// <summary>Schedules an event carrying arguments onto the UI thread.</summary>
    public void DispatchEvent<T>(object sender, EventHandler<T> handler, T args)
    {
        if (handler is null)
        {
            return;
        }

        _dispatcherQueue.TryEnqueue(() => handler(sender, args));
    }
}
