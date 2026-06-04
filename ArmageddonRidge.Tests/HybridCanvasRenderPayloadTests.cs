using System.Numerics;
using ArmageddonRidge.Client.Services.Rendering;
using ArmageddonRidge.Core.Models;

namespace ArmageddonRidge.Tests;

public sealed class HybridCanvasRenderPayloadTests
{
    [Fact]
    public void ShotPlaybackPayloadDropsNonFiniteValuesBeforeJsInterop()
    {
        var trail = new[]
        {
            new Vector2(10, 20),
            new Vector2(float.NaN, 30),
            new Vector2(40, float.PositiveInfinity),
            new Vector2(50, 60)
        };
        var explosions = new[]
        {
            new ExplosionResult(new Vector2(float.NaN, 90), 40, 30, 0, 0, false, false, [], ShotVisualKind.Laser),
            new ExplosionResult(new Vector2(80, 90), float.PositiveInfinity, float.NaN, 0, 0, false, false, [], ShotVisualKind.Laser, TriggerTrailIndex: 2),
            new ExplosionResult(new Vector2(100, 110), 42, 50, 0, 0, false, false, [], ShotVisualKind.PatriotIntercept)
        };

        var trailPayload = RenderPayloadSanitizer.BuildTrailPayload(trail);
        var explosionPayload = RenderPayloadSanitizer.BuildExplosionPayload(explosions, WeaponIds.LaserLance);
        var options = RenderPayloadSanitizer.BuildPlaybackOptions(
            intercepted: true,
            new Vector2(float.NaN, 20),
            ownerTankId: "cpu",
            visualKind: ShotVisualKind.PatriotIntercept.ToString());

        Assert.Collection(
            trailPayload,
            point => Assert.Equal(new ShotPointPayload(10, 20), point),
            point => Assert.Equal(new ShotPointPayload(50, 60), point));
        Assert.Collection(
            explosionPayload,
            explosion =>
            {
                Assert.Equal(80, explosion.x);
                Assert.Equal(90, explosion.y);
                Assert.Equal(32, explosion.radius);
                Assert.Equal(0, explosion.terrainRadius);
                Assert.Equal(2, explosion.triggerIndex);
                Assert.True(explosion.laserLike());
            },
            explosion =>
            {
                Assert.Equal(100, explosion.x);
                Assert.Equal(110, explosion.y);
                Assert.Equal(42, explosion.radius);
                Assert.True(explosion.patriotIntercept);
            });
        Assert.False(options.intercepted);
        Assert.Null(options.interceptX);
        Assert.Null(options.interceptY);
    }

    [Fact]
    public void RenderTrailPayloadDropsNonFiniteTracerPointsBeforeSceneInterop()
    {
        var trail = new[]
        {
            new Vector2(float.NaN, 10),
            new Vector2(0, 20),
            new Vector2(10, 25),
            new Vector2(20, float.NegativeInfinity),
            new Vector2(30, 35),
            new Vector2(40, 45)
        };

        var payload = RenderPayloadSanitizer.BuildRenderTrailPayload(trail, maxPoints: 3);
        var invalidPayload = RenderPayloadSanitizer.BuildRenderTrailPayload(
            [new Vector2(float.NaN, 10), new Vector2(20, float.PositiveInfinity)],
            maxPoints: 3);

        Assert.Collection(
            payload,
            point => Assert.Equal(new RenderPoint(0, 20), point),
            point => Assert.Equal(new RenderPoint(30, 35), point),
            point => Assert.Equal(new RenderPoint(40, 45), point));
        Assert.Empty(invalidPayload);
    }

    [Fact]
    public void EffectPayloadDropsNonFiniteValuesBeforeWebGpuInterop()
    {
        var trail = new[]
        {
            new Vector2(5, 10),
            new Vector2(float.NaN, 15),
            new Vector2(25, 30),
            new Vector2(45, float.PositiveInfinity),
            new Vector2(65, 70)
        };
        var explosions = new[]
        {
            new ExplosionResult(new Vector2(float.PositiveInfinity, 90), 60, 80, 0, 0, false, false, [], ShotVisualKind.Nuclear),
            new ExplosionResult(new Vector2(100, 120), float.NaN, float.NegativeInfinity, float.NaN, float.PositiveInfinity, false, true, [], ShotVisualKind.Nuclear),
            new ExplosionResult(new Vector2(160, 180), 44, 72, 12, 8, true, false, [], ShotVisualKind.Lava, TriggerTrailIndex: 3)
        };

        var trailPayload = RenderPayloadSanitizer.BuildEffectTrailPayload(trail, maxPoints: 2);
        var explosionPayload = RenderPayloadSanitizer.BuildEffectExplosionPayload(explosions);
        var validIntercept = RenderPayloadSanitizer.TryGetFinitePoint(new Vector2(12, 24), out var interceptX, out var interceptY);
        var invalidIntercept = RenderPayloadSanitizer.TryGetFinitePoint(new Vector2(float.NaN, 24), out _, out _);

        Assert.Collection(
            trailPayload,
            point => Assert.Equal(new EffectPointPayload(5, 10), point),
            point => Assert.Equal(new EffectPointPayload(65, 70), point));
        Assert.Collection(
            explosionPayload,
            explosion =>
            {
                Assert.Equal(100, explosion.X);
                Assert.Equal(120, explosion.Y);
                Assert.Equal(32, explosion.Radius);
                Assert.Equal(0, explosion.TerrainRadius);
                Assert.Equal(0, explosion.PlayerDamage);
                Assert.Equal(0, explosion.CpuDamage);
                Assert.True(explosion.Nuclear);
            },
            explosion =>
            {
                Assert.Equal(160, explosion.X);
                Assert.Equal(180, explosion.Y);
                Assert.Equal(44, explosion.Radius);
                Assert.Equal(72, explosion.TerrainRadius);
                Assert.True(explosion.Dirt);
                Assert.Equal(ShotVisualKind.Lava.ToString(), explosion.VisualKind);
                Assert.Equal(3, explosion.TriggerIndex);
            });
        Assert.True(validIntercept);
        Assert.Equal(12, interceptX);
        Assert.Equal(24, interceptY);
        Assert.False(invalidIntercept);
    }
}

file static class ShotExplosionPayloadTestExtensions
{
    public static bool laserLike(this ShotExplosionPayload payload) =>
        payload.visualKind == ShotVisualKind.Laser.ToString()
        && payload.weaponId == WeaponIds.LaserLance;
}
