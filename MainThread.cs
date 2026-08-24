using System.Collections.Concurrent;

namespace IronNestAgentBridge;

/// <summary>
/// Marshals work from HTTP listener threads onto Unity's main thread.
/// Game/Il2Cpp state may only be touched inside the OnUpdate pump.
/// </summary>
public static class MainThread
{
    private static readonly ConcurrentQueue<Action> Queue = new();

    public static Task<T> Run<T>(Func<T> func, int timeoutMs = 10_000)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        Queue.Enqueue(() =>
        {
            try { tcs.TrySetResult(func()); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        var cts = new CancellationTokenSource(timeoutMs);
        cts.Token.Register(() => tcs.TrySetException(
            new TimeoutException($"main-thread call not serviced within {timeoutMs}ms (game unfocused or scene loading?)")));
        return tcs.Task;
    }

    public static Task Run(Action action, int timeoutMs = 10_000)
        => Run<object?>(() => { action(); return null; }, timeoutMs);

    /// <summary>
    /// Fire-and-forget: enqueue for the main thread without waiting. For cosmetic work
    /// (map plotting etc.) that must never block the agent loop — if the game is paused
    /// it simply runs whenever the loop resumes.
    /// </summary>
    public static void Post(Action action)
        => Queue.Enqueue(() => { try { action(); } catch { /* cosmetic */ } });

    /// <summary>Called from AgentBridgeMod.OnUpdate every frame.</summary>
    public static void Pump()
    {
        while (Queue.TryDequeue(out var action))
            action();
    }
}
