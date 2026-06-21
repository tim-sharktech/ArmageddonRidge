using System.Numerics;
using ArmageddonRidge.Core.Models;
using ArmageddonRidge.Core.Terrain;

namespace ArmageddonRidge.Core.Physics;

/// <summary>
/// Deterministic fixed-step projectile simulator used by both live shots and CPU planning.
/// </summary>
public sealed class ProjectileSimulator
{
    /// <summary>
    /// Simulates a projectile and captures a renderer-friendly trail.
    /// </summary>
    public ProjectileSimulation Simulate(
        TerrainMask terrain,
        Tank owner,
        Tank opponent,
        WeaponDefinition weapon,
        float angleDegrees,
        int power,
        int wind,
        int maxSteps = 60 * 9,
        IReadOnlyList<CivilianStructure>? civilianStructures = null)
    {
        var result = SimulateCore(terrain, owner, opponent, weapon, angleDegrees, power, wind, maxSteps, captureTrail: true, civilianStructures: civilianStructures);
        return new ProjectileSimulation(
            result.Trail ?? [],
            result.ImpactPoint,
            result.StopReason,
            result.NearestOpponentDistance,
            result.NearestOwnerDistance);
    }

    /// <summary>
    /// Simulates a projectile without allocating a full trail for AI candidate scoring.
    /// </summary>
    public ProjectilePlanningSimulation SimulateForPlanning(
        TerrainMask terrain,
        Tank owner,
        Tank opponent,
        WeaponDefinition weapon,
        float angleDegrees,
        int power,
        int wind,
        int maxSteps = 60 * 9,
        IReadOnlyList<CivilianStructure>? civilianStructures = null)
    {
        var result = SimulateCore(terrain, owner, opponent, weapon, angleDegrees, power, wind, maxSteps, captureTrail: false, civilianStructures: civilianStructures);
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
        bool captureTrail,
        IReadOnlyList<CivilianStructure>? civilianStructures)
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
        var air = ProjectileAirProfile.For(weapon);
        var windAcceleration = wind * weapon.WindInfluence * air.WindCoupling * GameConstants.FixedDeltaTime;
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
                trail!.Add(new Vector2(px, py));

            var speedSquared = (vx * vx) + (vy * vy);
            var speedLength = MathF.Sqrt(speedSquared);
            var dragStep = air.Drag > 0 && speedLength > 0.001f
                ? Math.Clamp(air.Drag * speedLength * GameConstants.FixedDeltaTime, 0f, 0.18f)
                : 0f;
            var thrustX = air.Thrust > 0 && speedLength > 0.001f
                ? (vx / speedLength) * air.Thrust * GameConstants.FixedDeltaTime
                : 0f;
            var thrustY = air.Thrust > 0 && speedLength > 0.001f
                ? (vy / speedLength) * air.Thrust * GameConstants.FixedDeltaTime
                : 0f;
            var nextVx = (vx * (1f - dragStep)) + windAcceleration + thrustX;
            var nextVy = (vy * (1f - dragStep)) + gravityAcceleration + thrustY - (air.Lift * speedLength * GameConstants.FixedDeltaTime);
            if (float.IsFinite(air.TerminalVelocity) && air.TerminalVelocity > 0)
            {
                var nextSpeed = MathF.Sqrt((nextVx * nextVx) + (nextVy * nextVy));
                if (nextSpeed > air.TerminalVelocity)
                {
                    var scale = air.TerminalVelocity / nextSpeed;
                    nextVx *= scale;
                    nextVy *= scale;
                }
            }
            var nextX = px + (nextVx * GameConstants.FixedDeltaTime);
            var nextY = py + (nextVy * GameConstants.FixedDeltaTime);

            nearestOpponentSquared = MathF.Min(
                nearestOpponentSquared,
                SegmentDistanceSquared(px, py, nextX, nextY, opponentCenter.X, opponentCenter.Y));
            nearestOwnerSquared = MathF.Min(
                nearestOwnerSquared,
                SegmentDistanceSquared(px, py, nextX, nextY, ownerCenter.X, ownerCenter.Y));

