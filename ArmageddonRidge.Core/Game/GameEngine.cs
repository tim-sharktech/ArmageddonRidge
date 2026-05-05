using System.Diagnostics;
using System.Numerics;
using ArmageddonRidge.Core.AI;
using ArmageddonRidge.Core.Content;
using ArmageddonRidge.Core.Models;
using ArmageddonRidge.Core.Physics;
using ArmageddonRidge.Core.Terrain;

namespace ArmageddonRidge.Core.Game;

public sealed class GameEngine
{
    private readonly TerrainGenerator _terrainGenerator = new();
    private readonly TerrainDeformer _terrainDeformer = new();
    private readonly ProjectileSimulator _projectileSimulator = new();
    private readonly ExplosionService _explosionService = new();

    public GameEngine(WeaponCatalog weapons, UpgradeCatalog upgrades)
    {
        Weapons = weapons;
        Upgrades = upgrades;
        Economy = new EconomyService(weapons, upgrades);
        Cpu = new CpuOpponent(weapons, _projectileSimulator);
    }

    public WeaponCatalog Weapons { get; }

    public UpgradeCatalog Upgrades { get; }

    public EconomyService Economy { get; }

    public CpuOpponent Cpu { get; }

    public GameState NewMatch(MatchSettings settings)
    {
        var seed = settings.TerrainSeed ?? Random.Shared.Next(100_000, 999_999);
        var terrain = _terrainGenerator.Generate(seed);
        var player = CreateTank("player", "Ridge Runner", false, 160, terrain, 42);
        var cpu = CreateTank("cpu", "Iron Oracle", true, GameConstants.WorldWidth - 160, terrain, 138);

        player.Cash = settings.StartingCash;
        cpu.Cash = CpuBudget(settings.Difficulty, 1);
        SeedCpuInventory(cpu, settings, 1);

        var state = new GameState
        {
            Terrain = terrain,
            PlayerTank = player,
            CpuTank = cpu,
            Wind = 0,
            RandomSeed = seed,
            Random = new Random(seed),
            Phase = settings.EnableShop ? GamePhase.Shop : GamePhase.Battle
        };

        state.Wind = NextWind(state);
        state.EventLog.Add($"Seed {seed}. The ridge wakes up.");
        return state;
    }

    public void StartBattle(GameState state)
    {
        state.Phase = GamePhase.Battle;
        state.CurrentTurn = TurnOwner.Player;
        state.Wind = NextWind(state);
        ApplyStartOfTurnEffects(state);
    }

    public void StartNextRound(GameState state, MatchSettings settings)
    {
        var seed = state.RandomSeed + (state.RoundNumber * 7919);
        var terrain = _terrainGenerator.Generate(seed);
        state.RoundNumber++;
        state.Phase = settings.EnableShop ? GamePhase.Shop : GamePhase.Battle;
        state.CurrentTurn = TurnOwner.Player;
        state.RadiationZones.Clear();
        state.ShotsFired = 0;
        state.DamageDealtByCpu = 0;
        state.DamageDealtByPlayer = 0;
        state.Terrain.CopyFrom(terrain);
        PlaceTank(state.PlayerTank, terrain, 150);
        PlaceTank(state.CpuTank, terrain, GameConstants.WorldWidth - 150);
        state.PlayerTank.Health = Math.Min(state.PlayerTank.Health + 15, state.PlayerTank.MaxHealth);
        state.CpuTank.Health = GameConstants.StartingHealth;
        state.CpuTank.Shield = settings.Difficulty >= Difficulty.Veteran ? 50 + (state.RoundNumber * 5) : 0;
        state.CpuTank.Cash = CpuBudget(settings.Difficulty, state.RoundNumber);
        SeedCpuInventory(state.CpuTank, settings, state.RoundNumber);
        state.Wind = NextWind(state);
        state.EventLog.Add($"Round {state.RoundNumber}. New ridge. Same grudge.");
    }

