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
