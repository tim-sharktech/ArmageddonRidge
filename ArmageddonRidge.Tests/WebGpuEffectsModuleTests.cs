using System.Diagnostics;
using System.Text.Json;

namespace ArmageddonRidge.Tests;

public sealed class WebGpuEffectsModuleTests
{
    [Fact]
    public async Task WebGpuEffectSanitizersDropNonFinitePayloadGeometry()
    {
        var repoRoot = FindRepoRoot();
        var modulePath = Path.GetFullPath(Path.Combine(repoRoot, "ArmageddonRidge.Client", "wwwroot", "js", "webGpuEffectsRenderer.js"));
        var moduleUri = new Uri(modulePath).AbsoluteUri;
        var script = $$"""
            const effects = await import({{JsonSerializer.Serialize(moduleUri)}});
            const points = effects.sanitizeEffectPoints([
                { X: 10, Y: 20 },
                { x: Number.NaN, y: 30 },
                { x: 40, y: Number.POSITIVE_INFINITY },
                { x: '50', y: '60' }
            ], 2);
            const incomplete = effects.sanitizeEffectPoints([
                { x: Number.NaN, y: 30 },
                { x: 40, y: 50 }
            ], 2);
            const explosions = effects.sanitizeEffectExplosions([
                { X: Number.NaN, Y: 80, Radius: 40 },
                { X: 90, Y: 110, Radius: Number.POSITIVE_INFINITY, TerrainRadius: Number.NEGATIVE_INFINITY, VisualKind: 'Laser' },
                { x: 140, y: 150, radius: '55', terrainRadius: '70', visualKind: 'Lava' }
            ]);

            if (points.length !== 2 || points[0].x !== 10 || points[0].y !== 20 || points[1].x !== 50 || points[1].y !== 60) {
                throw new Error(`Unexpected sanitized WebGPU points ${JSON.stringify(points)}`);
            }

            if (incomplete.length !== 0) {
                throw new Error(`Expected incomplete WebGPU geometry to be dropped, got ${JSON.stringify(incomplete)}`);
            }

            if (explosions.length !== 2) {
                throw new Error(`Expected two finite explosions, got ${JSON.stringify(explosions)}`);
            }

            if (explosions[0].x !== 90 || explosions[0].y !== 110 || explosions[0].radius !== 36 || explosions[0].terrainRadius !== 36) {
                throw new Error(`Unexpected defaulted explosion ${JSON.stringify(explosions[0])}`);
            }

            if (explosions[1].x !== 140 || explosions[1].y !== 150 || explosions[1].radius !== 55 || explosions[1].terrainRadius !== 70) {
                throw new Error(`Unexpected numeric string explosion ${JSON.stringify(explosions[1])}`);
            }
            """;

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "node",
                ArgumentList = { "--input-type=module", "--eval", script },
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        Assert.True(process.Start(), "Failed to start node for WebGPU payload sanitizer smoke.");
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.True(
            process.ExitCode == 0,
            $"WebGPU payload sanitizer smoke failed with exit code {process.ExitCode}.{Environment.NewLine}{output}{Environment.NewLine}{error}");
    }

    [Fact]
    public async Task WebGpuImpactTimingHonorsExplosionTriggerIndexes()
    {
        var repoRoot = FindRepoRoot();
        var modulePath = Path.GetFullPath(Path.Combine(repoRoot, "ArmageddonRidge.Client", "wwwroot", "js", "webGpuEffectsRenderer.js"));
        var moduleUri = new Uri(modulePath).AbsoluteUri;
        var script = $$"""
            const effects = await import({{JsonSerializer.Serialize(moduleUri)}});
            const payload = {
                weaponId: 'gbu-57-mop',
                visualKind: 'PenetratorSecondary',
                trailPointCount: 100,
                trail: Array.from({ length: 100 }, (_, index) => ({ x: index, y: index })),
                explosions: [
                    { x: 40, y: 40, radius: 32, terrainRadius: 50, visualKind: 'PenetratorPrimary', triggerIndex: 40 },
                    { x: 80, y: 80, radius: 62, terrainRadius: 130, visualKind: 'PenetratorSecondary', triggerIndex: -1 }
                ]
            };

            const finalDelay = effects.estimateImpactDelayMs(payload);
            const primaryDelay = effects.estimateExplosionDelayMs(payload, payload.explosions[0]);
            const secondaryDelay = effects.estimateExplosionDelayMs(payload, payload.explosions[1]);
            const pascalDelay = effects.estimateExplosionDelayMs(payload, { TriggerIndex: 40 });
            if (!(primaryDelay >= 80 && primaryDelay < finalDelay)) {
                throw new Error(`Expected primary delay before final impact, got primary=${primaryDelay} final=${finalDelay}`);
            }
            if (secondaryDelay !== finalDelay) {
                throw new Error(`Expected untimed secondary to use final delay, got secondary=${secondaryDelay} final=${finalDelay}`);
            }
            if (pascalDelay !== primaryDelay) {
                throw new Error(`Expected Pascal-case trigger index to match camel-case delay, got pascal=${pascalDelay} camel=${primaryDelay}`);
            }
            """;

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "node",
                ArgumentList = { "--input-type=module", "--eval", script },
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        Assert.True(process.Start(), "Failed to start node for WebGPU impact timing smoke.");
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.True(
            process.ExitCode == 0,
            $"WebGPU impact timing smoke failed with exit code {process.ExitCode}.{Environment.NewLine}{output}{Environment.NewLine}{error}");
    }

