using System.Diagnostics;
using System.Numerics;
using ArmageddonRidge.Core;
using ArmageddonRidge.Core.AI;
using ArmageddonRidge.Core.Content;
using ArmageddonRidge.Core.Game;
using ArmageddonRidge.Core.Models;
using ArmageddonRidge.Core.Terrain;

namespace ArmageddonRidge.Tests;

public sealed class GameEngineTests
{
    public static TheoryData<string, ShotVisualKind, int> WeaponSmokeCases => new()
    {
        { WeaponIds.PeaShell, ShotVisualKind.Ballistic, 1 },
        { WeaponIds.HeavyShell, ShotVisualKind.Ballistic, 1 },
        { WeaponIds.BabyMissile, ShotVisualKind.Ballistic, 1 },
        { WeaponIds.ClusterPopper, ShotVisualKind.Ballistic, 5 },
        { WeaponIds.SplitterMirv, ShotVisualKind.Ballistic, 7 },
        { WeaponIds.NapalmFlask, ShotVisualKind.Fire, 1 },
        { WeaponIds.DirtDrop, ShotVisualKind.Dirt, 1 },
        { WeaponIds.Excavator, ShotVisualKind.Dirt, 1 },
        { WeaponIds.BunkerBuster, ShotVisualKind.Ballistic, 1 },
        { WeaponIds.LaserLance, ShotVisualKind.Laser, 1 },
        { WeaponIds.TeleportShot, ShotVisualKind.Teleport, 0 },
        { WeaponIds.DarkEagle, ShotVisualKind.Missile, 1 },
        { WeaponIds.ShahedDroneSwarm, ShotVisualKind.DroneSwarm, 3 },
        { WeaponIds.Gbu57Mop, ShotVisualKind.PenetratorSecondary, 2 },
        { WeaponIds.TacticalNuke, ShotVisualKind.Nuclear, 1 },
        { WeaponIds.DoomsdayNuke, ShotVisualKind.Nuclear, 1 }
    };

    [Fact]
    public void NewMatchSpawnsTanksOnTerrain()
    {
        var engine = CreateEngine();
        var state = engine.NewMatch(new MatchSettings(TerrainSeed: 123, EnableShop: false));

        Assert.Equal(state.Terrain.GetSurfaceY(state.PlayerTank.Position.X), state.PlayerTank.Position.Y);
        Assert.Equal(state.Terrain.GetSurfaceY(state.CpuTank.Position.X), state.CpuTank.Position.Y);
    }

    [Fact]
    public void NewMatchUsesFastPlaytestEconomyAndHealth()
    {
        var engine = CreateEngine();
        var state = engine.NewMatch(new MatchSettings(TerrainSeed: 123, EnableShop: false));

        Assert.Equal(75, state.PlayerTank.MaxHealth);
        Assert.Equal(75, state.CpuTank.MaxHealth);
        Assert.Equal(5000, state.PlayerTank.Cash);
    }

    [Fact]
    public void RookieDifficultyUsesShorterCpuHealthPool()
    {
        var engine = CreateEngine();
        var settings = new MatchSettings(Difficulty: Difficulty.Rookie, TerrainSeed: 123, EnableShop: false);
        var state = engine.NewMatch(settings);

        Assert.Equal(50, state.CpuTank.MaxHealth);
        Assert.Equal(50, state.CpuTank.Health);
        Assert.Equal(GameConstants.StartingHealth, state.PlayerTank.MaxHealth);
    }

    [Fact]
    public void BuyingWeaponAddsInventoryAndSpendsCash()
    {
        var engine = CreateEngine();
        var state = engine.NewMatch(new MatchSettings(TerrainSeed: 123));
        var before = state.PlayerTank.Cash;

        var bought = engine.BuyWeapon(state, WeaponIds.HeavyShell);

        Assert.True(bought);
        Assert.True(state.PlayerTank.Cash < before);
        Assert.Equal(1, state.PlayerTank.GetInventoryCount(WeaponIds.HeavyShell));
    }

