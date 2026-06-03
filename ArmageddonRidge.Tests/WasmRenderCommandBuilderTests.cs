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
}
