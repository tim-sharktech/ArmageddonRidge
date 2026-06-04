using System.Numerics;

namespace ArmageddonRidge.Core.Models;

/// <summary>
/// Mutable combat state for one tank during a duel.
/// </summary>
public sealed class Tank
{
    private int _health = GameConstants.StartingHealth;
    private int _maxHealth = GameConstants.StartingHealth;

    /// <summary>
    /// Stable identifier used by game rules and renderer snapshots.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// Player-facing display name for HUD, event log, and CPU taunts.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>
    /// World-space ground contact point at the horizontal center of the tank.
    /// </summary>
    public Vector2 Position { get; set; }

    /// <summary>
    /// Current turret elevation in degrees.
    /// </summary>
    public float TurretAngle { get; set; }

    /// <summary>
    /// Remaining hull health.
    /// </summary>
    public int Health
    {
        get => _health;
        set => _health = Math.Clamp(value, 0, _maxHealth);
    }

    /// <summary>
    /// Maximum hull health after upgrades.
    /// </summary>
    public int MaxHealth
    {
        get => _maxHealth;
        set
        {
            _maxHealth = Math.Max(0, value);
            if (_health > _maxHealth) _health = _maxHealth;
        }
    }

    /// <summary>
    /// Current shield pool that absorbs blockable blast damage.
    /// </summary>
    public float Shield { get; set; }

    /// <summary>
    /// Cash available for shop purchases.
    /// </summary>
    public int Cash { get; set; } = GameConstants.StartingCash;

    /// <summary>
    /// Indicates whether this tank is controlled by the CPU planner.
    /// </summary>
    public bool IsCpu { get; init; }

    /// <summary>
    /// Whether the next dangerous fall should be prevented.
    /// </summary>
    public bool HasParachute { get; set; }

    /// <summary>
    /// Number of single-use Patriot interceptors available.
    /// </summary>
    public int PatriotBatteryCharges { get; set; }

    /// <summary>
    /// Number of previous player shot trails to keep visible.
    /// </summary>
    public int TracerRoundCharges { get; set; }

    /// <summary>
    /// Non-free weapon counts keyed by weapon identifier.
    /// </summary>
    public Dictionary<string, int> Inventory { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Purchased defensive and utility upgrades.
    /// </summary>
    public HashSet<UpgradeType> Upgrades { get; } = [];

    /// <summary>
    /// Approximate world-space center used for hit and blast calculations.
    /// </summary>
    public Vector2 Center => new(Position.X, Position.Y - (GameConstants.TankCollisionHeight / 2f));

    /// <summary>
    /// Gets whether health has reached the death threshold.
    /// </summary>
    public bool IsDestroyed => Health <= 0;

    /// <summary>
    /// Returns the owned count for a non-free weapon.
    /// </summary>
    public int GetInventoryCount(string weaponId)
    {
        if (!Inventory.TryGetValue(weaponId, out var count)) return 0;

        return count;
    }

    /// <summary>
    /// Gets whether the tank can fire the requested weapon.
    /// </summary>
    public bool HasWeapon(string weaponId) => weaponId == WeaponIds.PeaShell || GetInventoryCount(weaponId) > 0;

    /// <summary>
    /// Adds one or more weapons to the tank inventory.
    /// </summary>
    public void AddWeapon(string weaponId, int count)
    {
        if (count <= 0)
        {
            if (weaponId == WeaponIds.PeaShell) return;

            throw new ArgumentOutOfRangeException(nameof(count), "Weapon count must be positive.");
        }

        if (Inventory.TryGetValue(weaponId, out var currentCount))
        {
            var nextCount = (long)currentCount + count;
            if (nextCount > int.MaxValue)
                throw new InvalidOperationException($"Tank {Id} inventory for {weaponId} would exceed the maximum supported count.");

            Inventory[weaponId] = (int)nextCount;
            return;
        }

        Inventory[weaponId] = count;
    }

    /// <summary>
    /// Consumes one weapon from inventory, leaving the free Pea Shell unlimited.
    /// </summary>
    public void ConsumeWeapon(string weaponId)
    {
        if (weaponId == WeaponIds.PeaShell) return;

        if (!Inventory.TryGetValue(weaponId, out var count) || count <= 0)
            throw new InvalidOperationException($"Tank {Id} does not own weapon {weaponId}.");

        Inventory[weaponId] = count - 1;
    }
}
