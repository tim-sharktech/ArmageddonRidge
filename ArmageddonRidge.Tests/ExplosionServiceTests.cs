using System.Numerics;
using ArmageddonRidge.Core;
using ArmageddonRidge.Core.Models;
using ArmageddonRidge.Core.Physics;

namespace ArmageddonRidge.Tests;

public sealed class ExplosionServiceTests
{
    [Fact]
    public void RadiationDamageIgnoresMalformedZones()
    {
        var service = new ExplosionService();
        var tank = new Tank { Id = "player", Name = "Player", Position = new Vector2(200, 600) };
        var zones = new List<RadiationZone>
        {
            new(new Vector2(float.NaN, 600), 120, 2, 15, ShotVisualKind.Nuclear, "cpu"),
            new(new Vector2(200, 600), float.PositiveInfinity, 2, 15, ShotVisualKind.Nuclear, "cpu"),
            new(new Vector2(200, 600), 120, 2, float.NaN, ShotVisualKind.Nuclear, "cpu"),
            new(new Vector2(200, 600), 120, 2, -8, ShotVisualKind.Nuclear, "cpu"),
            new(new Vector2(200, 600), 120, 2, 7, ShotVisualKind.Lava, "cpu")
        };
        var credited = 0f;

        var total = service.ApplyRadiation(tank, zones, (_, damage) => credited += damage);

        Assert.Equal(7, total);
        Assert.Equal(7, credited);
        Assert.Equal(GameConstants.StartingHealth - 7, tank.Health);
    }
}
