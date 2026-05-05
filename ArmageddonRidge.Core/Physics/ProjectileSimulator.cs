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

        var opponentLeft = opponent.Position.X - (GameConstants.TankWidth / 2f);
        var opponentRight = opponent.Position.X + (GameConstants.TankWidth / 2f);
        var opponentTop = opponent.Position.Y - GameConstants.TankHeight;
        var opponentBottom = opponent.Position.Y;
        var ownerLeft = owner.Position.X - (GameConstants.TankWidth / 2f);
        var ownerRight = owner.Position.X + (GameConstants.TankWidth / 2f);
        var ownerTop = owner.Position.Y - GameConstants.TankHeight;
        var ownerBottom = owner.Position.Y;

        List<Vector2>? trail = captureTrail ? new List<Vector2>(maxSteps / 2) : null;
        var nearestOpponentSquared = float.MaxValue;
        var nearestOwnerSquared = float.MaxValue;

        for (var step = 0; step < maxSteps; step++)
        {
            if (captureTrail && step % 2 == 0)
            {
                trail!.Add(new Vector2(px, py));
            }

            nearestOpponentSquared = MathF.Min(nearestOpponentSquared, DistanceSquared(px, py, opponentCenter.X, opponentCenter.Y));
            nearestOwnerSquared = MathF.Min(nearestOwnerSquared, DistanceSquared(px, py, ownerCenter.X, ownerCenter.Y));

            if (step > 2 && HitsTank(px, py, opponentLeft, opponentRight, opponentTop, opponentBottom))
            {
                return Finish(trail, captureTrail, px, py, ProjectileStopReason.TankHit, nearestOpponentSquared, nearestOwnerSquared);
            }

            if (step > 8 && HitsTank(px, py, ownerLeft, ownerRight, ownerTop, ownerBottom))
            {
                return Finish(trail, captureTrail, px, py, ProjectileStopReason.OwnerHit, nearestOpponentSquared, nearestOwnerSquared);
            }

            if (terrain.IsSolid(px, py))
            {
                return Finish(trail, captureTrail, px, py, ProjectileStopReason.TerrainHit, nearestOpponentSquared, nearestOwnerSquared);
            }

            if (px < -50 || px > terrain.Width + 50 || py > terrain.Height + 80)
            {
                return Finish(trail, captureTrail, px, py, ProjectileStopReason.OutOfBounds, nearestOpponentSquared, nearestOwnerSquared);
            }

            vx += windAcceleration;
            vy += gravityAcceleration;
            px += vx * GameConstants.FixedDeltaTime;
            py += vy * GameConstants.FixedDeltaTime;
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

    private static float DistanceSquared(float ax, float ay, float bx, float by)
    {
        var dx = ax - bx;
        var dy = ay - by;
        return (dx * dx) + (dy * dy);
    }

    private static bool HitsTank(float x, float y, float left, float right, float top, float bottom) =>
        x >= left && x <= right && y >= top && y <= bottom;
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
