using System.Collections.Concurrent;

namespace IronNestAgentBridge;

/// <summary>
/// The one legal channel from background threads (HTTP listeners, the agent loop) onto Unity's
/// main thread. Every Il2Cpp object, Unity API and reflected FCS instance may only be touched
/// from inside <see cref="Pump"/>, i.e. on the OnUpdate stack.
///
/// Never call <see cref="Run{T}"/> from the main thread itself and wait on it: the work item
/// cannot run while Pump is blocked, so it self-deadlocks until the timeout expires.
/// </summary>
public static class MainThread
{
    /// <summary>
    /// A queued closure plus its abandonment flag. Abandoned items stay in the queue but are
    /// skipped on dequeue, so a timed-out or reset-discarded call never lands late in a world
    /// the caller has already given up on (a stale QueueFireMission really would fire a gun).
    /// </summary>
    private sealed class WorkItem
    {
        private volatile bool _abandoned;

        public WorkItem(Action body, Action? discard)
        {
            Body = body;
            Discard = discard;
        }

        private Action Body { get; }

        /// <summary>Trips the caller-side failure path (cancels its timeout source).</summary>
        private Action? Discard { get; }

        public bool Abandoned => _abandoned;

        public void MarkAbandoned() => _abandoned = true;

        public void Invoke()
        {
            if (_abandoned) return;
            Body();
        }

        /// <summary>Drop this item and let the waiting caller fail immediately.</summary>
        public void Cancel()
        {
            _abandoned = true;
            try { Discard?.Invoke(); }
            catch { /* the caller may already be gone */ }
        }
    }

    private static readonly ConcurrentQueue<WorkItem> Queue = new();

    /// <summary>
    /// Marshals <paramref name="func"/> onto the main thread and returns its result.
    /// Exceptions thrown by <paramref name="func"/> are handed back through the task, never
    /// raised inside <see cref="Pump"/>. On timeout the caller gets a
    /// <see cref="TimeoutException"/> and the work item is abandoned.
    /// </summary>
    public static Task<T> Run<T>(Func<T> func, int timeoutMs = 10_000)
    {
        // Continuations must not run on the main thread — a caller's continuation could
        // otherwise re-enter game code from inside Pump.
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cts = new CancellationTokenSource(timeoutMs);

        var item = new WorkItem(
            body: () =>
            {
                // First writer wins: a result arriving after the timeout is dropped silently.
                try { tcs.TrySetResult(func()); }
                catch (Exception ex) { tcs.TrySetException(ex); }
            },
            discard: cts.Cancel);

        var registration = cts.Token.Register(() =>
        {
            item.MarkAbandoned();
            tcs.TrySetException(new TimeoutException(
                $"main-thread call not serviced within {timeoutMs}ms (game unfocused or scene loading?)"));
        });

        // Release the timer as soon as the call settles, whichever way it settled.
        tcs.Task.ContinueWith(
            static (_, state) =>
            {
                var (reg, source) = ((CancellationTokenRegistration, CancellationTokenSource))state!;
                try { reg.Dispose(); } catch { }
                try { source.Dispose(); } catch { }
            },
            (registration, cts),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        Queue.Enqueue(item);
        return tcs.Task;
    }

    /// <summary>Void overload of <see cref="Run{T}"/> with identical semantics.</summary>
    public static Task Run(Action action, int timeoutMs = 10_000) => Run<object?>(() =>
    {
        action();
        return null;
    }, timeoutMs);

    /// <summary>
    /// Fire-and-forget marshalling for decorative work (map drawing and the like). Exceptions
    /// are swallowed whole — cosmetic failures must never disturb the fire mission — and the
    /// caller never blocks; while the game is paused the item simply waits for the next pump.
    /// </summary>
    public static void Post(Action action)
    {
        Queue.Enqueue(new WorkItem(
            body: () =>
            {
                try { action(); }
                catch { /* decorative work: intentionally silent */ }
            },
            discard: null));
    }

    /// <summary>
    /// Drains the queue. Called exactly once per frame from OnUpdate, ahead of the mod's own
    /// logic. No per-frame budget and no time slice. Guaranteed never to throw.
    /// </summary>
    public static void Pump()
    {
        while (Queue.TryDequeue(out var item))
        {
            try { item.Invoke(); }
            catch { /* both enqueue paths already guard internally; belt and braces */ }
        }
    }

    /// <summary>
    /// Discards every queued work item. Required on FullReset (F9) and scene changes: leftover
    /// closures were written against the old bound world and must not execute in the new one.
    /// Waiting callers fail immediately with their own timeout exception.
    /// </summary>
    public static void Clear()
    {
        while (Queue.TryDequeue(out var item)) item.Cancel();
    }
}
