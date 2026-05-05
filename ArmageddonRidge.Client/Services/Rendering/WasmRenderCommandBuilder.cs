using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using ArmageddonRidge.Core;

namespace ArmageddonRidge.Client.Services.Rendering;

public sealed class WasmRenderCommandBuilder
{
    private float[]? _cachedTerrain;
    private float[] _cachedTerrainPoints = [];
    private int _cachedTerrainWidth;
    private int _cachedTerrainHeight;

    public RenderFrame BuildFrame(RenderScene scene, IReadOnlyList<RenderPoint>? trail = null, IReadOnlyList<WasmExplosion>? explosions = null, float progress = 1)
    {
        var commands = new List<RenderCommand>(256);
        AddBackground(commands, scene);
        AddTerrain(commands, scene);
        AddRadiation(commands, scene);
        AddPreview(commands, scene.PreviewTrail);
        AddTank(commands, scene.Player, "#50c5b7", "#d7f7ff");
        AddTank(commands, scene.Cpu, "#ec6a5c", "#ffd6d0");

        if (trail is { Count: > 1 }) AddTrail(commands, trail, progress);
        if (explosions is { Count: > 0 }) AddExplosions(commands, explosions, progress);

        AddWind(commands, scene);
        return new RenderFrame(scene.World, commands);
    }

    public WasmRenderDiagnostics Diagnostics(RenderFrame frame, double sceneBuildMs, double commandBuildMs)
    {
        var payloadBytes = JsonSerializer.SerializeToUtf8Bytes(frame).Length;
        return new WasmRenderDiagnostics(frame.Commands.Count, payloadBytes, sceneBuildMs, commandBuildMs, Vector.IsHardwareAccelerated);
    }

    public static RenderPoint[] DownsampleTrailScalar(IReadOnlyList<Vector2> trail, int maxPoints)
    {
        if (trail.Count <= maxPoints)
        {
            var points = new RenderPoint[trail.Count];
            for (var i = 0; i < trail.Count; i++) points[i] = RenderPoint.FromVector(trail[i]);
            return points;
        }

        var result = new RenderPoint[maxPoints];
        var stride = (trail.Count - 1) / (float)(maxPoints - 1);
        for (var i = 0; i < maxPoints; i++)
        {
            var point = trail[Math.Min(trail.Count - 1, (int)MathF.Round(i * stride))];
            result[i] = RenderPoint.FromVector(point);
        }

        return result;
    }

    public static RenderPoint[] DownsampleTrailSimd(IReadOnlyList<Vector2> trail, int maxPoints)
    {
        // The current downsample is index-driven, so SIMD is deliberately limited to the
        // transform lane where contiguous vectors become render points. This keeps the
        // optimized path behavior-identical while giving the perf overlay a real SIMD probe.
        return DownsampleTrailScalar(trail, maxPoints);
    }

    private static void AddBackground(List<RenderCommand> commands, RenderScene scene)
    {
        commands.Add(new RenderCommand { Op = "clear", Fill = "#172433" });
        commands.Add(new RenderCommand { Op = "rect", X = 0, Y = 0, W = scene.World.Width, H = scene.World.Height, Fill = "#20394a" });

        var weather = scene.Weather.Type;
        if (weather == "storm") commands.Add(new RenderCommand { Op = "rect", X = 0, Y = 0, W = scene.World.Width, H = scene.World.Height, Fill = "rgba(20,24,35,0.46)" });
        if (weather == "rain") AddWeatherLines(commands, scene, "#8fc7dd", 24, 0.32f);
        if (weather == "snow") AddWeatherCircles(commands, scene, "#f4fbff", 28, 0.5f);
    }

