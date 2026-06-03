using System.Diagnostics;
using System.Numerics;
using ArmageddonRidge.Core.AI;
using ArmageddonRidge.Core.Content;
using ArmageddonRidge.Core.Models;
using ArmageddonRidge.Core.Physics;
using ArmageddonRidge.Core.Terrain;

namespace ArmageddonRidge.Core.Game;

/// <summary>
/// Coordinates deterministic match setup, shot resolution, turns, economy, and round progression.
/// </summary>
/// <param name="weapons">Weapon catalog used by player, shop, and CPU decisions.</param>
/// <param name="upgrades">Upgrade catalog used by shop and economy decisions.</param>
public sealed class GameEngine(WeaponCatalog weapons, UpgradeCatalog upgrades)
{
    private readonly TerrainGenerator _terrainGenerator = new();
    private readonly TerrainDeformer _terrainDeformer = new();
    private readonly ProjectileSimulator _projectileSimulator = new();
    private readonly ExplosionService _explosionService = new();

    /// <summary>
    /// Gets the weapon catalog used by this engine.
    /// </summary>
    public WeaponCatalog Weapons { get; } = weapons;

    /// <summary>
    /// Gets the upgrade catalog used by this engine.
    /// </summary>
    public UpgradeCatalog Upgrades { get; } = upgrades;

    /// <summary>
    /// Gets the economy service for shop purchases and rewards.
    /// </summary>
    public EconomyService Economy { get; } = new(weapons, upgrades);

    /// <summary>
    /// Gets the CPU planner used for CPU-controlled turns.
    /// </summary>
    public CpuOpponent Cpu { get; } = new(weapons, new ProjectileSimulator());

    /// <summary>
    /// Creates a new deterministic match from player-selected settings.
    /// </summary>
    public GameState NewMatch(MatchSettings settings)
    {
        var seed = settings.TerrainSeed ?? Random.Shared.Next(100_000, 999_999);
        var terrain = _terrainGenerator.Generate(seed);
        var player = CreateTank("player", "Ridge Runner", false, 160, terrain, 42);
        var cpuProfile = CpuRivalProfile.ForSeed(seed);
        var cpu = CreateTank("cpu", cpuProfile.TankName, true, GameConstants.WorldWidth - 160, terrain, 138);
        SetTankHealth(cpu, CpuHealthFor(settings.Difficulty));

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

    /// <summary>
    /// Leaves the shop and begins a battle round.
    /// </summary>
    public void StartBattle(GameState state)
    {
        state.Phase = GamePhase.Battle;
        state.CurrentTurn = TurnOwner.Player;
        state.Wind = NextWind(state);
        ApplyStartOfTurnEffects(state);
    }

    /// <summary>
    /// Regenerates terrain and prepares the next round while preserving player progression.
    /// </summary>
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
        SetTankHealth(state.CpuTank, CpuHealthFor(settings.Difficulty));
        state.CpuTank.Shield = settings.Difficulty >= Difficulty.Veteran ? 50 + (state.RoundNumber * 5) : 0;
        state.CpuTank.Cash = CpuBudget(settings.Difficulty, state.RoundNumber);
        SeedCpuInventory(state.CpuTank, settings, state.RoundNumber);
        state.Wind = NextWind(state);
        state.EventLog.Add($"Round {state.RoundNumber}. New ridge. Same grudge.");
    }

    /// <summary>
    /// Fires the current turn's weapon and fully resolves projectile, explosions, terrain, and turn handoff.
    /// </summary>
    public ShotResolution FireCurrentTurn(GameState state, MatchSettings settings, float? angle = null, int? power = null) =>
        FireCurrentTurnCore(state, settings, angle, power, null);

    /// <summary>
    /// Fires a CPU turn using an already-computed plan.
    /// </summary>
    public ShotResolution FirePlannedCpuTurn(GameState state, MatchSettings settings, CpuShotPlan plan)
    {
        if (state.CurrentTurn != TurnOwner.Cpu)
            throw new InvalidOperationException("Cannot use a CPU plan outside the CPU turn.");

        return FireCurrentTurnCore(state, settings, plan.Angle, plan.Power, plan);
    }

