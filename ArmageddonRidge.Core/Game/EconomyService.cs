using ArmageddonRidge.Core.Content;
using ArmageddonRidge.Core.Models;

namespace ArmageddonRidge.Core.Game;

/// <summary>
/// Applies shop purchases and end-of-round cash rewards.
/// </summary>
/// <param name="weapons">Weapon catalog used for price and inventory lookups.</param>
/// <param name="upgrades">Upgrade catalog used for price and upgrade effects.</param>
public sealed class EconomyService(WeaponCatalog weapons, UpgradeCatalog upgrades)
{
    private readonly WeaponCatalog _weapons = weapons;
    private readonly UpgradeCatalog _upgrades = upgrades;

    /// <summary>
    /// Attempts to buy one or more copies of a weapon for a tank.
    /// </summary>
    public bool BuyWeapon(Tank tank, string weaponId, int count = 1)
    {
        if (count <= 0) return false;

        var weapon = _weapons.Get(weaponId);
        var total = (long)weapon.Cost * count;
        if (weapon.Cost <= 0 || tank.Cash < total) return false;
        if ((long)tank.GetInventoryCount(weaponId) + count > int.MaxValue) return false;

        tank.AddWeapon(weaponId, count);
        tank.Cash -= (int)total;
        return true;
    }

    /// <summary>
    /// Attempts to buy and immediately apply a defensive or utility upgrade.
    /// </summary>
    public bool BuyUpgrade(Tank tank, UpgradeType upgradeType)
    {
        var upgrade = _upgrades.Get(upgradeType);
        if (tank.Cash < upgrade.Cost) return false;
        if (!CanApplyUpgrade(tank, upgradeType)) return false;

        ApplyUpgrade(tank, upgradeType);
        tank.Cash -= upgrade.Cost;
        return true;
    }

    private static bool CanApplyUpgrade(Tank tank, UpgradeType upgradeType) => upgradeType switch
    {
        UpgradeType.TracerRounds => tank.TracerRoundCharges < int.MaxValue,
        UpgradeType.PatriotBattery => tank.PatriotBatteryCharges < int.MaxValue,
        _ => true
    };

    private static void ApplyUpgrade(Tank tank, UpgradeType upgradeType)
    {
        tank.Upgrades.Add(upgradeType);
        switch (upgradeType)
        {
            case UpgradeType.LightShield:
                tank.Shield = MathF.Max(tank.Shield, 50);
                break;
            case UpgradeType.HeavyShield:
                tank.Shield = MathF.Max(tank.Shield, 120);
                break;
            case UpgradeType.Parachute:
                tank.HasParachute = true;
                break;
            case UpgradeType.RepairKit:
                tank.Health = (int)Math.Min(tank.MaxHealth, (long)tank.Health + 35);
                break;
            case UpgradeType.Battery:
                tank.Shield += 25;
                break;
            case UpgradeType.TracerRounds:
                tank.TracerRoundCharges++;
                break;
            case UpgradeType.PatriotBattery:
                tank.PatriotBatteryCharges++;
                break;
        }
    }

    /// <summary>
    /// Awards player cash for the completed round and accumulated damage.
    /// </summary>
    public void AwardRound(GameState state, TurnOwner winner)
    {
        var playerWon = winner == TurnOwner.Player;
        AddCash(state.PlayerTank, playerWon ? GameConstants.WinReward + GameConstants.KillBonus : GameConstants.LossConsolation);
        AddCash(state.PlayerTank, DamageReward(state.DamageDealtByPlayer));
    }

    private static int DamageReward(float damageDealt)
    {
        if (!float.IsFinite(damageDealt) || damageDealt <= 0) return 0;

        var reward = MathF.Floor(damageDealt / 10f) * 10f;
        return reward >= int.MaxValue ? int.MaxValue : (int)reward;
    }

    private static void AddCash(Tank tank, int amount)
    {
        if (amount <= 0) return;

        var next = (long)tank.Cash + amount;
        tank.Cash = next >= int.MaxValue ? int.MaxValue : (int)next;
    }
}
