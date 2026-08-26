using Il2Cpp;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Il2CppTMPro;
using MelonLoader;
using UnityEngine;

namespace IronNestAgentBridge.GameState;

/// <summary>
/// The command table: scene binding, sheet extent, the turret origin, player markers and the
/// enemy entity list. Stateful — the mod holds exactly one instance and its lifetime follows the
/// scene.
///
/// Fog of war is enforced here and nowhere else: <see cref="ReadEntities"/> drops invisible
/// entities by default, <see cref="FindEntity"/> refuses them outright, and the only caller
/// allowed to pass <c>includeHidden: true</c> is this class's own event diff — whose output never
/// leaves as anything but events about entities that are visible right now.
/// </summary>
public sealed class MapReader
{
    /// <summary>
    /// The authoritative physical anchor. Three different objects answer to this name; this one
    /// is the real one and it never moves. <c>Canvas/MapRoot/TurretLocation</c> is a static icon
    /// and <c>Draggable Surface/Player Turret Piece</c> is the draggable inference — do not mix.
    /// </summary>
    public const string TurretAnchorName = "TurretLocation";

    public const string MapSurfaceName = "Draggable Surface";
    public const string FireMissionRootName = "Fire Mission Root";

    /// <summary>
    /// Where headquarters BELIEVES the turret stands. FCS and the bridge both solve from this
    /// piece's localPosition, so a misplaced piece means missed shots — by design.
    /// </summary>
    public const string PlayerTurretPieceName = "Player Turret Piece";

    public const string MarkerTokenName = "MapToken_Artillery";

    /// <summary>Seconds between bind attempts while the scene is not ready. Driven by the mod.</summary>
    public const float BindRetrySeconds = 2f;

    /// <summary>
    /// Seconds between entity polls. The impact poll shares this beat, and the two must be
    /// guarded separately: a map poll failure may not stop impacts from being recorded.
    /// </summary>
    public const float MapPollSeconds = 0.5f;

    /// <summary>map-local units; roughly 38 m. Not km and not metres.</summary>
    private const float MoveEpsilonLocal = 0.01f;

    /// <summary>Below this canvas alpha the entity is inside the fog as far as the player is concerned.</summary>
    private const float VisibleAlpha = 0.05f;

    private Transform? _turretAnchor;
    private Transform? _mapSurface;
    private Transform? _fireMissionRoot;

    /// <summary>Cached lookup of the draggable turret piece; dropped on <see cref="Unbind"/>.</summary>
    private Transform? _turretPiece;

    private readonly Dictionary<int, Transform> _markers = new();

    /// <summary>Last polled entity table, keyed by id, INCLUDING entities hidden by fog.</summary>
    private Dictionary<string, MapEntityDto> _previous = new();

    public bool IsBound { get; private set; }

    /// <summary>Handed to <see cref="ImpactReader"/>; null until bound.</summary>
    public Transform? MapSurface => _mapSurface;

    /// <summary>Measured sheet envelope in km, or null when the measurement was implausible.</summary>
    public (float MinX, float MinY, float MaxX, float MaxY)? KmBounds { get; private set; }

    // ---------------------------------------------------------------- binding

    /// <summary>
    /// Binds the three scene objects and measures the sheet. Returns false without side effects
    /// beyond <see cref="Unbind"/> when either required object is missing, so the caller can
    /// simply retry on its own cadence.
    /// </summary>
    public bool TryBind()
    {
        Unbind();

        var anchor = Il2CppSafe.GetRef(() => GameObject.Find(TurretAnchorName));
        var surface = Il2CppSafe.GetRef(() => GameObject.Find(MapSurfaceName));
        if (anchor == null || surface == null) return false;

        _turretAnchor = anchor.transform;
        _mapSurface = surface.transform;

        // Optional: without it we simply read no entities.
        var root = Il2CppSafe.GetRef(() => GameObject.Find(FireMissionRootName));
        _fireMissionRoot = root == null ? null : root.transform;

        ScanMarkers();

        KmBounds = MeasureSheetKm();
        if (KmBounds.HasValue)
        {
            var b = KmBounds.Value;
            Agent.GridMath.SetMapBoundsKm(b.MinX, b.MinY, b.MaxX, b.MaxY);
        }
        else
        {
            // A stale envelope from the previous mission is worse than no envelope at all.
            Agent.GridMath.ResetMapBounds();
        }

        IsBound = true;
        return true;
    }

