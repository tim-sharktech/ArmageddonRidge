using ArmageddonRidge.Client.Services.Rendering;
using ArmageddonRidge.Core;

namespace ArmageddonRidge.Tests;

public sealed class WasmRenderCommandBuilderTests
{
    [Theory]
    [InlineData("ShieldHit", "rgba(117,213,255", "ellipse")]
    [InlineData("PatriotIntercept", "rgba(125,220,255", "line")]
    [InlineData("PenetratorSecondary", "rgba(214,196,170", "line")]
    [InlineData("Lava", "rgba(255,95,36", "circle")]
    [InlineData("Laser", "rgba(123,243,255", "circle")]
    [InlineData("Dirt", "rgba(145,108,71", "circle")]
    public void FullWasmFallbackUsesDistinctExplosionVisuals(string visualKind, string expectedFillPrefix, string expectedSecondaryOp)
    {
        var builder = new WasmRenderCommandBuilder();
        var frame = builder.BuildFrame(
            EmptyScene(),
            [new RenderPoint(90, 90), new RenderPoint(100, 100)],
            [new WasmExplosion(100, 100, 42, 35, false, visualKind == "Dirt", visualKind, -1)],
            progress: 0.72f);

        Assert.Contains(frame.Commands, command =>
            command.Op == "circle"
            && command.X == 100
            && command.Y == 100
            && command.Fill?.StartsWith(expectedFillPrefix, StringComparison.Ordinal) == true);

        Assert.Contains(frame.Commands, command => DetailCommandTouchesImpact(command, expectedSecondaryOp));
    }

