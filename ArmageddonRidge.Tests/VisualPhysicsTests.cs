using System.Numerics;
using ArmageddonRidge.Core;
using ArmageddonRidge.Core.Content;
using ArmageddonRidge.Core.Game;
using ArmageddonRidge.Core.Models;
using ArmageddonRidge.Core.Physics;
using ArmageddonRidge.Core.Terrain;

namespace ArmageddonRidge.Tests;

public sealed class VisualPhysicsTests
{
    [Fact]
    public void SlumpingDoesNothingBelowSlopeThreshold()
    {
        var terrain = new TerrainMask(8, 100, [60, 61, 62, 63, 64, 65, 66, 67]);
        var service = new TerrainSlumpingService();

        var result = service.Relax(terrain, [Explosion(3, 60, 30)], mode: TerrainRelaxationMode.Scalar);

        Assert.Empty(result.Columns);
        Assert.Equal([60, 61, 62, 63, 64, 65, 66, 67], terrain.SolidTop);
    }

    [Fact]
    public void SteepCraterWallsRelaxDeterministically()
    {
        var heights = new float[] { 60, 60, 60, 92, 92, 60, 60, 60 };
        var first = new TerrainMask(8, 100, heights);
        var second = new TerrainMask(8, 100, heights);
        var service = new TerrainSlumpingService();

        var firstPayload = service.Relax(first, [Explosion(3, 60, 35)], mode: TerrainRelaxationMode.Scalar);
        var secondPayload = service.Relax(second, [Explosion(3, 60, 35)], mode: TerrainRelaxationMode.Scalar);

        Assert.NotEmpty(firstPayload.Columns);
        Assert.Equal(first.SolidTop, second.SolidTop);
        Assert.Equal(firstPayload.Columns, secondPayload.Columns);
        Assert.All(first.SolidTop, y => Assert.InRange(y, 0, 100));
    }

    [Fact]
    public void SimdAndScalarSlumpingMatch()
    {
        var heights = Enumerable.Range(0, 96)
            .Select(static x => 520f + (x % 9 == 0 ? 38f : 0) + MathF.Sin(x * 0.2f) * 8f)
            .ToArray();
        var scalar = new TerrainMask(96, 700, heights);
        var simd = new TerrainMask(96, 700, heights);
        var service = new TerrainSlumpingService();

        var scalarPayload = service.Relax(scalar, [Explosion(48, 520, 50)], mode: TerrainRelaxationMode.Scalar);
        var simdPayload = service.Relax(simd, [Explosion(48, 520, 50)], mode: TerrainRelaxationMode.Simd);

        Assert.Equal(scalarPayload.Columns.Length, simdPayload.Columns.Length);
        for (var i = 0; i < scalar.SolidTop.Count; i++)
            Assert.InRange(simd.SolidTop[i], scalar.SolidTop[i] - 0.0001f, scalar.SolidTop[i] + 0.0001f);
    }

    [Fact]
    public void TankPoseTiltsWithLocalSlopeAndStaysFiniteAtEdges()
    {
        var terrain = new TerrainMask(12, 100, [80, 78, 76, 74, 72, 70, 68, 66, 64, 62, 60, 58]);
        var service = new TankPoseService();
        var tank = Tank("player", 1, terrain, 42);

        var pose = service.BuildPose(tank, terrain, []);

        Assert.True(MathF.Abs(pose.HullAngleDegrees) > 0.1f);
        Assert.True(float.IsFinite(pose.HullAngleDegrees));
        Assert.True(float.IsFinite(pose.VerticalOffset));
    }

    [Fact]
    public void FlatTerrainYieldsNearZeroTankTilt()
    {
        var terrain = new TerrainMask(12, 100, Enumerable.Repeat(80f, 12).ToArray());
        var service = new TankPoseService();

        var pose = service.BuildPose(Tank("player", 6, terrain, 42), terrain, []);

        Assert.InRange(pose.HullAngleDegrees, -0.001f, 0.001f);
    }

    [Fact]
    public void RecoilOpposesFiringDirectionAndHitRockFollowsImpulse()
    {
        var terrain = new TerrainMask(120, 100, Enumerable.Repeat(80f, 120).ToArray());
        var service = new TankPoseService();
        var tank = Tank("player", 60, terrain, 0);
        var impulse = new ShockwaveImpulsePayload(20, 70, 120, 120, 1, 0, 1, "Ballistic");

        var pose = service.BuildPose(tank, terrain, [impulse], firingTankId: "player");

        Assert.True(pose.RecoilX < 0);
        Assert.True(MathF.Abs(pose.RecoilX) >= 20);
        Assert.True(pose.SuspensionCompression > 0.7f);
        Assert.True(pose.RockAngleDegrees > 0);
    }

