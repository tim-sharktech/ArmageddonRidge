using System.Numerics;
using ArmageddonRidge.Core;
using ArmageddonRidge.Core.Content;
using ArmageddonRidge.Core.Game;
using ArmageddonRidge.Core.Models;
using ArmageddonRidge.Core.Terrain;

namespace ArmageddonRidge.Tests;

public sealed class GameEngineTests
{
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

    private static GameEngine CreateEngine() => new(new WeaponCatalog(), new UpgradeCatalog());
}