    [Fact]
    public void FullWasmFallbackKeepsNuclearColumn()
    {
        var builder = new WasmRenderCommandBuilder();
        var frame = builder.BuildFrame(
            EmptyScene(),
            [new RenderPoint(90, 90), new RenderPoint(100, 100)],
            [new WasmExplosion(100, 100, 80, 100, true, false, "Nuclear", -1)],
            progress: 0.72f);

        Assert.Contains(frame.Commands, command =>
            command.Op == "circle"
            && command.X == 100
            && command.Y < 100
            && command.Stroke?.Contains("236,106,92", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void FullWasmFallbackHidesTriggeredExplosionUntilTrailReachesTriggerIndex()
    {
        var builder = new WasmRenderCommandBuilder();
        var trail = new[]
        {
            new RenderPoint(10, 10),
            new RenderPoint(20, 20),
            new RenderPoint(30, 30),
            new RenderPoint(40, 40),
            new RenderPoint(50, 50)
        };

        var frame = builder.BuildFrame(
            EmptyScene(),
            trail,
            [new WasmExplosion(40, 40, 42, 35, false, false, "PenetratorPrimary", 3)],
            progress: 0.5f);

        Assert.DoesNotContain(frame.Commands, command =>
            command.X == 40
            && command.Y == 40
            && command.Fill?.Contains("214,196,170", StringComparison.Ordinal) == true);

        var laterFrame = builder.BuildFrame(
            EmptyScene(),
            trail,
            [new WasmExplosion(40, 40, 42, 35, false, false, "PenetratorPrimary", 3)],
            progress: 0.8f);

        Assert.Contains(laterFrame.Commands, command =>
            command.X == 40
            && command.Y == 40
            && command.Fill?.Contains("214,196,170", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void FullWasmFallbackHidesFinalExplosionUntilTrailCompletes()
    {
        var builder = new WasmRenderCommandBuilder();
        var trail = new[]
        {
            new RenderPoint(10, 10),
            new RenderPoint(20, 20),
            new RenderPoint(30, 30),
            new RenderPoint(40, 40),
            new RenderPoint(50, 50)
        };

        var earlyFrame = builder.BuildFrame(
            EmptyScene(),
            trail,
            [
                new WasmExplosion(40, 40, 42, 35, false, false, "PenetratorPrimary", 3),
                new WasmExplosion(80, 80, 58, 80, false, false, "PenetratorSecondary", -1)
            ],
            progress: 0.5f);

        Assert.DoesNotContain(earlyFrame.Commands, command =>
            command.X == 40
            && command.Y == 40
            && command.Fill?.Contains("214,196,170", StringComparison.Ordinal) == true);
        Assert.DoesNotContain(earlyFrame.Commands, command =>
            command.X == 80
            && command.Y == 80
            && command.Fill?.Contains("214,196,170", StringComparison.Ordinal) == true);

        var finalFrame = builder.BuildFrame(
            EmptyScene(),
            trail,
            [
                new WasmExplosion(40, 40, 42, 35, false, false, "PenetratorPrimary", 3),
                new WasmExplosion(80, 80, 58, 80, false, false, "PenetratorSecondary", -1)
            ],
            progress: 1f);

        Assert.Contains(finalFrame.Commands, command =>
            command.X == 80
            && command.Y == 80
            && command.Fill?.Contains("214,196,170", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void FullWasmFallbackDoesNotEmitNonFiniteCommands()
    {
        var builder = new WasmRenderCommandBuilder();
        var terrain = Enumerable.Repeat(620f, GameConstants.WorldWidth).ToArray();
        terrain[16] = float.NaN;
        terrain[32] = float.PositiveInfinity;
        var scene = EmptyScene() with
        {
            Terrain = terrain,
            Radiation =
            [
                new RenderRadiationZone(float.NaN, 100, 30, 2, "Lava", true),
                new RenderRadiationZone(120, 120, 24, 2, "Lava", true)
            ],
            Player = new RenderTank("player", float.NaN, 620, 45, 75, 0, false, 620, 0),
            Cpu = new RenderTank("cpu", 1040, 620, 135, 75, 0, true, 620, 0),
            PreviewTrail = new RenderPreviewTrail(
                [new RenderPoint(float.NaN, 90), new RenderPoint(90, 100), new RenderPoint(110, 105)],
                [new RenderPoint(95, 95), new RenderPoint(float.PositiveInfinity, 105), new RenderPoint(130, 112)]),
            TracerTrails =
            [
                [new RenderPoint(float.NaN, 80), new RenderPoint(70, 85), new RenderPoint(90, 95)]
            ]
        };

        var frame = builder.BuildFrame(
            scene,
            [new RenderPoint(float.NaN, 90), new RenderPoint(100, 100), new RenderPoint(110, 110)],
            [
                new WasmExplosion(float.NaN, 100, 42, 35, false, false, "Laser", -1),
                new WasmExplosion(120, 120, 42, 35, false, false, "Laser", -1)
            ],
            progress: 1);

        foreach (var command in frame.Commands)
        {
            AssertCommandIsFinite(command);
        }

        Assert.DoesNotContain(frame.Commands, command => command.X == 0 && command.Y == 0 && command.Fill == "#50c5b7");
        Assert.Contains(frame.Commands, command => command.X == 120 && command.Y == 120 && command.Fill?.Contains("123,243,255", StringComparison.Ordinal) == true);
    }

    [Fact]
    public void FullWasmFallbackSkipsDestroyedTanks()
    {
        var builder = new WasmRenderCommandBuilder();
        var scene = EmptyScene() with
        {
            Cpu = new RenderTank("cpu", 1040, 620, 135, 0, 0, true, 620, 0)
        };

        var frame = builder.BuildFrame(scene);

        Assert.DoesNotContain(frame.Commands, command =>
            command.Op == "rect"
            && command.X == 1040 - (GameConstants.TankCollisionWidth / 2f)
            && command.Fill == "#ec6a5c");
        Assert.Contains(frame.Commands, command =>
            command.Op == "rect"
            && command.X == 160 - (GameConstants.TankCollisionWidth / 2f)
            && command.Fill == "#50c5b7");
    }

    private static RenderScene EmptyScene()
    {
        var terrain = Enumerable.Repeat(620f, GameConstants.WorldWidth).ToArray();
        return new RenderScene(
            new RenderWorld(GameConstants.WorldWidth, GameConstants.WorldHeight),
            terrain,
            1,
            new RenderWeather("clear", 0),
            0,
            "Battle",
            "pea-shell",
            new RenderPreviewTrail([], []),
            [],
            [],
            new RenderTank("player", 160, 620, 45, 75, 0, false, 620, 0),
            new RenderTank("cpu", 1040, 620, 135, 75, 0, true, 620, 0));
    }

    private static bool DetailCommandTouchesImpact(RenderCommand command, string expectedOp)
    {
        if (command.Op != expectedOp) return false;

        if (command.Op == "line")
        {
            return command.X <= 100
                && command.X2 >= 100
                && Math.Abs(command.Y - 100) <= 24;
        }

        return command.X == 100 && Math.Abs(command.Y - 100) <= 24;
    }

    private static void AssertCommandIsFinite(RenderCommand command)
    {
        AssertFinite(command.X);
        AssertFinite(command.Y);
        AssertFinite(command.X2);
        AssertFinite(command.Y2);
        AssertFinite(command.W);
        AssertFinite(command.H);
        AssertFinite(command.R);
        AssertFinite(command.LineWidth);
        AssertFinite(command.Alpha);

        if (command.Points is null) return;
        foreach (var point in command.Points)
        {
            AssertFinite(point);
        }
    }

    private static void AssertFinite(float value) =>
        Assert.True(float.IsFinite(value), $"Expected finite render command value, got {value}.");
}