    /// <summary>
    /// Plans the current CPU turn while yielding periodically to avoid monopolizing browser animation.
    /// </summary>
    public ValueTask<CpuShotPlan> PlanCurrentCpuTurnAsync(GameState state, MatchSettings settings, CancellationToken cancellationToken = default)
    {
        if (state.Phase != GamePhase.Battle || state.CurrentTurn != TurnOwner.Cpu)
            throw new InvalidOperationException("Cannot plan a CPU shot outside the CPU battle turn.");

        return Cpu.PlanShotAsync(state, settings, cancellationToken);
    }

    private ShotResolution FireCurrentTurnCore(GameState state, MatchSettings settings, float? angle, int? power, CpuShotPlan? plannedCpuShot)
    {
        if (state.Phase != GamePhase.Battle)
            throw new InvalidOperationException("Cannot fire outside the battle phase.");

        var cpuPlanningMs = 0d;
        var owner = state.CurrentTurn == TurnOwner.Player ? state.PlayerTank : state.CpuTank;
        var opponent = state.CurrentTurn == TurnOwner.Player ? state.CpuTank : state.PlayerTank;
        var weaponId = state.CurrentTurn == TurnOwner.Player ? state.SelectedWeaponId : WeaponIds.PeaShell;
        var taunt = string.Empty;

        if (state.CurrentTurn == TurnOwner.Cpu)
        {
            var plan = plannedCpuShot ?? Cpu.PlanShot(state, settings);
            weaponId = plan.WeaponId;
            angle = plan.Angle;
            power = plan.Power;
            cpuPlanningMs = plan.PlanningMs;
            taunt = plan.Taunt;
        }

        if (!owner.HasWeapon(weaponId))
            weaponId = WeaponIds.PeaShell;

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
            && HasPatriotInterceptor(opponent)
            && PatriotDefense.ShouldIntercept(opponent, simulation.Explosions, simulation.Trail))
        {
            intercepted = true;
            interceptPoint = PatriotDefense.InterceptPoint(opponent, simulation.Trail);
            ConsumePatriotInterceptor(opponent);
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
        var playerBuriedByDirt = IsCoveredByDirt(state.PlayerTank, state.Terrain, resolvedExplosions);
        var cpuBuriedByDirt = IsCoveredByDirt(state.CpuTank, state.Terrain, resolvedExplosions);
        SettleTank(state.PlayerTank, state.Terrain, playerBuriedByDirt);
        SettleTank(state.CpuTank, state.Terrain, cpuBuriedByDirt);
        ApplyFallDamage(state.PlayerTank, state.PlayerTank.Position.Y - playerBeforeSettle);
        ApplyFallDamage(state.CpuTank, state.CpuTank.Position.Y - cpuBeforeSettle);

        state.ShotsFired++;
        if (state.CurrentTurn == TurnOwner.Player)
        {
            var damage = 0f;
            for (var i = 0; i < resolvedExplosions.Count; i++)
            {
                damage += resolvedExplosions[i].CpuDamage;
            }

            state.DamageDealtByPlayer += damage;
        }
        else
        {
            var damage = 0f;
            for (var i = 0; i < resolvedExplosions.Count; i++)
            {
                damage += resolvedExplosions[i].PlayerDamage;
            }

            state.DamageDealtByCpu += damage;
        }

        var winner = Winner(state);
        if (winner is null)
        {
            state.CurrentTurn = TurnManager.OpponentOf(state.CurrentTurn);
            state.Wind = NextWind(state);
            ApplyStartOfTurnEffects(state);
            winner = Winner(state);
        }

        if (winner is not null)
        {
            Economy.AwardRound(state, winner.Value);
            state.Phase = GamePhase.RoundOver;
            events.Add(winner == TurnOwner.Player ? "Victory. The ridge salutes your math." : "Defeat. The CPU is insufferable now.");
        }

        var perf = new PerformanceSample(simulationWatch.Elapsed.TotalMilliseconds, terrainWatch.Elapsed.TotalMilliseconds, cpuPlanningMs, simulation.Trail.Count, touched);
        state.LastPerformance = perf;
        return new ShotResolution(weapon.Id, owner.Id, simulation.Trail, resolvedExplosions, events, winner is not null, winner, perf, VisualKindFor(weapon), intercepted, interceptPoint);
    }

