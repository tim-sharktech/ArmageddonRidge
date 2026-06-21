let getContext = () => undefined;
let atlas;
let extraSprites = {};
let extraSpriteFrames = {};
let manifest;
let frames = {};

export function configureSprites(contextProvider) {
    getContext = contextProvider;
}

export function spriteFrame(name) {
    return frames[name] ?? extraSpriteFrames[name];
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

export function drawExtraSpriteByHeight(name, centerX, centerY, height) {
    const frame = extraSpriteFrames[name];
    if (!frame) {
        drawExtraSprite(name, centerX - height, centerY - height * 0.5, height * 2, height);
        return;
    }

    const width = height * frame.aspect;
    drawExtraSprite(name, centerX - width * 0.5, centerY - height * 0.5, width, height);
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
        frames = buildFrameCache(manifest?.frames);
        atlas = await loadImage(cacheBustedUrl(manifest.image, manifest.version ?? version));
    } catch {
        manifest = { frames: {} };
        frames = {};
        atlas = undefined;
    }

    extraSprites = {};
    extraSpriteFrames = {};
    await Promise.allSettled([
        loadExtraSprite("shahedDrone", "assets/sprites/shahed-drone.png", version),
        loadExtraSprite("gbu57Mop", "assets/sprites/gbu-57-mop.png", version),
        loadExtraSprite("lavaPool", "assets/sprites/lava-pool.png", version),
        loadExtraSprite("tankDebrisHull", "assets/sprites/tank-debris-hull.png", version),
        loadExtraSprite("tankDebrisTurret", "assets/sprites/tank-debris-turret.png", version),
        loadExtraSprite("tankDebrisTread", "assets/sprites/tank-debris-tread.png", version),
        loadExtraSprite("tankDebrisBarrel", "assets/sprites/tank-debris-barrel.png", version),
        loadExtraSprite("tankDebrisPlate", "assets/sprites/tank-debris-plate.png", version),
        loadExtraSprite("civilianTowerIntact", "assets/sprites/civilian-tower-intact.png", version),
        loadExtraSprite("civilianTowerDamaged", "assets/sprites/civilian-tower-damaged.png", version),
        loadExtraSprite("civilianTowerRubble", "assets/sprites/civilian-tower-rubble.png", version),
        loadExtraSprite("civilianOfficeIntact", "assets/sprites/civilian-office-intact.png", version),
        loadExtraSprite("civilianOfficeDamaged", "assets/sprites/civilian-office-damaged.png", version),
        loadExtraSprite("civilianOfficeRubble", "assets/sprites/civilian-office-rubble.png", version),
        loadExtraSprite("civilianApartmentIntact", "assets/sprites/civilian-apartment-intact.png", version),
        loadExtraSprite("civilianApartmentDamaged", "assets/sprites/civilian-apartment-damaged.png", version),
        loadExtraSprite("civilianApartmentRubble", "assets/sprites/civilian-apartment-rubble.png", version),
        loadExtraSprite("civilianLuxuryIntact", "assets/sprites/civilian-luxury-intact.png", version),
        loadExtraSprite("civilianLuxuryDamaged", "assets/sprites/civilian-luxury-damaged.png", version),
        loadExtraSprite("civilianLuxuryRubble", "assets/sprites/civilian-luxury-rubble.png", version)
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

function buildFrameCache(sourceFrames = {}) {
    const cache = {};
    for (const [name, frame] of Object.entries(sourceFrames)) {
        const w = Number(frame?.w ?? 0);
        const h = Number(frame?.h ?? 0);
        cache[name] = {
            x: Number(frame?.x ?? 0),
            y: Number(frame?.y ?? 0),
            w,
            h,
            aspect: h > 0 ? w / h : 1
        };
    }

    return cache;
}

async function loadExtraSprite(name, url, version) {
    const image = await loadImage(cacheBustedUrl(url, version));
    extraSprites[name] = image;
    extraSpriteFrames[name] = {
        x: 0,
        y: 0,
        w: image.naturalWidth || image.width || 1,
        h: image.naturalHeight || image.height || 1,
        aspect: (image.naturalHeight || image.height || 1) > 0
            ? (image.naturalWidth || image.width || 1) / (image.naturalHeight || image.height || 1)
            : 1
    };
}

function loadImage(url) {
    return new Promise((resolve, reject) => {
        const image = new Image();
        image.decoding = "async";
        image.onload = () => resolve(image);
        image.onerror = reject;
        image.src = url;
    });
}

function cacheBustedUrl(url, version) {
    const separator = url.includes("?") ? "&" : "?";
    return `${url}${separator}v=${encodeURIComponent(version)}`;
}
