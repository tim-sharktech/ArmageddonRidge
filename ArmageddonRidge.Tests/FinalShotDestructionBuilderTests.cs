using System.Numerics;
using ArmageddonRidge.Client.Services.Rendering;
using ArmageddonRidge.Core;
using ArmageddonRidge.Core.Models;

namespace ArmageddonRidge.Tests;

public sealed class FinalShotDestructionBuilderTests
{
    [Fact]
    public void BuildReturnsNullWhenRoundContinues()
    {
        var payload = FinalShotDestructionBuilder.Build(Scene(), Resolution(roundEnded: false, TurnOwner.Player), wind: 0, reducedMotion: false);

        Assert.Null(payload);
    }

    [Fact]
    public void BuildSelectsLosingTankAsVictim()
    {
        var payload = FinalShotDestructionBuilder.Build(Scene(), Resolution(roundEnded: true, TurnOwner.Player), wind: 0, reducedMotion: false);

        Assert.NotNull(payload);
        Assert.All(payload!.Pieces, piece => Assert.Equal("cpu", piece.VictimId));
        Assert.InRange(payload.Pieces.Length, 16, 24);
    }

    [Fact]
    public void BuildIsDeterministicForSameInputs()
    {
        var scene = Scene();
        var resolution = Resolution(roundEnded: true, TurnOwner.Cpu);

        var first = FinalShotDestructionBuilder.Build(scene, resolution, wind: -12, reducedMotion: false);
        var second = FinalShotDestructionBuilder.Build(scene, resolution, wind: -12, reducedMotion: false);

        AssertPayloadEquivalent(first, second);
    }

    [Fact]
    public void BuildProducesFiniteBoundedPiecesOnEdgeTerrain()
    {
        var scene = Scene(playerX: 1, cpuX: GameConstants.WorldWidth - 1, terrain: [float.NaN, 620, 615]);
        var payload = RenderPayloadSanitizer.SanitizeFinalShotDestruction(
            FinalShotDestructionBuilder.Build(scene, Resolution(roundEnded: true, TurnOwner.Cpu), wind: 60, reducedMotion: false));

        Assert.NotNull(payload);
        Assert.All(payload!.Pieces, piece =>
        {
            AssertFinite(piece.X, piece.Y, piece.Vx, piece.Vy, piece.Size, piece.Mass, piece.Restitution, piece.Friction, piece.Drag, piece.Spin, piece.Lifetime);
            Assert.InRange(piece.Size, 3, 48);
            Assert.InRange(piece.Restitution, 0, 0.9f);
            Assert.InRange(piece.Friction, 0, 0.95f);
        });
    }

    [Fact]
    public void SimdAndScalarPathsMatch()
    {
        var scene = Scene();
        var resolution = Resolution(roundEnded: true, TurnOwner.Player);

        var simd = FinalShotDestructionBuilder.Build(scene, resolution, wind: 18, reducedMotion: false, forceScalar: false);
        var scalar = FinalShotDestructionBuilder.Build(scene, resolution, wind: 18, reducedMotion: false, forceScalar: true);

        AssertPayloadEquivalent(scalar, simd);
    }

    [Fact]
    public void BuildSupportsMutualDestructionWhenPostShotFlagsAreSet()
    {
        var payload = FinalShotDestructionBuilder.Build(
            Scene(),
            Resolution(roundEnded: true, TurnOwner.Player),
            wind: 0,
            reducedMotion: false,
            playerDestroyed: true,
            cpuDestroyed: true);

        Assert.NotNull(payload);
        Assert.True(payload!.Mutual);
        Assert.Contains(payload.Pieces, piece => piece.VictimId == "player");
        Assert.Contains(payload.Pieces, piece => piece.VictimId == "cpu");
    }

    private static RenderScene Scene(float playerX = 150, float cpuX = 980, float[]? terrain = null)
    {
        terrain ??= Enumerable.Range(0, GameConstants.WorldWidth)
            .Select(i => 610f - MathF.Sin(i / 70f) * 24f)
            .ToArray();

        return new RenderScene(
            new RenderWorld(GameConstants.WorldWidth, GameConstants.WorldHeight),
            terrain,
            1,
            new RenderWeather("clear", 0),
            0,
            GamePhase.Battle.ToString(),
            WeaponIds.PeaShell,
            new RenderPreviewTrail([], []),
            [],
            [],
            new RenderTank("player", playerX, terrain[Math.Clamp((int)playerX, 0, terrain.Length - 1)], 45, 75, 0, false, 0, 0),
            new RenderTank("cpu", cpuX, terrain[Math.Clamp((int)cpuX, 0, terrain.Length - 1)], 135, 75, 0, true, 0, 0));
    }

    private static ShotResolution Resolution(bool roundEnded, TurnOwner winner) =>
        new(
            WeaponIds.BabyMissile,
            "player",
            [new Vector2(100, 300), new Vector2(500, 420), new Vector2(940, 585)],
            [
                new ExplosionResult(new Vector2(940, 585), 82, 100, 0, 92, false, false, [], ShotVisualKind.Missile),
                new ExplosionResult(new Vector2(978, 590), 52, 70, 0, 38, false, false, [], ShotVisualKind.Ballistic)
            ],
            [],
            roundEnded,
            roundEnded ? winner : null,
            new PerformanceSample(1, 1, 0, 3, 60),
            ShotVisualKind.Missile);

    private static void AssertFinite(params float[] values)
    {
        for (var i = 0; i < values.Length; i++)
        {
            Assert.True(float.IsFinite(values[i]), $"Expected finite value at index {i}, got {values[i]}.");
        }
    }

    private static void AssertPayloadEquivalent(FinalShotDestructionPayload? expected, FinalShotDestructionPayload? actual)
    {
        Assert.NotNull(expected);
        Assert.NotNull(actual);
        Assert.Equal(expected!.Active, actual!.Active);
        Assert.Equal(expected.X, actual.X);
        Assert.Equal(expected.Y, actual.Y);
        Assert.Equal(expected.Radius, actual.Radius);
        Assert.Equal(expected.Mutual, actual.Mutual);
        Assert.Equal(expected.ReducedMotion, actual.ReducedMotion);
        Assert.Equal(expected.Pieces.Length, actual.Pieces.Length);
        for (var i = 0; i < expected.Pieces.Length; i++)
        {
            AssertPieceEquivalent(expected.Pieces[i], actual.Pieces[i]);
        }
    }

    private static void AssertPieceEquivalent(FinalShotDebrisPiece expected, FinalShotDebrisPiece actual)
    {
        Assert.Equal(expected.VictimId, actual.VictimId);
        Assert.Equal(expected.Sprite, actual.Sprite);
        Assert.Equal(expected.Seed, actual.Seed);
        AssertClose(expected.X, actual.X);
        AssertClose(expected.Y, actual.Y);
        AssertClose(expected.Vx, actual.Vx);
        AssertClose(expected.Vy, actual.Vy);
        AssertClose(expected.Size, actual.Size);
        AssertClose(expected.Mass, actual.Mass);
        AssertClose(expected.Restitution, actual.Restitution);
        AssertClose(expected.Friction, actual.Friction);
        AssertClose(expected.Drag, actual.Drag);
        AssertClose(expected.Spin, actual.Spin);
        AssertClose(expected.Lifetime, actual.Lifetime);
        AssertClose(expected.R, actual.R);
        AssertClose(expected.G, actual.G);
        AssertClose(expected.B, actual.B);
    }

    private static void AssertClose(float expected, float actual) =>
        Assert.True(MathF.Abs(expected - actual) <= 0.0001f, $"Expected {expected}, got {actual}.");
}