    /// <summary>
    /// Attempts to buy a weapon for the player tank.
    /// </summary>
    public bool BuyWeapon(GameState state, string weaponId, int count = 1) => Economy.BuyWeapon(state.PlayerTank, weaponId, count);

    /// <summary>
    /// Attempts to buy and apply an upgrade for the player tank.
    /// </summary>
    public bool BuyUpgrade(GameState state, UpgradeType upgradeType) => Economy.BuyUpgrade(state.PlayerTank, upgradeType);

    private static bool HasPatriotInterceptor(Tank tank) =>
        tank.PatriotBatteryCharges > 0 || tank.Upgrades.Contains(UpgradeType.PatriotBattery);

    private static void ConsumePatriotInterceptor(Tank tank)
    {
        if (tank.PatriotBatteryCharges > 0)
            tank.PatriotBatteryCharges--;

        if (tank.PatriotBatteryCharges <= 0)
            tank.Upgrades.Remove(UpgradeType.PatriotBattery);
    }

    /// <summary>
    /// Produces the approximate player shot preview used by the targeting computer.
    /// </summary>
    public IReadOnlyList<Vector2> PreviewPlayerShot(GameState state, float angle, int power)
    {
        if (state.Phase != GamePhase.Battle || state.CurrentTurn != TurnOwner.Player)
            return [];

        var weaponId = state.PlayerTank.HasWeapon(state.SelectedWeaponId)
            ? state.SelectedWeaponId
            : WeaponIds.PeaShell;
        var weapon = Weapons.Get(weaponId);
        if (weapon.BehaviorType is WeaponBehaviorType.Teleport or WeaponBehaviorType.Laser)
            return [];

        return weapon.Id == WeaponIds.DarkEagle
            ? SimulateGuidedDarkEagle(state.PlayerTank, state.CpuTank, weapon).Trail
            : _projectileSimulator.Simulate(state.Terrain, state.PlayerTank, state.CpuTank, weapon, angle, power, state.Wind, 60 * 6).Trail;
    }

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
            return SimulateLaser(state.Terrain, owner, opponent, weapon);

        if (weapon.Id == WeaponIds.DarkEagle)
            return SimulateGuidedDarkEagle(owner, opponent, weapon);

        var primary = _projectileSimulator.Simulate(state.Terrain, owner, opponent, weapon, angle, power, state.Wind);
        if (weapon.BehaviorType == WeaponBehaviorType.MultiStagePenetrator)
            return SimulateMultiStagePenetrator(primary, owner, weapon);

        if (weapon.Id == WeaponIds.SplitterMirv)
            return SimulateSplitterMirv(primary, weapon);

        if (weapon.BehaviorType != WeaponBehaviorType.Cluster && weapon.BehaviorType != WeaponBehaviorType.DroneSwarm)
            return new WeaponSimulation(primary.Trail, primary.ImpactPoint, [new ExplosionResult(primary.ImpactPoint, weapon.BlastRadius, weapon.TerrainRadius, 0, 0, weapon.BehaviorType == WeaponBehaviorType.Dirt, weapon.Category == WeaponCategory.Nuclear, [], VisualKindFor(weapon))]);

        var droneSwarm = weapon.BehaviorType == WeaponBehaviorType.DroneSwarm;
        var droneRandom = droneSwarm ? new Random(ShotSeed(state, owner, weapon)) : null;
        var explosions = new List<ExplosionResult>();
        var count = droneSwarm ? droneRandom!.Next(3, Math.Max(4, weapon.ClusterCount + 1)) : Math.Max(weapon.ClusterCount, 3);
        for (var i = 0; i < count; i++)
        {
            var spacing = droneSwarm ? 26f : 20f;
            var offset = (i - ((count - 1) / 2f)) * spacing;
            var wave = droneSwarm ? ((i % 2) == 0 ? -10f : 6f) : 0f;
            var chaosX = droneSwarm ? droneRandom!.NextSingle() * 58f - 29f : 0f;
            var chaosY = droneSwarm ? droneRandom!.NextSingle() * 42f - 18f : 0f;
            var center = primary.ImpactPoint + new Vector2(offset + chaosX, (-MathF.Abs(offset) * 0.25f) + wave + chaosY);
            explosions.Add(new ExplosionResult(center, weapon.BlastRadius, weapon.TerrainRadius, 0, 0, false, false, [], VisualKindFor(weapon)));
        }

