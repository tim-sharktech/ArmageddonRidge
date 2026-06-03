using System.Numerics;
using ArmageddonRidge.Core.Terrain;

namespace ArmageddonRidge.Tests;

public sealed class TerrainTests
{
    [Fact]
    public void TerrainGenerationIsSeeded()
    {
        var generator = new TerrainGenerator();
        var first = generator.Generate(555);
        var second = generator.Generate(555);

        Assert.Equal(first.SolidTop, second.SolidTop);
    }

    [Fact]
    public void CraterLowersSurfaceAndDirtRaisesSurface()
    {
        var terrain = new TerrainGenerator().Generate(777);
        var x = 500;
        var before = terrain.GetSurfaceY(x);

        terrain.RemoveCircle(new Vector2(x, before - 8), 54);
        var crater = terrain.GetSurfaceY(x);
        terrain.AddCircle(new Vector2(x, crater - 10), 42);
        var dirt = terrain.GetSurfaceY(x);

        Assert.True(crater > before);
        Assert.True(dirt < crater);
    }

    [Fact]
    public void SimdAndScalarCraterDeformationMatch()
    {
        var heights = Enumerable.Range(0, 1200)
            .Select(static x => 500f + (MathF.Sin(x * 0.025f) * 22f))
            .ToArray();
        var scalar = new TerrainMask(1200, 700, heights);
        var simd = new TerrainMask(1200, 700, heights);

        var scalarTouched = scalar.RemoveCircle(new Vector2(580.5f, 470.25f), 126, TerrainDeformationMode.Scalar);
        var simdTouched = simd.RemoveCircle(new Vector2(580.5f, 470.25f), 126, TerrainDeformationMode.Simd);

        Assert.Equal(scalarTouched, simdTouched);
        AssertTerrainEqual(scalar, simd);
    }

    [Fact]
    public void SimdAndScalarDirtDeformationMatch()
    {
        var heights = Enumerable.Range(0, 1200)
            .Select(static x => 500f + (MathF.Cos(x * 0.022f) * 18f))
            .ToArray();
        var scalar = new TerrainMask(1200, 700, heights);
        var simd = new TerrainMask(1200, 700, heights);

        var scalarTouched = scalar.AddCircle(new Vector2(620.75f, 455.5f), 94, TerrainDeformationMode.Scalar);
        var simdTouched = simd.AddCircle(new Vector2(620.75f, 455.5f), 94, TerrainDeformationMode.Simd);

        Assert.Equal(scalarTouched, simdTouched);
        AssertTerrainEqual(scalar, simd);
    }

    [Theory]
    [InlineData(2.4f, 452.25f, 37.6f, true)]
    [InlineData(1196.3f, 452.25f, 39.2f, true)]
    [InlineData(127.35f, 240.75f, 37.6f, true)]
    [InlineData(611.5f, 690.0f, 80.0f, true)]
    [InlineData(611.5f, -12.0f, 80.0f, false)]
    [InlineData(514.2f, 438.5f, 19.0f, false)]
    public void SimdAndScalarDeformationMatchEdgeAndTailCases(float x, float y, float radius, bool removeTerrain)
    {
        var heights = Enumerable.Range(0, 1200)
            .Select(static column => 470f + (MathF.Sin(column * 0.031f) * 34f) + (MathF.Cos(column * 0.011f) * 12f))
            .ToArray();
        var scalar = new TerrainMask(1200, 700, heights);
        var simd = new TerrainMask(1200, 700, heights);
        var center = new Vector2(x, y);

        var scalarTouched = removeTerrain
            ? scalar.RemoveCircle(center, radius, TerrainDeformationMode.Scalar)
            : scalar.AddCircle(center, radius, TerrainDeformationMode.Scalar);
        var simdTouched = removeTerrain
            ? simd.RemoveCircle(center, radius, TerrainDeformationMode.Simd)
            : simd.AddCircle(center, radius, TerrainDeformationMode.Simd);

        Assert.Equal(scalarTouched, simdTouched);
        AssertTerrainEqual(scalar, simd);
    }

    [Fact]
    public void RepeatedSimdDeformationReportsNoOpTouchedCount()
    {
        var heights = Enumerable.Repeat(500f, 1200).ToArray();
        var terrain = new TerrainMask(1200, 700, heights);
        var center = new Vector2(600, 450);

        var first = terrain.RemoveCircle(center, 96, TerrainDeformationMode.Simd);
        var second = terrain.RemoveCircle(center, 96, TerrainDeformationMode.Simd);

        Assert.True(first > 0);
        Assert.Equal(0, second);
    }

    private static void AssertTerrainEqual(TerrainMask expected, TerrainMask actual)
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        for (var i = 0; i < expected.SolidTop.Count; i++)
        {
            Assert.InRange(actual.SolidTop[i], expected.SolidTop[i] - 0.0001f, expected.SolidTop[i] + 0.0001f);
        }
    }

    [Fact]
    public void NearestVisibleSurfaceSkipsColumnsRemovedToWorldBottom()
    {
        var terrain = new TerrainMask(7, 100, [90, 100, 100, 100, 60, 70, 80]);

        var found = terrain.TryGetNearestVisibleSurface(2, out var surface);

        Assert.True(found);
        Assert.Equal(0, surface.X);
        Assert.Equal(90, surface.Y);
    }

    [Fact]
    public void NearestVisibleSurfaceReportsMissingWhenAllTerrainIsGone()
    {
        var terrain = new TerrainMask(4, 100, [100, 100, 100, 100]);

        var found = terrain.TryGetNearestVisibleSurface(1, out _);

        Assert.False(found);
    }
}
