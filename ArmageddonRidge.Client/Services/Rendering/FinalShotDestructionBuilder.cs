using System.Numerics;
using ArmageddonRidge.Core;
using ArmageddonRidge.Core.Models;

namespace ArmageddonRidge.Client.Services.Rendering;

internal static class FinalShotDestructionBuilder
{
    private const int FullPieceCount = 24;
    private const int MutualPieceCount = 14;

    private static readonly string[] SpriteKeys =
    [
        "hull",
        "turret",
        "tread",
        "barrel",
        "plate",
        "plate",
        "tread",
        "plate",
        "plate",
        "plate",
        "tread",
        "plate",
        "plate",
        "barrel",
        "plate",
        "tread",
        "plate",
        "plate",
        "plate"
    ];

    public static FinalShotDestructionPayload? Build(
        RenderScene scene,
        ShotResolution resolution,
        int wind,
        bool reducedMotion,
        bool? playerDestroyed = null,
        bool? cpuDestroyed = null,
        bool forceScalar = false)
    {
        if (!resolution.RoundEnded || resolution.Intercepted || resolution.Winner is null)
        {
            return null;
        }

        var victims = Victims(scene, resolution.Winner.Value, playerDestroyed, cpuDestroyed);
        if (victims.Length == 0)
        {
            return null;
        }

        var explosions = RenderPayloadSanitizer.BuildEffectExplosionPayload(resolution.Explosions);
        if (explosions.Length == 0)
        {
            return null;
        }

        var pieces = new List<FinalShotDebrisPiece>(victims.Length * FullPieceCount);
        var terrain = scene.Terrain ?? [];
        var worldWidth = Math.Max(1, scene.World.Width);
        var worldHeight = Math.Max(1, scene.World.Height);
        var mutual = victims.Length > 1;

        for (var victimIndex = 0; victimIndex < victims.Length; victimIndex++)
        {
            var victim = victims[victimIndex];
            var count = reducedMotion
                ? (mutual ? 6 : 8)
                : (mutual ? MutualPieceCount : FullPieceCount);
            var blast = forceScalar
                ? StrongestExplosionScalar(victim, explosions)
                : StrongestExplosionSimd(victim, explosions);
            var slope = forceScalar
                ? TerrainSlopeScalar(terrain, victim.X)
                : TerrainSlopeSimd(terrain, victim.X);
            var seed = HashCode.Combine(
                resolution.WeaponId,
                resolution.OwnerTankId,
                victim.Id,
                resolution.Trail.Count,
                resolution.Explosions.Count,
                wind,
                victimIndex);

            AddVictimPieces(
                pieces,
                victim,
                blast,
                count,
                seed,
                wind,
                slope,
                worldWidth,
                worldHeight,
                reducedMotion);
        }

        if (pieces.Count == 0)
        {
            return null;
        }

        var primary = explosions[^1];
        return new FinalShotDestructionPayload(
            true,
            primary.X,
            primary.Y,
            Math.Clamp(primary.Radius * (victims.Length > 1 ? 1.25f : 1.6f), 48, 280),
            victims.Length > 1,
            reducedMotion,
            pieces.ToArray());
    }

    private static RenderTank[] Victims(RenderScene scene, TurnOwner winner, bool? playerDestroyed, bool? cpuDestroyed)
    {
        var resolvedPlayerDestroyed = playerDestroyed ?? scene.Player.Health <= 0;
        var resolvedCpuDestroyed = cpuDestroyed ?? scene.Cpu.Health <= 0;

        if (resolvedPlayerDestroyed && resolvedCpuDestroyed)
        {
            return [scene.Player, scene.Cpu];
        }

        if (resolvedCpuDestroyed && !resolvedPlayerDestroyed)
        {
            return [scene.Cpu];
        }

        if (resolvedPlayerDestroyed && !resolvedCpuDestroyed)
        {
            return [scene.Player];
        }

        return winner == TurnOwner.Player ? [scene.Cpu] : [scene.Player];
    }

