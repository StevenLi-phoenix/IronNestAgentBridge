namespace IronNestAgentBridge.Agent;

public class QueuedMission
{
    public FireMissionRequest Request { get; init; } = new();
    public string Label { get; init; } = "";
    public int Priority { get; init; }   // higher dispatches first
    public long Seq { get; init; }       // FIFO tiebreaker
}

/// <summary>
/// The agent's internal priority queue. The LLM stages missions here with a priority;
/// the mod drains it into the FCS only while the physical queue is shallow, re-validating
/// each target at dispatch time (dead/fog-hidden targets are dropped, not fired at).
/// </summary>
public class MissionQueue
{
    private readonly object _gate = new();
    private readonly List<QueuedMission> _items = new();
    private long _seq;

    public int Count
    {
        get { lock (_gate) return _items.Count; }
    }

    public void Add(FireMissionRequest request, int priority, string label)
    {
        lock (_gate)
        {
            _items.Add(new QueuedMission { Request = request, Priority = priority, Label = label, Seq = _seq++ });
        }
    }

    public QueuedMission? PopBest()
    {
        lock (_gate)
        {
            if (_items.Count == 0) return null;
            QueuedMission? best = null;
            foreach (var item in _items)
                if (best == null || item.Priority > best.Priority
                    || item.Priority == best.Priority && item.Seq < best.Seq)
                    best = item;
            _items.Remove(best!);
            return best;
        }
    }

    /// <summary>Remove staged missions matching a predicate (e.g. target died). Returns removed labels.</summary>
    public List<string> RemoveWhere(Func<QueuedMission, bool> predicate)
    {
        lock (_gate)
        {
            var removed = _items.Where(predicate).ToList();
            foreach (var item in removed)
                _items.Remove(item);
            return removed.Select(m => m.Label).ToList();
        }
    }

    public void Clear()
    {
        lock (_gate) _items.Clear();
    }

    public List<string> Describe()
    {
        lock (_gate)
            return _items
                .OrderByDescending(m => m.Priority).ThenBy(m => m.Seq)
                .Select(m => $"P{m.Priority} {m.Label} ({m.Request.Shell})")
                .ToList();
    }
}
