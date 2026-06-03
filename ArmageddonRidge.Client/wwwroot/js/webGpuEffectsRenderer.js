let sourceCanvas;
let canvas;
let adapter;
let device;
let context;
let format;
let particleBuffer;
let radialBuffer;
let activeParticleIndexBuffer;
let activeRadialIndexBuffer;
let uniformBuffer;
let computePipeline;
let renderPipeline;
let postProcessPipeline;
let weatherPipeline;
let computeBindGroup;
let renderBindGroup;
let postProcessBindGroup;
let weatherBindGroup;
let sourceTexture;
let sourceSampler;
let sourceTextureWidth = 0;
let sourceTextureHeight = 0;
let rafId = 0;
let lastFrame = 0;
let frameMs = 0;
let postProcessMs = 0;
let sourceCopyMs = 0;
let supported = false;
let enabled = false;
let fallbackReason = "Not initialized";
let sourceCopySupported = true;
let sourceCopyReady = false;
let currentScene;
let currentWorld = { width: 1200, height: 700 };
let currentWeather = { type: "clear", intensity: 0 };
let currentWind = 0;
let terrainCache = [];
let reducedMotion = false;
let ambientAccumulator = 0;
let qualityTier = "high";
let qualityScale = 1;
let qualityLevel = 2;
let qualityDebt = 0;
let qualityCredit = 0;
let writeIndex = 0;
let radialWriteIndex = 0;
let spawnCount = 0;
let scheduledImpactId = 0;
let activeRadialCount = 0;
let activeParticleCount = 0;
let cachedParticleCount = 0;
let cachedRadialCount = 0;
let overlayScale = 1;
let canvasPixelRatio = 1;
let sourceCopyCadence = 1;
let sourceCopyFrame = 0;
let qualityFrameCounter = 0;
let lastSubmittedGpuCheck = 0;
let gpuQueueMs = 0;
let perfMode = "adaptive";
let spawnBatchNow = 0;
let particleDirtyStart = -1;
let particleDirtyEnd = -1;
let particleDirtyWrapped = false;
let diagnostics = {
    disableSourceCopy: false,
    disablePostProcess: false,
    disableAmbient: false,
    disableWeatherShader: false,
    forceQualityTier: "",
    forceOverlayScale: 0,
    benchmarkMode: false
};
let patriotState;

const maxParticles = 6000;
const particleFloats = 12;
const particleStride = particleFloats * 4;
const maxRadialEffects = 96;
const persistentRadialSlots = 24;
const reservedRadialSlots = 4;
const radialTransientEnd = maxRadialEffects - reservedRadialSlots;
const patriotReticleSlot = maxRadialEffects - 1;
const radialFloats = 16;
const radialStride = radialFloats * 4;
const workgroupSize = 64;
const gravity = 170;
const particleData = new Float32Array(maxParticles * particleFloats);
const expirations = new Float64Array(maxParticles);
const radialData = new Float32Array(maxRadialEffects * radialFloats);
const radialExpirations = new Float64Array(maxRadialEffects);
const radialStartedAt = new Float64Array(maxRadialEffects);
const uniformData = new Float32Array(24);
const activeParticleIndices = new Uint32Array(maxParticles);
const activeRadialIndices = new Uint32Array(maxRadialEffects);

const kinds = {
    spark: 0,
    smoke: 1,
    ember: 2,
    shockwave: 3,
    shield: 4,
    rain: 5,
    snow: 6,
    heat: 7,
    radiation: 8,
    debris: 9,
    flash: 10,
    plasma: 11
};

const radialKinds = {
    shockwave: 0,
    flash: 1,
    radiation: 2,
    heat: 3,
    dust: 4,
    lava: 5,
    glow: 6,
    patriotReticle: 7
};

const patriotInterceptDurationScale = 2.6;
const patriotInterceptMinDuration = 2900;
const patriotInterceptMaxDuration = 3400;

const qualityProfiles = {
    high: { scale: 1, level: 2, ambient: 1, distortion: 1 },
    balanced: { scale: 0.68, level: 1, ambient: 0.72, distortion: 0.72 },
    low: { scale: 0.42, level: 0, ambient: 0.45, distortion: 0.35 }
};

const explosionPresets = {
    ballistic: {
        debrisScale: 1,
        smokeScale: 0.72,
        sparkScale: 0.8,
        heatScale: 0,
        glowScale: 0.6,
        debrisColor: [0.72, 0.49, 0.25],
        smokeColor: [0.38, 0.34, 0.28],
        flashColor: [1, 0.72, 0.28, 0.42],
        shockColor: [1, 0.78, 0.38, 0.48],
        debrisKind: kinds.debris,
        smokeKind: kinds.smoke
    },
    missile: {
        debrisScale: 0.82,
        smokeScale: 1.1,
        sparkScale: 1.1,
        heatScale: 0.3,
        glowScale: 0.75,
        debrisColor: [0.82, 0.58, 0.28],
        smokeColor: [0.43, 0.39, 0.32],
        flashColor: [1, 0.76, 0.32, 0.5],
        shockColor: [0.96, 0.72, 0.34, 0.52],
        debrisKind: kinds.debris,
        smokeKind: kinds.smoke
    },
    penetrator: {
        debrisScale: 1.25,
        smokeScale: 1.35,
        sparkScale: 0.52,
        heatScale: 0.1,
        glowScale: 0.28,
        debrisColor: [0.56, 0.43, 0.28],
        smokeColor: [0.36, 0.32, 0.26],
        flashColor: [0.94, 0.72, 0.42, 0.3],
        shockColor: [0.78, 0.62, 0.46, 0.38],
        debrisKind: kinds.debris,
        smokeKind: kinds.smoke
    },
    dirt: {
        debrisScale: 1.45,
        smokeScale: 0.9,
        sparkScale: 0.08,
        heatScale: 0,
        glowScale: 0.18,
        debrisColor: [0.5, 0.39, 0.24],
        smokeColor: [0.62, 0.52, 0.36],
        flashColor: [0.86, 0.68, 0.42, 0.22],
        shockColor: [0.7, 0.58, 0.42, 0.28],
        debrisKind: kinds.debris,
        smokeKind: kinds.smoke
    },
    lava: {
        debrisScale: 0.65,
        smokeScale: 1,
        sparkScale: 1.45,
        heatScale: 1.35,
        glowScale: 1.2,
        debrisColor: [1, 0.34, 0.08],
        smokeColor: [0.38, 0.26, 0.2],
        flashColor: [1, 0.44, 0.08, 0.58],
        shockColor: [1, 0.36, 0.08, 0.55],
        debrisKind: kinds.ember,
        smokeKind: kinds.smoke
    },
    laser: {
        debrisScale: 0.1,
        smokeScale: 0.25,
        sparkScale: 1.6,
        heatScale: 0.45,
        glowScale: 1.15,
        debrisColor: [1, 0.26, 0.42],
        smokeColor: [0.46, 0.28, 0.34],
        flashColor: [1, 0.18, 0.34, 0.62],
        shockColor: [0.96, 0.28, 0.62, 0.52],
        debrisKind: kinds.plasma,
        smokeKind: kinds.heat
    },
    drone: {
        debrisScale: 0.55,
        smokeScale: 1.2,
        sparkScale: 0.72,
        heatScale: 0.24,
        glowScale: 0.42,
        debrisColor: [0.9, 0.68, 0.38],
        smokeColor: [0.48, 0.48, 0.42],
        flashColor: [1, 0.78, 0.38, 0.36],
        shockColor: [0.86, 0.78, 0.64, 0.34],
        debrisKind: kinds.debris,
        smokeKind: kinds.smoke
    },
    nuclear: {
        debrisScale: 1.6,
        smokeScale: 1.75,
        sparkScale: 1.4,
        heatScale: 0.8,
        glowScale: 1.8,
        debrisColor: [0.78, 0.62, 0.38],
        smokeColor: [0.42, 0.37, 0.29],
        flashColor: [1, 0.94, 0.66, 0.78],
        shockColor: [1, 0.9, 0.58, 0.7],
        debrisKind: kinds.debris,
        smokeKind: kinds.smoke
    }
};

export async function initialize(baseElement, overlayElement, options = {}) {
    if (overlayElement && typeof overlayElement.getContext === "function") {
        sourceCanvas = baseElement;
        canvas = overlayElement;
    } else {
        sourceCanvas = undefined;
        canvas = baseElement;
        options = overlayElement ?? {};
    }

    fallbackReason = "";
    sourceCopySupported = true;
    sourceCopyReady = false;
    postProcessMs = 0;
    sourceCopyMs = 0;
    applyDiagnosticOptions(options);

    if (!options.enabled) {
        enabled = false;
        supported = false;
        fallbackReason = "Disabled";
        clearScheduledImpact();
        clearPatriotState();
        clearCpuState();
        return getStats();
    }

    if (!navigator.gpu) {
        enabled = false;
        supported = false;
        fallbackReason = "WebGPU unavailable";
        clearScheduledImpact();
        clearPatriotState();
        clearCpuState();
        return getStats();
    }

    try {
        adapter = await navigator.gpu.requestAdapter({ powerPreference: "high-performance" });
        if (!adapter) {
            enabled = false;
            supported = false;
            fallbackReason = "No WebGPU adapter";
            return getStats();
        }

        device = await adapter.requestDevice();
        device.lost.then(info => {
            enabled = false;
            supported = false;
            fallbackReason = info?.message ? `Device lost: ${info.message}` : "WebGPU device lost";
            stopLoop();
        });

        context = canvas.getContext("webgpu");
        if (!context) {
            enabled = false;
            supported = false;
            fallbackReason = "WebGPU context unavailable";
            return getStats();
        }

        format = navigator.gpu.getPreferredCanvasFormat();
        createResources();
        supported = true;
        enabled = true;
        fallbackReason = "";
        resizeCanvas();
        startLoop();
    } catch (error) {
        enabled = false;
        supported = false;
        fallbackReason = shortError(error);
        stopLoop();
    }

    return getStats();
}

export async function setEnabled(value) {
    if (!value) {
        enabled = false;
        fallbackReason = "Disabled";
        clearScheduledImpact();
        clearPatriotState();
        clearCpuState();
        clearOverlay();
        stopLoop();
        return getStats();
    }

    if (!canvas || !device || !context) {
        return await initialize(sourceCanvas ?? canvas, canvas, { enabled: true });
    }

    enabled = supported;
    fallbackReason = supported ? "" : fallbackReason;
    startLoop();
    return getStats();
}

export function configureDiagnostics(options = {}) {
    applyDiagnosticOptions(options);
    return getStats();
}

export function setScene(scene, terrainRevision, options = {}) {
    applyDiagnosticOptions(options);
    currentScene = scene;
    currentWorld = scene?.world ?? currentWorld;
    currentWeather = scene?.weather ?? currentWeather;
    currentWind = Number(scene?.wind ?? 0);
    reducedMotion = Boolean(options?.reducedMotion);
    if (scene?.terrain?.length) {
        terrainCache = Array.from(scene.terrain);
    }
    syncRadiationZones(scene?.radiation ?? []);

    if (enabled) startLoop();
    return getStats();
}

function applyDiagnosticOptions(options = {}) {
    if (!options) return;

    diagnostics.disableSourceCopy = Boolean(options.disableSourceCopy ?? diagnostics.disableSourceCopy);
    diagnostics.disablePostProcess = Boolean(options.disablePostProcess ?? diagnostics.disablePostProcess);
    diagnostics.disableAmbient = Boolean(options.disableAmbient ?? diagnostics.disableAmbient);
    diagnostics.disableWeatherShader = Boolean(options.disableWeatherShader ?? diagnostics.disableWeatherShader);
    diagnostics.benchmarkMode = Boolean(options.benchmarkMode ?? diagnostics.benchmarkMode);
    diagnostics.forceQualityTier = normalized(options.forceQualityTier ?? diagnostics.forceQualityTier);
    diagnostics.forceOverlayScale = Number(options.forceOverlayScale ?? diagnostics.forceOverlayScale) || 0;
    perfMode = diagnostics.benchmarkMode ? "benchmark" : diagnostics.forceQualityTier ? `forced-${diagnostics.forceQualityTier}` : "adaptive";

    if (diagnostics.forceQualityTier && qualityProfiles[diagnostics.forceQualityTier]) {
        setQualityTier(diagnostics.forceQualityTier);
    }
}

export function spawnShotEffects(payload) {
    if (!enabled || !device) return getStats();

    beginSpawnBatch();
    if (String(payload?.phase ?? "").toLowerCase() === "flight") {
        spawnFlightEffects(payload);
        startPatriotInterception(payload);
        scheduleImpactEffects(payload);
    } else {
        spawnImpactEffects(payload);
    }
    endSpawnBatch();

    return getStats();
}

