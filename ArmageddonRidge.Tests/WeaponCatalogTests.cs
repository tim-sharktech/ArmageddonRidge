using ArmageddonRidge.Core.Content;
using ArmageddonRidge.Core.Models;

namespace ArmageddonRidge.Tests;

public sealed class WeaponCatalogTests
{
    [Fact]
    public void ModernArcadeWeaponsAreCatalogedWithDistinctIds()
    {
        var catalog = new WeaponCatalog();
        var darkEagle = catalog.Get(WeaponIds.DarkEagle);
        var drones = catalog.Get(WeaponIds.ShahedDroneSwarm);
        var mop = catalog.Get(WeaponIds.Gbu57Mop);

        Assert.Equal("Dark Eagle", darkEagle.DisplayName);
        Assert.Equal(WeaponBehaviorType.Missile, darkEagle.BehaviorType);
        Assert.True(darkEagle.ProjectileSpeedMultiplier > 1.5f);
        Assert.True(darkEagle.WindInfluence < 0.2f);

        Assert.Equal("Shahed Drone Swarm", drones.DisplayName);
        Assert.Equal(WeaponBehaviorType.DroneSwarm, drones.BehaviorType);
        Assert.Equal(WeaponCategory.Cluster, drones.Category);
        Assert.InRange(drones.ClusterCount, 3, 5);

        Assert.Equal("GBU-57 MOP", mop.DisplayName);
        Assert.Equal(WeaponBehaviorType.MultiStagePenetrator, mop.BehaviorType);
        Assert.InRange(mop.ProjectileSpeedMultiplier, 1f, 1.1f);
        Assert.InRange(mop.GravityInfluence, 1.1f, 1.25f);
        Assert.True(mop.WindInfluence < 0.1f);
        Assert.True(mop.TerrainRadius > mop.BlastRadius);
    }

    [Fact]
    public void NukesAreAffordableBeforeLateCampaign()
    {
        var catalog = new WeaponCatalog();

        Assert.InRange(catalog.Get(WeaponIds.TacticalNuke).Cost, 1, 900);
        Assert.InRange(catalog.Get(WeaponIds.DoomsdayNuke).Cost, 1, 1800);
    }

    [Fact]
    public void PatriotBatteryIsAvailableAsDefense()
    {
        var catalog = new UpgradeCatalog();
        var patriot = catalog.Get(UpgradeType.PatriotBattery);

        Assert.Equal("Patriot Battery", patriot.DisplayName);
        Assert.InRange(patriot.Cost, 1, 500);
    }

    [Fact]
    public void UpgradeCatalogOnlyAdvertisesImplementedUpgrades()
    {
        var catalog = new UpgradeCatalog();
        var upgradeTypes = catalog.All.Select(static upgrade => upgrade.Type).ToHashSet();

        Assert.DoesNotContain(UpgradeType.ReflectorShield, upgradeTypes);
        Assert.DoesNotContain(UpgradeType.Teleporter, upgradeTypes);
        Assert.DoesNotContain(UpgradeType.WindMeter, upgradeTypes);
        Assert.Throws<KeyNotFoundException>(() => catalog.Get(UpgradeType.ReflectorShield));
    }
}
