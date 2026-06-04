using System.Numerics;
using ArmageddonRidge.Core.Models;

namespace ArmageddonRidge.Client.Services.Rendering;

internal static class RenderPayloadSanitizer
{
    public static ShotPointPayload[] BuildTrailPayload(IReadOnlyList<Vector2> trail)
    {
        var payload = new List<ShotPointPayload>(trail.Count);
        for (var i = 0; i < trail.Count; i++)
        {
            var point = trail[i];
            if (IsFinite(point.X, point.Y))
            {
                payload.Add(new ShotPointPayload(point.X, point.Y));
            }
        }

        return payload.ToArray();
    }

    public static RenderPoint[] BuildRenderTrailPayload(IReadOnlyList<Vector2> trail, int maxPoints)
    {
        if (maxPoints < 2 || trail.Count < 2) return [];

        var finite = new List<Vector2>(trail.Count);
        for (var i = 0; i < trail.Count; i++)
        {
            var point = trail[i];
            if (IsFinite(point.X, point.Y))
            {
                finite.Add(point);
            }
        }

        if (finite.Count < 2) return [];
        if (finite.Count <= maxPoints)
        {
            var points = new RenderPoint[finite.Count];
            for (var i = 0; i < finite.Count; i++) points[i] = RenderPoint.FromVector(finite[i]);
            return points;
        }

        var result = new RenderPoint[maxPoints];
        var stride = (finite.Count - 1) / (float)(maxPoints - 1);
        for (var i = 0; i < maxPoints; i++)
        {
            var point = finite[Math.Min(finite.Count - 1, (int)MathF.Round(i * stride))];
            result[i] = RenderPoint.FromVector(point);
        }

        return result;
    }

    public static EffectPointPayload[] BuildEffectTrailPayload(IReadOnlyList<Vector2> trail, int maxPoints)
    {
        if (maxPoints < 1 || trail.Count == 0) return [];

        var finite = new List<Vector2>(trail.Count);
        for (var i = 0; i < trail.Count; i++)
        {
            var point = trail[i];
            if (IsFinite(point.X, point.Y))
            {
                finite.Add(point);
            }
        }

        if (finite.Count == 0) return [];
        var count = Math.Min(finite.Count, maxPoints);
        var result = new EffectPointPayload[count];
        var stride = finite.Count <= count ? 1 : (finite.Count - 1) / (float)Math.Max(1, count - 1);
        for (var i = 0; i < count; i++)
        {
            var point = finite[Math.Min(finite.Count - 1, (int)MathF.Round(i * stride))];
            result[i] = new EffectPointPayload(point.X, point.Y);
        }

        return result;
    }

    public static ShotExplosionPayload[] BuildExplosionPayload(
        IReadOnlyList<ExplosionResult> explosions,
        string? weaponId)
    {
        var payload = new List<ShotExplosionPayload>(explosions.Count);
        for (var i = 0; i < explosions.Count; i++)
        {
            var explosion = explosions[i];
            if (!IsFinite(explosion.Center.X, explosion.Center.Y)) continue;

            var radius = PositiveOrDefault(explosion.DamageRadius, 32);
            if (radius <= 0) continue;

            payload.Add(new ShotExplosionPayload(
                explosion.Center.X,
                explosion.Center.Y,
                radius,
                NonNegativeOrDefault(explosion.TerrainRadius, 0),
                explosion.Nuclear,
                explosion.DirtAdded,
                weaponId,
                explosion.VisualKind.ToString(),
                explosion.VisualKind == ShotVisualKind.Fire,
                explosion.VisualKind == ShotVisualKind.Lava,
                explosion.VisualKind == ShotVisualKind.Missile,
                explosion.VisualKind == ShotVisualKind.DroneSwarm,
                explosion.VisualKind == ShotVisualKind.PatriotIntercept,
                explosion.VisualKind == ShotVisualKind.ShieldHit,
                explosion.TriggerTrailIndex));
        }

        return payload.ToArray();
    }

