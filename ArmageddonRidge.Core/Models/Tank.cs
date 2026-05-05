using System.Numerics;

namespace ArmageddonRidge.Core.Models;

public sealed class Tank
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public Vector2 Position { get; set; }
    public float TurretAngle { get; set; }
    public int Health { get; set; } = GameConstants.StartingHealth;
    public int MaxHealth { get; set; } = GameConstants.StartingHealth;
    public float Shield { get; set; }
    public int Cash { get; set; } = GameConstants.StartingCash;
    public bool IsCpu { get; init; }
    public bool HasParachute { get; set; }
    public Dictionary<string, int> Inventory { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<UpgradeType> Upgrades { get; } = [];

    public Vector2 Center => new(Position.X, Position.Y - (GameConstants.TankHeight / 2f));

    public bool IsDestroyed => Health <= 0;

    public int GetInventoryCount(string weaponId)
    {
        if (!Inventory.TryGetValue(weaponId, out var count))
        {
            return 0;
        }

        return count;
    }

    public bool HasWeapon(string weaponId) => weaponId == WeaponIds.PeaShell || GetInventoryCount(weaponId) > 0;

    public void AddWeapon(string weaponId, int count)
    {
        if (!Inventory.TryAdd(weaponId, count))
        {
            Inventory[weaponId] += count;
        }
    }

    public void ConsumeWeapon(string weaponId)
    {
        if (weaponId == WeaponIds.PeaShell)
        {
            return;
        }

        if (!Inventory.TryGetValue(weaponId, out var count) || count <= 0)
        {
            throw new InvalidOperationException($"Tank {Id} does not own weapon {weaponId}.");
        }

        Inventory[weaponId] = count - 1;
    }
}
