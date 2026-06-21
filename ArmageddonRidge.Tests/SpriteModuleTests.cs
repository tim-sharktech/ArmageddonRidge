using System.Diagnostics;
using System.Text.Json;

namespace ArmageddonRidge.Tests;

public sealed class SpriteModuleTests
{
    [Fact]
    public void CivilianArchetypeSpriteSetsArePresent()
    {
        var repoRoot = FindRepoRoot();
        var spriteRoot = Path.Combine(repoRoot, "ArmageddonRidge.Client", "wwwroot", "assets", "sprites");
        var archetypes = new[] { "office", "apartment", "luxury" };
        var states = new[] { "intact", "damaged", "rubble" };

        foreach (var archetype in archetypes)
        {
            foreach (var state in states)
            {
                var path = Path.Combine(spriteRoot, $"civilian-{archetype}-{state}.png");
                var file = new FileInfo(path);
                Assert.True(file.Exists, $"Missing civilian sprite '{file.Name}'.");
                Assert.True(file.Length > 1_000, $"Civilian sprite '{file.Name}' is unexpectedly small.");
            }
        }
    }

    [Fact]
    public async Task ExtraSpriteMetadataPreservesNaturalAspectRatio()
    {
        var repoRoot = FindRepoRoot();
        var modulePath = Path.GetFullPath(Path.Combine(repoRoot, "ArmageddonRidge.Client", "wwwroot", "js", "rendering", "sprites.js"));
        var moduleUri = new Uri(modulePath).AbsoluteUri;
        var script = $$"""
            const drawCalls = [];
            globalThis.Image = class {
                set decoding(_) {}
                set src(value) {
                    this.url = value;
                    if (value.includes('shahed-drone')) {
                        this.naturalWidth = 96;
                        this.naturalHeight = 32;
                    } else if (value.includes('atlas')) {
                        this.naturalWidth = 256;
                        this.naturalHeight = 256;
                    } else {
                        this.naturalWidth = 80;
                        this.naturalHeight = 40;
                    }
                    this.onload?.();
                }
            };
            globalThis.fetch = async () => ({
                json: async () => ({
                    version: 'test',
                    image: 'assets/sprites/atlas.png',
                    frames: {}
                })
            });
            const sprites = await import({{JsonSerializer.Serialize(moduleUri)}});
            sprites.configureSprites(() => ({ drawImage: (...args) => drawCalls.push(args) }));
            await sprites.loadSprites('test');
            const frame = sprites.spriteFrame('shahedDrone');
            sprites.drawExtraSpriteByHeight('shahedDrone', 50, 60, 24);
            if (!frame || frame.aspect !== 3) {
                throw new Error(`Expected shahedDrone aspect 3, got ${frame?.aspect}`);
            }
            const call = drawCalls.at(-1);
            if (!call || call[1] !== 14 || call[2] !== 48 || call[3] !== 72 || call[4] !== 24) {
                throw new Error(`Unexpected draw call ${JSON.stringify(call)}`);
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

        Assert.True(process.Start(), "Failed to start node for sprite metadata smoke.");
        var output = await process.StandardOutput.ReadToEndAsync();
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.True(
            process.ExitCode == 0,
            $"Sprite metadata smoke failed with exit code {process.ExitCode}.{Environment.NewLine}{output}{Environment.NewLine}{error}");
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
}
