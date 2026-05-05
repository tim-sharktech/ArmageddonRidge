using System.Numerics;
using ArmageddonRidge.Core.Models;

namespace ArmageddonRidge.Core.Physics;

public sealed class ExplosionService
{
    public ExplosionResult Resolve(
        WeaponDefinition weapon,
        Vector2 center,
        Tank owner,
        Tank opponent,
        List<RadiationZone> zones)
    {
        var ownerDamage = weapon.CanDamageSelf ? ApplyDamage(owner, center, weapon) : 0;
        var opponentDamage = ApplyDamage(opponent, center, weapon);
        var newZones = new List<RadiationZone>();

        if (weapon.RadiationTurns > 0 && weapon.RadiationDamagePerTurn > 0)
        {
            var zone = new RadiationZone(center, weapon.BlastRadius * 0.7f, weapon.RadiationTurns, weapon.RadiationDamagePerTurn);
            zones.Add(zone);
            newZones.Add(zone);
        }

        return new ExplosionResult(
            center,
            weapon.BlastRadius,
            weapon.TerrainRadius,
            owner.IsCpu ? opponentDamage : ownerDamage,
            owner.IsCpu ? ownerDamage : opponentDamage,
            weapon.BehaviorType == WeaponBehaviorType.Dirt,
            weapon.Category == WeaponCategory.Nuclear,
            newZones);
    }

    public float ApplyRadiation(Tank tank, List<RadiationZone> zones)
    {
        var total = 0f;
        foreach (var zone in zones)
        {
            if (Vector2.DistanceSquared(tank.Center, zone.Center) <= zone.Radius * zone.Radius)
            {
                total += zone.DamagePerTurn;
                tank.Health -= (int)MathF.Ceiling(zone.DamagePerTurn);
            }
        }

        return total;
    }

    public void TickRadiation(List<RadiationZone> zones)
    {
        for (var i = zones.Count - 1; i >= 0; i--)
        {
            var zone = zones[i];
            zone = zone with { TurnsRemaining = zone.TurnsRemaining - 1 };
            if (zone.TurnsRemaining <= 0)
            {
                zones.RemoveAt(i);
            }
            else
            {
                zones[i] = zone;
            }
        }
    }

    private static float ApplyDamage(Tank tank, Vector2 center, WeaponDefinition weapon)
    {
        if (weapon.MaxDamage <= 0 || weapon.BlastRadius <= 0)
        {
            return 0;
        }

        var radiusSquared = weapon.BlastRadius * weapon.BlastRadius;
        var distanceSquared = Vector2.DistanceSquared(tank.Center, center);
        if (distanceSquared >= radiusSquared)
        {
            return 0;
        }

        var distance = MathF.Sqrt(distanceSquared);
        var normalized = Math.Clamp(1f - (distance / weapon.BlastRadius), 0f, 1f);
        var damage = weapon.MaxDamage * MathF.Pow(normalized, weapon.Falloff);
        if (damage <= 0.01f)
        {
            return 0;
        }

        var bypass = damage * weapon.ShieldBypassPercent;
        var blockable = damage - bypass;
        var absorbed = MathF.Min(tank.Shield, blockable);
        tank.Shield -= absorbed;
        var healthDamage = bypass + (blockable - absorbed);
        tank.Health -= (int)MathF.Ceiling(healthDamage);
        return damage;
    }
}
