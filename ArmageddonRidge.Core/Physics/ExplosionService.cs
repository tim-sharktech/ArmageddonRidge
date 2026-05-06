using System.Numerics;
using ArmageddonRidge.Core;
using ArmageddonRidge.Core.Models;

namespace ArmageddonRidge.Core.Physics;

/// <summary>
/// Applies blast falloff, shield absorption, and lingering hazard creation.
/// </summary>
public sealed class ExplosionService
{
    /// <summary>
    /// Resolves one explosion against the owner and opponent, mutating tank health and shields.
    /// </summary>
    public ExplosionResult Resolve(
        WeaponDefinition weapon,
        Vector2 center,
        Tank owner,
        Tank opponent,
        List<RadiationZone> zones,
        ShotVisualKind? visualKind = null)
    {
        var ownerDamage = weapon.CanDamageSelf ? ApplyDamage(owner, center, weapon) : 0;
        var opponentDamage = ApplyDamage(opponent, center, weapon);
        IReadOnlyList<RadiationZone> newZones = [];
        var resolvedVisualKind = visualKind ?? VisualKindFor(weapon);

        if (weapon.RadiationTurns > 0 && weapon.RadiationDamagePerTurn > 0)
        {
            var zoneKind = resolvedVisualKind == ShotVisualKind.Fire ? ShotVisualKind.Lava : resolvedVisualKind;
            var zone = new RadiationZone(center, weapon.BlastRadius * 0.7f, weapon.RadiationTurns, weapon.RadiationDamagePerTurn, zoneKind);
            zones.Add(zone);
            newZones = [zone];
        }

        return new ExplosionResult(
            center,
            weapon.BlastRadius,
            weapon.TerrainRadius,
            owner.IsCpu ? opponentDamage : ownerDamage,
            owner.IsCpu ? ownerDamage : opponentDamage,
            weapon.BehaviorType == WeaponBehaviorType.Dirt,
            weapon.Category == WeaponCategory.Nuclear,
            newZones,
            resolvedVisualKind);
    }

    /// <summary>
    /// Applies active radiation or lava zones to a tank and returns total raw damage.
    /// </summary>
    public float ApplyRadiation(Tank tank, List<RadiationZone> zones)
    {
        var total = 0f;
        for (var i = 0; i < zones.Count; i++)
        {
            var zone = zones[i];
            if (Vector2.DistanceSquared(tank.Center, zone.Center) <= zone.Radius * zone.Radius)
            {
                total += zone.DamagePerTurn;
                tank.Health -= (int)MathF.Ceiling(zone.DamagePerTurn);
            }
        }

        return total;
    }

    /// <summary>
    /// Advances lingering zones by one turn and removes expired zones.
    /// </summary>
    public void TickRadiation(List<RadiationZone> zones)
    {
        for (var i = zones.Count - 1; i >= 0; i--)
        {
            var zone = zones[i];
            zone = zone with { TurnsRemaining = zone.TurnsRemaining - 1 };
            if (zone.TurnsRemaining <= 0) zones.RemoveAt(i);
            else zones[i] = zone;
        }
    }

    private static float ApplyDamage(Tank tank, Vector2 center, WeaponDefinition weapon)
    {
        if (weapon.MaxDamage <= 0 || weapon.BlastRadius <= 0) return 0;

        var distance = tank.Shield > 0
            ? DistanceToShieldEnvelope(tank, center)
            : DistanceToHullEnvelope(tank, center);
        var distanceSquared = distance * distance;
        var radiusSquared = weapon.BlastRadius * weapon.BlastRadius;
        if (distanceSquared >= radiusSquared) return 0;

        var normalized = Math.Clamp(1f - (distance / weapon.BlastRadius), 0f, 1f);
        var damage = weapon.MaxDamage * MathF.Pow(normalized, weapon.Falloff);
        if (damage <= 0.01f) return 0;

        var bypass = damage * weapon.ShieldBypassPercent;
        var blockable = damage - bypass;
        var shieldBleedThrough = tank.Shield > 0
            ? blockable * GameConstants.ShieldHealthBleedThroughPercent
            : 0;
        var absorbable = blockable - shieldBleedThrough;
        var absorbed = MathF.Min(tank.Shield, absorbable);
        tank.Shield -= absorbed;
        var healthDamage = bypass + shieldBleedThrough + (absorbable - absorbed);
        tank.Health -= (int)MathF.Ceiling(healthDamage);
        return damage;
    }

    private static float DistanceToShieldEnvelope(Tank tank, Vector2 point)
    {
        var center = new Vector2(tank.Position.X, tank.Position.Y - GameConstants.ShieldCollisionCenterYOffset);
        var dx = point.X - center.X;
        var dy = point.Y - center.Y;
        var normalized = MathF.Sqrt(
            (dx * dx) / (GameConstants.ShieldCollisionRadiusX * GameConstants.ShieldCollisionRadiusX)
            + (dy * dy) / (GameConstants.ShieldCollisionRadiusY * GameConstants.ShieldCollisionRadiusY));
        if (normalized <= 1f) return 0;

        var directionLength = MathF.Sqrt((dx * dx) + (dy * dy));
        if (directionLength <= 0.001f) return 0;

        var boundaryScale = 1f / normalized;
        var boundary = new Vector2(center.X + (dx * boundaryScale), center.Y + (dy * boundaryScale));
        return Vector2.Distance(point, boundary);
    }

    private static float DistanceToHullEnvelope(Tank tank, Vector2 point)
    {
        var halfWidth = GameConstants.TankCollisionWidth / 2f;
        var closestX = Math.Clamp(point.X, tank.Position.X - halfWidth, tank.Position.X + halfWidth);
        var closestY = Math.Clamp(point.Y, tank.Position.Y - GameConstants.TankCollisionHeight, tank.Position.Y);
        return Vector2.Distance(point, new Vector2(closestX, closestY));
    }

    private static ShotVisualKind VisualKindFor(WeaponDefinition weapon) => weapon.BehaviorType switch
    {
        WeaponBehaviorType.Napalm => ShotVisualKind.Fire,
        WeaponBehaviorType.Nuclear => ShotVisualKind.Nuclear,
        WeaponBehaviorType.Missile => ShotVisualKind.Missile,
        WeaponBehaviorType.DroneSwarm => ShotVisualKind.DroneSwarm,
        WeaponBehaviorType.MultiStagePenetrator => ShotVisualKind.PenetratorSecondary,
        WeaponBehaviorType.Laser => ShotVisualKind.Laser,
        WeaponBehaviorType.Teleport => ShotVisualKind.Teleport,
        WeaponBehaviorType.Dirt => ShotVisualKind.Dirt,
        WeaponBehaviorType.Excavator => ShotVisualKind.Dirt,
        _ => ShotVisualKind.Ballistic
    };
}