    /// <summary>
    /// Drops every scene reference and all derived state. Called on scene load and on the full
    /// reset; after it the reader is indistinguishable from a freshly constructed one.
    /// </summary>
    public void Unbind()
    {
        // Shell specifications belong to the mission that loaded them. Only drop them when a real
        // binding is being torn down, so a bind-retry loop does not thrash the cache.
        if (IsBound) AmmoReader.ClearSpecCache();

        IsBound = false;
        KmBounds = null;
        _turretAnchor = null;
        _mapSurface = null;
        _fireMissionRoot = null;
        _turretPiece = null;
        _markers.Clear();
        _previous = new Dictionary<string, MapEntityDto>();
        Agent.GridMath.ResetMapBounds();
    }

    private void ScanMarkers()
    {
        var surface = _mapSurface;
        if (surface == null) return;

        var count = Il2CppSafe.Get(() => surface.childCount, 0);
        for (var i = 0; i < count; i++)
        {
            var child = Il2CppSafe.GetRef(() => surface.GetChild(i));
            if (child == null) continue;
            if (Il2CppSafe.Get(() => child.name, "") != MarkerTokenName) continue;

            var label = Il2CppSafe.GetRef(() => child.GetComponentInChildren<TextMeshPro>());
            if (label == null) continue;

            var text = Il2CppSafe.Get(() => label.text, "");
            if (!int.TryParse(text.Trim(), out var id)) continue;

            _markers[id] = child;
        }
    }

    // ---------------------------------------------------------------- sheet extent

    /// <summary>
    /// Measures the real firing envelope of THIS mission from the largest renderer under the map
    /// surface. A hard-coded A..Z envelope once let blind fire land kilometres past a small map's
    /// edge, which is why the measurement exists at all.
    ///
    /// Returns null — and keeps the caller on the generous fallback envelope — whenever the
    /// measured sheet is not a plausible tactical map.
    /// </summary>
    private (float MinX, float MinY, float MaxX, float MaxY)? MeasureSheetKm()
    {
        var surface = _mapSurface;
        if (surface == null) return null;

        Renderer? sheet = null;
        var bestArea = 0f;
        float minX = 0f, minY = 0f, maxX = 0f, maxY = 0f;

        Il2CppArrayBase<Renderer>? found;
        try { found = surface.GetComponentsInChildren<Renderer>(); }
        catch { return null; }
        if (found == null) return null;

        Il2CppArrayBase<Renderer> renderers = found;
        var rendererCount = Il2CppSafe.Get(() => renderers.Length, 0);
        for (var r = 0; r < rendererCount; r++)
        {
            var renderer = Il2CppSafe.GetRef(() => renderers[r]);
            if (renderer == null) continue;

            try
            {
                var bounds = renderer.bounds;
                var lo = bounds.min;
                var hi = bounds.max;

                var rMinX = float.MaxValue;
                var rMinY = float.MaxValue;
                var rMaxX = float.MinValue;
                var rMaxY = float.MinValue;

                // All eight corners: the surface may be rotated relative to world space.
                for (var c = 0; c < 8; c++)
                {
                    var corner = new Vector3(
                        (c & 1) == 0 ? lo.x : hi.x,
                        (c & 2) == 0 ? lo.y : hi.y,
                        (c & 4) == 0 ? lo.z : hi.z);
                    var local = surface.InverseTransformPoint(corner);
                    if (local.x < rMinX) rMinX = local.x;
                    if (local.y < rMinY) rMinY = local.y;
                    if (local.x > rMaxX) rMaxX = local.x;
                    if (local.y > rMaxY) rMaxY = local.y;
                }

                var area = (rMaxX - rMinX) * (rMaxY - rMinY);
                if (area <= bestArea) continue;

                bestArea = area;
                sheet = renderer;
                minX = rMinX;
                minY = rMinY;
                maxX = rMaxX;
                maxY = rMaxY;
            }
            catch
            {
                // One unreadable renderer must not abort the survey.
            }
        }

        if (sheet == null) return null;

        var kmLo = MapFrame.LocalToKm(minX, minY);
        var kmHi = MapFrame.LocalToKm(maxX, maxY);
        var width = kmHi.x - kmLo.x;
        var height = kmHi.y - kmLo.y;

        // Plausibility gate: anything outside these spans is some other renderer, not the sheet.
        if (width < 5f || width > 40f || height < 3f || height > 30f)
        {
            var name = Il2CppSafe.Get(() => sheet.gameObject.name, "?");
            MelonLogger.Warning(
                $"[AgentBridge] map sheet measurement implausible ({width:F1}x{height:F1}km via '{name}') — keeping generous bounds");
            return null;
        }

        return (kmLo.x, kmLo.y, kmHi.x, kmHi.y);
    }

    // ---------------------------------------------------------------- turret origin