    public ShotResolution FireCurrentTurn(GameState state, MatchSettings settings, float? angle = null, int? power = null)
    {
        if (state.Phase != GamePhase.Battle)
        {
            throw new InvalidOperationException("Cannot fire outside the battle phase.");
        }

        var cpuPlanningMs = 0d;
        var owner = state.CurrentTurn == TurnOwner.Player ? state.PlayerTank : state.CpuTank;
        var opponent = state.CurrentTurn == TurnOwner.Player ? state.CpuTank : state.PlayerTank;
        var weaponId = state.CurrentTurn == TurnOwner.Player ? state.SelectedWeaponId : WeaponIds.PeaShell;
        var taunt = string.Empty;

        if (state.CurrentTurn == TurnOwner.Cpu)
        {
            var plan = Cpu.PlanShot(state, settings);
            weaponId = plan.WeaponId;
            angle = plan.Angle;
            power = plan.Power;
            cpuPlanningMs = plan.PlanningMs;
            taunt = plan.Taunt;
        }

        if (!owner.HasWeapon(weaponId))
        {
            weaponId = WeaponIds.PeaShell;
        }

        var weapon = Weapons.Get(weaponId);
        owner.ConsumeWeapon(weaponId);
        owner.TurretAngle = Math.Clamp(angle ?? owner.TurretAngle, 5, 175);
        var shotPower = Math.Clamp(power ?? 65, GameConstants.PowerMin, GameConstants.PowerMax);

        var simulationWatch = Stopwatch.StartNew();
        var simulation = SimulateWeapon(state, owner, opponent, weapon, owner.TurretAngle, shotPower);
        simulationWatch.Stop();

        var terrainWatch = Stopwatch.StartNew();
        var resolvedExplosions = new List<ExplosionResult>(simulation.Explosions.Count);
        var touched = ResolveExplosions(state, owner, opponent, weapon, simulation.Explosions, resolvedExplosions);
        terrainWatch.Stop();

        var events = new List<string>();
        if (!string.IsNullOrWhiteSpace(taunt))
        {
            events.Add(taunt);
            state.EventLog.Add(taunt);
        }

        events.Add($"{owner.Name} fired {weapon.DisplayName}.");
        state.EventLog.Add(events[^1]);

        var playerBeforeSettle = state.PlayerTank.Position.Y;
        var cpuBeforeSettle = state.CpuTank.Position.Y;
        SettleTank(state.PlayerTank, state.Terrain);
        SettleTank(state.CpuTank, state.Terrain);
        ApplyFallDamage(state.PlayerTank, state.PlayerTank.Position.Y - playerBeforeSettle);
        ApplyFallDamage(state.CpuTank, state.CpuTank.Position.Y - cpuBeforeSettle);

        state.ShotsFired++;
        if (state.CurrentTurn == TurnOwner.Player)
        {
            state.DamageDealtByPlayer += resolvedExplosions.Sum(static explosion => explosion.CpuDamage);
        }
        else
        {
            state.DamageDealtByCpu += resolvedExplosions.Sum(static explosion => explosion.PlayerDamage);
        }

        var winner = Winner(state);
        if (winner is not null)
        {
            Economy.AwardRound(state, winner.Value);
            state.Phase = GamePhase.RoundOver;
            events.Add(winner == TurnOwner.Player ? "Victory. The ridge salutes your math." : "Defeat. The CPU is insufferable now.");
        }
        else
        {
            state.CurrentTurn = TurnManager.OpponentOf(state.CurrentTurn);
            state.Wind = NextWind(state);
            ApplyStartOfTurnEffects(state);
        }

        var perf = new PerformanceSample(simulationWatch.Elapsed.TotalMilliseconds, terrainWatch.Elapsed.TotalMilliseconds, cpuPlanningMs, simulation.Trail.Count, touched);
        state.LastPerformance = perf;
        return new ShotResolution(weapon.Id, owner.Id, simulation.Trail, resolvedExplosions, events, winner is not null, winner, perf);
    }

