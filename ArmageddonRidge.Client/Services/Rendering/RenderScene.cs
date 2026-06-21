using System.Numerics;
using System.Text.Json.Serialization;

namespace ArmageddonRidge.Client.Services.Rendering;

public sealed record RenderScene(
    RenderWorld World,
    float[]? Terrain,
    int Round,
    RenderWeather Weather,
    int Wind,
    string Phase,
    string SelectedWeapon,
    RenderPreviewTrail PreviewTrail,
    RenderPoint[][] TracerTrails,
    RenderRadiationZone[] Radiation,
    RenderTank Player,
    RenderTank Cpu,
    bool PlayerHurt = false,
    bool CpuHurt = false,
    bool PlayerShieldHit = false,
    bool CpuShieldHit = false,
    RenderBuilding[]? Buildings = null,
    int ShotsFired = 0);

public sealed record RenderWorld(int Width, int Height);

public sealed record RenderWeather(string Type, float Intensity);

public sealed record RenderPreviewTrail(RenderPoint[] Path, RenderPoint[] Cone);

public sealed record RenderPoint(float X, float Y)
{
    public static RenderPoint FromVector(Vector2 point) => new(point.X, point.Y);
}

public sealed record RenderRadiationZone(
    float X,
    float Y,
    float Radius,
    int Turns,
    string VisualKind,
    bool Lava);

public sealed record RenderBuilding(
    string Id,
    float X,
    float Y,
    float Width,
    float Height,
    float Health,
    float MaxHealth,
    float DamageFraction,
    bool Collapsed,
    int LastDamagedShot,
    int PenaltyValue,
    string Kind,
    float TiltDegrees = 0,
    float SupportFraction = 1);

public sealed record RenderTank(
    string Id,
    float X,
    float Y,
    float Angle,
    int Health,
    float Shield,
    bool IsCpu,
    float TerrainY,
    float BuriedDepth,
    float HullAngle = 0,
    float VerticalOffset = 0,
    float LeftTreadY = 0,
    float RightTreadY = 0,
    float SuspensionCompression = 0,
    float RecoilX = 0,
    float RecoilY = 0,
    float RockAngle = 0,
    float ShadowSquash = 1);

public sealed record RenderFrame(RenderWorld World, IReadOnlyList<RenderCommand> Commands);

public sealed record RenderCommand
{
    public string Op { get; init; } = string.Empty;
    public float X { get; init; }
    public float Y { get; init; }
    public float X2 { get; init; }
    public float Y2 { get; init; }
    public float W { get; init; }
    public float H { get; init; }
    public float R { get; init; }
    public float LineWidth { get; init; } = 1;
    public float Alpha { get; init; } = 1;
    public string? Fill { get; init; }
    public string? Stroke { get; init; }
    public string? Text { get; init; }
    public float[]? Points { get; init; }
}

public sealed record WasmRenderDiagnostics(
    int CommandCount,
    int PayloadBytes,
    double SceneBuildMs,
    double CommandBuildMs,
    bool SimdHardwareAccelerated);

public sealed record WasmExplosion(
    float X,
    float Y,
    float Radius,
    float TerrainRadius,
    bool Nuclear,
    bool Dirt,
    string VisualKind,
    int TriggerIndex);
