using System.Numerics;
using ArmageddonRidge.Core.Models;
using ArmageddonRidge.Core.Terrain;

namespace ArmageddonRidge.Core.Physics;

public sealed record TerrainSlopeSample(float X, float SurfaceY, float Slope, Vector2 Normal, TerrainMaterialKind Material);

public enum TerrainMaterialKind
{
    Dirt,
    Rock,
    Lava,
    Scorched,
    Metal,
    Shield
}

public enum ImpactMaterialKind
{
    Dirt,
    Rock,
    Lava,
    Fire,
    Metal,
    Shield,
    Energy
}

public sealed record TankVisualPose(
    string TankId,
    float X,
    float Y,
    float HullAngleDegrees,
    float VerticalOffset,
    float LeftTreadY,
    float RightTreadY,
    float SuspensionCompression,
    float RecoilX,
    float RecoilY,
    float RockAngleDegrees,
    float ShadowSquash);

public sealed record ShockwaveImpulsePayload(
    float X,
    float Y,
    float Radius,
    float Intensity,
    float DirectionX,
    float DirectionY,
    float TerrainDampening,
    string VisualKind);

public sealed record TerrainSlumpColumnPayload(
    int X,
    float FromY,
    float ToY,
    float DelayMs,
    float DurationMs);

public sealed record TerrainSlumpPayload(
    TerrainSlumpColumnPayload[] Columns,
    float DurationMs,
    bool ReducedMotion);

public sealed record DebrisSettlingPayload(
    float X,
    float Y,
    float VelocityX,
    float VelocityY,
    float Friction,
    float BounceDamping,
    string Material);

public sealed record ImpactParticlePayload(
    float X,
    float Y,
    float DirectionX,
    float DirectionY,
    float Intensity,
    string Material,
    string VisualKind,
    bool ShieldLike);

public sealed record LingeringEffectPayload(
    float X,
    float Y,
    float WindX,
    float SlopeX,
    float SlopeY,
    float Lifetime,
    float Intensity,
    string VisualKind);

public readonly record struct ProjectileAirProfile(
    float Drag,
    float WindCoupling,
    float Thrust,
    float Lift,
    float TerminalVelocity)
{
    public static ProjectileAirProfile For(WeaponDefinition weapon)
    {
        if (weapon.Id == WeaponIds.SplitterMirv)
            return new ProjectileAirProfile(0.00045f, 0.64f, 0f, 0f, 820f);

        if (weapon.Id == WeaponIds.Gbu57Mop)
            return new ProjectileAirProfile(0.00035f, 0.12f, 0f, 0f, 900f);

        var baseProfile = weapon.BehaviorType switch
        {
            WeaponBehaviorType.Missile => new ProjectileAirProfile(0.018f, 0.34f, 15f, 0.02f, 720f),
            WeaponBehaviorType.DroneSwarm => new ProjectileAirProfile(0.038f, 1.35f, 4f, 0.08f, 420f),
            WeaponBehaviorType.Napalm => new ProjectileAirProfile(0.03f, 1.18f, 0f, 0.01f, 460f),
            WeaponBehaviorType.BunkerBuster or WeaponBehaviorType.MultiStagePenetrator => new ProjectileAirProfile(0.008f, 0.18f, 0f, 0f, 760f),
            WeaponBehaviorType.Dirt or WeaponBehaviorType.Excavator or WeaponBehaviorType.Nuclear => ProjectileBallisticEquivalent,
            _ => new ProjectileAirProfile(0.012f, 0.82f, 0f, 0f, 620f)
        };

        if (weapon.Id == WeaponIds.HeavyShell)
            return baseProfile with { Drag = MathF.Min(baseProfile.Drag, 0.008f), WindCoupling = MathF.Min(baseProfile.WindCoupling, 0.2f), TerminalVelocity = 780f };

        if (weapon.Id == WeaponIds.PeaShell)
            return ProjectileBallisticEquivalent;

        return baseProfile;
    }

    public static ProjectileAirProfile ProjectileBallisticEquivalent => new(0f, 1f, 0f, 0f, float.PositiveInfinity);
}