    private static void AddVictimPieces(
        List<FinalShotDebrisPiece> pieces,
        RenderTank victim,
        EffectExplosionPayload blast,
        int count,
        int seed,
        int wind,
        float slope,
        int worldWidth,
        int worldHeight,
        bool reducedMotion)
    {
        var originX = ClampFinite(victim.X, 0, worldWidth);
        var originY = ClampFinite(victim.Y - 26, 0, worldHeight);
        var dx = originX - blast.X;
        var dy = originY - blast.Y;
        var length = MathF.Sqrt(dx * dx + dy * dy);
        if (length < 0.001f)
        {
            dx = victim.IsCpu ? -1 : 1;
            dy = -0.35f;
            length = MathF.Sqrt(dx * dx + dy * dy);
        }

        var blastX = dx / length;
        var blastY = MathF.Min(-0.22f, dy / length - 0.34f);
        var intensity = Math.Clamp((blast.Radius + blast.TerrainRadius) * 0.012f, 0.85f, 3.2f);
        var windImpulse = Math.Clamp(wind * 0.7f, -70, 70);
        var slopeImpulse = Math.Clamp(slope * -3.2f, -80, 80);

        for (var i = 0; i < count; i++)
        {
            var styleIndex = i % SpriteKeys.Length;
            var heavy = SpriteKeys[styleIndex] is "hull" or "turret";
            var tread = SpriteKeys[styleIndex] == "tread";
            var unit = HashUnit(seed, i);
            var spread = -0.92f + (unit * 1.84f);
            var lift = 0.56f + HashUnit(seed, i + 29) * 0.42f;
            var speed = (heavy ? 78f : tread ? 132f : 108f) * intensity * (0.78f + HashUnit(seed, i + 7) * 0.42f);
            var lateral = spread * (heavy ? 26f : 58f);
            var vx = (blastX * speed) + lateral + windImpulse + slopeImpulse;
            var vy = (blastY * speed * lift) - (heavy ? 58f : 98f) - HashUnit(seed, i + 13) * 46f;
            var size = heavy ? 14f + HashUnit(seed, i + 3) * 8f : tread ? 8f + HashUnit(seed, i + 5) * 6f : 5f + HashUnit(seed, i + 11) * 5f;
            var mass = heavy ? 5.6f + HashUnit(seed, i + 17) * 3.2f : tread ? 2.6f : 1.3f + HashUnit(seed, i + 19) * 1.2f;
            var restitution = heavy ? 0.12f : tread ? 0.22f : 0.28f;
            var friction = heavy ? 0.84f : tread ? 0.78f : 0.72f;
            var drag = heavy ? 0.32f : 0.42f;
            var spin = spread * (heavy ? 1.1f : 3.4f) + (HashUnit(seed, i + 23) - 0.5f) * 1.6f;
            var lifetime = reducedMotion ? 2.2f : heavy ? 6.2f : 5.1f;
            var tint = victim.IsCpu
                ? new[] { 0.92f, 0.42f, 0.32f }
                : new[] { 0.26f, 0.78f, 0.72f };

            pieces.Add(new FinalShotDebrisPiece(
                victim.Id,
                SpriteKeys[styleIndex],
                originX + spread * 12f,
                originY - HashUnit(seed, i + 31) * 12f,
                vx,
                vy,
                size,
                mass,
                restitution,
                friction,
                drag,
                spin,
                lifetime,
                tint[0],
                tint[1],
                tint[2],
                seed + i * 7919));
        }
    }

    private static EffectExplosionPayload StrongestExplosionScalar(RenderTank victim, IReadOnlyList<EffectExplosionPayload> explosions)
    {
        var bestIndex = explosions.Count - 1;
        var bestScore = float.NegativeInfinity;
        for (var i = 0; i < explosions.Count; i++)
        {
            var explosion = explosions[i];
            var dx = explosion.X - victim.X;
            var dy = explosion.Y - victim.Y;
            var distanceSquared = dx * dx + dy * dy;
            var score = (explosion.Radius * explosion.Radius) / MathF.Max(1, distanceSquared);
            if (score > bestScore)
            {
                bestScore = score;
                bestIndex = i;
            }
        }

        return explosions[bestIndex];
    }