    [Theory]
    [MemberData(nameof(WeaponSmokeCases))]
    public void EveryCatalogWeaponCanBeSelectedFiredLoggedAndRendered(string weaponId, ShotVisualKind expectedVisualKind, int minimumExplosions)
    {
        var engine = CreateEngine();
        var settings = new MatchSettings(TerrainSeed: 123, EnableShop: false, EnableNuclearWeapons: true);
        var state = engine.NewMatch(settings);
        var heights = Enumerable.Repeat(680f, GameConstants.WorldWidth).ToArray();
        state.Terrain.CopyFrom(new TerrainMask(GameConstants.WorldWidth, GameConstants.WorldHeight, heights));
        state.PlayerTank.Position = new Vector2(160, 620);
        state.CpuTank.Position = new Vector2(420, 620);
        state.Wind = 0;
        if (weaponId != WeaponIds.PeaShell)
            state.PlayerTank.AddWeapon(weaponId, 1);
        state.SelectedWeaponId = weaponId;
        engine.StartBattle(state);

        var result = engine.FireCurrentTurn(state, settings, angle: 24, power: 78);

        Assert.Equal(weaponId, result.WeaponId);
        Assert.Equal("player", result.OwnerTankId);
        Assert.Equal(expectedVisualKind, result.VisualKind);
        Assert.NotEmpty(result.Trail);
        Assert.Contains(result.Events, entry => entry.Contains($"fired {engine.Weapons.Get(weaponId).DisplayName}", StringComparison.Ordinal));
        Assert.Contains(state.EventLog, entry => entry.Contains($"fired {engine.Weapons.Get(weaponId).DisplayName}", StringComparison.Ordinal));
        Assert.True(result.Explosions.Count >= minimumExplosions);
        if (weaponId != WeaponIds.PeaShell)
            Assert.Equal(0, state.PlayerTank.GetInventoryCount(weaponId));
    }

    [Theory]
    [MemberData(nameof(WeaponSmokeCases))]
    public void EveryCatalogWeaponCanBeFiredByPlannedCpuTurn(string weaponId, ShotVisualKind expectedVisualKind, int minimumExplosions)
    {
        var engine = CreateEngine();
        var settings = new MatchSettings(TerrainSeed: 123, EnableShop: false, EnableNuclearWeapons: true);
        var state = engine.NewMatch(settings);
        var heights = Enumerable.Repeat(680f, GameConstants.WorldWidth).ToArray();
        state.Terrain.CopyFrom(new TerrainMask(GameConstants.WorldWidth, GameConstants.WorldHeight, heights));
        state.PlayerTank.Position = new Vector2(160, 620);
        state.CpuTank.Position = new Vector2(420, 620);
        state.PlayerTank.MaxHealth = 500;
        state.PlayerTank.Health = 500;
        state.CpuTank.MaxHealth = 500;
        state.CpuTank.Health = 500;
        state.Wind = 0;
        if (weaponId != WeaponIds.PeaShell)
            state.CpuTank.AddWeapon(weaponId, 1);
        engine.StartBattle(state);
        state.CurrentTurn = TurnOwner.Cpu;
        var plan = new CpuShotPlan(weaponId, 156, 78, "", 0, 0);

        var result = engine.FirePlannedCpuTurn(state, settings, plan);

        Assert.Equal(weaponId, result.WeaponId);
        Assert.Equal("cpu", result.OwnerTankId);
        Assert.Equal(expectedVisualKind, result.VisualKind);
        Assert.NotEmpty(result.Trail);
        Assert.Contains(result.Events, entry => entry.Contains($"fired {engine.Weapons.Get(weaponId).DisplayName}", StringComparison.Ordinal));
        Assert.Contains(state.EventLog, entry => entry.Contains($"fired {engine.Weapons.Get(weaponId).DisplayName}", StringComparison.Ordinal));
        Assert.True(result.Explosions.Count >= minimumExplosions);
        if (weaponId != WeaponIds.PeaShell)
            Assert.Equal(0, state.CpuTank.GetInventoryCount(weaponId));
    }

