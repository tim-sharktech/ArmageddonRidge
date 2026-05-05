using System.Numerics;

namespace ArmageddonRidge.Core.Terrain;

public sealed class TerrainMask
{
    private readonly float[] _solidTop;

    public TerrainMask(int width, int height, IReadOnlyList<float> solidTop)
    {
        Width = width;
        Height = height;
        if (solidTop.Count != width)
        {
            throw new ArgumentException("Terrain heightmap width mismatch.", nameof(solidTop));
        }

        _solidTop = solidTop.ToArray();
    }

    public int Width { get; }

    public int Height { get; }

    public IReadOnlyList<float> SolidTop => _solidTop;

    public void CopyFrom(TerrainMask source)
    {
        if (source.Width != Width || source.Height != Height)
        {
            throw new InvalidOperationException("Terrain dimensions must match.");
        }

        for (var i = 0; i < _solidTop.Length; i++)
        {
            _solidTop[i] = source._solidTop[i];
        }
    }

    public float GetSurfaceY(float x)
    {
        var ix = Math.Clamp((int)MathF.Round(x), 0, Width - 1);
        return _solidTop[ix];
    }

    public bool TryGetNearestVisibleSurface(float preferredX, out Vector2 surface)
    {
        var preferredIndex = Math.Clamp((int)MathF.Round(preferredX), 0, Width - 1);
        for (var offset = 0; offset < Width; offset++)
        {
            var left = preferredIndex - offset;
            if (IsVisibleSurfaceColumn(left))
            {
                surface = new Vector2(left, _solidTop[left]);
                return true;
            }

            var right = preferredIndex + offset;
            if (right != left && IsVisibleSurfaceColumn(right))
            {
                surface = new Vector2(right, _solidTop[right]);
                return true;
            }
        }

        surface = default;
        return false;
    }

    public bool IsSolid(Vector2 point) => IsSolid(point.X, point.Y);

    public bool IsSolid(float x, float y)
    {
        if (x < 0 || x >= Width || y < 0 || y >= Height)
        {
            return false;
        }

        return y >= _solidTop[(int)x];
    }

    private bool IsVisibleSurfaceColumn(int x) => x >= 0 && x < Width && _solidTop[x] < Height;

    public int RemoveCircle(Vector2 center, float radius)
    {
        var touched = 0;
        var minX = Math.Max(0, (int)MathF.Floor(center.X - radius));
        var maxX = Math.Min(Width - 1, (int)MathF.Ceiling(center.X + radius));

        for (var x = minX; x <= maxX; x++)
        {
            var dx = x - center.X;
            var remaining = (radius * radius) - (dx * dx);
            if (remaining <= 0)
            {
                continue;
            }

            var lowerArc = center.Y + MathF.Sqrt(remaining);
            var nextTop = Math.Clamp(lowerArc, 0, Height);
            if (nextTop > _solidTop[x])
            {
                _solidTop[x] = nextTop;
                touched++;
            }
        }

        return touched;
    }

    public int AddCircle(Vector2 center, float radius)
    {
        var touched = 0;
        var minX = Math.Max(0, (int)MathF.Floor(center.X - radius));
        var maxX = Math.Min(Width - 1, (int)MathF.Ceiling(center.X + radius));

        for (var x = minX; x <= maxX; x++)
        {
            var dx = x - center.X;
            var remaining = (radius * radius) - (dx * dx);
            if (remaining <= 0)
            {
                continue;
            }

            var upperArc = center.Y - MathF.Sqrt(remaining);
            var nextTop = Math.Clamp(upperArc, 0, Height);
            if (nextTop < _solidTop[x])
            {
                _solidTop[x] = nextTop;
                touched++;
            }
        }

        return touched;
    }
}