    /// <summary>
    /// The firing origin in map-local space. Priority is fixed and must not be reordered:
    /// the draggable piece is the inference headquarters actually acts on; the real anchor is
    /// only a fallback for missions that have no piece.
    /// </summary>
    public Vector3 TurretLocalOnMap()
    {
        var surface = _mapSurface;
        if (surface == null) return Vector3.zero;

        var piece = ResolveTurretPiece();
        if (piece != null)
        {
            var local = Il2CppSafe.Get(() => piece.localPosition, Vector3.zero);
            return local;
        }

        var anchor = _turretAnchor;
        if (anchor == null) return Vector3.zero;

        return Il2CppSafe.Get(() => surface.InverseTransformPoint(anchor.position), Vector3.zero);
    }

    private Transform? ResolveTurretPiece()
    {
        if (_turretPiece != null) return _turretPiece;

        var surface = _mapSurface;
        if (surface == null) return null;

        _turretPiece = Il2CppSafe.GetRef(() => surface.Find(PlayerTurretPieceName));
        return _turretPiece;
    }

    /// <summary>
    /// Moves the inference piece only. The real anchor is never touched: this declares where we
    /// BELIEVE the turret is, which is exactly what registration fire corrects.
    /// </summary>
    public (bool ok, string message) SetDeclaredTurret(float kmX, float kmY)
    {
        if (!IsBound || _mapSurface == null) return (false, "map not bound");

        var piece = ResolveTurretPiece();
        if (piece == null) return (false, $"'{PlayerTurretPieceName}' not found on the map");

        var moved = false;
        Il2CppSafe.Do(() =>
        {
            var current = piece.localPosition;
            // z carries the piece's resting height above the sheet; only the plane is declared.
            var target = MapFrame.KmToLocal(kmX, kmY, current.z);
            piece.localPosition = target;
            moved = true;
        });

        if (!moved) return (false, $"'{PlayerTurretPieceName}' not found on the map");

        return (true, $"turret piece moved to km({kmX:F2},{kmY:F2}); solutions now use it as origin");
    }

    // ---------------------------------------------------------------- solving helpers

    /// <summary>Situational bearing/range from the turret piece to a map-local point.</summary>
    public (float bearingDeg, float distanceKm) Solution(Vector3 entityLocal, Vector3 turretLocal)
    {
        var delta = entityLocal - turretLocal;
        delta.z = 0f;
        return (MapFrame.BearingOf(delta), MapFrame.DistanceKm(delta));
    }

    /// <summary>Inverse of <see cref="Solution"/>, anchored on the current turret piece.</summary>
    public Vector3 SolutionToMapLocal(float bearingDeg, float distanceKm)
        => MapFrame.FromBearing(TurretLocalOnMap(), bearingDeg, distanceKm);

    // ---------------------------------------------------------------- markers

    /// <summary>
    /// Player artillery tokens. MapX/MapY are map-local, not km — the bridge never moves these
    /// tokens, it only reports them.
    /// </summary>
    public List<MarkerDto> ReadMarkers()
    {
        var result = new List<MarkerDto>();
        if (!IsBound) return result;

        var turret = TurretLocalOnMap();
        foreach (var pair in _markers)
        {
            var transform = pair.Value;
            if (transform == null) continue;

            var local = Il2CppSafe.Get(() => transform.localPosition, Vector3.zero);
            var solution = Solution(local, turret);
            result.Add(new MarkerDto
            {
                Id = pair.Key,
                MapX = local.x,
                MapY = local.y,
                BearingDeg = solution.bearingDeg,
                DistanceKm = solution.distanceKm,
            });
        }

        return result;
    }

    // ---------------------------------------------------------------- entities

    /// <summary>
    /// Reads the entity table. <paramref name="includeHidden"/> is reserved for this class's own
    /// event diff — an entity that walks into the fog must not read as "disappeared" and then
    /// re-emit a reveal on its way out. Any path that reaches the LLM uses the default.
    /// </summary>
    public List<MapEntityDto> ReadEntities(bool includeHidden = false)
    {
        var result = new List<MapEntityDto>();
        var surface = _mapSurface;
        var root = _fireMissionRoot;
        if (!IsBound || surface == null || root == null) return result;

        var turret = TurretLocalOnMap();
        var count = Il2CppSafe.Get(() => root.childCount, 0);

        for (var i = 0; i < count; i++)
        {
            var child = Il2CppSafe.GetRef(() => root.GetChild(i));
            if (child == null) continue;

            var location = Il2CppSafe.GetRef(() => child.GetComponent<EntityLocation>());
            if (location == null) continue;

            // Null while the location is still initialising; skip this frame, not the mission.
            var entity = Il2CppSafe.GetRef(() => location.Entity);
            if (entity == null) continue;

            var visible = IsVisible(location);
            if (!visible && !includeHidden) continue;

            var local = Il2CppSafe.Get(() => surface.InverseTransformPoint(child.position), Vector3.zero);
            var solution = Solution(local, turret);

            var dto = new MapEntityDto
            {
                Id = Il2CppSafe.GetRef(() => entity.ID) ?? Il2CppSafe.Get(() => child.name, ""),
                RawId = Il2CppSafe.GetRef(() => entity.RawID) ?? "",
                Visible = visible,
                MapX = local.x,
                MapY = local.y,
                BearingDeg = solution.bearingDeg,
                DistanceKm = solution.distanceKm,
            };

            Il2CppSafe.Do(() => dto.Role = entity.Role.ToString());
            Il2CppSafe.Do(() => dto.RoleValue = (int)entity.Role);
            Il2CppSafe.Do(() => dto.State = entity.State.ToString());
            Il2CppSafe.Do(() => dto.StateValue = (int)entity.State);
            Il2CppSafe.Do(() => dto.Health = entity.Health);
            Il2CppSafe.Do(() => dto.MaxHealth = entity.MaxHealth);
            Il2CppSafe.Do(() => dto.Armour = entity.Armour);
            // Semantics unknown; forwarded verbatim rather than dropped.
            Il2CppSafe.Do(() => dto.Stars = entity.Stars);
            Il2CppSafe.Do(() => dto.IsAlive = entity.IsAlive);
            Il2CppSafe.Do(() => dto.ImmuneShells = ReadImmuneShells(entity));

            result.Add(dto);
        }

        return result;
    }