            if (SweptHitsShield(px, py, nextX, nextY, opponent, terrain, GameConstants.ProjectileCollisionRadius, out var shieldHit))
                return Finish(trail, captureTrail, shieldHit.X, shieldHit.Y, ProjectileStopReason.ShieldHit, nearestOpponentSquared, nearestOwnerSquared);

            if (SweptHitsTank(px, py, nextX, nextY, opponentHitbox, GameConstants.ProjectileCollisionRadius, out var opponentHit))
                return Finish(trail, captureTrail, opponentHit.X, opponentHit.Y, ProjectileStopReason.TankHit, nearestOpponentSquared, nearestOwnerSquared);

            var ownerSegmentTouches = SweptHitsTank(px, py, nextX, nextY, ownerHitbox, GameConstants.ProjectileCollisionRadius, out var ownerHit);
            if (step > 8 && ownerProjectileHasClearedTank && ownerSegmentTouches)
                return Finish(trail, captureTrail, ownerHit.X, ownerHit.Y, ProjectileStopReason.OwnerHit, nearestOpponentSquared, nearestOwnerSquared);

            if (!ownerSegmentTouches) ownerProjectileHasClearedTank = true;

            if (SweptHitsStructure(px, py, nextX, nextY, civilianStructures, GameConstants.ProjectileCollisionRadius, out var structureHit))
                return Finish(trail, captureTrail, structureHit.X, structureHit.Y, ProjectileStopReason.CivilianStructureHit, nearestOpponentSquared, nearestOwnerSquared);

            if (terrain.IsSolid(px, py)) return Finish(trail, captureTrail, px, py, ProjectileStopReason.TerrainHit, nearestOpponentSquared, nearestOwnerSquared);

            if (px < -50 || px > terrain.Width + 50 || py > terrain.Height + 80) return Finish(trail, captureTrail, px, py, ProjectileStopReason.OutOfBounds, nearestOpponentSquared, nearestOwnerSquared);

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
        if (captureTrail) trail!.Add(impact);

