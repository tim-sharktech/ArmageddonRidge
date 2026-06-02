using System.Diagnostics;
using ArmageddonRidge.Core.Content;
using ArmageddonRidge.Core.Game;
using ArmageddonRidge.Core.Models;
using ArmageddonRidge.Core.Physics;

namespace ArmageddonRidge.Core.AI;

/// <summary>
/// Deterministic CPU planner that scores simulated shots for the current difficulty.
/// </summary>
/// <param name="weapons">Weapon catalog used for candidate lookup.</param>
/// <param name="simulator">Projectile simulator used for deterministic candidate scoring.</param>
public sealed class CpuOpponent(WeaponCatalog weapons, ProjectileSimulator simulator)
{
    private readonly WeaponCatalog _weapons = weapons;
    private readonly ProjectileSimulator _simulator = simulator;

    /// <summary>
    /// Chooses the CPU weapon, angle, and power for the current turn.
    /// </summary>
    public CpuShotPlan PlanShot(GameState state, MatchSettings settings) =>
        PlanShotCoreAsync(state, settings, 0, CancellationToken.None).GetAwaiter().GetResult();

    /// <summary>
    /// Chooses the CPU weapon, angle, and power while yielding periodically so browser animation can keep ticking.
    /// </summary>
    public ValueTask<CpuShotPlan> PlanShotAsync(GameState state, MatchSettings settings, CancellationToken cancellationToken = default) =>
        PlanShotCoreAsync(state, settings, 24, cancellationToken);

    private async ValueTask<CpuShotPlan> PlanShotCoreAsync(
        GameState state,
        MatchSettings settings,
        int yieldEveryCandidates,
        CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        var profile = CpuDifficultyProfile.For(settings.Difficulty);
        var candidates = _weapons.All;
        var best = new CpuShotPlan(WeaponIds.PeaShell, 140, 60, "Calculating... emotionally.", 0, 0);
        var scoredCandidates = 0;

        for (var candidateIndex = 0; candidateIndex < candidates.Count; candidateIndex++)
        {
            var weapon = candidates[candidateIndex];
            if (weapon.Id != WeaponIds.PeaShell)
            {
                if (!state.CpuTank.HasWeapon(weapon.Id)) continue;
                if (!settings.EnableNuclearWeapons && weapon.Category == WeaponCategory.Nuclear) continue;
            }

            if (weapon.Id == WeaponIds.DarkEagle)
            {
                var guidedScore = (weapon.MaxDamage * 12f)
                    - (weapon.Cost * profile.CostPenalty)
                    + 450f
                    + (DeterministicNoise(state, weapon.Id, 0, 100, 0) * profile.Noise);

                if (guidedScore > best.Score)
                    best = new CpuShotPlan(weapon.Id, AngleToward(state.CpuTank, state.PlayerTank), 100, TauntFor(state.CpuTank, weapon), guidedScore, watch.Elapsed.TotalMilliseconds);

                continue;
            }

            const int angleStart = 92;
            const int angleEnd = 176;
            const int powerStart = 25;
            const int powerEnd = 100;
            var angleStep = profile.AngleStep;
            var powerStep = profile.PowerStep;
            var coarseAngleStep = Math.Max(angleStep * 2, angleStep + 2);
            var coarsePowerStep = Math.Max(powerStep * 2, powerStep + 4);
            var topCandidates = new List<CpuPlanningCandidate>(profile.RefineCandidates);

            for (var angle = angleStart; angle <= angleEnd; angle += coarseAngleStep)
            {
                for (var power = powerStart; power <= powerEnd; power += coarsePowerStep)
                {
                    if (yieldEveryCandidates > 0 && ++scoredCandidates % yieldEveryCandidates == 0)
                        await Task.Delay(1, cancellationToken);

                    var candidate = ScoreCandidate(state, weapon, profile, angle, power);
                    AddTopCandidate(topCandidates, candidate, profile.RefineCandidates);
                    if (candidate.Score > best.Score)
                    {
                        var angleNoise = Noise(state, weapon.Id, angle, power, 1, profile.AngleNoise);
                        var powerNoise = Noise(state, weapon.Id, angle, power, 2, profile.PowerNoise);
                        best = new CpuShotPlan(weapon.Id, angle + angleNoise, power + (int)powerNoise, TauntFor(state.CpuTank, weapon), candidate.Score, watch.Elapsed.TotalMilliseconds);
                    }
                }
            }

            for (var topIndex = 0; topIndex < topCandidates.Count; topIndex++)
            {
                var seed = topCandidates[topIndex];
                var refineAngleStart = Math.Max(angleStart, seed.Angle - coarseAngleStep);
                var refineAngleEnd = Math.Min(angleEnd, seed.Angle + coarseAngleStep);
                var refinePowerStart = Math.Max(powerStart, seed.Power - coarsePowerStep);
                var refinePowerEnd = Math.Min(powerEnd, seed.Power + coarsePowerStep);

                for (var angle = refineAngleStart; angle <= refineAngleEnd; angle += angleStep)
                {
                    for (var power = refinePowerStart; power <= refinePowerEnd; power += powerStep)
                    {
                        if (yieldEveryCandidates > 0 && ++scoredCandidates % yieldEveryCandidates == 0)
                            await Task.Delay(1, cancellationToken);

                        var candidate = ScoreCandidate(state, weapon, profile, angle, power);
                        if (candidate.Score <= best.Score)
                            continue;

                        var angleNoise = Noise(state, weapon.Id, angle, power, 1, profile.AngleNoise);
                        var powerNoise = Noise(state, weapon.Id, angle, power, 2, profile.PowerNoise);
                        best = new CpuShotPlan(weapon.Id, angle + angleNoise, power + (int)powerNoise, TauntFor(state.CpuTank, weapon), candidate.Score, watch.Elapsed.TotalMilliseconds);
                        if (profile.GoodEnoughScore > 0 && candidate.DirectHit && candidate.Score >= profile.GoodEnoughScore)
                            return best with { PlanningMs = watch.Elapsed.TotalMilliseconds };
                    }
                }
            }
        }

        watch.Stop();
        return best with { PlanningMs = watch.Elapsed.TotalMilliseconds };
    }

