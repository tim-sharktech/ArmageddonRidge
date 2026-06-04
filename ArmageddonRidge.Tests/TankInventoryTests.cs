using ArmageddonRidge.Core.Models;

namespace ArmageddonRidge.Tests;

public sealed class TankInventoryTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonFreeWeaponInventoryRejectsNonPositiveCounts(int count)
    {
        var tank = Tank();

        Assert.Throws<ArgumentOutOfRangeException>(() => tank.AddWeapon(WeaponIds.HeavyShell, count));
        Assert.Equal(0, tank.GetInventoryCount(WeaponIds.HeavyShell));
        Assert.False(tank.HasWeapon(WeaponIds.HeavyShell));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void PeaShellInventoryIgnoresNonPositiveCountsBecauseItIsUnlimited(int count)
    {
        var tank = Tank();

        tank.AddWeapon(WeaponIds.PeaShell, count);

        Assert.Equal(0, tank.GetInventoryCount(WeaponIds.PeaShell));
        Assert.True(tank.HasWeapon(WeaponIds.PeaShell));
    }

    [Fact]
    public void WeaponInventoryRejectsOverflowingStackCounts()
    {
        var tank = Tank();
        tank.AddWeapon(WeaponIds.HeavyShell, int.MaxValue);

        Assert.Throws<InvalidOperationException>(() => tank.AddWeapon(WeaponIds.HeavyShell, 1));
        Assert.Equal(int.MaxValue, tank.GetInventoryCount(WeaponIds.HeavyShell));
        Assert.True(tank.HasWeapon(WeaponIds.HeavyShell));
    }

    private static Tank Tank() => new()
    {
        Id = "test",
        Name = "Test Tank",
        TurretAngle = 45
    };
}