    [Fact]
    public async Task WebGpuImpactTimingHonorsSpecialWeaponIds()
    {
        var repoRoot = FindRepoRoot();
        var modulePath = Path.GetFullPath(Path.Combine(repoRoot, "ArmageddonRidge.Client", "wwwroot", "js", "webGpuEffectsRenderer.js"));
        var moduleUri = new Uri(modulePath).AbsoluteUri;
        var script = $$"""
            const effects = await import({{JsonSerializer.Serialize(moduleUri)}});
            const payload = (weaponId, trailPointCount = 100, extra = {}) => ({
                weaponId,
                visualKind: extra.visualKind ?? '',
                trailPointCount,
                trail: Array.from({ length: trailPointCount }, (_, index) => ({ x: index, y: index })),
                explosions: [{ x: 80, y: 80, radius: extra.radius ?? 42, terrainRadius: extra.terrainRadius ?? 60 }],
                ...extra
            });
            const checks = [
                ['dark eagle fixed timing', effects.estimateImpactDelayMs(payload('dark-eagle', 40)) === 2855],
                ['shahed long swarm timing', effects.estimateImpactDelayMs(payload('shahed-drone-swarm', 200)) === 2555],
                ['mirv cluster timing', effects.estimateImpactDelayMs(payload('splitter-mirv', 200)) === 1055],
                ['mop dramatic timing', effects.estimateImpactDelayMs(payload('gbu-57-mop', 200)) === 1655],
                ['nuke default timing', effects.estimateImpactDelayMs(payload('tactical-nuke', 200, { visualKind: 'Nuclear' })) === 755],
                ['patriot intercept clamps long', effects.estimateImpactDelayMs(payload('dark-eagle', 40, { intercepted: true })) === 3355]
            ];
            const failed = checks.filter(([, ok]) => !ok).map(([name]) => name);
            if (failed.length) {
                throw new Error(`WebGPU special weapon timing checks failed: ${failed.join(', ')}`);
            }
            """;

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "node",
                ArgumentList = { "--input-type=module", "--eval", script },
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        Assert.True(process.Start(), "Failed to start node for WebGPU special weapon timing smoke.");
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.True(
            process.ExitCode == 0,
            $"WebGPU special weapon timing smoke failed with exit code {process.ExitCode}.{Environment.NewLine}{output}{Environment.NewLine}{error}");
    }

    [Fact]
    public async Task WebGpuVisualPhysicsSanitizerDropsMalformedEntries()
    {
        var repoRoot = FindRepoRoot();
        var modulePath = Path.GetFullPath(Path.Combine(repoRoot, "ArmageddonRidge.Client", "wwwroot", "js", "webGpuEffectsRenderer.js"));
        var moduleUri = new Uri(modulePath).AbsoluteUri;
        var script = $$"""
            const effects = await import({{JsonSerializer.Serialize(moduleUri)}});
            const payload = effects.sanitizeVisualPhysics({
                slump: { columns: [{ x: 3, fromY: 60, toY: 70 }, { x: 4, fromY: Number.NaN, toY: 72 }] },
                debris: [{ x: 10, y: 20, velocityX: '12' }, { x: Number.NaN, y: 20 }],
                impacts: [{ x: 30, y: 40, material: 'Shield', shieldLike: true }],
                lingering: [{ x: 50, y: 60, windX: -12, visualKind: 'Lava' }],
                shockwaves: [{ x: 70, y: 80, radius: 90, intensity: 120 }, { x: 10, y: 20, radius: 0 }]
            });

            if (payload.slump.columns.length !== 1 || payload.debris.length !== 1 || payload.impacts.length !== 1 || payload.lingering.length !== 1 || payload.shockwaves.length !== 1) {
                throw new Error(`Unexpected visual physics payload ${JSON.stringify(payload)}`);
            }

            if (!payload.impacts[0].shieldLike || payload.shockwaves[0].intensity !== 120) {
                throw new Error(`Unexpected visual physics values ${JSON.stringify(payload)}`);
            }

            const shot = effects.sanitizeEffectPayload({
                trail: [{ x: 1, y: 2 }],
                civilianImpacts: [
                    { x: 20, y: 30, damage: 80, penalty: 125, collapsed: true, kind: 'office' },
                    { x: Number.NaN, y: 30, damage: 80 }
                ]
            });

            if (shot.civilianImpacts.length !== 1 || !shot.civilianImpacts[0].collapsed || shot.civilianImpacts[0].penalty !== 125) {
                throw new Error(`Unexpected civilian impact payload ${JSON.stringify(shot)}`);
            }
            """;

        await RunNodeSmoke(repoRoot, script, "WebGPU visual physics sanitizer smoke");
    }