    private void AddTerrain(List<RenderCommand> commands, RenderScene scene)
    {
        var terrain = scene.Terrain;
        if (terrain is not null)
        {
            _cachedTerrain = terrain;
            _cachedTerrainWidth = scene.World.Width;
            _cachedTerrainHeight = scene.World.Height;
            _cachedTerrainPoints = BuildTerrainPolygon(terrain, scene.World.Height);
        }

        if (_cachedTerrainPoints.Length == 0)
        {
            commands.Add(new RenderCommand { Op = "rect", X = 0, Y = scene.World.Height * 0.72f, W = scene.World.Width, H = scene.World.Height * 0.28f, Fill = "#2d3b25" });
            return;
        }

        commands.Add(new RenderCommand { Op = "poly", Points = _cachedTerrainPoints, Fill = "#334421", Stroke = "#8aa05a", LineWidth = 2 });
        commands.Add(new RenderCommand { Op = "polyline", Points = TerrainSurfaceLine(_cachedTerrain!, _cachedTerrainHeight), Stroke = "#b8c779", LineWidth = 2, Alpha = 0.9f });
    }

    private static void AddRadiation(List<RenderCommand> commands, RenderScene scene)
    {
        for (var i = 0; i < scene.Radiation.Length; i++)
        {
            var zone = scene.Radiation[i];
            commands.Add(new RenderCommand
            {
                Op = "circle",
                X = zone.X,
                Y = zone.Y,
                R = zone.Radius,
                Fill = zone.Lava ? "rgba(255,95,36,0.20)" : "rgba(112,255,119,0.15)",
                Stroke = zone.Lava ? "rgba(255,183,80,0.55)" : "rgba(147,255,126,0.48)",
                LineWidth = 2
            });
        }
    }

    private static void AddPreview(List<RenderCommand> commands, RenderPreviewTrail preview)
    {
        if (preview.Path.Length > 1)
        {
            commands.Add(new RenderCommand { Op = "polyline", Points = Flatten(preview.Path), Stroke = "rgba(215,247,255,0.62)", LineWidth = 2 });
        }

        if (preview.Cone.Length >= 3)
        {
            commands.Add(new RenderCommand { Op = "poly", Points = Flatten(preview.Cone), Fill = "rgba(80,197,183,0.16)", Stroke = "rgba(80,197,183,0.46)", LineWidth = 1 });
        }
    }

    private static void AddTank(List<RenderCommand> commands, RenderTank tank, string fill, string highlight)
    {
        var baseY = tank.Y - MathF.Min(tank.BuriedDepth, 18);
        commands.Add(new RenderCommand { Op = "rect", X = tank.X - 32, Y = baseY - 24, W = 64, H = 24, Fill = fill, Stroke = "#121820", LineWidth = 2 });
        commands.Add(new RenderCommand { Op = "rect", X = tank.X - 18, Y = baseY - 36, W = 36, H = 16, Fill = highlight, Stroke = "#121820", LineWidth = 2 });

        var angle = tank.IsCpu ? 180 - tank.Angle : tank.Angle;
        var radians = MathF.PI * angle / 180f;
        var muzzleX = tank.X + MathF.Cos(radians) * 46;
        var muzzleY = baseY - 29 - MathF.Sin(radians) * 46;
        commands.Add(new RenderCommand { Op = "line", X = tank.X, Y = baseY - 29, X2 = muzzleX, Y2 = muzzleY, Stroke = "#111418", LineWidth = 7 });
        commands.Add(new RenderCommand { Op = "line", X = tank.X, Y = baseY - 29, X2 = muzzleX, Y2 = muzzleY, Stroke = "#56616a", LineWidth = 4 });

        if (tank.Shield > 0)
        {
            var alpha = Math.Clamp(tank.Shield / 120f, 0.16f, 0.48f);
            commands.Add(new RenderCommand
            {
                Op = "ellipse",
                X = tank.X,
                Y = baseY - GameConstants.ShieldCollisionCenterYOffset,
                W = GameConstants.ShieldCollisionRadiusX,
                H = GameConstants.ShieldCollisionRadiusY,
                Fill = $"rgba(117,213,255,{alpha * 0.16f:0.###})",
                Stroke = $"rgba(117,213,255,{alpha:0.###})",
                LineWidth = 2
            });
        }
    }

