using ArmageddonRidge.Core.Game;
using ArmageddonRidge.Core.Models;
using Microsoft.AspNetCore.Components;

namespace ArmageddonRidge.Client.Pages;

public partial class ShopPanel
{
    [Parameter] public required IReadOnlyCollection<WeaponDefinition> Weapons { get; set; }
    [Parameter] public required IReadOnlyCollection<UpgradeDefinition> Upgrades { get; set; }
    [Parameter] public required GameState State { get; set; }
    [Parameter] public bool TargetingComputerEnabledByDefault { get; set; }
    [Parameter] public required EventCallback<string> OnBuyWeapon { get; set; }
    [Parameter] public required EventCallback<UpgradeType> OnBuyUpgrade { get; set; }
    [Parameter] public required EventCallback OnStartBattle { get; set; }

    private WeaponDefinition[] _visibleWeapons = [];
    private UpgradeDefinition[] _visibleUpgrades = [];

    protected override void OnParametersSet()
    {
        _visibleWeapons = BuildVisibleWeapons();
        _visibleUpgrades = BuildVisibleUpgrades();
    }

    private WeaponDefinition[] BuildVisibleWeapons()
    {
        var weapons = new List<WeaponDefinition>(Weapons.Count);
        foreach (var weapon in Weapons)
        {
            if (weapon.Cost > 0) weapons.Add(weapon);
        }

        return weapons.ToArray();
    }

    private UpgradeDefinition[] BuildVisibleUpgrades()
    {
        var upgrades = new List<UpgradeDefinition>(Upgrades.Count);
        foreach (var upgrade in Upgrades)
        {
            if (upgrade.Type == UpgradeType.TargetingComputer
                && (TargetingComputerEnabledByDefault || State.PlayerTank.Upgrades.Contains(UpgradeType.TargetingComputer)))
            {
                continue;
            }

            upgrades.Add(upgrade);
        }

        return upgrades.ToArray();
    }

    private Task BuyWeaponAsync(string weaponId) => OnBuyWeapon.InvokeAsync(weaponId);

    private Task BuyUpgradeAsync(UpgradeType upgradeType) => OnBuyUpgrade.InvokeAsync(upgradeType);

    private string UpgradeCountLabel(UpgradeType upgradeType) => upgradeType switch
    {
        UpgradeType.PatriotBattery when State.PlayerTank.PatriotBatteryCharges > 0 => $"{State.PlayerTank.PatriotBatteryCharges} ready",
        UpgradeType.TracerRounds when State.PlayerTank.TracerRoundCharges > 0 => $"{State.PlayerTank.TracerRoundCharges} trails",
        _ => string.Empty
    };

    private static string IconFor(string id) => $"assets/sprites/icons/{id.ToLowerInvariant()}.png";
}
