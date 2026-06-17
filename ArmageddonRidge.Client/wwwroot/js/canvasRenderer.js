import { clamp, clamp01, hash2d, positiveModulo, quadraticScalar } from "./rendering/math.js";
import { configureSprites, drawExtraSprite, drawExtraSpriteByHeight, drawOrientedSprite, drawSprite, drawSpriteFacing, hasSprite, loadSprites, spriteFrame } from "./rendering/sprites.js";
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
const shotRafIds = new Set();
const shotTimeoutIds = new Set();
const activeShotCompletes = new Set();
let lastAmbientRedraw = 0;
let shotInProgress = false;
const spriteManifestVersion = "2026-05-04-genesis-v7";
const dronePointCaches = Array.from({ length: 5 }, () => []);
const ambientRedrawIntervalMs = 1000 / 30;
const patriotInterceptDurationScale = 2.6;
const patriotInterceptMinDuration = 2900;
const patriotInterceptMaxDuration = 3400;
const patriotInterceptBannerExtraHoldMs = 500;
const patriotReticleScale = 1.65;
const shieldRadiusX = 90;
const shieldRadiusY = 60;
const shieldCenterYOffset = 52;
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
    const finished = performance.now();
    updateStats(finished);
    renderMs = performance.now() - started;
    lastAmbientRedraw = finished;
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
    const visualPhysics = sanitizeVisualPhysics(playbackOptions.visualPhysics);
    playbackOptions.visualPhysics = visualPhysics;
    playbackOptions.civilianImpacts = sanitizeCivilianImpacts(playbackOptions.civilianImpacts);
    visualPhysics.civilianImpacts = playbackOptions.civilianImpacts;
    let points = sanitizeRenderPoints(Array.isArray(trail) ? trail : Array.from(trail ?? []), 2);
    if (!points.length) {
        shotInProgress = false;
        render(scene);
        return;
    }

    const interceptX = Number(playbackOptions.interceptX);
    const interceptY = Number(playbackOptions.interceptY);
    if (playbackOptions.intercepted && Number.isFinite(interceptX) && Number.isFinite(interceptY)) {
        const patriotPlayback = createPatriotPlayback(points, { x: interceptX, y: interceptY });
        points = patriotPlayback.points;
        playbackOptions.patriot = patriotPlayback;
    }

    const finalDestruction = sanitizeCanvasDestruction(playbackOptions.finalShotDestruction ?? playbackOptions.FinalShotDestruction);
    const activeExplosions = Array.isArray(explosions) ? explosions : Array.from(explosions ?? []);
    applyVisualTankPoses(shotScene, visualPhysics?.tankPoses ?? []);
    const stagedExplosions = [];
    const finalExplosions = [];
    let highImpactShake = false;
    for (let i = 0; i < activeExplosions.length; i++) {
        const explosion = activeExplosions[i];
        if (explosion.nuclear || explosion.radius > 80) {
            highImpactShake = true;
        }

        const triggerIndex = Number(explosion.triggerIndex ?? -1);
        if (triggerIndex >= 0) {
            stagedExplosions.push({
                ...explosion,
                triggerIndex,
                playbackKey: `${triggerIndex}:${Math.round(explosion.x)}:${Math.round(explosion.y)}`
            });
        } else {
            finalExplosions.push(explosion);
        }
    }

    const stagedStarts = new Map();
    const baseDuration = shotDuration(points.length, weaponId, playbackOptions.visualKind);
    const duration = playbackOptions.intercepted
        ? clamp(baseDuration * patriotInterceptDurationScale, patriotInterceptMinDuration, patriotInterceptMaxDuration)
        : baseDuration;
    const extraHoldMs = playbackOptions.intercepted ? patriotInterceptBannerExtraHoldMs : 0;
    const started = performance.now();

    return new Promise(resolve => {
        let completed = false;
        const complete = () => {
            if (completed) {
                return;
            }

            completed = true;
            activeShotCompletes.delete(complete);
            shotInProgress = false;
            resolve();
        };
        activeShotCompletes.add(complete);

        const fail = error => {
            console.error("Shot playback failed", error);
            complete();
        };

        const finish = () => {
            if (!finalExplosions.length) {
                if (!finalDestruction) {
                    scheduleShotTimeout(complete, 120);
                    return;
                }

                animateFinalShotDestruction(scene, playbackOptions, screenShake)
                    .then(complete)
                    .catch(fail);
                return;
            }

            animateExplosions(scene, finalExplosions, screenShake, playbackOptions)
                .then(() => animateFinalShotDestruction(scene, playbackOptions, screenShake))
                .then(complete)
                .catch(fail);
        };

        const tick = now => {
            try {
                if (!ctx) {
                    complete();
                    return;
                }

                const elapsed = now - started;
                const t = Math.min(1, elapsed / duration);
                const holdProgress = extraHoldMs > 0 ? clamp((elapsed - duration) / extraHoldMs, 0, 1) : 1;
                const basePathProgress = shotPathProgress(t, weaponId);
                const patriotTimelineProgress = playbackOptions.patriot ? t : basePathProgress;
                const pathProgress = playbackOptions.patriot
                    ? patriotIncomingPathProgress(playbackOptions.patriot, patriotTimelineProgress)
                    : basePathProgress;
                const count = Math.max(1, Math.floor(points.length * pathProgress));
                const flightShakeLimit = finalDestruction ? (finalDestruction.mutual ? 3.2 : 2.4) : 8;
                const shake = screenShake && highImpactShake ? Math.sin(now * 0.08) * (1 - t) * flightShakeLimit : 0;
                drawScene(shotScene, shake, -shake * 0.4);
                drawTrail(points, count, weaponId, activeExplosions, playbackOptions.visualKind);
                drawPatriotCountermeasure(shotScene, playbackOptions, patriotTimelineProgress, holdProgress);
                drawTriggeredExplosions(stagedExplosions, count, now, stagedStarts);
                if (t < 1 || holdProgress < 1) {
                    scheduleShotFrame(tick);
                    return;
                }

                scheduleShotFrame(() => {
                    try {
                        if (!ctx) {
                            complete();
                            return;
                        }

                        drawScene(shotScene, 0, 0);
                        drawTrail(points, points.length, weaponId, activeExplosions, playbackOptions.visualKind);
                        drawPatriotCountermeasure(shotScene, playbackOptions, 1, 1);
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

        scheduleShotFrame(tick);
    });
}

export function getStats() {
    return { fps: Math.round(fps), frameMs, renderMs };
}

export function sanitizeRenderPoints(points, minCount = 0) {
    if (!Array.isArray(points)) {
        return [];
    }

    const sanitized = [];
    for (let i = 0; i < points.length; i++) {
        const point = points[i];
        const x = Number(point?.x);
        const y = Number(point?.y);
        if (Number.isFinite(x) && Number.isFinite(y)) {
            sanitized.push({ x, y });
        }
    }

    return sanitized.length >= minCount ? sanitized : [];
}

export function sanitizeVisualPhysics(payload) {
    if (!payload || typeof payload !== "object") {
        return {
            slump: { columns: [], durationMs: 0, reducedMotion: false },
            tankPoses: [],
            shockwaves: [],
            debris: [],
            impacts: [],
            lingering: [],
            simdEnabled: false
        };
    }

    return {
        slump: sanitizeSlump(payload.slump),
        tankPoses: sanitizeTankPoses(payload.tankPoses),
        shockwaves: sanitizeFiniteList(payload.shockwaves, item => {
            const x = Number(item?.x);
            const y = Number(item?.y);
            const radius = Number(item?.radius);
            if (!Number.isFinite(x) || !Number.isFinite(y) || !Number.isFinite(radius) || radius <= 0) return null;
            return {
                x,
                y,
                radius: clamp(radius, 1, 3000),
                intensity: clamp(Number(item?.intensity ?? 0), 0, 500),
                directionX: Number(item?.directionX ?? 0),
                directionY: Number(item?.directionY ?? -1),
                terrainDampening: clamp01(Number(item?.terrainDampening ?? 1)),
                visualKind: String(item?.visualKind ?? "")
            };
        }),
        debris: sanitizeFiniteList(payload.debris, item => {
            const x = Number(item?.x);
            const y = Number(item?.y);
            if (!Number.isFinite(x) || !Number.isFinite(y)) return null;
            return {
                x,
                y,
                velocityX: Number(item?.velocityX ?? 0),
                velocityY: Number(item?.velocityY ?? 0),
                friction: clamp01(Number(item?.friction ?? 0.6)),
                bounceDamping: clamp01(Number(item?.bounceDamping ?? 0.35)),
                material: String(item?.material ?? "Dirt")
            };
        }),
        impacts: sanitizeFiniteList(payload.impacts, item => {
            const x = Number(item?.x);
            const y = Number(item?.y);
            if (!Number.isFinite(x) || !Number.isFinite(y)) return null;
            return {
                x,
                y,
                directionX: Number(item?.directionX ?? 0),
                directionY: Number(item?.directionY ?? -1),
                intensity: clamp(Number(item?.intensity ?? 0), 0, 400),
                material: String(item?.material ?? "Dirt"),
                visualKind: String(item?.visualKind ?? ""),
                shieldLike: Boolean(item?.shieldLike)
            };
        }),
        lingering: sanitizeFiniteList(payload.lingering, item => {
            const x = Number(item?.x);
            const y = Number(item?.y);
            if (!Number.isFinite(x) || !Number.isFinite(y)) return null;
            return {
                x,
                y,
                windX: Number(item?.windX ?? 0),
                slopeX: Number(item?.slopeX ?? 0),
                slopeY: Number(item?.slopeY ?? 0),
                lifetime: clamp(Number(item?.lifetime ?? 0), 0, 12),
                intensity: clamp(Number(item?.intensity ?? 0), 0, 4),
                visualKind: String(item?.visualKind ?? "")
            };
        }),
        simdEnabled: Boolean(payload.simdEnabled)
    };
}

function sanitizeCivilianImpacts(impacts) {
    return sanitizeFiniteList(impacts, item => {
        const x = Number(item?.x);
        const y = Number(item?.y);
        if (!Number.isFinite(x) || !Number.isFinite(y)) return null;
        return {
            structureId: String(item?.structureId ?? ""),
            x,
            y,
            damage: clamp(Number(item?.damage ?? 0), 0, 500),
            healthRemaining: Math.max(0, Number(item?.healthRemaining ?? 0)),
            penalty: Math.max(0, Number(item?.penalty ?? 0)),
            collapsed: Boolean(item?.collapsed),
            kind: String(item?.kind ?? "apartment")
        };
    });
}

function sanitizeSlump(slump) {
    const columns = sanitizeFiniteList(slump?.columns, item => {
        const x = Number(item?.x);
        const fromY = Number(item?.fromY);
        const toY = Number(item?.toY);
        if (!Number.isFinite(x) || !Number.isFinite(fromY) || !Number.isFinite(toY)) return null;
        return {
            x: Math.max(0, Math.round(x)),
            fromY,
            toY,
            delayMs: Math.max(0, Number(item?.delayMs ?? 0)),
            durationMs: Math.max(0, Number(item?.durationMs ?? 0))
        };
    });
    return {
        columns,
        durationMs: Math.max(0, Number(slump?.durationMs ?? 0)),
        reducedMotion: Boolean(slump?.reducedMotion)
    };
}

function sanitizeTankPoses(poses) {
    return sanitizeFiniteList(poses, item => {
        const x = Number(item?.x);
        const y = Number(item?.y);
        if (!Number.isFinite(x) || !Number.isFinite(y)) return null;
        return {
            tankId: String(item?.tankId ?? ""),
            x,
            y,
            hullAngle: clamp(Number(item?.hullAngle ?? 0), -32, 32),
            verticalOffset: clamp(Number(item?.verticalOffset ?? 0), -20, 20),
            leftTreadY: Number(item?.leftTreadY ?? y),
            rightTreadY: Number(item?.rightTreadY ?? y),
            suspensionCompression: clamp01(Number(item?.suspensionCompression ?? 0)),
            recoilX: clamp(Number(item?.recoilX ?? 0), -28, 28),
            recoilY: clamp(Number(item?.recoilY ?? 0), -28, 28),
            rockAngle: clamp(Number(item?.rockAngle ?? 0), -18, 18),
            shadowSquash: clamp(Number(item?.shadowSquash ?? 1), 0.75, 1.3)
        };
    });
}

function sanitizeFiniteList(items, map) {
    const source = Array.isArray(items) ? items : Array.from(items ?? []);
    const result = [];
    for (let i = 0; i < source.length; i++) {
        const mapped = map(source[i]);
        if (mapped) result.push(mapped);
    }
    return result;
}

function applyVisualTankPoses(scene, poses) {
    if (!scene || !poses?.length) return;
    for (const pose of poses) {
        if (scene.player && pose.tankId === scene.player.id) {
            scene.player = { ...scene.player, ...pose };
        } else if (scene.cpu && pose.tankId === scene.cpu.id) {
            scene.cpu = { ...scene.cpu, ...pose };
        }
    }
}

export function dispose() {
    resolveActiveShotPlayback();
    if (rafId) {
        cancelAnimationFrame(rafId);
        rafId = 0;
    }

    canvas = undefined;
    ctx = undefined;
    lastScene = undefined;
    cachedTerrain = undefined;
    cachedTerrainTopRef = undefined;
    cachedTerrainTopLength = -1;
    cachedTerrainTopWorldHeight = 0;
    cachedTerrainTop = 0;
    shotInProgress = false;
}

function scheduleShotFrame(callback) {
    const id = requestAnimationFrame(now => {
        shotRafIds.delete(id);
        callback(now);
    });
    shotRafIds.add(id);
    return id;
}

function scheduleShotTimeout(callback, delay) {
    const id = setTimeout(() => {
        shotTimeoutIds.delete(id);
        callback();
    }, delay);
    shotTimeoutIds.add(id);
    return id;
}

function clearShotCallbacks() {
    for (const id of shotRafIds) {
        cancelAnimationFrame(id);
    }
    shotRafIds.clear();

    for (const id of shotTimeoutIds) {
        clearTimeout(id);
    }
    shotTimeoutIds.clear();
}

function resolveActiveShotPlayback() {
    if (!activeShotCompletes.size && !shotRafIds.size && !shotTimeoutIds.size) {
        return;
    }

    const completes = Array.from(activeShotCompletes);
    clearShotCallbacks();
    for (const complete of completes) {
        complete();
    }
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

function drawScene(scene, offsetX, offsetY, options = {}) {
    clearCanvas();
    ctx.save();
    setWorldTransform(offsetX, offsetY, scene.world);
    drawSky(scene);
    drawWeather(scene, false);
    drawTerrain(scene.terrain ?? [], scene.world);
    drawBuildings(scene.buildings ?? scene.Buildings ?? [], scene);
    drawRadiation(scene.radiation ?? []);
    drawTracerTrails(scene.tracerTrails ?? []);
    drawAimPreview(scene.previewTrail ?? []);
    const now = performance.now();
    const hiddenTankIds = options.hiddenTankIds ?? new Set();
    if (!hiddenTankIds.has(String(scene.player?.id ?? "")) && Number(scene.player?.health ?? 1) > 0) {
        drawTank(scene.player, "playerTank", isTankHurt(scene.player, scene, "player"), isTankShieldHit(scene.player, scene, "player"), now);
    }
    if (!hiddenTankIds.has(String(scene.cpu?.id ?? "")) && Number(scene.cpu?.health ?? 1) > 0) {
        drawTank(scene.cpu, "cpuTank", isTankHurt(scene.cpu, scene, "cpu"), isTankShieldHit(scene.cpu, scene, "cpu"), now);
    }
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
        const trail = sanitizeRenderPoints(trails[i], 2);
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

function terrainSurfaceY(x) {
    const terrain = cachedTerrain ?? lastScene?.terrain ?? [];
    if (!terrain.length) {
        return Number(lastScene?.world?.height ?? 700);
    }

    const index = clamp(Math.round(Number(x) || 0), 0, terrain.length - 1);
    return Number(terrain[index] ?? lastScene?.world?.height ?? 700);
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

function drawBuildings(buildings, scene) {
    const source = Array.isArray(buildings) ? buildings : Array.from(buildings ?? []);
    if (!source.length) return;

    ctx.save();
    for (let i = 0; i < source.length; i++) {
        const building = source[i];
        const x = Number(building?.x ?? building?.X);
        const y = Number(building?.y ?? building?.Y);
        const width = clamp(Number(building?.width ?? building?.Width ?? 46), 24, 92);
        const fullHeight = clamp(Number(building?.height ?? building?.Height ?? 80), 34, 150);
        if (!Number.isFinite(x) || !Number.isFinite(y)) continue;

        const damage = clamp01(Number(building?.damageFraction ?? building?.DamageFraction ?? 0));
        const collapsed = Boolean(building?.collapsed ?? building?.Collapsed) || damage >= 0.98;
        const kind = String(building?.kind ?? building?.Kind ?? "apartment").toLowerCase();
        const penaltyValue = Math.max(0, Number(building?.penaltyValue ?? building?.PenaltyValue ?? 0));
        const lastDamagedShot = Number(building?.lastDamagedShot ?? building?.LastDamagedShot ?? -1);
        const shotIndex = Number(scene?.shotsFired ?? scene?.ShotsFired ?? scene?.round ?? 0);
        const freshlyHit = Number.isFinite(lastDamagedShot) && lastDamagedShot >= 0 && lastDamagedShot >= shotIndex - 1;
        drawBuildingSprite(x, y, width, fullHeight, damage, collapsed, kind, freshlyHit, penaltyValue, i);
    }
    ctx.restore();
}

function drawBuildingSprite(x, groundY, width, fullHeight, damage, collapsed, kind, freshlyHit, penaltyValue, seed) {
    const standingHeight = collapsed ? fullHeight * 0.18 : fullHeight * (1 - damage * 0.46);
    const left = x - width * 0.5;
    const top = groundY - standingHeight;
    const lean = collapsed ? 0 : (hash2d(seed, Math.round(x)) % 13 - 6) * damage * 0.18;
    const palette = kind.includes("tower")
        ? { wall: "#b9c0b3", side: "#707a72", dark: "#313735", light: "#e8e1c5" }
        : kind.includes("office")
            ? { wall: "#879ba5", side: "#536670", dark: "#1e2b31", light: "#cbe5ec" }
            : kind.includes("row")
                ? { wall: "#b08261", side: "#6d4738", dark: "#2e201c", light: "#f1c987" }
                : { wall: "#a79c87", side: "#665f52", dark: "#292721", light: "#f1e1ad" };

    ctx.save();
    if (!collapsed) {
        drawCivilianHazardMarker(x, groundY, width, fullHeight, damage, penaltyValue, freshlyHit);
        ctx.translate(x, groundY);
        ctx.rotate(lean * Math.PI / 180);
        ctx.translate(-x, -groundY);
        ctx.fillStyle = "rgba(5, 7, 8, 0.28)";
        ctx.beginPath();
        ctx.ellipse(x, groundY - 1, width * 0.58, 7 + damage * 5, 0, 0, Math.PI * 2);
        ctx.fill();
        ctx.fillStyle = palette.side;
        ctx.fillRect(left + width * 0.14, top + 5, width * 0.86, standingHeight - 5);
        ctx.fillStyle = palette.wall;
        ctx.fillRect(left, top, width * 0.82, standingHeight);
        ctx.fillStyle = "rgba(255, 246, 190, 0.18)";
        ctx.fillRect(left + 4, top + 4, width * 0.18, standingHeight - 8);
        ctx.strokeStyle = palette.dark;
        ctx.lineWidth = 3;
        ctx.strokeRect(left, top, width * 0.82, standingHeight);
        drawBuildingWindows(left, top, width, standingHeight, damage, palette, seed);
        drawBuildingCracks(left, top, width, standingHeight, damage, palette.dark, seed);
    }

    if (collapsed) {
        drawCollapsedBuildingRubble(x, groundY, width, fullHeight, palette, seed);
    } else if (damage > 0.08) {
        drawBuildingRubbleScatter(x, groundY, width, fullHeight, damage, palette, seed);
    }

    if (freshlyHit || damage > 0.45) {
        drawCivilianWarning(x, top - 10, damage, collapsed);
    }
    ctx.restore();
}

function drawCivilianHazardMarker(x, groundY, width, fullHeight, damage, penaltyValue, freshlyHit) {
    const pulse = freshlyHit ? 1 : 0.5 + Math.sin(performance.now() * 0.005 + x * 0.02) * 0.5;
    const danger = clamp(0.34 + damage * 0.42 + (freshlyHit ? 0.22 : 0), 0.28, 0.82);
    const label = penaltyValue > 0 ? `CIV $${Math.round(penaltyValue)}` : "CIV";

    ctx.save();
    ctx.fillStyle = `rgba(255, 212, 93, ${0.04 + danger * 0.08})`;
    ctx.strokeStyle = `rgba(255, 212, 93, ${0.2 + danger * 0.22 + pulse * 0.08})`;
    ctx.lineWidth = freshlyHit ? 2.6 : 1.8;
    ctx.setLineDash([5, 7]);
    ctx.beginPath();
    ctx.ellipse(x, groundY - fullHeight * 0.46, width * 0.74 + pulse * 2, fullHeight * 0.53 + pulse * 4, 0, 0, Math.PI * 2);
    ctx.fill();
    ctx.stroke();
    ctx.setLineDash([]);

    const labelY = groundY - fullHeight - 20;
    const labelWidth = Math.max(54, Math.min(82, label.length * 7.2));
    ctx.fillStyle = `rgba(32, 22, 18, ${0.72 + damage * 0.14})`;
    ctx.fillRect(x - labelWidth * 0.5, labelY - 12, labelWidth, 16);
    ctx.strokeStyle = `rgba(255, 212, 93, ${0.34 + pulse * 0.16})`;
    ctx.lineWidth = 1;
    ctx.strokeRect(x - labelWidth * 0.5 + 0.5, labelY - 11.5, labelWidth - 1, 15);
    ctx.fillStyle = "#ffd45d";
    ctx.font = "800 9px system-ui, sans-serif";
    ctx.textAlign = "center";
    ctx.fillText(label, x, labelY);
    ctx.restore();
}

function drawBuildingWindows(left, top, width, height, damage, palette, seed) {
    const cols = Math.max(2, Math.floor(width / 15));
    const rows = Math.max(2, Math.floor(height / 18));
    const cellW = width * 0.72 / cols;
    for (let row = 0; row < rows; row++) {
        for (let col = 0; col < cols; col++) {
            const broken = (hash2d(seed + row * 13, col * 7) % 100) < damage * 84;
            const wx = left + 7 + col * cellW;
            const wy = top + 10 + row * 16;
            ctx.fillStyle = broken ? palette.dark : palette.light;
            ctx.fillRect(wx, wy, Math.max(4, cellW - 5), 7);
            if (broken) {
                ctx.strokeStyle = "rgba(240, 246, 255, 0.38)";
                ctx.lineWidth = 1;
                ctx.beginPath();
                ctx.moveTo(wx, wy + 1);
                ctx.lineTo(wx + cellW - 5, wy + 6);
                ctx.stroke();
            }
        }
    }
}

function drawBuildingCracks(left, top, width, height, damage, color, seed) {
    if (damage < 0.12) return;
    ctx.strokeStyle = color;
    ctx.lineWidth = 1.8;
    const crackCount = Math.ceil(damage * 6);
    for (let i = 0; i < crackCount; i++) {
        const x = left + width * (0.18 + (hash2d(seed, i) % 64) / 100);
        let y = top + height * ((hash2d(i, seed) % 72) / 100);
        ctx.beginPath();
        ctx.moveTo(x, y);
        for (let j = 0; j < 4; j++) {
            y += 5 + (hash2d(seed + j, i) % 8);
            ctx.lineTo(x + (hash2d(i + j, seed) % 15) - 7, y);
        }
        ctx.stroke();
    }
}

function drawBuildingRubbleScatter(x, groundY, width, fullHeight, damage, palette, seed) {
    const count = Math.ceil(damage * 18);
    for (let i = 0; i < count; i++) {
        const rx = x - width * 0.55 + (hash2d(seed + i, 17) % Math.max(1, Math.floor(width * 1.1)));
        const ry = groundY - (hash2d(19, seed + i) % Math.max(8, Math.floor(fullHeight * 0.16)));
        ctx.fillStyle = i % 2 ? palette.side : palette.dark;
        ctx.fillRect(rx, ry, 4 + (i % 3), 3 + (i % 2));
    }
}

function drawCollapsedBuildingRubble(x, groundY, width, fullHeight, palette, seed) {
    ctx.fillStyle = "rgba(5, 7, 8, 0.3)";
    ctx.beginPath();
    ctx.ellipse(x, groundY, width * 0.78, 13, 0, 0, Math.PI * 2);
    ctx.fill();
    const count = Math.max(16, Math.floor(width * 0.8));
    for (let i = 0; i < count; i++) {
        const rx = x - width * 0.62 + (hash2d(seed + i, 23) % Math.floor(width * 1.24));
        const ry = groundY - (hash2d(31, seed + i) % Math.max(12, Math.floor(fullHeight * 0.22)));
        const size = 3 + (hash2d(i, seed + 4) % 8);
        ctx.fillStyle = i % 4 === 0 ? palette.light : i % 2 === 0 ? palette.wall : palette.side;
        ctx.fillRect(rx, ry, size, Math.max(3, size * 0.65));
    }
    ctx.strokeStyle = palette.dark;
    ctx.lineWidth = 3;
    ctx.beginPath();
    ctx.moveTo(x - width * 0.55, groundY - 6);
    ctx.lineTo(x - width * 0.2, groundY - fullHeight * 0.22);
    ctx.lineTo(x + width * 0.1, groundY - 8);
    ctx.lineTo(x + width * 0.46, groundY - fullHeight * 0.16);
    ctx.stroke();
}

function drawCivilianWarning(x, y, damage, collapsed) {
    ctx.save();
    ctx.globalAlpha = collapsed ? 0.92 : clamp(0.38 + damage * 0.6, 0.38, 0.9);
    ctx.fillStyle = "rgba(32, 22, 18, 0.78)";
    ctx.fillRect(x - 24, y - 12, 48, 16);
    ctx.fillStyle = collapsed ? "#ff6961" : "#ffd45d";
    ctx.font = "700 10px system-ui, sans-serif";
    ctx.textAlign = "center";
    ctx.fillText(collapsed ? "CIV HIT" : "AVOID", x, y);
    ctx.restore();
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
    const pose = tankPose(tank);
    ctx.ellipse(tank.x + pose.recoilX * 0.35, tank.y - 4, 40 * pose.shadowSquash, 9 / pose.shadowSquash, pose.angleRadians * 0.18, 0, Math.PI * 2);
    ctx.fill();
    ctx.restore();

    if (tank.shield > 0 || shieldHit) {
        drawTankShield(tank, shieldHit, now);
    }

    drawTankSprite(tank, frameName);
    drawTankRecoilShock(tank, now);

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
    const radiusX = shieldRadiusX;
    const radiusY = shieldRadiusY - 3;
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
    const radiusX = shieldRadiusX;
    const radiusY = shieldRadiusY - 3;
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

function drawTankRecoilShock(tank, now) {
    const recoil = Math.hypot(Number(tank?.recoilX ?? 0), Number(tank?.recoilY ?? 0));
    if (recoil < 1.5) return;

    const pulse = 0.5 + Math.sin(now * 0.034) * 0.5;
    const facing = tank.isCpu ? -1 : 1;
    const angle = Number(tank?.angle ?? (tank?.isCpu ? 138 : 42)) * Math.PI / 180;
    const muzzleX = tank.x + (facing * 18) + Math.cos(angle) * 48;
    const muzzleY = tank.y - 44 - Math.sin(angle) * 48;

    ctx.save();
    ctx.strokeStyle = `rgba(255, 236, 170, ${0.72 * (1 - pulse * 0.34)})`;
    ctx.lineWidth = 4;
    ctx.beginPath();
    ctx.arc(muzzleX, muzzleY, 14 + pulse * 24 + recoil * 0.95, 0, Math.PI * 2);
    ctx.stroke();
    ctx.strokeStyle = `rgba(255, 208, 116, ${0.34 * (1 - pulse * 0.4)})`;
    ctx.lineWidth = 2;
    ctx.beginPath();
    ctx.arc(muzzleX, muzzleY, 28 + pulse * 38 + recoil * 1.15, 0, Math.PI * 2);
    ctx.stroke();

    ctx.strokeStyle = `rgba(35, 30, 24, ${0.48 * (1 - pulse * 0.2)})`;
    ctx.lineWidth = 7;
    ctx.beginPath();
    ctx.ellipse(tank.x, tank.y - 20, 52 + recoil * 2.1, 20 + recoil * 0.72, 0, 0, Math.PI * 2);
    ctx.stroke();
    ctx.strokeStyle = `rgba(20, 16, 12, ${0.34 * (1 - pulse * 0.18)})`;
    ctx.lineWidth = 3;
    ctx.beginPath();
    ctx.moveTo(tank.x - facing * (28 + recoil * 0.2), tank.y - 4);
    ctx.lineTo(tank.x - facing * (92 + recoil * 3.1), tank.y - 8 - pulse * 4);
    ctx.stroke();
    ctx.fillStyle = `rgba(116, 85, 48, ${0.32 * (1 - pulse * 0.35)})`;
    ctx.beginPath();
    ctx.ellipse(tank.x - facing * (48 + recoil * 1.5), tank.y - 3, 30 + recoil * 1.2, 7 + recoil * 0.18, -0.04 * facing, 0, Math.PI * 2);
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
    const pose = tankPose(tank);
    const baseX = tank.x + pose.recoilX;
    const baseY = tank.y + pose.recoilY + pose.verticalOffset + pose.compression * 2;

    if (!frame) {
        ctx.save();
        ctx.translate(baseX, baseY - 24);
        ctx.rotate(pose.angleRadians);
        drawSprite(baseFrameName, -48, -28, 96, 48);
        ctx.restore();
        drawTankBarrel(tank);
        return;
    }

    const targetHeight = tank.isCpu ? 66 : 68;
    const targetWidth = targetHeight * frame.aspect;
    const anchorX = targetWidth * 0.5;
    const footY = baseY + 3;
    ctx.save();
    ctx.translate(baseX, footY - targetHeight * 0.48);
    ctx.rotate(pose.angleRadians);
    ctx.scale(1, 1 - pose.compression * 0.055);
    drawSpriteFacing(frameName, -anchorX, -targetHeight * 0.52, targetWidth, targetHeight, tank.isCpu ? -1 : 1);
    ctx.restore();
    drawTreadCompression(tank, pose);
    drawTankBarrel(tank);
}

function drawTankBarrel(tank) {
    const facing = tank.isCpu ? -1 : 1;
    const pose = tankPose(tank);
    const pivotX = tank.x + pose.recoilX + (facing * 18);
    const pivotY = tank.y + pose.recoilY + pose.verticalOffset - 44 + pose.compression * 3;
    const angle = Number(tank?.angle ?? (tank?.isCpu ? 138 : 42));
    const length = 48;

    ctx.save();
    ctx.translate(pivotX, pivotY);
    ctx.rotate(pose.angleRadians - angle * Math.PI / 180);

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

function tankPose(tank) {
    const hull = Number(tank?.hullAngle ?? tank?.HullAngle ?? 0);
    const rock = Number(tank?.rockAngle ?? tank?.RockAngle ?? 0);
    return {
        angleRadians: (hull + rock) * Math.PI / 180,
        recoilX: Number(tank?.recoilX ?? 0),
        recoilY: Number(tank?.recoilY ?? 0),
        verticalOffset: Number(tank?.verticalOffset ?? 0),
        compression: clamp01(Number(tank?.suspensionCompression ?? 0)),
        shadowSquash: clamp(Number(tank?.shadowSquash ?? 1), 0.75, 1.3),
        leftTreadY: Number(tank?.leftTreadY ?? tank?.y ?? 0),
        rightTreadY: Number(tank?.rightTreadY ?? tank?.y ?? 0)
    };
}

function drawTreadCompression(tank, pose) {
    if (pose.compression <= 0.02) return;
    ctx.save();
    ctx.strokeStyle = `rgba(13, 16, 18, ${0.18 + pose.compression * 0.24})`;
    ctx.lineWidth = 3;
    ctx.beginPath();
    ctx.moveTo(tank.x - 33, pose.leftTreadY + 1);
    ctx.lineTo(tank.x + 33, pose.rightTreadY + 1);
    ctx.stroke();
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
    const points = sanitizeRenderPoints(Array.isArray(preview) ? preview : preview?.path, 2);
    const cone = sanitizeRenderPoints(Array.isArray(preview?.cone) ? preview.cone : [], 3);
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

    if (isDroneWeapon(weaponId, visualKind)) {
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

    const missileLike = isMissileWeapon(weaponId, visualKind) || (!weaponId && visibleCount > 36);
    if (missileLike) {
        drawSmokeTrail(points, visibleCount, weaponId, visualKind);
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
    drawFlameTip(last, prev, missileLike && !isMopWeapon(weaponId, visualKind));
    if (isMopWeapon(weaponId, visualKind)) {
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

function drawSmokeTrail(points, count, weaponId, visualKind) {
    const napalm = isNapalmWeapon(weaponId, visualKind);
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

function drawPatriotCountermeasure(scene, options, pathProgress, bannerHoldProgress = 0) {
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
        const bannerEntryProgress = clamp((launchProgress - 0.58) / 0.28, 0, 1);
        const bannerHold = clamp01(Number(bannerHoldProgress ?? 0));
        if (bannerEntryProgress > 0 || bannerHold > 0) {
            drawInterceptBanner(endX, endY, bannerEntryProgress, now, bannerHold);
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

function drawInterceptBanner(x, y, progress, now, holdProgress = 0) {
    const entry = clamp01(progress);
    const hold = clamp01(holdProgress);
    const entryEase = 1 - Math.pow(1 - entry, 3);
    const holdFade = hold <= 0.68 ? 1 : clamp01((1 - hold) / 0.32);
    const alpha = clamp01(entryEase * holdFade);
    if (alpha <= 0.01) {
        return;
    }

    const bounce = Math.sin(entry * Math.PI);
    const rise = entryEase * 24 + hold * 10;
    const scale = 0.82 + entryEase * 0.18 + bounce * 0.1 - hold * 0.03;
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
    const droneCount = dronePointCaches.length;
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
        const pointCache = dronePointCaches[drone];
        ctx.fillStyle = drone % 2 === 0 ? "rgba(255, 231, 139, 0.7)" : "rgba(126, 226, 213, 0.62)";

        for (let i = startIndex; i <= headIndex; i += 6) {
            const point = cachedDronePoint(pointCache, points, i, drone, phase, spin);
            const age = (headIndex - i) / Math.max(1, headIndex - startIndex);
            const alpha = Math.max(0.12, 0.52 * (1 - age));
            ctx.fillStyle = `rgba(255, 231, 139, ${alpha})`;
            ctx.beginPath();
            ctx.arc(point.x, point.y, 1.5 + (drone % 2), 0, Math.PI * 2);
            ctx.fill();
        }

        const head = cachedDronePoint(pointCache, points, headIndex, drone, phase, spin);
        const tailIndex = Math.max(0, headIndex - 5);
        const tail = cachedDronePoint(pointCache, points, tailIndex, drone, phase, spin);
        const baseAngle = Math.atan2(head.y - tail.y, head.x - tail.x);
        const angle = baseAngle + Math.sin((headIndex * 0.045) + phase) * 0.16;
        drawShahedDrone(head.x, head.y, angle, weaponId, 1 + (drone % 3) * 0.06);
    }
}

function cachedDronePoint(cache, points, index, drone, phase, spin) {
    let point = cache[index];
    if (!point) {
        point = { x: 0, y: 0 };
        cache[index] = point;
    }

    writeDronePoint(point, points, index, drone, phase, spin);
    return point;
}

function writeDronePoint(target, points, index, drone, phase, spin) {
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
    target.x = point.x + (normalX * offset) + (tangentX * corkscrew);
    target.y = point.y + (normalY * offset) + (tangentY * corkscrew);
}

function droneSpinDirection(drone, seed) {
    return (hash2d(drone + 11, seed + 37) % 2) === 0 ? 1 : -1;
}

function drawShahedDrone(x, y, angle, weaponId, scale = 1) {
    ctx.save();
    ctx.translate(x, y);
    ctx.rotate(angle);
    ctx.scale(scale, scale);
    if (hasSprite("shahedDrone")) {
        ctx.save();
        ctx.globalAlpha = 0.42;
        ctx.fillStyle = "#050708";
        ctx.beginPath();
        ctx.ellipse(0, 3, 25, 10, 0, 0, Math.PI * 2);
        ctx.fill();
        ctx.restore();
        drawExtraSpriteByHeight("shahedDrone", 0, 0, 24);
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
    if (hasSprite("gbu57Mop")) {
        drawExtraSpriteByHeight("gbu57Mop", 0, 0, 30);
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
        const triggerIndex = explosion.triggerIndex;
        if (triggerIndex < 0 || visibleTrailCount <= triggerIndex) {
            continue;
        }

        const key = explosion.playbackKey;
        if (!starts.has(key)) {
            starts.set(key, now);
        }

        const progress = clamp01((now - starts.get(key)) / 330);
        drawExplosion(explosion, progress);
    }
    ctx.restore();
}

function animateExplosions(scene, explosions, screenShake, playbackOptions = {}) {
    const started = performance.now();
    const visualPhysics = playbackOptions.visualPhysics ?? playbackOptions.VisualPhysics;
    const destruction = sanitizeCanvasDestruction(playbackOptions.finalShotDestruction ?? playbackOptions.FinalShotDestruction);
    const hiddenTankIds = destructionVictimIds(destruction);
    const intense = hasIntenseExplosion(explosions) || Boolean(destruction);
    const slumpDuration = Number(visualPhysics?.slump?.durationMs ?? 0);
    const duration = Math.max(hasNuclearExplosion(explosions) ? 560 : 420, slumpDuration);
    const impactShakeLimit = destruction
        ? (destruction.mutual ? 3.6 : 2.7)
        : intense ? 7 : 3;

    return new Promise(resolve => {
        const tick = now => {
            if (!ctx) {
                resolve();
                return;
            }

            const t = Math.min(1, (now - started) / duration);
            const strength = Math.sin(t * Math.PI);
            const impulseShake = strongestVisualImpulse(visualPhysics);
            const shakeClamp = visualPhysics?.slump?.reducedMotion ? 2.5 : intense ? 7 : 3;
            const shake = screenShake
                ? strength * Math.min(impactShakeLimit, Math.max(intense ? 4 : 2, Math.min(shakeClamp, impulseShake * 0.035)))
                : 0;
            drawScene(scene, Math.sin(now * 0.09) * shake, Math.cos(now * 0.07) * shake * 0.45, { hiddenTankIds });
            drawExplosions(explosions, t);
            drawVisualPhysicsEffects(visualPhysics, now - started, t);
            if (destruction) drawFinalShotDestructionFlash(destruction, t);
            if (t < 1) {
                scheduleShotFrame(tick);
                return;
            }

            resolve();
        };

        scheduleShotFrame(tick);
    });
}

function animateFinalShotDestruction(scene, playbackOptions = {}, screenShake = false) {
    const destruction = sanitizeCanvasDestruction(playbackOptions.finalShotDestruction ?? playbackOptions.FinalShotDestruction);
    if (!destruction) {
        return Promise.resolve();
    }

    const started = performance.now();
    const duration = destruction.reducedMotion ? 1800 : 4300;
    const hiddenTankIds = destructionVictimIds(destruction);
    const pieces = destruction.pieces.map(piece => ({
        ...piece,
        x: piece.x,
        y: piece.y,
        vx: piece.vx,
        vy: piece.vy,
        angle: seededUnit(piece.seed, 5) * Math.PI * 2,
        settled: false
    }));

    return new Promise(resolve => {
        let last = started;
        let backdrop = undefined;
        const tick = now => {
            if (!ctx) {
                resolve();
                return;
            }

            const elapsed = now - started;
            const t = clamp01(elapsed / duration);
            const dt = Math.min(0.05, Math.max(0.001, (now - last) / 1000));
            last = now;
            const recoil = Math.exp(-elapsed / 360);
            const lowRumble = Math.max(0, 1 - elapsed / 1350) * 0.24;
            const shakeStrength = screenShake ? (destruction.mutual ? 4.2 : 3.1) * (recoil + lowRumble) : 0;
            const shakeX = Math.sin(now * 0.1) * shakeStrength;
            const shakeY = Math.cos(now * 0.08) * shakeStrength * 0.45;
            if (!backdrop || backdrop.width !== canvas.width || backdrop.height !== canvas.height) {
                backdrop = captureDestructionBackdrop(scene, hiddenTankIds);
            }

            drawCachedDestructionBackdrop(backdrop, shakeX, shakeY);
            drawFinalDustCloud(destruction, t, false);
            if (t < 0.48) {
                drawFinalShotDestructionFlash(destruction, Math.min(1, t * 2.1));
            }
            updateCanvasDebrisPieces(pieces, dt);
            drawCanvasDebrisPieces(pieces, t);
            drawFinalDustCloud(destruction, t, true);
            drawFinalSmokeColumn(destruction, t);

            if (t < 1) {
                scheduleShotFrame(tick);
                return;
            }

            resolve();
        };

        scheduleShotFrame(tick);
    });
}

function updateCanvasDebrisPieces(pieces, dt) {
    for (const piece of pieces) {
        if (piece.settled) continue;

        const drag = Math.max(0, 1 - piece.drag * dt);
        piece.vx = (piece.vx + (lastScene?.wind ?? 0) * 0.055 * dt) * drag;
        piece.vy = (piece.vy + 285 * dt) * drag;
        piece.x += piece.vx * dt;
        piece.y += piece.vy * dt;
        piece.angle += piece.spin * dt;
        piece.spin *= Math.max(0, 1 - piece.friction * 0.42 * dt);

        const surface = terrainSurfaceY(piece.x);
        const radius = piece.size * 0.42;
        if (piece.y + radius >= surface) {
            const slope = (terrainSurfaceY(piece.x + 4) - terrainSurfaceY(piece.x - 4)) / 8;
            const nx = -slope;
            const ny = 1;
            const nLen = Math.max(0.001, Math.hypot(nx, ny));
            const normalX = nx / nLen;
            const normalY = ny / nLen;
            const dot = piece.vx * normalX + piece.vy * normalY;
            piece.x -= normalX * Math.min(18, Math.max(0, piece.y + radius - surface));
            piece.y = surface - radius;
            if (dot > 0) {
                piece.vx = (piece.vx - (1 + piece.restitution) * dot * normalX) * (1 - piece.friction * 0.52);
                piece.vy = (piece.vy - (1 + piece.restitution) * dot * normalY) * piece.restitution;
                piece.spin += (piece.vx * 0.012) / Math.max(0.2, piece.mass);
            }

            if (Math.hypot(piece.vx, piece.vy) < 26 && Math.abs(piece.spin) < 0.9) {
                piece.settled = true;
                piece.vx = 0;
                piece.vy = 0;
                piece.spin = 0;
            }
        }
    }
}

function drawFinalShotDestructionFlash(destruction, progress) {
    const fade = Math.max(0, 1 - progress);
    const radius = destruction.radius * (0.4 + progress * 1.6);
    ctx.save();
    setWorldTransform();
    const gradient = ctx.createRadialGradient(destruction.x, destruction.y, 2, destruction.x, destruction.y, radius);
    gradient.addColorStop(0, `rgba(255, 252, 220, ${0.72 * fade})`);
    gradient.addColorStop(0.36, `rgba(255, 162, 54, ${0.48 * fade})`);
    gradient.addColorStop(1, "rgba(52, 28, 18, 0)");
    ctx.fillStyle = gradient;
    ctx.beginPath();
    ctx.arc(destruction.x, destruction.y, radius, 0, Math.PI * 2);
    ctx.fill();
    ctx.strokeStyle = `rgba(255, 226, 128, ${0.78 * fade})`;
    ctx.lineWidth = 5;
    ctx.beginPath();
    ctx.arc(destruction.x, destruction.y, destruction.radius * (0.75 + progress * 1.45), 0, Math.PI * 2);
    ctx.stroke();
    ctx.restore();
}

function drawCanvasDebrisPieces(pieces, progress) {
    ctx.save();
    setWorldTransform();
    const sorted = pieces.slice().sort((a, b) => a.y - b.y);
    for (const piece of sorted) {
        const spriteName = canvasDebrisSpriteName(piece.sprite);
        const alpha = clamp(1 - progress * 0.12, 0.62, 1);
        const height = piece.size * (piece.sprite === "hull" || piece.sprite === "turret" ? 1.72 : 1.38);
        const frame = spriteFrame(spriteName);
        const width = frame ? height * frame.aspect : height * 1.4;
        drawDebrisShadow(piece, width, height);
        ctx.save();
        ctx.translate(piece.x, piece.y);
        ctx.rotate(piece.angle);
        ctx.globalAlpha = alpha;
        if (hasSprite(spriteName)) {
            drawExtraSprite(spriteName, -width * 0.5, -height * 0.5, width, height);
            ctx.globalCompositeOperation = "multiply";
            ctx.fillStyle = `rgba(34, 26, 20, ${0.12 + progress * 0.08})`;
            ctx.fillRect(-width * 0.5, -height * 0.5, width, height);
        } else {
            ctx.fillStyle = `rgba(${Math.round(piece.r * 255)}, ${Math.round(piece.g * 255)}, ${Math.round(piece.b * 255)}, ${alpha})`;
            ctx.fillRect(-width * 0.5, -height * 0.35, width, height * 0.7);
        }
        ctx.restore();
    }
    ctx.restore();
}

function drawDebrisShadow(piece, width, height) {
    const surface = terrainSurfaceY(piece.x);
    const altitude = Math.max(0, surface - piece.y);
    const closeness = clamp(1 - altitude / 130, 0, 1);
    if (closeness <= 0.03) return;

    ctx.save();
    ctx.fillStyle = `rgba(18, 15, 12, ${0.22 * closeness})`;
    ctx.beginPath();
    ctx.ellipse(piece.x, surface + 2, width * (0.32 + closeness * 0.18), Math.max(2, height * 0.12), 0, 0, Math.PI * 2);
    ctx.fill();
    ctx.restore();
}

function drawFinalDustCloud(destruction, progress, foreground) {
    const phase = foreground ? progress : Math.min(1, progress * 1.35);
    const fade = foreground
        ? Math.max(0, 1 - Math.max(0, progress - 0.12) * 1.6)
        : Math.max(0, 1 - progress * 1.05);
    if (fade <= 0.02) return;

    ctx.save();
    setWorldTransform();
    const count = foreground ? 10 : 14;
    for (let i = 0; i < count; i++) {
        const unit = seededUnit(Math.round(destruction.x * 13 + destruction.y), i + (foreground ? 100 : 0));
        const angle = -Math.PI + unit * Math.PI;
        const distance = destruction.radius * (0.18 + seededUnit(i, 7) * 0.92) * (0.35 + phase * 0.8);
        const x = destruction.x + Math.cos(angle) * distance + (lastScene?.wind ?? 0) * phase * 0.16;
        const y = terrainSurfaceY(destruction.x) - 6 + Math.sin(angle) * destruction.radius * 0.12 - phase * destruction.radius * (foreground ? 0.08 : 0.16);
        const rx = destruction.radius * (0.16 + seededUnit(i, 17) * 0.18) * (0.8 + phase * 0.65);
        const ry = rx * (0.34 + seededUnit(i, 23) * 0.2);
        ctx.fillStyle = foreground
            ? `rgba(45, 38, 30, ${fade * 0.11})`
            : `rgba(91, 76, 55, ${fade * 0.14})`;
        ctx.beginPath();
        ctx.ellipse(x, y, rx, ry, 0, 0, Math.PI * 2);
        ctx.fill();
    }
    ctx.restore();
}

function drawFinalSmokeColumn(destruction, progress) {
    if (progress <= 0.08) return;

    const fade = Math.max(0, 1 - progress * 0.65);
    ctx.save();
    setWorldTransform();
    for (let i = 0; i < 10; i++) {
        const t = i / 9;
        const x = destruction.x + Math.sin(progress * 7 + i) * destruction.radius * 0.12 + (lastScene?.wind ?? 0) * t * 0.18;
        const y = terrainSurfaceY(destruction.x) - t * destruction.radius * 1.2 - progress * 18;
        const r = destruction.radius * (0.09 + t * 0.18);
        ctx.fillStyle = `rgba(31, 27, 24, ${fade * (0.11 + t * 0.06)})`;
        ctx.beginPath();
        ctx.ellipse(x, y, r * 1.15, r * 0.72, 0, 0, Math.PI * 2);
        ctx.fill();
    }
    ctx.restore();
}

function sanitizeCanvasDestruction(destruction) {
    if (!destruction?.active && !destruction?.Active) return undefined;

    const pieces = destruction.pieces ?? destruction.Pieces;
    if (!Array.isArray(pieces) || pieces.length === 0) return undefined;

    const x = Number(destruction.x ?? destruction.X);
    const y = Number(destruction.y ?? destruction.Y);
    const radius = Number(destruction.radius ?? destruction.Radius ?? 96);
    if (!Number.isFinite(x) || !Number.isFinite(y) || !Number.isFinite(radius) || radius <= 0) return undefined;

    const sanitized = [];
    for (const piece of pieces) {
        const x = Number(piece.x ?? piece.X);
        const y = Number(piece.y ?? piece.Y);
        const vx = Number(piece.vx ?? piece.Vx);
        const vy = Number(piece.vy ?? piece.Vy);
        if (![x, y, vx, vy].every(Number.isFinite)) continue;

        sanitized.push({
            victimId: String(piece.victimId ?? piece.VictimId ?? ""),
            sprite: String(piece.sprite ?? piece.Sprite ?? "plate"),
            x,
            y,
            vx,
            vy,
            size: clamp(Number(piece.size ?? piece.Size ?? 10), 3, 48),
            mass: clamp(Number(piece.mass ?? piece.Mass ?? 1), 0.1, 12),
            restitution: clamp(Number(piece.restitution ?? piece.Restitution ?? 0.42), 0, 0.9),
            friction: clamp(Number(piece.friction ?? piece.Friction ?? 0.55), 0, 0.95),
            drag: clamp(Number(piece.drag ?? piece.Drag ?? 0.3), 0, 1.5),
            spin: clamp(Number(piece.spin ?? piece.Spin ?? 0), -16, 16),
            r: clamp(Number(piece.r ?? piece.R ?? 0.74), 0, 1),
            g: clamp(Number(piece.g ?? piece.G ?? 0.58), 0, 1),
            b: clamp(Number(piece.b ?? piece.B ?? 0.42), 0, 1),
            seed: Number(piece.seed ?? piece.Seed ?? sanitized.length * 997)
        });
    }

    if (!sanitized.length) return undefined;
    return {
        x,
        y,
        radius,
        mutual: Boolean(destruction.mutual ?? destruction.Mutual),
        reducedMotion: Boolean(destruction.reducedMotion ?? destruction.ReducedMotion),
        pieces: sanitized
    };
}

function canvasDebrisSpriteName(sprite) {
    switch (String(sprite ?? "").toLowerCase()) {
        case "hull":
            return "tankDebrisHull";
        case "turret":
            return "tankDebrisTurret";
        case "tread":
            return "tankDebrisTread";
        case "barrel":
            return "tankDebrisBarrel";
        default:
            return "tankDebrisPlate";
    }
}

function destructionVictimIds(destruction) {
    const ids = new Set();
    if (!destruction?.pieces) return ids;

    for (const piece of destruction.pieces) {
        if (piece.victimId) ids.add(String(piece.victimId));
    }

    return ids;
}

function captureDestructionBackdrop(scene, hiddenTankIds) {
    drawScene(scene, 0, 0, { hiddenTankIds });
    const backdrop = document.createElement("canvas");
    backdrop.width = canvas.width;
    backdrop.height = canvas.height;
    const backdropContext = backdrop.getContext("2d");
    backdropContext.drawImage(canvas, 0, 0);
    return backdrop;
}

function drawCachedDestructionBackdrop(backdrop, offsetX, offsetY) {
    ctx.save();
    ctx.setTransform(1, 0, 0, 1, 0, 0);
    ctx.clearRect(0, 0, canvas.width, canvas.height);
    ctx.drawImage(backdrop, offsetX, offsetY);
    ctx.restore();
}

function seededUnit(seed, salt) {
    let value = Math.imul((Number(seed) | 0) ^ Math.imul((Number(salt) | 0) + 0x9e3779b9, 0x85ebca6b), 0xc2b2ae35);
    value ^= value >>> 16;
    value = Math.imul(value, 0x7feb352d);
    value ^= value >>> 15;
    return ((value >>> 0) & 0x00ffffff) / 16777215;
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

function strongestVisualImpulse(visualPhysics) {
    const impulses = visualPhysics?.shockwaves ?? [];
    let strongest = 0;
    for (const impulse of impulses) {
        strongest = Math.max(strongest, Number(impulse.intensity ?? 0) * Number(impulse.terrainDampening ?? 1));
    }
    return strongest;
}

function drawVisualPhysicsEffects(visualPhysics, elapsedMs, progress) {
    if (!visualPhysics) return;

    ctx.save();
    setWorldTransform();
    drawSlumpingColumns(visualPhysics.slump, elapsedMs);
    drawMaterialImpacts(visualPhysics.impacts ?? [], progress);
    drawCivilianImpactEffects(visualPhysics.civilianImpacts ?? [], elapsedMs, progress);
    drawSettlingDebris(visualPhysics.debris ?? [], elapsedMs, visualPhysics.slump?.reducedMotion);
    drawLingeringPhysics(visualPhysics.lingering ?? [], elapsedMs);
    ctx.restore();
}

function drawSlumpingColumns(slump, elapsedMs) {
    const columns = slump?.columns ?? [];
    if (!columns.length) return;

    const reduced = Boolean(slump?.reducedMotion);
    ctx.save();
    for (let i = 0; i < columns.length; i++) {
        const column = columns[i];
        const duration = Math.max(1, Number(column.durationMs ?? 1));
        const local = clamp01((elapsedMs - Number(column.delayMs ?? 0)) / duration);
        if (local <= 0) continue;
        const x = Number(column.x);
        const fromY = Number(column.fromY);
        const toY = Number(column.toY);
        const y = fromY + (toY - fromY) * (1 - Math.pow(1 - local, 2));
        const height = Math.abs(toY - fromY);
        if (height <= 0.5) continue;

        ctx.fillStyle = `rgba(92, 67, 38, ${0.5 * (1 - local)})`;
        ctx.fillRect(x - 3, Math.min(fromY, y) - 2, 7, Math.max(5, height + 4));
        if (!reduced && i % 2 === 0) {
            const dust = 1 - local;
            ctx.fillStyle = `rgba(118, 92, 57, ${0.3 * dust})`;
            ctx.beginPath();
            ctx.ellipse(x, Math.min(fromY, toY) - 6 - local * 16, 9 + height * 0.18, 4 + height * 0.08, 0, 0, Math.PI * 2);
            ctx.fill();
            ctx.fillStyle = `rgba(78, 56, 34, ${0.36 * dust})`;
            ctx.fillRect(x - 4 + (i % 7), y - 8 - local * 24, 7, 5);
        }

        if (!reduced && i % 8 === 0) {
            const dir = toY > fromY ? 1 : -1;
            const dust = 1 - local;
            ctx.strokeStyle = `rgba(64, 44, 25, ${0.28 * dust})`;
            ctx.lineWidth = 3;
            ctx.beginPath();
            ctx.moveTo(x - 14, y - 3);
            ctx.quadraticCurveTo(x, y + dir * height * 0.12, x + 16, toY - 2);
            ctx.stroke();
        }
    }
    ctx.restore();
}

function drawMaterialImpacts(impacts, progress) {
    for (let i = 0; i < impacts.length; i++) {
        const impact = impacts[i];
        const material = String(impact.material ?? "").toLowerCase();
        const shield = Boolean(impact.shieldLike) || material.includes("shield") || material.includes("energy");
        const count = shield ? 8 : material.includes("metal") ? 10 : material.includes("lava") || material.includes("fire") ? 12 : 9;
        const fade = Math.max(0, 1 - progress);
        ctx.lineWidth = shield ? 2 : 1.7;
        ctx.strokeStyle = shield
            ? `rgba(134, 224, 255, ${0.55 * fade})`
            : material.includes("metal")
                ? `rgba(255, 232, 142, ${0.62 * fade})`
                : material.includes("lava") || material.includes("fire")
                    ? `rgba(255, 94, 28, ${0.6 * fade})`
                    : `rgba(114, 80, 44, ${0.48 * fade})`;
        for (let s = 0; s < count; s++) {
            const angle = Math.PI * 2 * s / count + hash2d(i, s) * 0.001;
            const inner = 4 + progress * 10;
            const outer = 12 + progress * clamp(Number(impact.intensity ?? 20) * 0.18, 12, 42);
            ctx.beginPath();
            ctx.moveTo(impact.x + Math.cos(angle) * inner, impact.y + Math.sin(angle) * inner);
            ctx.lineTo(impact.x + Math.cos(angle) * outer, impact.y + Math.sin(angle) * outer);
            ctx.stroke();
        }
    }
}

function drawCivilianImpactEffects(impacts, elapsedMs, progress) {
    if (!impacts.length) return;
    const t = Math.min(1.3, elapsedMs / 1000);
    for (let i = 0; i < impacts.length; i++) {
        const impact = impacts[i];
        const burst = Math.max(12, Math.min(42, impact.damage * 0.7));
        const fade = Math.max(0, 1 - progress * 0.85);
        ctx.fillStyle = `rgba(80, 64, 48, ${0.22 * fade})`;
        ctx.beginPath();
        ctx.ellipse(impact.x, impact.y - 8 - t * 14, burst * (0.8 + t), burst * 0.28, 0, 0, Math.PI * 2);
        ctx.fill();

        const count = impact.collapsed ? 22 : 12;
        for (let p = 0; p < count; p++) {
            const angle = -Math.PI + (Math.PI * 2 * p / count) + hash2d(i, p) * 0.0008;
            const speed = (impact.collapsed ? 42 : 28) + (hash2d(p, i) % 48);
            const x = impact.x + Math.cos(angle) * speed * t;
            const y = Math.min(terrainSurfaceY(x) - 2, impact.y - 28 + Math.sin(angle) * speed * t + 120 * t * t);
            ctx.fillStyle = p % 3 === 0 ? "rgba(226, 211, 164, 0.82)" : "rgba(92, 74, 58, 0.84)";
            ctx.fillRect(x - 2, y - 2, 4 + (p % 3), 3 + (p % 2));
        }

        if (impact.penalty > 0) {
            ctx.save();
            ctx.globalAlpha = fade;
            ctx.fillStyle = "rgba(34, 18, 16, 0.82)";
            ctx.fillRect(impact.x - 30, impact.y - 96 - t * 16, 60, 18);
            ctx.fillStyle = "#ffb45f";
            ctx.font = "700 11px system-ui, sans-serif";
            ctx.textAlign = "center";
            ctx.fillText(`-$${Math.round(impact.penalty)}`, impact.x, impact.y - 83 - t * 16);
            ctx.restore();
        }
    }
}

function drawSettlingDebris(debris, elapsedMs, reducedMotion) {
    const t = Math.min(1.8, elapsedMs / 1000);
    const maxCount = reducedMotion ? Math.min(debris.length, 18) : debris.length;
    for (let i = 0; i < maxCount; i++) {
        const item = debris[i];
        const friction = Number(item.friction ?? 0.6);
        const vx = Number(item.velocityX ?? 0) * (1 - friction * 0.35);
        const vy = Number(item.velocityY ?? 0) + 220 * t;
        const x = Number(item.x ?? 0) + vx * t;
        const y = Math.min(terrainSurfaceY(x) - 2, Number(item.y ?? 0) + vy * t * 0.35);
        const material = String(item.material ?? "").toLowerCase();
        ctx.fillStyle = material.includes("rock") ? "rgba(76, 65, 55, 0.72)" : "rgba(96, 70, 40, 0.74)";
        ctx.fillRect(x - 2, y - 2, 4, 4);
    }
}

function drawLingeringPhysics(lingering, elapsedMs) {
    const seconds = elapsedMs / 1000;
    for (let i = 0; i < lingering.length; i++) {
        const effect = lingering[i];
        const lifetime = Math.max(0.1, Number(effect.lifetime ?? 1));
        const t = clamp01(seconds / lifetime);
        const wind = Number(effect.windX ?? 0);
        const slope = Number(effect.slopeX ?? 0);
        const visual = String(effect.visualKind ?? "").toLowerCase();
        const lava = visual.includes("lava") || visual.includes("fire");
        const x = Number(effect.x ?? 0) + wind * t * (lava ? 0.4 : 1.8) + slope * t * 28;
        const y = Number(effect.y ?? 0) - t * (lava ? 8 : 42);
        const alpha = (1 - t) * (lava ? 0.22 : 0.18) * Number(effect.intensity ?? 1);
        ctx.fillStyle = lava ? `rgba(255, 82, 24, ${alpha})` : `rgba(54, 48, 40, ${alpha})`;
        ctx.beginPath();
        ctx.ellipse(x, y, lava ? 22 + t * 12 : 30 + t * 26, lava ? 8 + t * 5 : 14 + t * 12, 0, 0, Math.PI * 2);
        ctx.fill();
    }
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
    if (hasSprite("lavaPool")) {
        drawExtraSprite("lavaPool", x - width * 0.5, y - height * 0.5, width, height);
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

function shotDuration(pointCount, weaponId, visualKind) {
    if (isDarkEagleWeapon(weaponId)) {
        return 2900;
    }

    if (isDroneWeapon(weaponId, visualKind)) {
        return Math.min(3400, Math.max(1500, pointCount * 13));
    }

    if (isMirvWeapon(weaponId)) {
        return Math.min(1800, Math.max(900, pointCount * 5.5));
    }

    const dramatic = isMopWeapon(weaponId, visualKind);
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
    const ratio = Math.max(1, Math.min(2, window.devicePixelRatio || 1));
    const nextWidth = Math.max(1, Math.floor(rect.width * ratio));
    const nextHeight = Math.max(1, Math.floor(rect.height * ratio));
    if (canvas.width !== nextWidth || canvas.height !== nextHeight) {
        canvas.width = nextWidth;
        canvas.height = nextHeight;
        ctx.imageSmoothingEnabled = false;
        return true;
    }

    return false;
}

function updateStats(now = performance.now()) {
    frameMs = now - lastFrame;
    lastFrame = now;
    fps = (fps * 0.9) + ((1000 / Math.max(frameMs, 1)) * 0.1);
}

function startStatsLoop() {
    if (rafId) {
        return;
    }

    const tick = () => {
        const now = performance.now();
        const resized = sizeCanvas();
        const redrawAmbient = lastScene && !shotInProgress && (resized || now - lastAmbientRedraw >= ambientRedrawIntervalMs);
        if (redrawAmbient) {
            const started = performance.now();
            drawScene(lastScene, 0, 0);
            renderMs = performance.now() - started;
            lastAmbientRedraw = now;
        }

        updateStats(now);
        rafId = requestAnimationFrame(tick);
    };

    rafId = requestAnimationFrame(tick);
}
