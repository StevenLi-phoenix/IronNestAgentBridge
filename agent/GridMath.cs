using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace IronNestAgentBridge.Agent;

/// <summary>
/// Grid notation, map bounds and the intersection solver behind the <c>grid_to_km</c> and
/// <c>solve_target</c> tools.
///
/// Pure BCL, no Unity, no Il2Cpp, no IO — so it runs straight on the agent thread with no
/// main-thread marshalling, which is what keeps the tool path cheap.
///
/// This module works exclusively in the km frame. The map-local scale 3.8164 and the origin
/// offset (10.016, 5.235) must NEVER appear in here: callers convert on both sides, and
/// applying the conversion twice is the classic coordinate bug in this project.
///
/// Direction convention, the easiest thing to get backwards: letters A..Z are the x axis
/// running west to east; digits 1..N are the y axis running SOUTH to NORTH, so 1 is the
/// bottom-most row. Bearings are 0 = north, increasing clockwise, so the unit vector is
/// (sin θ, cos θ).
/// </summary>
public static class GridMath
{
    // ---------------------------------------------------------------- point parsing

    // Letter, 1-2 digit row, then the 0.1 km sub-cell "a:b". Whitespace is tolerated between
    // the letter and the row and around the colon; at least one space separates row from
    // sub-cell. Variants without a sub-cell ("G6") deliberately do not parse.
    private static readonly Regex GridPattern =
        new(@"^\s*([A-Za-z])\s*(\d{1,2})\s+(\d)\s*:\s*(\d)\s*$", RegexOptions.Compiled);