    [Fact]
    public void CpuPlannerReturnsOwnedOrFreeWeapon()
    {
        var engine = CreateEngine();
        var settings = new MatchSettings(Difficulty: Difficulty.Normal, TerrainSeed: 123, EnableShop: false);
        var state = engine.NewMatch(settings);
        engine.StartBattle(state);
        state.CurrentTurn = TurnOwner.Cpu;

        var result = engine.FireCurrentTurn(state, settings);

        Assert.NotEmpty(result.Trail);
        Assert.True(result.Performance.CpuPlanningMs >= 0);
    }

    [Fact]
    public void CpuOraclePlanningStaysWithinPerfSmokeBudget()
    {
        var engine = CreateEngine();
        var seeds = new[] { 123, 424242, 777333 };
        var started = Stopwatch.StartNew();

        foreach (var seed in seeds)
        {
            var settings = new MatchSettings(Difficulty: Difficulty.Oracle, TerrainSeed: seed, EnableShop: false);
            var state = engine.NewMatch(settings);
            engine.StartBattle(state);
            state.CurrentTurn = TurnOwner.Cpu;

            var plan = engine.Cpu.PlanShot(state, settings);

            Assert.True(state.CpuTank.HasWeapon(plan.WeaponId) || plan.WeaponId == WeaponIds.PeaShell);
        }

        started.Stop();
        Assert.True(started.ElapsedMilliseconds < 2_000, $"CPU planning smoke took {started.ElapsedMilliseconds} ms.");
    }

