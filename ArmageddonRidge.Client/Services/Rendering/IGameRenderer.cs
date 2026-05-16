using ArmageddonRidge.Core.Models;
using Microsoft.AspNetCore.Components;

namespace ArmageddonRidge.Client.Services.Rendering;

public interface IGameRenderer : IAsyncDisposable
{
    RenderMode Mode { get; }

    ValueTask InitializeAsync(ElementReference canvas);

    ValueTask<RenderStats?> RenderAsync(RenderScene scene);

    ValueTask PlayShotAsync(RenderScene scene, ShotResolution resolution, bool screenShake, bool suppressCanvasPatriotCountermeasure = false);

    ValueTask<RenderStats?> GetStatsAsync();
}
