using System.Numerics;
using ArmageddonRidge.Core.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace ArmageddonRidge.Client.Services;

public sealed class CanvasRenderer : IAsyncDisposable
{
    private readonly IJSRuntime _js;
    private IJSObjectReference? _module;

    public CanvasRenderer(IJSRuntime js)
    {
        _js = js;
    }

    public async ValueTask InitializeAsync(ElementReference canvas)
    {
        _module ??= await _js.InvokeAsync<IJSObjectReference>("import", "./js/canvasRenderer.js");
        await _module.InvokeVoidAsync("initialize", canvas);
    }

    public async ValueTask<RenderStats?> RenderAsync(object scene)
    {
        if (_module is null)
        {
            return null;
        }

        return await _module.InvokeAsync<RenderStats>("render", scene);
    }

    public async ValueTask PlayShotAsync(object scene, IReadOnlyList<Vector2> trail, IReadOnlyList<ExplosionResult> explosions, bool screenShake)
    {
        if (_module is null)
        {
            return;
        }

        await _module.InvokeVoidAsync(
            "playShot",
            scene,
            trail.Select(static point => new { x = point.X, y = point.Y }),
            explosions.Select(static explosion => new
            {
                x = explosion.Center.X,
                y = explosion.Center.Y,
                radius = explosion.DamageRadius,
                terrainRadius = explosion.TerrainRadius,
                nuclear = explosion.Nuclear,
                dirt = explosion.DirtAdded
            }),
            screenShake);
    }

    public async ValueTask<RenderStats?> GetStatsAsync()
    {
        if (_module is null)
        {
            return null;
        }

        return await _module.InvokeAsync<RenderStats>("getStats");
    }

    public async ValueTask DisposeAsync()
    {
        if (_module is not null)
        {
            await _module.DisposeAsync();
        }
    }
}

public sealed record RenderStats(int Fps, double FrameMs, double RenderMs);
