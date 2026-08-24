using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace IronNestAgentBridge.Agent;

/// <summary>
/// Exact triangulation for the LLM's tool calls. Works in the tactical map's km frame
/// (grid "A1 0:0" cell origin at km (0,0); FCS display offset 10.016/5.235 applies to
/// map-local coords, handled by the caller). Bearings: 0° = north (+Y), clockwise.
/// </summary>
public static class GridMath
{
    private static readonly Regex GridRe = new(@"^\s*([A-Za-z])\s*(\d{1,2})\s+(\d)\s*:\s*(\d)\s*$", RegexOptions.Compiled);

    /// <summary>"G6 5:3" → km center of the 0.1km sub-cell. Also accepts "kmX,kmY" and "turret" (via turretKm).</summary>
    public static (double x, double y)? ParsePoint(string from, (double x, double y) turretKm)
    {
        if (string.Equals(from.Trim(), "turret", StringComparison.OrdinalIgnoreCase))
            return turretKm;

        var grid = GridRe.Match(from);
        if (grid.Success)
        {
            var col = char.ToUpperInvariant(grid.Groups[1].Value[0]) - 'A';
            var row = int.Parse(grid.Groups[2].Value);
            var subCol = int.Parse(grid.Groups[3].Value);
            var subRow = int.Parse(grid.Groups[4].Value);
            return (col + subCol / 10.0 + 0.05, row - 1 + subRow / 10.0 + 0.05);
        }

        var parts = from.Split(',');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
            return (x, y);

        return null;
    }

    public static string GridToKm(JsonElement args, (double x, double y) turretKm)
    {
        var from = args.TryGetProperty("grid", out var g) ? g.GetString() ?? "" : "";
        var p = ParsePoint(from, turretKm);
        return p is { } pt
            ? Result(pt, turretKm)
            : Error($"cannot parse grid '{from}' (expected like 'G6 5:3')");
    }

    /// <summary>
    /// Solve a target from observation lines and/or range circles.
    /// Priority: any line carrying distanceKm is a direct fix; else two lines intersect;
    /// else line x circle; else circle x circle (needs 'near' to disambiguate).
    /// </summary>
    /// <summary>Plotting-work geometry from a solve, for drawing on the map with the player's tools.</summary>
    public class SolveGeometry
    {
        public List<((double x, double y) from, (double x, double y) to)> Lines { get; } = new();
        public List<((double x, double y) center, double radius)> Circles { get; } = new();
        public (double x, double y)? Solution { get; set; }
    }

    public static string SolveTarget(JsonElement args, (double x, double y) turretKm)
        => SolveTarget(args, turretKm, out _);

    public static string SolveTarget(JsonElement args, (double x, double y) turretKm, out SolveGeometry geometry)
    {
        geometry = new SolveGeometry();
        var directs = new List<(double x, double y)>();
        var lines = new List<((double x, double y) p, double bearing)>();
        var circles = new List<((double x, double y) c, double r)>();

        if (args.TryGetProperty("lines", out var linesEl) && linesEl.ValueKind == JsonValueKind.Array)
            foreach (var line in linesEl.EnumerateArray())
            {
                var from = line.TryGetProperty("from", out var f) ? f.GetString() ?? "" : "";
                if (ParsePoint(from, turretKm) is not { } p)
                    return Error($"cannot parse point '{from}'");
                if (!line.TryGetProperty("bearingDeg", out var b))
                    return Error("line missing bearingDeg");
                var bearing = b.GetDouble();
                if (line.TryGetProperty("distanceKm", out var d) && d.ValueKind == JsonValueKind.Number)
                {
                    var fix = Offset(p, bearing, d.GetDouble());
                    directs.Add(fix);
                    geometry.Lines.Add((p, fix));
                }
                else
                    lines.Add((p, bearing));
            }

        if (args.TryGetProperty("circles", out var circlesEl) && circlesEl.ValueKind == JsonValueKind.Array)
            foreach (var circle in circlesEl.EnumerateArray())
            {
                var from = circle.TryGetProperty("from", out var f) ? f.GetString() ?? "" : "";
                if (ParsePoint(from, turretKm) is not { } c)
                    return Error($"cannot parse point '{from}'");
                if (!circle.TryGetProperty("distanceKm", out var d))
                    return Error("circle missing distanceKm");
                circles.Add((c, d.GetDouble()));
                geometry.Circles.Add((c, d.GetDouble()));
            }

        (double x, double y)? near = null;
        if (args.TryGetProperty("near", out var nearEl) && nearEl.GetString() is { } nearStr)
            near = ParsePoint(nearStr, turretKm);

        (double x, double y) target;
        if (directs.Count > 0)
            target = directs[0];
        else if (lines.Count >= 2)
        {
            if (IntersectLines(lines[0], lines[1]) is not { } hit)
                return Error("observation lines are parallel or diverge (no forward intersection)");
            target = hit;
        }
        else if (lines.Count == 1 && circles.Count >= 1)
        {
            var hits = IntersectLineCircle(lines[0], circles[0]);
            if (hits.Count == 0)
                return Error("observation line does not reach the range circle");
            target = PickNearest(hits, near);
        }
        else if (circles.Count >= 2)
        {
            var hits = IntersectCircles(circles[0], circles[1]);
            if (hits.Count == 0)
                return Error("range circles do not intersect");
            if (hits.Count > 1 && near is null)
                return Error($"two circle intersections {Fmt(hits[0])} and {Fmt(hits[1])}; pass 'near' to choose");
            target = PickNearest(hits, near);
        }
        else
            return Error("need at least: 1 line with distanceKm, or 2 lines, or line+circle, or 2 circles");

        // Pure observation lines get drawn from the observer to the solved intersection.
        foreach (var (p, _) in lines)
            geometry.Lines.Add((p, target));
        geometry.Solution = target;

        return Result(target, turretKm);
    }

