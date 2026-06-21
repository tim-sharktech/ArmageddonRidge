using System.Numerics;

namespace ArmageddonRidge.Core.Models;

/// <summary>
/// Damageable civilian structure used as a visual physics and scoring-risk target.
/// </summary>
public sealed class CivilianStructure
{
    /// <summary>
    /// Stable structure identifier for renderer payloads and hit events.
    /// </summary>
    public required string Id { get; init; }

    /// <summary>
    /// World-space center of the building footprint at terrain contact.
    /// </summary>
    public Vector2 Position { get; set; }

    /// <summary>
    /// Renderer-facing building archetype.
    /// </summary>
    public string Kind { get; init; } = "apartment";

    /// <summary>
    /// Footprint width in world pixels.
    /// </summary>
    public float Width { get; init; }

    /// <summary>
    /// Standing height in world pixels.
    /// </summary>
    public float Height { get; init; }

    /// <summary>
    /// Maximum structural health.
    /// </summary>
    public float MaxHealth { get; init; } = 100;

    /// <summary>
    /// Current structural health.
    /// </summary>
    public float Health { get; set; } = 100;

    /// <summary>
    /// Last shot index that damaged this structure, used by renderers for fresh collapse effects.
    /// </summary>
    public int LastDamagedShot { get; set; } = -1;

    /// <summary>
    /// Cash penalty applied when this building is first damaged.
    /// </summary>
    public int PenaltyValue { get; init; } = 150;

    /// <summary>
    /// Visual lean caused by uneven blast damage or terrain support loss.
    /// </summary>
    public float TiltDegrees { get; set; }

    /// <summary>
    /// Fraction of the footprint still supported by terrain near the original base.
    /// </summary>
    public float SupportFraction { get; set; } = 1;

    /// <summary>
    /// Whether this structure has fully collapsed.
    /// </summary>
    public bool IsCollapsed => Health <= 0;

    /// <summary>
    /// Damage fraction from 0 for intact to 1 for collapsed.
    /// </summary>
    public float DamageFraction => MaxHealth <= 0 ? 1 : Math.Clamp(1f - (Health / MaxHealth), 0f, 1f);

    public CivilianStructure Clone() => new()
    {
        Id = Id,
        Position = Position,
        Kind = Kind,
        Width = Width,
        Height = Height,
        MaxHealth = MaxHealth,
        Health = Health,
        LastDamagedShot = LastDamagedShot,
        PenaltyValue = PenaltyValue,
        TiltDegrees = TiltDegrees,
        SupportFraction = SupportFraction
    };
}

public sealed record CivilianImpactResult(
    string StructureId,
    Vector2 Position,
    float Damage,
    float HealthRemaining,
    int Penalty,
    bool Collapsed,
    string Kind);
