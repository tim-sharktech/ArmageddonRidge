using System.Numerics;
using ArmageddonRidge.Core.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace ArmageddonRidge.Client.Services.Rendering;

public sealed class WebGpuEffectsRenderer(IJSRuntime js) : IAsyncDisposable
{
    private IJSObjectReference? _module;
    private WebGpuEffectsStats _stats = new();
    private int _lastEffectsTerrainRevision = int.MinValue;

    public async ValueTask<WebGpuEffectsStats?> InitializeAsync(ElementReference baseCanvas, ElementReference effectsCanvas, bool enabled)
    {
        try
        {
            _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/webGpuEffectsRenderer.js");
            _stats = await _module.InvokeAsync<WebGpuEffectsStats>("initialize", baseCanvas, effectsCanvas, new { enabled })
                ?? new WebGpuEffectsStats();
        }
        catch (JSException ex)
        {
            _stats = DisabledStats(ex.Message);
        }

        return _stats;
    }

    public async ValueTask<WebGpuEffectsStats?> SetEnabledAsync(bool enabled)
    {
        if (_module is null)
        {
            _stats = _stats with
            {
                Enabled = false,
                FallbackReason = enabled ? "Not initialized" : "Disabled"
            };
            return _stats;
        }

        try
        {
            _stats = await _module.InvokeAsync<WebGpuEffectsStats>("setEnabled", enabled)
                ?? new WebGpuEffectsStats();
        }
        catch (JSException ex)
        {
            _stats = DisabledStats(ex.Message);
        }

        return _stats;
    }

    public async ValueTask<WebGpuEffectsStats?> SetSceneAsync(RenderScene scene, int terrainRevision, bool reducedMotion)
    {
        if (_module is null) return _stats;

        try
        {
            _stats = await _module.InvokeAsync<WebGpuEffectsStats>(
                    "setScene",
                    BuildEffectsScenePayload(scene, terrainRevision),
                    terrainRevision,
                    new { reducedMotion })
                ?? _stats;
            _lastEffectsTerrainRevision = terrainRevision;
        }
        catch (JSException ex)
        {
            _stats = DisabledStats(ex.Message);
        }

        return _stats;
    }

    public async ValueTask<WebGpuEffectsStats?> SpawnShotEffectsAsync(
        RenderScene scene,
        ShotResolution resolution,
        int wind,
        int terrainRevision,
        bool shieldHit,
        bool healthHit,
        bool playerDestroyed,
        bool cpuDestroyed,
        bool reducedMotion,
        string phase,
        bool patriotOverlayEnabled = true)
    {
        if (_module is null) return _stats;

        try
        {
            _stats = await _module.InvokeAsync<WebGpuEffectsStats>(
                    "spawnShotEffects",
                    BuildShotPayload(scene, resolution, wind, terrainRevision, shieldHit, healthHit, playerDestroyed, cpuDestroyed, reducedMotion, phase, patriotOverlayEnabled))
                ?? _stats;
        }
        catch (JSException ex)
        {
            _stats = DisabledStats(ex.Message);
        }

        return _stats;
    }

    public async ValueTask<WebGpuEffectsStats?> SpawnTerrainEffectsAsync(
        ShotResolution resolution,
        int wind,
        int terrainRevision,
        bool reducedMotion)
    {
        if (_module is null) return _stats;

        try
        {
            _stats = await _module.InvokeAsync<WebGpuEffectsStats>(
                    "spawnTerrainEffects",
                    BuildTerrainPayload(resolution, wind, terrainRevision, reducedMotion))
                ?? _stats;
        }
        catch (JSException ex)
        {
            _stats = DisabledStats(ex.Message);
        }

        return _stats;
    }

    public async ValueTask<WebGpuEffectsStats?> GetStatsAsync()
    {
        if (_module is null) return _stats;

        try
        {
            _stats = await _module.InvokeAsync<WebGpuEffectsStats>("getStats") ?? _stats;
        }
        catch (JSException ex)
        {
            _stats = DisabledStats(ex.Message);
        }

        return _stats;
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is null) return;

        try
        {
            await _module.InvokeVoidAsync("dispose");
            await _module.DisposeAsync();
        }
        catch (JSException)
        {
        }
        finally
        {
            _module = null;
            _lastEffectsTerrainRevision = int.MinValue;
        }
    }

    private static object BuildShotPayload(
        RenderScene scene,
        ShotResolution resolution,
        int wind,
        int terrainRevision,
        bool shieldHit,
        bool healthHit,
        bool playerDestroyed,
        bool cpuDestroyed,
        bool reducedMotion,
        string phase,
        bool patriotOverlayEnabled)
    {
        var trail = RenderPayloadSanitizer.BuildEffectTrailPayload(resolution.Trail, 160);
        var explosions = RenderPayloadSanitizer.BuildEffectExplosionPayload(resolution.Explosions);
        var destruction = RenderPayloadSanitizer.SanitizeFinalShotDestruction(
            FinalShotDestructionBuilder.Build(scene, resolution, wind, reducedMotion, playerDestroyed, cpuDestroyed));
        float? interceptX = null;
        float? interceptY = null;
        var hasValidIntercept = false;
        if (resolution.Intercepted
            && RenderPayloadSanitizer.TryGetFinitePoint(resolution.InterceptPoint, out var resolvedInterceptX, out var resolvedInterceptY))
        {
            hasValidIntercept = true;
            interceptX = resolvedInterceptX;
            interceptY = resolvedInterceptY;
        }

        return new
        {
            resolution.WeaponId,
            resolution.OwnerTankId,
            VisualKind = resolution.VisualKind.ToString(),
            Intercepted = hasValidIntercept,
            InterceptX = interceptX,
            InterceptY = interceptY,
            TrailPointCount = trail.Length,
            Trail = trail,
            Explosions = explosions,
            FinalShotDestruction = destruction,
            TerrainColumnsTouched = resolution.Performance.TerrainColumnsTouched,
            Wind = wind,
            TerrainRevision = terrainRevision,
            ShieldHit = shieldHit,
            HealthHit = healthHit,
            ReducedMotion = reducedMotion,
            Phase = phase,
            PatriotOverlayEnabled = patriotOverlayEnabled
        };
    }

    private static object BuildTerrainPayload(
        ShotResolution resolution,
        int wind,
        int terrainRevision,
        bool reducedMotion) =>
        new
        {
            resolution.WeaponId,
            VisualKind = resolution.VisualKind.ToString(),
            Explosions = RenderPayloadSanitizer.BuildEffectExplosionPayload(resolution.Explosions),
            TerrainColumnsTouched = resolution.Performance.TerrainColumnsTouched,
            Wind = wind,
            TerrainRevision = terrainRevision,
            ReducedMotion = reducedMotion
        };

    private object BuildEffectsScenePayload(RenderScene scene, int terrainRevision) =>
        new
        {
            scene.World,
            Terrain = terrainRevision == _lastEffectsTerrainRevision ? null : scene.Terrain,
            scene.Weather,
            scene.Wind,
            scene.Radiation,
            Player = TankPayload(scene.Player),
            Cpu = TankPayload(scene.Cpu)
        };

    private static object TankPayload(RenderTank tank) =>
        new
        {
            tank.Id,
            tank.X,
            tank.Y,
            tank.IsCpu
        };

    private static WebGpuEffectsStats DisabledStats(string reason) =>
        new()
        {
            Supported = false,
            Enabled = false,
            FallbackReason = string.IsNullOrWhiteSpace(reason) ? "Unavailable" : reason
        };
}
