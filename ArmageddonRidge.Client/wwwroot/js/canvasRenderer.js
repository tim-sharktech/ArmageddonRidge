import { clamp, clamp01, hash2d, positiveModulo, quadraticScalar } from "./rendering/math.js";
import { configureSprites, drawOrientedSprite, drawSprite, drawSpriteFacing, loadSprites, spriteFrame } from "./rendering/sprites.js";
import {
    isDarkEagleWeapon,
    isDroneWeapon,
    isLaserExplosion,
    isLaserWeapon,
    isLavaExplosion,
    isMirvWeapon,
    isMissileWeapon,
    isMopWeapon,
    isNapalmWeapon,
    isPatriotExplosion,
    isPenetratorExplosion,
    isShieldHitExplosion
} from "./rendering/weaponVisuals.js";

let canvas;
let ctx;
let lastFrame = performance.now();
let fps = 60;
let frameMs = 16.7;
let renderMs = 0;
let lastScene;
let cachedTerrain;
let cachedTerrainTopRef;
let cachedTerrainTopLength = -1;
let cachedTerrainTopWorldHeight = 0;
let cachedTerrainTop = 0;
let rafId = 0;
let shotInProgress = false;
const spriteManifestVersion = "2026-05-04-genesis-v7";
const patriotInterceptDurationScale = 2.6;
const patriotInterceptMinDuration = 2900;
const patriotInterceptMaxDuration = 3400;
const patriotReticleScale = 1.65;
const shieldRadiusX = 76;
const shieldRadiusY = 58;
const shieldCenterYOffset = 62;
const cloudBands = [
    { x: 90, y: 64, scale: 1.08, speed: 7 },
    { x: 360, y: 104, scale: 0.84, speed: 11 },
    { x: 690, y: 72, scale: 1.18, speed: 8 },
    { x: 1010, y: 118, scale: 0.72, speed: 13 },
    { x: 1260, y: 48, scale: 0.96, speed: 9 }
];
const weatherTypes = ["clear", "rain", "snow", "storm"];
const terrainStride = 4;

export async function initialize(element) {
    canvas = element;
    ctx = canvas.getContext("2d", { alpha: false });
    ctx.imageSmoothingEnabled = false;
    configureSprites(() => ctx);
    await loadSprites(spriteManifestVersion);
    startStatsLoop();
}

export function render(scene) {
    if (!ctx || !scene?.world) {
        return { fps: 0, frameMs: 0, renderMs: 0 };
    }

    if (shotInProgress) {
        return getStats();
    }

    const started = performance.now();
    const renderScene = prepareScene(scene);
    lastScene = renderScene;
    sizeCanvas();
    drawScene(renderScene, 0, 0);
    updateStats();
    renderMs = performance.now() - started;
    return getStats();
}

export async function playShot(scene, trail, explosions, screenShake, weaponId, options = {}) {
    if (!ctx || !trail?.length) {
        render(scene);
        return;
    }

    const shotScene = prepareScene(scene);
    shotInProgress = true;
    const playbackOptions = options ?? {};
    const suppressCanvasPatriotCountermeasure = Boolean(playbackOptions.suppressCanvasPatriotCountermeasure);
    let points = Array.isArray(trail) ? trail : Array.from(trail);
    if (playbackOptions.intercepted && playbackOptions.interceptX !== undefined && playbackOptions.interceptY !== undefined) {
        const patriotPlayback = createPatriotPlayback(points, { x: playbackOptions.interceptX, y: playbackOptions.interceptY });
        points = patriotPlayback.points;
        playbackOptions.patriot = patriotPlayback;
    }

    const activeExplosions = Array.isArray(explosions) ? explosions : Array.from(explosions ?? []);
    const stagedExplosions = [];
    const finalExplosions = [];
    let highImpactShake = false;
    for (let i = 0; i < activeExplosions.length; i++) {
        const explosion = activeExplosions[i];
        if (explosion.nuclear || explosion.radius > 80) {
            highImpactShake = true;
        }

        if (Number(explosion.triggerIndex ?? -1) >= 0) {
            stagedExplosions.push(explosion);
        } else {
            finalExplosions.push(explosion);
        }
    }

    const stagedStarts = new Map();
    const baseDuration = shotDuration(points.length, weaponId);
    const duration = playbackOptions.intercepted
        ? clamp(baseDuration * patriotInterceptDurationScale, patriotInterceptMinDuration, patriotInterceptMaxDuration)
        : baseDuration;
    const started = performance.now();

    return new Promise(resolve => {
        let completed = false;
        const complete = () => {
            if (completed) {
                return;
            }

            completed = true;
            shotInProgress = false;
            resolve();
        };

        const fail = error => {
            console.error("Shot playback failed", error);
            complete();
        };

        const finish = () => {
            if (!finalExplosions.length) {
                setTimeout(complete, 120);
                return;
            }

            animateExplosions(scene, finalExplosions, screenShake)
                .then(complete)
                .catch(fail);
        };

        const tick = now => {
            try {
                const t = Math.min(1, (now - started) / duration);
                const basePathProgress = shotPathProgress(t, weaponId);
                const patriotTimelineProgress = playbackOptions.patriot ? t : basePathProgress;
                const pathProgress = playbackOptions.patriot
                    ? patriotIncomingPathProgress(playbackOptions.patriot, patriotTimelineProgress)
                    : basePathProgress;
                const count = Math.max(1, Math.floor(points.length * pathProgress));
                const shake = screenShake && highImpactShake ? Math.sin(now * 0.08) * (1 - t) * 8 : 0;
                drawScene(shotScene, shake, -shake * 0.4);
                drawTrail(points, count, weaponId, activeExplosions, playbackOptions.visualKind);
                if (!suppressCanvasPatriotCountermeasure) {
                    drawPatriotCountermeasure(shotScene, playbackOptions, patriotTimelineProgress);
                }
                drawTriggeredExplosions(stagedExplosions, count, now, stagedStarts);
                if (t < 1) {
                    requestAnimationFrame(tick);
                    return;
                }

                requestAnimationFrame(() => {
                    try {
                        drawScene(shotScene, 0, 0);
                        drawTrail(points, points.length, weaponId, activeExplosions, playbackOptions.visualKind);
                        if (!suppressCanvasPatriotCountermeasure) {
                            drawPatriotCountermeasure(shotScene, playbackOptions, 1);
                        }
                        drawTriggeredExplosions(stagedExplosions, points.length, performance.now(), stagedStarts);
                        finish();
                    } catch (error) {
                        fail(error);
                    }
                });
            } catch (error) {
                fail(error);
            }
        };

        requestAnimationFrame(tick);
    });
}

export function getStats() {
    return { fps: Math.round(fps), frameMs, renderMs };
}

function trimTrailToIntercept(points, intercept) {
    if (!points.length) {
        return [intercept];
    }

    let bestIndex = 0;
    let bestDistance = Number.MAX_VALUE;
    for (let i = 0; i < points.length; i++) {
        const point = points[i];
        const dx = point.x - intercept.x;
        const dy = point.y - intercept.y;
        const distance = (dx * dx) + (dy * dy);
        if (distance < bestDistance) {
            bestDistance = distance;
            bestIndex = i;
        }
    }

    const trimmed = points.slice(0, Math.min(points.length, bestIndex + 1));
    const last = trimmed[trimmed.length - 1];
    if (!last || ((last.x - intercept.x) ** 2) + ((last.y - intercept.y) ** 2) > 0.01) {
        trimmed.push(intercept);
    }

    return trimmed;
}

function createPatriotPlayback(points, intercept) {
    const trimmed = trimTrailToIntercept(points, intercept);
    const apexIndex = findApexIndex(trimmed);
    const apex = trimmed[apexIndex] ?? trimmed[0] ?? intercept;
    const apexProgress = clamp((apexIndex + 1) / Math.max(1, trimmed.length), 0.08, 1);
    const holdStartProgress = 0.48;
    const holdEndProgress = 0.68;
    return {
        points: trimmed,
        apexX: apex.x,
        apexY: apex.y,
        apexProgress,
        holdStartProgress,
        holdEndProgress,
        lockProgressStart: 0.22,
        launchProgressStart: 0.72,
        interceptX: intercept.x,
        interceptY: intercept.y
    };
}

function patriotIncomingPathProgress(patriot, timelineProgress) {
    const apexProgress = clamp(Number(patriot.apexProgress ?? 0.24), 0.02, 1);
    const holdStart = clamp(Number(patriot.holdStartProgress ?? apexProgress), 0.01, 0.94);
    const holdEnd = clamp(Number(patriot.holdEndProgress ?? holdStart), holdStart, 0.98);
    if (timelineProgress <= holdStart) {
        return clamp((timelineProgress / Math.max(0.001, holdStart)) * apexProgress, 0, apexProgress);
    }

    if (timelineProgress <= holdEnd) {
        return apexProgress;
    }

    const exitProgress = (timelineProgress - holdEnd) / Math.max(0.001, 1 - holdEnd);
    return clamp(apexProgress + exitProgress * (1 - apexProgress), apexProgress, 1);
}

