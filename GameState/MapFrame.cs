using UnityEngine;

namespace IronNestAgentBridge.GameState;

/// <summary>
/// The single source of truth for the tactical map's two coordinate frames and for the bearing
/// convention. Every reader, operator and consumer converts through here — the three calibration
/// constants used to be copy-pasted into five files, and a divergence between two copies is
/// indistinguishable from a gunnery error.
///
/// <para><b>map-local</b>: the local space of the <c>Draggable Surface</c> transform. x/y are the
/// plane, z is ignored. Markers, entities and impact instances all live here.</para>
///
/// <para><b>km frame</b>: the only frame that ever leaves this assembly — LLM context, HTTP
/// payloads, logs and events. Never apply the conversion twice; that is this project's classic
/// coordinate bug.</para>
///
/// Bearings are 0 degrees = map north (map-local +Y), increasing clockwise, normalised to
/// [0, 360). The unit vector is therefore (sin, cos), not the mathematical (cos, sin).
/// </summary>
public static class MapFrame
{
    /// <summary>map-local unit to kilometres.</summary>
    public const float MapLocalToKm = 3.8164f;

    /// <summary>km-frame origin offset on X.</summary>
    public const float MapOffsetX = 10.016f;

    /// <summary>km-frame origin offset on Y.</summary>
    public const float MapOffsetY = 5.235f;

    public static (float x, float y) LocalToKm(float localX, float localY)
        => (MapOffsetX + localX * MapLocalToKm, MapOffsetY + localY * MapLocalToKm);

    public static (float x, float y) LocalToKm(Vector3 local) => LocalToKm(local.x, local.y);

    /// <summary>Inverse of <see cref="LocalToKm(float,float)"/>; z is caller-supplied because
    /// several call sites must preserve the original depth of the object they are moving.</summary>
    public static Vector3 KmToLocal(float kmX, float kmY, float z = 0f)
        => new((kmX - MapOffsetX) / MapLocalToKm, (kmY - MapOffsetY) / MapLocalToKm, z);

    /// <summary>Wraps any angle into [0, 360).</summary>
    public static float NormalizeBearing(float deg) => (deg % 360f + 360f) % 360f;

    /// <summary>
    /// Bearing of a map-local delta. Equivalent to
    /// <c>Vector3.SignedAngle(delta, Vector3.up, Vector3.forward)</c> normalised to [0, 360):
    /// east reads 90, south 180, west 270.
    /// </summary>
    public static float BearingOf(Vector3 delta)
        => NormalizeBearing(Mathf.Atan2(delta.x, delta.y) * Mathf.Rad2Deg);

    /// <summary>Planar length of a map-local delta in kilometres; z is discarded.</summary>
    public static float DistanceKm(Vector3 delta)
        => new Vector2(delta.x, delta.y).magnitude * MapLocalToKm;

    /// <summary>Map-local point at <paramref name="distanceKm"/> from origin on a bearing.</summary>
    public static Vector3 FromBearing(Vector3 origin, float bearingDeg, float distanceKm)
    {
        var rad = bearingDeg * Mathf.Deg2Rad;
        var r = distanceKm / MapLocalToKm;
        return new Vector3(origin.x + Mathf.Sin(rad) * r, origin.y + Mathf.Cos(rad) * r, 0f);
    }
}
