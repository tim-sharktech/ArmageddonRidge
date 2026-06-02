using System.Diagnostics;
using System.Numerics;
using ArmageddonRidge.Core;
using ArmageddonRidge.Core.Game;
using ArmageddonRidge.Core.Models;
using ArmageddonRidge.Core.Terrain;

namespace ArmageddonRidge.Client.Pages;

public partial class Benchmarks
{
    private string _results = "Idle";

    private Task RunProjectileBatchAsync()
    {
        var settings = new MatchSettings(Difficulty: Difficulty.Oracle, TerrainSeed: 424242, EnableShop: false);
        var state = Engine.NewMatch(settings);
        Engine.StartBattle(state);
        var started = DateTimeOffset.UtcNow;
        var totalTrail = 0;

        for (var i = 0; i < 40 && state.Phase == GamePhase.Battle; i++)
        {
            var result = Engine.FireCurrentTurn(state, settings, 38 + (i % 12), 55 + (i % 30));
            totalTrail += result.Trail.Count;
            if (state.Phase == GamePhase.Battle)
            {
                result = Engine.FireCurrentTurn(state, settings);
                totalTrail += result.Trail.Count;
            }
        }

        var elapsed = DateTimeOffset.UtcNow - started;
        _results = $"Elapsed: {elapsed.TotalMilliseconds:0.0} ms\nTrail points: {totalTrail}\nLast sim: {state.LastPerformance.SimulationMs:0.00} ms\nLast terrain: {state.LastPerformance.TerrainMs:0.00} ms\nLast CPU: {state.LastPerformance.CpuPlanningMs:0.00} ms";
        return Task.CompletedTask;
    }

    private Task RunCpuPlanningAsync()
    {
        var seeds = new[] { 424242, 555111, 777333, 90210, 123456 };
        var samples = new List<double>(seeds.Length);
        var totalCandidatesMs = 0d;
        var totalElapsed = Stopwatch.StartNew();

        foreach (var seed in seeds)
        {
            var settings = new MatchSettings(Difficulty: Difficulty.Oracle, TerrainSeed: seed, EnableShop: false);
            var state = Engine.NewMatch(settings);
            Engine.StartBattle(state);
            state.CurrentTurn = TurnOwner.Cpu;
            var started = Stopwatch.StartNew();
            var plan = Engine.Cpu.PlanShot(state, settings);
            started.Stop();
            samples.Add(started.Elapsed.TotalMilliseconds);
            totalCandidatesMs += plan.PlanningMs;
        }

        totalElapsed.Stop();
        samples.Sort();
        _results =
            "CPU planning benchmark\n" +
            $"Seeds: {seeds.Length} Oracle turns\n" +
            $"Total wall: {totalElapsed.Elapsed.TotalMilliseconds:0.0} ms\n" +
            $"Median: {Percentile(samples, 0.50):0.00} ms\n" +
            $"P95: {Percentile(samples, 0.95):0.00} ms\n" +
            $"Max: {samples[^1]:0.00} ms\n" +
            $"Planner reported total: {totalCandidatesMs:0.00} ms";
        return Task.CompletedTask;
    }

    private Task RunTerrainSimdAsync()
    {
        const int edits = 1_000;
        var seedHeights = Enumerable.Range(0, GameConstants.WorldWidth)
            .Select(static x => 486f + (MathF.Sin(x * 0.023f) * 38f) + (MathF.Cos(x * 0.009f) * 18f))
            .ToArray();

        var scalarRemove = RunTerrainBatch(seedHeights, TerrainDeformationMode.Scalar, removeTerrain: true, edits);
        var simdRemove = RunTerrainBatch(seedHeights, TerrainDeformationMode.Simd, removeTerrain: true, edits);
        var scalarAdd = RunTerrainBatch(seedHeights, TerrainDeformationMode.Scalar, removeTerrain: false, edits);
        var simdAdd = RunTerrainBatch(seedHeights, TerrainDeformationMode.Simd, removeTerrain: false, edits);

        _results =
            "Terrain deformation SIMD benchmark\n" +
            $"SIMD accelerated: {TerrainMask.SimdAccelerated} ({TerrainMask.SimdLaneCount} lanes)\n" +
            $"Edits per batch: {edits}\n" +
            $"Remove scalar: {scalarRemove.ElapsedMs:0.00} ms, touched {scalarRemove.Touched}\n" +
            $"Remove SIMD:   {simdRemove.ElapsedMs:0.00} ms, touched {simdRemove.Touched}, speedup {Speedup(scalarRemove.ElapsedMs, simdRemove.ElapsedMs):0.00}x\n" +
            $"Add scalar:    {scalarAdd.ElapsedMs:0.00} ms, touched {scalarAdd.Touched}\n" +
            $"Add SIMD:      {simdAdd.ElapsedMs:0.00} ms, touched {simdAdd.Touched}, speedup {Speedup(scalarAdd.ElapsedMs, simdAdd.ElapsedMs):0.00}x";
        return Task.CompletedTask;
    }

    private static TerrainBatchResult RunTerrainBatch(float[] heights, TerrainDeformationMode mode, bool removeTerrain, int edits)
    {
        var terrain = new TerrainMask(GameConstants.WorldWidth, GameConstants.WorldHeight, heights);
        var touched = 0;
        var started = Stopwatch.StartNew();
        for (var i = 0; i < edits; i++)
        {
            var x = 80 + ((i * 37) % (GameConstants.WorldWidth - 160)) + ((i % 3) * 0.35f);
            var y = 470 + MathF.Sin(i * 0.19f) * 65f;
            var radius = 16 + ((i * 17) % 124);
            var center = new Vector2(x, y);
            touched += removeTerrain
                ? terrain.RemoveCircle(center, radius, mode)
                : terrain.AddCircle(center, radius, mode);
        }

        started.Stop();
        return new TerrainBatchResult(started.Elapsed.TotalMilliseconds, touched);
    }

    private static double Percentile(IReadOnlyList<double> sorted, double percentile)
    {
        if (sorted.Count == 0) return 0;
        var index = Math.Clamp((int)Math.Ceiling(sorted.Count * percentile) - 1, 0, sorted.Count - 1);
        return sorted[index];
    }

    private static double Speedup(double scalarMs, double simdMs) => simdMs <= 0 ? 0 : scalarMs / simdMs;

    private sealed record TerrainBatchResult(double ElapsedMs, int Touched);
}
