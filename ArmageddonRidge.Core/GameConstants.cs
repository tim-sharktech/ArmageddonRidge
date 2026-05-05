namespace ArmageddonRidge.Core;

/// <summary>
/// Shared tuning values for the deterministic world, physics, economy, and tank model.
/// </summary>
public static class GameConstants
{
    /// <summary>
    /// Logical world width in simulation units.
    /// </summary>
    public const int WorldWidth = 1200;

    /// <summary>
    /// Logical world height in simulation units.
    /// </summary>
    public const int WorldHeight = 700;

    /// <summary>
    /// Highest generated terrain surface Y coordinate.
    /// </summary>
    public const int GroundMinY = 300;

    /// <summary>
    /// Lowest generated terrain surface Y coordinate.
    /// </summary>
    public const int GroundMaxY = 650;

    /// <summary>
    /// Baseline tank sprite width from the original PRD scale.
    /// </summary>
    public const int TankWidth = 32;

    /// <summary>
    /// Baseline tank sprite height from the original PRD scale.
    /// </summary>
    public const int TankHeight = 18;

    /// <summary>
    /// Current tank collision width tuned for the sprite art.
    /// </summary>
    public const int TankCollisionWidth = 74;

    /// <summary>
    /// Current tank collision height tuned for the sprite art.
    /// </summary>
    public const int TankCollisionHeight = 46;

    /// <summary>
    /// Radius added around swept projectile checks for reliable hits.
    /// </summary>
    public const float ProjectileCollisionRadius = 5f;

    /// <summary>
    /// Horizontal radius for the visible shield bubble collision envelope.
    /// </summary>
    public const float ShieldCollisionRadiusX = 76f;

    /// <summary>
    /// Vertical radius for the visible shield bubble collision envelope.
    /// </summary>
    public const float ShieldCollisionRadiusY = 58f;

    /// <summary>
    /// Vertical offset from tank foot position to the center of the shield bubble.
    /// </summary>
    public const float ShieldCollisionCenterYOffset = 62f;

    /// <summary>
    /// Fraction of otherwise blockable shield damage that still bruises hull health.
    /// </summary>
    public const float ShieldHealthBleedThroughPercent = 0.18f;

    /// <summary>
    /// Fixed simulation timestep in seconds.
    /// </summary>
    public const float FixedDeltaTime = 1f / 60f;

    /// <summary>
    /// Base arcade gravity acceleration in world units per second squared.
    /// </summary>
    public const float Gravity = 120f;

    /// <summary>
    /// Minimum turn wind value.
    /// </summary>
    public const int WindMin = -24;

    /// <summary>
    /// Maximum turn wind value.
    /// </summary>
    public const int WindMax = 24;

    /// <summary>
    /// Minimum player shot power.
    /// </summary>
    public const int PowerMin = 1;

    /// <summary>
    /// Maximum player shot power.
    /// </summary>
    public const int PowerMax = 100;

    /// <summary>
    /// Default tank health for quick prototype rounds.
    /// </summary>
    public const int StartingHealth = 75;

    /// <summary>
    /// Maximum upgraded armor health.
    /// </summary>
    public const int ArmorUpgradeMax = 150;

    /// <summary>
    /// Default player starting cash.
    /// </summary>
    public const int StartingCash = 5000;

    /// <summary>
    /// Base reward for winning a round.
    /// </summary>
    public const int WinReward = 750;

    /// <summary>
    /// Cash awarded after losing a round.
    /// </summary>
    public const int LossConsolation = 300;

    /// <summary>
    /// Extra reward for destroying the opponent.
    /// </summary>
    public const int KillBonus = 500;
}
