let getContext = () => undefined;
let atlas;
let extraSprites = {};
let manifest;

export function configureSprites(contextProvider) {
    getContext = contextProvider;
}

export function spriteFrame(name) {
    return manifest?.frames?.[name];
}

export function hasSprite(name) {
    return Boolean(extraSprites[name]);
}

export function drawExtraSprite(name, x, y, width, height) {
    const ctx = getContext();
    const sprite = extraSprites[name];
    if (!ctx || !sprite) {
        fallbackSprite(name, x, y, width, height);
        return;
    }

    ctx.drawImage(sprite, x, y, width, height);
}

export function drawSprite(name, x, y, width, height) {
    const ctx = getContext();
    const sprite = extraSprites[name] ?? atlas;
    const frame = spriteFrame(name);
    if (!ctx || !sprite || !frame) {
        fallbackSprite(name, x, y, width, height);
        return;
    }

    ctx.drawImage(sprite, frame.x, frame.y, frame.w, frame.h, x, y, width, height);
}

export function drawOrientedSprite(name, x, y, width, height, angle) {
    const ctx = getContext();
    if (!ctx) return;

    ctx.save();
    ctx.translate(x, y);
    ctx.rotate(angle);
    drawSprite(name, -width / 2, -height / 2, width, height);
    ctx.restore();
}

export function drawSpriteFacing(name, x, y, width, height, facing = 1) {
    const ctx = getContext();
    if (!ctx) return;

    ctx.save();
    if (facing < 0) {
        ctx.translate(x + width, y);
        ctx.scale(-1, 1);
        drawSprite(name, 0, 0, width, height);
    } else {
        drawSprite(name, x, y, width, height);
    }

    ctx.restore();
}

export async function loadSprites(version) {
    try {
        manifest = await fetch(cacheBustedUrl("assets/sprites/atlas.json", version), { cache: "no-cache" }).then(r => r.json());
        atlas = await loadImage(cacheBustedUrl(manifest.image, manifest.version ?? version));
    } catch {
        manifest = { frames: {} };
        atlas = undefined;
    }

    extraSprites = {};
    await Promise.allSettled([
        loadExtraSprite("shahedDrone", "assets/sprites/shahed-drone.png", version),
        loadExtraSprite("gbu57Mop", "assets/sprites/gbu-57-mop.png", version),
        loadExtraSprite("lavaPool", "assets/sprites/lava-pool.png", version)
    ]);
}

function fallbackSprite(name, x, y, width, height) {
    const ctx = getContext();
    if (!ctx) return;

    ctx.fillStyle = name.includes("cpu") ? "#ec6a5c" : "#50c5b7";
    ctx.fillRect(x, y + height * 0.25, width, height * 0.55);
    ctx.fillStyle = "#111418";
    ctx.fillRect(x + width * 0.12, y + height * 0.78, width * 0.76, height * 0.12);
}

async function loadExtraSprite(name, url, version) {
    extraSprites[name] = await loadImage(cacheBustedUrl(url, version));
}

function loadImage(url) {
    return new Promise((resolve, reject) => {
        const image = new Image();
        image.onload = () => resolve(image);
        image.onerror = reject;
        image.src = url;
    });
}

function cacheBustedUrl(url, version) {
    const separator = url.includes("?") ? "&" : "?";
    return `${url}${separator}v=${encodeURIComponent(version)}`;
}
