let canvas;
let ctx;
let atlas;
let manifest;
let lastFrame = performance.now();
let fps = 60;
let frameMs = 16.7;
let renderMs = 0;
let lastScene;
let cachedTerrain;
let rafId = 0;
let shotInProgress = false;
const spriteManifestVersion = "2026-05-04-genesis-v4";
const cloudBands = [
    { x: 90, y: 64, scale: 1.08, speed: 7 },
    { x: 360, y: 104, scale: 0.84, speed: 11 },
    { x: 690, y: 72, scale: 1.18, speed: 8 },
    { x: 1010, y: 118, scale: 0.72, speed: 13 },
    { x: 1260, y: 48, scale: 0.96, speed: 9 }
];

export async function initialize(element) {
    canvas = element;
    ctx = canvas.getContext("2d", { alpha: false });
    ctx.imageSmoothingEnabled = false;
    await loadSprites();
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

export async function playShot(scene, trail, explosions, screenShake) {
    if (!ctx || !trail?.length) {
        render(scene);
        return;
    }

    const shotScene = prepareScene(scene);
    shotInProgress = true;
    const points = Array.from(trail);
    const duration = Math.min(1200, Math.max(260, points.length * 4));
    const started = performance.now();

    return new Promise(resolve => {
        const finish = () => {
            if (!explosions?.length) {
                setTimeout(() => {
                    shotInProgress = false;
                    resolve();
                }, 120);
                return;
            }

            animateExplosions(scene, explosions ?? [], screenShake).then(() => {
                shotInProgress = false;
                resolve();
            });
        };

        const tick = now => {
            const t = Math.min(1, (now - started) / duration);
            const count = Math.max(1, Math.floor(points.length * t));
            const shake = screenShake && explosions?.some(e => e.nuclear || e.radius > 80) ? Math.sin(now * 0.08) * (1 - t) * 8 : 0;
            drawScene(shotScene, shake, -shake * 0.4);
            drawTrail(points.slice(0, count));
            if (t < 1) {
                requestAnimationFrame(tick);
                return;
            }

            requestAnimationFrame(() => {
                drawScene(shotScene, 0, 0);
                drawTrail(points);
                finish();
            });
        };

        requestAnimationFrame(tick);
    });
}

export function getStats() {
    return { fps: Math.round(fps), frameMs, renderMs };
}

function prepareScene(scene) {
    if (scene?.terrain?.length) {
        cachedTerrain = scene.terrain;
        return scene;
    }

    return { ...scene, terrain: cachedTerrain ?? [] };
}

function drawScene(scene, offsetX, offsetY) {
    const scaleX = canvas.width / scene.world.width;
    const scaleY = canvas.height / scene.world.height;
    ctx.save();
    ctx.setTransform(scaleX, 0, 0, scaleY, offsetX, offsetY);
    drawSky(scene);
    drawRadiation(scene.radiation ?? []);
    drawTerrain(scene.terrain ?? [], scene.world);
    drawTank(scene.player, "playerTank");
    drawTank(scene.cpu, "cpuTank");
    drawWind(scene.wind);
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
    const surfaceTop = terrain.reduce((lowest, y) => Math.min(lowest, y), worldHeight);
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
    ctx.restore();

    ctx.lineCap = "round";
    ctx.lineJoin = "round";
    ctx.strokeStyle = "#8fbf64";
    ctx.lineWidth = 4;
    strokeTerrainTop(terrain, 4);
    ctx.strokeStyle = "rgba(232, 211, 143, 0.55)";
    ctx.lineWidth = 1.5;
    strokeTerrainTop(terrain, 6, 5);
}

function drawTerrainStrata(terrain, worldHeight) {
    for (let y = 370; y < worldHeight; y += 34) {
        ctx.beginPath();
        let started = false;
        for (let x = 0; x < terrain.length; x += 8) {
            const surface = terrain[x] ?? worldHeight;
            const lineY = y + Math.sin((x + y) * 0.018) * 5;
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

        ctx.strokeStyle = y % 68 === 0 ? "rgba(116, 93, 65, 0.42)" : "rgba(232, 211, 143, 0.18)";
        ctx.lineWidth = 2;
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
        ctx.beginPath();
        ctx.arc(zone.x, zone.y, zone.radius, 0, Math.PI * 2);
        ctx.fillStyle = "rgba(164, 255, 78, 0.16)";
        ctx.fill();
        ctx.strokeStyle = "rgba(164, 255, 78, 0.55)";
        ctx.lineWidth = 4;
        ctx.stroke();
    }
}

function drawTank(tank, frameName) {
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

    drawTankSprite(tank, frameName);

    if (tank.shield > 0) {
        ctx.save();
        ctx.globalAlpha = 0.5 + Math.sin(performance.now() * 0.008) * 0.16;
        drawSprite("shield", tank.x - 34, tank.y - 50, 68, 58);
        ctx.restore();
    }

    if (tank.health <= 35) {
        drawSmokeStack(tank.x, tank.y - 54);
    }
}

function drawTankSprite(tank, baseFrameName) {
    const frameName = tank.isCpu || baseFrameName === "cpuTank" ? "cpuHull" : "playerHull";
    const frame = manifest?.frames?.[frameName];

    if (!frame) {
        drawSprite(baseFrameName, tank.x - 48, tank.y - 52, 96, 48);
        drawTankBarrel(tank);
        return;
    }

    const targetHeight = tank.isCpu ? 54 : 56;
    const targetWidth = targetHeight * (frame.w / frame.h);
    const anchorX = targetWidth * 0.5;
    const footY = tank.y + 4;
    drawSpriteFacing(frameName, tank.x - anchorX, footY - targetHeight, targetWidth, targetHeight, tank.isCpu ? -1 : 1);
    drawTankBarrel(tank);
}

function drawTankBarrel(tank) {
    const facing = tank.isCpu ? -1 : 1;
    const pivotX = tank.x + (facing * 15);
    const pivotY = tank.y - 38;
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

    ctx.fillStyle = "#070a0d";
    for (let x = 12; x < length - 4; x += 12) {
        ctx.fillRect(x, -5, 3, 10);
    }

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

function drawTrail(points) {
    ctx.save();
    ctx.setTransform(canvas.width / 1200, 0, 0, canvas.height / 700, 0, 0);
    ctx.strokeStyle = "#fff6bf";
    ctx.lineWidth = 3;
    ctx.beginPath();
    points.forEach((point, index) => {
        if (index === 0) {
            ctx.moveTo(point.x, point.y);
        } else {
            ctx.lineTo(point.x, point.y);
        }
    });
    ctx.stroke();
    const last = points[points.length - 1];
    drawSprite("shell", last.x - 9, last.y - 5, 18, 9);
    ctx.restore();
}

function animateExplosions(scene, explosions, screenShake) {
    const started = performance.now();
    const duration = explosions.some(explosion => explosion.nuclear) ? 560 : 420;

    return new Promise(resolve => {
        const tick = now => {
            const t = Math.min(1, (now - started) / duration);
            const strength = Math.sin(t * Math.PI);
            const shake = screenShake ? strength * (explosions.some(e => e.nuclear || e.radius > 80) ? 7 : 3) : 0;
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

function drawExplosions(explosions, progress = 1) {
    ctx.save();
    ctx.setTransform(canvas.width / 1200, 0, 0, canvas.height / 700, 0, 0);
    for (const explosion of explosions) {
        const radius = explosion.radius ?? 32;
        const bloom = radius * (0.45 + progress * 0.9);
        const fade = Math.max(0, 1 - progress);
        const flash = Math.max(0, 1 - progress * 2.2);
        const spriteName = explosion.nuclear ? "nuclear" : radius > 58 ? "explosionLarge" : "explosionSmall";
        const spriteSize = radius * (1.4 + progress * 0.95);
        ctx.save();
        ctx.globalAlpha = Math.min(1, 0.25 + progress * 1.2) * Math.max(0.18, 1 - progress * 0.16);
        drawSprite(spriteName, explosion.x - spriteSize / 2, explosion.y - spriteSize / 2, spriteSize, spriteSize);
        ctx.restore();

        const gradient = ctx.createRadialGradient(explosion.x, explosion.y, 2, explosion.x, explosion.y, bloom);
        gradient.addColorStop(0, "#fffdf0");
        gradient.addColorStop(0.22, explosion.nuclear ? "#d7ff6a" : "#ffdc68");
        gradient.addColorStop(0.58, explosion.nuclear ? "rgba(101, 197, 78, 0.58)" : "rgba(236, 106, 92, 0.72)");
        gradient.addColorStop(1, "rgba(28, 22, 18, 0)");
        ctx.fillStyle = gradient;
        ctx.beginPath();
        ctx.arc(explosion.x, explosion.y, bloom, 0, Math.PI * 2);
        ctx.fill();

        ctx.strokeStyle = `rgba(255, 246, 191, ${0.78 * fade})`;
        ctx.lineWidth = 5;
        ctx.beginPath();
        ctx.arc(explosion.x, explosion.y, radius * (0.65 + progress * 1.1), 0, Math.PI * 2);
        ctx.stroke();

        drawBlastSparks(explosion, radius, progress);
        drawSmokePuffs(explosion, radius, progress);

        if (flash > 0) {
            ctx.fillStyle = `rgba(255, 255, 255, ${flash * 0.72})`;
            ctx.beginPath();
            ctx.arc(explosion.x, explosion.y, radius * (0.18 + progress * 0.35), 0, Math.PI * 2);
            ctx.fill();
        }

        if (explosion.nuclear) {
            ctx.strokeStyle = `rgba(213, 255, 106, ${0.75 * fade})`;
            ctx.lineWidth = 6;
            ctx.beginPath();
            ctx.arc(explosion.x, explosion.y, radius * (1.1 + progress * 1.15), 0, Math.PI * 2);
            ctx.stroke();
        }
    }
    ctx.restore();
}

function drawBlastSparks(explosion, radius, progress) {
    const count = explosion.nuclear ? 22 : 14;
    ctx.strokeStyle = `rgba(255, 218, 104, ${Math.max(0, 1 - progress)})`;
    ctx.lineWidth = 2;
    for (let i = 0; i < count; i++) {
        const angle = (Math.PI * 2 * i / count) + hash2d(i, radius) * 0.0009;
        const inner = radius * (0.24 + progress * 0.38);
        const outer = radius * (0.42 + progress * (0.78 + (i % 3) * 0.12));
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

function drawSprite(name, x, y, width, height) {
    const frame = manifest?.frames?.[name];
    if (!atlas || !frame) {
        fallbackSprite(name, x, y, width, height);
        return;
    }

    ctx.drawImage(atlas, frame.x, frame.y, frame.w, frame.h, x, y, width, height);
}

function drawSpriteFacing(name, x, y, width, height, facing = 1) {
    if (facing >= 0) {
        drawSprite(name, x, y, width, height);
        return;
    }

    ctx.save();
    ctx.translate(x + width, y);
    ctx.scale(-1, 1);
    drawSprite(name, 0, 0, width, height);
    ctx.restore();
}

function fallbackSprite(name, x, y, width, height) {
    ctx.fillStyle = name.includes("cpu") ? "#ec6a5c" : "#50c5b7";
    ctx.fillRect(x, y + height * 0.25, width, height * 0.55);
    ctx.fillStyle = "#111418";
    ctx.fillRect(x + width * 0.12, y + height * 0.78, width * 0.76, height * 0.12);
}

async function loadSprites() {
    try {
        const response = await fetch(`assets/sprites/atlas.json?v=${spriteManifestVersion}`, { cache: "no-cache" });
        manifest = await response.json();
        atlas = new Image();
        atlas.src = cacheBustedUrl(manifest.image, manifest.version ?? spriteManifestVersion);
        await atlas.decode();
    } catch {
        manifest = { frames: {} };
        atlas = undefined;
    }
}

function cacheBustedUrl(url, version) {
    if (!url) {
        return url;
    }

    return `${url}${url.includes("?") ? "&" : "?"}v=${encodeURIComponent(version)}`;
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

function hash2d(x, y) {
    const value = Math.sin(x * 12.9898 + y * 78.233) * 43758.5453;
    return Math.abs(Math.floor(value));
}

function positiveModulo(value, divisor) {
    return ((value % divisor) + divisor) % divisor;
}
