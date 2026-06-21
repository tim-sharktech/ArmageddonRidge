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
    private readonly TerrainSlumpingService _terrainSlumping = new();
    private readonly ProjectileSimulator _projectileSimulator = new();
    private readonly ExplosionService _explosionService = new();
    private readonly VisualPhysicsPayloadService _visualPhysics = new();

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
        if (settings.EnableCivilianStructures)
            SeedCivilianStructures(state, terrain, seed);
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
        state.EventLog.Add($"Round {state.RoundNumber}. New ridge. Same grudge.");
        EndRoundIfWon(state);
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
        state.CivilianStructures.Clear();
        state.ShotsFired = 0;
        state.DamageDealtByCpu = 0;
        state.DamageDealtByPlayer = 0;
        state.CivilianPenaltyByCpu = 0;
        state.CivilianPenaltyByPlayer = 0;
        state.Terrain.CopyFrom(terrain);
        PlaceTank(state.PlayerTank, terrain, 150);
        PlaceTank(state.CpuTank, terrain, GameConstants.WorldWidth - 150);
        if (settings.EnableCivilianStructures)
            SeedCivilianStructures(state, terrain, seed);
        state.PlayerTank.Health = state.PlayerTank.MaxHealth;
        SetTankHealth(state.CpuTank, CpuHealthFor(settings.Difficulty));
        state.CpuTank.Shield = settings.Difficulty >= Difficulty.Veteran ? 50 + (state.RoundNumber * 5) : 0;
        state.CpuTank.Cash = CpuBudget(settings.Difficulty, state.RoundNumber);
        SeedCpuInventory(state.CpuTank, settings, state.RoundNumber);
        state.Wind = NextWind(state);
        state.EventLog.Add($"Round {state.RoundNumber}. New ridge. Same grudge.");
    }

    /// <summary>
    /// Applies the civilian tower setting to an active match without changing turn flow.
    /// </summary>
    public void ApplyCivilianStructureSetting(GameState state, MatchSettings settings)
    {
        if (!settings.EnableCivilianStructures)
        {
            state.CivilianStructures.Clear();
            state.CivilianPenaltyByCpu = 0;
            state.CivilianPenaltyByPlayer = 0;
            return;
        }

        if (state.CivilianStructures.Count > 0) return;
        var roundSeed = state.RandomSeed + ((state.RoundNumber - 1) * 7919);
        SeedCivilianStructures(state, state.Terrain, roundSeed);
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
        var shotWind = state.Wind;
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

        if (!owner.HasWeapon(weaponId) || !WeaponIsEnabled(weaponId, settings))
            weaponId = WeaponIds.PeaShell;

        var weapon = Weapons.Get(weaponId);
        owner.ConsumeWeapon(weaponId);
        owner.TurretAngle = Math.Clamp(angle ?? owner.TurretAngle, 5, 175);
        var shotPower = Math.Clamp(power ?? 65, GameConstants.PowerMin, GameConstants.PowerMax);

        var simulationWatch = Stopwatch.StartNew();
        var simulation = SimulateWeapon(state, owner, opponent, weapon, owner.TurretAngle, shotPower, settings);
        simulationWatch.Stop();

        var intercepted = false;
        Vector2? interceptPoint = null;
        var terrainWatch = Stopwatch.StartNew();
        var resolvedExplosions = new List<ExplosionResult>(simulation.Explosions.Count);
        var civilianTerrainBefore = settings.EnableCivilianStructures
            ? CaptureCivilianTerrain(state.Terrain, state.CivilianStructures)
            : [];
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

        TerrainSlumpPayload slumpPayload;
        if (touched > 0 && !intercepted)
        {
            slumpPayload = _terrainSlumping.Relax(state.Terrain, resolvedExplosions);
            touched += slumpPayload.Columns.Length;
        }
        else
        {
            slumpPayload = new TerrainSlumpPayload([], 0, false);
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

        var civilianImpacts = settings.EnableCivilianStructures
            ? ResolveCivilianImpacts(state, owner, resolvedExplosions).ToList()
            : [];
        if (settings.EnableCivilianStructures)
        {
            var impactedIds = civilianImpacts
                .Select(static impact => impact.StructureId)
                .ToHashSet(StringComparer.Ordinal);
            civilianImpacts.AddRange(ResolveCivilianTerrainSupport(state, owner, impactedIds, civilianTerrainBefore));
        }
        if (civilianImpacts.Count > 0)
        {
            var penalty = 0;
            var collapsed = 0;
            for (var i = 0; i < civilianImpacts.Count; i++)
            {
                penalty += civilianImpacts[i].Penalty;
                if (civilianImpacts[i].Collapsed) collapsed++;
            }

            var message = collapsed > 0
                ? $"{owner.Name} hit civilian structures. Penalty ${penalty}; {collapsed} collapsed."
                : $"{owner.Name} hit civilian structures. Penalty ${penalty}.";
            events.Add(message);
            state.EventLog.Add(message);
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

        if (Winner(state) is null)
        {
            state.CurrentTurn = TurnManager.OpponentOf(state.CurrentTurn);
            state.Wind = NextWind(state);
            ApplyStartOfTurnEffects(state);
        }

        var winner = EndRoundIfWon(state);
        if (winner is not null)
        {
            events.Add(winner == TurnOwner.Player ? "Victory. The ridge salutes your math." : "Defeat. The CPU is insufferable now.");
            state.EventLog.Add(events[^1]);
        }

        var visualWatch = Stopwatch.StartNew();
        var visualKind = VisualKindFor(weapon);
        var visualPhysics = _visualPhysics.Build(
            state.Terrain,
            state.PlayerTank,
            state.CpuTank,
            resolvedExplosions,
            weapon.Id,
            owner.Id,
            visualKind,
            shotWind,
            slumpPayload,
            false);
        visualWatch.Stop();

        var perf = new PerformanceSample(
            simulationWatch.Elapsed.TotalMilliseconds,
            terrainWatch.Elapsed.TotalMilliseconds,
            cpuPlanningMs,
            simulation.Trail.Count,
            touched,
            slumpPayload.Columns.Length,
            visualWatch.Elapsed.TotalMilliseconds);
        state.LastPerformance = perf;
        return new ShotResolution(weapon.Id, owner.Id, simulation.Trail, resolvedExplosions, events, winner is not null, winner, perf, visualKind, intercepted, interceptPoint, visualPhysics, civilianImpacts);
    }

    /// <summary>
    /// Attempts to buy a weapon for the player tank.
    /// </summary>
    public bool BuyWeapon(GameState state, string weaponId, int count = 1) => Economy.BuyWeapon(state.PlayerTank, weaponId, count);

    /// <summary>
    /// Attempts to buy and apply an upgrade for the player tank.
    /// </summary>
    public bool BuyUpgrade(GameState state, UpgradeType upgradeType) => Economy.BuyUpgrade(state.PlayerTank, upgradeType);

    private bool WeaponIsEnabled(string weaponId, MatchSettings settings) =>
        settings.EnableNuclearWeapons || Weapons.Get(weaponId).Category != WeaponCategory.Nuclear;

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
    public IReadOnlyList<Vector2> PreviewPlayerShot(GameState state, MatchSettings settings, float angle, int power)
    {
        if (state.Phase != GamePhase.Battle || state.CurrentTurn != TurnOwner.Player)
            return [];

        var weaponId = state.PlayerTank.HasWeapon(state.SelectedWeaponId) && WeaponIsEnabled(state.SelectedWeaponId, settings)
            ? state.SelectedWeaponId
            : WeaponIds.PeaShell;
        var weapon = Weapons.Get(weaponId);
        if (weapon.BehaviorType is WeaponBehaviorType.Teleport or WeaponBehaviorType.Laser)
            return [];

        var civilianStructures = settings.EnableCivilianStructures ? state.CivilianStructures : null;
        return weapon.Id == WeaponIds.DarkEagle
            ? SimulateGuidedDarkEagle(state.PlayerTank, state.CpuTank, weapon).Trail
            : _projectileSimulator.Simulate(state.Terrain, state.PlayerTank, state.CpuTank, weapon, angle, power, state.Wind, 60 * 6, civilianStructures).Trail;
    }

    private WeaponSimulation SimulateWeapon(GameState state, Tank owner, Tank opponent, WeaponDefinition weapon, float angle, int power, MatchSettings settings)
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

        var civilianStructures = settings.EnableCivilianStructures ? state.CivilianStructures : null;
        var primary = _projectileSimulator.Simulate(state.Terrain, owner, opponent, weapon, angle, power, state.Wind, civilianStructures: civilianStructures);
        if (weapon.BehaviorType == WeaponBehaviorType.BunkerBuster)
            return SimulateBunkerBuster(primary, owner, weapon);

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

    private static WeaponSimulation SimulateBunkerBuster(ProjectileSimulation primary, Tank owner, WeaponDefinition weapon)
    {
        if (primary.StopReason != ProjectileStopReason.TerrainHit)
        {
            return new WeaponSimulation(
                primary.Trail,
                primary.ImpactPoint,
                [new ExplosionResult(primary.ImpactPoint, weapon.BlastRadius, weapon.TerrainRadius, 0, 0, false, false, [], VisualKindFor(weapon))]);
        }

        var trail = new List<Vector2>(primary.Trail.Count + 12);
        trail.AddRange(primary.Trail);
        if (trail.Count == 0)
            trail.Add(primary.ImpactPoint);

        var impact = primary.ImpactPoint;
        var previous = trail.Count > 1 ? trail[^2] : owner.Center;
        var direction = impact - previous;
        if (direction.LengthSquared() < 0.001f)
            direction = new Vector2(owner.IsCpu ? -0.25f : 0.25f, 1f);

        direction = NormalizeOrFallback(
            new Vector2(direction.X * 0.35f, MathF.Max(MathF.Abs(direction.Y), 0.86f)),
            new Vector2(owner.IsCpu ? -0.25f : 0.25f, 1f));
        const int burrowSteps = 12;
        var burrowDistance = Math.Clamp(weapon.TerrainRadius * 0.72f, 42f, 86f);
        for (var i = 1; i <= burrowSteps; i++)
        {
            var t = i / (float)burrowSteps;
            trail.Add(ClampToWorld(impact + (direction * burrowDistance * t)));
        }

        return new WeaponSimulation(
            trail,
            trail[^1],
            [new ExplosionResult(trail[^1], weapon.BlastRadius, weapon.TerrainRadius, 0, 0, false, false, [], VisualKindFor(weapon))]);
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

        if (ProjectileSimulator.SweptHitsTankOrShield(origin, target, opponent, terrain, BeamPadding, out var laserHit, out var stopReason))
        {
            trail[^1] = laserHit;
            var visualKind = stopReason == ProjectileStopReason.ShieldHit
                ? ShotVisualKind.ShieldHit
                : ShotVisualKind.Laser;
            return new WeaponSimulation(trail, laserHit, [LaserExplosion(laserHit, weapon, visualKind)]);
        }

        return new WeaponSimulation(trail, trail[^1], []);
    }

    private static ExplosionResult LaserExplosion(Vector2 center, WeaponDefinition weapon, ShotVisualKind visualKind = ShotVisualKind.Laser) =>
        new(center, weapon.BlastRadius, weapon.TerrainRadius, 0, 0, false, false, [], visualKind);

    private static Vector2 LaserOrigin(Tank owner, Tank opponent)
    {
        var delta = opponent.Center - owner.Center;
        if (delta.LengthSquared() <= 0.001f) return owner.Center;

        return ClampToWorld(owner.Center + (NormalizeOrFallback(delta, new Vector2(owner.IsCpu ? -1f : 1f, 0f)) * 32f));
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

        direction = NormalizeOrFallback(
            new Vector2(direction.X * 0.42f, MathF.Max(MathF.Abs(direction.Y), 0.68f)),
            new Vector2(owner.IsCpu ? -0.45f : 0.45f, 0.9f));
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

    private static IReadOnlyList<CivilianImpactResult> ResolveCivilianImpacts(GameState state, Tank owner, IReadOnlyList<ExplosionResult> explosions)
    {
        if (state.CivilianStructures.Count == 0 || explosions.Count == 0) return [];

        var impacts = new List<CivilianImpactResult>();
        for (var e = 0; e < explosions.Count; e++)
        {
            var explosion = explosions[e];
            var radius = MathF.Max(explosion.DamageRadius, explosion.TerrainRadius * 0.85f);
            if (radius <= 0 || !float.IsFinite(radius) || !float.IsFinite(explosion.Center.X) || !float.IsFinite(explosion.Center.Y))
                continue;

            for (var i = 0; i < state.CivilianStructures.Count; i++)
            {
                var structure = state.CivilianStructures[i];
                if (structure.IsCollapsed) continue;

                var closestX = Math.Clamp(explosion.Center.X, structure.Position.X - structure.Width * 0.5f, structure.Position.X + structure.Width * 0.5f);
                var closestY = Math.Clamp(explosion.Center.Y, structure.Position.Y - structure.Height, structure.Position.Y);
                var distance = Vector2.Distance(explosion.Center, new Vector2(closestX, closestY));
                if (distance > radius) continue;

                var physics = CivilianPhysicsFor(structure.Kind);
                var normalized = Math.Clamp(1f - distance / radius, 0.15f, 1f);
                var damage = Math.Clamp(
                    ((explosion.DamageRadius * 0.42f + explosion.TerrainRadius * 0.28f) * normalized) / physics.BlastResistance,
                    6f,
                    structure.MaxHealth);
                var wasIntact = structure.Health >= structure.MaxHealth - 0.001f;
                structure.Health = Math.Max(0, structure.Health - damage);
                structure.LastDamagedShot = state.ShotsFired;
                var impactLean = structure.Position.X >= explosion.Center.X ? 1f : -1f;
                structure.TiltDegrees = Math.Clamp(
                    structure.TiltDegrees + (impactLean * normalized * 12f * physics.LeanMultiplier),
                    -34f,
                    34f);
                structure.SupportFraction = Math.Clamp(structure.SupportFraction - (normalized * 0.16f), 0f, 1f);
                var penalty = wasIntact
                    ? structure.PenaltyValue
                    : Math.Max(25, (int)MathF.Round(structure.PenaltyValue * 0.35f * normalized));

                owner.Cash = Math.Max(0, owner.Cash - penalty);
                if (owner.IsCpu) state.CivilianPenaltyByCpu += penalty;
                else state.CivilianPenaltyByPlayer += penalty;

                impacts.Add(new CivilianImpactResult(
                    structure.Id,
                    structure.Position,
                    damage,
                    structure.Health,
                    penalty,
                    structure.IsCollapsed,
                    structure.Kind));
            }
        }

        return impacts;
    }

    private static IReadOnlyList<CivilianImpactResult> ResolveCivilianTerrainSupport(
        GameState state,
        Tank owner,
        ISet<string> alreadyImpacted,
        IReadOnlyDictionary<string, CivilianTerrainSnapshot> terrainBefore)
    {
        if (state.CivilianStructures.Count == 0 || terrainBefore.Count == 0) return [];

        var impacts = new List<CivilianImpactResult>();
        for (var i = 0; i < state.CivilianStructures.Count; i++)
        {
            var structure = state.CivilianStructures[i];
            if (!terrainBefore.TryGetValue(structure.Id, out var before)) continue;

            var physics = CivilianPhysicsFor(structure.Kind);
            var support = SampleCivilianSupport(state.Terrain, structure, before);
            if (support.TerrainDrop <= 0.5f
                && support.SupportFraction >= 0.999f
                && MathF.Abs(support.SlopeDeltaDegrees) <= 0.5f)
            {
                continue;
            }

            structure.SupportFraction = MathF.Min(structure.SupportFraction, support.SupportFraction);
            structure.TiltDegrees = Math.Clamp(
                structure.TiltDegrees + (support.SlopeDeltaDegrees * physics.LeanMultiplier),
                -34f,
                34f);

            if (structure.IsCollapsed)
            {
                structure.Position = new Vector2(
                    structure.Position.X,
                    Math.Clamp(structure.Position.Y + support.TerrainDrop, 0, state.Terrain.Height - 1));
                continue;
            }

            var fallDistance = support.TerrainDrop;
            if (fallDistance > 0.5f)
            {
                var settle = Math.Clamp(fallDistance * 0.72f, 0f, MathF.Max(18f, structure.Height * 0.62f));
                structure.Position = new Vector2(structure.Position.X, Math.Clamp(structure.Position.Y + settle, 0, state.Terrain.Height - 1));
            }

            var supportDamage = MathF.Max(0, 0.72f - support.SupportFraction) * 72f * physics.SupportDamageMultiplier;
            var fallDamage = MathF.Max(0, fallDistance - 5f) * 0.62f * physics.SupportDamageMultiplier;
            var tiltDamage = MathF.Max(0, MathF.Abs(support.SlopeDeltaDegrees) - 8f) * 1.05f * physics.LeanMultiplier;
            var damage = supportDamage + fallDamage + tiltDamage;
            if (support.SupportFraction < physics.CollapseSupportFraction || fallDistance > structure.Height * physics.CollapseFallFraction)
                damage = MathF.Max(damage, structure.Health);

            if (damage < 5f) continue;

            var damageApplied = MathF.Min(structure.Health, damage);
            structure.Health = Math.Max(0, structure.Health - damageApplied);
            structure.LastDamagedShot = state.ShotsFired;
            if (alreadyImpacted.Contains(structure.Id)) continue;

            var penalty = structure.IsCollapsed
                ? Math.Max(60, (int)MathF.Round(structure.PenaltyValue * 0.45f))
                : Math.Max(25, (int)MathF.Round(structure.PenaltyValue * Math.Clamp(damageApplied / MathF.Max(structure.MaxHealth, 1f), 0.16f, 0.55f)));

            owner.Cash = Math.Max(0, owner.Cash - penalty);
            if (owner.IsCpu) state.CivilianPenaltyByCpu += penalty;
            else state.CivilianPenaltyByPlayer += penalty;

            impacts.Add(new CivilianImpactResult(
                structure.Id,
                structure.Position,
                damageApplied,
                structure.Health,
                penalty,
                structure.IsCollapsed,
                structure.Kind));
        }

        return impacts;
    }

    private static Dictionary<string, CivilianTerrainSnapshot> CaptureCivilianTerrain(
        TerrainMask terrain,
        IReadOnlyList<CivilianStructure> structures)
    {
        var snapshots = new Dictionary<string, CivilianTerrainSnapshot>(structures.Count, StringComparer.Ordinal);
        for (var i = 0; i < structures.Count; i++)
        {
            var structure = structures[i];
            snapshots[structure.Id] = CaptureCivilianTerrain(terrain, structure);
        }

        return snapshots;
    }

    private static CivilianTerrainSnapshot CaptureCivilianTerrain(TerrainMask terrain, CivilianStructure structure)
    {
        const int SampleCount = 9;
        var halfWidth = MathF.Max(8f, structure.Width * 0.5f);
        var samples = new float[SampleCount];
        var total = 0f;
        var leftTotal = 0f;
        var rightTotal = 0f;
        var leftCount = 0;
        var rightCount = 0;

        for (var i = 0; i < SampleCount; i++)
        {
            var t = SampleCount == 1 ? 0.5f : i / (float)(SampleCount - 1);
            var x = structure.Position.X - halfWidth + (t * halfWidth * 2f);
            var surfaceY = terrain.GetSurfaceY(x);
            samples[i] = surfaceY;
            total += surfaceY;
            if (t < 0.5f)
            {
                leftTotal += surfaceY;
                leftCount++;
            }
            else if (t > 0.5f)
            {
                rightTotal += surfaceY;
                rightCount++;
            }
        }

        var average = total / SampleCount;
        var leftAverage = leftCount > 0 ? leftTotal / leftCount : average;
        var rightAverage = rightCount > 0 ? rightTotal / rightCount : average;
        var slopeDegrees = MathF.Atan2(rightAverage - leftAverage, MathF.Max(1f, halfWidth * 2f)) * 180f / MathF.PI;
        return new CivilianTerrainSnapshot(
            samples,
            Math.Clamp(average, 0, terrain.Height),
            Math.Clamp(slopeDegrees, -34f, 34f));
    }

    private static CivilianSupportSample SampleCivilianSupport(
        TerrainMask terrain,
        CivilianStructure structure,
        CivilianTerrainSnapshot before)
    {
        var current = CaptureCivilianTerrain(terrain, structure);
        var sampleCount = Math.Min(before.SurfaceY.Length, current.SurfaceY.Length);
        if (sampleCount == 0)
            return new CivilianSupportSample(1f, 0f, 0f);

        var supported = 0;
        for (var i = 0; i < sampleCount; i++)
        {
            if (current.SurfaceY[i] <= before.SurfaceY[i] + 4f)
                supported++;
        }

        return new CivilianSupportSample(
            Math.Clamp(supported / (float)sampleCount, 0f, 1f),
            MathF.Max(0, current.AverageSurfaceY - before.AverageSurfaceY),
            Math.Clamp(current.SlopeDegrees - before.SlopeDegrees, -34f, 34f));
    }

    private static void SeedCivilianStructures(GameState state, TerrainMask terrain, int seed)
    {
        var random = new Random(seed ^ 0x51C1A11);
        var candidates = new[]
        {
            315f + random.NextSingle() * 45f,
            505f + random.NextSingle() * 50f,
            705f + random.NextSingle() * 50f,
            875f + random.NextSingle() * 35f
        };
        var kinds = new[] { "high-rise-apartment", "glass-office", "luxury-tower", "civic-tower" };
        for (var i = 0; i < candidates.Length; i++)
        {
            var x = Math.Clamp(candidates[i], 230f, terrain.Width - 230f);
            if (MathF.Abs(x - state.PlayerTank.Position.X) < 115 || MathF.Abs(x - state.CpuTank.Position.X) < 115)
                continue;

            var kind = kinds[i % kinds.Length];
            var physics = CivilianPhysicsFor(kind);
            var (width, height) = kind switch
            {
                "glass-office" => (52f + random.Next(0, 15), 104f + random.Next(0, 41)),
                "high-rise-apartment" => (60f + random.Next(0, 17), 110f + random.Next(0, 41)),
                "luxury-tower" => (50f + random.Next(0, 15), 118f + random.Next(0, 33)),
                _ => (48f + random.Next(0, 15), 88f + random.Next(0, 39))
            };
            var maxHealth = 70f + height * physics.HealthPerHeight;
            state.CivilianStructures.Add(new CivilianStructure
            {
                Id = $"civ-{state.RoundNumber}-{i}",
                Position = new Vector2(x, terrain.GetSurfaceY(x)),
                Kind = kind,
                Width = width,
                Height = height,
                MaxHealth = maxHealth,
                Health = maxHealth,
                PenaltyValue = 125 + (int)MathF.Round(height * 2.3f * physics.PenaltyMultiplier)
            });
        }
    }

    private static CivilianBuildingPhysics CivilianPhysicsFor(string kind) =>
        kind.ToLowerInvariant() switch
        {
            "glass-office" => new CivilianBuildingPhysics(0.78f, 0.86f, 0.92f, 0.14f, 0.64f, 0.43f, 1.05f),
            "high-rise-apartment" => new CivilianBuildingPhysics(1.18f, 0.72f, 1.22f, 0.22f, 0.48f, 0.62f, 1f),
            "luxury-tower" => new CivilianBuildingPhysics(0.92f, 1.22f, 1.05f, 0.2f, 0.52f, 0.5f, 1.4f),
            _ => new CivilianBuildingPhysics(1f, 1f, 1f, 0.18f, 0.58f, 0.48f, 0.9f)
        };

    private static Vector2 ClampToWorld(Vector2 point) => new(
        Math.Clamp(point.X, 0, GameConstants.WorldWidth - 1),
        Math.Clamp(point.Y, 0, GameConstants.WorldHeight - 1));

    private static Vector2 NormalizeOrFallback(Vector2 vector, Vector2 fallback)
    {
        var lengthSquared = vector.LengthSquared();
        if (float.IsFinite(lengthSquared) && lengthSquared > 0.0001f)
            return Vector2.Normalize(vector);

        var fallbackLengthSquared = fallback.LengthSquared();
        return float.IsFinite(fallbackLengthSquared) && fallbackLengthSquared > 0.0001f
            ? Vector2.Normalize(fallback)
            : Vector2.UnitX;
    }

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
        _explosionService.ApplyRadiation(active, state.RadiationZones, (zone, damage) => CreditHazardDamage(state, active, zone, damage));
        _explosionService.TickRadiation(state.RadiationZones);
    }

    private TurnOwner? EndRoundIfWon(GameState state)
    {
        var winner = Winner(state);
        if (winner is null) return null;

        if (state.Phase != GamePhase.RoundOver)
        {
            Economy.AwardRound(state, winner.Value);
            state.Phase = GamePhase.RoundOver;
        }

        return winner;
    }

    private static void CreditHazardDamage(GameState state, Tank damagedTank, RadiationZone zone, float damage)
    {
        if (damage <= 0 || string.IsNullOrWhiteSpace(zone.OwnerTankId)) return;

        if (zone.OwnerTankId == state.PlayerTank.Id && damagedTank.Id == state.CpuTank.Id)
            state.DamageDealtByPlayer += damage;
        else if (zone.OwnerTankId == state.CpuTank.Id && damagedTank.Id == state.PlayerTank.Id)
            state.DamageDealtByCpu += damage;
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

internal readonly record struct CivilianTerrainSnapshot(float[] SurfaceY, float AverageSurfaceY, float SlopeDegrees);

internal readonly record struct CivilianSupportSample(float SupportFraction, float TerrainDrop, float SlopeDeltaDegrees);

internal readonly record struct CivilianBuildingPhysics(
    float BlastResistance,
    float LeanMultiplier,
    float SupportDamageMultiplier,
    float CollapseSupportFraction,
    float CollapseFallFraction,
    float HealthPerHeight,
    float PenaltyMultiplier);

internal readonly record struct CpuRivalProfile(string DisplayName, string TankName)
{
    public static CpuRivalProfile ForSeed(int seed) => (Math.Abs(seed) % 3) switch
    {
        0 => new("Volga Circuit rival, Russia-inspired arcade faction", "Volga Redline"),
        1 => new("Zagros Signal rival, Iran-inspired arcade faction", "Zagros Signal"),
        _ => new("Paektu Spark rival, North Korea-inspired arcade faction", "Paektu Spark")
    };
}