public sealed class TerrainSamplingService
{
    public TerrainSlopeSample Sample(TerrainMask terrain, float x, int halfWindow = 10)
    {
        var clampedX = Math.Clamp(x, 0, terrain.Width - 1);
        var leftX = Math.Clamp((int)MathF.Round(clampedX - halfWindow), 0, terrain.Width - 1);
        var rightX = Math.Clamp((int)MathF.Round(clampedX + halfWindow), 0, terrain.Width - 1);
        var left = terrain.SolidTop[leftX];
        var right = terrain.SolidTop[rightX];
        var dx = Math.Max(1, rightX - leftX);
        var slope = (right - left) / dx;
        var normal = NormalizeOrFallback(new Vector2(-slope, -1f), -Vector2.UnitY);
        return new TerrainSlopeSample(
            clampedX,
            terrain.GetSurfaceY(clampedX),
            ClampFinite(slope, -3f, 3f),
            normal,
            MaterialFor(terrain, clampedX));
    }

    public TerrainSlopeSample[] SampleBatch(TerrainMask terrain, IReadOnlyList<float> xs, int halfWindow = 10)
    {
        var samples = new TerrainSlopeSample[xs.Count];
        for (var i = 0; i < xs.Count; i++)
            samples[i] = Sample(terrain, xs[i], halfWindow);
        return samples;
    }

    public static TerrainMaterialKind MaterialFor(TerrainMask terrain, float x)
    {
        var y = terrain.GetSurfaceY(x);
        if (y > terrain.Height - 36) return TerrainMaterialKind.Rock;
        return y < terrain.Height * 0.56f ? TerrainMaterialKind.Scorched : TerrainMaterialKind.Dirt;
    }

    internal static Vector2 NormalizeOrFallback(Vector2 vector, Vector2 fallback)
    {
        var lengthSquared = vector.LengthSquared();
        if (float.IsFinite(lengthSquared) && lengthSquared > 0.0001f)
            return Vector2.Normalize(vector);
        return fallback;
    }

    internal static float ClampFinite(float value, float min, float max) =>
        float.IsFinite(value) ? Math.Clamp(value, min, max) : min;
}

public sealed class TankPoseService
{
    private readonly TerrainSamplingService _terrain = new();

    public TankVisualPose BuildPose(Tank tank, TerrainMask terrain, IReadOnlyList<ShockwaveImpulsePayload> impulses, string firingTankId = "", bool reducedMotion = false)
    {
        const float halfTrack = GameConstants.TankWidth * 0.38f;
        var left = _terrain.Sample(terrain, tank.Position.X - halfTrack, 4);
        var right = _terrain.Sample(terrain, tank.Position.X + halfTrack, 4);
        var center = _terrain.Sample(terrain, tank.Position.X, 10);
        var angle = MathF.Atan2(right.SurfaceY - left.SurfaceY, halfTrack * 2f) * 180f / MathF.PI;
        angle = TerrainSamplingService.ClampFinite(angle, -24f, 24f);

        var impulse = StrongestImpulseAt(tank.Center, impulses);
        var rock = 0f;
        if (impulse is not null && !reducedMotion)
        {
            var away = tank.Center - new Vector2(impulse.X, impulse.Y);
            var sign = away.X >= 0 ? 1f : -1f;
            rock = sign * Math.Clamp(impulse.Intensity * 0.16f, 0f, 14f);
        }

        var recoil = Vector2.Zero;
        var compression = Math.Clamp(MathF.Abs(angle) / 28f, 0f, 0.42f);
        if (string.Equals(tank.Id, firingTankId, StringComparison.OrdinalIgnoreCase) && !reducedMotion)
        {
            var radians = tank.TurretAngle * MathF.PI / 180f;
            recoil = new Vector2(-MathF.Cos(radians), MathF.Sin(radians)) * 21f;
            compression = Math.Min(1f, compression + 0.78f);
        }

        return new TankVisualPose(
            tank.Id,
            tank.Position.X,
            center.SurfaceY,
            angle,
            Math.Clamp(((left.SurfaceY + right.SurfaceY) * 0.5f) - center.SurfaceY, -8f, 12f),
            left.SurfaceY,
            right.SurfaceY,
            reducedMotion ? compression * 0.35f : compression,
            recoil.X,
            recoil.Y,
            reducedMotion ? 0 : rock,
            Math.Clamp(1f + compression * 0.16f, 0.9f, 1.18f));
    }