        return new ProjectileSimulationCore(
            trail,
            impact,
            stopReason,
            MathF.Sqrt(nearestOpponentSquared),
            MathF.Sqrt(nearestOwnerSquared));
    }

    internal static bool SweptHitsTank(Vector2 start, Vector2 end, Tank tank, float padding, out Vector2 hit) =>
        SweptHitsTank(start.X, start.Y, end.X, end.Y, TankHitbox(tank), padding, out hit);

    internal static bool SweptHitsTankOrShield(Vector2 start, Vector2 end, Tank tank, TerrainMask terrain, float padding, out Vector2 hit, out ProjectileStopReason stopReason)
    {
        if (SweptHitsShield(start.X, start.Y, end.X, end.Y, tank, terrain, padding, out hit))
        {
            stopReason = ProjectileStopReason.ShieldHit;
            return true;
        }

        if (SweptHitsTank(start, end, tank, padding, out hit))
        {
            stopReason = ProjectileStopReason.TankHit;
            return true;
        }

        stopReason = ProjectileStopReason.Expired;
        return false;
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
        if (lengthSquared <= 0.0001f) return DistanceSquared(ax, ay, px, py);

        var t = (((px - ax) * dx) + ((py - ay) * dy)) / lengthSquared;
        t = Math.Clamp(t, 0f, 1f);
        var closestX = ax + (dx * t);
        var closestY = ay + (dy * t);
        return DistanceSquared(closestX, closestY, px, py);
    }

    private static bool SweptHitsTank(float ax, float ay, float bx, float by, Hitbox hitbox, float padding, out Vector2 hit)
    {
        var expanded = hitbox.Expand(padding);
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

    private static bool SweptHitsStructure(
        float ax,
        float ay,
        float bx,
        float by,
        IReadOnlyList<CivilianStructure>? structures,
        float padding,
        out Vector2 hit)
    {
        hit = default;
        if (structures is null || structures.Count == 0) return false;

        var bestDistance = float.MaxValue;
        var found = false;
        for (var i = 0; i < structures.Count; i++)
        {
            var structure = structures[i];
            if (structure.IsCollapsed) continue;

            var halfWidth = Math.Max(4, structure.Width * 0.5f);
            var hitbox = new Hitbox(
                structure.Position.X - halfWidth,
                structure.Position.X + halfWidth,
                structure.Position.Y - Math.Max(8, structure.Height),
                structure.Position.Y);
            if (!SweptHitsTank(ax, ay, bx, by, hitbox, padding, out var candidate)) continue;

            var distance = DistanceSquared(ax, ay, candidate.X, candidate.Y);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            hit = candidate;
            found = true;
        }

        return found;
    }

    private static bool SweptHitsShield(float ax, float ay, float bx, float by, Tank tank, TerrainMask terrain, float padding, out Vector2 hit)
    {
        if (tank.Shield <= 0)
        {
            hit = default;
            return false;
        }

        var centerX = tank.Position.X;
        var centerY = tank.Position.Y - GameConstants.ShieldCollisionCenterYOffset;
        var radiusX = GameConstants.ShieldCollisionRadiusX + padding;
        var radiusY = GameConstants.ShieldCollisionRadiusY + padding;
        var startX = (ax - centerX) / radiusX;
        var startY = (ay - centerY) / radiusY;
        var dx = (bx - ax) / radiusX;
        var dy = (by - ay) / radiusY;
        var a = (dx * dx) + (dy * dy);
        var b = 2f * ((startX * dx) + (startY * dy));
        var c = (startX * startX) + (startY * startY) - 1f;

        if (a <= 0.0001f)
        {
            hit = default;
            return false;
        }

        var discriminant = (b * b) - (4f * a * c);
        if (discriminant < 0)
        {
            hit = default;
            return false;
        }

        var root = MathF.Sqrt(discriminant);
        var inv = 1f / (2f * a);
        var t1 = (-b - root) * inv;
        var t2 = (-b + root) * inv;
        var t = float.MaxValue;
        if (t1 >= 0f && t1 <= 1f) t = t1;
        else if (t2 >= 0f && t2 <= 1f) t = t2;

        if (t == float.MaxValue)
        {
            hit = default;
            return false;
        }

        hit = new Vector2(ax + ((bx - ax) * t), ay + ((by - ay) * t));
        return hit.Y < terrain.GetSurfaceY(hit.X);
    }

    private static bool ClipAxis(float origin, float delta, float min, float max, ref float tMin, ref float tMax)
    {
        const float Epsilon = 0.0001f;
        if (MathF.Abs(delta) < Epsilon)
            return origin >= min && origin <= max;

        var inv = 1f / delta;
        var t1 = (min - origin) * inv;
        var t2 = (max - origin) * inv;
        if (t1 > t2)
            (t1, t2) = (t2, t1);

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

/// <summary>
/// Reason a projectile simulation stopped.
/// </summary>
public enum ProjectileStopReason
{
    TerrainHit,
    ShieldHit,
    TankHit,
    OwnerHit,
    CivilianStructureHit,
    OutOfBounds,
    Expired
}

/// <summary>
/// Full projectile simulation result including the visible sampled trail.
/// </summary>
/// <param name="Trail">Sampled world-space path points for rendering.</param>
/// <param name="ImpactPoint">Final point where the projectile stopped.</param>
/// <param name="StopReason">Reason the simulation stopped.</param>
/// <param name="NearestOpponentDistance">Nearest distance from the path to the opponent center.</param>
/// <param name="NearestOwnerDistance">Nearest distance from the path to the owner center.</param>
public sealed record ProjectileSimulation(
    IReadOnlyList<Vector2> Trail,
    Vector2 ImpactPoint,
    ProjectileStopReason StopReason,
    float NearestOpponentDistance,
    float NearestOwnerDistance);

/// <summary>
/// Allocation-light simulation result used by CPU planning.
/// </summary>
/// <param name="ImpactPoint">Final point where the projectile stopped.</param>
/// <param name="StopReason">Reason the simulation stopped.</param>
/// <param name="NearestOpponentDistance">Nearest distance from the path to the opponent center.</param>
/// <param name="NearestOwnerDistance">Nearest distance from the path to the owner center.</param>
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
