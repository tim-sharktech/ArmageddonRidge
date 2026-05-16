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

    public void SetMode(RenderMode mode)
    {
        if (_mode == mode) return;

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

    public async ValueTask PlayShotAsync(RenderScene scene, ShotResolution resolution, bool screenShake, bool suppressCanvasPatriotCountermeasure = false)
    {
        var renderer = await EnsureActiveAsync();
        await renderer.PlayShotAsync(scene, resolution, screenShake, suppressCanvasPatriotCountermeasure);
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