export function spawnTerrainEffects(payload) {
    if (!enabled || !device || Number(payload?.terrainColumnsTouched ?? 0) <= 0) {
        return getStats();
    }

    const wind = Number(payload.wind ?? currentWind);
    const explosions = payload.explosions ?? [];
    beginSpawnBatch();
    for (const explosion of explosions) {
        const x = Number(explosion.x ?? 0);
        const radius = Math.max(16, Number(explosion.terrainRadius ?? explosion.radius ?? 40));
        const centerY = Number(explosion.y ?? surfaceY(x));
        const surface = Math.min(centerY, surfaceY(x));
        const preset = resolveExplosionPreset(explosion, payload);
        const rimCount = scaledCount(radius * 0.62, 14, 72);
        for (let i = 0; i < rimCount; i++) {
            const side = i % 2 === 0 ? -1 : 1;
            const distance = radius * randomBetween(0.68, 1.22) * side;
            const px = x + distance + randomBetween(-radius * 0.18, radius * 0.18);
            const py = surfaceY(px);
            const slope = surfaceY(px + 4) - surfaceY(px - 4);
            const lift = clamp(Math.abs(slope) / 18, 0.25, 1.4);
            const vx = wind * 0.85 + side * randomBetween(18, 86) - slope * randomBetween(1.2, 2.6);
            const vy = randomBetween(-110, -22) * lift;
            const color = preset.debrisColor;
            spawnParticle(px, py - randomBetween(0, 6), vx, vy, color[0], color[1], color[2], randomBetween(0.38, 0.66), randomBetween(3.5, 9), randomBetween(0.75, 1.7), kinds.debris);
            if (i % 2 === 0) {
                const smoke = preset.smokeColor;
                spawnParticle(px, py - randomBetween(0, 12), wind * 1.35 + randomBetween(-20, 20), randomBetween(-40, -6), smoke[0], smoke[1], smoke[2], 0.22, randomBetween(16, 36), randomBetween(1.2, 2.8), kinds.smoke);
            }
        }

        const count = scaledCount(radius * 0.48, 8, 54);
        for (let i = 0; i < count; i++) {
            const t = randomBetween(-1, 1);
            const px = x + t * radius * randomBetween(0.25, 1.05);
            const py = Math.min(surfaceY(px), surface + randomBetween(-12, 22));
            const vx = wind * 0.85 + t * randomBetween(16, 76);
            const vy = randomBetween(-112, -22);
            const color = preset.debrisColor;
            spawnParticle(px, py, vx, vy, color[0], color[1], color[2], 0.46, randomBetween(4, 10), randomBetween(0.85, 1.8), kinds.debris);
            if (i % 3 === 0) {
                const smoke = preset.smokeColor;
                spawnParticle(px, py, wind * 1.1 + randomBetween(-18, 18), randomBetween(-38, -8), smoke[0], smoke[1], smoke[2], 0.26, randomBetween(12, 30), randomBetween(1.1, 2.4), kinds.smoke);
            }
        }
    }
    endSpawnBatch();

    return getStats();
}

export function getStats() {
    return {
        supported,
        enabled,
        frameMs,
        postProcessMs,
        sourceCopyMs,
        particleCount: cachedParticleCount,
        radialEffectCount: cachedRadialCount,
        spawnCount,
        activeParticleCount,
        particleCapacity: maxParticles,
        overlayScale,
        canvasPixelRatio,
        sourceCopyCadence,
        gpuQueueMs,
        perfMode,
        qualityTier,
        fallbackReason: fallbackReason ?? ""
    };
}

export function dispose() {
    stopLoop();
    clearScheduledImpact();
    clearPatriotState();
    clearCpuState();
    if (particleBuffer) particleBuffer.destroy();
    if (radialBuffer) radialBuffer.destroy();
    if (activeParticleIndexBuffer) activeParticleIndexBuffer.destroy();
    if (activeRadialIndexBuffer) activeRadialIndexBuffer.destroy();
    if (uniformBuffer) uniformBuffer.destroy();
    if (sourceTexture) sourceTexture.destroy();
    particleBuffer = undefined;
    radialBuffer = undefined;
    activeParticleIndexBuffer = undefined;
    activeRadialIndexBuffer = undefined;
    uniformBuffer = undefined;
    sourceTexture = undefined;
    sourceSampler = undefined;
    computePipeline = undefined;
    renderPipeline = undefined;
    postProcessPipeline = undefined;
    weatherPipeline = undefined;
    computeBindGroup = undefined;
    renderBindGroup = undefined;
    postProcessBindGroup = undefined;
    weatherBindGroup = undefined;
    enabled = false;
}

function createResources() {
    resizeCanvas();
    context.configure({
        device,
        format,
        alphaMode: "premultiplied"
    });

    particleBuffer = device.createBuffer({
        size: maxParticles * particleStride,
        usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST
    });

    radialBuffer = device.createBuffer({
        size: maxRadialEffects * radialStride,
        usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST
    });

    activeParticleIndexBuffer = device.createBuffer({
        size: activeParticleIndices.byteLength,
        usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST
    });

    activeRadialIndexBuffer = device.createBuffer({
        size: activeRadialIndices.byteLength,
        usage: GPUBufferUsage.STORAGE | GPUBufferUsage.COPY_DST
    });

    uniformBuffer = device.createBuffer({
        size: uniformData.byteLength,
        usage: GPUBufferUsage.UNIFORM | GPUBufferUsage.COPY_DST
    });

    sourceSampler = device.createSampler({
        magFilter: "linear",
        minFilter: "linear",
        addressModeU: "clamp-to-edge",
        addressModeV: "clamp-to-edge"
    });
    createOrResizeSourceTexture();

    const computeModule = device.createShaderModule({ code: computeShader });
    computePipeline = device.createComputePipeline({
        layout: "auto",
        compute: { module: computeModule, entryPoint: "updateParticles" }
    });
    computeBindGroup = device.createBindGroup({
        layout: computePipeline.getBindGroupLayout(0),
        entries: [
            { binding: 0, resource: { buffer: particleBuffer } },
            { binding: 1, resource: { buffer: uniformBuffer } },
            { binding: 2, resource: { buffer: activeParticleIndexBuffer } }
        ]
    });

    const renderModule = device.createShaderModule({ code: renderShader });
    renderPipeline = device.createRenderPipeline({
        layout: "auto",
        vertex: { module: renderModule, entryPoint: "vertexMain" },
        fragment: {
            module: renderModule,
            entryPoint: "fragmentMain",
            targets: [
                {
                    format,
                    blend: {
                        color: {
                            srcFactor: "one",
                            dstFactor: "one-minus-src-alpha",
                            operation: "add"
                        },
                        alpha: {
                            srcFactor: "one",
                            dstFactor: "one-minus-src-alpha",
                            operation: "add"
                        }
                    }
                }
            ]
        },
        primitive: { topology: "triangle-list" }
    });
    renderBindGroup = device.createBindGroup({
        layout: renderPipeline.getBindGroupLayout(0),
        entries: [
            { binding: 0, resource: { buffer: particleBuffer } },
            { binding: 1, resource: { buffer: uniformBuffer } },
            { binding: 2, resource: { buffer: activeParticleIndexBuffer } }
        ]
    });

    const postProcessModule = device.createShaderModule({ code: postProcessShader });
    postProcessPipeline = device.createRenderPipeline({
        layout: "auto",
        vertex: { module: postProcessModule, entryPoint: "vertexMain" },
        fragment: {
            module: postProcessModule,
            entryPoint: "fragmentMain",
            targets: [
                {
                    format,
                    blend: {
                        color: {
                            srcFactor: "one",
                            dstFactor: "one-minus-src-alpha",
                            operation: "add"
                        },
                        alpha: {
                            srcFactor: "one",
                            dstFactor: "one-minus-src-alpha",
                            operation: "add"
                        }
                    }
                }
            ]
        },
        primitive: { topology: "triangle-list" }
    });
    createPostProcessBindGroup();

    const weatherModule = device.createShaderModule({ code: weatherShader });
    weatherPipeline = device.createRenderPipeline({
        layout: "auto",
        vertex: { module: weatherModule, entryPoint: "vertexMain" },
        fragment: {
            module: weatherModule,
            entryPoint: "fragmentMain",
            targets: [
                {
                    format,
                    blend: {
                        color: {
                            srcFactor: "one",
                            dstFactor: "one-minus-src-alpha",
                            operation: "add"
                        },
                        alpha: {
                            srcFactor: "one",
                            dstFactor: "one-minus-src-alpha",
                            operation: "add"
                        }
                    }
                }
            ]
        },
        primitive: { topology: "triangle-list" }
    });
    weatherBindGroup = device.createBindGroup({
        layout: weatherPipeline.getBindGroupLayout(0),
        entries: [
            { binding: 0, resource: { buffer: uniformBuffer } }
        ]
    });

    clearCpuState();
}

function startLoop() {
    if (rafId || !enabled) return;
    lastFrame = performance.now();
    rafId = requestAnimationFrame(frame);
}

function stopLoop() {
    if (!rafId) return;
    cancelAnimationFrame(rafId);
    rafId = 0;
}

function clearScheduledImpact() {
    if (scheduledImpactId) {
        clearTimeout(scheduledImpactId);
        scheduledImpactId = 0;
    }
}

function frame(now) {
    rafId = 0;
    if (!enabled || !device || !context) return;

    const dt = Math.min(0.05, Math.max(0.001, (now - lastFrame) / 1000));
    lastFrame = now;
    const started = performance.now();
    resizeCanvas();
    emitAmbient(dt, now);
    emitPatriotEffects(dt, now);
    const particleCount = refreshActiveParticleIndices(now);
    const radialCount = refreshActiveRadialIndices(now);
    copySourceCanvasIfNeeded(radialCount);
    updateUniforms(dt, now);
    flushParticleWrites();

    const encoder = device.createCommandEncoder();
    if (particleCount > 0) {
        const computePass = encoder.beginComputePass();
        computePass.setPipeline(computePipeline);
        computePass.setBindGroup(0, computeBindGroup);
        computePass.dispatchWorkgroups(Math.ceil(particleCount / workgroupSize));
        computePass.end();
    }

    const renderPass = encoder.beginRenderPass({
        colorAttachments: [
            {
                view: context.getCurrentTexture().createView(),
                clearValue: { r: 0, g: 0, b: 0, a: 0 },
                loadOp: "clear",
                storeOp: "store"
            }
        ]
    });
    const postStarted = performance.now();
    if (!diagnostics.disableWeatherShader && weatherPipeline && weatherBindGroup && shouldRenderProceduralWeather()) {
        renderPass.setPipeline(weatherPipeline);
        renderPass.setBindGroup(0, weatherBindGroup);
        renderPass.draw(6, 1);
    }
    if (!diagnostics.disablePostProcess && postProcessPipeline && postProcessBindGroup && radialCount > 0) {
        renderPass.setPipeline(postProcessPipeline);
        renderPass.setBindGroup(0, postProcessBindGroup);
        renderPass.draw(6, radialCount);
    }
    postProcessMs = performance.now() - postStarted;
    if (particleCount > 0) {
        renderPass.setPipeline(renderPipeline);
        renderPass.setBindGroup(0, renderBindGroup);
        renderPass.draw(6, particleCount);
    }
    renderPass.end();

    device.queue.submit([encoder.finish()]);
    frameMs = performance.now() - started;
    updateQualityTier();
    sampleGpuQueue(now);
    if (shouldContinueLoop()) {
        rafId = requestAnimationFrame(frame);
    } else {
        clearOverlay();
    }
}

function clearOverlay() {
    if (!device || !context) return;

    resizeCanvas();
    const encoder = device.createCommandEncoder();
    const renderPass = encoder.beginRenderPass({
        colorAttachments: [
            {
                view: context.getCurrentTexture().createView(),
                clearValue: { r: 0, g: 0, b: 0, a: 0 },
                loadOp: "clear",
                storeOp: "store"
            }
        ]
    });
    renderPass.end();
    device.queue.submit([encoder.finish()]);
}

function shouldContinueLoop() {
    if (!enabled || diagnostics.benchmarkMode) return enabled;
    if (particleDirtyStart >= 0 || cachedParticleCount > 0 || cachedRadialCount > 0 || patriotState) return true;
    if (shouldRenderProceduralWeather() || hasAmbientEmitters()) return true;
    return false;
}

function hasAmbientEmitters() {
    if (!currentScene || reducedMotion || diagnostics.disableAmbient) return false;
    const weather = normalized(currentWeather?.type);
    if (weather === "rain" || weather === "storm" || weather === "snow") return true;
    if (Math.abs(currentWind) > 4) return true;
    return (currentScene.radiation ?? []).length > 0;
}

function shouldRenderProceduralWeather() {
    if (!currentScene || reducedMotion || diagnostics.disableAmbient || diagnostics.disableWeatherShader) return false;
    const weather = normalized(currentWeather?.type);
    return weather === "rain" || weather === "storm" || weather === "snow";
}

function sampleGpuQueue(now) {
    if (!device?.queue?.onSubmittedWorkDone || now - lastSubmittedGpuCheck < 1000) return;

    lastSubmittedGpuCheck = now;
    const started = performance.now();
    device.queue.onSubmittedWorkDone()
        .then(() => {
            gpuQueueMs = performance.now() - started;
        })
        .catch(() => {
            gpuQueueMs = 0;
        });
}

