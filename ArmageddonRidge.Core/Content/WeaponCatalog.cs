using System.Collections.Frozen;
using ArmageddonRidge.Core.Models;

namespace ArmageddonRidge.Core.Content;

/// <summary>
/// Read-only catalog of built-in weapon definitions.
/// </summary>
public sealed class WeaponCatalog
{
    private readonly WeaponDefinition[] _all;
    private readonly IReadOnlyList<WeaponDefinition> _allView;
    private readonly FrozenDictionary<string, WeaponDefinition> _weapons;

    /// <summary>
    /// Creates the default MVP weapon catalog.
    /// </summary>
    public WeaponCatalog()
    {
        _all =
        [
            new WeaponDefinition(WeaponIds.PeaShell, "Pea Shell", WeaponCategory.BasicBallistic, 0, 25, 28, 34, 1f, 1f, 1f, WeaponBehaviorType.Ballistic, true, 0f, 1.5f, "Free basic shot."),
            new WeaponDefinition(WeaponIds.HeavyShell, "Heavy Shell", WeaponCategory.BasicBallistic, 75, 45, 36, 42, 0.95f, 1f, 0.85f, WeaponBehaviorType.Ballistic, true, 0f, 1.5f, "Reliable mid-weight damage."),
            new WeaponDefinition(WeaponIds.BabyMissile, "Baby Missile", WeaponCategory.AreaDamage, 125, 60, 42, 48, 1.05f, 1f, 1f, WeaponBehaviorType.Ballistic, true, 0.05f, 1.4f, "A stronger standard projectile."),
            new WeaponDefinition(WeaponIds.ClusterPopper, "Cluster Popper", WeaponCategory.Cluster, 250, 18, 18, 22, 1f, 1f, 1f, WeaponBehaviorType.Cluster, true, 0f, 1.5f, "Splits into five small pops.", 5),
            new WeaponDefinition(WeaponIds.SplitterMirv, "Splitter MIRV", WeaponCategory.Cluster, 500, 20, 20, 25, 1.02f, 1f, 0.9f, WeaponBehaviorType.Cluster, true, 0.05f, 1.45f, "Seven late-game fragments.", 7),
            new WeaponDefinition(WeaponIds.NapalmFlask, "Napalm Flask", WeaponCategory.Fire, 350, 15, 48, 40, 0.9f, 1f, 1.2f, WeaponBehaviorType.Napalm, true, 0f, 1.2f, "Impact plus a lingering hot zone.", RadiationTurns: 2, RadiationDamagePerTurn: 4),
            new WeaponDefinition(WeaponIds.DirtDrop, "Dirt Drop", WeaponCategory.Terrain, 200, 0, 60, 60, 0.85f, 1f, 1.1f, WeaponBehaviorType.Dirt, false, 0f, 1f, "Adds terrain to bury or block."),
            new WeaponDefinition(WeaponIds.Excavator, "Excavator", WeaponCategory.Terrain, 200, 10, 70, 70, 0.9f, 1f, 0.9f, WeaponBehaviorType.Excavator, true, 0f, 1.2f, "Removes a wide bite of terrain."),
            new WeaponDefinition(WeaponIds.BunkerBuster, "Bunker Buster", WeaponCategory.AreaDamage, 450, 75, 32, 82, 1.1f, 1f, 0.45f, WeaponBehaviorType.BunkerBuster, true, 0.1f, 2f, "Punches deeper before exploding."),
            new WeaponDefinition(WeaponIds.LaserLance, "Laser Lance", WeaponCategory.Precision, 650, 50, 20, 12, 1f, 0f, 0f, WeaponBehaviorType.Laser, false, 0.15f, 2.5f, "Straight beam. Wind is irrelevant."),
            new WeaponDefinition(WeaponIds.TeleportShot, "Teleport Shot", WeaponCategory.Utility, 300, 0, 0, 0, 1f, 1f, 0.7f, WeaponBehaviorType.Teleport, false, 0f, 1f, "Reposition before the next shot."),
            new WeaponDefinition(WeaponIds.DarkEagle, "Dark Eagle", WeaponCategory.Precision, 700, 95, 44, 54, 1.65f, 0.85f, 0.12f, WeaponBehaviorType.Missile, true, 0.18f, 1.35f, "Guided arcade hypersonic strike: locks onto the rival tank."),
            new WeaponDefinition(WeaponIds.ShahedDroneSwarm, "Shahed Drone Swarm", WeaponCategory.Cluster, 520, 18, 24, 22, 0.82f, 0.75f, 0.45f, WeaponBehaviorType.DroneSwarm, true, 0.03f, 1.4f, "Stylized triangular drone swarm: 3-5 wandering impacts.", 5),
            new WeaponDefinition(WeaponIds.Gbu57Mop, "GBU-57 MOP", WeaponCategory.AreaDamage, 950, 105, 62, 130, 1.04f, 1.18f, 0.08f, WeaponBehaviorType.MultiStagePenetrator, true, 0.12f, 1.35f, "Heavy long-range two-stage bunker buster: surface pop, then a deeper larger blast."),
            new WeaponDefinition(WeaponIds.TacticalNuke, "Tactical Nuke", WeaponCategory.Nuclear, 850, 110, 110, 130, 0.85f, 1f, 0.75f, WeaponBehaviorType.Nuclear, true, 0.25f, 1.2f, "Huge crater, shockwave, and temporary radiation.", RadiationTurns: 2, RadiationDamagePerTurn: 5),
            new WeaponDefinition(WeaponIds.DoomsdayNuke, "Doomsday Nuke", WeaponCategory.Nuclear, 1600, 180, 190, 240, 0.75f, 1f, 0.5f, WeaponBehaviorType.Nuclear, true, 0.4f, 1.1f, "Rare endgame battlefield reset.", RadiationTurns: 3, RadiationDamagePerTurn: 8)
        ];

        _weapons = _all.ToFrozenDictionary(static weapon => weapon.Id, StringComparer.OrdinalIgnoreCase);
        _allView = Array.AsReadOnly(_all);
    }

    /// <summary>
    /// Gets all available weapon definitions.
    /// </summary>
    public IReadOnlyList<WeaponDefinition> All => _allView;

    /// <summary>
    /// Gets a weapon definition by stable identifier.
    /// </summary>
    public WeaponDefinition Get(string id) => _weapons.TryGetValue(id, out var weapon)
        ? weapon
        : throw new KeyNotFoundException($"Unknown weapon '{id}'.");
}
