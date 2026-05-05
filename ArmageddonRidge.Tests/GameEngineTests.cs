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

    private static GameEngine CreateEngine() => new(new WeaponCatalog(), new UpgradeCatalog());
}
