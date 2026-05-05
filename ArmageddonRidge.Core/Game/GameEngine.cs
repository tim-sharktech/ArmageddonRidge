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
        var cpuProfile = CpuRivalProfile.ForSeed(seed);
        var cpu = CreateTank("cpu", cpuProfile.TankName, true, GameConstants.WorldWidth - 160, terrain, 138);

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
        state.EventLog.Add($"Rival channel: {cpuProfile.DisplayName}.");
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

        var intercepted = false;
        Vector2? interceptPoint = null;
        var terrainWatch = Stopwatch.StartNew();
        var resolvedExplosions = new List<ExplosionResult>(simulation.Explosions.Count);
        int touched;
        if (owner.IsCpu
            && opponent.Upgrades.Contains(UpgradeType.PatriotBattery)
            && PatriotDefense.ShouldIntercept(opponent, simulation.Explosions))
        {
            intercepted = true;
            interceptPoint = PatriotDefense.InterceptPoint(opponent, simulation.Trail);
            opponent.Upgrades.Remove(UpgradeType.PatriotBattery);
            resolvedExplosions.Add(new ExplosionResult(interceptPoint.Value, 34, 0, 0, 0, false, false, [], ShotVisualKind.PatriotIntercept));
            touched = 0;
        }
        else
        {
            touched = ResolveExplosions(state, owner, opponent, weapon, simulation.Explosions, resolvedExplosions);
        }

        terrainWatch.Stop();

        var events = new List<string>();
        if (!string.IsNullOrWhiteSpace(taunt))
        {
            events.Add(taunt);
            state.EventLog.Add(taunt);
        }

        events.Add($"{owner.Name} fired {weapon.DisplayName}.");
        state.EventLog.Add(events[^1]);
        if (intercepted)
        {
            events.Add("Patriot Battery intercepted the incoming shot.");
            state.EventLog.Add(events[^1]);
        }

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
        return new ShotResolution(weapon.Id, owner.Id, simulation.Trail, resolvedExplosions, events, winner is not null, winner, perf, VisualKindFor(weapon), intercepted, interceptPoint);
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

        if (weapon.Id == WeaponIds.DarkEagle)
        {
            return SimulateGuidedDarkEagle(owner, opponent, weapon);
        }

        var primary = _projectileSimulator.Simulate(state.Terrain, owner, opponent, weapon, angle, power, state.Wind);
        if (weapon.BehaviorType == WeaponBehaviorType.MultiStagePenetrator)
        {
            return SimulateMultiStagePenetrator(primary, owner, weapon);
        }

        if (weapon.BehaviorType != WeaponBehaviorType.Cluster && weapon.BehaviorType != WeaponBehaviorType.DroneSwarm)
        {
            return new WeaponSimulation(primary.Trail, primary.ImpactPoint, [new ExplosionResult(primary.ImpactPoint, weapon.BlastRadius, weapon.TerrainRadius, 0, 0, weapon.BehaviorType == WeaponBehaviorType.Dirt, weapon.Category == WeaponCategory.Nuclear, [], VisualKindFor(weapon))]);
        }

        var explosions = new List<ExplosionResult>();
        var count = Math.Max(weapon.ClusterCount, 3);
        for (var i = 0; i < count; i++)
        {
            var spacing = weapon.BehaviorType == WeaponBehaviorType.DroneSwarm ? 28f : 20f;
            var offset = (i - ((count - 1) / 2f)) * spacing;
            var wave = weapon.BehaviorType == WeaponBehaviorType.DroneSwarm ? ((i % 2) == 0 ? -10f : 6f) : 0f;
            var center = primary.ImpactPoint + new Vector2(offset, (-MathF.Abs(offset) * 0.25f) + wave);
            explosions.Add(new ExplosionResult(center, weapon.BlastRadius, weapon.TerrainRadius, 0, 0, false, false, [], VisualKindFor(weapon)));
        }

        return new WeaponSimulation(primary.Trail, primary.ImpactPoint, explosions);
    }

    private static WeaponSimulation SimulateGuidedDarkEagle(Tank owner, Tank opponent, WeaponDefinition weapon)
    {
        var origin = GuidedLaunchPoint(owner);
        var target = opponent.Center;
        var distance = Vector2.Distance(origin, target);
        var apexY = Math.Clamp(Math.Min(origin.Y, target.Y) - Math.Clamp(distance * 0.22f, 130f, 230f), 48f, GameConstants.WorldHeight - 1);
        var apexX = origin.X + ((target.X - origin.X) * 0.44f);
        var apex = new Vector2(apexX, apexY);
        var ascentSteps = Math.Clamp((int)(distance / 24f), 24, 48);
        var diveSteps = Math.Clamp((int)(distance / 18f), 30, 62);
        var trail = new List<Vector2>(ascentSteps + diveSteps + 1);

        for (var i = 0; i <= ascentSteps; i++)
        {
            var t = i / (float)ascentSteps;
            var ignitionWobble = new Vector2(0, MathF.Sin(t * MathF.PI * 3f) * 4f * (1f - t));
            trail.Add(ClampToWorld(Vector2.Lerp(origin, apex, t) + ignitionWobble));
        }

        for (var i = 1; i <= diveSteps; i++)
        {
            var t = i / (float)diveSteps;
            trail.Add(ClampToWorld(Vector2.Lerp(apex, target, t)));
        }

        return new WeaponSimulation(
            trail,
            target,
            [new ExplosionResult(target, weapon.BlastRadius, weapon.TerrainRadius, 0, 0, false, false, [], ShotVisualKind.Missile)]);
    }

    private static WeaponSimulation SimulateMultiStagePenetrator(ProjectileSimulation primary, Tank owner, WeaponDefinition weapon)
    {
        var trail = new List<Vector2>(primary.Trail.Count + 18);
        trail.AddRange(primary.Trail);
        if (trail.Count == 0)
        {
            trail.Add(primary.ImpactPoint);
        }

        var impact = primary.ImpactPoint;
        var previous = trail.Count > 1 ? trail[^2] : owner.Center;
        var direction = impact - previous;
        if (direction.LengthSquared() < 0.001f)
        {
            direction = new Vector2(owner.IsCpu ? -0.45f : 0.45f, 0.9f);
        }

        direction = Vector2.Normalize(new Vector2(direction.X * 0.42f, MathF.Max(MathF.Abs(direction.Y), 0.68f)));
        var firstTriggerIndex = Math.Max(0, trail.Count - 1);
        const int burrowSteps = 18;
        const float burrowDistance = 96f;
        for (var i = 1; i <= burrowSteps; i++)
        {
            var t = i / (float)burrowSteps;
            var wander = new Vector2(MathF.Sin(t * MathF.PI) * (owner.IsCpu ? -6f : 6f), t * t * 14f);
            trail.Add(ClampToWorld(impact + (direction * burrowDistance * t) + wander));
        }

        var primaryExplosion = new ExplosionResult(
            impact,
            MathF.Max(30f, weapon.BlastRadius * 0.52f),
            MathF.Max(50f, weapon.TerrainRadius * 0.44f),
            0,
            0,
            false,
            false,
            [],
            ShotVisualKind.PenetratorPrimary,
            MathF.Max(36f, weapon.MaxDamage * 0.46f),
            firstTriggerIndex);

        var secondaryExplosion = new ExplosionResult(
            trail[^1],
            weapon.BlastRadius,
            weapon.TerrainRadius,
            0,
            0,
            false,
            false,
            [],
            ShotVisualKind.PenetratorSecondary);

        return new WeaponSimulation(trail, trail[^1], [primaryExplosion, secondaryExplosion]);
    }

    private int ResolveExplosions(GameState state, Tank owner, Tank opponent, WeaponDefinition weapon, IReadOnlyList<ExplosionResult> pending, List<ExplosionResult> resolvedExplosions)
    {
        var touched = 0;
        for (var i = 0; i < pending.Count; i++)
        {
            var pendingExplosion = pending[i];
            var center = pendingExplosion.Center;
            var effectiveWeapon = weapon with
            {
                MaxDamage = pendingExplosion.MaxDamageOverride > 0 ? pendingExplosion.MaxDamageOverride : weapon.MaxDamage,
                BlastRadius = pendingExplosion.DamageRadius > 0 ? pendingExplosion.DamageRadius : weapon.BlastRadius,
                TerrainRadius = pendingExplosion.TerrainRadius > 0 ? pendingExplosion.TerrainRadius : weapon.TerrainRadius
            };
            var resolved = _explosionService.Resolve(effectiveWeapon, center, owner, opponent, state.RadiationZones, pendingExplosion.VisualKind) with
            {
                MaxDamageOverride = pendingExplosion.MaxDamageOverride,
                TriggerTrailIndex = pendingExplosion.TriggerTrailIndex
            };
            resolvedExplosions.Add(resolved);

            if (effectiveWeapon.TerrainRadius > 0)
            {
                touched += _terrainDeformer.Apply(state.Terrain, effectiveWeapon, center);
            }
        }

        return touched;
    }

    private static Vector2 ClampToWorld(Vector2 point) => new(
        Math.Clamp(point.X, 0, GameConstants.WorldWidth - 1),
        Math.Clamp(point.Y, 0, GameConstants.WorldHeight - 1));

    private static Vector2 GuidedLaunchPoint(Tank tank)
    {
        var radians = tank.TurretAngle * (MathF.PI / 180f);
        return ClampToWorld(tank.Center + new Vector2(MathF.Cos(radians) * 32f, -MathF.Sin(radians) * 32f));
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

        if (round >= 4 && settings.Difficulty >= Difficulty.Normal)
        {
            cpu.AddWeapon(WeaponIds.ShahedDroneSwarm, 1);
        }

        if (round >= 5 && settings.Difficulty >= Difficulty.Veteran)
        {
            cpu.AddWeapon(WeaponIds.DarkEagle, 1);
        }

        if (round >= 6 && settings.Difficulty >= Difficulty.Veteran)
        {
            cpu.AddWeapon(WeaponIds.Gbu57Mop, 1);
        }

        if (settings.EnableNuclearWeapons && round >= 5 && settings.Difficulty >= Difficulty.Maniac)
        {
            cpu.AddWeapon(WeaponIds.TacticalNuke, 1);
        }
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

internal sealed record WeaponSimulation(IReadOnlyList<Vector2> Trail, Vector2 ImpactPoint, IReadOnlyList<ExplosionResult> Explosions);

internal readonly record struct CpuRivalProfile(string DisplayName, string TankName)
{
    public static CpuRivalProfile ForSeed(int seed) => (Math.Abs(seed) % 3) switch
    {
        0 => new("Volga Circuit rival, Russia-inspired arcade faction", "Volga Redline"),
        1 => new("Zagros Signal rival, Iran-inspired arcade faction", "Zagros Signal"),
        _ => new("Paektu Spark rival, North Korea-inspired arcade faction", "Paektu Spark")
    };
}
