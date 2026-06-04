using System.Numerics;
using ArmageddonRidge.Client.Services;
using ArmageddonRidge.Client.Services.Rendering;
using ArmageddonRidge.Core;
using ArmageddonRidge.Core.Content;
using ArmageddonRidge.Core.Game;
using ArmageddonRidge.Core.Models;
using ArmageddonRidge.Core.Terrain;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using RenderMode = ArmageddonRidge.Core.Models.RenderMode;

namespace ArmageddonRidge.Client.Pages;

public partial class Home
{
    private static readonly Difficulty[] DifficultyOptions = Enum.GetValues<Difficulty>();
    private static readonly RenderMode[] RenderModeOptions = Enum.GetValues<RenderMode>();

    private ElementReference _canvas;
    private ElementReference _effectsCanvas;
    private GameState? _state;
    private MatchSettings _settings = new();
    private Difficulty _difficulty = Difficulty.Normal;
    private int _power = 65;
    private bool _canFire = true;
    private bool _showPerf;
    private bool _settingsOpen;
    private bool _screenShake = true;
    private bool _reducedMotion;
    private bool _targetingComputerEnabledByDefault = true;
    private bool _enableNuclearWeapons = true;
    private bool _webGpuEffectsEnabled = true;
    private RenderMode _renderMode = RenderMode.Hybrid;
    private float _sfxVolume = 0.9f;
    private int _startingCash = GameConstants.StartingCash;
    private int _fps = 60;
    private double _frameMs = 16.7;
    private double _renderMs;
    private double _sceneBuildMs;
    private double _commandBuildMs;
    private double _submitMs;
    private int _commandCount;
    private int _payloadBytes;
    private bool _simdHardwareAccelerated;
    private bool _effectsSupported;
    private bool _effectsActive;
    private double _effectFrameMs;
    private double _effectPostProcessMs;
    private double _effectSourceCopyMs;
    private int _effectParticleCount;
    private int _effectActiveParticleCount;
    private int _effectParticleCapacity;
    private int _effectRadialEffectCount;
    private int _effectSpawnCount;
    private double _effectOverlayScale = 1;
    private double _effectCanvasPixelRatio = 1;
    private int _effectSourceCopyCadence;
    private double _effectGpuQueueMs;
    private string _effectPerfMode = "adaptive";
    private string _effectQualityTier = "n/a";
    private string _effectFallbackReason = "Not initialized";
    private string _rendererModeLabel = "Hybrid";
    private int _bestScore;
    private bool _rendererReady;
    private bool _effectsReady;
    private bool _shotPlaybackInProgress;
    private int? _displayPlayerHealth;
    private int? _displayCpuHealth;
    private float? _displayPlayerShield;
    private float? _displayCpuShield;
    private int _terrainRevision;
    private int _lastSentTerrainRevision = -1;
    private CancellationTokenSource? _perfLoop;
    private Task? _perfLoopTask;
    private CancellationTokenSource? _fpsButtonLoop;
    private Task? _fpsButtonLoopTask;
    private bool _playerHurt;
    private bool _cpuHurt;
    private bool _playerShieldHit;
    private bool _cpuShieldHit;
    private int _damagePulse;
    private WeaponDefinition[] _allWeapons = [];
    private UpgradeDefinition[] _allUpgrades = [];
    private string[] _unlockedWeaponIds = [];
    private WeaponDefinition[] _availablePlayerWeapons = [];
    private readonly List<RenderPoint[]> _tracerTrails = [];

    private IReadOnlyCollection<WeaponDefinition> AllWeapons => _allWeapons;

    private IReadOnlyCollection<UpgradeDefinition> AllUpgrades => _allUpgrades;

    private IReadOnlyList<WeaponDefinition> AvailablePlayerWeapons => _availablePlayerWeapons;

    private bool AnyTankHurt => _playerHurt || _cpuHurt;

    private bool AnyTankShieldHit => _playerShieldHit || _cpuShieldHit;

    private bool AnyTankFeedback => AnyTankHurt || AnyTankShieldHit;

    private bool CanFirePlayer => _canFire && _state?.Phase == GamePhase.Battle && _state.CurrentTurn == TurnOwner.Player;

    private GamePhase VisiblePhase => _state is null
        ? GamePhase.MainMenu
        : _shotPlaybackInProgress ? GamePhase.Battle : _state.Phase;

    private string BattlefieldPanelCss =>
        $"battlefield-panel{(_playerHurt ? " player-hurt" : "")}{(_cpuHurt ? " cpu-hurt" : "")}{(_playerShieldHit ? " player-shield-hit" : "")}{(_cpuShieldHit ? " cpu-shield-hit" : "")}";

    private string BattleHudCss =>
        $"battle-hud{(AnyTankHurt ? " hurt-flash" : "")}{(AnyTankShieldHit ? " shield-flash" : "")}";

    private string PlayerVersusHealthCss =>
        $"versus-health player{(_playerHurt ? " is-hurt" : "")}{(_playerShieldHit ? " is-shield-hit" : "")}";

    private string CpuVersusHealthCss =>
        $"versus-health cpu{(_cpuHurt ? " is-hurt" : "")}{(_cpuShieldHit ? " is-shield-hit" : "")}";

    private string BattleLayoutCss => VisiblePhase == GamePhase.Battle
        ? "is-battle"
        : string.Empty;

    private float AngleScrubValue => 90f - Math.Clamp(_state?.PlayerTank.TurretAngle ?? 45f, 5f, 85f);