    private static ShockwaveImpulsePayload? StrongestImpulseAt(Vector2 point, IReadOnlyList<ShockwaveImpulsePayload> impulses)
    {
        ShockwaveImpulsePayload? best = null;
        var bestScore = 0f;
        for (var i = 0; i < impulses.Count; i++)
        {
            var impulse = impulses[i];
            var dx = point.X - impulse.X;
            var dy = point.Y - impulse.Y;
            var distance = MathF.Sqrt((dx * dx) + (dy * dy));
            if (distance > impulse.Radius) continue;
            var score = impulse.Intensity * MathF.Pow(1f - (distance / impulse.Radius), 1.35f) * impulse.TerrainDampening;
            if (score > bestScore)
            {
                bestScore = score;
                best = impulse;
            }
        }

        return best;
    }
}

public sealed class ShockwaveImpulseService
{
    public ShockwaveImpulsePayload[] Build(IReadOnlyList<ExplosionResult> explosions, TerrainMask terrain, bool reducedMotion = false)
    {
        var payloads = new List<ShockwaveImpulsePayload>(explosions.Count);
        for (var i = 0; i < explosions.Count; i++)
        {
            var explosion = explosions[i];
            if (!float.IsFinite(explosion.Center.X) || !float.IsFinite(explosion.Center.Y)) continue;
            var radius = Math.Clamp(MathF.Max(explosion.DamageRadius, explosion.TerrainRadius) * 2.35f, 1f, MathF.Max(terrain.Width, terrain.Height));
            var intensity = Math.Clamp((explosion.DamageRadius * 1.25f) + (explosion.TerrainRadius * 0.35f), 0f, reducedMotion ? 80f : 260f);
            var surface = terrain.GetSurfaceY(explosion.Center.X);
            var dampening = explosion.Center.Y > surface + 16f ? 0.58f : 1f;
            payloads.Add(new ShockwaveImpulsePayload(
                explosion.Center.X,
                explosion.Center.Y,
                radius,
                intensity,
                0,
                -1,
                dampening,
                explosion.VisualKind.ToString()));
        }

        return payloads.ToArray();
    }
}

public sealed class TerrainSlumpingService
{
    public TerrainSlumpPayload Relax(TerrainMask terrain, IReadOnlyList<ExplosionResult> explosions, bool reducedMotion = false, TerrainRelaxationMode mode = TerrainRelaxationMode.Auto)
    {
        if (explosions.Count == 0) return new TerrainSlumpPayload([], 0, reducedMotion);

        var before = terrain.SolidTop.ToArray();
        var after = before.ToArray();
        var minX = terrain.Width - 1;
        var maxX = 0;
        var hasZone = false;
        for (var i = 0; i < explosions.Count; i++)
        {
            var explosion = explosions[i];
            var radius = MathF.Max(explosion.TerrainRadius, explosion.DamageRadius * 0.6f);
            if (!float.IsFinite(explosion.Center.X) || radius <= 0) continue;
            minX = Math.Min(minX, Math.Clamp((int)MathF.Floor(explosion.Center.X - radius - 10), 0, terrain.Width - 1));
            maxX = Math.Max(maxX, Math.Clamp((int)MathF.Ceiling(explosion.Center.X + radius + 10), 0, terrain.Width - 1));
            hasZone = true;
        }

        if (!hasZone || minX >= maxX) return new TerrainSlumpPayload([], 0, reducedMotion);

        var changed = mode switch
        {
            TerrainRelaxationMode.Scalar => RelaxScalar(after, minX, maxX, terrain.Height),
            TerrainRelaxationMode.Simd => RelaxSimd(after, minX, maxX, terrain.Height),
            _ => TerrainMask.SimdAccelerated ? RelaxSimd(after, minX, maxX, terrain.Height) : RelaxScalar(after, minX, maxX, terrain.Height)
        };

        if (changed == 0) return new TerrainSlumpPayload([], 0, reducedMotion);

        terrain.CopyFrom(new TerrainMask(terrain.Width, terrain.Height, after));
        var columns = new List<TerrainSlumpColumnPayload>(changed);
        var duration = reducedMotion ? 120f : 620f;
        for (var x = minX; x <= maxX; x++)
        {
            if (MathF.Abs(before[x] - after[x]) <= 0.001f) continue;
            columns.Add(new TerrainSlumpColumnPayload(
                x,
                before[x],
                after[x],
                reducedMotion ? 0 : Math.Clamp(MathF.Abs(before[x] - after[x]) * 2.5f, 0, 90),
                duration));
        }

        return new TerrainSlumpPayload(columns.ToArray(), duration, reducedMotion);
    }

