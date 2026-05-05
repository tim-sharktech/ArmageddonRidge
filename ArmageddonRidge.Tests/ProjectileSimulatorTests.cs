using System.Numerics;
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
