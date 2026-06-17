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

    public async ValueTask PlayShotAsync(RenderScene scene, ShotResolution resolution, bool screenShake)
    {
        if (_module is null) return;

        await PlayShotAsync(scene, resolution.Trail, resolution.Explosions, screenShake, resolution.WeaponId, resolution.Intercepted, resolution.InterceptPoint, resolution.OwnerTankId, resolution.VisualKind.ToString(), resolution.VisualPhysics, resolution.CivilianImpacts);
    }

    public async ValueTask<RenderStats?> GetStatsAsync()
    {
        if (_module is null) return null;

        var stats = await _module.InvokeAsync<RenderStats>("getStats");
        return stats with { Mode = "Hybrid (JS + WASM)", SimdHardwareAccelerated = false };
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
        }
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
        ArmageddonRidge.Core.Physics.VisualPhysicsPayload? visualPhysics = null,
        IReadOnlyList<CivilianImpactResult>? civilianImpacts = null)
    {
        if (_module is null) return;

        var trailPayload = RenderPayloadSanitizer.BuildTrailPayload(trail);
        var explosionPayload = RenderPayloadSanitizer.BuildExplosionPayload(explosions, weaponId);
        var playbackOptions = RenderPayloadSanitizer.BuildPlaybackOptions(intercepted, interceptPoint, ownerTankId, visualKind, visualPhysics, civilianImpacts);

        await _module.InvokeVoidAsync(
            "playShot",
            scene,
            trailPayload,
            explosionPayload,
            screenShake,
            weaponId,
            playbackOptions);
    }
}