    /// <summary>
    /// Parses a point spec — the literal "turret", a grid reference, or a bare "kmX,kmY" pair —
    /// into km. Returns null on failure and never throws.
    /// </summary>
    public static (float x, float y)? ParsePoint(string? spec, (float x, float y) turretKm)
    {
        if (spec == null) return null;
        var s = spec.Trim();
        if (s.Length == 0) return null;

        if (string.Equals(s, "turret", StringComparison.OrdinalIgnoreCase)) return turretKm;

        var m = GridPattern.Match(s);
        if (m.Success)
        {
            var letter = char.ToUpperInvariant(m.Groups[1].Value[0]);
            var row = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            var subCol = m.Groups[3].Value[0] - '0';
            var subRow = m.Groups[4].Value[0] - '0';

            // +0.05 puts the point at the CENTRE of the 0.1 km sub-cell, not its corner.
            return ((float)(letter - 'A' + subCol / 10.0 + 0.05),
                    (float)(row - 1 + subRow / 10.0 + 0.05));
        }

        // Invariant culture is mandatory: a Chinese Windows must not reinterpret the decimal point.
        var parts = s.Split(',');
        if (parts.Length == 2
            && double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var kx)
            && double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var ky))
        {
            return ((float)kx, (float)ky);
        }

        return null;
    }

    /// <summary>
    /// km to display grid, same formula as the FCS ConvertPosition.
    ///
    /// Out of range on EITHER axis prints "#" for that axis: truncation towards zero would let
    /// x = -0.5 pretend to be column A, y = -0.5 pretend to be row 1, and either sub-cell digit
    /// come back negative ("D1 0:-5"). A grid string that looks plausible but is wrong is worse
    /// than one that visibly refuses, because it reaches the model as an impact report.
    /// </summary>
    public static string GridOf((float x, float y) p)
    {
        var xInRange = p.x >= 0f && p.x < 26f;
        var yInRange = p.y >= 0f;

        var col = xInRange ? ((char)('A' + (int)p.x)).ToString() : "#";
        var row = yInRange ? ((int)p.y + 1).ToString(CultureInfo.InvariantCulture) : "#";
        var subCol = xInRange ? ((int)(p.x * 10f) % 10).ToString(CultureInfo.InvariantCulture) : "#";
        var subRow = yInRange ? ((int)(p.y * 10f) % 10).ToString(CultureInfo.InvariantCulture) : "#";

        return $"{col}{row} {subCol}:{subRow}";
    }

    // ---------------------------------------------------------------- map bounds

    /// <summary>Slack added on each of the four edges.</summary>
    private const float EdgeMarginKm = 0.3f;

    /// <summary>
    /// Immutable bounds snapshot. Swapped atomically through a volatile reference so a reader
    /// can never observe a half-updated envelope.
    /// </summary>
    private sealed record MapBounds(float MinX, float MinY, float MaxX, float MaxY, bool Measured);

    /// <summary>Generous global envelope used until the sheet has actually been measured.</summary>
    private static readonly MapBounds FallbackBounds = new(-1f, -1f, 27f, 16f, false);

    // The only mutable global in this module. It is process-wide, so scene load and the full
    // reset must clear it — otherwise a mission inherits the previous mission's sheet.
    private static volatile MapBounds _bounds = FallbackBounds;

    public static void SetMapBoundsKm(float minX, float minY, float maxX, float maxY)
        => _bounds = new MapBounds(minX, minY, maxX, maxY, true);

    public static void ResetMapBounds() => _bounds = FallbackBounds;

    /// <summary>Closed interval, each edge widened by <see cref="EdgeMarginKm"/>.</summary>
    public static bool InMapBounds((float x, float y) p)
    {
        var b = _bounds;
        return p.x >= b.MinX - EdgeMarginKm && p.x <= b.MaxX + EdgeMarginKm
            && p.y >= b.MinY - EdgeMarginKm && p.y <= b.MaxY + EdgeMarginKm;
    }

    /// <summary>Planning envelope shown to the LLM in the snapshot.</summary>
    public static string MapBoundsText
    {
        get
        {
            var b = _bounds;
            return b.Measured
                ? string.Format(CultureInfo.InvariantCulture,
                    "km({0:F1},{1:F1})-({2:F1},{3:F1})", b.MinX, b.MinY, b.MaxX, b.MaxY)
                : "未实测(宽松包络)";
        }
    }

    // ---------------------------------------------------------------- tool: grid_to_km

    /// <summary>
    /// Position only — gunnery data is <c>firing_solution</c>'s job, because that reads the
    /// live turret origin.
    /// </summary>
    public static string GridToKm(JsonElement args, (float x, float y) turretKm)
    {
        try
        {
            var from = StringOf(args, "grid");
            var p = ParsePoint(from, turretKm);
            if (p == null) return Error($"cannot parse grid '{from}' (expected like 'G6 5:3')");
            return Result(ToD(p.Value));
        }
        catch (Exception ex)
        {
            // No entry point may throw at the caller: a wrong JSON value kind becomes a
            // structured error, not an exception.
            return Error(ex.Message);
        }
    }

    // ---------------------------------------------------------------- tool: solve_target

    /// <summary>Drawing by-product: what the solver would like plotted on the tactical map.</summary>
    public sealed class SolveGeometry
    {
        /// <summary>Observation strokes, (start, end).</summary>
        public List<((float x, float y) From, (float x, float y) To)> Lines { get; } = new();

        /// <summary>Range circles, (centre, radius km).</summary>
        public List<((float x, float y) Center, float RadiusKm)> Circles { get; } = new();

        /// <summary>Set only on the success path; ambiguous and error paths leave it null.</summary>
        public (float x, float y)? Solution { get; set; }
    }

    private readonly record struct Observation((double x, double y) From, double BearingDeg, double? DistanceKm);

    private readonly record struct RangeCircle((double x, double y) Center, double RadiusKm);

    public static string SolveTarget(JsonElement args, (float x, float y) turretKm)
        => SolveTarget(args, turretKm, out _);

    public static string SolveTarget(JsonElement args, (float x, float y) turretKm, out SolveGeometry geometry)
    {
        geometry = new SolveGeometry();
        try
        {
            return Solve(args, turretKm, geometry);
        }
        catch (Exception ex)
        {
            return Error(ex.Message);
        }
    }

    private static string Solve(JsonElement args, (float x, float y) turretKm, SolveGeometry geometry)
    {
        var lines = new List<Observation>();
        var circles = new List<RangeCircle>();
        var directs = new List<(double x, double y)>();

        if (TryArray(args, "lines", out var lineArray))
        {
            foreach (var l in lineArray.EnumerateArray())
            {
                var from = StringOf(l, "from");
                var origin = ParsePoint(from, turretKm);
                if (origin == null) return Error($"cannot parse point '{from}'");
                if (!TryNumber(l, "bearingDeg", out var bearing)) return Error("line missing bearingDeg");

                // A distance only counts as a direct fix when it really is a number; anything
                // else degrades the line to a pure bearing observation.
                double? distance = TryNumber(l, "distanceKm", out var d) ? (double?)d : null;

                var obs = new Observation(ToD(origin.Value), bearing, distance);
                lines.Add(obs);

                if (distance.HasValue)
                {
                    // Direct fixes are plotted at parse time — they stand even if the overall
                    // solve later fails.
                    var fix = Offset(obs.From, bearing, distance.Value);
                    directs.Add(fix);
                    geometry.Lines.Add((ToF(obs.From), ToF(fix)));
                }
            }
        }

        if (TryArray(args, "circles", out var circleArray))
        {
            foreach (var c in circleArray.EnumerateArray())
            {
                var from = StringOf(c, "from");
                var center = ParsePoint(from, turretKm);
                if (center == null) return Error($"cannot parse point '{from}'");
                if (!TryNumber(c, "distanceKm", out var radius)) return Error("circle missing distanceKm");

                circles.Add(new RangeCircle(ToD(center.Value), radius));
                geometry.Circles.Add((center.Value, (float)radius));
            }
        }

        // A "near" that cannot be read is an ERROR, never a silent fall-through to the ambiguous
        // branch: the model supplied it precisely to disambiguate, and degrading it would answer a
        // question it did not ask. A wrong value kind is treated the same way as an unparseable
        // string — null alone means "not supplied".
        (double x, double y)? near = null;
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("near", out var nearElement)
            && nearElement.ValueKind != JsonValueKind.Null
            && nearElement.ValueKind != JsonValueKind.Undefined)
        {
            if (nearElement.ValueKind != JsonValueKind.String)
                return Error($"near must be a point string like 'G6 5:3' or 'kmX,kmY', got {nearElement.ValueKind}");

            var spec = nearElement.GetString() ?? "";
            if (spec.Trim().Length > 0)
            {
                var p = ParsePoint(spec, turretKm);
                if (p == null) return Error($"cannot parse point '{spec}'");
                near = ToD(p.Value);
            }
        }

        // Strict priority: first satisfied wins. Extra observations are ignored, not averaged
        // and not cross-checked.
        List<(double x, double y)> candidates;
        var used = 2;
        var circleCirclePath = false;

        if (directs.Count > 0)
        {
            candidates = new List<(double x, double y)> { directs[0] };
            used = 1;
        }
        else if (lines.Count >= 2)
        {
            if (!TryTwoLines(lines[0], lines[1], out var cross, out var error)) return Error(error);
            candidates = new List<(double x, double y)> { cross };
        }
        else if (lines.Count >= 1 && circles.Count >= 1)
        {
            if (!TryLineCircle(lines[0], circles[0], out candidates, out var error)) return Error(error);
        }
        else if (circles.Count >= 2)
        {
            if (!TryCircleCircle(circles[0], circles[1], out candidates, out var error)) return Error(error);
            circleCirclePath = true;
        }
        else
        {
            return Error("need at least: 1 line with distanceKm, or 2 lines, or line+circle, or 2 circles");
        }

        // Two circle intersections with no tie-breaker: never guess. Hand both fully solved
        // candidates back and let the model pick on other intelligence. This returns BEFORE the
        // out-of-bounds gate, so a candidate may sit off-sheet — flagged, not rejected.
        if (circleCirclePath && candidates.Count == 2 && near == null)
        {
            return JsonSerializer.Serialize(new
            {
                ambiguous = true,
                note = "两圆有两个交点, 按其他情报选择其一直接使用, 或用near重解",
                candidates = candidates.ConvertAll(c => new
                {
                    kmX = Math.Round(c.x, 3),
                    kmY = Math.Round(c.y, 3),
                    grid = GridOf(ToF(c)),
                    inMapBounds = InMapBounds(ToF(c)),
                }),
            }, JsonOptions);
        }

        var solution = PickNearest(candidates, near);

        // A solution outside the sheet means an observation is wrong, not that the map is small.
        // A loose envelope once let blind fire land 7 km beyond a small map's real edge.
        if (!InMapBounds(ToF(solution)))
        {
            return Error($"solution {PointText(solution)} km is outside the map — an observation is wrong "
                       + "(bearing reversed, wrong observer grid, or mismatched pairing); re-check the report, do not fire at this");
        }

        // Pure bearing lines are plotted as segments ending at the fix, not as endless rays.
        foreach (var l in lines)
        {
            if (!l.DistanceKm.HasValue) geometry.Lines.Add((ToF(l.From), ToF(solution)));
        }
        geometry.Solution = ToF(solution);

        var ignored = lines.Count + circles.Count - used;
        return Result(solution, ignored > 0 ? $"(忽略多余观测 {ignored} 条)" : null);
    }

    // ---------------------------------------------------------------- geometry

    private static (double x, double y) Direction(double bearingDeg)
    {
        var rad = bearingDeg * Math.PI / 180.0;
        return (Math.Sin(rad), Math.Cos(rad));
    }

    private static (double x, double y) Offset((double x, double y) from, double bearingDeg, double distanceKm)
    {
        var d = Direction(bearingDeg);
        return (from.x + d.x * distanceKm, from.y + d.y * distanceKm);
    }

    private static bool TryTwoLines(Observation a, Observation b, out (double x, double y) point, out string error)
    {
        point = default;
        var d1 = Direction(a.BearingDeg);
        var d2 = Direction(b.BearingDeg);

        var det = d2.x * d1.y - d1.x * d2.y;
        if (Math.Abs(det) < 1e-9)
        {
            error = "observation lines are parallel (bearings equal or opposite)";
            return false;
        }

        var dx = b.From.x - a.From.x;
        var dy = b.From.y - a.From.y;
        var t = (d2.x * dy - d2.y * dx) / det;
        var s = (d1.x * dy - d1.y * dx) / det;

        point = (a.From.x + t * d1.x, a.From.y + t * d1.y);

        // A negative parameter puts the crossing behind an observer, which is never a real fix.
        if (t < 0 || s < 0)
        {
            error = $"lines only cross BEHIND {(t < 0 ? "the first" : "the second")} observer, at {PointText(point)} km "
                  + "— a bearing is probably reversed (±180°) or an observer point is wrong; do not retry the same inputs";
            return false;
        }

        error = "";
        return true;
    }

    private static bool TryLineCircle(Observation line, RangeCircle circle,
        out List<(double x, double y)> candidates, out string error)
    {
        candidates = new List<(double x, double y)>();
        var d = Direction(line.BearingDeg);
        var fx = line.From.x - circle.Center.x;
        var fy = line.From.y - circle.Center.y;

        // Unit direction, so the quadratic coefficient a is exactly 1.
        var b = 2.0 * (fx * d.x + fy * d.y);
        var c = fx * fx + fy * fy - circle.RadiusKm * circle.RadiusKm;
        var disc = b * b - 4.0 * c;

        if (disc >= 0)
        {
            var root = Math.Sqrt(disc);
            // Near root first, and only ahead of the observer.
            foreach (var t in new[] { (-b - root) / 2.0, (-b + root) / 2.0 })
            {
                if (t >= 0) candidates.Add((line.From.x + d.x * t, line.From.y + d.y * t));
            }
        }

        if (candidates.Count == 0)
        {
            error = "observation line does not reach the range circle";
            return false;
        }

        error = "";
        return true;
    }

    private static bool TryCircleCircle(RangeCircle a, RangeCircle b,
        out List<(double x, double y)> candidates, out string error)
    {
        candidates = new List<(double x, double y)>();
        var dx = b.Center.x - a.Center.x;
        var dy = b.Center.y - a.Center.y;
        var d = Math.Sqrt(dx * dx + dy * dy);

        if (d < 1e-9 || d > a.RadiusKm + b.RadiusKm || d < Math.Abs(a.RadiusKm - b.RadiusKm))
        {
            error = "range circles do not intersect";
            return false;
        }

        var l = (a.RadiusKm * a.RadiusKm - b.RadiusKm * b.RadiusKm + d * d) / (2.0 * d);
        var hSquared = a.RadiusKm * a.RadiusKm - l * l;
        var h = hSquared > 0 ? Math.Sqrt(hSquared) : 0.0;

        var mx = a.Center.x + l * dx / d;
        var my = a.Center.y + l * dy / d;

        candidates.Add((mx + h * dy / d, my - h * dx / d));
        // Tangent circles produce a single solution.
        if (h > 1e-9) candidates.Add((mx - h * dy / d, my + h * dx / d));

        error = "";
        return true;
    }

    private static (double x, double y) PickNearest(List<(double x, double y)> candidates, (double x, double y)? near)
    {
        if (near == null || candidates.Count == 1) return candidates[0];

        var best = candidates[0];
        var bestDistance = double.MaxValue;
        foreach (var c in candidates)
        {
            var dx = c.x - near.Value.x;
            var dy = c.y - near.Value.y;
            var distance = dx * dx + dy * dy;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = c;
            }
        }
        return best;
    }

    // ---------------------------------------------------------------- JSON helpers

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // Diagnostics are Chinese; \uXXXX escaping would pollute the LLM context.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    private static string Error(string message)
        => JsonSerializer.Serialize(new { error = message }, JsonOptions);

    /// <summary>Standard result object: position only, three decimals.</summary>
    private static string Result((double x, double y) p, string? note = null)
        => JsonSerializer.Serialize(new
        {
            kmX = Math.Round(p.x, 3),
            kmY = Math.Round(p.y, 3),
            grid = GridOf(ToF(p)),
            note,
        }, JsonOptions);

    private static string PointText((double x, double y) p)
        => string.Format(CultureInfo.InvariantCulture, "({0:F2},{1:F2})", p.x, p.y);

    private static string StringOf(JsonElement obj, string name)
    {
        if (obj.ValueKind != JsonValueKind.Object) return "";
        if (!obj.TryGetProperty(name, out var el)) return "";
        if (el.ValueKind != JsonValueKind.String) return "";
        return el.GetString() ?? "";
    }

    private static bool TryNumber(JsonElement obj, string name, out double value)
    {
        value = 0;
        if (obj.ValueKind != JsonValueKind.Object) return false;
        if (!obj.TryGetProperty(name, out var el)) return false;
        if (el.ValueKind != JsonValueKind.Number) return false;
        return el.TryGetDouble(out value);
    }

    private static bool TryArray(JsonElement obj, string name, out JsonElement array)
    {
        array = default;
        if (obj.ValueKind != JsonValueKind.Object) return false;
        if (!obj.TryGetProperty(name, out var el)) return false;
        if (el.ValueKind != JsonValueKind.Array) return false;
        array = el;
        return true;
    }

    private static (double x, double y) ToD((float x, float y) p) => (p.x, p.y);

    private static (float x, float y) ToF((double x, double y) p) => ((float)p.x, (float)p.y);
}
