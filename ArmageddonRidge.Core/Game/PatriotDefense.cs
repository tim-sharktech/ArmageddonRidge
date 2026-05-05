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
    /// Chooses the projectile apex for the defensive intercept effect.
    /// </summary>
    public static Vector2 InterceptPoint(Tank protectedTank, IReadOnlyList<Vector2> trail)
    {
        if (trail.Count == 0)
            return protectedTank.Center + new Vector2(0, -70);

        var best = trail[0];
        for (var i = 1; i < trail.Count; i++)
        {
            if (trail[i].Y < best.Y)
            {
                best = trail[i];
            }
        }

        return best;
    }
}