    [Fact]
    public async Task CpuAsyncPlanningObservesCancellation()
    {
        var engine = CreateEngine();
        var settings = new MatchSettings(Difficulty: Difficulty.Oracle, TerrainSeed: 424242, EnableShop: false);
        var state = engine.NewMatch(settings);
        engine.StartBattle(state);
        state.CurrentTurn = TurnOwner.Cpu;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await engine.PlanCurrentCpuTurnAsync(state, settings, cancellation.Token));
    }

    [Fact]
    public void PreviewPlayerShotReturnsDeterministicPlayerArc()
    {
        var engine = CreateEngine();
        var settings = new MatchSettings(TerrainSeed: 123, EnableShop: false);
        var state = engine.NewMatch(settings);
        engine.StartBattle(state);

        var preview = engine.PreviewPlayerShot(state, state.PlayerTank.TurretAngle, 65);

        Assert.NotEmpty(preview);
        Assert.Equal(preview, engine.PreviewPlayerShot(state, state.PlayerTank.TurretAngle, 65));
    }

    [Fact]
    public void PreviewPlayerShotIncludesWindForTargetingAid()
    {
        var engine = CreateEngine();
        var settings = new MatchSettings(TerrainSeed: 123, EnableShop: false);
        var state = engine.NewMatch(settings);
        engine.StartBattle(state);

        state.Wind = GameConstants.WindMin;
        var leftWind = engine.PreviewPlayerShot(state, state.PlayerTank.TurretAngle, 65);
        state.Wind = GameConstants.WindMax;
        var rightWind = engine.PreviewPlayerShot(state, state.PlayerTank.TurretAngle, 65);

        Assert.NotEmpty(leftWind);
        Assert.NotEqual(leftWind, rightWind);
    }

    [Fact]
    public void WindStaysInsidePlayableRange()
    {
        var engine = CreateEngine();
        var settings = new MatchSettings(TerrainSeed: 123, EnableShop: false);

        for (var seed = 0; seed < 200; seed++)
        {
            var state = engine.NewMatch(settings with { TerrainSeed = seed });
            Assert.InRange(state.Wind, GameConstants.WindMin, GameConstants.WindMax);
        }
    }

    [Fact]
    public void TeleportShotIsConsumedAndCannotBeReusedFromOneInventoryCount()
    {
        var engine = CreateEngine();
        var settings = new MatchSettings(TerrainSeed: 123, EnableShop: false);
        var state = engine.NewMatch(settings);
        state.PlayerTank.AddWeapon(WeaponIds.TeleportShot, 1);
        state.SelectedWeaponId = WeaponIds.TeleportShot;
        engine.StartBattle(state);

        var teleport = engine.FireCurrentTurn(state, settings);

        Assert.Equal(WeaponIds.TeleportShot, teleport.WeaponId);
        Assert.Equal(0, state.PlayerTank.GetInventoryCount(WeaponIds.TeleportShot));
        Assert.False(state.PlayerTank.HasWeapon(WeaponIds.TeleportShot));

        state.CurrentTurn = TurnOwner.Player;
        var fallback = engine.FireCurrentTurn(state, settings);

        Assert.Equal(WeaponIds.PeaShell, fallback.WeaponId);
        Assert.Equal(0, state.PlayerTank.GetInventoryCount(WeaponIds.TeleportShot));
    }

    [Fact]
    public void FiringSettlesTanksToNearestVisibleTerrainWhenTheirColumnIsGone()
    {
        var engine = CreateEngine();
        var settings = new MatchSettings(TerrainSeed: 123, EnableShop: false);
        var state = engine.NewMatch(settings);
        var heights = Enumerable.Repeat((float)GameConstants.WorldHeight, GameConstants.WorldWidth).ToArray();
        heights[140] = 500;
        heights[180] = 500;
        heights[1000] = 510;
        state.Terrain.CopyFrom(new TerrainMask(GameConstants.WorldWidth, GameConstants.WorldHeight, heights));
        state.PlayerTank.Position = new Vector2(160, GameConstants.WorldHeight);
        state.CpuTank.Position = new Vector2(1020, GameConstants.WorldHeight);
        engine.StartBattle(state);

        engine.FireCurrentTurn(state, settings, angle: 5, power: 1);

        Assert.Equal(new Vector2(140, 500), state.PlayerTank.Position);
        Assert.Equal(new Vector2(1000, 510), state.CpuTank.Position);
        Assert.False(state.PlayerTank.IsDestroyed);
        Assert.False(state.CpuTank.IsDestroyed);
    }

    [Fact]
    public void DirtDropCanCoverTankWithoutSettlingItOntoMound()
    {
        var engine = CreateEngine();
        var settings = new MatchSettings(TerrainSeed: 123, EnableShop: false);
        const float surfaceY = 560f;

        for (var angle = 25; angle <= 65; angle++)
        {
            for (var power = 35; power <= 75; power++)
            {
                var state = engine.NewMatch(settings);
                var heights = Enumerable.Repeat(surfaceY, GameConstants.WorldWidth).ToArray();
                state.Terrain.CopyFrom(new TerrainMask(GameConstants.WorldWidth, GameConstants.WorldHeight, heights));
                state.PlayerTank.Position = new Vector2(220, surfaceY);
                state.CpuTank.Position = new Vector2(610, surfaceY);
                state.PlayerTank.AddWeapon(WeaponIds.DirtDrop, 1);
                state.SelectedWeaponId = WeaponIds.DirtDrop;
                engine.StartBattle(state);
                state.Wind = 0;

                var result = engine.FireCurrentTurn(state, settings, angle, power);
                var cpuTerrainY = state.Terrain.GetSurfaceY(state.CpuTank.Position.X);
                if (!result.Explosions.Any(static explosion => explosion.DirtAdded)
                    || cpuTerrainY >= surfaceY - 4f)
                {
                    continue;
                }

                Assert.Equal(surfaceY, state.CpuTank.Position.Y);
                Assert.True(cpuTerrainY < state.CpuTank.Position.Y - 4f);
                return;
            }
        }

        Assert.Fail("Expected one deterministic Dirt Drop firing solution to cover the CPU tank.");
    }

    [Fact]
    public void PatriotBatteryInterceptsThreateningCpuShotAndIsConsumed()
    {
        var engine = CreateEngine();
        var settings = new MatchSettings(Difficulty: Difficulty.Oracle, TerrainSeed: 123, EnableShop: false);
        var state = engine.NewMatch(settings);
        engine.StartBattle(state);
        state.CurrentTurn = TurnOwner.Cpu;
        state.PlayerTank.Upgrades.Add(UpgradeType.PatriotBattery);
        state.CpuTank.AddWeapon(WeaponIds.DarkEagle, 1);

        var beforeHealth = state.PlayerTank.Health;

        var result = engine.FireCurrentTurn(state, settings);

        Assert.True(result.Intercepted);
        Assert.Equal(ShotVisualKind.PatriotIntercept, Assert.Single(result.Explosions).VisualKind);
        Assert.DoesNotContain(UpgradeType.PatriotBattery, state.PlayerTank.Upgrades);
        Assert.Equal(beforeHealth, state.PlayerTank.Health);
    }

    [Fact]
    public void ProjectileDetonatingOnShieldBoundaryConsumesShield()
    {
        var engine = CreateEngine();
        var settings = new MatchSettings(TerrainSeed: 123, EnableShop: false);
        var state = engine.NewMatch(settings);
        var heights = Enumerable.Repeat(680f, GameConstants.WorldWidth).ToArray();
        state.Terrain.CopyFrom(new TerrainMask(GameConstants.WorldWidth, GameConstants.WorldHeight, heights));
        state.PlayerTank.Position = new Vector2(160, 620);
        state.CpuTank.Position = new Vector2(300, 620);
        state.CpuTank.Shield = 120;
        state.Wind = 0;
        engine.StartBattle(state);

        engine.FireCurrentTurn(state, settings, angle: 0, power: 100);

        Assert.True(state.CpuTank.Shield < 120);
    }

    [Fact]
    public void DirectHullHitsDeductOpponentHealth()
    {
        var engine = CreateEngine();
        var settings = new MatchSettings(TerrainSeed: 123, EnableShop: false);
        var state = engine.NewMatch(settings);
        var heights = Enumerable.Repeat(680f, GameConstants.WorldWidth).ToArray();
        state.Terrain.CopyFrom(new TerrainMask(GameConstants.WorldWidth, GameConstants.WorldHeight, heights));
        state.PlayerTank.Position = new Vector2(160, 620);
        state.CpuTank.Position = new Vector2(300, 620);
        state.Wind = 0;
        engine.StartBattle(state);

        engine.FireCurrentTurn(state, settings, angle: 0, power: 100);

        Assert.True(state.CpuTank.Health < state.CpuTank.MaxHealth);
    }

    [Fact]
    public void CpuDirectHullHitsDeductPlayerHealth()
    {
        var engine = CreateEngine();
        var settings = new MatchSettings(TerrainSeed: 123, EnableShop: false);
        var state = engine.NewMatch(settings);
        var heights = Enumerable.Repeat(680f, GameConstants.WorldWidth).ToArray();
        state.Terrain.CopyFrom(new TerrainMask(GameConstants.WorldWidth, GameConstants.WorldHeight, heights));
        state.PlayerTank.Position = new Vector2(300, 620);
        state.CpuTank.Position = new Vector2(440, 620);
        state.Wind = 0;
        engine.StartBattle(state);
        state.CurrentTurn = TurnOwner.Cpu;
        var plan = new CpuShotPlan(WeaponIds.PeaShell, 180, 100, "", 0, 0);

        engine.FirePlannedCpuTurn(state, settings, plan);

        Assert.True(state.PlayerTank.Health < state.PlayerTank.MaxHealth);
    }

    [Fact]
    public void HitsNearVisibleHullTopDeductOpponentHealth()
    {
        var engine = CreateEngine();
        var settings = new MatchSettings(TerrainSeed: 123, EnableShop: false);
        var state = engine.NewMatch(settings);
        var heights = Enumerable.Repeat(680f, GameConstants.WorldWidth).ToArray();
        state.Terrain.CopyFrom(new TerrainMask(GameConstants.WorldWidth, GameConstants.WorldHeight, heights));
        state.PlayerTank.Position = new Vector2(160, 620);
        state.CpuTank.Position = new Vector2(300, 620);
        state.Wind = 0;
        engine.StartBattle(state);

        engine.FireCurrentTurn(state, settings, angle: 14, power: 100);

        Assert.True(state.CpuTank.Health < state.CpuTank.MaxHealth);
    }

    [Fact]
    public void RealTerrainHullHitsDeductOpponentHealth()
    {
        var engine = CreateEngine();
        var settings = new MatchSettings(TerrainSeed: 123, EnableShop: false);

        for (var angle = 5; angle <= 85; angle++)
        {
            for (var power = GameConstants.PowerMin; power <= GameConstants.PowerMax; power++)
            {
                var state = engine.NewMatch(settings);
                engine.StartBattle(state);
                state.Wind = 0;

                var result = engine.FireCurrentTurn(state, settings, angle, power);
                if (result.Explosions.Count == 0)
                    continue;

                var directHit = result.Explosions.Any(static explosion => explosion.CpuDamage > 0);
                if (!directHit)
                    continue;

                Assert.True(state.CpuTank.Health < state.CpuTank.MaxHealth);
                return;
            }
        }

        Assert.Fail("Expected one deterministic normal shot to damage the CPU on generated terrain.");
    }

    [Fact]
    public void DirectShieldHitsBleedSomeDamageThroughToHealth()
    {
        var engine = CreateEngine();
        var settings = new MatchSettings(TerrainSeed: 123, EnableShop: false);
        var state = engine.NewMatch(settings);
        var heights = Enumerable.Repeat(680f, GameConstants.WorldWidth).ToArray();
        state.Terrain.CopyFrom(new TerrainMask(GameConstants.WorldWidth, GameConstants.WorldHeight, heights));
        state.PlayerTank.Position = new Vector2(160, 620);
        state.CpuTank.Position = new Vector2(300, 620);
        state.CpuTank.Shield = 120;
        state.Wind = 0;
        engine.StartBattle(state);

        engine.FireCurrentTurn(state, settings, angle: 0, power: 100);

        Assert.True(state.CpuTank.Shield < 120);
        Assert.True(state.CpuTank.Health < state.CpuTank.MaxHealth);
    }

    [Fact]
    public void LaserShieldHitsUseShieldImpactVisual()
    {
        var engine = CreateEngine();
        var settings = new MatchSettings(TerrainSeed: 123, EnableShop: false);
        var state = engine.NewMatch(settings);
        var heights = Enumerable.Repeat(680f, GameConstants.WorldWidth).ToArray();
        state.Terrain.CopyFrom(new TerrainMask(GameConstants.WorldWidth, GameConstants.WorldHeight, heights));
        state.PlayerTank.Position = new Vector2(160, 620);
        state.CpuTank.Position = new Vector2(320, 620);
        state.CpuTank.Shield = 120;
        state.PlayerTank.AddWeapon(WeaponIds.LaserLance, 1);
        state.SelectedWeaponId = WeaponIds.LaserLance;
        state.Wind = GameConstants.WindMax;
        engine.StartBattle(state);

        var result = engine.FireCurrentTurn(state, settings, angle: 85, power: 1);

        Assert.Equal(WeaponIds.LaserLance, result.WeaponId);
        Assert.Equal(ShotVisualKind.Laser, result.VisualKind);
        Assert.Equal(ShotVisualKind.ShieldHit, Assert.Single(result.Explosions).VisualKind);
        Assert.True(state.CpuTank.Shield < 120);
        Assert.True(state.CpuTank.Health < state.CpuTank.MaxHealth);
    }

    [Fact]
    public void PatriotBatteryPurchasesStackAndConsumeOneAtATime()
    {
        var engine = CreateEngine();
        var settings = new MatchSettings(Difficulty: Difficulty.Oracle, TerrainSeed: 123, EnableShop: false);
        var state = engine.NewMatch(settings);
        engine.BuyUpgrade(state, UpgradeType.PatriotBattery);
        engine.BuyUpgrade(state, UpgradeType.PatriotBattery);
        engine.StartBattle(state);
        state.CurrentTurn = TurnOwner.Cpu;
        state.CpuTank.AddWeapon(WeaponIds.DarkEagle, 1);

        var result = engine.FireCurrentTurn(state, settings);

        Assert.True(result.Intercepted);
        Assert.Equal(1, state.PlayerTank.PatriotBatteryCharges);
        Assert.Contains(UpgradeType.PatriotBattery, state.PlayerTank.Upgrades);
    }

    [Fact]
    public void TracerRoundPurchasesStackAsTrailSlots()
    {
        var engine = CreateEngine();
        var state = engine.NewMatch(new MatchSettings(TerrainSeed: 123, EnableShop: false));

        engine.BuyUpgrade(state, UpgradeType.TracerRounds);
        engine.BuyUpgrade(state, UpgradeType.TracerRounds);

        Assert.Equal(2, state.PlayerTank.TracerRoundCharges);
    }

    [Fact]
    public void BunkerBusterBurrowsIntoTerrainBeforeExploding()
    {
        var engine = CreateEngine();
        var settings = new MatchSettings(TerrainSeed: 123, EnableShop: false);
        const float surfaceY = 620f;

        for (var angle = 24; angle <= 68; angle += 2)
        {
            for (var power = 36; power <= 88; power += 2)
            {
                var state = engine.NewMatch(settings);
                var heights = Enumerable.Repeat(surfaceY, GameConstants.WorldWidth).ToArray();
                state.Terrain.CopyFrom(new TerrainMask(GameConstants.WorldWidth, GameConstants.WorldHeight, heights));
                state.PlayerTank.Position = new Vector2(160, surfaceY);
                state.CpuTank.Position = new Vector2(1040, surfaceY);
                state.PlayerTank.AddWeapon(WeaponIds.BunkerBuster, 1);
                state.SelectedWeaponId = WeaponIds.BunkerBuster;
                state.Wind = 0;
                engine.StartBattle(state);

                var result = engine.FireCurrentTurn(state, settings, angle, power);
                if (result.Explosions.Count != 1 || result.Explosions[0].Center.Y <= surfaceY + 32f)
                    continue;

                Assert.Equal(WeaponIds.BunkerBuster, result.WeaponId);
                Assert.Equal(ShotVisualKind.Ballistic, result.Explosions[0].VisualKind);
                Assert.Equal(result.Trail[^1], result.Explosions[0].Center);
                Assert.True(result.Trail.Count > 12);
                return;
            }
        }

        Assert.Fail("Expected one deterministic Bunker Buster firing solution to burrow below the terrain surface.");
    }

    [Fact]
    public void MassiveOrdnancePenetratorProducesStagedPrimaryAndSecondaryExplosions()
    {
        var engine = CreateEngine();
        var settings = new MatchSettings(TerrainSeed: 123, EnableShop: false);
        var state = engine.NewMatch(settings);
        state.PlayerTank.AddWeapon(WeaponIds.Gbu57Mop, 1);
        state.SelectedWeaponId = WeaponIds.Gbu57Mop;
        engine.StartBattle(state);

        var result = engine.FireCurrentTurn(state, settings, angle: 42, power: 65);

        Assert.Equal(WeaponIds.Gbu57Mop, result.WeaponId);
        Assert.Equal(2, result.Explosions.Count);
        var primary = result.Explosions[0];
        var secondary = result.Explosions[1];
        Assert.Equal(ShotVisualKind.PenetratorPrimary, primary.VisualKind);
        Assert.Equal(ShotVisualKind.PenetratorSecondary, secondary.VisualKind);
        Assert.InRange(primary.TriggerTrailIndex, 0, result.Trail.Count - 1);
        Assert.Equal(-1, secondary.TriggerTrailIndex);
        Assert.True(secondary.DamageRadius > primary.DamageRadius);
        Assert.True(secondary.TerrainRadius > primary.TerrainRadius);
    }

    [Fact]
    public void SplitterMirvProducesSevenSpreadImpacts()
    {
        var engine = CreateEngine();
        var settings = new MatchSettings(TerrainSeed: 123, EnableShop: false);
        var state = engine.NewMatch(settings);
        state.PlayerTank.AddWeapon(WeaponIds.SplitterMirv, 1);
        state.SelectedWeaponId = WeaponIds.SplitterMirv;
        engine.StartBattle(state);

        var result = engine.FireCurrentTurn(state, settings, angle: 42, power: 65);

        Assert.Equal(WeaponIds.SplitterMirv, result.WeaponId);
        Assert.Equal(7, result.Explosions.Count);
        Assert.True(result.Trail.Count > 8);
        Assert.True(result.Explosions.Max(static explosion => explosion.Center.X) - result.Explosions.Min(static explosion => explosion.Center.X) > 150);
    }

    [Fact]
    public void DarkEagleGuidesDirectlyToOpponent()
    {
        var engine = CreateEngine();
        var settings = new MatchSettings(TerrainSeed: 123, EnableShop: false);
        var state = engine.NewMatch(settings);
        state.PlayerTank.AddWeapon(WeaponIds.DarkEagle, 1);
        state.SelectedWeaponId = WeaponIds.DarkEagle;
        engine.StartBattle(state);
        var targetBeforeShot = state.CpuTank.Center;

        var result = engine.FireCurrentTurn(state, settings, angle: 8, power: 1);

        Assert.Equal(WeaponIds.DarkEagle, result.WeaponId);
        Assert.NotEmpty(result.Trail);
        Assert.Equal(ShotVisualKind.Missile, Assert.Single(result.Explosions).VisualKind);
        Assert.True(result.Trail.Min(static point => point.Y) < targetBeforeShot.Y - 100);
        Assert.True(Vector2.Distance(result.Explosions[0].Center, targetBeforeShot) < 0.01f);
        Assert.True(state.CpuTank.Health < state.CpuTank.MaxHealth);
    }

    [Fact]
    public void TurnStartHazardDamageCanEndRoundAfterPlayerShot()
    {
        var engine = CreateEngine();
        var settings = new MatchSettings(TerrainSeed: 123, EnableShop: false);
        var state = engine.NewMatch(settings);
        engine.StartBattle(state);
        state.CpuTank.MaxHealth = 500;
        state.CpuTank.Health = 500;
        state.RadiationZones.Add(new RadiationZone(state.CpuTank.Center, 140, 2, 600, ShotVisualKind.Lava));

        var result = engine.FireCurrentTurn(state, settings, angle: 85, power: 1);

        Assert.True(result.RoundEnded);
        Assert.Equal(TurnOwner.Player, result.Winner);
        Assert.Equal(GamePhase.RoundOver, state.Phase);
        Assert.True(state.CpuTank.IsDestroyed);
        Assert.Contains(result.Events, e => e.Contains("Victory", StringComparison.Ordinal));
    }

    [Fact]
    public void TurnStartHazardDamageCanEndRoundAfterCpuShot()
    {
        var engine = CreateEngine();
        var settings = new MatchSettings(TerrainSeed: 123, EnableShop: false);
        var state = engine.NewMatch(settings);
        engine.StartBattle(state);
        state.CurrentTurn = TurnOwner.Cpu;
        state.PlayerTank.MaxHealth = 500;
        state.PlayerTank.Health = 500;
        state.RadiationZones.Add(new RadiationZone(state.PlayerTank.Center, 140, 2, 600, ShotVisualKind.Nuclear));
        var plan = new CpuShotPlan(WeaponIds.PeaShell, 95, 1, "", 0, 0);

        var result = engine.FirePlannedCpuTurn(state, settings, plan);

        Assert.True(result.RoundEnded);
        Assert.Equal(TurnOwner.Cpu, result.Winner);
        Assert.Equal(GamePhase.RoundOver, state.Phase);
        Assert.True(state.PlayerTank.IsDestroyed);
        Assert.Contains(result.Events, e => e.Contains("Defeat", StringComparison.Ordinal));
    }

    private static GameEngine CreateEngine() => new(new WeaponCatalog(), new UpgradeCatalog());
}