    public bool BuyWeapon(GameState state, string weaponId, int count = 1) => Economy.BuyWeapon(state.PlayerTank, weaponId, count);

    public bool BuyUpgrade(GameState state, UpgradeType upgradeType) => Economy.BuyUpgrade(state.PlayerTank, upgradeType);

    private WeaponSimulation SimulateWeapon(GameState state, Tank owner, Tank opponent, WeaponDefinition weapon, float angle, int power)
    {
        if (weapon.BehaviorType == WeaponBehaviorType.Teleport)
        {
            var x = owner.IsCpu ? GameConstants.WorldWidth - 260 : 260;
            x += state.Random.Next(-80, 81);
            owner.Position = new Vector2(x, state.Terrain.GetSurfaceY(x));
            return new WeaponSimulation([owner.Center], owner.Center, []);
        }

        if (weapon.BehaviorType == WeaponBehaviorType.Laser)
        {
            var radians = angle * MathF.PI / 180f;
            var stepX = MathF.Cos(radians) * 7f;
            var stepY = -MathF.Sin(radians) * 7f;
            var trail = new List<Vector2>();
            var origin = owner.Center;
            var opponentCenter = opponent.Center;
            var hitRadiusSquared = GameConstants.TankWidth * GameConstants.TankWidth;
            for (var i = 0; i < 180; i++)
            {
                var p = origin + new Vector2(stepX * i, stepY * i);
                trail.Add(p);
                if (state.Terrain.IsSolid(p))
                {
                    return new WeaponSimulation(trail, p, [new ExplosionResult(p, weapon.BlastRadius, weapon.TerrainRadius, 0, 0, false, false, [])]);
                }

                if (Vector2.DistanceSquared(p, opponentCenter) < hitRadiusSquared)
                {
                    return new WeaponSimulation(trail, p, [new ExplosionResult(p, weapon.BlastRadius, weapon.TerrainRadius, 0, 0, false, false, [])]);
                }
            }

            return new WeaponSimulation(trail, trail[^1], []);
        }

        var primary = _projectileSimulator.Simulate(state.Terrain, owner, opponent, weapon, angle, power, state.Wind);
        if (weapon.BehaviorType != WeaponBehaviorType.Cluster)
        {
            return new WeaponSimulation(primary.Trail, primary.ImpactPoint, [new ExplosionResult(primary.ImpactPoint, weapon.BlastRadius, weapon.TerrainRadius, 0, 0, weapon.BehaviorType == WeaponBehaviorType.Dirt, weapon.Category == WeaponCategory.Nuclear, [])]);
        }

        var explosions = new List<ExplosionResult>();
        var count = Math.Max(weapon.ClusterCount, 3);
        for (var i = 0; i < count; i++)
        {
            var offset = (i - ((count - 1) / 2f)) * 20f;
            var center = primary.ImpactPoint + new Vector2(offset, -MathF.Abs(offset) * 0.25f);
            explosions.Add(new ExplosionResult(center, weapon.BlastRadius, weapon.TerrainRadius, 0, 0, false, false, []));
        }

        return new WeaponSimulation(primary.Trail, primary.ImpactPoint, explosions);
    }

    private int ResolveExplosions(GameState state, Tank owner, Tank opponent, WeaponDefinition weapon, IReadOnlyList<ExplosionResult> pending, List<ExplosionResult> resolvedExplosions)
    {
        var touched = 0;
        for (var i = 0; i < pending.Count; i++)
        {
            var center = pending[i].Center;
            var resolved = _explosionService.Resolve(weapon, center, owner, opponent, state.RadiationZones);
            resolvedExplosions.Add(resolved);

            if (weapon.TerrainRadius > 0)
            {
                touched += _terrainDeformer.Apply(state.Terrain, weapon, center);
            }
        }

        return touched;
    }