    private string WindText => _state is null
        ? "0"
        : $"{(_state.Wind > 0 ? "->" : _state.Wind < 0 ? "<-" : "--")} {Math.Abs(_state.Wind)}";

    private string FpsButtonText => $"{Math.Max(0, _fps)} FPS";

    private string RoundResult => _state?.PlayerTank.IsDestroyed == true ? "CPU wins the ridge" : "Player wins the ridge";

    private string LatestCombatEvent => _state?.EventLog.LastOrDefault() ?? string.Empty;

    private string SimdStatusLabel => _renderMode == RenderMode.Hybrid
        ? "N/A"
        : _simdHardwareAccelerated ? "Enabled" : "Disabled";

    private string EffectsStatusLabel =>
        !_webGpuEffectsEnabled
            ? "Off"
            : _effectsActive
                ? "WebGPU"
                : _effectsSupported
                    ? "Ready"
                    : string.IsNullOrWhiteSpace(_effectFallbackReason) ? "Unavailable" : _effectFallbackReason;

    private static string RenderModeLabel(RenderMode mode) => mode switch
    {
        RenderMode.FullWasm => "Full WASM",
        _ => "Hybrid (JS + WASM)"
    };

    private string HintText => HasTargetingComputer
        ? "Targeting computer estimates the launch arc. Wind still matters."
        : HasTracerRounds
            ? "Tracer rounds help you read the last shot trail."
            : "Watch the last trail, then correct angle and power.";

