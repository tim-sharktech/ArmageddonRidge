using System.Numerics;
using ArmageddonRidge.Core;
using ArmageddonRidge.Core.Content;
using ArmageddonRidge.Core.Game;
using ArmageddonRidge.Core.Models;
using ArmageddonRidge.Core.Physics;
using ArmageddonRidge.Core.Terrain;

namespace ArmageddonRidge.Tests;

public sealed class ProjectileSimulatorTests
{
    [Fact]
    public void ProjectilePathIsDeterministicForSameInputs()
    {
        var terrain = new TerrainGenerator().Generate(12345);
        var player = Tank("player", 160, terrain, 42);
        var cpu = Tank("cpu", 980, terrain, 138);
        var weapon = new WeaponCatalog().Get(WeaponIds.PeaShell);
        var simulator = new ProjectileSimulator();

        var first = simulator.Simulate(terrain, player, cpu, weapon, 42, 70, 12);
        var second = simulator.Simulate(terrain, player, cpu, weapon, 42, 70, 12);

        Assert.Equal(first.ImpactPoint, second.ImpactPoint);
        Assert.Equal(first.Trail.Count, second.Trail.Count);
    }

    [Fact]
    public void WindChangesProjectileDrift()
    {
        var terrain = new TerrainGenerator().Generate(12345);
        var player = Tank("player", 160, terrain, 42);
        var cpu = Tank("cpu", 980, terrain, 138);
        var weapon = new WeaponCatalog().Get(WeaponIds.PeaShell);
        var simulator = new ProjectileSimulator();

        var leftWind = simulator.Simulate(terrain, player, cpu, weapon, 42, 70, -40);
        var rightWind = simulator.Simulate(terrain, player, cpu, weapon, 42, 70, 40);

        Assert.NotEqual(leftWind.ImpactPoint.X, rightWind.ImpactPoint.X);
    }

    [Fact]
    public void PlanningSimulationSkipsTrailCapture()
    {
        var terrain = new TerrainGenerator().Generate(12345);
        var player = Tank("player", 160, terrain, 42);
        var cpu = Tank("cpu", 980, terrain, 138);
        var weapon = new WeaponCatalog().Get(WeaponIds.PeaShell);
        var simulator = new ProjectileSimulator();

        var visual = simulator.Simulate(terrain, player, cpu, weapon, 42, 70, 12);
        var planning = simulator.SimulateForPlanning(terrain, player, cpu, weapon, 42, 70, 12);

        Assert.NotEmpty(visual.Trail);
        Assert.Equal(visual.ImpactPoint, planning.ImpactPoint);
        Assert.Equal(visual.StopReason, planning.StopReason);
    }

    [Fact]
    public void SweptCollisionHitsSpriteSizedTankBody()
    {
        var heights = Enumerable.Repeat(680f, GameConstants.WorldWidth).ToArray();
        var terrain = new TerrainMask(GameConstants.WorldWidth, GameConstants.WorldHeight, heights);
        var player = Tank("player", 160, terrain, 0);
        var cpu = Tank("cpu", 260, terrain, 180);
        player.Position = new Vector2(160, 620);
        cpu.Position = new Vector2(260, 620);
        var weapon = new WeaponCatalog().Get(WeaponIds.PeaShell);
        var simulator = new ProjectileSimulator();

        var result = simulator.Simulate(terrain, player, cpu, weapon, 0, 100, 0, maxSteps: 60);

        Assert.Equal(ProjectileStopReason.TankHit, result.StopReason);
        Assert.InRange(result.ImpactPoint.X, cpu.Position.X - (GameConstants.TankCollisionWidth / 2f) - GameConstants.ProjectileCollisionRadius, cpu.Position.X);
    }

    [Fact]
    public void SweptCollisionHitsCivilianStructureBeforeTank()
    {
        var heights = Enumerable.Repeat(680f, GameConstants.WorldWidth).ToArray();
        var terrain = new TerrainMask(GameConstants.WorldWidth, GameConstants.WorldHeight, heights);
        var player = Tank("player", 160, terrain, 0);
        var cpu = Tank("cpu", 430, terrain, 180);
        player.Position = new Vector2(160, 620);
        cpu.Position = new Vector2(430, 620);
        var structure = new CivilianStructure
        {
            Id = "test-tower",
            Position = new Vector2(280, 620),
            Kind = "tower",
            Width = 56,
            Height = 118,
            MaxHealth = 100,
            Health = 100,
            PenaltyValue = 180
        };
        var weapon = new WeaponCatalog().Get(WeaponIds.PeaShell);
        var simulator = new ProjectileSimulator();

        var result = simulator.Simulate(terrain, player, cpu, weapon, 0, 100, 0, maxSteps: 60, civilianStructures: [structure]);

        Assert.Equal(ProjectileStopReason.CivilianStructureHit, result.StopReason);
        Assert.InRange(result.ImpactPoint.X, structure.Position.X - (structure.Width / 2f) - GameConstants.ProjectileCollisionRadius, structure.Position.X);
    }

