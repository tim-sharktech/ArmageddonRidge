using System.Diagnostics;
using ArmageddonRidge.Core.Content;
using ArmageddonRidge.Core.Game;
using ArmageddonRidge.Core.Models;
using ArmageddonRidge.Core.Physics;

namespace ArmageddonRidge.Core.AI;

public sealed class CpuOpponent
{
    private readonly WeaponCatalog _weapons;
    private readonly ProjectileSimulator _simulator;

    public CpuOpponent(WeaponCatalog weapons, ProjectileSimulator simulator)
    {
        _weapons = weapons;
        _simulator = simulator;
    }

    public CpuShotPlan PlanShot(GameState state, MatchSettings settings)
    {
        var watch = Stopwatch.StartNew();
        var profile = CpuDifficultyProfile.For(settings.Difficulty);
        var candidates = CandidateWeapons(state.CpuTank, settings).ToArray();
        var best = new CpuShotPlan(WeaponIds.PeaShell, 140, 60, "Calculating... emotionally.", 0, 0);

        foreach (var weapon in candidates)
        {
            var angleStart = 92;
            var angleEnd = 176;
            var angleStep = profile.AngleStep;
            var powerStep = profile.PowerStep;

            for (var angle = angleStart; angle <= angleEnd; angle += angleStep)
            {
                for (var power = 25; power <= 100; power += powerStep)
                {
                    var simulation = _simulator.SimulateForPlanning(state.Terrain, state.CpuTank, state.PlayerTank, weapon, angle, power, state.Wind, 60 * 7);
                    var enemyPotential = MathF.Max(0, weapon.BlastRadius - simulation.NearestOpponentDistance);
                    var selfRisk = MathF.Max(0, weapon.BlastRadius - simulation.NearestOwnerDistance);
                    var score = (enemyPotential * 10f)
                        - (selfRisk * profile.SelfDamagePenalty)
                        - (weapon.Cost * profile.CostPenalty)
                        + (simulation.StopReason == ProjectileStopReason.TankHit ? 400 : 0);

                    if (weapon.Category == WeaponCategory.Nuclear && selfRisk / MathF.Max(weapon.BlastRadius, 1) > profile.NukeSelfRiskTolerance)
                    {
                        score -= 900;
                    }

                    score += DeterministicNoise(state, weapon.Id, angle, power, 0) * profile.Noise;
                    if (score > best.Score)
                    {
                        var angleNoise = Noise(state, weapon.Id, angle, power, 1, profile.AngleNoise);
                        var powerNoise = Noise(state, weapon.Id, angle, power, 2, profile.PowerNoise);
                        best = new CpuShotPlan(weapon.Id, angle + angleNoise, power + (int)powerNoise, TauntFor(weapon), score, watch.Elapsed.TotalMilliseconds);
                    }
                }
            }
        }

        watch.Stop();
        return best with { PlanningMs = watch.Elapsed.TotalMilliseconds };
    }

    private IEnumerable<WeaponDefinition> CandidateWeapons(Tank tank, MatchSettings settings)
    {
        yield return _weapons.Get(WeaponIds.PeaShell);

        foreach (var weapon in _weapons.All)
        {
            if (weapon.Id == WeaponIds.PeaShell || !tank.HasWeapon(weapon.Id))
            {
                continue;
            }

            if (!settings.EnableNuclearWeapons && weapon.Category == WeaponCategory.Nuclear)
            {
                continue;
            }

            yield return weapon;
        }
    }

    private static float Noise(GameState state, string weaponId, int angle, int power, int salt, float magnitude) =>
        (DeterministicNoise(state, weaponId, angle, power, salt) - 0.5f) * magnitude;

    private static float DeterministicNoise(GameState state, string weaponId, int angle, int power, int salt)
    {
        var hash = HashCode.Combine(state.RandomSeed, state.RoundNumber, state.ShotsFired, weaponId, angle, power, salt);
        return (hash & 0x7fffffff) / (float)int.MaxValue;
    }

    private static string TauntFor(WeaponDefinition weapon)
    {
        if (weapon.Category == WeaponCategory.Nuclear)
        {
            return "I brought sunscreen. And a warhead.";
        }

        return weapon.BehaviorType switch
        {
            WeaponBehaviorType.Dirt => "That hill looks expensive.",
            WeaponBehaviorType.Laser => "Wind is merely a suggestion.",
            _ => "Your crater awaits."
        };
    }
}

public sealed record CpuShotPlan(string WeaponId, float Angle, int Power, string Taunt, float Score, double PlanningMs);

internal sealed record CpuDifficultyProfile(
    int AngleStep,
    int PowerStep,
    float AngleNoise,
    float PowerNoise,
    float Noise,
    float SelfDamagePenalty,
    float CostPenalty,
    float NukeSelfRiskTolerance)
{
    public static CpuDifficultyProfile For(Difficulty difficulty) => difficulty switch
    {
        Difficulty.Rookie => new(8, 12, 16, 20, 120, 8, 0.35f, 0.18f),
        Difficulty.Normal => new(5, 8, 8, 10, 70, 15, 0.18f, 0.28f),
        Difficulty.Veteran => new(4, 6, 4, 6, 38, 20, 0.12f, 0.35f),
        Difficulty.Maniac => new(4, 6, 7, 8, 55, 11, 0.08f, 0.46f),
        Difficulty.Oracle => new(2, 4, 2, 3, 12, 22, 0.05f, 0.32f),
        _ => new(5, 8, 8, 10, 70, 15, 0.18f, 0.28f)
    };
}
