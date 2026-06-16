using ArmageddonRidge.Client.Services.Rendering;
using ArmageddonRidge.Core.Models;
using Microsoft.AspNetCore.Components;

namespace ArmageddonRidge.Client.Services;

public sealed class CanvasRenderer(HybridCanvasRenderer hybrid, WasmCanvasRenderer fullWasm) : IAsyncDisposable
{
    private ElementReference _canvas;
    private RenderMode _mode = RenderMode.Hybrid;
    private IGameRenderer? _active;

    public RenderMode Mode => _mode;

    public async ValueTask SetModeAsync(RenderMode mode)
    {
        if (_mode == mode) return;

        if (_active is not null)
        {
            await _active.DisposeAsync();
        }

        _mode = mode;
        _active = null;
    }

    public async ValueTask InitializeAsync(ElementReference canvas)
    {
        _canvas = canvas;
        await EnsureActiveAsync();
    }

    public async ValueTask<RenderStats?> RenderAsync(RenderScene scene)
    {
        var renderer = await EnsureActiveAsync();
        return await renderer.RenderAsync(scene);
    }

    public async ValueTask PlayShotAsync(RenderScene scene, ShotResolution resolution, bool screenShake, bool playerDestroyed, bool cpuDestroyed)
    {
        var renderer = await EnsureActiveAsync();
        await renderer.PlayShotAsync(scene, resolution, screenShake, playerDestroyed, cpuDestroyed);
    }

    public async ValueTask<RenderStats?> GetStatsAsync()
    {
        var renderer = await EnsureActiveAsync();
        return await renderer.GetStatsAsync();
    }

    public async ValueTask DisposeAsync()
    {
        await hybrid.DisposeAsync();
        await fullWasm.DisposeAsync();
        _active = null;
    }

    private async ValueTask<IGameRenderer> EnsureActiveAsync()
    {
        IGameRenderer renderer = _mode == RenderMode.FullWasm ? fullWasm : hybrid;
        if (!ReferenceEquals(_active, renderer))
        {
            await renderer.InitializeAsync(_canvas);
            _active = renderer;
        }

        return renderer;
    }
}