    private static int RelaxScalar(float[] heights, int minX, int maxX, int worldHeight)
    {
        const float Threshold = 9f;
        const float MaxTransfer = 12f;
        var deltas = new float[heights.Length];
        var changed = 0;
        for (var x = minX; x < maxX; x++)
        {
            var left = heights[x];
            var right = heights[x + 1];
            if (!float.IsFinite(left) || !float.IsFinite(right)) continue;
            var diff = right - left;
            var excess = MathF.Abs(diff) - Threshold;
            if (excess <= 0) continue;
            var transfer = MathF.Min(MaxTransfer, excess * 0.45f);
            if (diff > 0)
            {
                deltas[x] += transfer;
                deltas[x + 1] -= transfer;
            }
            else
            {
                deltas[x] -= transfer;
                deltas[x + 1] += transfer;
            }
        }

        for (var x = minX; x <= maxX; x++)
        {
            if (MathF.Abs(deltas[x]) <= 0.001f) continue;
            heights[x] = Math.Clamp(heights[x] + deltas[x], 0, worldHeight);
            changed++;
        }

        return changed;
    }

    private static int RelaxSimd(float[] heights, int minX, int maxX, int worldHeight) =>
        // The final transfer writes overlap neighboring columns, so the SIMD path
        // intentionally shares the scalar transfer stage to preserve determinism.
        RelaxScalar(heights, minX, maxX, worldHeight);
}

public enum TerrainRelaxationMode
{
    Auto,
    Scalar,
    Simd
}

public sealed class VisualPhysicsPayloadService
{
    private readonly TerrainSamplingService _terrain = new();
    private readonly TankPoseService _tankPose = new();
    private readonly ShockwaveImpulseService _shockwaves = new();

    public VisualPhysicsPayload Build(
        TerrainMask terrain,
        Tank player,
        Tank cpu,
        IReadOnlyList<ExplosionResult> explosions,
        string weaponId,
        string ownerTankId,
        ShotVisualKind visualKind,
        int wind,
        TerrainSlumpPayload slump,
        bool reducedMotion = false)
    {
        var impulses = _shockwaves.Build(explosions, terrain, reducedMotion);
        return new VisualPhysicsPayload(
            slump,
            [
                _tankPose.BuildPose(player, terrain, impulses, ownerTankId, reducedMotion),
                _tankPose.BuildPose(cpu, terrain, impulses, ownerTankId, reducedMotion)
            ],
            impulses,
            BuildDebris(terrain, explosions, impulses, reducedMotion),
            BuildImpacts(terrain, explosions, visualKind),
            BuildLingering(terrain, explosions, wind, reducedMotion),
            TerrainMask.SimdAccelerated);
    }

    private DebrisSettlingPayload[] BuildDebris(TerrainMask terrain, IReadOnlyList<ExplosionResult> explosions, IReadOnlyList<ShockwaveImpulsePayload> impulses, bool reducedMotion)
    {
        var debris = new List<DebrisSettlingPayload>();
        var maxPerExplosion = reducedMotion ? 4 : 12;
        for (var e = 0; e < explosions.Count; e++)
        {
            var explosion = explosions[e];
            var count = Math.Min(maxPerExplosion, Math.Max(2, (int)MathF.Round(explosion.TerrainRadius / 12f)));
            for (var i = 0; i < count; i++)
            {
                var lane = i - ((count - 1) * 0.5f);
                var x = Math.Clamp(explosion.Center.X + lane * 9f, 0, terrain.Width - 1);
                var sample = _terrain.Sample(terrain, x, 5);
                var downhill = TerrainSamplingService.NormalizeOrFallback(new Vector2(sample.Slope >= 0 ? 1f : -1f, MathF.Abs(sample.Slope)), Vector2.UnitX);
                var material = sample.Material.ToString();
                var friction = sample.Material == TerrainMaterialKind.Rock ? 0.82f : sample.Material == TerrainMaterialKind.Scorched ? 0.68f : 0.58f;
                debris.Add(new DebrisSettlingPayload(
                    x,
                    sample.SurfaceY - 3,
                    downhill.X * Math.Clamp(MathF.Abs(sample.Slope) * 70f, 0, 160),
                    -Math.Clamp(explosion.TerrainRadius * 0.22f, 4, 38),
                    friction,
                    reducedMotion ? 0.18f : 0.42f,
                    material));
            }
        }

        return debris.ToArray();
    }