    private static (double x, double y) Offset((double x, double y) p, double bearingDeg, double distanceKm)
    {
        var rad = bearingDeg * Math.PI / 180.0;
        return (p.x + Math.Sin(rad) * distanceKm, p.y + Math.Cos(rad) * distanceKm);
    }

    private static (double x, double y)? IntersectLines(
        ((double x, double y) p, double bearing) a, ((double x, double y) p, double bearing) b)
    {
        var (ax, ay) = a.p;
        var (bx, by) = b.p;
        var (adx, ady) = Dir(a.bearing);
        var (bdx, bdy) = Dir(b.bearing);
        var det = adx * -bdy - ady * -bdx;
        if (Math.Abs(det) < 1e-9) return null;
        var rx = bx - ax;
        var ry = by - ay;
        var t = (rx * -bdy - ry * -bdx) / det;
        var s = (adx * ry - ady * rx) / -det;
        if (t < 0 || s < 0) return null; // both observers look forward along their bearing
        return (ax + adx * t, ay + ady * t);
    }

    private static List<(double x, double y)> IntersectLineCircle(
        ((double x, double y) p, double bearing) line, ((double x, double y) c, double r) circle)
    {
        var (dx, dy) = Dir(line.bearing);
        var fx = line.p.x - circle.c.x;
        var fy = line.p.y - circle.c.y;
        var b = 2 * (fx * dx + fy * dy);
        var c = fx * fx + fy * fy - circle.r * circle.r;
        var disc = b * b - 4 * c;
        var hits = new List<(double, double)>();
        if (disc < 0) return hits;
        var sq = Math.Sqrt(disc);
        foreach (var t in new[] { (-b - sq) / 2, (-b + sq) / 2 })
            if (t >= 0)
                hits.Add((line.p.x + dx * t, line.p.y + dy * t));
        return hits;
    }

    private static List<(double x, double y)> IntersectCircles(
        ((double x, double y) c, double r) a, ((double x, double y) c, double r) b)
    {
        var dx = b.c.x - a.c.x;
        var dy = b.c.y - a.c.y;
        var d = Math.Sqrt(dx * dx + dy * dy);
        var hits = new List<(double, double)>();
        if (d < 1e-9 || d > a.r + b.r || d < Math.Abs(a.r - b.r)) return hits;
        var l = (a.r * a.r - b.r * b.r + d * d) / (2 * d);
        var h2 = a.r * a.r - l * l;
        var h = h2 > 0 ? Math.Sqrt(h2) : 0;
        var mx = a.c.x + l * dx / d;
        var my = a.c.y + l * dy / d;
        hits.Add((mx + h * dy / d, my - h * dx / d));
        if (h > 1e-9)
            hits.Add((mx - h * dy / d, my + h * dx / d));
        return hits;
    }

    private static (double x, double y) PickNearest(List<(double x, double y)> hits, (double x, double y)? near)
    {
        if (near is not { } n || hits.Count == 1) return hits[0];
        return hits.OrderBy(h => (h.x - n.x) * (h.x - n.x) + (h.y - n.y) * (h.y - n.y)).First();
    }

    private static (double dx, double dy) Dir(double bearingDeg)
    {
        var rad = bearingDeg * Math.PI / 180.0;
        return (Math.Sin(rad), Math.Cos(rad));
    }

    private static string Result((double x, double y) target, (double x, double y) turretKm)
    {
        var dx = target.x - turretKm.x;
        var dy = target.y - turretKm.y;
        var dist = Math.Sqrt(dx * dx + dy * dy);
        var bearing = Math.Atan2(dx, dy) * 180.0 / Math.PI;
        if (bearing < 0) bearing += 360;
        return JsonSerializer.Serialize(new
        {
            kmX = Math.Round(target.x, 3),
            kmY = Math.Round(target.y, 3),
            bearingDeg = Math.Round(bearing, 2),
            distanceKm = Math.Round(dist, 3),
        });
    }

    private static string Fmt((double x, double y) p) => $"({p.x:F2},{p.y:F2})";
    private static string Error(string message) => JsonSerializer.Serialize(new { error = message });
}