function resizeCanvas() {
    if (!canvas) return;

    const rect = canvas.getBoundingClientRect();
    overlayScale = resolveOverlayScale();
    canvasPixelRatio = Math.max(1, Math.min(2, window.devicePixelRatio || 1));
    const ratio = Math.max(0.5, canvasPixelRatio * overlayScale);
    const width = Math.max(1, Math.floor(rect.width * ratio));
    const height = Math.max(1, Math.floor(rect.height * ratio));
    if (canvas.width !== width || canvas.height !== height) {
        canvas.width = width;
        canvas.height = height;
        createOrResizeSourceTexture();
    } else {
        createOrResizeSourceTexture();
    }
}

function resolveOverlayScale() {
    if (diagnostics.forceOverlayScale > 0) {
        return clamp(diagnostics.forceOverlayScale, 0.35, 1);
    }

    if (qualityTier === "low") return 0.5;
    if (qualityTier === "balanced") return 0.75;
    return 1;
}

function createOrResizeSourceTexture() {
    if (!device || !canvas || canvas.width <= 0 || canvas.height <= 0) return;

    const sourceWidth = Math.max(canvas.width, Number(sourceCanvas?.width ?? canvas.width));
    const sourceHeight = Math.max(canvas.height, Number(sourceCanvas?.height ?? canvas.height));
    if (sourceTexture && sourceTextureWidth === sourceWidth && sourceTextureHeight === sourceHeight) {
        return;
    }

    if (sourceTexture) sourceTexture.destroy();
    sourceTextureWidth = sourceWidth;
    sourceTextureHeight = sourceHeight;
    sourceTexture = device.createTexture({
        size: { width: sourceTextureWidth, height: sourceTextureHeight, depthOrArrayLayers: 1 },
        format: "rgba8unorm",
        usage: GPUTextureUsage.TEXTURE_BINDING | GPUTextureUsage.COPY_DST
    });
    createPostProcessBindGroup();
}

function createPostProcessBindGroup() {
    if (!device || !postProcessPipeline || !radialBuffer || !activeRadialIndexBuffer || !uniformBuffer || !sourceTexture || !sourceSampler) return;

    postProcessBindGroup = device.createBindGroup({
        layout: postProcessPipeline.getBindGroupLayout(0),
        entries: [
            { binding: 0, resource: { buffer: radialBuffer } },
            { binding: 1, resource: { buffer: uniformBuffer } },
            { binding: 2, resource: sourceTexture.createView() },
            { binding: 3, resource: sourceSampler },
            { binding: 4, resource: { buffer: activeRadialIndexBuffer } }
        ]
    });
}

function updateUniforms(dt, now) {
    const worldWidth = Number(currentWorld?.width ?? 1200);
    const worldHeight = Number(currentWorld?.height ?? 700);
    const scale = Math.min(canvas.width / worldWidth, canvas.height / worldHeight);
    const left = (canvas.width - worldWidth * scale) * 0.5;
    const top = (canvas.height - worldHeight * scale) * 0.5;
    const weather = normalized(currentWeather?.type);
    const weatherType = weather === "rain" || weather === "storm" ? 1 : weather === "snow" ? 2 : 0;
    const weatherIntensity = clamp(Number(currentWeather?.intensity ?? 0), 0, 1);
    uniformData[0] = dt;
    uniformData[1] = currentWind;
    uniformData[2] = gravity;
    uniformData[3] = now * 0.001;
    uniformData[4] = canvas.width;
    uniformData[5] = canvas.height;
    uniformData[6] = worldWidth;
    uniformData[7] = worldHeight;
    uniformData[8] = sourceCopyReady ? 1 : 0;
    uniformData[9] = qualityLevel;
    uniformData[10] = activeRadialCount;
    uniformData[11] = activeParticleCount;
    uniformData[12] = scale;
    uniformData[13] = left;
    uniformData[14] = top;
    uniformData[15] = canvas.width > 0 ? 1 / canvas.width : 1;
    uniformData[16] = canvas.height > 0 ? 1 / canvas.height : 1;
    uniformData[17] = weatherType;
    uniformData[18] = weatherIntensity;
    uniformData[19] = overlayScale;
    uniformData[20] = canvasPixelRatio;
    uniformData[21] = 0;
    uniformData[22] = sourceTextureWidth > 0 ? canvas.width / sourceTextureWidth : 1;
    uniformData[23] = sourceTextureHeight > 0 ? canvas.height / sourceTextureHeight : 1;
    device.queue.writeBuffer(uniformBuffer, 0, uniformData);
}

function copySourceCanvasIfNeeded(radialCount) {
    sourceCopyReady = false;
    sourceCopyMs = 0;
    sourceCopyCadence = 0;
    if (diagnostics.disableSourceCopy || !radialCount || !sourceCanvas || !sourceTexture || !sourceCopySupported || qualityTier === "low") {
        return;
    }

    if (!hasSourceSamplingRadials()) return;
    sourceCopyFrame++;
    sourceCopyCadence = qualityTier === "balanced" ? 2 : 1;
    if (sourceCopyCadence > 1 && sourceCopyFrame % sourceCopyCadence !== 0) {
        sourceCopyReady = true;
        return;
    }

    const width = Math.min(sourceTextureWidth, Number(sourceCanvas.width ?? sourceTextureWidth));
    const height = Math.min(sourceTextureHeight, Number(sourceCanvas.height ?? sourceTextureHeight));
    if (width <= 0 || height <= 0) return;

    const started = performance.now();
    try {
        device.queue.copyExternalImageToTexture(
            { source: sourceCanvas },
            { texture: sourceTexture },
            { width, height }
        );
        sourceCopyReady = true;
        sourceCopyMs = performance.now() - started;
    } catch (error) {
        sourceCopyReady = false;
        sourceCopySupported = false;
        sourceCopyMs = 0;
        fallbackReason = "Canvas copy fallback";
        console.debug?.("WebGPU source canvas copy disabled", error);
    }
}

function spawnFlightEffects(payload) {
    const trail = payload?.trail ?? [];
    if (trail.length < 2) return;

    const visual = normalized(payload.visualKind ?? payload.weaponId);
    const wind = Number(payload.wind ?? currentWind);
    const step = Math.max(2, Math.floor(trail.length / scaledCount(28, 12, 48)));
    for (let i = 1; i < trail.length; i += step) {
        const point = trail[i];
        const previous = trail[Math.max(0, i - 1)];
        const angle = Math.atan2(Number(point.y) - Number(previous.y), Number(point.x) - Number(previous.x));
        const x = Number(point.x);
        const y = Number(point.y);
        if (visual.includes("laser")) {
            spawnParticle(x, y, randomBetween(-16, 16) + wind * 0.25, randomBetween(-26, 18), 1, 0.25, 0.32, 0.72, randomBetween(3, 7), randomBetween(0.28, 0.62), kinds.plasma);
        } else if (visual.includes("drone")) {
            spawnParticle(x, y, wind * 0.35 + randomBetween(-18, 18), randomBetween(-8, 18), 0.78, 0.83, 0.78, 0.24, randomBetween(8, 18), randomBetween(0.75, 1.5), kinds.smoke);
            spawnParticle(x, y, Math.cos(angle + Math.PI) * 18, Math.sin(angle + Math.PI) * 18, 1, 0.74, 0.42, 0.36, randomBetween(3, 6), randomBetween(0.35, 0.7), kinds.spark);
        } else if (visual.includes("missile") || visual.includes("mop") || visual.includes("dark") || visual.includes("penetrator")) {
            spawnParticle(x, y, wind * 0.42 - Math.cos(angle) * 22 + randomBetween(-9, 9), -Math.sin(angle) * 14 + randomBetween(-8, 8), 0.72, 0.68, 0.56, 0.28, randomBetween(11, 24), randomBetween(0.7, 1.4), kinds.smoke);
            if (visual.includes("dark")) {
                spawnParticle(x, y, 0, 0, 0.66, 0.86, 1, 0.2, randomBetween(16, 28), randomBetween(0.18, 0.34), kinds.shockwave);
            }
        } else if (visual.includes("fire") || visual.includes("lava") || visual.includes("napalm")) {
            spawnParticle(x, y, wind * 0.35 + randomBetween(-26, 26), randomBetween(-38, 10), 1, 0.34, 0.1, 0.58, randomBetween(3, 8), randomBetween(0.55, 1.15), kinds.ember);
        } else {
            spawnParticle(x, y, wind * 0.2 + randomBetween(-10, 10), randomBetween(-12, 10), 1, 0.9, 0.58, 0.24, randomBetween(3, 6), randomBetween(0.35, 0.8), kinds.spark);
        }
    }
}

function scheduleImpactEffects(payload) {
    clearScheduledImpact();
    const delay = estimateImpactDelayMs(payload);
    scheduledImpactId = setTimeout(() => {
        scheduledImpactId = 0;
        if (!enabled || !device) return;
        beginSpawnBatch();
        spawnImpactEffects(payload);
        endSpawnBatch();
    }, delay);
}

function estimateImpactDelayMs(payload) {
    const trailCount = Math.max(2, Number(payload?.trailPointCount ?? payload?.trail?.length ?? 2));
    const weaponId = normalized(payload?.weaponId);
    const visualKind = normalized(payload?.visualKind);
    let visualDuration;
    if (weaponId.includes("dark-eagle") || visualKind.includes("dark")) {
        visualDuration = 2900;
    } else if (weaponId.includes("shahed") || visualKind.includes("drone")) {
        visualDuration = Math.min(3400, Math.max(1500, trailCount * 13));
    } else if (weaponId.includes("splitter") || visualKind.includes("mirv")) {
        visualDuration = Math.min(1800, Math.max(900, trailCount * 5.5));
    } else if (weaponId.includes("gbu") || weaponId.includes("mop")) {
        visualDuration = Math.min(2100, Math.max(900, trailCount * 8.5));
    } else {
        visualDuration = Math.min(1200, Math.max(260, trailCount * 4));
    }

    if (payload?.intercepted) {
        visualDuration = Math.max(2900, Math.min(3400, visualDuration * 2.6));
    }

    return Math.max(80, Math.min(3400, visualDuration - 45));
}

function startPatriotInterception(payload) {
    clearPatriotState();

    const interceptX = Number(payloadValue(payload, "interceptX"));
    const interceptY = Number(payloadValue(payload, "interceptY"));
    if (!truthyPayloadValue(payload, "patriotOverlayEnabled") || !truthyPayloadValue(payload, "intercepted") || !Number.isFinite(interceptX) || !Number.isFinite(interceptY) || !currentScene) {
        return;
    }

    const trail = payload?.trail ?? [];
    const intercept = { x: interceptX, y: interceptY };

    const patriot = createPatriotPlayback(trail, intercept);
    const incomingOwner = String(payload.ownerTankId ?? "");
    const playerTank = currentScene.player;
    const cpuTank = currentScene.cpu;
    const launcher = incomingOwner === String(playerTank?.id ?? "") ? cpuTank : playerTank;
    if (!launcher) {
        return;
    }

    const startX = Number(launcher.x ?? 0) + (launcher.isCpu ? -28 : 28);
    const startY = Number(launcher.y ?? 0) - 46;
    const endX = Number(patriot.interceptX ?? intercept.x);
    const endY = Number(patriot.interceptY ?? intercept.y);
    const apexX = Number(patriot.apexX ?? intercept.x);
    const apexY = Number(patriot.apexY ?? intercept.y);
    const duration = patriotShotDuration(patriot.points.length, payload.weaponId);

    patriotState = {
        started: performance.now(),
        duration,
        startX,
        startY,
        controlX: startX + ((endX - startX) * 0.22),
        controlY: Math.min(startY, endY, apexY) - 155 - Math.abs(endX - startX) * 0.04,
        apexX,
        apexY,
        endX,
        endY,
        holdStart: Number(patriot.holdStartProgress ?? 0.48),
        holdEnd: Number(patriot.holdEndProgress ?? 0.68),
        lockStart: Number(patriot.lockProgressStart ?? 0.22),
        launchStart: Number(patriot.launchProgressStart ?? 0.72),
        lastMissileProgress: 0,
        lastGuideEmit: 0,
        lastMoteEmit: 0,
        burstSpawned: false,
        seed: Math.random() * 1000
    };
    spawnRadialEffect(apexX, apexY, 96, 0.32, radialKinds.flash, 0.5, [0.66, 0.9, 1, 0.18], { softness: 0.62, seed: patriotState.seed });
    startLoop();
}

function clearPatriotState() {
    patriotState = undefined;
    clearRadialSlot(patriotReticleSlot);
    if (device && radialBuffer) {
        device.queue.writeBuffer(radialBuffer, patriotReticleSlot * radialStride, radialData, patriotReticleSlot * radialFloats, radialFloats);
    }
}