    [Fact]
    public void ShockwaveImpulseDecreasesWithDistanceAndClampsReducedMotion()
    {
        var terrain = new TerrainMask(200, 100, Enumerable.Repeat(80f, 200).ToArray());
        var service = new ShockwaveImpulseService();

        var full = service.Build([Explosion(50, 70, 80)], terrain, reducedMotion: false);
        var reduced = service.Build([Explosion(50, 70, 80)], terrain, reducedMotion: true);

        Assert.Single(full);
        Assert.True(full[0].Intensity > reduced[0].Intensity);
        Assert.True(full[0].Radius > 0);
    }

    [Fact]
    public void ProjectileAirProfilesKeepZeroDragBallisticsAndWindAffectsLightProfilesMore()
    {
        var terrain = new TerrainMask(GameConstants.WorldWidth, GameConstants.WorldHeight, Enumerable.Repeat(680f, GameConstants.WorldWidth).ToArray());
        var player = Tank("player", 160, terrain, 42);
        var cpu = Tank("cpu", 980, terrain, 138);
        var catalog = new WeaponCatalog();
        var simulator = new ProjectileSimulator();

        var peaLeft = simulator.Simulate(terrain, player, cpu, catalog.Get(WeaponIds.PeaShell), 42, 70, -40);
        var peaRight = simulator.Simulate(terrain, player, cpu, catalog.Get(WeaponIds.PeaShell), 42, 70, 40);
        var heavyLeft = simulator.Simulate(terrain, player, cpu, catalog.Get(WeaponIds.HeavyShell), 42, 70, -40);
        var heavyRight = simulator.Simulate(terrain, player, cpu, catalog.Get(WeaponIds.HeavyShell), 42, 70, 40);

        Assert.All(peaLeft.Trail.Concat(peaRight.Trail), point =>
        {
            Assert.True(float.IsFinite(point.X));
            Assert.True(float.IsFinite(point.Y));
        });
        Assert.True(MathF.Abs(peaRight.ImpactPoint.X - peaLeft.ImpactPoint.X) > MathF.Abs(heavyRight.ImpactPoint.X - heavyLeft.ImpactPoint.X));
    }

    [Fact]
    public void NuclearAirProfilePreservesReadableBallisticRange()
    {
        var terrain = new TerrainMask(GameConstants.WorldWidth, GameConstants.WorldHeight, Enumerable.Repeat(680f, GameConstants.WorldWidth).ToArray());
        var player = Tank("player", 160, terrain, 42);
        var cpu = Tank("cpu", 980, terrain, 138);
        var weapon = new WeaponCatalog().Get(WeaponIds.TacticalNuke);
        var simulator = new ProjectileSimulator();

        var nuke = simulator.Simulate(terrain, player, cpu, weapon, 42, 70, 0);
        Assert.InRange(nuke.ImpactPoint.X, 650, 820);
        Assert.True(nuke.Trail.Count > 40);
    }

    [Theory]
    [InlineData(WeaponIds.SplitterMirv, 720, 900)]
    [InlineData(WeaponIds.Gbu57Mop, 680, 850)]
    [InlineData(WeaponIds.NapalmFlask, 580, 730)]
    public void SpecializedWeaponsPreserveReadableLongRange(string weaponId, float minimumX, float maximumX)
    {
        var terrain = new TerrainMask(GameConstants.WorldWidth, GameConstants.WorldHeight, Enumerable.Repeat(680f, GameConstants.WorldWidth).ToArray());
        var player = Tank("player", 160, terrain, 42);
        var cpu = Tank("cpu", 980, terrain, 138);
        var weapon = new WeaponCatalog().Get(weaponId);
        var simulator = new ProjectileSimulator();

        var shot = simulator.Simulate(terrain, player, cpu, weapon, 42, 70, 0);

        Assert.InRange(shot.ImpactPoint.X, minimumX, maximumX);
        Assert.True(shot.Trail.Count > 70);
        Assert.All(shot.Trail, point =>
        {
            Assert.True(float.IsFinite(point.X));
            Assert.True(float.IsFinite(point.Y));
        });
    }

    [Fact]
    public void GameResolutionCarriesVisualPhysicsPayload()
    {
        var engine = new GameEngine(new WeaponCatalog(), new UpgradeCatalog());
        var settings = new MatchSettings(TerrainSeed: 12345, EnableShop: false);
        var state = engine.NewMatch(settings);
        engine.StartBattle(state);

        var resolution = engine.FireCurrentTurn(state, settings, 45, 70);

        Assert.NotNull(resolution.VisualPhysics);
        Assert.True(resolution.VisualPhysics!.TankPoses.Length >= 2);
        Assert.True(resolution.Performance.VisualPhysicsPrepMs >= 0);
    }

    private static ExplosionResult Explosion(float x, float y, float radius) =>
        new(new Vector2(x, y), radius, radius, 0, 0, false, false, [], ShotVisualKind.Ballistic);

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
