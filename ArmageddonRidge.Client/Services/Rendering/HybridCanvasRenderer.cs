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

    public async ValueTask PlayShotAsync(RenderScene scene, ShotResolution resolution, bool screenShake, bool playerDestroyed, bool cpuDestroyed)
    {
        if (_module is null) return;

        await PlayShotWithPayloadAsync(scene, resolution, screenShake, playerDestroyed, cpuDestroyed);
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

    private async ValueTask PlayShotWithPayloadAsync(
        RenderScene scene,
        ShotResolution resolution,
        bool screenShake,
        bool playerDestroyed,
        bool cpuDestroyed)
    {
        if (_module is null) return;

        var trailPayload = RenderPayloadSanitizer.BuildTrailPayload(resolution.Trail);
        var explosionPayload = RenderPayloadSanitizer.BuildExplosionPayload(resolution.Explosions, resolution.WeaponId);
        var finalShotDestruction = RenderPayloadSanitizer.SanitizeFinalShotDestruction(
            FinalShotDestructionBuilder.Build(scene, resolution, scene.Wind, false, playerDestroyed, cpuDestroyed));
        var playbackOptions = RenderPayloadSanitizer.BuildPlaybackOptions(
            resolution.Intercepted,
            resolution.InterceptPoint,
            resolution.OwnerTankId,
            resolution.VisualKind.ToString(),
            resolution.VisualPhysics,
            resolution.CivilianImpacts,
            finalShotDestruction);

        await _module.InvokeVoidAsync(
            "playShot",
            scene,
            trailPayload,
            explosionPayload,
            screenShake,
            resolution.WeaponId,
            playbackOptions);
    }
}