        return new WeaponSimulation(primary.Trail, primary.ImpactPoint, explosions);
    }

    private static WeaponSimulation SimulateSplitterMirv(ProjectileSimulation primary, WeaponDefinition weapon)
    {
        var count = Math.Max(weapon.ClusterCount, 7);
        var explosions = new List<ExplosionResult>(count);
        for (var i = 0; i < count; i++)
        {
            var lane = i - ((count - 1) / 2f);
            var xOffset = lane * 34f;
            var yOffset = (MathF.Abs(lane) * -9f) + ((i % 2) == 0 ? -5f : 7f);
            var center = ClampToWorld(primary.ImpactPoint + new Vector2(xOffset, yOffset));
            explosions.Add(new ExplosionResult(center, weapon.BlastRadius, weapon.TerrainRadius, 0, 0, false, false, [], ShotVisualKind.Ballistic));
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

    private static WeaponSimulation SimulateLaser(TerrainMask terrain, Tank owner, Tank opponent, WeaponDefinition weapon)
    {
        const float BeamStep = 4f;
        const float BeamPadding = 2f;

        var origin = LaserOrigin(owner, opponent);
        var target = opponent.Center;
        var delta = target - origin;
        var distance = delta.Length();
        if (distance <= 0.001f) return new WeaponSimulation([target], target, [LaserExplosion(target, weapon)]);

        var steps = Math.Max(1, (int)MathF.Ceiling(distance / BeamStep));
        var trail = new List<Vector2>(steps + 1) { origin };
        if (terrain.IsSolid(origin)) return new WeaponSimulation(trail, origin, [LaserExplosion(origin, weapon)]);

        var previous = origin;
        for (var i = 1; i <= steps; i++)
        {
            var t = i / (float)steps;
            var point = origin + (delta * t);
            trail.Add(point);
            if (terrain.IsSolid(point))
            {
                var hit = RefineTerrainHit(terrain, previous, point);
                trail[^1] = hit;
                return new WeaponSimulation(trail, hit, [LaserExplosion(hit, weapon)]);
            }

            previous = point;
        }

        if (ProjectileSimulator.SweptHitsTankOrShield(origin, target, opponent, terrain, BeamPadding, out var laserHit, out _))
        {
            trail[^1] = laserHit;
            return new WeaponSimulation(trail, laserHit, [LaserExplosion(laserHit, weapon)]);
        }

        return new WeaponSimulation(trail, trail[^1], []);
    }

    private static ExplosionResult LaserExplosion(Vector2 center, WeaponDefinition weapon) =>
        new(center, weapon.BlastRadius, weapon.TerrainRadius, 0, 0, false, false, [], ShotVisualKind.Laser);

    private static Vector2 LaserOrigin(Tank owner, Tank opponent)
    {
        var delta = opponent.Center - owner.Center;
        if (delta.LengthSquared() <= 0.001f) return owner.Center;

        return ClampToWorld(owner.Center + (Vector2.Normalize(delta) * 32f));
    }

    private static Vector2 RefineTerrainHit(TerrainMask terrain, Vector2 clear, Vector2 solid)
    {
        for (var i = 0; i < 6; i++)
        {
            var midpoint = (clear + solid) * 0.5f;
            if (terrain.IsSolid(midpoint)) solid = midpoint;
            else clear = midpoint;
        }

        return solid;
    }

    private static WeaponSimulation SimulateMultiStagePenetrator(ProjectileSimulation primary, Tank owner, WeaponDefinition weapon)
    {
        var trail = new List<Vector2>(primary.Trail.Count + 18);
        trail.AddRange(primary.Trail);
        if (trail.Count == 0)
            trail.Add(primary.ImpactPoint);

        var impact = primary.ImpactPoint;
        var previous = trail.Count > 1 ? trail[^2] : owner.Center;
        var direction = impact - previous;
        if (direction.LengthSquared() < 0.001f)
            direction = new Vector2(owner.IsCpu ? -0.45f : 0.45f, 0.9f);

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
                touched += _terrainDeformer.Apply(state.Terrain, effectiveWeapon, center);
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

    private static int ShotSeed(GameState state, Tank owner, WeaponDefinition weapon)
    {
        unchecked
        {
            // Visual submunitions need deterministic variation without consuming the
            // match RNG that controls terrain, wind, CPU planning, and progression.
            var hash = state.RandomSeed;
            hash = (hash * 397) ^ state.RoundNumber;
            hash = (hash * 397) ^ state.ShotsFired;
            hash = (hash * 397) ^ (owner.IsCpu ? 17 : 31);
            for (var i = 0; i < weapon.Id.Length; i++)
            {
                hash = (hash * 33) ^ weapon.Id[i];
            }

            return hash;
        }
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

    private static void SettleTank(Tank tank, TerrainMask terrain, bool preserveBuriedPosition = false)
    {
        if (terrain.TryGetNearestVisibleSurface(tank.Position.X, out var surface))
        {
            if (preserveBuriedPosition && surface.Y < tank.Position.Y)
            {
                tank.Position = new Vector2(
                    Math.Clamp(tank.Position.X, GameConstants.TankWidth / 2f, terrain.Width - (GameConstants.TankWidth / 2f)),
                    tank.Position.Y);
                return;
            }

            tank.Position = surface;
            return;
        }

        tank.Health = 0;
        tank.Position = new Vector2(
            Math.Clamp(tank.Position.X, GameConstants.TankWidth / 2f, terrain.Width - (GameConstants.TankWidth / 2f)),
            terrain.Height - 1);
    }

    private static bool IsCoveredByDirt(Tank tank, TerrainMask terrain, IReadOnlyList<ExplosionResult> explosions)
    {
        var dirtAdded = false;
        for (var i = 0; i < explosions.Count; i++)
        {
            if (!explosions[i].DirtAdded) continue;
            dirtAdded = true;
            break;
        }

        if (!dirtAdded) return false;

        var surfaceY = terrain.GetSurfaceY(tank.Position.X);
        return surfaceY < tank.Position.Y - 4f;
    }

    private static void ApplyFallDamage(Tank tank, float fallDistance)
    {
        if (fallDistance <= 70) return;

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
            return state.CurrentTurn;

        if (state.CpuTank.IsDestroyed)
            return TurnOwner.Player;

        if (state.PlayerTank.IsDestroyed)
            return TurnOwner.Cpu;

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

    private static int CpuHealthFor(Difficulty difficulty) => difficulty switch
    {
        Difficulty.Rookie => 50,
        Difficulty.Normal => GameConstants.StartingHealth,
        Difficulty.Veteran => 85,
        Difficulty.Maniac => 90,
        Difficulty.Oracle => 100,
        _ => GameConstants.StartingHealth
    };

    private static void SetTankHealth(Tank tank, int health)
    {
        var clampedHealth = Math.Max(0, health);
        tank.MaxHealth = clampedHealth;
        tank.Health = clampedHealth;
    }

    private void SeedCpuInventory(Tank cpu, MatchSettings settings, int round)
    {
        cpu.Inventory.Clear();
        cpu.AddWeapon(WeaponIds.PeaShell, -1);
        if (round >= 2)
            cpu.AddWeapon(WeaponIds.HeavyShell, 2);

        if (round >= 3)
            cpu.AddWeapon(WeaponIds.Excavator, 1);

        if (round >= 4)
            cpu.AddWeapon(WeaponIds.ClusterPopper, 1);

        if (round >= 4 && settings.Difficulty >= Difficulty.Normal)
            cpu.AddWeapon(WeaponIds.ShahedDroneSwarm, 1);

        if (round >= 5 && settings.Difficulty >= Difficulty.Veteran)
            cpu.AddWeapon(WeaponIds.DarkEagle, 1);

        if (round >= 6 && settings.Difficulty >= Difficulty.Veteran)
            cpu.AddWeapon(WeaponIds.Gbu57Mop, 1);

        if (settings.EnableNuclearWeapons && round >= 5 && settings.Difficulty >= Difficulty.Maniac)
            cpu.AddWeapon(WeaponIds.TacticalNuke, 1);
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