function emitPatriotEffects(dt, now) {
    if (!patriotState) {
        return;
    }

    const state = patriotState;
    const elapsed = now - state.started;
    const progress = clamp(elapsed / Math.max(1, state.duration), 0, 1);
    const lockProgress = clamp((progress - state.lockStart) / Math.max(0.05, state.holdStart - state.lockStart), 0, 1);
    const launchProgress = clamp((progress - state.launchStart) / Math.max(0.08, 1 - state.launchStart), 0, 1);
    const postLaunchFade = launchProgress > 0 ? Math.max(0.42, 1 - launchProgress * 0.72) : 1;

    if (lockProgress > 0) {
        const alpha = lockProgress * postLaunchFade;
        writeRadialSlot(patriotReticleSlot, {
            x: state.apexX,
            y: state.apexY,
            radius: 58,
            duration: 0.12,
            type: radialKinds.patriotReticle,
            intensity: alpha,
            color: [0.78, 0.93, 1, 0.78],
            wind: 0,
            softness: 0.08,
            seed: state.seed
        });

        if (now - state.lastMoteEmit > 65) {
            emitPatriotLockMotes(state, alpha, now);
            state.lastMoteEmit = now;
        }
    }

    if (launchProgress > 0) {
        const missileProgress = launchProgress * launchProgress * (3 - (2 * launchProgress));
        emitPatriotGuide(state, launchProgress, now);
        emitPatriotTrail(state, missileProgress, now);
        state.lastMissileProgress = missileProgress;

        const head = pointOnPatriotCurve(state, missileProgress);
        spawnParticle(head.x, head.y, 0, 0, 0.74, 0.92, 1, 0.42, randomBetween(14, 22), randomBetween(0.08, 0.16), kinds.flash);

        if (!state.burstSpawned && progress >= 0.965) {
            spawnPatriotInterceptBurst(state);
            state.burstSpawned = true;
        }
    }

    if (progress >= 1 && elapsed > state.duration + 260) {
        clearPatriotState();
    }
}

function payloadValue(payload, camelName) {
    if (!payload) return undefined;
    if (payload[camelName] !== undefined) return payload[camelName];
    const pascalName = camelName.charAt(0).toUpperCase() + camelName.slice(1);
    return payload[pascalName];
}

function truthyPayloadValue(payload, camelName) {
    const value = payloadValue(payload, camelName);
    return value === true || value === "true" || value === 1;
}

function emitPatriotLockMotes(state, alpha, now) {
    const count = scaledCount(6, 2, 8);
    for (let i = 0; i < count; i++) {
        const angle = now * 0.0026 + state.seed + (i / count) * Math.PI * 2;
        const radius = randomBetween(44, 66);
        const x = state.apexX + Math.cos(angle) * radius;
        const y = state.apexY + Math.sin(angle) * radius;
        spawnParticle(x, y, Math.cos(angle + Math.PI) * 10, Math.sin(angle + Math.PI) * 10, 0.64, 0.9, 1, 0.28 * alpha, randomBetween(2.5, 5.5), randomBetween(0.22, 0.42), kinds.plasma);
    }
}

function emitPatriotGuide(state, launchProgress, now) {
    if (now - state.lastGuideEmit < 45) return;

    state.lastGuideEmit = now;
    const alpha = Math.max(0, 0.22 * (1 - launchProgress * 0.55));
    if (alpha <= 0.01) return;

    const samples = qualityTier === "low" ? 4 : 7;
    for (let i = 0; i <= samples; i++) {
        const t = i / samples;
        const x = state.apexX + (state.endX - state.apexX) * t;
        const y = state.apexY + (state.endY - state.apexY) * t;
        spawnParticle(x, y, 0, 0, 0.82, 0.95, 1, alpha * (0.45 + Math.sin((t + now * 0.001) * Math.PI) * 0.2), randomBetween(2.5, 4.5), randomBetween(0.16, 0.28), kinds.plasma);
    }
}

function emitPatriotTrail(state, missileProgress, now) {
    const previous = clamp(Number(state.lastMissileProgress ?? 0), 0, missileProgress);
    const span = Math.max(0.001, missileProgress - previous);
    const steps = Math.max(1, Math.min(8, Math.ceil(span * 48)));
    for (let i = 0; i <= steps; i++) {
        const t = previous + span * (i / steps);
        const point = pointOnPatriotCurve(state, t);
        const next = pointOnPatriotCurve(state, Math.min(1, t + 0.012));
        const angle = Math.atan2(next.y - point.y, next.x - point.x);
        const backX = Math.cos(angle + Math.PI);
        const backY = Math.sin(angle + Math.PI);

        spawnParticle(point.x, point.y, backX * randomBetween(16, 42), backY * randomBetween(16, 42), 0.72, 0.9, 1, randomBetween(0.34, 0.62), randomBetween(3, 7), randomBetween(0.16, 0.36), kinds.plasma);
        if (!reducedMotion && i % 2 === 0) {
            spawnParticle(point.x + randomBetween(-3, 3), point.y + randomBetween(-3, 3), backX * randomBetween(18, 54) + currentWind * 0.18, backY * randomBetween(18, 54), 0.62, 0.72, 0.78, 0.18, randomBetween(8, 18), randomBetween(0.45, 0.85), kinds.smoke);
        }
    }
}

function spawnPatriotInterceptBurst(state) {
    spawnRadialEffect(state.endX, state.endY, 112, 0.28, radialKinds.flash, 1.1, [0.82, 0.96, 1, 0.5], { softness: 0.5, seed: state.seed + 17 });
    spawnRadialEffect(state.endX, state.endY, 145, 0.75, radialKinds.shockwave, 0.86, [0.58, 0.86, 1, 0.58], { softness: 0.14, seed: state.seed + 33 });

    const sparks = scaledCount(72, 26, 110);
    for (let i = 0; i < sparks; i++) {
        const angle = randomBetween(-Math.PI, Math.PI);
        const speed = randomBetween(40, 230);
        spawnParticle(state.endX, state.endY, Math.cos(angle) * speed + currentWind * 0.18, Math.sin(angle) * speed, 0.78, 0.94, 1, randomBetween(0.42, 0.78), randomBetween(2.5, 7), randomBetween(0.28, 0.82), i % 4 === 0 ? kinds.spark : kinds.plasma);
    }

    const vapor = scaledCount(38, 10, 62);
    for (let i = 0; i < vapor; i++) {
        const angle = randomBetween(-Math.PI, Math.PI);
        const distance = randomBetween(4, 28);
        spawnParticle(
            state.endX + Math.cos(angle) * distance,
            state.endY + Math.sin(angle) * distance,
            Math.cos(angle) * randomBetween(10, 80) + currentWind * randomBetween(0.25, 0.75),
            Math.sin(angle) * randomBetween(10, 80) - randomBetween(6, 32),
            0.62,
            0.74,
            0.82,
            randomBetween(0.12, 0.28),
            randomBetween(14, 34),
            randomBetween(0.75, 1.6),
            kinds.smoke);
    }
}

function pointOnPatriotCurve(state, t) {
    const clamped = clamp(t, 0, 1);
    return {
        x: quadraticScalar(state.startX, state.controlX, state.endX, clamped),
        y: quadraticScalar(state.startY, state.controlY, state.endY, clamped)
    };
}

function createPatriotPlayback(points, intercept) {
    const trimmed = trimTrailToIntercept(points, intercept);
    const apexIndex = findPatriotApexIndex(trimmed);
    const apex = trimmed[apexIndex] ?? trimmed[0] ?? intercept;
    const apexProgress = clamp((apexIndex + 1) / Math.max(1, trimmed.length), 0.08, 1);
    return {
        points: trimmed,
        apexX: apex.x,
        apexY: apex.y,
        apexProgress,
        holdStartProgress: 0.48,
        holdEndProgress: 0.68,
        lockProgressStart: 0.22,
        launchProgressStart: 0.72,
        interceptX: intercept.x,
        interceptY: intercept.y
    };
}

function trimTrailToIntercept(points, intercept) {
    if (!points.length) return [intercept];

    let bestIndex = 0;
    let bestDistance = Number.MAX_VALUE;
    for (let i = 0; i < points.length; i++) {
        const point = points[i];
        const dx = Number(point.x ?? 0) - intercept.x;
        const dy = Number(point.y ?? 0) - intercept.y;
        const distance = (dx * dx) + (dy * dy);
        if (distance < bestDistance) {
            bestDistance = distance;
            bestIndex = i;
        }
    }

    const trimmed = points.slice(0, Math.min(points.length, bestIndex + 1));
    const last = trimmed[trimmed.length - 1];
    if (!last || ((Number(last.x ?? 0) - intercept.x) ** 2) + ((Number(last.y ?? 0) - intercept.y) ** 2) > 0.01) {
        trimmed.push(intercept);
    }

    return trimmed;
}

function findPatriotApexIndex(points) {
    let apexIndex = 0;
    let apexY = Number.POSITIVE_INFINITY;
    for (let i = 0; i < points.length; i++) {
        const y = Number(points[i]?.y ?? Number.POSITIVE_INFINITY);
        if (y < apexY) {
            apexY = y;
            apexIndex = i;
        }
    }

    return apexIndex;
}

function patriotShotDuration(pointCount, weaponId) {
    const id = normalized(weaponId);
    let baseDuration;
    if (id.includes("dark-eagle")) {
        baseDuration = 2900;
    } else if (id.includes("shahed") || id.includes("drone")) {
        baseDuration = Math.min(3400, Math.max(1500, pointCount * 13));
    } else if (id.includes("splitter") || id.includes("mirv")) {
        baseDuration = Math.min(1800, Math.max(900, pointCount * 5.5));
    } else if (id.includes("gbu") || id.includes("mop")) {
        baseDuration = Math.min(2100, Math.max(900, pointCount * 8.5));
    } else {
        baseDuration = Math.min(1200, Math.max(260, pointCount * 4));
    }

    return clamp(baseDuration * patriotInterceptDurationScale, patriotInterceptMinDuration, patriotInterceptMaxDuration);
}

function quadraticScalar(start, control, end, t) {
    const inverse = 1 - t;
    return (inverse * inverse * start) + (2 * inverse * t * control) + (t * t * end);
}

function spawnImpactEffects(payload) {
    const explosions = payload?.explosions ?? [];
    const wind = Number(payload?.wind ?? currentWind);
    for (const explosion of explosions) {
        spawnExplosion(explosion, payload, wind);
    }

    if (payload?.shieldHit) {
        spawnShieldRipple(payload);
    }
}

function spawnExplosion(explosion, payload, wind) {
    const x = Number(explosion.x ?? 0);
    const y = Number(explosion.y ?? 0);
    const radius = Math.max(12, Number(explosion.radius ?? 36));
    const terrainRadius = Math.max(radius * 0.6, Number(explosion.terrainRadius ?? radius));
    if (isPatriotExplosion(explosion, payload)) {
        if (!truthyPayloadValue(payload, "patriotOverlayEnabled")) return;
        spawnPatriotImpactExplosion(x, y, radius, wind);
        return;
    }

    const preset = resolveExplosionPreset(explosion, payload);
    const nuclear = preset === explosionPresets.nuclear;
    const doomsday = isDoomsdayExplosion(explosion, payload, radius);
    const lava = preset === explosionPresets.lava;
    const laser = preset === explosionPresets.laser;
    const penetrator = preset === explosionPresets.penetrator;
    const nukeBoost = doomsday ? 1.32 : 1;

    spawnParticle(x, y, 0, 0, preset.flashColor[0], preset.flashColor[1], preset.flashColor[2], preset.flashColor[3], radius * (nuclear ? 3.35 * nukeBoost : 1.35), nuclear ? 1.05 : 0.48, kinds.flash);
    spawnParticle(x, y, 0, 0, preset.shockColor[0], preset.shockColor[1], preset.shockColor[2], preset.shockColor[3], radius * (nuclear ? 2.05 * nukeBoost : 0.98), nuclear ? 1.65 : 0.75, kinds.shockwave);

    spawnExplosionRadials(x, y, radius, terrainRadius, wind, preset, { nuclear, doomsday, lava, laser, penetrator });
    spawnExplosionDebris(x, y, radius, terrainRadius, wind, preset, { nuclear, doomsday, lava, laser, penetrator });
    spawnExplosionSmoke(x, y, radius, terrainRadius, wind, preset, { nuclear, doomsday, lava, penetrator });

    if (nuclear) {
        spawnNuclearColumn(x, y, radius, terrainRadius, wind, preset, doomsday);
    } else if (lava) {
        spawnLavaBurst(x, y, radius, wind);
    } else if (laser) {
        spawnLaserPlasma(x, y, radius, wind);
    }
}

function resolveExplosionPreset(explosion, payload) {
    const visual = explosionIdentity(explosion, payload);
    if (Boolean(explosion?.nuclear) || visual.includes("nuclear") || visual.includes("nuke") || visual.includes("doomsday")) {
        return explosionPresets.nuclear;
    }

    if (visual.includes("lava") || visual.includes("fire") || visual.includes("napalm")) {
        return explosionPresets.lava;
    }

    if (visual.includes("laser")) {
        return explosionPresets.laser;
    }

    if (visual.includes("drone") || visual.includes("shahed")) {
        return explosionPresets.drone;
    }

    if (Boolean(explosion?.dirt) || visual.includes("dirt") || visual.includes("excavator")) {
        return explosionPresets.dirt;
    }

    if (visual.includes("penetrator") || visual.includes("mop") || visual.includes("bunker") || visual.includes("gbu")) {
        return explosionPresets.penetrator;
    }

    if (visual.includes("missile") || visual.includes("dark") || visual.includes("eagle")) {
        return explosionPresets.missile;
    }

    return explosionPresets.ballistic;
}