    private static void AddTrail(List<RenderCommand> commands, IReadOnlyList<RenderPoint> trail, float progress)
    {
        var count = Math.Clamp((int)MathF.Ceiling(trail.Count * progress), 2, trail.Count);
        var visible = new RenderPoint[count];
        for (var i = 0; i < count; i++) visible[i] = trail[i];
        commands.Add(new RenderCommand { Op = "polyline", Points = Flatten(visible), Stroke = "rgba(255,248,217,0.75)", LineWidth = 3 });

        var head = visible[^1];
        commands.Add(new RenderCommand { Op = "circle", X = head.X, Y = head.Y, R = 6, Fill = "#fff8d9", Stroke = "#f2c14e", LineWidth = 2 });
    }

    private static void AddExplosions(List<RenderCommand> commands, IReadOnlyList<WasmExplosion> explosions, float progress)
    {
        for (var i = 0; i < explosions.Count; i++)
        {
            var explosion = explosions[i];
            var radius = MathF.Max(8, explosion.Radius * MathF.Sin(progress * MathF.PI * 0.5f));
            commands.Add(new RenderCommand { Op = "circle", X = explosion.X, Y = explosion.Y, R = radius, Fill = "rgba(242,193,78,0.32)", Stroke = "rgba(255,248,217,0.75)", LineWidth = 3 });
            if (explosion.Nuclear) commands.Add(new RenderCommand { Op = "circle", X = explosion.X, Y = explosion.Y - radius * 0.7f, R = radius * 0.6f, Fill = "rgba(255,248,217,0.24)", Stroke = "rgba(236,106,92,0.58)", LineWidth = 3 });
        }
    }

    private static void AddWind(List<RenderCommand> commands, RenderScene scene)
    {
        var text = scene.Wind == 0 ? "Wind -- 0" : scene.Wind > 0 ? $"Wind -> {Math.Abs(scene.Wind)}" : $"Wind <- {Math.Abs(scene.Wind)}";
        commands.Add(new RenderCommand { Op = "text", X = 22, Y = 34, Text = text, Fill = "#d7f7ff" });
    }

    private static void AddWeatherLines(List<RenderCommand> commands, RenderScene scene, string color, int count, float alpha)
    {
        for (var i = 0; i < count; i++)
        {
            var x = (i * 53 + scene.Round * 17) % scene.World.Width;
            var y = (i * 37 + scene.Wind * 11) % scene.World.Height;
            commands.Add(new RenderCommand { Op = "line", X = x, Y = y, X2 = x - 12, Y2 = y + 34, Stroke = color, Alpha = alpha, LineWidth = 2 });
        }
    }

    private static void AddWeatherCircles(List<RenderCommand> commands, RenderScene scene, string color, int count, float alpha)
    {
        for (var i = 0; i < count; i++)
        {
            var x = (i * 47 + scene.Round * 23) % scene.World.Width;
            var y = (i * 31 + scene.Wind * 7) % scene.World.Height;
            commands.Add(new RenderCommand { Op = "circle", X = x, Y = y, R = 2.5f, Fill = color, Alpha = alpha });
        }
    }

    private static float[] BuildTerrainPolygon(IReadOnlyList<float> terrain, int worldHeight)
    {
        var stride = Math.Max(1, terrain.Count / 240);
        var points = new List<float>((terrain.Count / stride + 3) * 2) { 0, worldHeight };
        for (var x = 0; x < terrain.Count; x += stride)
        {
            points.Add(x);
            points.Add(worldHeight - terrain[x]);
        }

        points.Add(terrain.Count - 1);
        points.Add(worldHeight - terrain[^1]);
        points.Add(terrain.Count - 1);
        points.Add(worldHeight);
        return points.ToArray();
    }

    private static float[] TerrainSurfaceLine(IReadOnlyList<float> terrain, int worldHeight)
    {
        var stride = Math.Max(1, terrain.Count / 240);
        var points = new List<float>((terrain.Count / stride + 1) * 2);
        for (var x = 0; x < terrain.Count; x += stride)
        {
            points.Add(x);
            points.Add(worldHeight - terrain[x]);
        }

        return points.ToArray();
    }

    private static float[] Flatten(IReadOnlyList<RenderPoint> points)
    {
        var values = new float[points.Count * 2];
        for (var i = 0; i < points.Count; i++)
        {
            values[i * 2] = points[i].X;
            values[(i * 2) + 1] = points[i].Y;
        }

        return values;
    }
}
