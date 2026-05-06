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
        var weapon = _weapons.Get(weaponId);
        var total = weapon.Cost * count;
        if (weapon.Cost <= 0 || tank.Cash < total) return false;

        tank.Cash -= total;
        tank.AddWeapon(weaponId, count);
        return true;
    }

    /// <summary>
    /// Attempts to buy and immediately apply a defensive or utility upgrade.
    /// </summary>
    public bool BuyUpgrade(Tank tank, UpgradeType upgradeType)
    {
        var upgrade = _upgrades.Get(upgradeType);
        if (tank.Cash < upgrade.Cost) return false;

        tank.Cash -= upgrade.Cost;
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
                tank.Health = Math.Min(tank.MaxHealth, tank.Health + 35);
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

        return true;
    }

    /// <summary>
    /// Awards player cash for the completed round and accumulated damage.
    /// </summary>
    public void AwardRound(GameState state, TurnOwner winner)
    {
        var playerWon = winner == TurnOwner.Player;
        state.PlayerTank.Cash += playerWon ? GameConstants.WinReward + GameConstants.KillBonus : GameConstants.LossConsolation;
        var hitReward = (int)MathF.Floor(state.DamageDealtByPlayer / 10f) * 10;
        state.PlayerTank.Cash += hitReward;
    }
}