function explosionIdentity(explosion, payload) {
    return normalized(`${explosion?.visualKind ?? ""} ${payload?.visualKind ?? ""} ${payload?.weaponId ?? ""}`);
}

function isDoomsdayExplosion(explosion, payload, radius) {
    const visual = explosionIdentity(explosion, payload);
    return visual.includes("doomsday") || Number(radius ?? explosion?.radius ?? 0) >= 165;
}

function isPatriotExplosion(explosion, payload) {
    const visual = explosionIdentity(explosion, payload);
    return visual.includes("patriot") || Boolean(truthyPayloadValue(payload, "intercepted") && Number(explosion?.terrainRadius ?? 0) <= 0);
}

function spawnPatriotImpactExplosion(x, y, radius, wind) {
    spawnRadialEffect(x, y, radius * 3.2, 0.42, radialKinds.flash, 0.92, [0.76, 0.94, 1, 0.46], { softness: 0.42, seed: randomBetween(0, 1000) });
    spawnRadialEffect(x, y, radius * 4.4, 0.84, radialKinds.shockwave, 0.7, [0.58, 0.86, 1, 0.52], { softness: 0.16, seed: randomBetween(0, 1000) });

    for (let i = 0; i < scaledCount(radius * 1.6, 18, 90); i++) {
        const angle = randomBetween(-Math.PI, Math.PI);
        const speed = randomBetween(45, 210);
        spawnParticle(x, y, Math.cos(angle) * speed + wind * 0.14, Math.sin(angle) * speed, 0.78, 0.94, 1, randomBetween(0.34, 0.72), randomBetween(2.5, 6), randomBetween(0.24, 0.72), i % 3 === 0 ? kinds.spark : kinds.plasma);
    }

    for (let i = 0; i < scaledCount(radius * 0.8, 8, 36); i++) {
        const angle = randomBetween(-Math.PI, Math.PI);
        spawnParticle(
            x + Math.cos(angle) * randomBetween(0, radius * 0.6),
            y + Math.sin(angle) * randomBetween(0, radius * 0.35),
            Math.cos(angle) * randomBetween(8, 62) + wind * randomBetween(0.2, 0.7),
            Math.sin(angle) * randomBetween(8, 62) - randomBetween(6, 28),
            0.58,
            0.7,
            0.78,
            randomBetween(0.1, 0.22),
            randomBetween(12, 28),
            randomBetween(0.72, 1.5),
            kinds.smoke);
    }
}

function spawnExplosionRadials(x, y, radius, terrainRadius, wind, preset, flags) {
    if (reducedMotion && !flags.nuclear) return;

    const glow = preset.glowScale;
    if (flags.nuclear) {
        const boost = flags.doomsday ? 1.35 : 1;
        const motionScale = reducedMotion ? 0.62 : 1;
        const worldWidth = Number(currentWorld?.width ?? 1200);
        const worldHeight = Number(currentWorld?.height ?? 700);
        const screenRadius = Math.max(worldWidth, worldHeight) * (flags.doomsday ? 1.18 : 0.92);
        spawnRadialEffect(worldWidth * 0.5, worldHeight * 0.48, screenRadius, 0.46 * motionScale, radialKinds.flash, 1.65 * glow * boost, [1, 0.96, 0.76, flags.doomsday ? 0.54 : 0.38], { wind: 0, softness: 0.92, seed: randomBetween(0, 1000) });
        spawnRadialEffect(x, y, radius * 2.25 * boost, 0.62 * motionScale, radialKinds.flash, 1.4 * glow * boost, [1, 0.95, 0.66, 0.74], { wind, softness: 0.52, seed: randomBetween(0, 1000) });
        spawnRadialEffect(x, y, radius * 3.55 * boost, 0.95 * motionScale, radialKinds.shockwave, 1.55 * glow * boost, [1, 0.92, 0.58, 0.74], { wind, softness: 0.16, seed: randomBetween(0, 1000) });
        spawnRadialEffect(x, y, radius * 5.25 * boost, 1.6 * motionScale, radialKinds.shockwave, 1.25 * glow * boost, [0.98, 0.82, 0.44, 0.56], { wind, softness: 0.18, seed: randomBetween(0, 1000) });
        spawnRadialEffect(x, y, radius * 4.65 * boost, 4.8 * motionScale, radialKinds.dust, 0.98 * boost, [0.62, 0.52, 0.37, flags.doomsday ? 0.46 : 0.36], { wind, softness: 0.48, aspect: 1.2, seed: randomBetween(0, 1000) });
        spawnRadialEffect(x, y, radius * 3.15 * boost, 7.2 * motionScale, radialKinds.radiation, 0.66 * boost, [0.72, 1, 0.3, flags.doomsday ? 0.32 : 0.22], { wind, softness: 0.6, aspect: 1.34, seed: randomBetween(0, 1000) });
        return;
    }

    spawnRadialEffect(x, y, radius * (flags.laser ? 1.6 : 2.1), flags.laser ? 0.38 : 0.62, radialKinds.flash, 0.45 * glow, preset.flashColor, { wind, softness: 0.42, seed: randomBetween(0, 1000) });

    if (flags.lava) {
        spawnRadialEffect(x, y, terrainRadius * 1.65, 2.4, radialKinds.heat, 0.7 * glow, [1, 0.42, 0.08, 0.32], { wind, softness: 0.5, aspect: 1.22, seed: randomBetween(0, 1000) });
    } else if (!flags.laser) {
        spawnRadialEffect(x, y, radius * 2.7, 0.72, radialKinds.shockwave, 0.42 * glow, preset.shockColor, { wind, softness: 0.2, seed: randomBetween(0, 1000) });
    }
}

function spawnExplosionDebris(x, y, radius, terrainRadius, wind, preset, flags) {
    if (flags.laser) return;

    const nuclearBoost = flags.doomsday ? 1.34 : 1;
    const count = scaledCount(radius * preset.debrisScale * (flags.nuclear ? 1.45 * nuclearBoost : 1), 8, flags.nuclear ? (flags.doomsday ? 340 : 240) : 130);
    for (let i = 0; i < count; i++) {
        const angle = randomBetween(-Math.PI, Math.PI);
        const speed = randomBetween(radius * 0.65, radius * (flags.nuclear ? 3.35 : flags.penetrator ? 2.45 : 2.05));
        const upward = Math.sin(angle) < 0 ? 1.2 : 0.72;
        const vx = Math.cos(angle) * speed + wind * randomBetween(0.15, flags.nuclear ? 1.25 : 0.9);
        const vy = Math.sin(angle) * speed * upward - randomBetween(18, flags.nuclear ? 175 : 80);
        const color = preset.debrisColor;
        const kind = flags.lava && Math.random() < 0.62 ? kinds.ember : preset.debrisKind;
        spawnParticle(x, y, vx, vy, color[0], color[1], color[2], randomBetween(0.38, 0.78), randomBetween(3, flags.nuclear ? 13 : 9), randomBetween(0.58, flags.nuclear ? 2.3 : 1.55), kind);
    }

    const rimSamples = scaledCount(terrainRadius * (flags.doomsday ? 0.46 : 0.34), 8, flags.nuclear ? (flags.doomsday ? 112 : 72) : 38);
    for (let i = 0; i < rimSamples; i++) {
        const side = i % 2 === 0 ? -1 : 1;
        const px = x + side * randomBetween(terrainRadius * 0.42, terrainRadius * 1.12);
        const py = surfaceY(px);
        const slope = surfaceY(px + 4) - surfaceY(px - 4);
        const color = preset.debrisColor;
        spawnParticle(
            px,
            py - randomBetween(0, 5),
            wind * randomBetween(0.5, 1.2) + side * randomBetween(18, 92) - slope * randomBetween(1.2, 2.8),
            randomBetween(-92, -12),
            color[0],
            color[1],
            color[2],
            randomBetween(0.32, 0.58),
            randomBetween(3, flags.nuclear ? 10 : 7),
            randomBetween(0.75, flags.nuclear ? 2.1 : 1.45),
            kinds.debris);
    }
}

function spawnExplosionSmoke(x, y, radius, terrainRadius, wind, preset, flags) {
    const nuclearBoost = flags.doomsday ? 1.32 : 1;
    const count = scaledCount(radius * preset.smokeScale * (flags.nuclear ? 1.35 * nuclearBoost : 0.82), 8, flags.nuclear ? (flags.doomsday ? 260 : 180) : 84);
    const smoke = preset.smokeColor;
    for (let i = 0; i < count; i++) {
        const angle = randomBetween(-Math.PI, Math.PI);
        const distance = randomBetween(0, terrainRadius * (flags.nuclear ? 0.45 : 0.32));
        const speed = randomBetween(5, radius * (flags.nuclear ? 0.85 : 0.62));
        const alpha = flags.nuclear ? randomBetween(0.2, flags.doomsday ? 0.5 : 0.4) : randomBetween(0.12, 0.31);
        spawnParticle(
            x + Math.cos(angle) * distance,
            y + Math.sin(angle) * distance * 0.56,
            Math.cos(angle) * speed + wind * randomBetween(0.55, flags.nuclear ? 2.1 : 1.55),
            Math.sin(angle) * speed - randomBetween(10, flags.nuclear ? 108 : 44),
            smoke[0],
            smoke[1],
            smoke[2],
            alpha,
            randomBetween(18, flags.nuclear ? (flags.doomsday ? 108 : 88) : 44),
            randomBetween(1.15, flags.nuclear ? (flags.doomsday ? 6.4 : 5.2) : 2.8),
            preset.smokeKind);
    }
}

function spawnNuclearColumn(x, y, radius, terrainRadius, wind, preset, doomsday) {
    const boost = doomsday ? 1.34 : 1;
    const stemCount = scaledCount(radius * 0.95 * boost, 18, doomsday ? 175 : 120);
    const smoke = preset.smokeColor;
    for (let i = 0; i < stemCount; i++) {
        const rise = randomBetween(0, radius * (doomsday ? 3.2 : 2.5));
        const spread = randomBetween(4, radius * 0.28 + rise * 0.08);
        spawnParticle(
            x + randomBetween(-spread, spread),
            y - rise * randomBetween(0.12, 0.34),
            wind * randomBetween(0.35, 1.25) + randomBetween(-12, 12),
            -randomBetween(38, doomsday ? 162 : 132),
            smoke[0],
            smoke[1],
            smoke[2],
            randomBetween(0.2, doomsday ? 0.42 : 0.34),
            randomBetween(34, doomsday ? 118 : 94),
            randomBetween(2.5, doomsday ? 7.4 : 6.2),
            kinds.smoke);
    }

    const capCount = scaledCount(radius * 0.95 * boost, 16, doomsday ? 160 : 105);
    for (let i = 0; i < capCount; i++) {
        const angle = randomBetween(-Math.PI, Math.PI);
        const distance = randomBetween(radius * 0.25, radius * (doomsday ? 1.75 : 1.35));
        spawnParticle(
            x + Math.cos(angle) * distance,
            y - terrainRadius * randomBetween(0.9, doomsday ? 2.15 : 1.7) + Math.sin(angle) * radius * 0.24,
            Math.cos(angle) * randomBetween(18, doomsday ? 96 : 74) + wind * randomBetween(0.8, 2.5),
            randomBetween(-38, 16),
            0.42,
            0.38,
            0.31,
            randomBetween(0.16, doomsday ? 0.36 : 0.28),
            randomBetween(38, doomsday ? 128 : 96),
            randomBetween(2.6, doomsday ? 7.2 : 6.5),
            kinds.smoke);
    }

    const ashCount = scaledCount(radius * 0.9 * boost, 18, doomsday ? 145 : 90);
    for (let i = 0; i < ashCount; i++) {
        spawnParticle(
            x + randomBetween(-radius * (doomsday ? 4.4 : 3.2), radius * (doomsday ? 4.4 : 3.2)),
            y - randomBetween(radius * 1.1, radius * (doomsday ? 4.5 : 3.6)),
            wind * randomBetween(1, doomsday ? 3.4 : 2.8) + randomBetween(-20, 20),
            randomBetween(18, doomsday ? 94 : 78),
            0.64,
            0.62,
            0.54,
            randomBetween(0.14, doomsday ? 0.32 : 0.26),
            randomBetween(2.5, doomsday ? 7.5 : 6.5),
            randomBetween(3.5, doomsday ? 8.8 : 7.8),
            kinds.radiation);
    }
}

function spawnLavaBurst(x, y, radius, wind) {
    for (let i = 0; i < scaledCount(radius * 1.05, 18, 150); i++) {
        const angle = randomBetween(-Math.PI, 0);
        const speed = randomBetween(18, radius * 1.85);
        spawnParticle(x, y, Math.cos(angle) * speed + wind * 0.6, Math.sin(angle) * speed - randomBetween(18, 104), 1, randomBetween(0.25, 0.62), 0.08, randomBetween(0.46, 0.84), randomBetween(3, 9), randomBetween(0.65, 1.95), kinds.ember);
    }

    for (let i = 0; i < scaledCount(radius * 0.5, 8, 44); i++) {
        spawnParticle(x + randomBetween(-radius, radius), y + randomBetween(-radius * 0.2, radius * 0.35), wind * 0.2 + randomBetween(-6, 6), randomBetween(-12, 2), 1, 0.36, 0.12, 0.16, randomBetween(22, 56), randomBetween(0.8, 1.8), kinds.heat);
    }
}

