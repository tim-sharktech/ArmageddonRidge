namespace ArmageddonRidge.Client.Services.Rendering;

public sealed record RenderStats
{
    public int Fps { get; init; }
    public double FrameMs { get; init; }
    public double RenderMs { get; init; }
    public double SceneBuildMs { get; init; }
    public double CommandBuildMs { get; init; }
    public double SubmitMs { get; init; }
    public int CommandCount { get; init; }
    public int PayloadBytes { get; init; }
    public bool SimdHardwareAccelerated { get; init; }
    public bool WebGpuEffectsSupported { get; init; }
    public bool WebGpuEffectsEnabled { get; init; }
    public double EffectFrameMs { get; init; }
    public double EffectPostProcessMs { get; init; }
    public double EffectSourceCopyMs { get; init; }
    public int EffectParticleCount { get; init; }
    public int EffectRadialEffectCount { get; init; }
    public int EffectSpawnCount { get; init; }
    public string EffectQualityTier { get; init; } = "n/a";
    public string EffectFallbackReason { get; init; } = string.Empty;
    public string Mode { get; init; } = "Hybrid (JS + WASM)";
}

public sealed record WebGpuEffectsStats
{
    public bool Supported { get; init; }
    public bool Enabled { get; init; }
    public double FrameMs { get; init; }
    public double PostProcessMs { get; init; }
    public double SourceCopyMs { get; init; }
    public int ParticleCount { get; init; }
    public int RadialEffectCount { get; init; }
    public int SpawnCount { get; init; }
    public string QualityTier { get; init; } = "n/a";
    public string FallbackReason { get; init; } = "Not initialized";
}