    [Fact]
    public void ShieldedTankProjectileImpactsBubbleBeforeHull()
    {
        var heights = Enumerable.Repeat(680f, GameConstants.WorldWidth).ToArray();
        var terrain = new TerrainMask(GameConstants.WorldWidth, GameConstants.WorldHeight, heights);
        var player = Tank("player", 160, terrain, 0);
        var cpu = Tank("cpu", 300, terrain, 180);
        player.Position = new Vector2(160, 620);
        cpu.Position = new Vector2(300, 620);
        cpu.Shield = 120;
        var weapon = new WeaponCatalog().Get(WeaponIds.PeaShell);
        var simulator = new ProjectileSimulator();

        var result = simulator.Simulate(terrain, player, cpu, weapon, 0, 100, 0, maxSteps: 60);

        Assert.Equal(ProjectileStopReason.ShieldHit, result.StopReason);
        Assert.InRange(result.ImpactPoint.X, 215, cpu.Position.X - (GameConstants.TankCollisionWidth / 2f));
    }

    [Fact]
    public void UnshieldedTankProjectileStillImpactsHull()
    {
        var heights = Enumerable.Repeat(680f, GameConstants.WorldWidth).ToArray();
        var terrain = new TerrainMask(GameConstants.WorldWidth, GameConstants.WorldHeight, heights);
        var player = Tank("player", 160, terrain, 0);
        var cpu = Tank("cpu", 300, terrain, 180);
        player.Position = new Vector2(160, 620);
        cpu.Position = new Vector2(300, 620);
        var weapon = new WeaponCatalog().Get(WeaponIds.PeaShell);
        var simulator = new ProjectileSimulator();

        var result = simulator.Simulate(terrain, player, cpu, weapon, 0, 100, 0, maxSteps: 60);

        Assert.Equal(ProjectileStopReason.TankHit, result.StopReason);
        Assert.InRange(result.ImpactPoint.X, cpu.Position.X - (GameConstants.TankCollisionWidth / 2f) - GameConstants.ProjectileCollisionRadius, cpu.Position.X);
    }

    [Fact]
    public void ProjectileOutsideShieldEllipseDoesNotHitShield()
    {
        var heights = Enumerable.Repeat(680f, GameConstants.WorldWidth).ToArray();
        var terrain = new TerrainMask(GameConstants.WorldWidth, GameConstants.WorldHeight, heights);
        var player = Tank("player", 160, terrain, 0);
        var cpu = Tank("cpu", 300, terrain, 180);
        player.Position = new Vector2(160, 500);
        cpu.Position = new Vector2(300, 620);
        cpu.Shield = 120;
        var weapon = new WeaponCatalog().Get(WeaponIds.PeaShell);
        var simulator = new ProjectileSimulator();

        var result = simulator.Simulate(terrain, player, cpu, weapon, 0, 100, 0, maxSteps: 60);

        Assert.NotEqual(ProjectileStopReason.ShieldHit, result.StopReason);
        Assert.NotEqual(ProjectileStopReason.TankHit, result.StopReason);
    }

    [Fact]
    public void ProjectileDoesNotHitBuriedShieldArea()
    {
        var heights = Enumerable.Repeat(680f, GameConstants.WorldWidth).ToArray();
        for (var x = 220; x <= 360; x++) heights[x] = 600f;
        var terrain = new TerrainMask(GameConstants.WorldWidth, GameConstants.WorldHeight, heights);
        var player = Tank("player", 160, terrain, 0);
        var cpu = Tank("cpu", 300, terrain, 180);
        player.Position = new Vector2(160, 650);
        cpu.Position = new Vector2(300, 650);
        cpu.Shield = 120;
        var weapon = new WeaponCatalog().Get(WeaponIds.PeaShell);
        var simulator = new ProjectileSimulator();

        var result = simulator.Simulate(terrain, player, cpu, weapon, 0, 100, 0, maxSteps: 60);

        Assert.NotEqual(ProjectileStopReason.ShieldHit, result.StopReason);
        Assert.Equal(ProjectileStopReason.TerrainHit, result.StopReason);
    }

    [Fact]
    public void OwnerCollisionArmsOnlyAfterProjectileClearsLaunchTank()
    {
        var heights = Enumerable.Repeat(680f, GameConstants.WorldWidth).ToArray();
        var terrain = new TerrainMask(GameConstants.WorldWidth, GameConstants.WorldHeight, heights);
        var player = Tank("player", 160, terrain, 5);
        var cpu = Tank("cpu", 900, terrain, 175);
        player.Position = new Vector2(160, 620);
        cpu.Position = new Vector2(900, 620);
        var weapon = new WeaponCatalog().Get(WeaponIds.PeaShell);
        var simulator = new ProjectileSimulator();

        var result = simulator.Simulate(terrain, player, cpu, weapon, 5, 1, 0, maxSteps: 180);

        Assert.NotEqual(ProjectileStopReason.OwnerHit, result.StopReason);
    }

    private static Tank Tank(string id, float x, TerrainMask terrain, float angle)
    {
        var tank = new Tank
        {
            Id = id,
            Name = id,
            Position = new Vector2(x, terrain.GetSurfaceY(x)),
            TurretAngle = angle
        };
        tank.AddWeapon(WeaponIds.PeaShell, -1);
        return tank;
    }
}