function spawnLaserPlasma(x, y, radius, wind) {
    for (let i = 0; i < scaledCount(radius * 1.25, 14, 96); i++) {
        const angle = randomBetween(-Math.PI, Math.PI);
        const speed = randomBetween(40, 230);
        spawnParticle(x, y, Math.cos(angle) * speed + wind * 0.25, Math.sin(angle) * speed, 1, randomBetween(0.2, 0.42), randomBetween(0.32, 0.72), randomBetween(0.48, 0.88), randomBetween(2, 7), randomBetween(0.2, 0.75), kinds.plasma);
    }
}

function spawnShieldRipple(payload) {
    if (!currentScene) return;

    const target = payload.ownerTankId === "player" ? currentScene.cpu : currentScene.player;
    if (!target) return;

    const x = Number(target.x ?? 0);
    const y = Number(target.y ?? 0) - 62;
    spawnParticle(x, y, 0, 0, 0.42, 0.82, 1, 0.62, 78, 0.75, kinds.shield);
    spawnParticle(x, y, 0, 0, 0.78, 0.96, 1, 0.5, 52, 0.45, kinds.shield);
    for (let i = 0; i < scaledCount(34, 14, 42); i++) {
        const angle = randomBetween(-Math.PI, Math.PI);
        const rx = Math.cos(angle) * randomBetween(34, 76);
        const ry = Math.sin(angle) * randomBetween(22, 58);
        spawnParticle(x + rx, y + ry, Math.cos(angle) * 34, Math.sin(angle) * 22, 0.6, 0.9, 1, 0.62, randomBetween(3, 7), randomBetween(0.35, 0.8), kinds.plasma);
    }
}

function emitAmbient(dt, now) {
    if (!currentScene || reducedMotion || diagnostics.disableAmbient) return;

    beginSpawnBatch(now);
    ambientAccumulator += dt;
    if (ambientAccumulator < 0.016) {
        endSpawnBatch();
        return;
    }
    const elapsed = Math.min(0.08, ambientAccumulator);
    ambientAccumulator = 0;

    const weather = normalized(currentWeather?.type);
    const intensity = clamp(Number(currentWeather?.intensity ?? 0.35), 0, 1);
    if (weather === "rain" || weather === "storm") {
        if (weather === "storm" && Math.random() < elapsed * 0.35 * intensity) {
            spawnParticle(randomBetween(160, currentWorld.width - 160), randomBetween(60, 220), 0, 0, 0.72, 0.86, 1, 0.22, randomBetween(280, 520), randomBetween(0.16, 0.32), kinds.flash);
            spawnRadialEffect(randomBetween(160, currentWorld.width - 160), randomBetween(80, 240), randomBetween(260, 520), randomBetween(0.18, 0.34), radialKinds.flash, 0.45, [0.72, 0.86, 1, 0.16], { softness: 0.72, wind: currentWind });
        }
    }

    const windStrength = Math.abs(currentWind);
    if (windStrength > 4) {
        emitCount(scaledDensity(windStrength * 0.7, true) * elapsed, spawnWindDust);
    }

    const zones = currentScene.radiation ?? [];
    for (const zone of zones) {
        const rate = scaledDensity(zone.lava ? 28 : 18, true);
        emitCount(rate * elapsed, () => spawnRadiation(zone));
    }
    endSpawnBatch();
}

function spawnWindDust() {
    const fromLeft = currentWind > 0;
    const x = fromLeft ? randomBetween(-30, 80) : randomBetween(currentWorld.width - 80, currentWorld.width + 30);
    const y = surfaceY(x) - randomBetween(4, 30);
    spawnParticle(x, y, currentWind * randomBetween(2.6, 4.8), randomBetween(-12, 8), 0.74, 0.62, 0.42, 0.18, randomBetween(10, 24), randomBetween(1.5, 3.2), kinds.smoke);
}

function spawnRadiation(zone) {
    const radius = Number(zone.radius ?? 32);
    const angle = randomBetween(-Math.PI, Math.PI);
    const distance = radius * Math.sqrt(Math.random());
    const x = Number(zone.x ?? 0) + Math.cos(angle) * distance;
    const y = Number(zone.y ?? 0) + Math.sin(angle) * distance * 0.55;
    if (zone.lava) {
        spawnParticle(x, y, currentWind * 0.25 + randomBetween(-18, 18), randomBetween(-46, -8), 1, randomBetween(0.24, 0.58), 0.08, 0.44, randomBetween(3, 7), randomBetween(0.6, 1.4), kinds.ember);
        if (Math.random() < 0.32) spawnParticle(x, y, currentWind * 0.16, randomBetween(-12, 0), 1, 0.36, 0.12, 0.12, randomBetween(20, 44), randomBetween(0.8, 1.8), kinds.heat);
    } else {
        spawnParticle(x, y, currentWind * 0.18 + randomBetween(-10, 10), randomBetween(-24, -4), 0.44, 1, 0.38, 0.28, randomBetween(4, 10), randomBetween(0.85, 1.8), kinds.radiation);
    }
}

function spawnParticle(x, y, vx, vy, r, g, b, a, size, lifetime, kind) {
    if (!enabled || !device || !particleBuffer) return;

    const index = writeIndex;
    writeIndex = (writeIndex + 1) % maxParticles;
    const offset = index * particleFloats;
    particleData[offset] = x;
    particleData[offset + 1] = y;
    particleData[offset + 2] = vx;
    particleData[offset + 3] = vy;
    particleData[offset + 4] = clamp(r, 0, 1);
    particleData[offset + 5] = clamp(g, 0, 1);
    particleData[offset + 6] = clamp(b, 0, 1);
    particleData[offset + 7] = clamp(a, 0, 1);
    particleData[offset + 8] = 0;
    particleData[offset + 9] = Math.max(0.05, lifetime);
    particleData[offset + 10] = Math.max(1, size);
    particleData[offset + 11] = kind;
    expirations[index] = (spawnBatchNow || performance.now()) + (lifetime * 1000);
    markParticleDirty(index);
    spawnCount++;
}

function beginSpawnBatch(now = performance.now()) {
    spawnBatchNow = now;
}

function endSpawnBatch() {
    spawnBatchNow = 0;
}

function markParticleDirty(index) {
    if (particleDirtyStart < 0) {
        particleDirtyStart = index;
        particleDirtyEnd = index;
        particleDirtyWrapped = false;
        return;
    }

    if (particleDirtyWrapped) {
        particleDirtyEnd = Math.max(particleDirtyEnd, index);
        return;
    }

    if (index >= particleDirtyStart) {
        particleDirtyEnd = Math.max(particleDirtyEnd, index);
    } else {
        particleDirtyWrapped = true;
        particleDirtyEnd = index;
    }
}

function flushParticleWrites() {
    if (!particleBuffer || particleDirtyStart < 0) return;

    if (particleDirtyWrapped) {
        const tailCount = maxParticles - particleDirtyStart;
        if (tailCount > 0) {
            device.queue.writeBuffer(particleBuffer, particleDirtyStart * particleStride, particleData, particleDirtyStart * particleFloats, tailCount * particleFloats);
        }

        const headCount = particleDirtyEnd + 1;
        if (headCount > 0) {
            device.queue.writeBuffer(particleBuffer, 0, particleData, 0, headCount * particleFloats);
        }
    } else {
        const count = particleDirtyEnd - particleDirtyStart + 1;
        device.queue.writeBuffer(particleBuffer, particleDirtyStart * particleStride, particleData, particleDirtyStart * particleFloats, count * particleFloats);
    }

    particleDirtyStart = -1;
    particleDirtyEnd = -1;
    particleDirtyWrapped = false;
}

function spawnRadialEffect(x, y, radius, duration, type, intensity, color, options = {}) {
    if (!enabled || !device || !radialBuffer) return;

    const index = radialWriteIndex;
    radialWriteIndex++;
    if (radialWriteIndex >= radialTransientEnd) radialWriteIndex = persistentRadialSlots;

    writeRadialSlot(index, {
        x,
        y,
        radius,
        duration,
        type,
        intensity,
        color,
        wind: options.wind ?? currentWind,
        softness: options.softness ?? 0.2,
        aspect: options.aspect ?? 1,
        seed: options.seed ?? Math.random() * 1000
    });
}

function writeRadialSlot(index, effect) {
    if (index < 0 || index >= maxRadialEffects) return;

    const offset = index * radialFloats;
    const color = effect.color ?? [1, 1, 1, 0.4];
    const now = performance.now();
    radialData[offset] = Number(effect.x ?? 0);
    radialData[offset + 1] = Number(effect.y ?? 0);
    radialData[offset + 2] = Math.max(1, Number(effect.radius ?? 1));
    radialData[offset + 3] = now * 0.001;
    radialData[offset + 4] = clamp(Number(color[0] ?? 1), 0, 1);
    radialData[offset + 5] = clamp(Number(color[1] ?? 1), 0, 1);
    radialData[offset + 6] = clamp(Number(color[2] ?? 1), 0, 1);
    radialData[offset + 7] = clamp(Number(color[3] ?? 0.4), 0, 1);
    radialData[offset + 8] = Math.max(0.05, Number(effect.duration ?? 1));
    radialData[offset + 9] = Number(effect.type ?? radialKinds.glow);
    radialData[offset + 10] = Math.max(0, Number(effect.intensity ?? 1));
    radialData[offset + 11] = Number(effect.seed ?? Math.random() * 1000);
    radialData[offset + 12] = Number(effect.wind ?? currentWind);
    radialData[offset + 13] = Math.max(0.01, Number(effect.softness ?? 0.2));
    radialData[offset + 14] = Math.max(0.2, Number(effect.aspect ?? 1));
    radialData[offset + 15] = Number(effect.flags ?? 0);
    radialStartedAt[index] = now;
    radialExpirations[index] = now + radialData[offset + 8] * 1000;
    device.queue.writeBuffer(radialBuffer, index * radialStride, radialData, offset, radialFloats);
}

function clearRadialSlot(index) {
    const offset = index * radialFloats;
    for (let i = 0; i < radialFloats; i++) radialData[offset + i] = 0;
    radialExpirations[index] = 0;
    radialStartedAt[index] = 0;
}

function refreshActiveParticleIndices(now) {
    if (!activeParticleIndexBuffer) return 0;

    let count = 0;
    for (let i = 0; i < maxParticles; i++) {
        if (expirations[i] > now) {
            activeParticleIndices[count++] = i;
        }
    }

    activeParticleCount = count;
    cachedParticleCount = count;
    if (count > 0) {
        device.queue.writeBuffer(activeParticleIndexBuffer, 0, activeParticleIndices, 0, count);
    }

    return count;
}

function refreshActiveRadialIndices(now) {
    if (!activeRadialIndexBuffer) return 0;

    let count = 0;
    for (let i = 0; i < maxRadialEffects; i++) {
        const offset = i * radialFloats;
        if (radialData[offset + 8] <= 0) continue;

        if (radialExpirations[i] <= now) {
            clearRadialSlot(i);
            continue;
        }

        activeRadialIndices[count++] = i;
    }

    activeRadialCount = count;
    cachedRadialCount = count;
    if (count > 0) {
        device.queue.writeBuffer(activeRadialIndexBuffer, 0, activeRadialIndices, 0, count);
    }
    return count;
}

function syncRadiationZones(zones) {
    if (!device || !radialBuffer) return;

    for (let i = 0; i < persistentRadialSlots; i++) {
        const zone = zones[i];
        if (!zone) {
            clearRadialSlot(i);
            continue;
        }

        const radius = Math.max(10, Number(zone.radius ?? 32));
        const lava = Boolean(zone.lava);
        const turns = Math.max(1, Number(zone.turns ?? 1));
        writeRadialSlot(i, {
            x: Number(zone.x ?? 0),
            y: Number(zone.y ?? 0),
            radius: radius * (lava ? 1.08 : 1.15),
            duration: 9999,
            type: lava ? radialKinds.lava : radialKinds.radiation,
            intensity: clamp(0.38 + turns * 0.06 + radius / 420, 0.45, lava ? 0.9 : 0.78),
            color: lava ? [1, 0.36, 0.08, 0.32] : [0.5, 1, 0.28, 0.3],
            wind: currentWind,
            softness: lava ? 0.34 : 0.42,
            aspect: 1.25,
            seed: i * 97.31 + radius
        });
    }
}

function hasSourceSamplingRadials() {
    const now = performance.now();
    for (let i = 0; i < maxRadialEffects; i++) {
        const offset = i * radialFloats;
        if (radialData[offset + 8] <= 0 || radialExpirations[i] <= now) continue;

        const type = radialData[offset + 9];
        const intensity = radialData[offset + 10];
        if (isRadialKind(type, radialKinds.shockwave) || isRadialKind(type, radialKinds.heat) || isRadialKind(type, radialKinds.lava)) {
            return intensity > 0.08;
        }
    }

    return false;
}

