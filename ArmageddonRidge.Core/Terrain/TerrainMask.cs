using System.Numerics;

namespace ArmageddonRidge.Core.Terrain;

/// <summary>
/// Heightmap-backed terrain mask used for collision, deformation, and rendering snapshots.
/// </summary>
public sealed class TerrainMask
{
    private static readonly Vector<float> LaneOffsets = CreateLaneOffsets();
    private readonly float[] _solidTop;

    /// <summary>
    /// Creates a terrain mask from one surface height per world column.
    /// </summary>
    public TerrainMask(int width, int height, IReadOnlyList<float> solidTop)
    {
        Width = width;
        Height = height;
        if (solidTop.Count != width)
            throw new ArgumentException("Terrain heightmap width mismatch.", nameof(solidTop));

        _solidTop = solidTop.ToArray();
    }

    /// <summary>
    /// Gets the world width in terrain columns.
    /// </summary>
    public int Width { get; }

    /// <summary>
    /// Gets the world height used for bounds checks.
    /// </summary>
    public int Height { get; }

    /// <summary>
    /// Gets the topmost solid Y value for each terrain column.
    /// </summary>
    public IReadOnlyList<float> SolidTop => _solidTop;

    /// <summary>
    /// Reports whether the current runtime can accelerate terrain deformation with SIMD vectors.
    /// </summary>
    public static bool SimdAccelerated => Vector.IsHardwareAccelerated && Vector<float>.Count > 1;

    /// <summary>
    /// Gets the number of terrain columns processed per SIMD vector.
    /// </summary>
    public static int SimdLaneCount => Vector<float>.Count;

    /// <summary>
    /// Copies terrain heights from another mask with matching dimensions.
    /// </summary>
    public void CopyFrom(TerrainMask source)
    {
        if (source.Width != Width || source.Height != Height)
            throw new InvalidOperationException("Terrain dimensions must match.");

        for (var i = 0; i < _solidTop.Length; i++)
        {
            _solidTop[i] = source._solidTop[i];
        }
    }

    /// <summary>
    /// Gets the solid surface Y coordinate for a world X coordinate.
    /// </summary>
    public float GetSurfaceY(float x)
    {
        var ix = Math.Clamp((int)MathF.Round(x), 0, Width - 1);
        return _solidTop[ix];
    }

    /// <summary>
    /// Finds the nearest non-empty surface column to a preferred X coordinate.
    /// </summary>
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

    /// <summary>
    /// Returns whether a world point is inside solid terrain.
    /// </summary>
    public bool IsSolid(Vector2 point) => IsSolid(point.X, point.Y);

    /// <summary>
    /// Returns whether a world coordinate is inside solid terrain.
    /// </summary>
    public bool IsSolid(float x, float y)
    {
        if (x < 0 || x >= Width || y < 0 || y >= Height)
            return false;

        return y >= _solidTop[(int)x];
    }

    private bool IsVisibleSurfaceColumn(int x) => x >= 0 && x < Width && _solidTop[x] < Height;

    /// <summary>
    /// Removes a circular crater from the heightmap and returns touched column count.
    /// </summary>
    public int RemoveCircle(Vector2 center, float radius) => RemoveCircle(center, radius, TerrainDeformationMode.Auto);

    /// <summary>
    /// Removes a circular crater with an explicit deformation kernel, used by benchmarks and diagnostics.
    /// </summary>
    public int RemoveCircle(Vector2 center, float radius, TerrainDeformationMode mode) =>
        mode switch
        {
            TerrainDeformationMode.Scalar => ApplyCircleScalar(center, radius, removeTerrain: true),
            TerrainDeformationMode.Simd => ApplyCircleSimd(center, radius, removeTerrain: true),
            _ => SimdAccelerated
                ? ApplyCircleSimd(center, radius, removeTerrain: true)
                : ApplyCircleScalar(center, radius, removeTerrain: true)
        };

    /// <summary>
    /// Adds a circular dirt mound to the heightmap and returns touched column count.
    /// </summary>
    public int AddCircle(Vector2 center, float radius) => AddCircle(center, radius, TerrainDeformationMode.Auto);

