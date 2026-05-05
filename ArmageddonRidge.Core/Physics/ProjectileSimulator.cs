using System.Numerics;
using ArmageddonRidge.Core.Models;
using ArmageddonRidge.Core.Terrain;

namespace ArmageddonRidge.Core.Physics;

public sealed class ProjectileSimulator
{
    public ProjectileSimulation Simulate(
        TerrainMask terrain,
        Tank owner,
        Tank opponent,
        WeaponDefinition weapon,
        float angleDegrees,
        int power,
        int wind,
        int maxSteps = 60 * 9)
    {
        var result = SimulateCore(terrain, owner, opponent, weapon, angleDegrees, power, wind, maxSteps, captureTrail: true);
        return new ProjectileSimulation(
            result.Trail ?? [],
            result.ImpactPoint,
            result.StopReason,
            result.NearestOpponentDistance,
            result.NearestOwnerDistance);
    }

    public ProjectilePlanningSimulation SimulateForPlanning(
        TerrainMask terrain,
        Tank owner,
        Tank opponent,
        WeaponDefinition weapon,
        float angleDegrees,
        int power,
        int wind,
        int maxSteps = 60 * 9)
    {
        var result = SimulateCore(terrain, owner, opponent, weapon, angleDegrees, power, wind, maxSteps, captureTrail: false);
        return new ProjectilePlanningSimulation(result.ImpactPoint, result.StopReason, result.NearestOpponentDistance, result.NearestOwnerDistance);
    }

    private static ProjectileSimulationCore SimulateCore(
        TerrainMask terrain,
        Tank owner,
        Tank opponent,
        WeaponDefinition weapon,
        float angleDegrees,
        int power,
        int wind,
        int maxSteps,
        bool captureTrail)
    {
        var angleRadians = angleDegrees * MathF.PI / 180f;
        var cos = MathF.Cos(angleRadians);
        var sin = MathF.Sin(angleRadians);
        var ownerCenter = owner.Center;
        var opponentCenter = opponent.Center;
        var px = ownerCenter.X + (cos * 23f);
        var py = ownerCenter.Y - (sin * 23f);
        var speed = Math.Clamp(power, GameConstants.PowerMin, GameConstants.PowerMax) * 4.15f * weapon.ProjectileSpeedMultiplier;
        var vx = cos * speed;
        var vy = -sin * speed;
        var windAcceleration = wind * weapon.WindInfluence * GameConstants.FixedDeltaTime;
        var gravityAcceleration = GameConstants.Gravity * weapon.GravityInfluence * GameConstants.FixedDeltaTime;

        var opponentHitbox = TankHitbox(opponent);
        var ownerHitbox = TankHitbox(owner);

        List<Vector2>? trail = captureTrail ? new List<Vector2>(maxSteps / 2) : null;
        var nearestOpponentSquared = float.MaxValue;
        var nearestOwnerSquared = float.MaxValue;
        var ownerProjectileHasClearedTank = false;

        for (var step = 0; step < maxSteps; step++)
        {
            if (captureTrail && step % 2 == 0)
            {
                trail!.Add(new Vector2(px, py));
            }

            var nextVx = vx + windAcceleration;
            var nextVy = vy + gravityAcceleration;
            var nextX = px + (nextVx * GameConstants.FixedDeltaTime);
            var nextY = py + (nextVy * GameConstants.FixedDeltaTime);

            nearestOpponentSquared = MathF.Min(
                nearestOpponentSquared,
                SegmentDistanceSquared(px, py, nextX, nextY, opponentCenter.X, opponentCenter.Y));
            nearestOwnerSquared = MathF.Min(
                nearestOwnerSquared,
                SegmentDistanceSquared(px, py, nextX, nextY, ownerCenter.X, ownerCenter.Y));

            if (step > 2 && SweptHitsTank(px, py, nextX, nextY, opponentHitbox, out var opponentHit))
            {
                return Finish(trail, captureTrail, opponentHit.X, opponentHit.Y, ProjectileStopReason.TankHit, nearestOpponentSquared, nearestOwnerSquared);
            }

            var ownerSegmentTouches = SweptHitsTank(px, py, nextX, nextY, ownerHitbox, out var ownerHit);
            if (step > 8 && ownerProjectileHasClearedTank && ownerSegmentTouches)
            {
                return Finish(trail, captureTrail, ownerHit.X, ownerHit.Y, ProjectileStopReason.OwnerHit, nearestOpponentSquared, nearestOwnerSquared);
            }

            if (!ownerSegmentTouches)
            {
                ownerProjectileHasClearedTank = true;
            }

            if (terrain.IsSolid(px, py))
            {
                return Finish(trail, captureTrail, px, py, ProjectileStopReason.TerrainHit, nearestOpponentSquared, nearestOwnerSquared);
            }

            if (px < -50 || px > terrain.Width + 50 || py > terrain.Height + 80)
            {
                return Finish(trail, captureTrail, px, py, ProjectileStopReason.OutOfBounds, nearestOpponentSquared, nearestOwnerSquared);
            }

            vx = nextVx;
            vy = nextVy;
            px = nextX;
            py = nextY;
        }

        return Finish(trail, captureTrail, px, py, ProjectileStopReason.Expired, nearestOpponentSquared, nearestOwnerSquared);
    }