function findApexIndex(points) {
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

function prepareScene(scene) {
    if (scene?.terrain?.length) {
        cachedTerrain = scene.terrain;
        return scene;
    }

    return { ...scene, terrain: cachedTerrain ?? [] };
}

function clearCanvas() {
    ctx.save();
    ctx.setTransform(1, 0, 0, 1, 0, 0);
    ctx.fillStyle = "#102129";
    ctx.fillRect(0, 0, canvas.width, canvas.height);
    ctx.restore();
}

function setWorldTransform(offsetX = 0, offsetY = 0, world = { width: 1200, height: 700 }) {
    const worldWidth = Number(world?.width ?? 1200);
    const worldHeight = Number(world?.height ?? 700);
    const scale = Math.min(canvas.width / worldWidth, canvas.height / worldHeight);
    const left = (canvas.width - (worldWidth * scale)) * 0.5;
    const top = (canvas.height - (worldHeight * scale)) * 0.5;
    ctx.setTransform(scale, 0, 0, scale, left + offsetX, top + offsetY);
}

function drawScene(scene, offsetX, offsetY) {
    clearCanvas();
    ctx.save();
    setWorldTransform(offsetX, offsetY, scene.world);
    drawSky(scene);
    drawWeather(scene, false);
    drawTerrain(scene.terrain ?? [], scene.world);
    drawRadiation(scene.radiation ?? []);
    drawTracerTrails(scene.tracerTrails ?? []);
    drawAimPreview(scene.previewTrail ?? []);
    const now = performance.now();
    drawTank(scene.player, "playerTank", isTankHurt(scene.player, scene, "player"), isTankShieldHit(scene.player, scene, "player"), now);
    drawTank(scene.cpu, "cpuTank", isTankHurt(scene.cpu, scene, "cpu"), isTankShieldHit(scene.cpu, scene, "cpu"), now);
    if (String(scene.phase ?? "").toLowerCase() !== "battle") {
        drawWind(scene.wind);
    }
    drawWeather(scene, true);
    ctx.restore();
}

function drawTracerTrails(trails) {
    if (!Array.isArray(trails) || trails.length === 0) {
        return;
    }

    ctx.save();
    ctx.lineCap = "round";
    ctx.lineJoin = "round";
    for (let i = 0; i < trails.length; i++) {
        const trail = trails[i] ?? [];
        if (trail.length < 2) {
            continue;
        }

        const alpha = clamp(0.2 + ((i + 1) / trails.length) * 0.34, 0.2, 0.54);
        ctx.strokeStyle = `rgba(255, 248, 217, ${alpha})`;
        ctx.lineWidth = 2;
        ctx.setLineDash([10, 10]);
        ctx.beginPath();
        for (let pointIndex = 0; pointIndex < trail.length; pointIndex++) {
            const point = trail[pointIndex];
            if (pointIndex === 0) {
                ctx.moveTo(point.x, point.y);
            } else {
                ctx.lineTo(point.x, point.y);
            }
        }
        ctx.stroke();
    }

    ctx.setLineDash([]);
    ctx.restore();
}

function drawSky(scene) {
    const gradient = ctx.createLinearGradient(0, 0, 0, scene.world.height);
    gradient.addColorStop(0, "#82c8ee");
    gradient.addColorStop(0.58, "#f2d9a2");
    gradient.addColorStop(1, "#5b4a3d");
    ctx.fillStyle = gradient;
    ctx.fillRect(0, 0, scene.world.width, scene.world.height);
    drawClouds(scene);
}

function drawClouds(scene) {
    const worldWidth = scene.world.width;
    const wind = scene.wind ?? 0;
    const windDirection = wind === 0 ? 1 : Math.sign(wind);
    const windStrength = Math.abs(wind);
    const nowSeconds = performance.now() / 1000;

    for (const cloud of cloudBands) {
        const travel = worldWidth + 260;
        const speed = (cloud.speed * 0.35) + (windStrength * 1.35);
        const x = positiveModulo(cloud.x + (windDirection * speed * nowSeconds), travel) - 150;
        drawCloud(x, cloud.y, cloud.scale);
    }
}

function drawWeather(scene, foreground) {
    const weather = resolveWeather(scene);
    if (weather.type === "clear") {
        return;
    }

    if (!foreground && weather.type === "storm") {
        drawStormSky(scene, weather);
        return;
    }

    if (weather.type === "rain" || weather.type === "storm") {
        drawRain(scene, weather, foreground);
    } else if (weather.type === "snow") {
        drawSnow(scene, weather, foreground);
    }
}

function resolveWeather(scene) {
    const source = scene.weather ?? scene.world?.weather ?? scene.environment?.weather;
    const explicit = typeof source === "string" ? source : source?.type ?? source?.kind;
    const normalized = String(explicit ?? "").toLowerCase();
    const wind = Number(scene.wind ?? 0);
    const round = Number(scene.round ?? scene.turn ?? 0);
    const phaseValue = scene.phase ? String(scene.phase).length : 0;
    const seed = hash2d(Math.round(wind * 19) + round * 31, phaseValue + Math.round((scene.world?.width ?? 1200) * 0.1));
    const type = weatherTypes.includes(normalized) ? normalized : weatherTypes[seed % weatherTypes.length];
    const intensity = clamp01(Number(source?.intensity ?? source?.strength ?? (0.36 + (seed % 31) / 100)));
    return { type, intensity, seed, wind };
}

function drawStormSky(scene, weather) {
    const world = scene.world;
    const pulse = Math.sin(performance.now() * 0.004 + weather.seed) * 0.5 + 0.5;
    ctx.fillStyle = `rgba(19, 24, 34, ${0.18 + weather.intensity * 0.18})`;
    ctx.fillRect(0, 0, world.width, world.height);

    if ((weather.seed % 7) < 2 && pulse > 0.82) {
        const x = 160 + (weather.seed % 820);
        ctx.strokeStyle = `rgba(226, 238, 255, ${(pulse - 0.82) * 2.2})`;
        ctx.lineWidth = 2;
        ctx.beginPath();
        ctx.moveTo(x, 38);
        ctx.lineTo(x + 24, 112);
        ctx.lineTo(x - 5, 174);
        ctx.lineTo(x + 42, 256);
        ctx.stroke();
        ctx.fillStyle = `rgba(235, 245, 255, ${(pulse - 0.82) * 0.12})`;
        ctx.fillRect(0, 0, world.width, world.height);
    }
}

function drawRain(scene, weather, foreground) {
    const world = scene.world;
    const now = performance.now() * 0.001;
    const baseCount = 58 + Math.floor(weather.intensity * 48);
    const count = foreground ? baseCount : Math.floor(baseCount * 0.36);
    const slant = clamp(weather.wind * 0.75, -34, 34);
    const alpha = foreground ? (weather.type === "storm" ? 0.38 : 0.34) : 0.14;
    ctx.strokeStyle = weather.type === "storm" ? `rgba(190, 218, 240, ${alpha})` : `rgba(207, 232, 245, ${alpha})`;
    ctx.lineWidth = foreground ? 1.2 : 0.8;
    ctx.beginPath();
    for (let i = 0; i < count; i++) {
        const lane = hash2d(i + weather.seed, 19) % 1220;
        const speed = 380 + (hash2d(i, weather.seed) % 180);
        const x = positiveModulo(lane + slant * now * 5, world.width + 80) - 40;
        const y = positiveModulo((hash2d(i, 43) % 720) + now * speed, world.height + 80) - 40;
        ctx.moveTo(x, y);
        ctx.lineTo(x + slant * 0.45, y + (foreground ? 24 : 16));
    }
    ctx.stroke();
}

function drawSnow(scene, weather, foreground) {
    const world = scene.world;
    const now = performance.now() * 0.001;
    const baseCount = 42 + Math.floor(weather.intensity * 34);
    const count = foreground ? baseCount : Math.floor(baseCount * 0.4);
    ctx.fillStyle = foreground ? "rgba(246, 251, 255, 0.56)" : "rgba(246, 251, 255, 0.24)";
    for (let i = 0; i < count; i++) {
        const drift = Math.sin(now * 0.9 + i) * 18 + weather.wind * 0.25;
        const x = positiveModulo((hash2d(i, weather.seed) % 1240) + drift + now * weather.wind * 2, world.width + 60) - 30;
        const y = positiveModulo((hash2d(weather.seed, i) % 760) + now * (36 + (i % 5) * 9), world.height + 50) - 25;
        const size = 1.2 + (i % 3) * 0.7;
        ctx.fillRect(x, y, size, size);
    }
}

function drawCloud(x, y, scale) {
    ctx.save();
    ctx.translate(x, y);
    ctx.scale(scale, scale);
    ctx.fillStyle = "rgba(255,255,255,0.46)";
    ctx.beginPath();
    ctx.ellipse(26, 10, 32, 13, 0, 0, Math.PI * 2);
    ctx.ellipse(62, 5, 44, 18, 0, 0, Math.PI * 2);
    ctx.ellipse(104, 12, 38, 14, 0, 0, Math.PI * 2);
    ctx.fill();
    ctx.fillStyle = "rgba(225,239,245,0.32)";
    ctx.fillRect(18, 16, 108, 8);
    ctx.restore();
}

function drawTerrain(terrain, world) {
    if (!terrain.length) {
        return;
    }

    const worldHeight = world?.height ?? 700;
    const surfaceTop = terrainSurfaceTop(terrain, worldHeight);
    ctx.beginPath();
    ctx.moveTo(0, worldHeight);
    for (let x = 0; x < terrain.length; x++) {
        ctx.lineTo(x, terrain[x]);
    }
    ctx.lineTo(terrain.length - 1, worldHeight);
    ctx.closePath();
    const dirtGradient = ctx.createLinearGradient(0, surfaceTop, 0, worldHeight);
    dirtGradient.addColorStop(0, "#4f6d38");
    dirtGradient.addColorStop(0.08, "#2f4324");
    dirtGradient.addColorStop(0.34, "#3d3327");
    dirtGradient.addColorStop(1, "#171511");
    ctx.fillStyle = dirtGradient;
    ctx.fill();

    ctx.save();
    ctx.beginPath();
    ctx.moveTo(0, worldHeight);
    for (let x = 0; x < terrain.length; x++) {
        ctx.lineTo(x, terrain[x]);
    }
    ctx.lineTo(terrain.length - 1, worldHeight);
    ctx.closePath();
    ctx.clip();
    drawTerrainStrata(terrain, worldHeight);
    drawTerrainGrain(terrain, worldHeight);
    drawTerrainRocks(terrain, worldHeight);
    ctx.restore();

    ctx.lineCap = "round";
    ctx.lineJoin = "round";
    ctx.strokeStyle = "rgba(30, 42, 22, 0.65)";
    ctx.lineWidth = 7;
    strokeTerrainTop(terrain, terrainStride, 7);
    ctx.strokeStyle = "#8fbf64";
    ctx.lineWidth = 4;
    strokeTerrainTop(terrain, terrainStride);
    ctx.strokeStyle = "rgba(232, 211, 143, 0.55)";
    ctx.lineWidth = 1.5;
    strokeTerrainTop(terrain, 6, 5);
    drawTerrainGrass(terrain);
}

function terrainSurfaceTop(terrain, worldHeight) {
    if (terrain === cachedTerrainTopRef
        && terrain.length === cachedTerrainTopLength
        && worldHeight === cachedTerrainTopWorldHeight) {
        return cachedTerrainTop;
    }

    let top = worldHeight;
    for (let i = 0; i < terrain.length; i++) {
        if (terrain[i] < top) {
            top = terrain[i];
        }
    }

    cachedTerrainTopRef = terrain;
    cachedTerrainTopLength = terrain.length;
    cachedTerrainTopWorldHeight = worldHeight;
    cachedTerrainTop = top;
    return top;
}

function drawTerrainStrata(terrain, worldHeight) {
    for (let y = 330; y < worldHeight; y += 28) {
        ctx.beginPath();
        let started = false;
        for (let x = 0; x < terrain.length; x += 7) {
            const surface = terrain[x] ?? worldHeight;
            const lineY = y + Math.sin((x + y) * 0.018) * 5 + ((hash2d(x, y) % 5) - 2);
            if (lineY <= surface + 12) {
                started = false;
                continue;
            }

            if (!started) {
                ctx.moveTo(x, lineY);
                started = true;
            } else {
                ctx.lineTo(x, lineY);
            }
        }

        ctx.strokeStyle = y % 56 === 0 ? "rgba(112, 82, 54, 0.46)" : "rgba(226, 194, 128, 0.17)";
        ctx.lineWidth = y % 84 === 0 ? 3 : 1.5;
        ctx.stroke();
    }
}

function drawTerrainGrain(terrain, worldHeight) {
    ctx.fillStyle = "rgba(255, 235, 178, 0.11)";
    for (let x = 3; x < terrain.length; x += 17) {
        const top = terrain[x] ?? worldHeight;
        const depth = Math.max(18, worldHeight - top);
        const y = top + 12 + (hash2d(x, top) % Math.floor(depth));
        ctx.fillRect(x, y, 2, 1);
    }

    ctx.fillStyle = "rgba(9, 9, 7, 0.2)";
    for (let x = 9; x < terrain.length; x += 23) {
        const top = terrain[x] ?? worldHeight;
        const depth = Math.max(18, worldHeight - top);
        const y = top + 18 + (hash2d(x, top + 11) % Math.floor(depth));
        ctx.fillRect(x, y, 3, 2);
    }
}

function drawTerrainRocks(terrain, worldHeight) {
    for (let x = 14; x < terrain.length; x += 47) {
        const top = terrain[x] ?? worldHeight;
        const depth = worldHeight - top;
        if (depth < 42) {
            continue;
        }

        const y = top + 16 + (hash2d(x, top) % Math.min(150, Math.floor(depth - 12)));
        const w = 5 + (hash2d(top, x) % 9);
        const h = 3 + (hash2d(x + 7, top) % 5);
        ctx.fillStyle = "rgba(18, 18, 16, 0.34)";
        ctx.fillRect(x + 1, y + 1, w, h);
        ctx.fillStyle = "rgba(145, 126, 93, 0.34)";
        ctx.fillRect(x, y, w, Math.max(1, h - 1));
        ctx.fillStyle = "rgba(230, 208, 151, 0.2)";
        ctx.fillRect(x + 1, y, Math.max(2, w * 0.45), 1);
    }
}

function drawTerrainGrass(terrain) {
    ctx.strokeStyle = "rgba(159, 214, 94, 0.62)";
    ctx.lineWidth = 1;
    ctx.beginPath();
    for (let x = 2; x < terrain.length; x += 11) {
        const y = terrain[x] ?? 0;
        const h = 3 + (hash2d(x, y) % 6);
        const lean = ((hash2d(y, x) % 5) - 2) * 0.8;
        ctx.moveTo(x, y + 1);
        ctx.lineTo(x + lean, y - h);
    }
    ctx.stroke();
}

function strokeTerrainTop(terrain, step, yOffset = 0) {
    ctx.beginPath();
    for (let x = 0; x < terrain.length; x += step) {
        const y = terrain[x] + yOffset;
        if (x === 0) {
            ctx.moveTo(x, y);
        } else {
            ctx.lineTo(x, y);
        }
    }
    ctx.stroke();
}

function drawRadiation(zones) {
    for (const zone of zones) {
        if (isLavaZone(zone)) {
            drawLavaZone(zone);
        } else {
            drawRadiationZone(zone);
        }
    }
}

function isLavaZone(zone) {
    const kind = String(zone.visualKind ?? zone.kind ?? "").toLowerCase();
    return Boolean(zone.lava || zone.napalm || kind.includes("lava") || kind.includes("fire"));
}

function drawRadiationZone(zone) {
    const x = Number(zone.x ?? 0);
    const y = Number(zone.y ?? 0);
    const radius = Number(zone.radius ?? 42);
    const turns = Math.max(1, Number(zone.turns ?? 1));
    const now = performance.now() * 0.001;
    const pulse = 0.72 + Math.sin(now * 3.2 + x * 0.017) * 0.18;
    const alpha = Math.min(0.34, 0.12 + turns * 0.055);

    ctx.save();
    ctx.translate(x, y + radius * 0.08);
    ctx.scale(1, 0.34);

    const glow = ctx.createRadialGradient(0, 0, 2, 0, 0, radius);
    glow.addColorStop(0, `rgba(255, 239, 112, ${alpha * pulse})`);
    glow.addColorStop(0.48, `rgba(199, 177, 71, ${alpha * 0.42})`);
    glow.addColorStop(1, "rgba(64, 53, 25, 0)");
    ctx.fillStyle = glow;
    ctx.beginPath();
    ctx.arc(0, 0, radius, 0, Math.PI * 2);
    ctx.fill();

    ctx.save();
    ctx.beginPath();
    ctx.arc(0, 0, radius * 0.92, 0, Math.PI * 2);
    ctx.clip();
    ctx.rotate(-0.62);
    for (let stripe = -radius * 1.8; stripe < radius * 1.8; stripe += 22) {
        ctx.fillStyle = (Math.round(stripe / 22) & 1) === 0
            ? `rgba(242, 200, 55, ${0.18 * pulse})`
            : `rgba(20, 22, 18, ${0.15 * pulse})`;
        ctx.fillRect(stripe, -radius * 1.25, 9, radius * 2.5);
    }
    ctx.restore();

    ctx.setLineDash([10, 12]);
    ctx.strokeStyle = `rgba(255, 214, 72, ${0.42 * pulse})`;
    ctx.lineWidth = 3;
    ctx.beginPath();
    ctx.arc(0, 0, radius * 0.9, 0, Math.PI * 2);
    ctx.stroke();
    ctx.restore();

    drawRadioactiveGlyph(x, y - Math.min(34, radius * 0.24), clamp(radius * 0.2, 24, 44), 0.78 * pulse);
}

function drawLavaZone(zone) {
    const x = Number(zone.x ?? 0);
    const y = Number(zone.y ?? 0);
    const radius = Number(zone.radius ?? 42);
    const turns = Math.max(1, Number(zone.turns ?? 1));
    const now = performance.now() * 0.001;
    const pulse = 0.76 + Math.sin(now * 5.2 + x * 0.021) * 0.22;

    ctx.save();
    ctx.translate(x, y + radius * 0.16);
    ctx.scale(1, 0.38);
    const glow = ctx.createRadialGradient(0, 0, 2, 0, 0, radius * 1.05);
    glow.addColorStop(0, `rgba(255, 224, 78, ${0.25 * pulse})`);
    glow.addColorStop(0.42, `rgba(255, 82, 24, ${0.22 + turns * 0.04})`);
    glow.addColorStop(0.82, "rgba(84, 24, 15, 0.2)");
    glow.addColorStop(1, "rgba(84, 24, 15, 0)");
    ctx.fillStyle = glow;
    ctx.beginPath();
    ctx.arc(0, 0, radius, 0, Math.PI * 2);
    ctx.fill();
    ctx.restore();

    for (let i = 0; i < 5; i++) {
        const angle = (Math.PI * 2 * i / 5) + now * 0.35;
        const distance = radius * (0.1 + (i % 3) * 0.13);
        drawLavaSprite(
            x + Math.cos(angle) * distance,
            y + radius * 0.1 + Math.sin(angle) * distance * 0.34,
            radius * (0.34 + (i % 2) * 0.08),
            now + i * 1.7,
            0.42 + i * 0.05);
    }
}

function drawTank(tank, frameName, hurt = false, shieldHit = false, now = performance.now()) {
    if (!tank) {
        return;
    }

    ctx.save();
    ctx.globalAlpha = 0.3;
    ctx.fillStyle = "#05070a";
    ctx.beginPath();
    ctx.ellipse(tank.x, tank.y - 4, 40, 9, 0, 0, Math.PI * 2);
    ctx.fill();
    ctx.restore();

    if (tank.shield > 0 || shieldHit) {
        drawTankShield(tank, shieldHit, now);
    }

    drawTankSprite(tank, frameName);

    if (shieldHit) {
        drawTankShieldHitGlimmer(tank, now);
    }

    if (Number(tank.buriedDepth ?? 0) > 4) {
        drawBurialCover(tank);
    }

    if (hurt) {
        drawTankHitPulse(tank, now);
    }

    if (tank.health <= 35) {
        drawSmokeStack(tank.x, tank.y - 54);
    }
}

function drawTankShield(tank, shieldHit, now) {
    const shield = Math.max(0, Number(tank.shield ?? 0));
    const strength = clamp01(shield / 120);
    const bubbleAlpha = shieldHit ? 0.52 : 0.14 + strength * 0.18;
    const pulse = 0.5 + Math.sin(now * 0.008) * 0.5;
    const centerX = Number(tank.x ?? 0);
    const centerY = Number(tank.y ?? 0) - shieldCenterYOffset;
    const radiusX = shieldRadiusX - 5;
    const radiusY = shieldRadiusY - 4;
    const surfaceY = Number(tank.terrainY ?? tank.y);
    const top = centerY - radiusY - 22;
    const clipHeight = Math.max(0, surfaceY - top - 1);
    if (clipHeight <= 1) return;

    ctx.save();
    ctx.beginPath();
    ctx.rect(centerX - radiusX - 28, top, (radiusX + 28) * 2, clipHeight);
    ctx.clip();

    const shell = ctx.createRadialGradient(centerX - 24, centerY - 26, 8, centerX, centerY, radiusX + 20);
    shell.addColorStop(0, `rgba(255, 255, 255, ${0.06 + strength * 0.035})`);
    shell.addColorStop(0.52, `rgba(121, 214, 255, ${0.025 + strength * 0.045})`);
    shell.addColorStop(0.82, `rgba(57, 175, 255, ${0.045 + strength * 0.055})`);
    shell.addColorStop(1, `rgba(57, 175, 255, ${0.11 + strength * 0.1})`);
    ctx.fillStyle = shell;
    ctx.beginPath();
    ctx.ellipse(centerX, centerY, radiusX + 5, radiusY + 5, 0, 0, Math.PI * 2);
    ctx.fill();

    ctx.lineCap = "round";
    ctx.lineJoin = "round";
    ctx.shadowColor = "rgba(121, 214, 255, 0.42)";
    ctx.shadowBlur = 8 + strength * 7 + (shieldHit ? 8 : 0);
    ctx.strokeStyle = `rgba(136, 226, 255, ${bubbleAlpha})`;
    ctx.lineWidth = 1.35 + strength * 1.9;
    ctx.beginPath();
    ctx.ellipse(centerX, centerY, radiusX, radiusY, 0, 0, Math.PI * 2);
    ctx.stroke();

    ctx.shadowBlur = 0;
    ctx.strokeStyle = `rgba(255, 255, 255, ${0.2 + strength * 0.14})`;
    ctx.lineWidth = 1 + strength * 0.45;
    ctx.beginPath();
    ctx.ellipse(centerX - radiusX * 0.04, centerY - radiusY * 0.08, radiusX - 12, radiusY - 10, 0, Math.PI * 1.04, Math.PI * 1.74);
    ctx.stroke();

    ctx.strokeStyle = `rgba(126, 226, 213, ${0.12 + strength * 0.12})`;
    ctx.lineWidth = 1;
    for (let i = 0; i < 3; i++) {
        const phase = now * 0.0018 + i * 1.74;
        const start = phase % (Math.PI * 2);
        const end = start + Math.PI * (0.14 + strength * 0.08);
        ctx.beginPath();
        ctx.ellipse(centerX, centerY, radiusX - 4 - i * 9, radiusY - 3 - i * 6, 0, start, end);
        ctx.stroke();
    }

    if (shieldHit) {
        const ripple = 0.5 + pulse * 0.5;
        ctx.strokeStyle = `rgba(224, 250, 255, ${0.42 * (1 - ripple * 0.26)})`;
        ctx.lineWidth = 1.6;
        ctx.setLineDash([7, 10]);
        ctx.beginPath();
        ctx.ellipse(centerX, centerY, radiusX + 4 + ripple * 10, radiusY + 4 + ripple * 8, 0, 0, Math.PI * 2);
        ctx.stroke();
        ctx.setLineDash([]);
    }

    ctx.restore();
}

function drawTankShieldHitGlimmer(tank, now) {
    const centerX = Number(tank.x ?? 0);
    const centerY = Number(tank.y ?? 0) - shieldCenterYOffset;
    const radiusX = shieldRadiusX - 5;
    const radiusY = shieldRadiusY - 4;
    const pulse = 0.5 + Math.sin(now * 0.026) * 0.5;

    ctx.save();
    ctx.lineCap = "round";
    ctx.strokeStyle = `rgba(236, 254, 255, ${0.42 + pulse * 0.18})`;
    ctx.lineWidth = 1.5;
    for (let i = 0; i < 4; i++) {
        const angle = (Math.PI * 0.5 * i) + now * 0.004;
        const innerX = centerX + Math.cos(angle) * (radiusX - 14);
        const innerY = centerY + Math.sin(angle) * (radiusY - 11);
        const outerX = centerX + Math.cos(angle) * (radiusX + 9);
        const outerY = centerY + Math.sin(angle) * (radiusY + 7);
        ctx.beginPath();
        ctx.moveTo(innerX, innerY);
        ctx.lineTo(outerX, outerY);
        ctx.stroke();
    }

    ctx.fillStyle = `rgba(255, 255, 255, ${0.45 + pulse * 0.16})`;
    for (let i = 0; i < 3; i++) {
        const angle = now * 0.006 + i * 2.1;
        const x = centerX + Math.cos(angle) * (radiusX - 10);
        const y = centerY + Math.sin(angle) * (radiusY - 8);
        ctx.fillRect(x - 1, y - 1, 2, 2);
    }

    ctx.restore();
}

function drawTankHitPulse(tank, now) {
    const pulse = 0.5 + Math.sin(now * 0.026) * 0.5;
    ctx.save();
    ctx.lineCap = "round";
    ctx.strokeStyle = `rgba(255, 52, 42, ${0.42 + pulse * 0.3})`;
    ctx.lineWidth = 5;
    ctx.beginPath();
    ctx.ellipse(tank.x, tank.y - 26, 48 + pulse * 9, 30 + pulse * 6, 0, 0, Math.PI * 2);
    ctx.stroke();
    ctx.fillStyle = `rgba(255, 46, 38, ${0.08 + pulse * 0.08})`;
    ctx.beginPath();
    ctx.ellipse(tank.x, tank.y - 30, 54, 36, 0, 0, Math.PI * 2);
    ctx.fill();
    ctx.restore();
}

function isTankHurt(tank, scene, side) {
    if (hasVisualFlag(tank?.hurt)
        || hasVisualFlag(tank?.isHurt)
        || hasVisualFlag(tank?.hit)
        || hasVisualFlag(tank?.damagePulse)
        || hasVisualFlag(tank?.recentDamage)
        || hasVisualFlag(tank?.healthDamage)
        || hasVisualFlag(tank?.damageTaken)) {
        return true;
    }

    return side === "player"
        ? hasVisualFlag(scene?.playerHurt) || hasVisualFlag(scene?.hurtPlayer) || hasVisualFlag(scene?.playerHit)
        : hasVisualFlag(scene?.cpuHurt) || hasVisualFlag(scene?.hurtCpu) || hasVisualFlag(scene?.cpuHit);
}

function isTankShieldHit(tank, scene, side) {
    if (hasVisualFlag(tank?.shieldHit)
        || hasVisualFlag(tank?.shieldHurt)
        || hasVisualFlag(tank?.shieldDamaged)
        || hasVisualFlag(tank?.shieldAbsorbed)
        || hasVisualFlag(tank?.absorbedShieldDamage)
        || hasVisualFlag(tank?.shieldDamage)
        || hasVisualFlag(tank?.shieldPulse)) {
        return true;
    }

    return side === "player"
        ? hasVisualFlag(scene?.playerShieldHit) || hasVisualFlag(scene?.shieldHitPlayer) || hasVisualFlag(scene?.playerShieldDamaged)
        : hasVisualFlag(scene?.cpuShieldHit) || hasVisualFlag(scene?.shieldHitCpu) || hasVisualFlag(scene?.cpuShieldDamaged);
}

function hasVisualFlag(value) {
    if (value === true) {
        return true;
    }

    if (typeof value === "number") {
        return value > 0;
    }

    if (typeof value === "string") {
        const normalized = value.toLowerCase();
        return normalized === "true" || normalized === "hit" || normalized === "hurt" || normalized === "damaged" || normalized === "pulse";
    }

    return Boolean(value);
}

function drawBurialCover(tank) {
    const surfaceY = Number(tank.terrainY ?? tank.y - tank.buriedDepth);
    const depth = clamp(Number(tank.buriedDepth ?? 0), 0, 74);
    const width = 96;
    const height = clamp(depth + 14, 16, 64);
    const x = tank.x - width * 0.5;
    const y = surfaceY - 3;

    ctx.save();
    const fill = ctx.createLinearGradient(0, y, 0, y + height);
    fill.addColorStop(0, "#8fbf64");
    fill.addColorStop(0.16, "#4f6d38");
    fill.addColorStop(0.44, "#3d3327");
    fill.addColorStop(1, "#201b15");
    ctx.fillStyle = fill;
    ctx.beginPath();
    ctx.moveTo(x + 6, y + 7);
    ctx.quadraticCurveTo(tank.x - 22, y - 8, tank.x + 3, y + 2);
    ctx.quadraticCurveTo(tank.x + 34, y - 7, x + width - 6, y + 9);
    ctx.lineTo(x + width - 1, y + height);
    ctx.lineTo(x + 1, y + height);
    ctx.closePath();
    ctx.fill();

    ctx.strokeStyle = "rgba(159, 214, 94, 0.76)";
    ctx.lineWidth = 3;
    ctx.beginPath();
    ctx.moveTo(x + 6, y + 7);
    ctx.quadraticCurveTo(tank.x - 22, y - 8, tank.x + 3, y + 2);
    ctx.quadraticCurveTo(tank.x + 34, y - 7, x + width - 6, y + 9);
    ctx.stroke();

    ctx.fillStyle = "rgba(255, 235, 178, 0.16)";
    for (let i = 0; i < 9; i++) {
        const px = x + 10 + (i * 9) + (hash2d(i, Math.round(tank.x)) % 6);
        const py = y + 13 + (hash2d(Math.round(tank.y), i) % Math.max(8, Math.floor(height - 10)));
        ctx.fillRect(px, py, 2, 1);
    }

    ctx.restore();
}

function drawTankSprite(tank, baseFrameName) {
    const frameName = tank.isCpu || baseFrameName === "cpuTank" ? "cpuHull" : "playerHull";
    const frame = spriteFrame(frameName);

    if (!frame) {
        drawSprite(baseFrameName, tank.x - 48, tank.y - 52, 96, 48);
        drawTankBarrel(tank);
        return;
    }

    const targetHeight = tank.isCpu ? 66 : 68;
    const targetWidth = targetHeight * (frame.w / frame.h);
    const anchorX = targetWidth * 0.5;
    const footY = tank.y + 3;
    drawSpriteFacing(frameName, tank.x - anchorX, footY - targetHeight, targetWidth, targetHeight, tank.isCpu ? -1 : 1);
    drawTankBarrel(tank);
}

function drawTankBarrel(tank) {
    const facing = tank.isCpu ? -1 : 1;
    const pivotX = tank.x + (facing * 18);
    const pivotY = tank.y - 44;
    const angle = Number(tank?.angle ?? (tank?.isCpu ? 138 : 42));
    const length = 48;

    ctx.save();
    ctx.translate(pivotX, pivotY);
    ctx.rotate(-angle * Math.PI / 180);

    ctx.lineCap = "round";
    ctx.strokeStyle = "#070a0d";
    ctx.lineWidth = 12;
    ctx.beginPath();
    ctx.moveTo(-4, 0);
    ctx.lineTo(length, 0);
    ctx.stroke();

    const metal = ctx.createLinearGradient(0, -5, 0, 5);
    metal.addColorStop(0, "#f6f4dc");
    metal.addColorStop(0.28, "#87949f");
    metal.addColorStop(0.58, "#2d343c");
    metal.addColorStop(0.82, "#cfd8df");
    metal.addColorStop(1, "#111720");
    ctx.strokeStyle = metal;
    ctx.lineWidth = 8;
    ctx.beginPath();
    ctx.moveTo(0, 0);
    ctx.lineTo(length, 0);
    ctx.stroke();

    ctx.strokeStyle = "rgba(255, 255, 255, 0.58)";
    ctx.lineWidth = 1.5;
    ctx.beginPath();
    ctx.moveTo(5, -3);
    ctx.lineTo(length - 8, -3);
    ctx.stroke();

    ctx.fillStyle = "rgba(255, 255, 255, 0.22)";
    ctx.fillRect(length * 0.38, -3, 2, 6);
    ctx.fillRect(length * 0.66, -3, 2, 6);
    ctx.fillStyle = "rgba(7, 10, 13, 0.46)";
    ctx.fillRect(length * 0.5, 3, length * 0.28, 2);

    ctx.fillStyle = "#1b2028";
    ctx.fillRect(length - 2, -6, 8, 12);
    ctx.fillStyle = "#e6edf0";
    ctx.fillRect(length, -3, 5, 6);
    ctx.restore();

    drawTurretCap(tank, pivotX, pivotY);
}

function drawTurretCap(tank, x, y) {
    ctx.save();
    const capGradient = ctx.createLinearGradient(x - 16, y - 8, x + 16, y + 8);
    if (tank.isCpu) {
        capGradient.addColorStop(0, "#721f13");
        capGradient.addColorStop(0.5, "#ff7a2f");
        capGradient.addColorStop(1, "#33100d");
    } else {
        capGradient.addColorStop(0, "#073c42");
        capGradient.addColorStop(0.5, "#22d8cd");
        capGradient.addColorStop(1, "#08232b");
    }

    ctx.fillStyle = "#070a0d";
    ctx.beginPath();
    ctx.ellipse(x, y + 2, 17, 10, 0, 0, Math.PI * 2);
    ctx.fill();
    ctx.fillStyle = capGradient;
    ctx.beginPath();
    ctx.ellipse(x, y, 15, 8, 0, 0, Math.PI * 2);
    ctx.fill();
    ctx.restore();
}

function drawSmokeStack(x, y) {
    for (let i = 0; i < 3; i++) {
        const drift = Math.sin(performance.now() * 0.002 + i) * 5;
        ctx.fillStyle = `rgba(28, 28, 28, ${0.38 - i * 0.08})`;
        ctx.beginPath();
        ctx.arc(x + drift, y - i * 11, 6 + i * 2, 0, Math.PI * 2);
        ctx.fill();
    }
}

function drawWind(wind) {
    ctx.fillStyle = "#172028";
    ctx.fillRect(520, 18, 160, 36);
    ctx.fillStyle = "#f3f6e8";
    ctx.font = "18px system-ui";
    const arrow = wind > 0 ? "->" : wind < 0 ? "<-" : "--";
    ctx.fillText(`Wind ${arrow} ${Math.abs(wind ?? 0)}`, 545, 42);
}

function drawAimPreview(preview) {
    const points = Array.isArray(preview) ? preview : preview?.path;
    const cone = Array.isArray(preview?.cone) ? preview.cone : [];
    if ((!points?.length || points.length < 2) && cone.length < 3) {
        return;
    }

    ctx.save();
    ctx.lineCap = "round";
    ctx.lineJoin = "round";

    if (points?.length >= 2) {
        drawPreviewPath(points, "rgba(255, 248, 217, 0.34)", 7);
        drawPreviewPath(points, "rgba(126, 226, 213, 0.84)", 3);
    }

    if (cone.length >= 3) {
        drawPreviewCone(cone);
    }

    ctx.restore();
}

function drawPreviewPath(points, color, width) {
    ctx.setLineDash([9, 10]);
    ctx.strokeStyle = color;
    ctx.lineWidth = width;
    ctx.beginPath();
    traceSmoothPreviewPath(points);
    ctx.stroke();
    ctx.setLineDash([]);
}

function traceSmoothPreviewPath(points) {
    if (!points?.length) {
        return;
    }

    ctx.moveTo(points[0].x, points[0].y);
    if (points.length === 2) {
        ctx.lineTo(points[1].x, points[1].y);
        return;
    }

    for (let index = 0; index < points.length - 1; index++) {
        const previous = points[Math.max(0, index - 1)];
        const current = points[index];
        const next = points[index + 1];
        const afterNext = points[Math.min(points.length - 1, index + 2)];
        const cp1x = current.x + (next.x - previous.x) / 6;
        const cp1y = current.y + (next.y - previous.y) / 6;
        const cp2x = next.x - (afterNext.x - current.x) / 6;
        const cp2y = next.y - (afterNext.y - current.y) / 6;
        ctx.bezierCurveTo(cp1x, cp1y, cp2x, cp2y, next.x, next.y);
    }
}

function drawPreviewCone(cone) {
    const apex = cone[0];
    const left = cone[1];
    const right = cone[2];
    const now = performance.now() * 0.001;

    const centerX = (left.x + right.x) * 0.5;
    const centerY = (left.y + right.y) * 0.5;
    const gradient = ctx.createLinearGradient(apex.x, apex.y, centerX, centerY);
    gradient.addColorStop(0, "rgba(126, 226, 213, 0.08)");
    gradient.addColorStop(0.44, "rgba(126, 226, 213, 0.22)");
    gradient.addColorStop(0.76, "rgba(242, 193, 78, 0.16)");
    gradient.addColorStop(1, "rgba(236, 106, 92, 0.05)");
    ctx.fillStyle = gradient;
    ctx.beginPath();
    ctx.moveTo(apex.x, apex.y);
    ctx.lineTo(left.x, left.y);
    ctx.lineTo(right.x, right.y);
    ctx.closePath();
    ctx.fill();

    const bloomRadius = Math.max(18, Math.hypot(left.x - right.x, left.y - right.y) * 0.62);
    const bloom = ctx.createRadialGradient(centerX, centerY, 2, centerX, centerY, bloomRadius);
    bloom.addColorStop(0, "rgba(242, 193, 78, 0.2)");
    bloom.addColorStop(0.58, "rgba(126, 226, 213, 0.09)");
    bloom.addColorStop(1, "rgba(126, 226, 213, 0)");
    ctx.fillStyle = bloom;
    ctx.beginPath();
    ctx.moveTo(apex.x, apex.y);
    ctx.lineTo(left.x, left.y);
    ctx.lineTo(right.x, right.y);
    ctx.closePath();
    ctx.fill();

    ctx.setLineDash([7, 8]);
    ctx.strokeStyle = "rgba(126, 226, 213, 0.74)";
    ctx.lineWidth = 2;
    ctx.beginPath();
    ctx.moveTo(apex.x, apex.y);
    ctx.lineTo(left.x, left.y);
    ctx.moveTo(apex.x, apex.y);
    ctx.lineTo(right.x, right.y);
    ctx.stroke();
    ctx.setLineDash([]);

    ctx.strokeStyle = "rgba(255, 248, 217, 0.28)";
    ctx.lineWidth = 1.5;
    ctx.beginPath();
    ctx.moveTo(apex.x, apex.y);
    for (let i = 1; i <= 8; i++) {
        const t = i / 8;
        const wobble = Math.sin(now * 2.4 + i * 1.7) * t * 2.2;
        const x = apex.x + ((centerX - apex.x) * t);
        const y = apex.y + ((centerY - apex.y) * t) + wobble;
        ctx.lineTo(x, y);
    }
    ctx.stroke();

    for (let i = 1; i <= 5; i++) {
        const t = i / 6;
        const edgeT = 0.25 + (hash2d(i, Math.round(apex.x)) % 50) / 100;
        const x = apex.x + (((left.x + ((right.x - left.x) * edgeT)) - apex.x) * t);
        const y = apex.y + (((left.y + ((right.y - left.y) * edgeT)) - apex.y) * t);
        ctx.fillStyle = `rgba(126, 226, 213, ${0.16 + t * 0.2})`;
        ctx.fillRect(x - 1, y - 1, 2, 2);
    }
}

function drawTrail(points, count = points.length, weaponId, explosions = [], visualKind) {
    if (!points?.length || count <= 0) {
        return;
    }

    ctx.save();
    setWorldTransform();
    const visibleCount = Math.min(points.length, count);
    if (isLaserWeapon(weaponId, visualKind)) {
        drawLaserBeam(points, visibleCount);
        ctx.restore();
        return;
    }

    if (isDroneWeapon(weaponId)) {
        drawDroneSwarmTrail(points, visibleCount, weaponId);
        ctx.restore();
        return;
    }

    if (isDarkEagleWeapon(weaponId)) {
        drawDarkEagleTrail(points, visibleCount);
        ctx.restore();
        return;
    }

    if (isMirvWeapon(weaponId)) {
        drawMirvTrail(points, visibleCount, explosions);
        ctx.restore();
        return;
    }

    const missileLike = isMissileWeapon(weaponId) || (!weaponId && visibleCount > 36);
    if (missileLike) {
        drawSmokeTrail(points, visibleCount, weaponId);
    }

    ctx.strokeStyle = missileLike ? "rgba(255, 246, 191, 0.86)" : "#fff6bf";
    ctx.lineWidth = missileLike ? 2.2 : 3;
    ctx.beginPath();
    for (let index = 0; index < visibleCount; index++) {
        const point = points[index];
        if (index === 0) {
            ctx.moveTo(point.x, point.y);
        } else {
            ctx.lineTo(point.x, point.y);
        }
    }
    ctx.stroke();
    const last = points[visibleCount - 1];
    const prev = trajectoryReferencePoint(points, visibleCount);
    const angle = Math.atan2(last.y - prev.y, last.x - prev.x);
    drawFlameTip(last, prev, missileLike && !isMopWeapon(weaponId));
    if (isMopWeapon(weaponId)) {
        drawMopProjectile(last, prev);
    } else if (missileLike) {
        drawOrientedSprite("missile", last.x, last.y, 34, 14, angle);
    } else {
        drawOrientedSprite("shell", last.x, last.y, 22, 9, angle);
    }
    ctx.restore();
}

function drawLaserBeam(points, visibleCount) {
    const start = points[0];
    const end = points[Math.max(0, visibleCount - 1)];
    if (!start || !end) {
        return;
    }

    const progress = clamp(visibleCount / Math.max(2, points.length), 0.12, 1);
    const now = performance.now();
    const flicker = 0.78 + Math.sin(now * 0.05) * 0.14;
    ctx.lineCap = "round";
    ctx.lineJoin = "round";

    ctx.strokeStyle = `rgba(255, 49, 71, ${0.18 + progress * 0.18})`;
    ctx.lineWidth = 18;
    strokeLaserLine(start.x, start.y, end.x, end.y);

    ctx.strokeStyle = `rgba(255, 117, 94, ${0.34 + progress * 0.26})`;
    ctx.lineWidth = 9;
    strokeLaserLine(start.x, start.y, end.x, end.y);

    ctx.strokeStyle = `rgba(255, 250, 230, ${0.72 * flicker})`;
    ctx.lineWidth = 3;
    strokeLaserLine(start.x, start.y, end.x, end.y);

    const angle = Math.atan2(end.y - start.y, end.x - start.x);
    drawLaserMuzzle(start.x, start.y, angle, progress, now);
    drawLaserImpact(end.x, end.y, progress, now);
}

function strokeLaserLine(startX, startY, endX, endY) {
    ctx.beginPath();
    ctx.moveTo(startX, startY);
    ctx.lineTo(endX, endY);
    ctx.stroke();
}

function drawLaserMuzzle(x, y, angle, progress, now) {
    const pulse = 0.5 + Math.sin(now * 0.06) * 0.5;
    ctx.save();
    ctx.translate(x, y);
    ctx.rotate(angle);
    const glow = ctx.createRadialGradient(0, 0, 1, 0, 0, 18 + pulse * 8);
    glow.addColorStop(0, `rgba(255, 255, 255, ${0.72 * progress})`);
    glow.addColorStop(0.42, `rgba(255, 72, 86, ${0.52 * progress})`);
    glow.addColorStop(1, "rgba(255, 72, 86, 0)");
    ctx.fillStyle = glow;
    ctx.beginPath();
    ctx.ellipse(0, 0, 26 + pulse * 5, 11 + pulse * 2, 0, 0, Math.PI * 2);
    ctx.fill();
    ctx.restore();
}

function drawLaserImpact(x, y, progress, now) {
    const pulse = 0.5 + Math.sin(now * 0.075) * 0.5;
    const radius = 10 + pulse * 8 + progress * 8;
    const glow = ctx.createRadialGradient(x, y, 1, x, y, radius * 2.2);
    glow.addColorStop(0, `rgba(255, 255, 255, ${0.84 * progress})`);
    glow.addColorStop(0.32, `rgba(255, 66, 88, ${0.58 * progress})`);
    glow.addColorStop(1, "rgba(255, 66, 88, 0)");
    ctx.fillStyle = glow;
    ctx.beginPath();
    ctx.arc(x, y, radius * 2.2, 0, Math.PI * 2);
    ctx.fill();

    ctx.strokeStyle = `rgba(255, 244, 213, ${0.62 * progress})`;
    ctx.lineWidth = 2;
    for (let i = 0; i < 6; i++) {
        const angle = (Math.PI * 2 * i / 6) + now * 0.012;
        ctx.beginPath();
        ctx.moveTo(x + Math.cos(angle) * 4, y + Math.sin(angle) * 4);
        ctx.lineTo(x + Math.cos(angle) * radius, y + Math.sin(angle) * radius);
        ctx.stroke();
    }
}

function trajectoryReferencePoint(points, visibleCount) {
    const last = points[Math.max(0, visibleCount - 1)];
    for (let offset = 4; offset <= 12; offset += 2) {
        const previous = points[Math.max(0, visibleCount - offset)];
        if (!previous) {
            continue;
        }

        const dx = last.x - previous.x;
        const dy = last.y - previous.y;
        if ((dx * dx) + (dy * dy) > 0.05) {
            return previous;
        }
    }

    return points[Math.max(0, visibleCount - 2)] ?? last;
}

function drawMirvTrail(points, visibleCount, explosions) {
    const apexIndex = findTrajectoryApexIndex(points);
    const apex = points[apexIndex];
    const primaryCount = Math.min(visibleCount, apexIndex + 1);
    drawMirvPath(points, 0, primaryCount, "rgba(255, 246, 191, 0.82)", 3);

    if (visibleCount <= apexIndex + 1) {
        const last = points[primaryCount - 1] ?? apex;
        const prev = points[Math.max(0, primaryCount - 4)] ?? last;
        drawOrientedSprite("shell", last.x, last.y, 24, 10, Math.atan2(last.y - prev.y, last.x - prev.x));
        return;
    }

    const splitProgress = clamp((visibleCount - apexIndex) / Math.max(1, points.length - apexIndex), 0, 1);
    drawMirvSplitFlash(apex, splitProgress);
    const fallbackTarget = points[points.length - 1];
    const targetCount = explosions?.length ? explosions.length : 1;
    for (let i = 0; i < targetCount; i++) {
        const target = explosions?.length ? explosions[i] : fallbackTarget;
        const targetX = Number(target.x ?? fallbackTarget.x);
        const targetY = Number(target.y ?? fallbackTarget.y);
        const lane = i - ((targetCount - 1) / 2);
        const controlX = apex.x + lane * 24;
        const controlY = apex.y - 18 - Math.abs(lane) * 9;
        const previousProgress = Math.max(0, splitProgress - 0.045);
        const currentX = quadraticScalar(apex.x, controlX, targetX, splitProgress);
        const currentY = quadraticScalar(apex.y, controlY, targetY, splitProgress);
        const previousX = quadraticScalar(apex.x, controlX, targetX, previousProgress);
        const previousY = quadraticScalar(apex.y, controlY, targetY, previousProgress);
        drawMirvFragmentTrail(apex.x, apex.y, controlX, controlY, targetX, targetY, splitProgress, i);
        drawOrientedSprite("shell", currentX, currentY, 16, 7, Math.atan2(currentY - previousY, currentX - previousX));
    }
}

function drawMirvPath(points, start, end, color, width) {
    if (end - start < 2) {
        return;
    }

    ctx.lineCap = "round";
    ctx.lineJoin = "round";
    ctx.strokeStyle = "rgba(7, 10, 13, 0.36)";
    ctx.lineWidth = width + 3;
    ctx.beginPath();
    for (let i = start; i < end; i++) {
        const point = points[i];
        if (i === start) {
            ctx.moveTo(point.x, point.y);
        } else {
            ctx.lineTo(point.x, point.y);
        }
    }
    ctx.stroke();

    ctx.strokeStyle = color;
    ctx.lineWidth = width;
    ctx.beginPath();
    for (let i = start; i < end; i++) {
        const point = points[i];
        if (i === start) {
            ctx.moveTo(point.x, point.y);
        } else {
            ctx.lineTo(point.x, point.y);
        }
    }
    ctx.stroke();
}

function drawMirvFragmentTrail(startX, startY, controlX, controlY, targetX, targetY, progress, fragment) {
    const steps = Math.max(2, Math.floor(progress * 14));
    ctx.strokeStyle = fragment % 2 === 0 ? "rgba(255, 220, 98, 0.74)" : "rgba(255, 248, 217, 0.62)";
    ctx.lineWidth = 2;
    ctx.beginPath();
    for (let i = 0; i <= steps; i++) {
        const t = progress * (i / steps);
        const x = quadraticScalar(startX, controlX, targetX, t);
        const y = quadraticScalar(startY, controlY, targetY, t);
        if (i === 0) {
            ctx.moveTo(x, y);
        } else {
            ctx.lineTo(x, y);
        }
    }
    ctx.stroke();

    for (let i = 0; i <= steps; i += 2) {
        const t = progress * (i / steps);
        const x = quadraticScalar(startX, controlX, targetX, t);
        const y = quadraticScalar(startY, controlY, targetY, t);
        const alpha = 0.52 * (i / Math.max(1, steps));
        ctx.fillStyle = `rgba(255, 126, 38, ${alpha})`;
        ctx.fillRect(x - 1, y - 1, 2, 2);
    }
}

function drawMirvSplitFlash(apex, progress) {
    const flash = Math.sin(Math.min(1, progress * 2.2) * Math.PI);
    ctx.fillStyle = `rgba(255, 248, 217, ${0.44 * flash})`;
    ctx.beginPath();
    ctx.arc(apex.x, apex.y, 12 + flash * 18, 0, Math.PI * 2);
    ctx.fill();

    ctx.strokeStyle = `rgba(255, 199, 66, ${0.72 * flash})`;
    ctx.lineWidth = 2;
    for (let i = 0; i < 7; i++) {
        const angle = (Math.PI * 2 * i / 7) + progress * 1.2;
        ctx.beginPath();
        ctx.moveTo(apex.x + Math.cos(angle) * 8, apex.y + Math.sin(angle) * 8);
        ctx.lineTo(apex.x + Math.cos(angle) * (28 + flash * 16), apex.y + Math.sin(angle) * (28 + flash * 16));
        ctx.stroke();
    }
}

function findTrajectoryApexIndex(points) {
    let index = 0;
    let minY = Number.MAX_VALUE;
    for (let i = 0; i < points.length; i++) {
        if (points[i].y < minY) {
            minY = points[i].y;
            index = i;
        }
    }

    return index;
}

function drawSmokeTrail(points, count, weaponId) {
    const napalm = isNapalmWeapon(weaponId);
    const step = Math.max(2, Math.floor(count / 28));
    for (let i = Math.max(0, count - 120); i < count; i += step) {
        const point = points[i];
        const age = (count - i) / Math.max(count, 1);
        const wobble = (hash2d(i, count) % 9) - 4;
        const size = 3 + age * 15 + (i % 3);
        ctx.fillStyle = napalm
            ? `rgba(78, 45, 32, ${0.12 + age * 0.18})`
            : `rgba(62, 64, 62, ${0.12 + age * 0.2})`;
        ctx.beginPath();
        ctx.arc(point.x + wobble, point.y + Math.sin(i) * 2, size, 0, Math.PI * 2);
        ctx.fill();

        if (napalm && i > count - 38) {
            ctx.fillStyle = `rgba(255, 103, 35, ${0.34 * (1 - age)})`;
            ctx.beginPath();
            ctx.arc(point.x - wobble * 0.4, point.y + 2, size * 0.42, 0, Math.PI * 2);
            ctx.fill();
        }
    }
}

function drawDarkEagleTrail(points, count) {
    const visibleCount = Math.min(points.length, count);
    if (visibleCount <= 0) {
        return;
    }

    const start = Math.max(0, visibleCount - 150);
    ctx.lineCap = "round";
    ctx.lineJoin = "round";
    ctx.strokeStyle = "rgba(7, 10, 13, 0.62)";
    ctx.lineWidth = 7;
    ctx.beginPath();
    for (let index = start; index < visibleCount; index++) {
        const point = points[index];
        if (index === start) {
            ctx.moveTo(point.x, point.y);
        } else {
            ctx.lineTo(point.x, point.y);
        }
    }
    ctx.stroke();

    ctx.strokeStyle = "rgba(255, 237, 127, 0.92)";
    ctx.lineWidth = 3.2;
    ctx.beginPath();
    for (let index = start; index < visibleCount; index++) {
        const point = points[index];
        if (index === start) {
            ctx.moveTo(point.x, point.y);
        } else {
            ctx.lineTo(point.x, point.y);
        }
    }
    ctx.stroke();

    const smokeStep = Math.max(2, Math.floor(visibleCount / 34));
    for (let i = start; i < visibleCount; i += smokeStep) {
        const point = points[i];
        const age = (visibleCount - i) / Math.max(1, visibleCount - start);
        const size = 5 + age * 18;
        ctx.fillStyle = `rgba(42, 45, 47, ${0.2 * age})`;
        ctx.beginPath();
        ctx.arc(point.x + ((hash2d(i, visibleCount) % 11) - 5), point.y + Math.sin(i * 0.31) * 5, size, 0, Math.PI * 2);
        ctx.fill();
    }

    const last = points[visibleCount - 1];
    const prev = points[Math.max(0, visibleCount - 5)];
    drawFlameTip(last, prev, true);
    drawOrientedSprite("missile", last.x, last.y, 58, 22, Math.atan2(last.y - prev.y, last.x - prev.x));
}

function drawPatriotCountermeasure(scene, options, pathProgress) {
    if (!options?.intercepted || options.interceptX === undefined || options.interceptY === undefined) {
        return;
    }

    const patriot = options.patriot ?? {
        apexX: Number(options.interceptX),
        apexY: Number(options.interceptY),
        apexProgress: 0.24,
        launchProgressStart: 0.32,
        interceptX: Number(options.interceptX),
        interceptY: Number(options.interceptY)
    };
    const incomingOwner = String(options.ownerTankId ?? "");
    const playerTank = scene.player;
    const cpuTank = scene.cpu;
    const launcher = incomingOwner === String(playerTank?.id ?? "") ? cpuTank : playerTank;
    if (!launcher) {
        return;
    }

    const startX = launcher.x + (launcher.isCpu ? -28 : 28);
    const startY = launcher.y - 46;
    const apexX = Number(patriot.apexX ?? options.interceptX);
    const apexY = Number(patriot.apexY ?? options.interceptY);
    const endX = Number(patriot.interceptX ?? options.interceptX);
    const endY = Number(patriot.interceptY ?? options.interceptY);
    const apexProgress = Number(patriot.apexProgress ?? 0.24);
    const holdStart = Number(patriot.holdStartProgress ?? Math.max(0, apexProgress - 0.02));
    const holdEnd = Number(patriot.holdEndProgress ?? (apexProgress + 0.14));
    const lockStart = Number(patriot.lockProgressStart ?? Math.max(0, holdStart - 0.08));
    const launchStart = Number(patriot.launchProgressStart ?? (holdEnd + 0.012));
    const lockProgress = clamp((pathProgress - lockStart) / Math.max(0.05, holdStart - lockStart), 0, 1);
    const launchProgress = clamp((pathProgress - launchStart) / Math.max(0.08, 1 - launchStart), 0, 1);
    const missileProgress = launchProgress * launchProgress * (3 - (2 * launchProgress));
    const controlX = startX + ((endX - startX) * 0.22);
    const controlY = Math.min(startY, endY, apexY) - 155 - Math.abs(endX - startX) * 0.04;
    const x = quadraticScalar(startX, controlX, endX, missileProgress);
    const y = quadraticScalar(startY, controlY, endY, missileProgress);
    const tangentT = Math.min(1, missileProgress + 0.012);
    const tangentX = quadraticScalar(startX, controlX, endX, tangentT);
    const tangentY = quadraticScalar(startY, controlY, endY, tangentT);
    const angle = Math.atan2(tangentY - y, tangentX - x);
    const now = performance.now();

    ctx.save();
    setWorldTransform();

    if (lockProgress > 0) {
        const postLaunchFade = launchProgress > 0 ? Math.max(0.35, 1 - launchProgress * 0.75) : 1;
        drawPatriotReticle(apexX, apexY, lockProgress * postLaunchFade, now, patriotReticleScale);
    }

    if (pathProgress >= holdStart && pathProgress <= holdEnd) {
        drawPatriotHoldField(apexX, apexY, clamp((pathProgress - holdStart) / Math.max(0.001, holdEnd - holdStart), 0, 1), now);
    }

    if (launchProgress > 0) {
        drawPatriotLaunchPlume(startX, startY, launchProgress);
        drawPatriotFlightTrail(startX, startY, controlX, controlY, endX, endY, missileProgress);
        drawPatriotInterceptGuide(apexX, apexY, endX, endY, launchProgress, now);
        if (launchProgress > 0.72) {
            drawInterceptBanner(endX, endY, clamp((launchProgress - 0.72) / 0.28, 0, 1), now);
        }

        const glow = ctx.createRadialGradient(x, y, 1, x, y, 44);
        glow.addColorStop(0, "rgba(255, 255, 255, 0.95)");
        glow.addColorStop(0.42, "rgba(121, 214, 255, 0.58)");
        glow.addColorStop(1, "rgba(121, 214, 255, 0)");
        ctx.fillStyle = glow;
        ctx.beginPath();
        ctx.arc(x, y, 44, 0, Math.PI * 2);
        ctx.fill();

        drawOrientedSprite("missile", x, y, 72, 27, angle);
    }

    ctx.restore();
}

function drawInterceptBanner(x, y, progress, now) {
    const rise = Math.sin(progress * Math.PI) * 24;
    const scale = 0.82 + Math.sin(progress * Math.PI) * 0.28;
    const alpha = clamp01(Math.sin(progress * Math.PI));
    const text = "INTERCEPTED!";
    ctx.save();
    ctx.translate(x, y - 92 - rise);
    ctx.scale(scale, scale);
    ctx.textAlign = "center";
    ctx.textBaseline = "middle";
    ctx.font = "900 34px system-ui, sans-serif";
    ctx.lineWidth = 8;
    ctx.strokeStyle = `rgba(8, 12, 18, ${0.86 * alpha})`;
    ctx.strokeText(text, 0, 0);

    const colors = ["#ff4d6d", "#f2c14e", "#7ee2d5", "#79d6ff"];
    for (let i = 0; i < colors.length; i++) {
        const wobble = Math.sin(now * 0.012 + i) * 2.5;
        ctx.fillStyle = colors[i];
        ctx.globalAlpha = alpha * (0.5 + i * 0.13);
        ctx.fillText(text, wobble + (i - 1.5) * 2, (i - 1.5) * 1.7);
    }

    ctx.globalAlpha = alpha;
    ctx.strokeStyle = "rgba(255, 248, 217, 0.78)";
    ctx.lineWidth = 2;
    ctx.strokeText(text, 0, 0);
    ctx.restore();
}

function drawPatriotReticle(x, y, alpha, now, scale = 1) {
    const pulse = 0.5 + Math.sin(now * 0.018) * 0.5;
    const spin = now * 0.0045;
    const radius = 34 * scale;
    const armInner = 12 * scale;
    const armOuter = 42 * scale;

    ctx.save();
    ctx.globalAlpha = clamp01(alpha);
    ctx.strokeStyle = `rgba(255, 248, 217, ${0.42 + pulse * 0.34})`;
    ctx.lineWidth = 2 + scale;
    ctx.lineCap = "round";
    strokeArcSegment(x, y, radius, spin, spin + Math.PI * 0.72);
    strokeArcSegment(x, y, radius, spin + Math.PI, spin + Math.PI * 1.72);

    ctx.strokeStyle = `rgba(121, 214, 255, ${0.52 + pulse * 0.28})`;
    ctx.lineWidth = 1.5 + scale * 0.5;
    ctx.lineCap = "butt";
    ctx.beginPath();
    ctx.arc(x, y, radius * 0.55, 0, Math.PI * 2);
    ctx.stroke();

    ctx.strokeStyle = `rgba(255, 248, 217, ${0.7 * alpha})`;
    ctx.beginPath();
    ctx.moveTo(x - armOuter, y);
    ctx.lineTo(x - armInner, y);
    ctx.moveTo(x + armInner, y);
    ctx.lineTo(x + armOuter, y);
    ctx.moveTo(x, y - armOuter);
    ctx.lineTo(x, y - armInner);
    ctx.moveTo(x, y + armInner);
    ctx.lineTo(x, y + armOuter);
    ctx.stroke();
    ctx.restore();
}

function strokeArcSegment(x, y, radius, startAngle, endAngle) {
    ctx.beginPath();
    ctx.arc(x, y, radius, startAngle, endAngle);
    ctx.stroke();
}

function drawPatriotHoldField(x, y, progress, now) {
    const fade = Math.sin(progress * Math.PI);
    const pulse = 0.5 + Math.sin(now * 0.032) * 0.5;
    ctx.save();
    ctx.strokeStyle = `rgba(121, 214, 255, ${0.18 + fade * 0.34})`;
    ctx.lineWidth = 2;
    ctx.setLineDash([5, 8]);
    ctx.beginPath();
    ctx.arc(x, y, 52 + pulse * 9, 0, Math.PI * 2);
    ctx.stroke();
    ctx.setLineDash([]);
    ctx.fillStyle = `rgba(121, 214, 255, ${0.08 + fade * 0.08})`;
    ctx.beginPath();
    ctx.arc(x, y, 18 + pulse * 6, 0, Math.PI * 2);
    ctx.fill();
    ctx.restore();
}

function drawPatriotLaunchPlume(x, y, launchProgress) {
    const alpha = 1 - launchProgress * 0.35;
    const plume = ctx.createRadialGradient(x, y, 1, x, y, 30 + launchProgress * 22);
    plume.addColorStop(0, `rgba(255, 255, 255, ${0.82 * alpha})`);
    plume.addColorStop(0.34, `rgba(121, 214, 255, ${0.56 * alpha})`);
    plume.addColorStop(0.72, `rgba(242, 193, 78, ${0.18 * alpha})`);
    plume.addColorStop(1, "rgba(121, 214, 255, 0)");
    ctx.fillStyle = plume;
    ctx.beginPath();
    ctx.arc(x, y, 48, 0, Math.PI * 2);
    ctx.fill();
}

function drawPatriotFlightTrail(startX, startY, controlX, controlY, endX, endY, missileProgress) {
    const tailStart = Math.max(0, missileProgress - 0.24);
    const segments = 18;
    ctx.lineCap = "round";
    for (let pass = 0; pass < 2; pass++) {
        ctx.strokeStyle = pass === 0 ? "rgba(73, 175, 255, 0.28)" : "rgba(255, 248, 217, 0.74)";
        ctx.lineWidth = pass === 0 ? 9 : 3;
        ctx.beginPath();
        for (let i = 0; i <= segments; i++) {
            const t = tailStart + ((missileProgress - tailStart) * (i / segments));
            const x = quadraticScalar(startX, controlX, endX, t);
            const y = quadraticScalar(startY, controlY, endY, t);
            if (i === 0) {
                ctx.moveTo(x, y);
            } else {
                ctx.lineTo(x, y);
            }
        }
        ctx.stroke();
    }
}

function drawPatriotInterceptGuide(apexX, apexY, endX, endY, launchProgress, now) {
    const alpha = Math.max(0, 0.34 * (1 - launchProgress * 0.55));
    ctx.save();
    ctx.setLineDash([8, 10]);
    ctx.strokeStyle = `rgba(255, 248, 217, ${alpha})`;
    ctx.lineWidth = 2;
    ctx.beginPath();
    ctx.moveTo(apexX, apexY);
    const wobble = Math.sin(now * 0.01) * 5;
    ctx.quadraticCurveTo((apexX + endX) * 0.5, Math.min(apexY, endY) - 28 + wobble, endX, endY);
    ctx.stroke();
    ctx.setLineDash([]);
    ctx.restore();
}

function drawDroneSwarmTrail(points, count, weaponId) {
    const droneCount = 5;
    const visibleCount = Math.min(points.length, count);
    if (visibleCount <= 0) {
        return;
    }

    for (let drone = 0; drone < droneCount; drone++) {
        const lag = drone * 4;
        const headIndex = Math.max(0, visibleCount - 1 - lag);
        const startIndex = Math.max(0, headIndex - 92);
        const phase = drone * 1.73;
        const spin = droneSpinDirection(drone, points.length);
        ctx.fillStyle = drone % 2 === 0 ? "rgba(255, 231, 139, 0.7)" : "rgba(126, 226, 213, 0.62)";

        for (let i = startIndex; i <= headIndex; i += 6) {
            const point = dronePoint(points, i, drone, phase, spin);
            const age = (headIndex - i) / Math.max(1, headIndex - startIndex);
            const alpha = Math.max(0.12, 0.52 * (1 - age));
            ctx.fillStyle = `rgba(255, 231, 139, ${alpha})`;
            ctx.beginPath();
            ctx.arc(point.x, point.y, 1.5 + (drone % 2), 0, Math.PI * 2);
            ctx.fill();
        }

        const head = dronePoint(points, headIndex, drone, phase, spin);
        const tailIndex = Math.max(0, headIndex - 5);
        const tail = dronePoint(points, tailIndex, drone, phase, spin);
        const baseAngle = Math.atan2(head.y - tail.y, head.x - tail.x);
        const angle = baseAngle + Math.sin((headIndex * 0.045) + phase) * 0.16;
        drawShahedDrone(head.x, head.y, angle, weaponId, 1 + (drone % 3) * 0.06);
    }
}

function dronePoint(points, index, drone, phase, spin) {
    const point = points[index];
    const prev = points[Math.max(0, index - 3)] ?? point;
    const next = points[Math.min(points.length - 1, index + 3)] ?? point;
    const dx = next.x - prev.x;
    const dy = next.y - prev.y;
    const length = Math.max(1, Math.hypot(dx, dy));
    const tangentX = dx / length;
    const tangentY = dy / length;
    const normalX = -tangentY;
    const normalY = tangentX;
    const lane = drone - 2;
    const wanderRate = (0.17 + (drone * 0.033)) * spin;
    const counterRate = (0.11 + (drone * 0.021)) * -spin;
    const swirl = Math.sin((index * wanderRate) + phase) * (16 + (drone % 3) * 3.5);
    const corkscrew = Math.cos((index * counterRate) + phase * 1.9) * (4 + (drone % 2) * 2);
    const offset = (lane * 9) + swirl + Math.sin(index * 0.045 + phase * 2.2) * 6;
    return {
        x: point.x + (normalX * offset) + (tangentX * corkscrew),
        y: point.y + (normalY * offset) + (tangentY * corkscrew)
    };
}

function droneSpinDirection(drone, seed) {
    return (hash2d(drone + 11, seed + 37) % 2) === 0 ? 1 : -1;
}

function drawShahedDrone(x, y, angle, weaponId, scale = 1) {
    ctx.save();
    ctx.translate(x, y);
    ctx.rotate(angle);
    ctx.scale(scale, scale);
    if (extraSprites.shahedDrone) {
        ctx.drawImage(extraSprites.shahedDrone, -24, -12, 48, 24);
        ctx.restore();
        return;
    }

    ctx.fillStyle = "rgba(0, 0, 0, 0.34)";
    ctx.beginPath();
    ctx.ellipse(0, 4, 15, 5, 0, 0, Math.PI * 2);
    ctx.fill();

    ctx.fillStyle = "#15191b";
    ctx.beginPath();
    ctx.moveTo(18, 0);
    ctx.lineTo(-15, -11);
    ctx.lineTo(-7, 0);
    ctx.lineTo(-15, 11);
    ctx.closePath();
    ctx.fill();

    ctx.fillStyle = "#bbc3aa";
    ctx.beginPath();
    ctx.moveTo(14, 0);
    ctx.lineTo(-10, -8);
    ctx.lineTo(-4, 0);
    ctx.lineTo(-10, 8);
    ctx.closePath();
    ctx.fill();

    ctx.fillStyle = "#59666a";
    ctx.fillRect(-7, -2, 17, 4);
    ctx.fillStyle = "#f4e9b4";
    ctx.fillRect(1, -5, 7, 2);
    ctx.fillStyle = "#e96a54";
    ctx.fillRect(-10, -6, 3, 3);
    ctx.fillRect(-10, 3, 3, 3);
    ctx.restore();
}

function drawFlameTip(last, prev, missileLike) {
    if (!missileLike) {
        return;
    }

    const dx = last.x - prev.x;
    const dy = last.y - prev.y;
    const angle = Math.atan2(dy, dx);
    ctx.save();
    ctx.translate(last.x, last.y);
    ctx.rotate(angle);
    const gradient = ctx.createRadialGradient(-6, 0, 1, -6, 0, 15);
    gradient.addColorStop(0, "#fff6bf");
    gradient.addColorStop(0.42, "rgba(255, 126, 38, 0.82)");
    gradient.addColorStop(1, "rgba(255, 62, 28, 0)");
    ctx.fillStyle = gradient;
    ctx.beginPath();
    ctx.ellipse(-8, 0, 18, 7, 0, 0, Math.PI * 2);
    ctx.fill();
    ctx.restore();
}

function drawMopProjectile(last, prev) {
    const angle = Math.atan2(last.y - prev.y, last.x - prev.x);
    ctx.save();
    ctx.translate(last.x, last.y);
    ctx.rotate(angle);
    if (extraSprites.gbu57Mop) {
        ctx.drawImage(extraSprites.gbu57Mop, -39, -15, 78, 30);
        ctx.restore();
        return;
    }

    ctx.fillStyle = "rgba(0, 0, 0, 0.48)";
    ctx.beginPath();
    ctx.ellipse(-3, 6, 30, 8, 0, 0, Math.PI * 2);
    ctx.fill();

    const body = ctx.createLinearGradient(-30, -7, -30, 7);
    body.addColorStop(0, "#f2f1dc");
    body.addColorStop(0.32, "#9da7ad");
    body.addColorStop(0.72, "#3d454d");
    body.addColorStop(1, "#12171e");
    ctx.fillStyle = body;
    ctx.beginPath();
    ctx.moveTo(30, 0);
    ctx.lineTo(17, -8);
    ctx.lineTo(-26, -8);
    ctx.lineTo(-35, 0);
    ctx.lineTo(-26, 8);
    ctx.lineTo(17, 8);
    ctx.closePath();
    ctx.fill();

    ctx.fillStyle = "#f0d264";
    ctx.fillRect(-14, -5, 5, 10);
    ctx.fillStyle = "#161b23";
    ctx.fillRect(-30, -13, 11, 6);
    ctx.fillRect(-30, 7, 11, 6);
    ctx.fillStyle = "#fff8d9";
    ctx.fillRect(8, -4, 12, 2);
    ctx.restore();
}

function drawTriggeredExplosions(explosions, visibleTrailCount, now, starts) {
    if (!explosions.length) {
        return;
    }

    ctx.save();
    setWorldTransform();
    for (const explosion of explosions) {
        const triggerIndex = Number(explosion.triggerIndex ?? -1);
        if (triggerIndex < 0 || visibleTrailCount <= triggerIndex) {
            continue;
        }

        const key = `${triggerIndex}:${Math.round(explosion.x)}:${Math.round(explosion.y)}`;
        if (!starts.has(key)) {
            starts.set(key, now);
        }

        const progress = clamp01((now - starts.get(key)) / 330);
        drawExplosion(explosion, progress);
    }
    ctx.restore();
}

function animateExplosions(scene, explosions, screenShake) {
    const started = performance.now();
    const intense = hasIntenseExplosion(explosions);
    const duration = hasNuclearExplosion(explosions) ? 560 : 420;

    return new Promise(resolve => {
        const tick = now => {
            const t = Math.min(1, (now - started) / duration);
            const strength = Math.sin(t * Math.PI);
            const shake = screenShake ? strength * (intense ? 7 : 3) : 0;
            drawScene(scene, Math.sin(now * 0.09) * shake, Math.cos(now * 0.07) * shake * 0.45);
            drawExplosions(explosions, t);
            if (t < 1) {
                requestAnimationFrame(tick);
                return;
            }

            resolve();
        };

        requestAnimationFrame(tick);
    });
}

function hasNuclearExplosion(explosions) {
    for (let i = 0; i < explosions.length; i++) {
        if (explosions[i].nuclear) {
            return true;
        }
    }

    return false;
}

function hasIntenseExplosion(explosions) {
    for (let i = 0; i < explosions.length; i++) {
        const explosion = explosions[i];
        if (explosion.nuclear || explosion.radius > 80) {
            return true;
        }
    }

    return false;
}

function drawExplosions(explosions, progress = 1) {
    if (!explosions.length) {
        return;
    }

    ctx.save();
    setWorldTransform();
    for (const explosion of explosions) {
        drawExplosion(explosion, progress);
    }
    ctx.restore();
}

function drawExplosion(explosion, progress = 1) {
    const radius = explosion.radius ?? 32;
    const lava = isLavaExplosion(explosion);
    const patriot = isPatriotExplosion(explosion);
    const shieldHit = isShieldHitExplosion(explosion);
    const laser = isLaserExplosion(explosion);
    const penetrator = isPenetratorExplosion(explosion);
    const shieldLike = patriot || shieldHit;
    const bloom = radius * (0.45 + progress * 0.9);
    const fade = Math.max(0, 1 - progress);
    const flash = Math.max(0, 1 - progress * 2.2);
    const spriteName = shieldLike ? "shield" : explosion.nuclear ? "nuclear" : radius > 58 || penetrator ? "explosionLarge" : "explosionSmall";
    const spriteSize = radius * (1.4 + progress * (explosion.nuclear ? 1.4 : 0.95));
    ctx.save();
    ctx.globalAlpha = Math.min(1, 0.25 + progress * 1.2) * Math.max(0.18, 1 - progress * 0.16);
    drawSprite(spriteName, explosion.x - spriteSize / 2, explosion.y - spriteSize / 2, spriteSize, spriteSize);
    ctx.restore();

    const gradient = ctx.createRadialGradient(explosion.x, explosion.y, 2, explosion.x, explosion.y, bloom);
    gradient.addColorStop(0, "#fffdf0");
    gradient.addColorStop(0.22, shieldLike ? "#9fd6ff" : explosion.nuclear ? "#fff0a2" : laser ? "#fff5f4" : lava ? "#fff06a" : penetrator ? "#ffd276" : "#ffdc68");
    gradient.addColorStop(0.58, shieldLike ? "rgba(71, 165, 255, 0.58)" : explosion.nuclear ? "rgba(255, 136, 49, 0.64)" : laser ? "rgba(255, 58, 86, 0.62)" : lava ? "rgba(255, 80, 24, 0.78)" : penetrator ? "rgba(191, 92, 48, 0.72)" : "rgba(236, 106, 92, 0.72)");
    gradient.addColorStop(1, "rgba(28, 22, 18, 0)");
    ctx.fillStyle = gradient;
    ctx.beginPath();
    ctx.arc(explosion.x, explosion.y, bloom, 0, Math.PI * 2);
    ctx.fill();

    ctx.strokeStyle = shieldLike
        ? `rgba(121, 214, 255, ${0.78 * fade})`
        : laser
            ? `rgba(255, 82, 98, ${0.78 * fade})`
            : `rgba(255, 246, 191, ${0.78 * fade})`;
    ctx.lineWidth = shieldLike ? 4 : 5;
    ctx.beginPath();
    ctx.arc(explosion.x, explosion.y, radius * (0.65 + progress * 1.1), 0, Math.PI * 2);
    ctx.stroke();

    drawBlastSparks(explosion, radius, progress, lava || laser);
    drawSmokePuffs(explosion, radius, progress);

    if (flash > 0) {
        ctx.fillStyle = `rgba(255, 255, 255, ${flash * 0.72})`;
        ctx.beginPath();
        ctx.arc(explosion.x, explosion.y, radius * (0.18 + progress * 0.35), 0, Math.PI * 2);
        ctx.fill();
    }

    if (patriot) {
        drawPatriotIntercept(explosion, radius, progress);
    } else if (shieldHit) {
        drawShieldAbsorption(explosion, radius, progress);
    } else if (penetrator) {
        drawPenetratorShock(explosion, radius, progress);
    } else if (explosion.nuclear) {
        drawNukeColumn(explosion, radius, progress);
        ctx.strokeStyle = `rgba(255, 222, 82, ${0.75 * fade})`;
        ctx.lineWidth = 6;
        ctx.beginPath();
        ctx.arc(explosion.x, explosion.y, radius * (1.1 + progress * 1.15), 0, Math.PI * 2);
        ctx.stroke();
        drawNuclearGroundFlash(explosion, radius, progress);
    } else if (lava) {
        drawLavaSplash(explosion, radius, progress);
    } else if (laser) {
        drawLaserImpactBurst(explosion, radius, progress);
    }
}

function drawBlastSparks(explosion, radius, progress, lava = false) {
    const count = explosion.nuclear ? 28 : lava ? 20 : 14;
    ctx.strokeStyle = lava ? `rgba(255, 82, 28, ${Math.max(0, 1 - progress * 0.75)})` : `rgba(255, 218, 104, ${Math.max(0, 1 - progress)})`;
    ctx.lineWidth = 2;
    for (let i = 0; i < count; i++) {
        const angle = (Math.PI * 2 * i / count) + hash2d(i, radius) * 0.0009;
        const inner = radius * (0.24 + progress * 0.38);
        const outer = radius * (0.42 + progress * ((lava ? 1.02 : 0.78) + (i % 3) * 0.12));
        ctx.beginPath();
        ctx.moveTo(explosion.x + Math.cos(angle) * inner, explosion.y + Math.sin(angle) * inner);
        ctx.lineTo(explosion.x + Math.cos(angle) * outer, explosion.y + Math.sin(angle) * outer);
        ctx.stroke();
    }
}

function drawSmokePuffs(explosion, radius, progress) {
    const count = explosion.nuclear ? 11 : 7;
    for (let i = 0; i < count; i++) {
        const angle = Math.PI * 2 * i / count;
        const drift = radius * (0.28 + progress * 0.55);
        const puffRadius = radius * (0.12 + progress * 0.18) * (1 + (i % 3) * 0.16);
        const x = explosion.x + Math.cos(angle) * drift;
        const y = explosion.y + Math.sin(angle) * drift - progress * radius * 0.16;
        ctx.fillStyle = `rgba(42, 37, 31, ${0.34 * progress * (1 - progress * 0.35)})`;
        ctx.beginPath();
        ctx.arc(x, y, puffRadius, 0, Math.PI * 2);
        ctx.fill();
    }
}

function drawShieldAbsorption(explosion, radius, progress) {
    const fade = Math.max(0, 1 - progress);
    ctx.strokeStyle = `rgba(121, 214, 255, ${0.76 * fade})`;
    ctx.lineWidth = 4;
    for (let i = 0; i < 3; i++) {
        ctx.beginPath();
        ctx.arc(explosion.x, explosion.y, radius * (0.55 + progress * (0.78 + i * 0.28)), 0, Math.PI * 2);
        ctx.stroke();
    }

    ctx.strokeStyle = `rgba(255, 255, 255, ${0.58 * fade})`;
    ctx.lineWidth = 2;
    for (let i = 0; i < 8; i++) {
        const angle = (Math.PI * 2 * i / 8) - progress * 0.7;
        const inner = radius * (0.18 + progress * 0.16);
        const outer = radius * (0.62 + progress * 0.4);
        ctx.beginPath();
        ctx.moveTo(explosion.x + Math.cos(angle) * inner, explosion.y + Math.sin(angle) * inner);
        ctx.lineTo(explosion.x + Math.cos(angle) * outer, explosion.y + Math.sin(angle) * outer);
        ctx.stroke();
    }
}

function drawLaserImpactBurst(explosion, radius, progress) {
    const fade = Math.max(0, 1 - progress);
    ctx.strokeStyle = `rgba(255, 246, 222, ${0.64 * fade})`;
    ctx.lineWidth = 2;
    for (let i = 0; i < 9; i++) {
        const angle = (Math.PI * 2 * i / 9) + progress * 0.8;
        const inner = radius * (0.12 + progress * 0.18);
        const outer = radius * (0.48 + progress * 0.52);
        ctx.beginPath();
        ctx.moveTo(explosion.x + Math.cos(angle) * inner, explosion.y + Math.sin(angle) * inner);
        ctx.lineTo(explosion.x + Math.cos(angle) * outer, explosion.y + Math.sin(angle) * outer);
        ctx.stroke();
    }
}

function drawLavaSplash(explosion, radius, progress) {
    const fade = Math.max(0, 1 - progress * 0.55);
    drawLavaSprite(explosion.x, explosion.y + radius * 0.18, radius * (0.9 + progress * 0.22), progress * 7, fade);
    for (let i = 0; i < 13; i++) {
        const angle = -Math.PI + (Math.PI * i / 12);
        const arc = radius * (0.35 + progress * (0.55 + (i % 4) * 0.1));
        const x = explosion.x + Math.cos(angle) * arc;
        const y = explosion.y + Math.sin(angle) * arc + progress * radius * 0.65;
        ctx.fillStyle = i % 2 === 0 ? `rgba(255, 215, 74, ${0.75 * fade})` : `rgba(255, 75, 24, ${0.78 * fade})`;
        ctx.beginPath();
        ctx.arc(x, y, radius * (0.035 + (i % 3) * 0.012), 0, Math.PI * 2);
        ctx.fill();
    }

    ctx.fillStyle = `rgba(255, 68, 22, ${0.28 * fade})`;
    ctx.fillRect(explosion.x - radius * 0.55, explosion.y + radius * 0.18, radius * 1.1, Math.max(3, radius * 0.08));
    ctx.fillStyle = `rgba(255, 225, 92, ${0.34 * fade})`;
    ctx.fillRect(explosion.x - radius * 0.34, explosion.y + radius * 0.2, radius * 0.68, Math.max(2, radius * 0.035));
}

function drawLavaSprite(x, y, size, phase, alpha = 1) {
    const width = Math.max(18, size * 1.35);
    const height = Math.max(10, size * 0.45);
    ctx.save();
    ctx.globalAlpha = clamp01(alpha);
    if (extraSprites.lavaPool) {
        ctx.drawImage(extraSprites.lavaPool, x - width * 0.5, y - height * 0.5, width, height);
    } else {
        ctx.fillStyle = "rgba(63, 17, 12, 0.92)";
        ctx.fillRect(x - width * 0.5, y - height * 0.38, width, height * 0.72);
        ctx.fillStyle = "#ff4c1b";
        ctx.fillRect(x - width * 0.42, y - height * 0.22, width * 0.84, height * 0.42);
        ctx.fillStyle = "#ffd84d";
        ctx.fillRect(x - width * 0.22, y - height * 0.12, width * 0.38, height * 0.2);
    }

    const bubbleCount = 6;
    for (let i = 0; i < bubbleCount; i++) {
        const t = (Math.sin(phase + i * 1.31) + 1) * 0.5;
        const bx = x - width * 0.36 + width * ((i + 0.5) / bubbleCount);
        const by = y - height * (0.05 + t * 0.36);
        ctx.fillStyle = i % 2 === 0 ? `rgba(255, 216, 72, ${0.62 * alpha})` : `rgba(255, 83, 24, ${0.58 * alpha})`;
        ctx.fillRect(bx, by, Math.max(2, width * 0.045), Math.max(2, height * 0.16));
    }

    ctx.restore();
}

function drawNukeColumn(explosion, radius, progress) {
    const fade = Math.max(0, 1 - progress * 0.45);
    const stemHeight = radius * (1.15 + progress * 1.35);
    const stemWidth = radius * (0.22 + progress * 0.18);
    const x = explosion.x;
    const y = explosion.y;

    const stemGradient = ctx.createLinearGradient(x, y - stemHeight, x, y + radius * 0.2);
    stemGradient.addColorStop(0, `rgba(255, 235, 164, ${0.25 * fade})`);
    stemGradient.addColorStop(0.48, `rgba(176, 132, 82, ${0.32 * fade})`);
    stemGradient.addColorStop(1, `rgba(57, 45, 35, ${0.36 * fade})`);
    ctx.fillStyle = stemGradient;
    ctx.beginPath();
    ctx.ellipse(x, y - stemHeight * 0.45, stemWidth, stemHeight * 0.55, 0, 0, Math.PI * 2);
    ctx.fill();

    const capY = y - stemHeight * (0.82 + progress * 0.08);
    const capRadius = radius * (0.72 + progress * 0.62);
    const lobes = [
        { dx: 0, dy: -0.1, rx: 0.72, ry: 0.36 },
        { dx: -0.52, dy: 0.08, rx: 0.45, ry: 0.28 },
        { dx: 0.52, dy: 0.06, rx: 0.48, ry: 0.3 },
        { dx: -0.18, dy: -0.28, rx: 0.42, ry: 0.26 },
        { dx: 0.2, dy: -0.3, rx: 0.42, ry: 0.26 }
    ];

    for (const lobe of lobes) {
        const lobeGradient = ctx.createRadialGradient(
            x + capRadius * lobe.dx,
            capY + capRadius * lobe.dy,
            2,
            x + capRadius * lobe.dx,
            capY + capRadius * lobe.dy,
            capRadius * lobe.rx);
        lobeGradient.addColorStop(0, `rgba(255, 246, 192, ${0.5 * fade})`);
        lobeGradient.addColorStop(0.48, `rgba(181, 141, 93, ${0.42 * fade})`);
        lobeGradient.addColorStop(1, `rgba(38, 33, 29, ${0.1 * fade})`);
        ctx.fillStyle = lobeGradient;
        ctx.beginPath();
        ctx.ellipse(
            x + capRadius * lobe.dx,
            capY + capRadius * lobe.dy,
            capRadius * lobe.rx,
            capRadius * lobe.ry,
            0,
            0,
            Math.PI * 2);
        ctx.fill();
    }

    ctx.strokeStyle = `rgba(255, 232, 132, ${0.48 * (1 - progress)})`;
    ctx.lineWidth = 3;
    for (let i = 0; i < 3; i++) {
        ctx.beginPath();
        ctx.ellipse(
            x,
            capY + radius * (0.05 + i * 0.12),
            radius * (0.48 + progress * 0.86 + i * 0.24),
            radius * (0.18 + progress * 0.25 + i * 0.08),
            0,
            0,
            Math.PI * 2);
        ctx.stroke();
    }

    if (progress > 0.18) {
        drawRadioactiveGlyph(x, capY, clamp(radius * 0.18, 28, 52), 0.62 * fade);
    }
}

function drawNuclearGroundFlash(explosion, radius, progress) {
    const fade = Math.max(0, 1 - progress * 0.72);
    ctx.save();
    ctx.translate(explosion.x, explosion.y + radius * 0.1);
    ctx.scale(1, 0.28);
    const flash = ctx.createRadialGradient(0, 0, radius * 0.12, 0, 0, radius * (1.45 + progress * 0.9));
    flash.addColorStop(0, `rgba(255, 248, 176, ${0.36 * fade})`);
    flash.addColorStop(0.46, `rgba(255, 170, 64, ${0.22 * fade})`);
    flash.addColorStop(1, "rgba(255, 170, 64, 0)");
    ctx.fillStyle = flash;
    ctx.beginPath();
    ctx.arc(0, 0, radius * (1.45 + progress * 0.9), 0, Math.PI * 2);
    ctx.fill();
    ctx.restore();
}

function drawRadioactiveGlyph(x, y, size, alpha = 1) {
    ctx.save();
    ctx.translate(x, y);
    ctx.globalAlpha = clamp01(alpha);

    ctx.fillStyle = "rgba(12, 14, 12, 0.72)";
    ctx.beginPath();
    ctx.arc(2, 3, size * 0.58, 0, Math.PI * 2);
    ctx.fill();

    ctx.fillStyle = "#ffd64d";
    ctx.beginPath();
    ctx.arc(0, 0, size * 0.52, 0, Math.PI * 2);
    ctx.fill();
    ctx.strokeStyle = "#151814";
    ctx.lineWidth = Math.max(2, size * 0.08);
    ctx.stroke();

    ctx.fillStyle = "#151814";
    for (let i = 0; i < 3; i++) {
        const angle = (-Math.PI / 2) + (i * Math.PI * 2 / 3);
        ctx.beginPath();
        ctx.moveTo(Math.cos(angle - 0.22) * size * 0.18, Math.sin(angle - 0.22) * size * 0.18);
        ctx.arc(0, 0, size * 0.43, angle - 0.46, angle + 0.46);
        ctx.lineTo(Math.cos(angle + 0.22) * size * 0.18, Math.sin(angle + 0.22) * size * 0.18);
        ctx.closePath();
        ctx.fill();
    }

    ctx.beginPath();
    ctx.arc(0, 0, size * 0.12, 0, Math.PI * 2);
    ctx.fill();
    ctx.restore();
}

function shotDuration(pointCount, weaponId) {
    if (isDarkEagleWeapon(weaponId)) {
        return 2900;
    }

    if (isDroneWeapon(weaponId)) {
        return Math.min(3400, Math.max(1500, pointCount * 13));
    }

    if (isMirvWeapon(weaponId)) {
        return Math.min(1800, Math.max(900, pointCount * 5.5));
    }

    const dramatic = isMopWeapon(weaponId);
    return Math.min(
        dramatic ? 2100 : 1200,
        Math.max(dramatic ? 900 : 260, pointCount * (dramatic ? 8.5 : 4)));
}

function shotPathProgress(t, weaponId) {
    return isDarkEagleWeapon(weaponId)
        ? darkEagleFlightProgress(t)
        : t;
}

function darkEagleFlightProgress(t) {
    if (t < 0.64) {
        return (1 - Math.pow(1 - (t / 0.64), 2.1)) * 0.48;
    }

    return 0.48 + (Math.pow((t - 0.64) / 0.36, 1.9) * 0.52);
}

function drawPatriotIntercept(explosion, radius, progress) {
    const fade = Math.max(0, 1 - progress);
    ctx.strokeStyle = `rgba(113, 198, 255, ${0.82 * fade})`;
    ctx.lineWidth = 3;
    for (let i = 0; i < 3; i++) {
        ctx.beginPath();
        ctx.arc(explosion.x, explosion.y, radius * (0.45 + progress * (0.78 + i * 0.24)), 0, Math.PI * 2);
        ctx.stroke();
    }

    ctx.strokeStyle = `rgba(255, 248, 217, ${0.75 * fade})`;
    ctx.lineWidth = 2;
    for (let i = 0; i < 10; i++) {
        const angle = (Math.PI * 2 * i / 10) + progress * 0.4;
        ctx.beginPath();
        ctx.moveTo(explosion.x + Math.cos(angle) * radius * 0.18, explosion.y + Math.sin(angle) * radius * 0.18);
        ctx.lineTo(explosion.x + Math.cos(angle) * radius * (0.9 + progress * 0.42), explosion.y + Math.sin(angle) * radius * (0.9 + progress * 0.42));
        ctx.stroke();
    }
}

function drawPenetratorShock(explosion, radius, progress) {
    const kind = String(explosion.visualKind ?? "").toLowerCase();
    const secondary = kind.includes("secondary");
    const fade = Math.max(0, 1 - progress);
    ctx.strokeStyle = secondary
        ? `rgba(255, 216, 118, ${0.82 * fade})`
        : `rgba(255, 246, 191, ${0.62 * fade})`;
    ctx.lineWidth = secondary ? 5 : 3;
    for (let i = 0; i < (secondary ? 3 : 2); i++) {
        ctx.beginPath();
        ctx.ellipse(
            explosion.x,
            explosion.y + radius * 0.1,
            radius * (0.38 + progress * (0.7 + i * 0.22)),
            radius * (0.2 + progress * (0.42 + i * 0.12)),
            0,
            0,
            Math.PI * 2);
        ctx.stroke();
    }

    ctx.fillStyle = `rgba(92, 68, 45, ${0.28 * fade})`;
    ctx.beginPath();
    ctx.ellipse(explosion.x, explosion.y + radius * 0.38, radius * (0.68 + progress * 0.22), radius * 0.16, 0, 0, Math.PI * 2);
    ctx.fill();
}

function sizeCanvas() {
    const rect = canvas.getBoundingClientRect();
    const nextWidth = Math.max(1, Math.floor(rect.width * devicePixelRatio));
    const nextHeight = Math.max(1, Math.floor(rect.height * devicePixelRatio));
    if (canvas.width !== nextWidth || canvas.height !== nextHeight) {
        canvas.width = nextWidth;
        canvas.height = nextHeight;
        ctx.imageSmoothingEnabled = false;
    }
}

function updateStats() {
    const now = performance.now();
    frameMs = now - lastFrame;
    lastFrame = now;
    fps = (fps * 0.9) + ((1000 / Math.max(frameMs, 1)) * 0.1);
}

function startStatsLoop() {
    if (rafId) {
        return;
    }

    const tick = () => {
        const started = performance.now();
        if (lastScene && !shotInProgress) {
            sizeCanvas();
            drawScene(lastScene, 0, 0);
            renderMs = performance.now() - started;
        }

        updateStats();
        rafId = requestAnimationFrame(tick);
    };

    rafId = requestAnimationFrame(tick);
}