    public static EffectExplosionPayload[] BuildEffectExplosionPayload(IReadOnlyList<ExplosionResult> explosions)
    {
        var payload = new List<EffectExplosionPayload>(explosions.Count);
        for (var i = 0; i < explosions.Count; i++)
        {
            var explosion = explosions[i];
            if (!IsFinite(explosion.Center.X, explosion.Center.Y)) continue;

            var radius = PositiveOrDefault(explosion.DamageRadius, 32);
            if (radius <= 0) continue;

            payload.Add(new EffectExplosionPayload(
                explosion.Center.X,
                explosion.Center.Y,
                radius,
                NonNegativeOrDefault(explosion.TerrainRadius, 0),
                explosion.Nuclear,
                explosion.DirtAdded,
                explosion.VisualKind.ToString(),
                FiniteOrDefault(explosion.PlayerDamage, 0),
                FiniteOrDefault(explosion.CpuDamage, 0),
                explosion.TriggerTrailIndex));
        }

        return payload.ToArray();
    }

    public static RenderRadiationZone[] BuildRadiationPayload(IReadOnlyList<RadiationZone> zones)
    {
        var payload = new List<RenderRadiationZone>(zones.Count);
        for (var i = 0; i < zones.Count; i++)
        {
            var zone = zones[i];
            if (!IsFinite(zone.Center.X, zone.Center.Y)
                || !float.IsFinite(zone.Radius)
                || zone.Radius <= 0
                || zone.TurnsRemaining <= 0)
            {
                continue;
            }

            payload.Add(new RenderRadiationZone(
                zone.Center.X,
                zone.Center.Y,
                zone.Radius,
                zone.TurnsRemaining,
                zone.VisualKind.ToString(),
                zone.VisualKind == ShotVisualKind.Lava || zone.VisualKind == ShotVisualKind.Fire));
        }

        return payload.ToArray();
    }

    public static ShotPlaybackOptionsPayload BuildPlaybackOptions(
        bool intercepted,
        Vector2? interceptPoint,
        string? ownerTankId,
        string? visualKind)
    {
        var hasValidIntercept = intercepted
            && interceptPoint is { } point
            && IsFinite(point.X, point.Y);

        return new ShotPlaybackOptionsPayload(
            hasValidIntercept,
            hasValidIntercept ? interceptPoint!.Value.X : null,
            hasValidIntercept ? interceptPoint!.Value.Y : null,
            ownerTankId,
            visualKind);
    }

    public static bool TryGetFinitePoint(Vector2? point, out float x, out float y)
    {
        if (point is { } value && IsFinite(value.X, value.Y))
        {
            x = value.X;
            y = value.Y;
            return true;
        }

        x = 0;
        y = 0;
        return false;
    }

    private static float FiniteOrDefault(float value, float fallback) =>
        float.IsFinite(value) ? value : fallback;

    private static float PositiveOrDefault(float value, float fallback) =>
        float.IsFinite(value) && value > 0 ? value : fallback;

    private static float NonNegativeOrDefault(float value, float fallback) =>
        float.IsFinite(value) && value >= 0 ? value : fallback;

    private static bool IsFinite(float x, float y) => float.IsFinite(x) && float.IsFinite(y);
}

internal sealed record ShotPointPayload(float x, float y);

internal sealed record EffectPointPayload(float X, float Y);

internal sealed record ShotExplosionPayload(
    float x,
    float y,
    float radius,
    float terrainRadius,
    bool nuclear,
    bool dirt,
    string? weaponId,
    string visualKind,
    bool napalm,
    bool lava,
    bool missile,
    bool drone,
    bool patriotIntercept,
    bool shieldHit,
    int triggerIndex);

internal sealed record EffectExplosionPayload(
    float X,
    float Y,
    float Radius,
    float TerrainRadius,
    bool Nuclear,
    bool Dirt,
    string VisualKind,
    float PlayerDamage,
    float CpuDamage,
    int TriggerIndex);

internal sealed record ShotPlaybackOptionsPayload(
    bool intercepted,
    float? interceptX,
    float? interceptY,
    string? ownerTankId,
    string? visualKind);
