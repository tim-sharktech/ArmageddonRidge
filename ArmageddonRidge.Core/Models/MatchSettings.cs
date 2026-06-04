namespace ArmageddonRidge.Core.Models;

/// <summary>
/// User-selectable setup values for creating a deterministic match.
/// </summary>
/// <param name="Difficulty">CPU skill profile used for planning and budgeting.</param>
/// <param name="StartingCash">Initial player cash for the run.</param>
/// <param name="TerrainSeed">Optional terrain seed; random when omitted.</param>
/// <param name="RoundLimit">Maximum campaign-style rounds before ending a run.</param>
/// <param name="EnableNuclearWeapons">Whether nuclear weapons can appear and be used.</param>
/// <param name="EnableShop">Whether the player starts rounds in the shop phase.</param>
public sealed record MatchSettings(
    Difficulty Difficulty = Difficulty.Normal,
    int StartingCash = GameConstants.StartingCash,
    int? TerrainSeed = null,
    int RoundLimit = 10,
    bool EnableNuclearWeapons = true,
    bool EnableShop = true);

/// <summary>
/// Persisted browser-local progression and preferences.
/// </summary>
/// <param name="BestScore">Highest recorded cash score.</param>
/// <param name="CampaignRound">Last reached campaign round.</param>
/// <param name="Losses">Number of campaign losses recorded for the run.</param>
/// <param name="UnlockedWeapons">Weapon identifiers unlocked for future runs.</param>
/// <param name="LastUsedWeapon">Weapon identifier selected most recently.</param>
/// <param name="Settings">Saved player settings.</param>
public sealed record SaveGame(
    int BestScore,
    int CampaignRound,
    int Losses,
    IReadOnlyCollection<string> UnlockedWeapons,
    string LastUsedWeapon,
    GameSettings Settings);

/// <summary>
/// Browser-local player preferences that affect presentation and starting conditions.
/// </summary>
/// <param name="MasterVolume">Overall audio volume multiplier.</param>
/// <param name="SfxVolume">Sound-effect volume multiplier.</param>
/// <param name="ScreenShake">Whether large explosions shake the camera.</param>
/// <param name="ReducedMotion">Whether motion-heavy effects should be shortened or softened.</param>
/// <param name="ShowTutorialHints">Whether contextual tutorial hints should be shown.</param>
/// <param name="EnableNuclearWeapons">Whether nuclear weapons are enabled for new matches.</param>
/// <param name="Difficulty">Default CPU difficulty for new duels.</param>
/// <param name="StartingCash">Default player cash for new runs.</param>
/// <param name="TargetingComputerEnabledByDefault">Whether the targeting computer is granted without shop purchase.</param>
/// <param name="RenderMode">Preferred battlefield renderer backend.</param>
/// <param name="WebGpuEffectsEnabled">Whether the optional WebGPU visual effects overlay should run when supported.</param>
public sealed record GameSettings(
    float MasterVolume = 0.8f,
    float SfxVolume = 0.9f,
    bool ScreenShake = true,
    bool ReducedMotion = false,
    bool ShowTutorialHints = true,
    bool EnableNuclearWeapons = true,
    Difficulty Difficulty = Difficulty.Normal,
    int StartingCash = GameConstants.StartingCash,
    bool TargetingComputerEnabledByDefault = true,
    RenderMode RenderMode = RenderMode.Hybrid,
    bool WebGpuEffectsEnabled = true)
{
    public GameSettings Normalize() => this with
    {
        MasterVolume = ClampVolume(MasterVolume, 0.8f),
        SfxVolume = ClampVolume(SfxVolume, 0.9f),
        Difficulty = Enum.IsDefined(Difficulty) ? Difficulty : Difficulty.Normal,
        StartingCash = Math.Clamp(StartingCash, 500, 10_000),
        RenderMode = Enum.IsDefined(RenderMode) ? RenderMode : RenderMode.Hybrid
    };

    private static float ClampVolume(float value, float fallback) =>
        float.IsFinite(value) ? Math.Clamp(value, 0f, 1f) : fallback;
}
