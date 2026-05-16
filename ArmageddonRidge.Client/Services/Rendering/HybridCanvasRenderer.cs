using System.Numerics;
using ArmageddonRidge.Core.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace ArmageddonRidge.Client.Services.Rendering;

public sealed class HybridCanvasRenderer(IJSRuntime js) : IGameRenderer
{
    private IJSObjectReference? _module;

    public RenderMode Mode => RenderMode.Hybrid;

    public async ValueTask InitializeAsync(ElementReference canvas)
    {
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/canvasRenderer.js");
        await _module.InvokeVoidAsync("initialize", canvas);
    }

    public async ValueTask<RenderStats?> RenderAsync(RenderScene scene)
    {
        if (_module is null) return null;

        var stats = await _module.InvokeAsync<RenderStats>("render", scene);
        return stats with { Mode = "Hybrid (JS + WASM)", SimdHardwareAccelerated = false };
    }

    public async ValueTask PlayShotAsync(RenderScene scene, ShotResolution resolution, bool screenShake, bool suppressCanvasPatriotCountermeasure = false)
    {
        if (_module is null) return;

        await PlayShotAsync(scene, resolution.Trail, resolution.Explosions, screenShake, resolution.WeaponId, resolution.Intercepted, resolution.InterceptPoint, resolution.OwnerTankId, resolution.VisualKind.ToString(), suppressCanvasPatriotCountermeasure);
    }

    public async ValueTask<RenderStats?> GetStatsAsync()
    {
        if (_module is null) return null;

        var stats = await _module.InvokeAsync<RenderStats>("getStats");
        return stats with { Mode = "Hybrid (JS + WASM)", SimdHardwareAccelerated = false };
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not null) await _module.DisposeAsync();
    }

    private async ValueTask PlayShotAsync(
        RenderScene scene,
        IReadOnlyList<Vector2> trail,
        IReadOnlyList<ExplosionResult> explosions,
        bool screenShake,
        string? weaponId = null,
        bool intercepted = false,
        Vector2? interceptPoint = null,
        string? ownerTankId = null,
        string? visualKind = null,
        bool suppressCanvasPatriotCountermeasure = false)
    {
        if (_module is null) return;

        var trailPayload = new object[trail.Count];
        for (var i = 0; i < trail.Count; i++)
        {
            var point = trail[i];
            trailPayload[i] = new { x = point.X, y = point.Y };
        }

        var explosionPayload = new object[explosions.Count];
        for (var i = 0; i < explosions.Count; i++)
        {
            var explosion = explosions[i];
            explosionPayload[i] = new
            {
                x = explosion.Center.X,
                y = explosion.Center.Y,
                radius = explosion.DamageRadius,
                terrainRadius = explosion.TerrainRadius,
                nuclear = explosion.Nuclear,
                dirt = explosion.DirtAdded,
                weaponId,
                visualKind = explosion.VisualKind.ToString(),
                napalm = explosion.VisualKind == ShotVisualKind.Fire,
                lava = explosion.VisualKind == ShotVisualKind.Lava,
                missile = explosion.VisualKind == ShotVisualKind.Missile,
                drone = explosion.VisualKind == ShotVisualKind.DroneSwarm,
                patriotIntercept = explosion.VisualKind == ShotVisualKind.PatriotIntercept,
                triggerIndex = explosion.TriggerTrailIndex
            };
        }

        await _module.InvokeVoidAsync(
            "playShot",
            scene,
            trailPayload,
            explosionPayload,
            screenShake,
            weaponId,
            new
            {
                intercepted,
                interceptX = interceptPoint?.X,
                interceptY = interceptPoint?.Y,
                ownerTankId,
                visualKind,
                suppressCanvasPatriotCountermeasure
            });
    }
}
