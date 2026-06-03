namespace ArmageddonRidge.Core.Models;

/// <summary>
/// High-level flow states for a duel or campaign round.
/// </summary>
public enum GamePhase
{
    /// <summary>
    /// Main menu and match setup screen.
    /// </summary>
    MainMenu,

    /// <summary>
    /// Between-round purchase phase.
    /// </summary>
    Shop,

    /// <summary>
    /// Active turn-based combat phase.
    /// </summary>
    Battle,

    /// <summary>
    /// Projectile or effect animation is currently playing.
    /// </summary>
    Animating,

    /// <summary>
    /// Round result has been resolved.
    /// </summary>
    RoundOver
}

/// <summary>
/// Identifies the tank that currently owns the turn.
/// </summary>
public enum TurnOwner
{
    /// <summary>
    /// Human-controlled player turn.
    /// </summary>
    Player,

    /// <summary>
    /// CPU-controlled opponent turn.
    /// </summary>
    Cpu
}

/// <summary>
/// CPU skill presets that tune planning accuracy, risk tolerance, and inventory.
/// </summary>
public enum Difficulty
{
    /// <summary>
    /// Forgiving CPU with large planning error.
    /// </summary>
    Rookie,

    /// <summary>
    /// Balanced default CPU behavior.
    /// </summary>
    Normal,

    /// <summary>
    /// Stronger CPU with better planning and defenses.
    /// </summary>
    Veteran,

    /// <summary>
    /// Aggressive CPU with higher weapon risk tolerance.
    /// </summary>
    Maniac,

    /// <summary>
    /// Challenge CPU with near-perfect candidate search.
    /// </summary>
    Oracle
}

/// <summary>
/// Broad gameplay role for a weapon definition.
/// </summary>
public enum WeaponCategory
{
    /// <summary>
    /// Standard arcing projectile.
    /// </summary>
    BasicBallistic,

    /// <summary>
    /// Weapon focused on blast radius and terrain damage.
    /// </summary>
    AreaDamage,

    /// <summary>
    /// Weapon that produces multiple impacts.
    /// </summary>
    Cluster,

    /// <summary>
    /// Weapon primarily meant to add or remove terrain.
    /// </summary>
    Terrain,

    /// <summary>
    /// Weapon with lingering heat or lava effects.
    /// </summary>
    Fire,

    /// <summary>
    /// Weapon focused on accuracy or direct paths.
    /// </summary>
    Precision,

    /// <summary>
    /// Nuclear-class weapon with very large blast effects.
    /// </summary>
    Nuclear,

    /// <summary>
    /// Non-damaging or movement-focused weapon.
    /// </summary>
    Utility
}

/// <summary>
/// Engine behavior used to resolve a weapon after it is fired.
/// </summary>
public enum WeaponBehaviorType
{
    /// <summary>
    /// Single fixed-step projectile and one impact.
    /// </summary>
    Ballistic,

    /// <summary>
    /// Impact splits into multiple smaller blasts.
    /// </summary>
    Cluster,

    /// <summary>
    /// Impact adds terrain instead of removing it.
    /// </summary>
    Dirt,

    /// <summary>
    /// Impact removes a larger terrain area.
    /// </summary>
    Excavator,

    /// <summary>
    /// Projectile penetrates before resolving its blast.
    /// </summary>
    BunkerBuster,

    /// <summary>
    /// Direct line shot that ignores wind.
    /// </summary>
    Laser,

    /// <summary>
    /// Repositions the firing tank.
    /// </summary>
    Teleport,

    /// <summary>
    /// Nuclear explosion and lingering hazard behavior.
    /// </summary>
    Nuclear,

    /// <summary>
    /// Impact plus lingering heat behavior.
    /// </summary>
    Napalm,

    /// <summary>
    /// Guided missile-style behavior.
    /// </summary>
    Missile,

    /// <summary>
    /// Multiple wandering drone-style projectiles.
    /// </summary>
    DroneSwarm,

    /// <summary>
    /// Two-stage heavy penetrator behavior.
    /// </summary>
    MultiStagePenetrator
}

/// <summary>
/// Purchasable defensive and utility upgrades.
/// </summary>
public enum UpgradeType
{
    /// <summary>
    /// Small shield pool.
    /// </summary>
    LightShield,

    /// <summary>
    /// Larger shield pool.
    /// </summary>
    HeavyShield,

    /// <summary>
    /// Chance-based weak projectile reflection upgrade.
    /// </summary>
    ReflectorShield,

    /// <summary>
    /// One-time fall damage prevention.
    /// </summary>
    Parachute,

    /// <summary>
    /// Between-round hull repair.
    /// </summary>
    RepairKit,

    /// <summary>
    /// Shield recharge purchase.
    /// </summary>
    Battery,

    /// <summary>
    /// One-time reposition utility.
    /// </summary>
    Teleporter,

    /// <summary>
    /// Precise wind readout.
    /// </summary>
    WindMeter,

    /// <summary>
    /// Last-shot trail helper.
    /// </summary>
    TracerRounds,

    /// <summary>
    /// Approximate future-shot preview helper.
    /// </summary>
    TargetingComputer,

    /// <summary>
    /// Single-use defensive missile intercept.
    /// </summary>
    PatriotBattery
}

/// <summary>
/// Visual style hints passed to the canvas renderer for projectile and explosion effects.
/// </summary>
public enum ShotVisualKind
{
    /// <summary>
    /// Default shell projectile and explosion.
    /// </summary>
    Ballistic,

    /// <summary>
    /// Fire impact effect.
    /// </summary>
    Fire,

    /// <summary>
    /// Lingering lava hazard effect.
    /// </summary>
    Lava,

    /// <summary>
    /// Missile projectile and plume effect.
    /// </summary>
    Missile,

    /// <summary>
    /// Nuclear blast effect.
    /// </summary>
    Nuclear,

    /// <summary>
    /// Drone swarm projectile effect.
    /// </summary>
    DroneSwarm,

    /// <summary>
    /// Laser beam effect.
    /// </summary>
    Laser,

    /// <summary>
    /// Teleport shimmer effect.
    /// </summary>
    Teleport,

    /// <summary>
    /// Dirt or excavation effect.
    /// </summary>
    Dirt,

    /// <summary>
    /// Patriot defensive intercept effect.
    /// </summary>
    PatriotIntercept,

    /// <summary>
    /// Shield absorption impact effect.
    /// </summary>
    ShieldHit,

    /// <summary>
    /// First-stage penetrator impact effect.
    /// </summary>
    PenetratorPrimary,

    /// <summary>
    /// Second-stage penetrator impact effect.
    /// </summary>
    PenetratorSecondary
}

/// <summary>
/// Selects the battlefield rendering backend.
/// </summary>
public enum RenderMode
{
    /// <summary>
    /// Existing JavaScript canvas renderer fed by Blazor WebAssembly game state.
    /// </summary>
    Hybrid,

    /// <summary>
    /// C# render orchestration and command generation with a minimal JavaScript canvas bridge.
    /// </summary>
    FullWasm
}