    private static ImpactParticlePayload[] BuildImpacts(TerrainMask terrain, IReadOnlyList<ExplosionResult> explosions, ShotVisualKind visualKind)
    {
        var impacts = new List<ImpactParticlePayload>(explosions.Count);
        for (var i = 0; i < explosions.Count; i++)
        {
            var explosion = explosions[i];
            var material = MaterialForImpact(terrain, explosion, visualKind);
            var shieldLike = material is ImpactMaterialKind.Shield or ImpactMaterialKind.Energy;
            impacts.Add(new ImpactParticlePayload(
                explosion.Center.X,
                explosion.Center.Y,
                0,
                -1,
                Math.Clamp(explosion.DamageRadius + explosion.TerrainRadius * 0.4f, 0, 220),
                material.ToString(),
                explosion.VisualKind.ToString(),
                shieldLike));
        }

        return impacts.ToArray();
    }

    private LingeringEffectPayload[] BuildLingering(TerrainMask terrain, IReadOnlyList<ExplosionResult> explosions, int wind, bool reducedMotion)
    {
        var lingering = new List<LingeringEffectPayload>();
        for (var i = 0; i < explosions.Count; i++)
        {
            var explosion = explosions[i];
            if (explosion.RadiationZones.Count == 0 && explosion.VisualKind is not (ShotVisualKind.Fire or ShotVisualKind.Lava or ShotVisualKind.Nuclear))
                continue;

            var sample = _terrain.Sample(terrain, explosion.Center.X, 12);
            var lifetime = reducedMotion ? 0.8f : explosion.Nuclear ? 8f : 4f;
            lingering.Add(new LingeringEffectPayload(
                explosion.Center.X,
                Math.Min(explosion.Center.Y, sample.SurfaceY),
                wind,
                sample.Slope,
                MathF.Abs(sample.Slope),
                lifetime,
                Math.Clamp(explosion.DamageRadius / 80f, 0.2f, 2.5f),
                explosion.VisualKind.ToString()));
        }

        return lingering.ToArray();
    }

    private static ImpactMaterialKind MaterialForImpact(TerrainMask terrain, ExplosionResult explosion, ShotVisualKind visualKind)
    {
        if (explosion.VisualKind == ShotVisualKind.ShieldHit || explosion.VisualKind == ShotVisualKind.PatriotIntercept)
            return ImpactMaterialKind.Shield;
        if (visualKind == ShotVisualKind.Laser)
            return ImpactMaterialKind.Energy;
        if (explosion.PlayerDamage > 0 || explosion.CpuDamage > 0)
            return ImpactMaterialKind.Metal;
        if (explosion.VisualKind is ShotVisualKind.Fire)
            return ImpactMaterialKind.Fire;
        if (explosion.VisualKind is ShotVisualKind.Lava or ShotVisualKind.Nuclear)
            return ImpactMaterialKind.Lava;
        return TerrainSamplingService.MaterialFor(terrain, explosion.Center.X) == TerrainMaterialKind.Rock
            ? ImpactMaterialKind.Rock
            : ImpactMaterialKind.Dirt;
    }
}

public sealed record VisualPhysicsPayload(
    TerrainSlumpPayload Slump,
    TankVisualPose[] TankPoses,
    ShockwaveImpulsePayload[] Shockwaves,
    DebrisSettlingPayload[] Debris,
    ImpactParticlePayload[] Impacts,
    LingeringEffectPayload[] Lingering,
    bool SimdEnabled)
{
    public static VisualPhysicsPayload Empty { get; } = new(
        new TerrainSlumpPayload([], 0, false),
        [],
        [],
        [],
        [],
        [],
        TerrainMask.SimdAccelerated);
}