    [Fact]
    public async Task WebGpuFinalShotDestructionSanitizerClampsPayload()
    {
        var repoRoot = FindRepoRoot();
        var modulePath = Path.GetFullPath(Path.Combine(repoRoot, "ArmageddonRidge.Client", "wwwroot", "js", "webGpuEffectsRenderer.js"));
        var moduleUri = new Uri(modulePath).AbsoluteUri;
        var script = $$"""
            const effects = await import({{JsonSerializer.Serialize(moduleUri)}});
            const pieces = Array.from({ length: 16 }, (_, index) => ({
                victimId: 'cpu',
                sprite: 'plate',
                x: 100 + index,
                y: 200,
                vx: 50,
                vy: -120,
                size: index === 0 ? 999 : 10,
                mass: 1,
                restitution: 2,
                friction: -1,
                drag: 0.2,
                spin: 1,
                lifetime: 2,
                r: 1,
                g: 0.5,
                b: 0.2,
                seed: index
            }));
            pieces.push({ x: Number.NaN, y: 2, vx: 1, vy: 2 });
            const high = effects.sanitizeFinalShotDestruction({ active: true, x: 90, y: 110, radius: 80, pieces }, { qualityTier: 'high' });
            const reduced = effects.sanitizeFinalShotDestruction({ active: true, x: 90, y: 110, radius: 80, reducedMotion: true, pieces }, { qualityTier: 'high' });
            if (!high || high.pieces.length !== 16) {
                throw new Error(`Expected high quality to keep all 16 finite pieces, got ${JSON.stringify(high)}`);
            }
            if (high.pieces[0].size !== 48 || high.pieces[0].restitution !== 0.9 || high.pieces[0].friction !== 0) {
                throw new Error(`Expected clamped first piece, got ${JSON.stringify(high.pieces[0])}`);
            }
            if (!reduced || reduced.pieces.length !== 8) {
                throw new Error(`Expected reduced motion clamp to 8 pieces, got ${JSON.stringify(reduced)}`);
            }
            if (effects.sanitizeFinalShotDestruction({ active: true, x: Number.NaN, y: 1, pieces })) {
                throw new Error('Expected malformed destruction payload to be dropped.');
            }
            """;

        await RunNodeSmoke(repoRoot, script, "WebGPU final-shot destruction sanitizer smoke");
    }

    [Fact]
    public async Task WebGpuFinalShotDestructionTimingFollowsImpact()
    {
        var repoRoot = FindRepoRoot();
        var modulePath = Path.GetFullPath(Path.Combine(repoRoot, "ArmageddonRidge.Client", "wwwroot", "js", "webGpuEffectsRenderer.js"));
        var moduleUri = new Uri(modulePath).AbsoluteUri;
        var script = $$"""
            const effects = await import({{JsonSerializer.Serialize(moduleUri)}});
            const payload = {
                weaponId: 'baby-missile',
                trailPointCount: 100,
                trail: Array.from({ length: 100 }, (_, index) => ({ x: index, y: index })),
                finalShotDestruction: { active: true, x: 90, y: 110, radius: 80, pieces: [{ x: 90, y: 110, vx: 1, vy: -2, size: 8, mass: 1, lifetime: 1 }] }
            };
            const impact = effects.estimateImpactDelayMs(payload);
            const destruction = effects.estimateFinalShotDestructionDelayMs(payload);
            if (destruction !== impact + 95) {
                throw new Error(`Expected destruction timing after impact, impact=${impact} destruction=${destruction}`);
            }
            """;

        await RunNodeSmoke(repoRoot, script, "WebGPU final-shot destruction timing smoke");
    }

    private static string FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir, "ArmageddonRidge.slnx")))
            {
                return dir;
            }

            dir = Directory.GetParent(dir)?.FullName;
        }

        throw new InvalidOperationException("Could not locate repo root.");
    }

    private static async Task RunNodeSmoke(string repoRoot, string script, string label)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "node",
                ArgumentList = { "--input-type=module", "--eval", script },
                WorkingDirectory = repoRoot,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        Assert.True(process.Start(), $"Failed to start node for {label}.");
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.True(
            process.ExitCode == 0,
            $"{label} failed with exit code {process.ExitCode}.{Environment.NewLine}{output}{Environment.NewLine}{error}");
    }
}