    private static Tank CreateTank(string id, string name, bool isCpu, float x, TerrainMask terrain, float angle)
    {
        var tank = new Tank
        {
            Id = id,
            Name = name,
            IsCpu = isCpu,
            TurretAngle = angle,
            Position = new Vector2(x, terrain.GetSurfaceY(x))
        };
        tank.AddWeapon(WeaponIds.PeaShell, -1);
        return tank;
    }

    private static void PlaceTank(Tank tank, TerrainMask terrain, float preferredX)
    {
        tank.Position = new Vector2(preferredX, terrain.GetSurfaceY(preferredX));
        tank.TurretAngle = tank.IsCpu ? 138 : 42;
    }

    private static void SettleTank(Tank tank, TerrainMask terrain)
    {
        if (terrain.TryGetNearestVisibleSurface(tank.Position.X, out var surface))
        {
            tank.Position = surface;
            return;
        }

        tank.Health = 0;
        tank.Position = new Vector2(
            Math.Clamp(tank.Position.X, GameConstants.TankWidth / 2f, terrain.Width - (GameConstants.TankWidth / 2f)),
            terrain.Height - 1);
    }

    private static void ApplyFallDamage(Tank tank, float fallDistance)
    {
        if (fallDistance <= 70)
        {
            return;
        }

        if (tank.HasParachute)
        {
            tank.HasParachute = false;
            return;
        }

        tank.Health -= (int)MathF.Ceiling((fallDistance - 70) * 0.2f);
    }

    private void ApplyStartOfTurnEffects(GameState state)
    {
        var active = state.CurrentTurn == TurnOwner.Player ? state.PlayerTank : state.CpuTank;
        _explosionService.ApplyRadiation(active, state.RadiationZones);
        _explosionService.TickRadiation(state.RadiationZones);
    }

    private TurnOwner? Winner(GameState state)
    {
        if (state.PlayerTank.IsDestroyed && state.CpuTank.IsDestroyed)
        {
            return state.CurrentTurn;
        }

        if (state.CpuTank.IsDestroyed)
        {
            return TurnOwner.Player;
        }

        if (state.PlayerTank.IsDestroyed)
        {
            return TurnOwner.Cpu;
        }

        return null;
    }

    private static int NextWind(GameState state)
    {
        var firstGust = state.Random.Next(GameConstants.WindMin, GameConstants.WindMax + 1);
        var secondGust = state.Random.Next(GameConstants.WindMin, GameConstants.WindMax + 1);
        return (int)MathF.Round((firstGust + secondGust) / 2f);
    }

    private int CpuBudget(Difficulty difficulty, int round) => difficulty switch
    {
        Difficulty.Rookie => 250 + (round * 110),
        Difficulty.Normal => 450 + (round * 170),
        Difficulty.Veteran => 650 + (round * 240),
        Difficulty.Maniac => 850 + (round * 320),
        Difficulty.Oracle => 1000 + (round * 380),
        _ => 450 + (round * 170)
    };

    private void SeedCpuInventory(Tank cpu, MatchSettings settings, int round)
    {
        cpu.Inventory.Clear();
        cpu.AddWeapon(WeaponIds.PeaShell, -1);
        if (round >= 2)
        {
            cpu.AddWeapon(WeaponIds.HeavyShell, 2);
        }

        if (round >= 3)
        {
            cpu.AddWeapon(WeaponIds.Excavator, 1);
        }

        if (round >= 4)
        {
            cpu.AddWeapon(WeaponIds.ClusterPopper, 1);
        }

        if (settings.EnableNuclearWeapons && round >= 5 && settings.Difficulty >= Difficulty.Maniac)
        {
            cpu.AddWeapon(WeaponIds.TacticalNuke, 1);
        }
    }
}

internal sealed record WeaponSimulation(IReadOnlyList<Vector2> Trail, Vector2 ImpactPoint, IReadOnlyList<ExplosionResult> Explosions);
