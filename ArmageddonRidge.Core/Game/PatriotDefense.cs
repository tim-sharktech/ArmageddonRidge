using System.Numerics;
using ArmageddonRidge.Core.Models;

namespace ArmageddonRidge.Core.Game;

/// <summary>
/// Heuristics for deciding when the single-use Patriot Battery should intercept a CPU shot.
/// </summary>
public static class PatriotDefense
{
    private const float ThreatPadding = GameConstants.TankWidth * 0.65f;
    private const float TrailEnvelope = 190f;
    private const float ReadableScreenPadding = 54f;
    private const float PreferredInterceptRange = 360f;

    /// <summary>
    /// Determines whether projected explosions alone threaten the protected tank.
    /// </summary>
    public static bool ShouldIntercept(Tank protectedTank, IReadOnlyList<ExplosionResult> projectedExplosions) =>
        ShouldIntercept(protectedTank, projectedExplosions, []);

    /// <summary>
    /// Determines whether projected explosions or the incoming trail threaten the protected tank.
    /// </summary>
    public static bool ShouldIntercept(Tank protectedTank, IReadOnlyList<ExplosionResult> projectedExplosions, IReadOnlyList<Vector2> trail)
    {
        for (var i = 0; i < projectedExplosions.Count; i++)
        {
            var explosion = projectedExplosions[i];
            var radius = explosion.DamageRadius + ThreatPadding;
            if (radius <= 0)
                continue;

            if (Vector2.DistanceSquared(protectedTank.Center, explosion.Center) <= radius * radius)
                return true;
        }

        var envelopeSquared = TrailEnvelope * TrailEnvelope;
        var step = Math.Max(1, trail.Count / 40);
        for (var i = 0; i < trail.Count; i += step)
        {
            if (Vector2.DistanceSquared(protectedTank.Center, trail[i]) <= envelopeSquared)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Chooses a readable defensive intercept point, preferring the apex when it is visible.
    /// </summary>
    public static Vector2 InterceptPoint(Tank protectedTank, IReadOnlyList<Vector2> trail)
    {
        if (trail.Count == 0)
            return protectedTank.Center + new Vector2(0, -70);

        var apexIndex = 0;
        var apex = trail[0];
        for (var i = 1; i < trail.Count; i++)
        {
            if (trail[i].Y < apex.Y)
            {
                apex = trail[i];
                apexIndex = i;
            }
        }

        if (IsReadableInterceptPoint(apex))
            return apex;

        var protectedCenter = protectedTank.Center;
        var preferredRangeSquared = PreferredInterceptRange * PreferredInterceptRange;
        var closestReadable = Vector2.Zero;
        var closestDistanceSquared = float.MaxValue;
        var hasReadablePoint = false;

        for (var i = apexIndex + 1; i < trail.Count; i++)
        {
            var point = trail[i];
            if (!IsReadableInterceptPoint(point))
                continue;

            var distanceSquared = Vector2.DistanceSquared(point, protectedCenter);
            if (distanceSquared <= preferredRangeSquared)
                return point;

            if (distanceSquared < closestDistanceSquared)
            {
                closestReadable = point;
                closestDistanceSquared = distanceSquared;
                hasReadablePoint = true;
            }
        }

        return hasReadablePoint ? closestReadable : ClampToReadableArea(apex);
    }

    private static bool IsReadableInterceptPoint(Vector2 point) =>
        point.X >= ReadableScreenPadding &&
        point.X <= GameConstants.WorldWidth - ReadableScreenPadding &&
        point.Y >= ReadableScreenPadding &&
        point.Y <= GameConstants.WorldHeight - ReadableScreenPadding;

    private static Vector2 ClampToReadableArea(Vector2 point) =>
        new(
            Math.Clamp(point.X, ReadableScreenPadding, GameConstants.WorldWidth - ReadableScreenPadding),
            Math.Clamp(point.Y, ReadableScreenPadding, GameConstants.WorldHeight - ReadableScreenPadding));
}
