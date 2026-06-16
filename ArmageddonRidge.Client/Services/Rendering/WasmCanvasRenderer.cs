using System.Diagnostics;
using ArmageddonRidge.Core.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace ArmageddonRidge.Client.Services.Rendering;

public sealed class WasmCanvasRenderer(IJSRuntime js, WasmRenderCommandBuilder commands) : IGameRenderer
{
    private IJSObjectReference? _module;
    private RenderStats _stats = new() { Mode = "Full WASM", SimdHardwareAccelerated = System.Numerics.Vector.IsHardwareAccelerated };

    public RenderMode Mode => RenderMode.FullWasm;

    public async ValueTask InitializeAsync(ElementReference canvas)
    {
        _module ??= await js.InvokeAsync<IJSObjectReference>("import", "./js/wasmCanvasBridge.js");
        await _module.InvokeVoidAsync("initialize", canvas);
    }

    public async ValueTask<RenderStats?> RenderAsync(RenderScene scene)
    {
        if (_module is null) return null;

        var commandTimer = Stopwatch.StartNew();
        var frame = commands.BuildFrame(scene);
        commandTimer.Stop();
        var diagnostics = commands.Diagnostics(frame, 0, commandTimer.Elapsed.TotalMilliseconds);
        var started = Stopwatch.StartNew();
        var bridgeStats = await _module.InvokeAsync<RenderStats>("submit", frame);
        started.Stop();

        _stats = bridgeStats with
        {
            Mode = "Full WASM",
            SceneBuildMs = diagnostics.SceneBuildMs,
            CommandBuildMs = diagnostics.CommandBuildMs,
            SubmitMs = started.Elapsed.TotalMilliseconds,
            CommandCount = diagnostics.CommandCount,
            PayloadBytes = diagnostics.PayloadBytes,
            SimdHardwareAccelerated = diagnostics.SimdHardwareAccelerated
        };
        return _stats;
    }

    public async ValueTask PlayShotAsync(RenderScene scene, ShotResolution resolution, bool screenShake, bool playerDestroyed, bool cpuDestroyed)
    {
        if (_module is null) return;

        var trail = WasmRenderCommandBuilder.DownsampleTrailSimd(resolution.Trail, 180);
        var explosions = BuildExplosions(resolution);
        var duration = Math.Clamp(trail.Length * 8, 420, resolution.VisualKind == ShotVisualKind.Nuclear ? 1600 : 1050);
        var started = await _module.InvokeAsync<double>("requestFrame");
        double now = started;

        while (now - started < duration)
        {
            var progress = (float)Math.Clamp((now - started) / duration, 0, 1);
            var commandTimer = Stopwatch.StartNew();
            var frame = commands.BuildFrame(scene, trail, explosions, progress);
            commandTimer.Stop();
            var diagnostics = commands.Diagnostics(frame, 0, commandTimer.Elapsed.TotalMilliseconds);
            var submitTimer = Stopwatch.StartNew();
            var bridgeStats = await _module.InvokeAsync<RenderStats>("submit", frame);
            submitTimer.Stop();
            _stats = bridgeStats with
            {
                Mode = "Full WASM",
                CommandBuildMs = diagnostics.CommandBuildMs,
                SubmitMs = submitTimer.Elapsed.TotalMilliseconds,
                CommandCount = diagnostics.CommandCount,
                PayloadBytes = diagnostics.PayloadBytes,
                SimdHardwareAccelerated = diagnostics.SimdHardwareAccelerated
            };
            now = await _module.InvokeAsync<double>("requestFrame");
        }

        await RenderAsync(scene);
    }

    public ValueTask<RenderStats?> GetStatsAsync() => ValueTask.FromResult<RenderStats?>(_stats);

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

    private static WasmExplosion[] BuildExplosions(ShotResolution resolution)
    {
        var result = new WasmExplosion[resolution.Explosions.Count];
        for (var i = 0; i < resolution.Explosions.Count; i++)
        {
            var explosion = resolution.Explosions[i];
            result[i] = new WasmExplosion(
                explosion.Center.X,
                explosion.Center.Y,
                explosion.DamageRadius,
                explosion.TerrainRadius,
                explosion.Nuclear,
                explosion.DirtAdded,
                explosion.VisualKind.ToString(),
                explosion.TriggerTrailIndex);
        }

        return result;
    }
}
