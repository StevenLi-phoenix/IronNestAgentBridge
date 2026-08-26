namespace IronNestAgentBridge.GameState;

/// <summary>
/// Field-granular guards for Il2Cpp member access, the most common defensive pattern in this
/// layer. A single unmapped property, a destroyed native object or a null interior reference must
/// never take down a whole DTO or abort a loop — the reader keeps the field's default and moves on.
///
/// These wrappers exist so that granularity stays cheap to write: one guard per property read,
/// never one guard per record.
/// </summary>
internal static class Il2CppSafe
{
    /// <summary>Runs a write/side-effecting read and swallows any failure.</summary>
    public static void Do(Action action)
    {
        try { action(); }
        catch { /* keep the caller's default and continue */ }
    }

    /// <summary>Reads one member, falling back to <paramref name="fallback"/> on any failure.</summary>
    public static T Get<T>(Func<T> read, T fallback)
    {
        try { return read(); }
        catch { return fallback; }
    }

    /// <summary>Reference overload; the fallback is null.</summary>
    public static T? GetRef<T>(Func<T?> read) where T : class
    {
        try { return read(); }
        catch { return null; }
    }
}
