using System.Numerics;
using ArmageddonRidge.Core.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace ArmageddonRidge.Client.Services.Rendering;

public sealed class WebGpuEffectsRenderer(IJSRuntime js) : IAsyncDisposable
{
    private IJSObjectReference? _module;
    private WebGpuEffectsStats _stats = new();

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
                    scene,
                    terrainRevision,
                    new { reducedMotion })
                ?? _stats;
        }
        catch (JSException ex)
        {
            _stats = DisabledStats(ex.Message);
        }

        return _stats;
    }

    public async ValueTask<WebGpuEffectsStats?> SpawnShotEffectsAsync(
        ShotResolution resolution,
        int wind,
        int terrainRevision,
        bool shieldHit,
        bool healthHit,
        bool reducedMotion,
        string phase,
        bool patriotOverlayEnabled = true)
    {
        if (_module is null) return _stats;

        try
        {
            _stats = await _module.InvokeAsync<WebGpuEffectsStats>(
                    "spawnShotEffects",
                    BuildShotPayload(resolution, wind, terrainRevision, shieldHit, healthHit, reducedMotion, phase, patriotOverlayEnabled))
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
        if (_module is not null)
        {
            try
            {
                await _module.InvokeVoidAsync("dispose");
                await _module.DisposeAsync();
            }
            catch (JSException)
            {
            }
        }
    }

    private static object BuildShotPayload(
        ShotResolution resolution,
        int wind,
        int terrainRevision,
        bool shieldHit,
        bool healthHit,
        bool reducedMotion,
        string phase,
        bool patriotOverlayEnabled) =>
        new
        {
            resolution.WeaponId,
            resolution.OwnerTankId,
            VisualKind = resolution.VisualKind.ToString(),
            resolution.Intercepted,
            InterceptX = resolution.InterceptPoint?.X,
            InterceptY = resolution.InterceptPoint?.Y,
            TrailPointCount = resolution.Trail.Count,
            Trail = DownsampleTrail(resolution.Trail, 160),
            Explosions = ExplosionPayload(resolution.Explosions),
            TerrainColumnsTouched = resolution.Performance.TerrainColumnsTouched,
            Wind = wind,
            TerrainRevision = terrainRevision,
            ShieldHit = shieldHit,
            HealthHit = healthHit,
            ReducedMotion = reducedMotion,
            Phase = phase,
            PatriotOverlayEnabled = patriotOverlayEnabled
        };

    private static object BuildTerrainPayload(
        ShotResolution resolution,
        int wind,
        int terrainRevision,
        bool reducedMotion) =>
        new
        {
            resolution.WeaponId,
            VisualKind = resolution.VisualKind.ToString(),
            Explosions = ExplosionPayload(resolution.Explosions),
            TerrainColumnsTouched = resolution.Performance.TerrainColumnsTouched,
            Wind = wind,
            TerrainRevision = terrainRevision,
            ReducedMotion = reducedMotion
        };

    private static object[] ExplosionPayload(IReadOnlyList<ExplosionResult> explosions)
    {
        var payload = new object[explosions.Count];
        for (var i = 0; i < explosions.Count; i++)
        {
            var explosion = explosions[i];
            payload[i] = new
            {
                X = explosion.Center.X,
                Y = explosion.Center.Y,
                Radius = explosion.DamageRadius,
                TerrainRadius = explosion.TerrainRadius,
                explosion.Nuclear,
                Dirt = explosion.DirtAdded,
                VisualKind = explosion.VisualKind.ToString(),
                explosion.PlayerDamage,
                explosion.CpuDamage,
                TriggerIndex = explosion.TriggerTrailIndex
            };
        }

        return payload;
    }

    private static object[] DownsampleTrail(IReadOnlyList<Vector2> trail, int maxPoints)
    {
        if (trail.Count == 0) return [];

        var count = Math.Min(trail.Count, maxPoints);
        var result = new object[count];
        var stride = trail.Count <= count ? 1 : (trail.Count - 1) / (float)Math.Max(1, count - 1);
        for (var i = 0; i < count; i++)
        {
            var point = trail[Math.Min(trail.Count - 1, (int)MathF.Round(i * stride))];
            result[i] = new { X = point.X, Y = point.Y };
        }

        return result;
    }

    private static WebGpuEffectsStats DisabledStats(string reason) =>
        new()
        {
            Supported = false,
            Enabled = false,
            FallbackReason = string.IsNullOrWhiteSpace(reason) ? "Unavailable" : reason
        };
}