    private ProjectilePlanningSimulation SimulateCandidate(GameState state, WeaponDefinition weapon, int angle, int power) =>
        _simulator.SimulateForPlanning(state.Terrain, state.CpuTank, state.PlayerTank, weapon, angle, power, state.Wind, 60 * 7);

    private CpuPlanningCandidate ScoreCandidate(GameState state, WeaponDefinition weapon, CpuDifficultyProfile profile, int angle, int power)
    {
        var simulation = SimulateCandidate(state, weapon, angle, power);
        var enemyPotential = MathF.Max(0, weapon.BlastRadius - simulation.NearestOpponentDistance);
        var selfRisk = MathF.Max(0, weapon.BlastRadius - simulation.NearestOwnerDistance);
        var directHit = simulation.StopReason is ProjectileStopReason.TankHit or ProjectileStopReason.ShieldHit;
        var score = (enemyPotential * 10f)
            - (selfRisk * profile.SelfDamagePenalty)
            - (weapon.Cost * profile.CostPenalty)
            + (directHit ? 400 : 0);

        if (weapon.BehaviorType == WeaponBehaviorType.DroneSwarm)
            score += 60;

        if (weapon.Category == WeaponCategory.Nuclear && selfRisk / MathF.Max(weapon.BlastRadius, 1) > profile.NukeSelfRiskTolerance)
            score -= 900;

        score += DeterministicNoise(state, weapon.Id, angle, power, 0) * profile.Noise;
        return new CpuPlanningCandidate(angle, power, score, directHit);
    }