    private static EffectExplosionPayload StrongestExplosionSimd(RenderTank victim, IReadOnlyList<EffectExplosionPayload> explosions)
    {
        if (explosions.Count < Vector<float>.Count)
        {
            return StrongestExplosionScalar(victim, explosions);
        }

        var width = Vector<float>.Count;
        Span<float> xs = stackalloc float[width];
        Span<float> ys = stackalloc float[width];
        Span<float> radii = stackalloc float[width];
        var victimX = new Vector<float>(victim.X);
        var victimY = new Vector<float>(victim.Y);
        var bestIndex = explosions.Count - 1;
        var bestScore = float.NegativeInfinity;

        var i = 0;
        for (; i <= explosions.Count - width; i += width)
        {
            for (var lane = 0; lane < width; lane++)
            {
                xs[lane] = explosions[i + lane].X;
                ys[lane] = explosions[i + lane].Y;
                radii[lane] = explosions[i + lane].Radius;
            }

            var dx = new Vector<float>(xs) - victimX;
            var dy = new Vector<float>(ys) - victimY;
            var radius = new Vector<float>(radii);
            var scores = (radius * radius) / Vector.Max(Vector<float>.One, (dx * dx) + (dy * dy));
            for (var lane = 0; lane < width; lane++)
            {
                var score = scores[lane];
                if (score > bestScore)
                {
                    bestScore = score;
                    bestIndex = i + lane;
                }
            }
        }

        for (; i < explosions.Count; i++)
        {
            var explosion = explosions[i];
            var dx = explosion.X - victim.X;
            var dy = explosion.Y - victim.Y;
            var score = (explosion.Radius * explosion.Radius) / MathF.Max(1, (dx * dx) + (dy * dy));
            if (score > bestScore)
            {
                bestScore = score;
                bestIndex = i;
            }
        }

        return explosions[bestIndex];
    }

    private static float TerrainSlopeScalar(float[] terrain, float x)
    {
        if (terrain.Length < 3)
        {
            return 0;
        }

        var center = Math.Clamp((int)MathF.Round(x), 1, terrain.Length - 2);
        var right = FiniteOrDefault(terrain[Math.Min(terrain.Length - 1, center + 6)], terrain[center]);
        var left = FiniteOrDefault(terrain[Math.Max(0, center - 6)], terrain[center]);
        return float.IsFinite(right - left) ? (right - left) / 12f : 0;
    }

    private static float TerrainSlopeSimd(float[] terrain, float x)
    {
        if (terrain.Length < Vector<float>.Count + 2)
        {
            return TerrainSlopeScalar(terrain, x);
        }

        var width = Vector<float>.Count;
        var center = Math.Clamp((int)MathF.Round(x), width, terrain.Length - width - 1);
        for (var i = center - width; i <= center + width; i++)
        {
            if (!float.IsFinite(terrain[i]))
            {
                return TerrainSlopeScalar(terrain, x);
            }
        }

        var left = new Vector<float>(terrain, center - width);
        var right = new Vector<float>(terrain, center + 1);
        var total = Vector.Sum(right - left);
        return total / (width * (width + 1));
    }

    private static float HashUnit(int seed, int salt)
    {
        var value = (uint)HashCode.Combine(seed, salt);
        value ^= value >> 16;
        value *= 0x7feb352d;
        value ^= value >> 15;
        value *= 0x846ca68b;
        value ^= value >> 16;
        return (value & 0x00ffffff) / 16777215f;
    }

    private static float ClampFinite(float value, float min, float max) =>
        float.IsFinite(value) ? Math.Clamp(value, min, max) : min;

    private static float FiniteOrDefault(float value, float fallback) =>
        float.IsFinite(value) ? value : float.IsFinite(fallback) ? fallback : 0;
}

internal sealed record FinalShotDestructionPayload(
    bool Active,
    float X,
    float Y,
    float Radius,
    bool Mutual,
    bool ReducedMotion,
    FinalShotDebrisPiece[] Pieces);

internal sealed record FinalShotDebrisPiece(
    string VictimId,
    string Sprite,
    float X,
    float Y,
    float Vx,
    float Vy,
    float Size,
    float Mass,
    float Restitution,
    float Friction,
    float Drag,
    float Spin,
    float Lifetime,
    float R,
    float G,
    float B,
    int Seed);
