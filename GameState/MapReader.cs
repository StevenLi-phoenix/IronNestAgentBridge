using Il2Cpp;
using Il2CppTMPro;
using MelonLoader;
using UnityEngine;

namespace IronNestAgentBridge.GameState;

/// <summary>
/// Reads the tactical map ("指挥桌"): revealed entities, player markers, turret location.
/// Same scene-object contract as IronNestFCS's MapTable, but read-only and independent of it.
/// Map-local units convert to kilometres with the 3.8164 factor used by the FCS mod.
/// </summary>
public class MapReader
{
    private const float MapLocalToKm = 3.8164f;

    private Transform? _turretLocation;
    private Transform? _mapSurface;
    private Transform? _fireMissionRoot;
    private readonly Dictionary<int, Transform> _markers = new();

    // Previous snapshot keyed by entity id, for diffing.
    private Dictionary<string, MapEntityDto> _previous = new();

    public bool IsBound { get; private set; }

    public void Unbind()
    {
        IsBound = false;
        _turretLocation = null;
        _mapSurface = null;
        _fireMissionRoot = null;
        _markers.Clear();
        _previous = new Dictionary<string, MapEntityDto>();
    }

    public bool TryBind()
    {
        _turretLocation = GameObject.Find("TurretLocation")?.transform;
        _mapSurface = GameObject.Find("Draggable Surface")?.transform;
        _fireMissionRoot = GameObject.Find("Fire Mission Root")?.transform;

        if (_turretLocation == null || _mapSurface == null)
            return false;

        _markers.Clear();
        for (var i = 0; i < _mapSurface.childCount; i++)
        {
            var child = _mapSurface.GetChild(i);
            if (child.name != "MapToken_Artillery")
                continue;
            var tmp = child.GetComponentInChildren<TextMeshPro>();
            if (tmp != null && int.TryParse(tmp.text, out var id))
                _markers[id] = child;
        }

        IsBound = true;
        return true;
    }

    public Vector3 TurretLocalOnMap()
    {
        if (_mapSurface == null || _turretLocation == null)
            return Vector3.zero;
        return _mapSurface.InverseTransformPoint(_turretLocation.position);
    }

    private (float bearing, float distanceKm) Solution(Vector3 entityLocal, Vector3 turretLocal)
    {
        var delta = entityLocal - turretLocal;
        delta.z = 0;
        var distance = delta.magnitude * MapLocalToKm;
        var angle = Vector3.SignedAngle(delta, Vector3.up, Vector3.forward);
        if (angle < 0) angle += 360f;
        return (angle, distance);
    }

    public List<MarkerDto> ReadMarkers()
    {
        var result = new List<MarkerDto>();
        if (!IsBound) return result;
        var turretLocal = TurretLocalOnMap();
        foreach (var (id, tr) in _markers)
        {
            if (tr == null) continue;
            var local = tr.localPosition;
            var (bearing, dist) = Solution(local, turretLocal);
            result.Add(new MarkerDto { Id = id, MapX = local.x, MapY = local.y, BearingDeg = bearing, DistanceKm = dist });
        }
        return result;
    }

    /// <summary>Move a marker onto a map-local position (used for entity-targeted fire missions).</summary>
    public bool TryMoveMarker(int id, float mapX, float mapY)
    {
        if (!_markers.TryGetValue(id, out var tr) || tr == null)
            return false;
        var p = tr.localPosition;
        tr.localPosition = new Vector3(mapX, mapY, p.z);
        return true;
    }

    public MapEntityDto? FindEntity(string entityId)
        => ReadEntities().FirstOrDefault(e => e.Id == entityId || e.RawId == entityId);

    public List<MapEntityDto> ReadEntities()
    {
        var result = new List<MapEntityDto>();
        if (!IsBound || _fireMissionRoot == null || _mapSurface == null)
            return result;

        var turretLocal = TurretLocalOnMap();
        for (var i = 0; i < _fireMissionRoot.childCount; i++)
        {
            var child = _fireMissionRoot.GetChild(i);
            var loc = child.GetComponent<EntityLocation>();
            if (loc == null) continue;

            MapEntity? entity = null;
            try { entity = loc.Entity; } catch { /* not initialized yet */ }
            if (entity == null) continue;

            var local = _mapSurface.InverseTransformPoint(child.position);
            var (bearing, dist) = Solution(local, turretLocal);

            var visible = false;
            try
            {
                visible = loc.VisualRoot != null && loc.VisualRoot.activeInHierarchy;
                if (visible && loc.VisibilityGroup != null)
                    visible = loc.VisibilityGroup.alpha > 0.05f;
            }
            catch { /* keep false */ }

            string[] immune = Array.Empty<string>();
            try
            {
                if (entity.ImmuneShells != null)
                    immune = entity.ImmuneShells.ToArray();
            }
            catch { }

            result.Add(new MapEntityDto
            {
                Id = entity.ID ?? child.name,
                RawId = entity.RawID ?? "",
                Role = ((Il2Cpp.EntityRoles)entity.Role).ToString(),
                RoleValue = (int)entity.Role,
                State = ((Il2Cpp.MapEntityStates)entity.State).ToString(),
                StateValue = (int)entity.State,
                Health = entity.Health,
                MaxHealth = entity.MaxHealth,
                Armour = entity.Armour,
                Stars = entity.Stars,
                IsAlive = entity.IsAlive,
                Visible = visible,
                ImmuneShells = immune,
                MapX = local.x,
                MapY = local.y,
                BearingDeg = bearing,
                DistanceKm = dist,
            });
        }
        return result;
    }

    /// <summary>
    /// Snapshot + diff against previous poll. Emits events for the LLM: entities newly revealed
    /// on the command table (which never appear on the telegraph), movement, damage, destruction.
    /// </summary>
    public void PollAndEmitEvents()
    {
        if (!IsBound) return;
        var current = ReadEntities();
        var currentById = new Dictionary<string, MapEntityDto>();

        foreach (var e in current)
        {
            currentById[e.Id] = e;
            _previous.TryGetValue(e.Id, out var prev);

            if (e.Visible && (prev == null || !prev.Visible))
            {
                EventLog.Append("entity_revealed", "map",
                    $"{e.Id} ({e.Role}) revealed at bearing {e.BearingDeg:F1}°, {e.DistanceKm:F2} km", e);
            }
            else if (prev != null && e.Visible)
            {
                var moved = Math.Abs(e.MapX - prev.MapX) + Math.Abs(e.MapY - prev.MapY) > 0.01f;
                if (moved)
                    EventLog.Append("entity_moved", "map",
                        $"{e.Id} moved to bearing {e.BearingDeg:F1}°, {e.DistanceKm:F2} km", e);
                if (e.Health < prev.Health && e.IsAlive)
                    EventLog.Append("entity_damaged", "map",
                        $"{e.Id} damaged: {e.Health}/{e.MaxHealth}", e);
            }

            if (prev != null && prev.IsAlive && !e.IsAlive)
                EventLog.Append("entity_destroyed", "map", $"{e.Id} destroyed", e);
        }

        _previous = currentById;
    }
}