    private static string[] ReadImmuneShells(MapEntity entity)
    {
        var list = entity.ImmuneShells;
        if (list == null) return Array.Empty<string>();

        var buffer = new List<string>();
        var count = list.Count;
        for (var i = 0; i < count; i++)
        {
            var index = i;
            var id = Il2CppSafe.GetRef(() => list[index]);
            if (!string.IsNullOrEmpty(id)) buffer.Add(id!);
        }
        return buffer.ToArray();
    }

    /// <summary>
    /// Fog test. The visual root carries reveal state; the canvas group fades it. Anything that
    /// throws counts as invisible — the safe direction, because a false "visible" leaks map
    /// knowledge the player does not have.
    /// </summary>
    private static bool IsVisible(EntityLocation location)
    {
        try
        {
            var root = location.VisualRoot;
            if (root == null || !root.activeInHierarchy) return false;

            var group = location.VisibilityGroup;
            if (group != null && group.alpha <= VisibleAlpha) return false;

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Resolves an LLM-supplied target id against the VISIBLE table only, matching either the
    /// display id or the raw asset id. Case sensitive on purpose: the ids handed out in the
    /// snapshot are meant to be echoed back verbatim.
    /// </summary>
    public MapEntityDto? FindEntity(string entityId)
    {
        foreach (var entity in ReadEntities())
        {
            if (entity.Visible && (entity.Id == entityId || entity.RawId == entityId)) return entity;
        }
        return null;
    }

    // ---------------------------------------------------------------- entity events

    /// <summary>
    /// Diffs the full entity table against the previous poll and emits map events. Events are
    /// only ever emitted for entities that are visible RIGHT NOW, so the hidden half of the table
    /// exists purely to keep the diff honest.
    /// </summary>
    public void PollAndEmitEvents()
    {
        if (!IsBound) return;

        var current = ReadEntities(includeHidden: true);
        var next = new Dictionary<string, MapEntityDto>(current.Count);

        foreach (var entity in current)
        {
            next[entity.Id] = entity;
            _previous.TryGetValue(entity.Id, out var previous);

            if (entity.Visible)
            {
                if (previous == null || !previous.Visible)
                {
                    EventLog.Append("entity_revealed", "map",
                        $"{entity.Id} ({entity.Role}) 显现: 方位 {entity.BearingDeg:F1}°, {entity.DistanceKm:F2} km",
                        entity);
                }
                else
                {
                    if (Math.Abs(entity.MapX - previous.MapX) + Math.Abs(entity.MapY - previous.MapY) > MoveEpsilonLocal)
                    {
                        EventLog.Append("entity_moved", "map",
                            $"{entity.Id} 移动至: 方位 {entity.BearingDeg:F1}°, {entity.DistanceKm:F2} km",
                            entity);
                    }

                    if (entity.Health < previous.Health && entity.IsAlive)
                    {
                        EventLog.Append("entity_damaged", "map",
                            $"{entity.Id} 受损: {entity.Health}/{entity.MaxHealth}", entity);
                    }
                }
            }

            // Destruction requires the target to have been VISIBLE last poll. A kill inside the
            // fog is therefore never reported — by the time it re-emerges prev.IsAlive is already
            // false. Deliberate: reporting it would hand the LLM knowledge the player lacks.
            if (previous != null && previous.Visible && previous.IsAlive && !entity.IsAlive)
            {
                EventLog.Append("entity_destroyed", "map", $"{entity.Id} 已摧毁", entity);
            }
        }

        _previous = next;
    }
}