    private static ProjectileSimulationCore Finish(
        List<Vector2>? trail,
        bool captureTrail,
        float x,
        float y,
        ProjectileStopReason stopReason,
        float nearestOpponentSquared,
        float nearestOwnerSquared)
    {
        var impact = new Vector2(x, y);
        if (captureTrail)
        {
            trail!.Add(impact);
        }

        return new ProjectileSimulationCore(
            trail,
            impact,
            stopReason,
            MathF.Sqrt(nearestOpponentSquared),
            MathF.Sqrt(nearestOwnerSquared));
    }

    private static Hitbox TankHitbox(Tank tank)
    {
        var halfWidth = GameConstants.TankCollisionWidth / 2f;
        return new Hitbox(
            tank.Position.X - halfWidth,
            tank.Position.X + halfWidth,
            tank.Position.Y - GameConstants.TankCollisionHeight,
            tank.Position.Y);
    }

    private static float SegmentDistanceSquared(float ax, float ay, float bx, float by, float px, float py)
    {
        var dx = bx - ax;
        var dy = by - ay;
        var lengthSquared = (dx * dx) + (dy * dy);
        if (lengthSquared <= 0.0001f)
        {
            return DistanceSquared(ax, ay, px, py);
        }

        var t = (((px - ax) * dx) + ((py - ay) * dy)) / lengthSquared;
        t = Math.Clamp(t, 0f, 1f);
        var closestX = ax + (dx * t);
        var closestY = ay + (dy * t);
        return DistanceSquared(closestX, closestY, px, py);
    }

    private static bool SweptHitsTank(float ax, float ay, float bx, float by, Hitbox hitbox, out Vector2 hit)
    {
        var expanded = hitbox.Expand(GameConstants.ProjectileCollisionRadius);
        if (HitsTank(ax, ay, expanded))
        {
            hit = new Vector2(ax, ay);
            return true;
        }

        var dx = bx - ax;
        var dy = by - ay;
        var tMin = 0f;
        var tMax = 1f;
        if (!ClipAxis(ax, dx, expanded.Left, expanded.Right, ref tMin, ref tMax)
            || !ClipAxis(ay, dy, expanded.Top, expanded.Bottom, ref tMin, ref tMax))
        {
            hit = default;
            return false;
        }

        hit = new Vector2(ax + (dx * tMin), ay + (dy * tMin));
        return true;
    }

    private static bool ClipAxis(float origin, float delta, float min, float max, ref float tMin, ref float tMax)
    {
        const float Epsilon = 0.0001f;
        if (MathF.Abs(delta) < Epsilon)
        {
            return origin >= min && origin <= max;
        }

        var inv = 1f / delta;
        var t1 = (min - origin) * inv;
        var t2 = (max - origin) * inv;
        if (t1 > t2)
        {
            (t1, t2) = (t2, t1);
        }

        tMin = MathF.Max(tMin, t1);
        tMax = MathF.Min(tMax, t2);
        return tMin <= tMax;
    }

    private static float DistanceSquared(float ax, float ay, float bx, float by)
    {
        var dx = ax - bx;
        var dy = ay - by;
        return (dx * dx) + (dy * dy);
    }

    private static bool HitsTank(float x, float y, Hitbox hitbox) =>
        x >= hitbox.Left && x <= hitbox.Right && y >= hitbox.Top && y <= hitbox.Bottom;
}

internal readonly record struct Hitbox(float Left, float Right, float Top, float Bottom)
{
    public Hitbox Expand(float amount) => new(Left - amount, Right + amount, Top - amount, Bottom + amount);
}

public enum ProjectileStopReason
{
    TerrainHit,
    TankHit,
    OwnerHit,
    OutOfBounds,
    Expired
}

public sealed record ProjectileSimulation(
    IReadOnlyList<Vector2> Trail,
    Vector2 ImpactPoint,
    ProjectileStopReason StopReason,
    float NearestOpponentDistance,
    float NearestOwnerDistance);

public readonly record struct ProjectilePlanningSimulation(
    Vector2 ImpactPoint,
    ProjectileStopReason StopReason,
    float NearestOpponentDistance,
    float NearestOwnerDistance);

internal readonly record struct ProjectileSimulationCore(
    List<Vector2>? Trail,
    Vector2 ImpactPoint,
    ProjectileStopReason StopReason,
    float NearestOpponentDistance,
    float NearestOwnerDistance);
