using ArmageddonRidge.Core.Models;
using ArmageddonRidge.Core.Terrain;

namespace ArmageddonRidge.Core.Game;

/// <summary>
/// Mutable aggregate for the current duel, including terrain, tanks, turn flow, and scoring counters.
/// </summary>
public sealed class GameState
{
    /// <summary>
    /// Unique identifier for this match instance.
    /// </summary>
    public string MatchId { get; init; } = Guid.NewGuid().ToString("N");

    /// <summary>
    /// Current round number within the run.
    /// </summary>
    public int RoundNumber { get; set; } = 1;

    /// <summary>
    /// Tank that currently owns the turn.
    /// </summary>
    public TurnOwner CurrentTurn { get; set; } = TurnOwner.Player;

    /// <summary>
    /// Current high-level UI and gameplay phase.
    /// </summary>
    public GamePhase Phase { get; set; } = GamePhase.MainMenu;

    /// <summary>
    /// Authoritative terrain height mask for collision and deformation.
    /// </summary>
    public required TerrainMask Terrain { get; init; }

    /// <summary>
    /// Current turn wind acceleration tuning value.
    /// </summary>
    public int Wind { get; set; }

    /// <summary>
    /// Human-controlled tank state.
    /// </summary>
    public required Tank PlayerTank { get; init; }

    /// <summary>
    /// CPU-controlled tank state.
    /// </summary>
    public required Tank CpuTank { get; init; }

    /// <summary>
    /// Active lingering damage zones.
    /// </summary>
    public List<RadiationZone> RadiationZones { get; } = [];

    /// <summary>
    /// Damageable civilian structures that players should avoid hitting.
    /// </summary>
    public List<CivilianStructure> CivilianStructures { get; } = [];

    /// <summary>
    /// Seed used for terrain and deterministic random decisions.
    /// </summary>
    public int RandomSeed { get; init; }

    /// <summary>
    /// Recent player-facing combat events.
    /// </summary>
    public List<string> EventLog { get; } = [];

    /// <summary>
    /// Current player-selected weapon identifier.
    /// </summary>
    public string SelectedWeaponId { get; set; } = WeaponIds.PeaShell;

    /// <summary>
    /// Deterministic random stream scoped to this match.
    /// </summary>
    public Random Random { get; init; } = new(0);

    /// <summary>
    /// Number of shots fired in the current round.
    /// </summary>
    public int ShotsFired { get; set; }

    /// <summary>
    /// Accumulated blast damage credited to the player this round.
    /// </summary>
    public float DamageDealtByPlayer { get; set; }

    /// <summary>
    /// Accumulated blast damage credited to the CPU this round.
    /// </summary>
    public float DamageDealtByCpu { get; set; }

    /// <summary>
    /// Cash penalties charged to the player for damaging civilian structures this round.
    /// </summary>
    public int CivilianPenaltyByPlayer { get; set; }

    /// <summary>
    /// Cash penalties charged to the CPU for damaging civilian structures this round.
    /// </summary>
    public int CivilianPenaltyByCpu { get; set; }

    /// <summary>
    /// Most recent simulation and renderer-facing performance counters.
    /// </summary>
    public PerformanceSample LastPerformance { get; set; } = new(0, 0, 0, 0, 0);
}
