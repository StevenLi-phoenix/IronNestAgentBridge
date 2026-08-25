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
    private readonly Dictionary<int, Vector3> _markerHomes = new();

    // Previous snapshot keyed by entity id, for diffing.
    private Dictionary<string, MapEntityDto> _previous = new();

    public bool IsBound { get; private set; }

    public Transform? MapSurface => _mapSurface;

    /// <summary>
    /// Real km extent of THIS mission's map, measured from the physical map sheet at bind.
    /// Null when no plausible sheet renderer was found (callers fall back to the generous
    /// global envelope). Missions have different sheet sizes — the hardcoded A..Z envelope
    /// let blind fire sail kilometres past a small map's edge.
    /// </summary>
    public (float MinX, float MinY, float MaxX, float MaxY)? KmBounds { get; private set; }

    public void Unbind()
    {
        IsBound = false;
        KmBounds = null;
        _turretLocation = null;
        _mapSurface = null;
        _fireMissionRoot = null;
        _markers.Clear();
        _markerHomes.Clear();
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
            {
                _markers[id] = child;
                _markerHomes[id] = child.localPosition; // parking spot to return to after the shot
            }
        }

        KmBounds = MeasureKmBounds(_mapSurface);
        IsBound = true;
        return true;
    }

    /// <summary>
    /// Measure the map sheet: largest-area renderer under the surface, world AABB corners
    /// inverse-transformed into surface-local space, converted to km. Sanity-gated so a
    /// mis-picked prop can never shrink or explode the firing envelope.
    /// </summary>
    private static (float, float, float, float)? MeasureKmBounds(Transform surface)
    {
        Renderer? sheet = null;
        var sheetArea = 0f;
        var sheetMin = Vector2.zero;
        var sheetMax = Vector2.zero;

        foreach (var renderer in surface.GetComponentsInChildren<Renderer>())
        {
            var b = renderer.bounds;
            var min = new Vector2(float.MaxValue, float.MaxValue);
            var max = new Vector2(float.MinValue, float.MinValue);
            for (var i = 0; i < 8; i++)
            {
                var corner = new Vector3(
                    (i & 1) == 0 ? b.min.x : b.max.x,
                    (i & 2) == 0 ? b.min.y : b.max.y,
                    (i & 4) == 0 ? b.min.z : b.max.z);
                var local = surface.InverseTransformPoint(corner);
                min = Vector2.Min(min, new Vector2(local.x, local.y));
                max = Vector2.Max(max, new Vector2(local.x, local.y));
            }
            var area = (max.x - min.x) * (max.y - min.y);
            if (area > sheetArea)
            {
                sheetArea = area;
                sheet = renderer;
                sheetMin = min;
                sheetMax = max;
            }
        }

        if (sheet == null)
            return null;

        var minKmX = 10.016f + sheetMin.x * MapLocalToKm;
        var minKmY = 5.235f + sheetMin.y * MapLocalToKm;
        var maxKmX = 10.016f + sheetMax.x * MapLocalToKm;
        var maxKmY = 5.235f + sheetMax.y * MapLocalToKm;
        var width = maxKmX - minKmX;
        var height = maxKmY - minKmY;
        if (width is < 5f or > 40f || height is < 3f or > 30f)
        {
            MelonLogger.Warning(
                $"[AgentBridge] map sheet measurement implausible ({width:F1}x{height:F1}km via '{sheet.gameObject.name}') — keeping generous bounds");
            return null;
        }
        return (minKmX, minKmY, maxKmX, maxKmY);
    }

    public const string PlayerTurretPieceName = "Player Turret Piece";
    private Transform? _turretMapModel;

    /// <summary>The player's draggable turret piece on the table — inferred ground truth.</summary>
    private Transform? TurretMapModel()
    {
        if (_turretMapModel == null && _mapSurface != null)
            _turretMapModel = _mapSurface.Find(PlayerTurretPieceName);
        return _turretMapModel;
    }

    public Vector3 TurretLocalOnMap()
    {
        if (_mapSurface == null)
            return Vector3.zero;
        if (TurretMapModel() is { } piece)
            return piece.localPosition;
        if (_turretLocation == null)
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

    public IReadOnlyCollection<int> MarkerIds => _markers.Keys;

    /// <summary>Convert a turret-relative firing solution back to map-local coordinates.</summary>
    public Vector3 SolutionToMapLocal(float bearingDeg, float distanceKm)
    {
        var turretLocal = TurretLocalOnMap();
        var r = distanceKm / MapLocalToKm;
        var rad = bearingDeg * Mathf.Deg2Rad;
        return new Vector3(turretLocal.x + Mathf.Sin(rad) * r, turretLocal.y + Mathf.Cos(rad) * r, 0f);
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

    /// <summary>Visible entities only — fire missions must not target fog-of-war contacts.</summary>
    /// <summary>
    /// Move the player's turret piece on the table (NOT the real turret) to a km
    /// position. FCS and our solvers read the piece as the firing origin.
    /// </summary>
    public string SetDeclaredTurret(float kmX, float kmY)
    {
        if (_mapSurface == null)
            return "map not bound";
        if (TurretMapModel() is not { } piece)
            return $"'{PlayerTurretPieceName}' not found on the map";

        var local = piece.localPosition;
        local.x = (kmX - 10.016f) / MapLocalToKm;
        local.y = (kmY - 5.235f) / MapLocalToKm;
        piece.localPosition = local;
        return $"turret piece moved to km({kmX:F2},{kmY:F2}); solutions now use it as origin";
    }

    public bool ReturnMarkerHome(int id)
    {
        if (!_markers.TryGetValue(id, out var tr) || tr == null || !_markerHomes.TryGetValue(id, out var home))
            return false;
        tr.localPosition = home;
        return true;
    }

    public MapEntityDto? FindEntity(string entityId)
        => ReadEntities().FirstOrDefault(e => e.Visible && (e.Id == entityId || e.RawId == entityId));

    /// <summary>
    /// includeHidden=true is for internal diffing only. Anything exposed to the LLM
    /// must use the default: fog-of-war entities would be wallhack intel.
    /// </summary>
    public List<MapEntityDto> ReadEntities(bool includeHidden = false)
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

            if (!visible && !includeHidden)
                continue;

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
        var current = ReadEntities(includeHidden: true); // full list for transition tracking; events below only fire for visible entities
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

            if (prev != null && prev.Visible && prev.IsAlive && !e.IsAlive)
                EventLog.Append("entity_destroyed", "map", $"{e.Id} destroyed", e);
        }

        _previous = currentById;
    }
}