function clearCpuState() {
    particleData.fill(0);
    expirations.fill(0);
    radialData.fill(0);
    radialExpirations.fill(0);
    radialStartedAt.fill(0);
    writeIndex = 0;
    radialWriteIndex = persistentRadialSlots;
    spawnCount = 0;
    activeRadialCount = 0;
    activeParticleCount = 0;
    cachedParticleCount = 0;
    cachedRadialCount = 0;
    particleDirtyStart = -1;
    particleDirtyEnd = -1;
    particleDirtyWrapped = false;
    if (device && particleBuffer) {
        device.queue.writeBuffer(particleBuffer, 0, particleData);
    }
    if (device && radialBuffer) {
        device.queue.writeBuffer(radialBuffer, 0, radialData);
    }
}

function updateQualityTier() {
    qualityFrameCounter++;
    if (diagnostics.forceQualityTier && qualityProfiles[diagnostics.forceQualityTier]) {
        setQualityTier(diagnostics.forceQualityTier);
        return;
    }

    if (qualityFrameCounter % 4 !== 0) return;

    const tooExpensive = frameMs > 22 || sourceCopyMs > 4 || postProcessMs > 3.5 || cachedParticleCount > maxParticles * 0.78;
    const comfortable = frameMs < 14.5 && sourceCopyMs < 1.8 && postProcessMs < 1.8 && cachedParticleCount < maxParticles * 0.5;

    if (tooExpensive) {
        qualityDebt++;
        qualityCredit = 0;
        if (qualityDebt > 3) {
            if (qualityTier === "high") setQualityTier("balanced");
            else if (qualityTier === "balanced") setQualityTier("low");
            qualityDebt = 0;
        }
    } else if (comfortable) {
        qualityCredit++;
        qualityDebt = Math.max(0, qualityDebt - 1);
        if (qualityCredit > 240) {
            if (qualityTier === "low") setQualityTier("balanced");
            else if (qualityTier === "balanced") setQualityTier("high");
            qualityCredit = 0;
        }
    } else {
        qualityDebt = Math.max(0, qualityDebt - 1);
        qualityCredit = Math.max(0, qualityCredit - 1);
    }

    const profile = qualityProfiles[qualityTier] ?? qualityProfiles.high;
    qualityScale = reducedMotion ? Math.min(profile.scale, 0.38) : profile.scale;
    qualityLevel = profile.level;
}

function setQualityTier(tier) {
    qualityTier = tier;
    const profile = qualityProfiles[tier] ?? qualityProfiles.high;
    qualityScale = reducedMotion ? Math.min(profile.scale, 0.38) : profile.scale;
    qualityLevel = profile.level;
}

function emitCount(value, callback) {
    const whole = Math.floor(value);
    const extra = Math.random() < value - whole ? 1 : 0;
    for (let i = 0; i < whole + extra; i++) callback();
}

function scaledCount(value, min, max) {
    const scale = reducedMotion ? 0.38 : qualityScale;
    return Math.floor(clamp(value * scale, min * scale, max * scale));
}

function scaledDensity(value, ambient = false) {
    const profile = qualityProfiles[qualityTier] ?? qualityProfiles.high;
    const scale = ambient ? profile.ambient : qualityScale;
    return value * (reducedMotion ? Math.min(scale, 0.38) : scale);
}

function surfaceY(x) {
    if (!terrainCache.length) return currentWorld.height * 0.72;

    const index = Math.max(0, Math.min(terrainCache.length - 1, Math.round(x)));
    return Number(terrainCache[index] ?? currentWorld.height * 0.72);
}

function normalized(value) {
    return String(value ?? "").toLowerCase();
}

function randomBetween(min, max) {
    return min + Math.random() * (max - min);
}

function clamp(value, min, max) {
    return Math.min(max, Math.max(min, value));
}

function isRadialKind(kind, target) {
    return Math.abs(kind - target) < 0.5;
}

function shortError(error) {
    const text = error?.message ?? String(error ?? "WebGPU initialization failed");
    return text.length > 96 ? `${text.slice(0, 93)}...` : text;
}

const computeShader = `
struct Particle {
    position: vec2f,
    velocity: vec2f,
    color: vec4f,
    meta: vec4f
};

struct Uniforms {
    dt: f32,
    wind: f32,
    gravity: f32,
    time: f32,
    canvasSize: vec2f,
    worldSize: vec2f,
    sourceReady: f32,
    qualityLevel: f32,
    radialCount: f32,
    activeParticleCount: f32,
    worldScale: f32,
    worldLeft: f32,
    worldTop: f32,
    invCanvasWidth: f32,
    invCanvasHeight: f32,
    weatherType: f32,
    weatherIntensity: f32,
    overlayScale: f32,
    canvasPixelRatio: f32,
    sourceScale: vec2f
};

@group(0) @binding(0) var<storage, read_write> particles: array<Particle>;
@group(0) @binding(1) var<uniform> uniforms: Uniforms;
@group(0) @binding(2) var<storage, read> activeIndices: array<u32>;

fn inKind(kind: f32, target: f32) -> bool {
    return abs(kind - target) < 0.5;
}

@compute @workgroup_size(${workgroupSize})
fn updateParticles(@builtin(global_invocation_id) id: vec3u) {
    if (id.x >= u32(uniforms.activeParticleCount)) {
        return;
    }
    let index = activeIndices[id.x];

    var particle = particles[index];
    if (particle.meta.y <= 0.0 || particle.meta.x >= particle.meta.y) {
        return;
    }

    let dt = uniforms.dt;
    let kind = particle.meta.w;
    particle.meta.x = particle.meta.x + dt;

    if (inKind(kind, 0.0) || inKind(kind, 2.0) || inKind(kind, 9.0) || inKind(kind, 11.0)) {
        particle.velocity.y = particle.velocity.y + uniforms.gravity * dt;
        particle.velocity.x = particle.velocity.x + uniforms.wind * 0.02 * dt;
    } else if (inKind(kind, 1.0)) {
        particle.velocity.x = particle.velocity.x + uniforms.wind * 0.08 * dt;
        particle.velocity.y = particle.velocity.y - 12.0 * dt;
        particle.meta.z = particle.meta.z + 10.0 * dt;
    } else if (inKind(kind, 5.0)) {
        particle.velocity.x = particle.velocity.x + uniforms.wind * 0.2 * dt;
    } else if (inKind(kind, 6.0)) {
        particle.velocity.x = particle.velocity.x + sin(uniforms.time + f32(index) * 0.37) * 18.0 * dt + uniforms.wind * 0.035 * dt;
    } else if (inKind(kind, 7.0) || inKind(kind, 8.0)) {
        particle.velocity.x = particle.velocity.x + uniforms.wind * 0.05 * dt;
        particle.velocity.y = particle.velocity.y - 8.0 * dt;
        particle.meta.z = particle.meta.z + 6.0 * dt;
    }

    particle.position = particle.position + particle.velocity * dt;
    if (particle.position.y > uniforms.worldSize.y + 120.0 || particle.position.x < -180.0 || particle.position.x > uniforms.worldSize.x + 180.0) {
        if (!inKind(kind, 5.0) && !inKind(kind, 6.0)) {
            particle.meta.x = particle.meta.y;
        }
    }

    particles[index] = particle;
}
`;

const renderShader = `
struct Particle {
    position: vec2f,
    velocity: vec2f,
    color: vec4f,
    meta: vec4f
};

struct Uniforms {
    dt: f32,
    wind: f32,
    gravity: f32,
    time: f32,
    canvasSize: vec2f,
    worldSize: vec2f,
    sourceReady: f32,
    qualityLevel: f32,
    radialCount: f32,
    activeParticleCount: f32,
    worldScale: f32,
    worldLeft: f32,
    worldTop: f32,
    invCanvasWidth: f32,
    invCanvasHeight: f32,
    weatherType: f32,
    weatherIntensity: f32,
    overlayScale: f32,
    canvasPixelRatio: f32,
    sourceScale: vec2f
};

struct VertexOut {
    @builtin(position) position: vec4f,
    @location(0) local: vec2f,
    @location(1) color: vec4f,
    @location(2) meta: vec4f
};

@group(0) @binding(0) var<storage, read> particles: array<Particle>;
@group(0) @binding(1) var<uniform> uniforms: Uniforms;
@group(0) @binding(2) var<storage, read> activeIndices: array<u32>;

const corners = array<vec2f, 6>(
    vec2f(-1.0, -1.0),
    vec2f(1.0, -1.0),
    vec2f(-1.0, 1.0),
    vec2f(-1.0, 1.0),
    vec2f(1.0, -1.0),
    vec2f(1.0, 1.0)
);

fn inKind(kind: f32, target: f32) -> bool {
    return abs(kind - target) < 0.5;
}

@vertex
fn vertexMain(@builtin(vertex_index) vertexIndex: u32, @builtin(instance_index) instanceIndex: u32) -> VertexOut {
    let particle = particles[activeIndices[instanceIndex]];
    let corner = corners[vertexIndex];
    let progress = clamp(particle.meta.x / max(particle.meta.y, 0.001), 0.0, 1.0);
    let kind = particle.meta.w;
    var size = particle.meta.z;
    if (inKind(kind, 3.0)) {
        size = size * (1.0 + progress * 3.2);
    } else if (inKind(kind, 4.0)) {
        size = size * (1.0 + progress * 2.4);
    } else if (inKind(kind, 7.0)) {
        size = size * (1.0 + progress * 1.5 + sin(uniforms.time * 8.0 + particle.position.x * 0.03) * 0.08);
    } else if (inKind(kind, 10.0)) {
        size = size * (1.0 + progress * 0.55);
    }

    if (particle.meta.y <= 0.0 || particle.meta.x >= particle.meta.y) {
        size = 0.0;
    }

    let worldPosition = particle.position + corner * size;
    let pixel = vec2f(uniforms.worldLeft + worldPosition.x * uniforms.worldScale, uniforms.worldTop + worldPosition.y * uniforms.worldScale);
    let clip = vec2f((pixel.x * uniforms.invCanvasWidth) * 2.0 - 1.0, 1.0 - (pixel.y * uniforms.invCanvasHeight) * 2.0);

    var output: VertexOut;
    output.position = vec4f(clip, 0.0, 1.0);
    output.local = corner;
    output.color = particle.color;
    output.meta = particle.meta;
    return output;
}

@fragment
fn fragmentMain(input: VertexOut) -> @location(0) vec4f {
    let kind = input.meta.w;
    let progress = clamp(input.meta.x / max(input.meta.y, 0.001), 0.0, 1.0);
    let d = length(input.local);
    var alpha = input.color.a * (1.0 - progress);
    var color = input.color.rgb;

    if (input.meta.y <= 0.0 || input.meta.x >= input.meta.y) {
        discard;
    }

    if (inKind(kind, 3.0) || inKind(kind, 4.0)) {
        let ringTarget = 0.52 + progress * 0.34;
        let ring = 1.0 - smoothstep(0.035, 0.18, abs(d - ringTarget));
        alpha = alpha * ring;
        color = mix(color, vec3f(1.0, 0.96, 0.78), 0.22);
    } else if (inKind(kind, 5.0)) {
        alpha = alpha * (1.0 - smoothstep(0.05, 0.42, abs(input.local.x))) * (1.0 - smoothstep(0.75, 1.18, abs(input.local.y)));
    } else if (inKind(kind, 6.0)) {
        alpha = alpha * smoothstep(1.05, 0.2, d);
    } else if (inKind(kind, 7.0)) {
        alpha = alpha * smoothstep(1.1, 0.15, d) * (0.45 + 0.35 * sin(input.local.x * 8.0 + uniforms.time * 10.0));
        color = mix(color, vec3f(1.0, 0.68, 0.22), 0.32);
    } else if (inKind(kind, 10.0)) {
        alpha = alpha * smoothstep(1.2, 0.05, d);
    } else {
        alpha = alpha * smoothstep(1.05, 0.35, d);
    }

    if (!inKind(kind, 5.0) && d > 1.18) {
        discard;
    }

    if (alpha < 0.004) {
        discard;
    }

    return vec4f(color * alpha, alpha);
}
`;

