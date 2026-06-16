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

    public static RenderPreviewTrail BuildPreviewPayload(
        IReadOnlyList<RenderPoint> path,
        IReadOnlyList<RenderPoint> cone)
    {
        var finitePath = BuildFiniteRenderPointPayload(path);
        var finiteCone = BuildFiniteRenderPointPayload(cone);
        return new RenderPreviewTrail(
            finitePath.Length >= 2 ? finitePath : [],
            finiteCone.Length >= 3 ? finiteCone : []);
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

    public static FinalShotDestructionPayload? SanitizeFinalShotDestruction(FinalShotDestructionPayload? destruction)
    {
        if (destruction is null || !destruction.Active || destruction.Pieces.Length == 0)
        {
            return null;
        }

        if (!IsFinite(destruction.X, destruction.Y)
            || !float.IsFinite(destruction.Radius)
            || destruction.Radius <= 0)
        {
            return null;
        }

        var pieces = new List<FinalShotDebrisPiece>(destruction.Pieces.Length);
        for (var i = 0; i < destruction.Pieces.Length; i++)
        {
            var piece = destruction.Pieces[i];
            if (!IsFinite(piece.X, piece.Y)
                || !IsFinite(piece.Vx, piece.Vy)
                || !AllFinite(piece.Size, piece.Mass, piece.Restitution, piece.Friction, piece.Drag, piece.Spin, piece.Lifetime, piece.R, piece.G, piece.B)
                || piece.Size <= 0
                || piece.Mass <= 0
                || piece.Lifetime <= 0)
            {
                continue;
            }

            pieces.Add(piece with
            {
                Sprite = string.IsNullOrWhiteSpace(piece.Sprite) ? "plate" : piece.Sprite,
                Size = Math.Clamp(piece.Size, 3, 48),
                Mass = Math.Clamp(piece.Mass, 0.1f, 12),
                Restitution = Math.Clamp(piece.Restitution, 0, 0.9f),
                Friction = Math.Clamp(piece.Friction, 0, 0.95f),
                Drag = Math.Clamp(piece.Drag, 0, 1.5f),
                Lifetime = Math.Clamp(piece.Lifetime, 0.25f, 10),
                R = Math.Clamp(piece.R, 0, 1),
                G = Math.Clamp(piece.G, 0, 1),
                B = Math.Clamp(piece.B, 0, 1)
            });
        }

        return pieces.Count == 0
            ? null
            : destruction with
            {
                Radius = Math.Clamp(destruction.Radius, 12, 420),
                Pieces = pieces.ToArray()
            };
    }

    public static ShotPlaybackOptionsPayload BuildPlaybackOptions(
        bool intercepted,
        Vector2? interceptPoint,
        string? ownerTankId,
        string? visualKind,
        FinalShotDestructionPayload? finalShotDestruction = null)
    {
        var hasValidIntercept = intercepted
            && interceptPoint is { } point
            && IsFinite(point.X, point.Y);

        return new ShotPlaybackOptionsPayload(
            hasValidIntercept,
            hasValidIntercept ? interceptPoint!.Value.X : null,
            hasValidIntercept ? interceptPoint!.Value.Y : null,
            ownerTankId,
            visualKind,
            finalShotDestruction);
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

    private static bool AllFinite(params float[] values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            if (!float.IsFinite(values[i])) return false;
        }

        return true;
    }

    private static RenderPoint[] BuildFiniteRenderPointPayload(IReadOnlyList<RenderPoint> points)
    {
        var payload = new List<RenderPoint>(points.Count);
        for (var i = 0; i < points.Count; i++)
        {
            var point = points[i];
            if (IsFinite(point.X, point.Y))
            {
                payload.Add(point);
            }
        }

        return payload.ToArray();
    }
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
    string? visualKind,
    FinalShotDestructionPayload? finalShotDestruction = null);