    protected override void OnInitialized()
    {
        _allWeapons = Weapons.All.ToArray();
        _allUpgrades = Upgrades.All.ToArray();
        _unlockedWeaponIds = BuildUnlockedWeaponIds(_allWeapons);
    }

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            await Audio.InitializeAsync();
            var save = await Storage.GetAsync<SaveGame>("armageddon-ridge-save");
            if (save is not null)
            {
                _bestScore = save.BestScore;
                _difficulty = save.Settings.Difficulty;
                _sfxVolume = save.Settings.SfxVolume;
                _screenShake = save.Settings.ScreenShake;
                _reducedMotion = save.Settings.ReducedMotion;
                _targetingComputerEnabledByDefault = save.Settings.TargetingComputerEnabledByDefault;
                _enableNuclearWeapons = save.Settings.EnableNuclearWeapons;
                _webGpuEffectsEnabled = save.Settings.WebGpuEffectsEnabled;
                _renderMode = save.Settings.RenderMode;
                _startingCash = Math.Clamp(save.Settings.StartingCash, 500, 10_000);
                await Renderer.SetModeAsync(_renderMode);
                await Audio.SetVolumeAsync(_sfxVolume);
                StateHasChanged();
            }
        }

        if (_state is not null && !_shotPlaybackInProgress)
        {
            await EnsureRendererAsync();
            await RenderSceneAsync();
        }
    }

    private async Task NewDuelAsync()
    {
        _settings = new MatchSettings(
            Difficulty: _difficulty,
            StartingCash: _startingCash,
            TerrainSeed: Random.Shared.Next(10_000, 99_999),
            EnableNuclearWeapons: _enableNuclearWeapons);
        _state = Engine.NewMatch(_settings);
        ClearDamageFeedback();
        _tracerTrails.Clear();
        RefreshPlayerWeapons();
        ResetRenderCache();
        await Audio.UnlockAsync();
        await Audio.PlayAsync("menu");
    }

    private async Task StartBattleAsync()
    {
        if (_state is null) return;

        Engine.StartBattle(_state);
        await Audio.PlayAsync("menu");
        await RenderSceneAsync();
    }

    private async Task NextRoundAsync()
    {
        if (_state is null) return;

        Engine.StartNextRound(_state, _settings);
        ClearDamageFeedback();
        _tracerTrails.Clear();
        RefreshPlayerWeapons();
        MarkTerrainChanged();
        await RenderSceneAsync();
    }

    private async Task FirePlayerAsync()
    {
        if (!_canFire || _state is null || _state.CurrentTurn != TurnOwner.Player || _state.Phase != GamePhase.Battle) return;

        _canFire = false;
        try
        {
            await Audio.UnlockAsync();
            var playerHealthBefore = _state.PlayerTank.Health;
            var cpuHealthBefore = _state.CpuTank.Health;
            var playerShieldBefore = _state.PlayerTank.Shield;
            var cpuShieldBefore = _state.CpuTank.Shield;
            var playerPreShotScene = BuildScene();
            var playerShot = Engine.FireCurrentTurn(_state, _settings, _state.PlayerTank.TurretAngle, _power);
            HoldDisplayedDamage(playerHealthBefore, cpuHealthBefore, playerShieldBefore, cpuShieldBefore);
            RecordTracerTrail(playerShot.Trail);
            await PlayResolutionAsync(
                playerPreShotScene,
                playerShot,
                ShieldChanged(playerShieldBefore, _state.PlayerTank.Shield, cpuShieldBefore, _state.CpuTank.Shield),
                HealthChanged(playerHealthBefore, _state.PlayerTank, cpuHealthBefore, _state.CpuTank),
                playerHealthBefore,
                cpuHealthBefore,
                playerShieldBefore,
                cpuShieldBefore);
            RefreshPlayerWeapons();

            if (_state.Phase == GamePhase.Battle && _state.CurrentTurn == TurnOwner.Cpu)
            {
                await Task.Delay(_reducedMotion ? 250 : 700);
                await RenderSceneCoreAsync(force: true);
                await Task.Delay(1);
                var cpuPlan = await Engine.PlanCurrentCpuTurnAsync(_state, _settings);
                playerHealthBefore = _state.PlayerTank.Health;
                cpuHealthBefore = _state.CpuTank.Health;
                playerShieldBefore = _state.PlayerTank.Shield;
                cpuShieldBefore = _state.CpuTank.Shield;
                var cpuPreShotScene = BuildScene();
                var cpuShot = Engine.FirePlannedCpuTurn(_state, _settings, cpuPlan);
                HoldDisplayedDamage(playerHealthBefore, cpuHealthBefore, playerShieldBefore, cpuShieldBefore);
                await PlayResolutionAsync(
                    cpuPreShotScene,
                    cpuShot,
                    ShieldChanged(playerShieldBefore, _state.PlayerTank.Shield, cpuShieldBefore, _state.CpuTank.Shield),
                    HealthChanged(playerHealthBefore, _state.PlayerTank, cpuHealthBefore, _state.CpuTank),
                    playerHealthBefore,
                    cpuHealthBefore,
                    playerShieldBefore,
                    cpuShieldBefore);
            }

            if (_state.Phase == GamePhase.RoundOver) await PersistSaveAsync();
        }
        finally
        {
            RefreshPlayerWeapons();
            _canFire = true;
            StateHasChanged();
        }
    }

    private async Task PlayResolutionAsync(
        RenderScene preShotScene,
        ShotResolution resolution,
        bool shieldHit,
        bool healthHit,
        int playerHealthBefore,
        int cpuHealthBefore,
        float playerShieldBefore,
        float cpuShieldBefore)
    {
        _shotPlaybackInProgress = true;
        Task? impactAudioTask = null;
        Task? impactFeedbackTask = null;
        try
        {
            await EnsureRendererAsync();
            var hasNuclear = HasNuclearExplosion(resolution.Explosions);
            await Audio.PlayAsync(hasNuclear ? "nuclear" : "fire");
            impactAudioTask = PlayImpactAudioDuringPlaybackAsync(resolution, shieldHit, healthHit, hasNuclear);
            impactFeedbackTask = PlayImpactFeedbackDuringPlaybackAsync(resolution, playerHealthBefore, cpuHealthBefore, playerShieldBefore, cpuShieldBefore);
            var effectsStats = await AwaitWithTimeoutAsync(
                Effects.SpawnShotEffectsAsync(
                    resolution,
                    preShotScene.Wind,
                    _terrainRevision,
                    shieldHit,
                    healthHit,
                    _reducedMotion,
                    "flight",
                    _renderMode == RenderMode.Hybrid).AsTask(),
                900,
                "WebGPU shot effects");
            ApplyEffectsStats(effectsStats);
            await AwaitWithTimeoutAsync(
                Renderer.PlayShotAsync(preShotScene, resolution, _screenShake && !_reducedMotion).AsTask(),
                EstimateShotVisualDurationMs(resolution) + 2600,
                "renderer shot playback");
            await impactAudioTask;
            await impactFeedbackTask;
            if (resolution.RoundEnded && hasNuclear)
                await HoldRoundResultForNukeEffectsAsync(resolution);
        }
        catch (JSException ex)
        {
            if (impactAudioTask is not null)
                await AwaitQuietlyAsync(impactAudioTask);
            if (impactFeedbackTask is not null)
                await AwaitQuietlyAsync(impactFeedbackTask);

            _state?.EventLog.Add($"Recovered from renderer playback error: {ex.Message}");
        }
        finally
        {
            _shotPlaybackInProgress = false;
            ReleaseDisplayedDamage();
        }

        await InvokeAsync(StateHasChanged);

        if (resolution.Performance.TerrainColumnsTouched > 0) MarkTerrainChanged();
        if (!resolution.RoundEnded && resolution.Performance.TerrainColumnsTouched > 0)
        {
            ApplyEffectsStats(await AwaitWithTimeoutAsync(
                Effects.SpawnTerrainEffectsAsync(resolution, preShotScene.Wind, _terrainRevision, _reducedMotion).AsTask(),
                900,
                "WebGPU terrain effects"));
        }

        await RenderSceneCoreAsync(force: true);
    }

    private async Task<T?> AwaitWithTimeoutAsync<T>(Task<T?> task, int timeoutMs, string operationName)
    {
        var timeout = Task.Delay(Math.Max(250, timeoutMs));
        var completed = await Task.WhenAny(task, timeout);
        if (completed == task)
        {
            return await task;
        }

        _state?.EventLog.Add($"Timed out waiting for {operationName}; continuing UI flow.");
        _ = task.ContinueWith(static pending =>
        {
            _ = pending.Exception;
        }, TaskContinuationOptions.OnlyOnFaulted);
        return default;
    }

    private async Task AwaitWithTimeoutAsync(Task task, int timeoutMs, string operationName)
    {
        var timeout = Task.Delay(Math.Max(250, timeoutMs));
        var completed = await Task.WhenAny(task, timeout);
        if (completed == task)
        {
            await task;
            return;
        }

        _state?.EventLog.Add($"Timed out waiting for {operationName}; continuing UI flow.");
        _ = task.ContinueWith(static pending =>
        {
            _ = pending.Exception;
        }, TaskContinuationOptions.OnlyOnFaulted);
    }

    private static async Task AwaitQuietlyAsync(Task task)
    {
        try
        {
            await task;
        }
        catch
        {
        }
    }

    private async Task PlayImpactAudioDuringPlaybackAsync(ShotResolution resolution, bool shieldHit, bool healthHit, bool hasNuclear)
    {
        var delay = EstimateImpactAudioDelayMs(resolution);
        if (delay > 0) await Task.Delay(delay);

        if (shieldHit) await Audio.PlayAsync("shieldHit");
        if (!shieldHit || healthHit)
            await Audio.PlayAsync(hasNuclear || HasLargeExplosion(resolution.Explosions) ? "largeExplosion" : "smallExplosion");
    }

    private async Task PlayImpactFeedbackDuringPlaybackAsync(
        ShotResolution resolution,
        int playerHealthBefore,
        int cpuHealthBefore,
        float playerShieldBefore,
        float cpuShieldBefore)
    {
        if (_state is null) return;

        var willPulse =
            TankHealth(_state.PlayerTank) < Math.Max(0, playerHealthBefore)
            || TankHealth(_state.CpuTank) < Math.Max(0, cpuHealthBefore)
            || _state.PlayerTank.Shield < playerShieldBefore
            || _state.CpuTank.Shield < cpuShieldBefore;
        if (!willPulse) return;

        var delay = EstimateImpactFeedbackDelayMs(resolution);
        if (delay > 0) await Task.Delay(delay);

        await ApplyDamageFeedbackAsync(playerHealthBefore, cpuHealthBefore, playerShieldBefore, cpuShieldBefore);
    }

    private int EstimateImpactAudioDelayMs(ShotResolution resolution)
    {
        if (_reducedMotion) return 120;

        var visualDuration = EstimateShotVisualDurationMs(resolution);
        return Math.Clamp((int)MathF.Round(visualDuration * 0.86f), 120, 3000);
    }

    private static int EstimateImpactFeedbackDelayMs(ShotResolution resolution)
    {
        var visualDuration = EstimateShotVisualDurationMs(resolution);
        return Math.Clamp(visualDuration - 45, 80, 3400);
    }

    private static int EstimateShotVisualDurationMs(ShotResolution resolution)
    {
        var trailCount = Math.Max(2, resolution.Trail.Count);
        var visualDuration = resolution.WeaponId switch
        {
            WeaponIds.DarkEagle => 2900,
            WeaponIds.ShahedDroneSwarm => Math.Min(3400, Math.Max(1500, trailCount * 13)),
            WeaponIds.SplitterMirv => Math.Min(1800, Math.Max(900, (int)MathF.Round(trailCount * 5.5f))),
            WeaponIds.Gbu57Mop => Math.Min(2100, Math.Max(900, (int)MathF.Round(trailCount * 8.5f))),
            _ => Math.Min(1200, Math.Max(260, trailCount * 4))
        };

        if (resolution.Intercepted)
            visualDuration = Math.Clamp((int)MathF.Round(visualDuration * 2.6f), 2900, 3400);

        return visualDuration;
    }

    private Task HoldRoundResultForNukeEffectsAsync(ShotResolution resolution)
    {
        if (_reducedMotion) return Task.Delay(180);

        var delay = resolution.WeaponId == WeaponIds.DoomsdayNuke ? 1350 : 760;
        return Task.Delay(delay);
    }

    private async Task BuyWeaponAsync(string weaponId)
    {
        if (_state is null || !WeaponIsEnabled(weaponId)) return;

        if (Engine.BuyWeapon(_state, weaponId))
        {
            RefreshPlayerWeapons();
            await Audio.PlayAsync("menu");
        }
    }

    private bool WeaponIsEnabled(string weaponId) =>
        _enableNuclearWeapons || Weapons.Get(weaponId).Category != WeaponCategory.Nuclear;

    private async Task BuyUpgradeAsync(UpgradeType upgrade)
    {
        if (_state is not null && Engine.BuyUpgrade(_state, upgrade))
        {
            await Audio.PlayAsync("shield");
            StateHasChanged();
        }
    }

    private Task RenderSceneAsync() => RenderSceneCoreAsync(force: false);

    private async Task RenderSceneCoreAsync(bool force)
    {
        if (_state is null || (_shotPlaybackInProgress && !force)) return;

        await EnsureRendererAsync();
        var includeTerrain = force || _lastSentTerrainRevision != _terrainRevision;
        var scene = BuildScene(includeTerrain);
        var stats = await Renderer.RenderAsync(scene);
        if (includeTerrain) _lastSentTerrainRevision = _terrainRevision;

        ApplyStats(stats);
        ApplyEffectsStats(await AwaitWithTimeoutAsync(
            Effects.SetSceneAsync(scene, _terrainRevision, _reducedMotion).AsTask(),
            900,
            "WebGPU scene update"));
        SyncBattleFpsLoop();
    }

    private async Task EnsureRendererAsync()
    {
        if (_state is null) return;

        if (!_rendererReady)
        {
            await Renderer.InitializeAsync(_canvas);
            _rendererReady = true;
        }

        if (!_effectsReady)
        {
            ApplyEffectsStats(await Effects.InitializeAsync(_canvas, _effectsCanvas, _webGpuEffectsEnabled));
            _effectsReady = true;
        }
    }

    private RenderScene BuildScene(bool includeTerrain = true)
    {
        if (_state is null)
        {
            return new RenderScene(
                new RenderWorld(GameConstants.WorldWidth, GameConstants.WorldHeight),
                null,
                1,
                new RenderWeather("clear", 0),
                0,
                GamePhase.MainMenu.ToString(),
                WeaponIds.PeaShell,
                EmptyPreview(),
                [],
                [],
                new RenderTank("player", 0, 0, 45, 0, 0, false, 0, 0),
                new RenderTank("cpu", 0, 0, 45, 0, 0, true, 0, 0));
        }

        var terrain = includeTerrain ? _state.Terrain.SolidTop.ToArray() : null;
        return new RenderScene(
            new RenderWorld(GameConstants.WorldWidth, GameConstants.WorldHeight),
            terrain,
            _state.RoundNumber,
            WeatherDto(_state),
            _state.Wind,
            _state.Phase.ToString(),
            _state.SelectedWeaponId,
            PreviewTrailDto(),
            _tracerTrails.ToArray(),
            RenderPayloadSanitizer.BuildRadiationPayload(_state.RadiationZones),
            TankDto(_state.PlayerTank, _state.Terrain),
            TankDto(_state.CpuTank, _state.Terrain),
            _playerHurt,
            _cpuHurt,
            _playerShieldHit,
            _cpuShieldHit);
    }

    private void ResetRenderCache()
    {
        _terrainRevision++;
        _lastSentTerrainRevision = -1;
    }

    private void MarkTerrainChanged() => _terrainRevision++;

    private static RenderTank TankDto(Tank tank, TerrainMask terrain) =>
        new(
            tank.Id,
            tank.Position.X,
            tank.Position.Y,
            tank.TurretAngle,
            TankHealth(tank),
            MathF.Max(0, tank.Shield),
            tank.IsCpu,
            terrain.GetSurfaceY(tank.Position.X),
            MathF.Max(0, tank.Position.Y - terrain.GetSurfaceY(tank.Position.X)));

    private static RenderWeather WeatherDto(GameState state)
    {
        var index = Math.Abs(HashCode.Combine(state.RandomSeed, state.RoundNumber)) % 4;
        var type = index switch
        {
            1 => "rain",
            2 => "snow",
            3 => "storm",
            _ => "clear"
        };
        var intensity = 0.34f + ((Math.Abs(HashCode.Combine(state.RandomSeed, state.RoundNumber, state.Wind)) % 36) / 100f);
        return new RenderWeather(type, intensity);
    }

    private bool HasTracerRounds =>
        _state?.PlayerTank.TracerRoundCharges > 0 || _state?.PlayerTank.Upgrades.Contains(UpgradeType.TracerRounds) == true;

    private int TracerTrailCapacity => Math.Max(
        _state?.PlayerTank.TracerRoundCharges ?? 0,
        _state?.PlayerTank.Upgrades.Contains(UpgradeType.TracerRounds) == true ? 1 : 0);

    private bool HasTargetingComputer => _targetingComputerEnabledByDefault || _state?.PlayerTank.Upgrades.Contains(UpgradeType.TargetingComputer) == true;

    private RenderPreviewTrail PreviewTrailDto()
    {
        if (_state is null || !HasTargetingComputer) return EmptyPreview();

        var trail = Engine.PreviewPlayerShot(_state, _settings, _state.PlayerTank.TurretAngle, _power);
        if (trail.Count < 3) return EmptyPreview();

        var apexIndex = 0;
        var apexY = float.MaxValue;
        for (var i = 0; i < trail.Count; i++)
        {
            if (trail[i].Y < apexY)
            {
                apexY = trail[i].Y;
                apexIndex = i;
            }
        }

        var path = new List<RenderPoint>(Math.Min(80, apexIndex + 1));
        var step = Math.Max(1, apexIndex / 72);
        for (var i = 0; i <= apexIndex; i += step)
        {
            var point = trail[i];
            path.Add(RenderPoint.FromVector(point));
        }

        var apex = trail[apexIndex];
        path.Add(RenderPoint.FromVector(apex));

        var remaining = trail.Count - apexIndex - 1;
        if (remaining <= 0) return new RenderPreviewTrail(path.ToArray(), []);

        var coneSteps = Math.Clamp((int)MathF.Round(remaining * 0.325f), 2, 40);
        var coneCenter = trail[Math.Min(trail.Count - 1, apexIndex + coneSteps)];
        var direction = coneCenter - apex;
        if (direction.LengthSquared() < 0.001f) return new RenderPreviewTrail(path.ToArray(), []);

        direction = Vector2.Normalize(direction);
        var normal = new Vector2(-direction.Y, direction.X);
        var distance = Vector2.Distance(apex, coneCenter);
        var jitter = TinyPreviewNoise(_state.RandomSeed, _state.RoundNumber, _state.PlayerTank.TurretAngle, _power);
        var width = Math.Clamp((distance * 0.075f) + MathF.Abs(jitter), 10f, 28f);
        var left = coneCenter + (normal * (width + jitter));
        var right = coneCenter - (normal * (width - jitter * 0.5f));

        return new RenderPreviewTrail(
            path.ToArray(),
            [
                RenderPoint.FromVector(apex),
                RenderPoint.FromVector(left),
                RenderPoint.FromVector(right)
            ]);
    }

    private static RenderPreviewTrail EmptyPreview() => new([], []);

    private void RecordTracerTrail(IReadOnlyList<Vector2> trail)
    {
        var capacity = TracerTrailCapacity;
        if (capacity <= 0 || trail.Count < 2) return;

        var payload = RenderPayloadSanitizer.BuildRenderTrailPayload(trail, 96);
        if (payload.Length < 2) return;

        _tracerTrails.Add(payload);
        while (_tracerTrails.Count > capacity)
        {
            _tracerTrails.RemoveAt(0);
        }
    }

    private static float TinyPreviewNoise(int seed, int round, float angle, int power)
    {
        var hash = HashCode.Combine(seed, round, (int)MathF.Round(angle * 10), power);
        return (((hash & int.MaxValue) % 401) - 200) / 100f;
    }

    private string InventoryLabel(string weaponId)
    {
        var count = _state?.PlayerTank.GetInventoryCount(weaponId) ?? 0;
        return weaponId == WeaponIds.PeaShell || count < 0 ? "inf" : count.ToString();
    }

    private int DisplayTankHealth(Tank tank, bool player) =>
        Math.Max(0, player ? _displayPlayerHealth ?? tank.Health : _displayCpuHealth ?? tank.Health);

    private string TankHealthWidth(Tank tank, bool player) =>
        $"{Math.Clamp(DisplayTankHealth(tank, player) / (float)Math.Max(tank.MaxHealth, 1), 0, 1) * 100:0}%";

    private static int TankHealth(Tank tank) => Math.Max(0, tank.Health);

    private float DisplayTankShield(Tank tank, bool player) =>
        MathF.Max(0, player ? _displayPlayerShield ?? tank.Shield : _displayCpuShield ?? tank.Shield);

    private string TankShieldWidth(Tank tank, bool player) =>
        $"{Math.Clamp(DisplayTankShield(tank, player) / 120f, 0, 1) * 100:0}%";

    private static bool ShieldChanged(float playerShieldBefore, float playerShieldAfter, float cpuShieldBefore, float cpuShieldAfter) =>
        playerShieldAfter < playerShieldBefore || cpuShieldAfter < cpuShieldBefore;

    private static bool HealthChanged(int playerHealthBefore, Tank player, int cpuHealthBefore, Tank cpu) =>
        TankHealth(player) < Math.Max(0, playerHealthBefore) || TankHealth(cpu) < Math.Max(0, cpuHealthBefore);

    private async Task ApplyDamageFeedbackAsync(int playerHealthBefore, int cpuHealthBefore, float playerShieldBefore, float cpuShieldBefore)
    {
        if (_state is null) return;

        _playerHurt = TankHealth(_state.PlayerTank) < Math.Max(0, playerHealthBefore);
        _cpuHurt = TankHealth(_state.CpuTank) < Math.Max(0, cpuHealthBefore);
        _playerShieldHit = _state.PlayerTank.Shield < playerShieldBefore;
        _cpuShieldHit = _state.CpuTank.Shield < cpuShieldBefore;
        ReleaseDisplayedDamage();

        if (!AnyTankFeedback) return;

        var pulse = ++_damagePulse;
        await InvokeAsync(StateHasChanged);
        _ = ClearDamageFeedbackAsync(pulse);
    }

    private async Task ClearDamageFeedbackAsync(int pulse)
    {
        await Task.Delay(_reducedMotion ? 180 : 760);
        if (pulse != _damagePulse) return;

        ClearDamageFeedback();
        await InvokeAsync(StateHasChanged);
    }

    private void ClearDamageFeedback()
    {
        _playerHurt = false;
        _cpuHurt = false;
        _playerShieldHit = false;
        _cpuShieldHit = false;
    }

    private void HoldDisplayedDamage(int playerHealthBefore, int cpuHealthBefore, float playerShieldBefore, float cpuShieldBefore)
    {
        _displayPlayerHealth = Math.Max(0, playerHealthBefore);
        _displayCpuHealth = Math.Max(0, cpuHealthBefore);
        _displayPlayerShield = MathF.Max(0, playerShieldBefore);
        _displayCpuShield = MathF.Max(0, cpuShieldBefore);
    }

    private void ReleaseDisplayedDamage()
    {
        _displayPlayerHealth = null;
        _displayCpuHealth = null;
        _displayPlayerShield = null;
        _displayCpuShield = null;
    }

    private async Task HandleKeyDown(KeyboardEventArgs args)
    {
        if (_state is null) return;

        if (args.Key == "Escape")
        {
            ToggleSettings();
            return;
        }

        if (args.Key == "`")
        {
            TogglePerf();
            return;
        }

        if (!CanFirePlayer) return;

        switch (args.Key)
        {
            case "ArrowLeft":
                _state.PlayerTank.TurretAngle = Math.Max(5, _state.PlayerTank.TurretAngle - (args.ShiftKey ? 5 : 1));
                await RenderSceneAsync();
                break;
            case "ArrowRight":
                _state.PlayerTank.TurretAngle = Math.Min(85, _state.PlayerTank.TurretAngle + (args.ShiftKey ? 5 : 1));
                await RenderSceneAsync();
                break;
            case "ArrowUp":
                _power = Math.Min(100, _power + 2);
                break;
            case "ArrowDown":
                _power = Math.Max(1, _power - 2);
                break;
            case " ":
                await FirePlayerAsync();
                break;
            case "Tab":
                CycleWeapon();
                break;
        }
    }

    private async Task HandleAngleInputAsync(ChangeEventArgs args)
    {
        if (_state is null || !float.TryParse(args.Value?.ToString(), out var angle)) return;

        _state.PlayerTank.TurretAngle = Math.Clamp(angle, 5, 85);
        await RenderSceneAsync();
    }

    private async Task HandleAngleScrubInputAsync(ChangeEventArgs args)
    {
        if (_state is null || !float.TryParse(args.Value?.ToString(), out var scrubValue)) return;

        _state.PlayerTank.TurretAngle = Math.Clamp(90f - scrubValue, 5f, 85f);
        await RenderSceneAsync();
    }

    private void CycleWeapon()
    {
        if (_state is null || _availablePlayerWeapons.Length == 0) return;

        var current = -1;
        for (var i = 0; i < _availablePlayerWeapons.Length; i++)
        {
            if (_availablePlayerWeapons[i].Id == _state.SelectedWeaponId)
            {
                current = i;
                break;
            }
        }

        _state.SelectedWeaponId = _availablePlayerWeapons[(current + 1 + _availablePlayerWeapons.Length) % _availablePlayerWeapons.Length].Id;
    }

    private void TogglePerf()
    {
        _showPerf = !_showPerf;
        if (_showPerf) StartPerfLoop();
        else StopPerfLoop();
    }

    private void ToggleSettings() => _settingsOpen = !_settingsOpen;

    private async Task ApplyAudioSettingsAsync()
    {
        await Audio.SetVolumeAsync(_sfxVolume);
        await PersistSaveAsync();
    }

    private async Task HandleScreenShakeChangedAsync(ChangeEventArgs args)
    {
        _screenShake = CheckedValue(args);
        await PersistSaveAsync();
    }

    private async Task HandleReducedMotionChangedAsync(ChangeEventArgs args)
    {
        _reducedMotion = CheckedValue(args);
        await PersistSaveAsync();
    }

    private async Task HandleTargetingComputerChangedAsync(ChangeEventArgs args)
    {
        _targetingComputerEnabledByDefault = CheckedValue(args);
        await PersistSaveAsync();
        if (_state?.Phase == GamePhase.Battle) await RenderSceneAsync();
    }

    private async Task HandleNuclearWeaponsChangedAsync(ChangeEventArgs args)
    {
        _enableNuclearWeapons = CheckedValue(args);
        _settings = _settings with { EnableNuclearWeapons = _enableNuclearWeapons };
        RefreshPlayerWeapons();
        await PersistSaveAsync();
    }

    private async Task HandleWebGpuEffectsChangedAsync(ChangeEventArgs args)
    {
        _webGpuEffectsEnabled = CheckedValue(args);
        _effectsReady = false;
        ApplyEffectsStats(await Effects.SetEnabledAsync(_webGpuEffectsEnabled));
        await PersistSaveAsync();
        if (_state is not null)
        {
            await EnsureRendererAsync();
            await RenderSceneCoreAsync(force: true);
        }
    }

    private async Task HandleRenderModeChangedAsync(ChangeEventArgs args)
    {
        if (!Enum.TryParse<RenderMode>(args.Value?.ToString(), out var mode)) return;

        _renderMode = mode;
        await Renderer.SetModeAsync(_renderMode);
        _rendererReady = false;
        _lastSentTerrainRevision = -1;
        await PersistSaveAsync();
        if (_state is not null) await RenderSceneCoreAsync(force: true);
    }

    private async Task HandleStartingCashChangedAsync(ChangeEventArgs args)
    {
        if (!int.TryParse(args.Value?.ToString(), out var cash)) return;

        _startingCash = Math.Clamp(cash, 500, 10_000);
        await PersistSaveAsync();
    }

    private async Task PersistSaveAsync()
    {
        var cash = _state?.PlayerTank.Cash ?? 0;
        _bestScore = Math.Max(_bestScore, cash);
        var save = new SaveGame(
            _bestScore,
            _state?.RoundNumber ?? 1,
            _state?.PlayerTank.IsDestroyed == true ? 1 : 0,
            _unlockedWeaponIds,
            _state?.SelectedWeaponId ?? WeaponIds.PeaShell,
            new GameSettings(
                SfxVolume: _sfxVolume,
                ScreenShake: _screenShake,
                ReducedMotion: _reducedMotion,
                EnableNuclearWeapons: _enableNuclearWeapons,
                Difficulty: _difficulty,
                StartingCash: _startingCash,
                TargetingComputerEnabledByDefault: _targetingComputerEnabledByDefault,
                RenderMode: _renderMode,
                WebGpuEffectsEnabled: _webGpuEffectsEnabled));
        await Storage.SetAsync("armageddon-ridge-save", save);
    }

    public async ValueTask DisposeAsync()
    {
        var fpsButtonLoopTask = _fpsButtonLoopTask;
        var perfLoopTask = _perfLoopTask;
        StopFpsButtonLoop();
        StopPerfLoop();
        if (fpsButtonLoopTask is not null)
            await AwaitQuietlyAsync(fpsButtonLoopTask);
        if (perfLoopTask is not null)
            await AwaitQuietlyAsync(perfLoopTask);

        await Effects.DisposeAsync();
        await Renderer.DisposeAsync();
        await Audio.DisposeAsync();
    }

    private void ApplyStats(RenderStats? stats)
    {
        if (stats is null) return;

        _fps = stats.Fps;
        _frameMs = stats.FrameMs;
        _renderMs = stats.RenderMs;
        _sceneBuildMs = stats.SceneBuildMs;
        _commandBuildMs = stats.CommandBuildMs;
        _submitMs = stats.SubmitMs;
        _commandCount = stats.CommandCount;
        _payloadBytes = stats.PayloadBytes;
        _simdHardwareAccelerated = stats.SimdHardwareAccelerated;
        _rendererModeLabel = stats.Mode;
    }

    private void ApplyEffectsStats(WebGpuEffectsStats? stats)
    {
        if (stats is null) return;

        _effectsSupported = stats.Supported;
        _effectsActive = stats.Enabled;
        _effectFrameMs = stats.FrameMs;
        _effectPostProcessMs = stats.PostProcessMs;
        _effectSourceCopyMs = stats.SourceCopyMs;
        _effectParticleCount = stats.ParticleCount;
        _effectActiveParticleCount = stats.ActiveParticleCount;
        _effectParticleCapacity = stats.ParticleCapacity;
        _effectRadialEffectCount = stats.RadialEffectCount;
        _effectSpawnCount = stats.SpawnCount;
        _effectOverlayScale = stats.OverlayScale;
        _effectCanvasPixelRatio = stats.CanvasPixelRatio;
        _effectSourceCopyCadence = stats.SourceCopyCadence;
        _effectGpuQueueMs = stats.GpuQueueMs;
        _effectPerfMode = string.IsNullOrWhiteSpace(stats.PerfMode) ? "adaptive" : stats.PerfMode;
        _effectQualityTier = string.IsNullOrWhiteSpace(stats.QualityTier) ? "n/a" : stats.QualityTier;
        _effectFallbackReason = stats.FallbackReason;
    }

    private void SyncBattleFpsLoop()
    {
        if (VisiblePhase == GamePhase.Battle)
        {
            StartFpsButtonLoop();
        }
        else
        {
            StopFpsButtonLoop();
        }
    }

    private void StartFpsButtonLoop()
    {
        if (_fpsButtonLoopTask is { IsCompleted: false })
            return;

        _fpsButtonLoop?.Dispose();
        _fpsButtonLoop = new CancellationTokenSource();
        _fpsButtonLoopTask = PollFpsButtonAsync(_fpsButtonLoop);
    }

    private void StopFpsButtonLoop()
    {
        if (_fpsButtonLoop is null)
            return;

        _fpsButtonLoop.Cancel();
    }

    private async Task PollFpsButtonAsync(CancellationTokenSource source)
    {
        var cancellationToken = source.Token;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(250, cancellationToken);

                if (_showPerf)
                    continue;

                var rendererStats = await Renderer.GetStatsAsync();
                if (cancellationToken.IsCancellationRequested) return;
                ApplyStats(rendererStats);

                await InvokeAsync(StateHasChanged);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (JSException ex)
        {
            _state?.EventLog.Add($"FPS polling stopped: {ex.Message}");
        }
        catch (Exception ex)
        {
            _state?.EventLog.Add($"FPS polling stopped: {ex.Message}");
        }
        finally
        {
            if (ReferenceEquals(_fpsButtonLoop, source))
            {
                _fpsButtonLoop = null;
                _fpsButtonLoopTask = null;
            }

            source.Dispose();
        }
    }

    private void StartPerfLoop()
    {
        if (_perfLoopTask is { IsCompleted: false })
            return;

        _perfLoop?.Dispose();
        _perfLoop = new CancellationTokenSource();
        _perfLoopTask = PollPerfAsync(_perfLoop);
    }

    private void StopPerfLoop()
    {
        if (_perfLoop is null)
            return;

        _perfLoop?.Cancel();
    }

    private async Task PollPerfAsync(CancellationTokenSource source)
    {
        var cancellationToken = source.Token;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(250, cancellationToken);

                var rendererStats = await Renderer.GetStatsAsync();
                if (cancellationToken.IsCancellationRequested) return;
                ApplyStats(rendererStats);

                var effectsStats = await Effects.GetStatsAsync();
                if (cancellationToken.IsCancellationRequested) return;
                ApplyEffectsStats(effectsStats);

                await InvokeAsync(StateHasChanged);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (JSException ex)
        {
            _state?.EventLog.Add($"Performance overlay polling stopped: {ex.Message}");
            _showPerf = false;
            await InvokeAsync(StateHasChanged);
        }
        catch (Exception ex)
        {
            _state?.EventLog.Add($"Performance overlay polling stopped: {ex.Message}");
            _showPerf = false;
            await InvokeAsync(StateHasChanged);
        }
        finally
        {
            if (ReferenceEquals(_perfLoop, source))
            {
                _perfLoop = null;
                _perfLoopTask = null;
            }

            source.Dispose();
        }
    }

    private void RefreshPlayerWeapons()
    {
        if (_state is null)
        {
            _availablePlayerWeapons = [];
            return;
        }

        var weapons = new List<WeaponDefinition>(_allWeapons.Length);
        for (var i = 0; i < _allWeapons.Length; i++)
        {
            var weapon = _allWeapons[i];
            if (!WeaponIsEnabled(weapon.Id)) continue;
            if (_state.PlayerTank.HasWeapon(weapon.Id) || weapon.Id == WeaponIds.PeaShell) weapons.Add(weapon);
        }

        _availablePlayerWeapons = weapons.ToArray();
        EnsureSelectedWeaponIsAvailable();
    }

    private void EnsureSelectedWeaponIsAvailable()
    {
        if (_state is null || _availablePlayerWeapons.Length == 0) return;

        for (var i = 0; i < _availablePlayerWeapons.Length; i++)
        {
            if (_availablePlayerWeapons[i].Id == _state.SelectedWeaponId) return;
        }

        _state.SelectedWeaponId = _availablePlayerWeapons[0].Id;
    }

    private static string[] BuildUnlockedWeaponIds(IReadOnlyList<WeaponDefinition> weapons)
    {
        var ids = new string[weapons.Count];
        for (var i = 0; i < weapons.Count; i++) ids[i] = weapons[i].Id;
        return ids;
    }

    private static bool CheckedValue(ChangeEventArgs args) =>
        args.Value is bool value ? value : bool.TryParse(args.Value?.ToString(), out var parsed) && parsed;

    private static bool HasNuclearExplosion(IReadOnlyList<ExplosionResult> explosions)
    {
        for (var i = 0; i < explosions.Count; i++)
        {
            if (explosions[i].Nuclear) return true;
        }

        return false;
    }

    private static bool HasLargeExplosion(IReadOnlyList<ExplosionResult> explosions)
    {
        for (var i = 0; i < explosions.Count; i++)
        {
            if (explosions[i].DamageRadius > 80) return true;
        }

        return false;
    }
}