const postProcessShader = `
struct RadialEffect {
    centerRadiusStart: vec4f,
    color: vec4f,
    meta: vec4f,
    extra: vec4f
};

struct Uniforms {
    dt: f32,
    wind: f32,
    gravity: f32,
    time: f32,
    canvasSize: vec2f,
    worldSize: vec2f,
    sourceReady: f32,
    qualityLevel: f32,
    radialCount: f32,
    activeParticleCount: f32,
    worldScale: f32,
    worldLeft: f32,
    worldTop: f32,
    invCanvasWidth: f32,
    invCanvasHeight: f32,
    weatherType: f32,
    weatherIntensity: f32,
    overlayScale: f32,
    canvasPixelRatio: f32,
    sourceScale: vec2f
};

struct VertexOut {
    @builtin(position) position: vec4f,
    @location(0) local: vec2f,
    @location(1) uv: vec2f,
    @location(2) color: vec4f,
    @location(3) effect: vec4f,
    @location(4) meta: vec4f,
    @location(5) extra: vec4f
};

@group(0) @binding(0) var<storage, read> effects: array<RadialEffect>;
@group(0) @binding(1) var<uniform> uniforms: Uniforms;
@group(0) @binding(2) var sourceTexture: texture_2d<f32>;
@group(0) @binding(3) var sourceSampler: sampler;
@group(0) @binding(4) var<storage, read> activeRadialIndices: array<u32>;

const corners = array<vec2f, 6>(
    vec2f(-1.0, -1.0),
    vec2f(1.0, -1.0),
    vec2f(-1.0, 1.0),
    vec2f(-1.0, 1.0),
    vec2f(1.0, -1.0),
    vec2f(1.0, 1.0)
);

fn inKind(kind: f32, target: f32) -> bool {
    return abs(kind - target) < 0.5;
}

fn worldToPixel(worldPosition: vec2f) -> vec2f {
    return vec2f(uniforms.worldLeft + worldPosition.x * uniforms.worldScale, uniforms.worldTop + worldPosition.y * uniforms.worldScale);
}

fn hash21(p: vec2f) -> f32 {
    return fract(sin(dot(p, vec2f(127.1, 311.7))) * 43758.5453123);
}

fn noise(p: vec2f) -> f32 {
    let i = floor(p);
    let f = fract(p);
    let u = f * f * (3.0 - 2.0 * f);
    let a = hash21(i);
    let b = hash21(i + vec2f(1.0, 0.0));
    let c = hash21(i + vec2f(0.0, 1.0));
    let d = hash21(i + vec2f(1.0, 1.0));
    return mix(mix(a, b, u.x), mix(c, d, u.x), u.y);
}

@vertex
fn vertexMain(@builtin(vertex_index) vertexIndex: u32, @builtin(instance_index) instanceIndex: u32) -> VertexOut {
    let effect = effects[activeRadialIndices[instanceIndex]];
    let corner = corners[vertexIndex];
    let age = max(0.0, uniforms.time - effect.centerRadiusStart.w);
    let duration = effect.meta.x;
    let progress = clamp(age / max(duration, 0.001), 0.0, 1.0);
    let kind = effect.meta.y;
    var radius = effect.centerRadiusStart.z;

    if (duration <= 0.0 || age >= duration) {
        radius = 0.0;
    } else if (inKind(kind, 0.0)) {
        radius = radius * (0.28 + progress * 0.82);
    } else if (inKind(kind, 4.0)) {
        radius = radius * (0.45 + progress * 0.72);
    }

    let aspect = max(effect.extra.z, 0.2);
    let worldPosition = effect.centerRadiusStart.xy + vec2f(corner.x * radius * aspect, corner.y * radius);
    let pixel = worldToPixel(worldPosition);
    let clip = vec2f((pixel.x * uniforms.invCanvasWidth) * 2.0 - 1.0, 1.0 - (pixel.y * uniforms.invCanvasHeight) * 2.0);

    var output: VertexOut;
    output.position = vec4f(clip, 0.0, 1.0);
    output.local = corner;
    output.uv = pixel * vec2f(uniforms.invCanvasWidth, uniforms.invCanvasHeight);
    output.color = effect.color;
    output.effect = vec4f(effect.centerRadiusStart.xyz, age);
    output.meta = effect.meta;
    output.extra = effect.extra;
    return output;
}

@fragment
fn fragmentMain(input: VertexOut) -> @location(0) vec4f {
    let duration = input.meta.x;
    let kind = input.meta.y;
    let intensity = input.meta.z;
    let seed = input.meta.w;
    let age = input.effect.w;
    let progress = clamp(age / max(duration, 0.001), 0.0, 1.0);
    let d = length(input.local);
    var alpha = 0.0;
    var color = input.color.rgb;
    var uvOffset = vec2f(0.0, 0.0);
    var sourceMix = 0.0;

    if (duration <= 0.0 || age >= duration || d > 1.18) {
        discard;
    }

    if (inKind(kind, 0.0)) {
        let n = noise(input.local * 3.2 + vec2f(seed * 0.17 + uniforms.time * 0.34 + input.extra.x * 0.01, seed * 0.11 - uniforms.time * 0.21));
        let ring = 1.0 - smoothstep(0.026, 0.145, abs(d - 0.82));
        let ripple = 0.72 + 0.28 * n;
        alpha = input.color.a * intensity * ring * ripple * (1.0 - progress * 0.72);
        let direction = normalize(input.local + vec2f(0.001, 0.001));
        uvOffset = direction * ring * intensity * (0.006 + 0.011 * (1.0 - progress));
        sourceMix = 0.72;
        color = mix(color, vec3f(1.0, 0.95, 0.72), 0.28);
    } else if (inKind(kind, 1.0)) {
        let body = smoothstep(1.05, 0.0, d);
        alpha = input.color.a * intensity * body * body * (1.0 - progress);
        sourceMix = 0.18;
    } else if (inKind(kind, 7.0)) {
        let outer = 1.0 - smoothstep(0.018, 0.052, abs(d - 0.82));
        let inner = 1.0 - smoothstep(0.018, 0.048, abs(d - 0.46));
        let horizontal = (1.0 - smoothstep(0.012, 0.032, abs(input.local.y))) * smoothstep(0.42, 0.56, abs(input.local.x)) * (1.0 - smoothstep(0.92, 1.08, abs(input.local.x)));
        let vertical = (1.0 - smoothstep(0.012, 0.032, abs(input.local.x))) * smoothstep(0.42, 0.56, abs(input.local.y)) * (1.0 - smoothstep(0.92, 1.08, abs(input.local.y)));
        let sweepDirection = vec2f(cos(uniforms.time * 2.8 + seed), sin(uniforms.time * 2.8 + seed));
        let localDirection = normalize(input.local + vec2f(0.0001, 0.0001));
        let sweep = smoothstep(0.976, 1.0, dot(localDirection, sweepDirection)) * smoothstep(0.12, 0.86, d);
        alpha = input.color.a * intensity * clamp(outer + inner * 0.62 + horizontal * 0.52 + vertical * 0.52 + sweep * 0.5, 0.0, 1.15);
        color = mix(vec3f(0.46, 0.84, 1.0), vec3f(1.0, 0.98, 0.78), outer * 0.35 + sweep * 0.3);
    } else if (inKind(kind, 2.0)) {
        let n = noise(input.local * 3.2 + vec2f(seed * 0.17 + uniforms.time * 0.34 + input.extra.x * 0.01, seed * 0.11 - uniforms.time * 0.21));
        let body = smoothstep(1.02, 0.16, d);
        let boundary = 1.0 - smoothstep(0.025, 0.12, abs(d - (0.78 + sin(uniforms.time * 1.7 + seed) * 0.025)));
        let pulse = 0.72 + 0.28 * sin(uniforms.time * 2.4 + seed);
        alpha = input.color.a * intensity * (body * 0.36 + boundary * 0.72) * (0.72 + n * 0.28) * pulse;
        color = mix(vec3f(0.36, 1.0, 0.25), vec3f(0.88, 1.0, 0.34), n * 0.55 + boundary * 0.24);
    } else if (inKind(kind, 3.0) || inKind(kind, 5.0)) {
        let n = noise(input.local * 3.2 + vec2f(seed * 0.17 + uniforms.time * 0.34 + input.extra.x * 0.01, seed * 0.11 - uniforms.time * 0.21));
        let body = smoothstep(1.04, 0.08, d);
        let waves = 0.5 + 0.5 * sin((input.local.x + input.local.y) * 9.0 + uniforms.time * 8.0 + seed);
        alpha = input.color.a * intensity * body * (0.28 + 0.72 * n) * (0.55 + waves * 0.18);
        uvOffset = vec2f(sin(input.local.y * 10.0 + uniforms.time * 7.0 + seed), cos(input.local.x * 8.0 - uniforms.time * 5.0)) * intensity * body * 0.0045;
        sourceMix = 0.46;
        color = mix(color, vec3f(1.0, 0.54, 0.12), 0.34 + n * 0.24);
    } else if (inKind(kind, 4.0)) {
        let n = noise(input.local * 3.2 + vec2f(seed * 0.17 + uniforms.time * 0.34 + input.extra.x * 0.01, seed * 0.11 - uniforms.time * 0.21));
        let ring = 1.0 - smoothstep(0.08, 0.28, abs(d - 0.78));
        let body = smoothstep(1.0, 0.2, d) * (1.0 - progress * 0.35);
        alpha = input.color.a * intensity * (body * 0.24 + ring * 0.62) * (0.72 + n * 0.28) * (1.0 - progress * 0.55);
        color = mix(color, vec3f(0.66, 0.55, 0.38), 0.42);
    } else {
        let body = smoothstep(1.05, 0.08, d);
        alpha = input.color.a * intensity * body * (1.0 - progress * 0.7);
    }

    if (uniforms.qualityLevel <= 0.5) {
        alpha = alpha * 0.72;
        sourceMix = 0.0;
        uvOffset = vec2f(0.0, 0.0);
    }

    if (uniforms.sourceReady > 0.5 && sourceMix > 0.0) {
        let sampleUv = clamp(input.uv + uvOffset, vec2f(0.001, 0.001), vec2f(0.999, 0.999));
        let sampled = textureSampleLevel(sourceTexture, sourceSampler, sampleUv, 0.0).rgb;
        color = mix(color, sampled + color * 0.42, sourceMix);
    }

    alpha = clamp(alpha, 0.0, 0.78);
    if (alpha < 0.004) {
        discard;
    }

    return vec4f(color * alpha, alpha);
}
`;

const weatherShader = `
struct Uniforms {
    dt: f32,
    wind: f32,
    gravity: f32,
    time: f32,
    canvasSize: vec2f,
    worldSize: vec2f,
    sourceReady: f32,
    qualityLevel: f32,
    radialCount: f32,
    activeParticleCount: f32,
    worldScale: f32,
    worldLeft: f32,
    worldTop: f32,
    invCanvasWidth: f32,
    invCanvasHeight: f32,
    weatherType: f32,
    weatherIntensity: f32,
    overlayScale: f32,
    canvasPixelRatio: f32,
    sourceScale: vec2f
};

struct VertexOut {
    @builtin(position) position: vec4f,
    @location(0) uv: vec2f
};

@group(0) @binding(0) var<uniform> uniforms: Uniforms;

const corners = array<vec2f, 6>(
    vec2f(-1.0, -1.0),
    vec2f(1.0, -1.0),
    vec2f(-1.0, 1.0),
    vec2f(-1.0, 1.0),
    vec2f(1.0, -1.0),
    vec2f(1.0, 1.0)
);

fn hash21(p: vec2f) -> f32 {
    return fract(sin(dot(p, vec2f(127.1, 311.7))) * 43758.5453123);
}

@vertex
fn vertexMain(@builtin(vertex_index) vertexIndex: u32) -> VertexOut {
    let corner = corners[vertexIndex];
    var output: VertexOut;
    output.position = vec4f(corner, 0.0, 1.0);
    output.uv = corner * 0.5 + vec2f(0.5, 0.5);
    return output;
}

@fragment
fn fragmentMain(input: VertexOut) -> @location(0) vec4f {
    let weatherType = uniforms.weatherType;
    if (weatherType < 0.5) {
        discard;
    }

    let intensity = clamp(uniforms.weatherIntensity, 0.0, 1.0);
    let densityScale = select(0.45, 0.72, uniforms.qualityLevel > 1.5) * select(0.5, 1.0, uniforms.qualityLevel > 0.5);
    let pixel = input.uv * uniforms.canvasSize;
    let wind = uniforms.wind * 0.0018;
    var alpha = 0.0;
    var color = vec3f(0.72, 0.86, 1.0);

    if (weatherType < 1.5) {
        let stormBoost = select(1.0, 1.18, intensity > 0.62);
        let density = mix(18.0, 38.0, intensity) * densityScale * stormBoost;
        let cell = vec2f(18.0, 58.0);
        let rainOffset = vec2f(uniforms.time * uniforms.wind * 22.0, uniforms.time * 560.0);
        let grid = floor((pixel + rainOffset) / cell);
        let local = fract((pixel + rainOffset) / cell);
        let seed = hash21(grid);
        let xTarget = 0.2 + seed * 0.6;
        let streak = (1.0 - smoothstep(0.025, 0.11, abs(local.x - xTarget))) * smoothstep(0.02, 0.18, local.y) * (1.0 - smoothstep(0.42, 0.98, local.y));
        alpha = streak * step(seed, density / 100.0) * (0.045 + intensity * 0.075);
        color = vec3f(0.58, 0.76, 1.0);
    } else {
        let cell = mix(26.0, 18.0, intensity) / max(0.55, densityScale);
        let drift = vec2f(uniforms.time * (uniforms.wind * 10.0 + 16.0), uniforms.time * 34.0);
        let grid = floor((pixel + drift) / cell);
        let local = fract((pixel + drift) / cell) - vec2f(0.5, 0.5);
        let seed = hash21(grid);
        let flake = smoothstep(0.24, 0.02, length(local + vec2f(seed * 0.18 - 0.09, seed * 0.12 - 0.06)));
        alpha = flake * step(seed, 0.28 + intensity * 0.22) * (0.055 + intensity * 0.08);
        color = vec3f(0.94, 0.98, 1.0);
    }

    if (alpha < 0.004) {
        discard;
    }

    return vec4f(color * alpha, alpha);
}
`;