    /// <summary>
    /// Adds a circular dirt mound with an explicit deformation kernel, used by benchmarks and diagnostics.
    /// </summary>
    public int AddCircle(Vector2 center, float radius, TerrainDeformationMode mode) =>
        mode switch
        {
            TerrainDeformationMode.Scalar => ApplyCircleScalar(center, radius, removeTerrain: false),
            TerrainDeformationMode.Simd => ApplyCircleSimd(center, radius, removeTerrain: false),
            _ => SimdAccelerated
                ? ApplyCircleSimd(center, radius, removeTerrain: false)
                : ApplyCircleScalar(center, radius, removeTerrain: false)
        };

    private int ApplyCircleScalar(Vector2 center, float radius, bool removeTerrain)
    {
        var touched = 0;
        var minX = Math.Max(0, (int)MathF.Floor(center.X - radius));
        var maxX = Math.Min(Width - 1, (int)MathF.Ceiling(center.X + radius));

        for (var x = minX; x <= maxX; x++)
        {
            var dx = x - center.X;
            var remaining = (radius * radius) - (dx * dx);
            if (remaining <= 0)
                continue;

            var arc = center.Y + (MathF.Sqrt(remaining) * (removeTerrain ? 1f : -1f));
            var nextTop = Math.Clamp(arc, 0, Height);
            if ((removeTerrain && nextTop > _solidTop[x]) || (!removeTerrain && nextTop < _solidTop[x]))
            {
                _solidTop[x] = nextTop;
                touched++;
            }
        }

        return touched;
    }

    private int ApplyCircleSimd(Vector2 center, float radius, bool removeTerrain)
    {
        var touched = 0;
        var minX = Math.Max(0, (int)MathF.Floor(center.X - radius));
        var maxX = Math.Min(Width - 1, (int)MathF.Ceiling(center.X + radius));
        var width = maxX - minX + 1;
        var lanes = Vector<float>.Count;
        if (width < lanes * 2)
            return ApplyCircleScalar(center, radius, removeTerrain);

        var radiusSquared = new Vector<float>(radius * radius);
        var centerX = new Vector<float>(center.X);
        var centerY = new Vector<float>(center.Y);
        var zero = Vector<float>.Zero;
        var height = new Vector<float>(Height);
        var direction = new Vector<float>(removeTerrain ? 1f : -1f);
        var vectorEnd = minX + ((width / lanes) * lanes);
        for (var x = minX; x < vectorEnd; x += lanes)
        {
            var xs = LaneOffsets + new Vector<float>(x);
            var dx = xs - centerX;
            var remaining = radiusSquared - (dx * dx);
            var inCircle = Vector.GreaterThan(remaining, zero);
            var arc = centerY + (Vector.SquareRoot(Vector.Max(remaining, zero)) * direction);
            var nextTop = Vector.Min(Vector.Max(arc, zero), height);
            var current = new Vector<float>(_solidTop, x);
            var changed = removeTerrain
                ? Vector.GreaterThan(nextTop, current)
                : Vector.LessThan(nextTop, current);
            var changedInCircle = Vector.BitwiseAnd(inCircle, changed);
            var updated = Vector.ConditionalSelect(changedInCircle, nextTop, current);
            for (var lane = 0; lane < lanes; lane++)
            {
                if (changedInCircle[lane] != 0)
                    touched++;
            }

            updated.CopyTo(_solidTop, x);
        }

        for (var x = vectorEnd; x <= maxX; x++)
        {
            var dx = x - center.X;
            var remaining = (radius * radius) - (dx * dx);
            if (remaining <= 0)
                continue;

            var arc = center.Y + (MathF.Sqrt(remaining) * (removeTerrain ? 1f : -1f));
            var nextTop = Math.Clamp(arc, 0, Height);
            if ((removeTerrain && nextTop > _solidTop[x]) || (!removeTerrain && nextTop < _solidTop[x]))
            {
                _solidTop[x] = nextTop;
                touched++;
            }
        }

        return touched;
    }

    private static Vector<float> CreateLaneOffsets()
    {
        var lanes = new float[Vector<float>.Count];
        for (var i = 0; i < lanes.Length; i++) lanes[i] = i;
        return new Vector<float>(lanes);
    }
}

/// <summary>
/// Selects the terrain deformation implementation used for crater and dirt benchmark scenarios.
/// </summary>
public enum TerrainDeformationMode
{
    /// <summary>
    /// Uses SIMD when the runtime supports it and scalar code otherwise.
    /// </summary>
    Auto,

    /// <summary>
    /// Uses the one-column-at-a-time reference implementation.
    /// </summary>
    Scalar,

    /// <summary>
    /// Uses the vectorized implementation that updates multiple terrain columns per operation.
    /// </summary>
    Simd
}