    private static void AddTopCandidate(List<CpuPlanningCandidate> candidates, CpuPlanningCandidate candidate, int maxCount)
    {
        var insertAt = candidates.Count;
        for (var i = 0; i < candidates.Count; i++)
        {
            if (candidate.Score > candidates[i].Score)
            {
                insertAt = i;
                break;
            }
        }

        if (insertAt >= maxCount)
            return;

        candidates.Insert(insertAt, candidate);
        if (candidates.Count > maxCount)
            candidates.RemoveAt(candidates.Count - 1);
    }

    private static float Noise(GameState state, string weaponId, int angle, int power, int salt, float magnitude) =>
        (DeterministicNoise(state, weaponId, angle, power, salt) - 0.5f) * magnitude;

    private static float DeterministicNoise(GameState state, string weaponId, int angle, int power, int salt)
    {
        var hash = HashCode.Combine(state.RandomSeed, state.RoundNumber, state.ShotsFired, weaponId, angle, power, salt);
        return (hash & 0x7fffffff) / (float)int.MaxValue;
    }

    private static float AngleToward(Tank owner, Tank target)
    {
        var delta = target.Center - owner.Center;
        var angle = MathF.Atan2(-delta.Y, delta.X) * 180f / MathF.PI;
        return Math.Clamp(angle, 5f, 175f);
    }

    private static string TauntFor(Tank cpu, WeaponDefinition weapon)
    {
        if (weapon.Category == WeaponCategory.Nuclear)
            return $"{cpu.Name}: I brought sunscreen. And a warhead.";

        if (weapon.Id == WeaponIds.ShahedDroneSwarm)
            return $"{cpu.Name}: Arcade drones inbound. Very dramatic, barely regulated.";

        if (weapon.Id == WeaponIds.DarkEagle)
            return $"{cpu.Name}: Low wind, high drama.";

        if (weapon.Id == WeaponIds.Gbu57Mop)
            return $"{cpu.Name}: Two-stage problem incoming.";

        var line = weapon.BehaviorType switch
        {
            WeaponBehaviorType.Laser => "Wind is merely a suggestion.",
            _ => ""
        };
        return string.IsNullOrWhiteSpace(line) ? "" : $"{cpu.Name}: {line}";
    }
}

/// <summary>
/// Selected CPU shot and planning metadata.
/// </summary>
/// <param name="WeaponId">Weapon identifier to fire.</param>
/// <param name="Angle">Chosen turret angle in degrees.</param>
/// <param name="Power">Chosen shot power.</param>
/// <param name="Taunt">Optional CPU taunt to show with the shot.</param>
/// <param name="Score">Internal candidate score used for comparisons.</param>
/// <param name="PlanningMs">Elapsed planning time in milliseconds.</param>
public sealed record CpuShotPlan(string WeaponId, float Angle, int Power, string Taunt, float Score, double PlanningMs);

internal sealed record CpuDifficultyProfile(
    int AngleStep,
    int PowerStep,
    int RefineCandidates,
    float GoodEnoughScore,
    float AngleNoise,
    float PowerNoise,
    float Noise,
    float SelfDamagePenalty,
    float CostPenalty,
    float NukeSelfRiskTolerance)
{
    public static CpuDifficultyProfile For(Difficulty difficulty) => difficulty switch
    {
        Difficulty.Rookie => new(8, 12, 2, 420, 16, 20, 120, 8, 0.35f, 0.18f),
        Difficulty.Normal => new(5, 8, 3, 520, 8, 10, 70, 15, 0.18f, 0.28f),
        Difficulty.Veteran => new(4, 6, 4, 660, 4, 6, 38, 20, 0.12f, 0.35f),
        Difficulty.Maniac => new(4, 6, 4, 620, 7, 8, 55, 11, 0.08f, 0.46f),
        Difficulty.Oracle => new(2, 4, 6, 0, 2, 3, 12, 22, 0.05f, 0.32f),
        _ => new(5, 8, 3, 520, 8, 10, 70, 15, 0.18f, 0.28f)
    };
}

internal sealed record CpuPlanningCandidate(int Angle, int Power, float Score, bool DirectHit);
