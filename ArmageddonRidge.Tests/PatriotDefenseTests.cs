using System.Numerics;
using ArmageddonRidge.Core;
using ArmageddonRidge.Core.Game;
using ArmageddonRidge.Core.Models;

namespace ArmageddonRidge.Tests;

public sealed class PatriotDefenseTests
{
    [Fact]
    public void PatriotDefenseInterceptsProjectedExplosionNearProtectedTank()
    {
        var tank = Tank("player", 300, 500);
        var projected = new[]
        {
            new ExplosionResult(new Vector2(310, 475), 35, 30, 0, 0, false, false, [], ShotVisualKind.Missile)
        };

        Assert.True(PatriotDefense.ShouldIntercept(tank, projected));
    }

    [Fact]
    public void PatriotDefenseIgnoresProjectedExplosionFarFromProtectedTank()
    {
        var tank = Tank("player", 300, 500);
        var projected = new[]
        {
            new ExplosionResult(new Vector2(700, 475), 35, 30, 0, 0, false, false, [], ShotVisualKind.Missile)
        };

        Assert.False(PatriotDefense.ShouldIntercept(tank, projected));
    }

    [Fact]
    public void PatriotDefenseInterceptsDangerousIncomingTrail()
    {
        var tank = Tank("player", 300, 500);
        var projected = new[]
        {
            new ExplosionResult(new Vector2(700, 475), 35, 30, 0, 0, false, false, [], ShotVisualKind.Missile)
        };
        var trail = new[]
        {
            new Vector2(900, 260),
            new Vector2(520, 330),
            new Vector2(320, 430),
            new Vector2(700, 475)
        };

        Assert.True(PatriotDefense.ShouldIntercept(tank, projected, trail));
    }

    [Fact]
    public void PatriotDefenseChoosesTrailApexAsInterceptPoint()
    {
        var tank = Tank("player", 300, 500);
        var trail = new[]
        {
            new Vector2(100, 300),
            new Vector2(260, 430),
            new Vector2(470, 240),
            new Vector2(900, 300)
        };

        Assert.Equal(trail[2], PatriotDefense.InterceptPoint(tank, trail));
    }

    [Fact]
    public void PatriotDefenseWaitsForReadableInterceptPointWhenApexIsOffscreen()
    {
        var tank = Tank("player", 300, 500);
        var trail = new[]
        {
            new Vector2(940, 420),
            new Vector2(780, 120),
            new Vector2(620, -80),
            new Vector2(470, 86),
            new Vector2(335, 270)
        };

        Assert.Equal(trail[4], PatriotDefense.InterceptPoint(tank, trail));
    }

    [Fact]
    public void PatriotDefenseClampsInterceptPointWhenTrailNeverReturnsToScreen()
    {
        var tank = Tank("player", 300, 500);
        var trail = new[]
        {
            new Vector2(40, -120),
            new Vector2(260, -180),
            new Vector2(460, -90)
        };

        Assert.Equal(new Vector2(260, 54), PatriotDefense.InterceptPoint(tank, trail));
    }

    private static Tank Tank(string id, float x, float y) => new()
    {
        Id = id,
        Name = id,
        Position = new Vector2(x, y),
        TurretAngle = 45
    };
}
