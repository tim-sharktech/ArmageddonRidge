let canvas;
let ctx;
let lastFrame = performance.now();
let fps = 60;
let frameMs = 16.7;
let renderMs = 0;

export function initialize(element) {
    canvas = element;
    ctx = canvas.getContext("2d", { alpha: false });
    ctx.imageSmoothingEnabled = false;
}

export function loadImage(url) {
    return new Promise((resolve, reject) => {
        const image = new Image();
        image.onload = () => resolve({ width: image.width, height: image.height });
        image.onerror = reject;
        image.src = url;
    });
}

export function requestFrame(dotNetRef) {
    return new Promise(resolve => {
        requestAnimationFrame(now => {
            if (dotNetRef) {
                dotNetRef.invokeMethodAsync("OnAnimationFrame", now);
            }

            resolve(now);
        });
    });
}

export function beginFrame(width, height) {
    if (!canvas || !ctx) {
        return;
    }

    if (canvas.width !== width) canvas.width = width;
    if (canvas.height !== height) canvas.height = height;
    ctx.setTransform(1, 0, 0, 1, 0, 0);
}

export function submit(frame) {
    if (!ctx || !frame?.world) {
        return getStats();
    }

    const started = performance.now();
    beginFrame(frame.world.width, frame.world.height);
    const commands = frame.commands ?? [];
    for (let i = 0; i < commands.length; i++) {
        drawCommand(commands[i]);
    }

    updateStats();
    renderMs = performance.now() - started;
    return getStats(commands.length);
}

export function getStats(commandCount = 0) {
    return {
        fps: Math.round(fps),
        frameMs,
        renderMs,
        commandCount,
        mode: "FullWasm"
    };
}

function drawCommand(command) {
    if (!command?.op) return;

    ctx.save();
    ctx.globalAlpha = command.alpha ?? 1;
    if (command.fill) ctx.fillStyle = command.fill;
    if (command.stroke) ctx.strokeStyle = command.stroke;
    ctx.lineWidth = command.lineWidth ?? 1;
    ctx.lineCap = "round";
    ctx.lineJoin = "round";

    switch (command.op) {
        case "clear":
            ctx.fillStyle = command.fill ?? "#172433";
            ctx.fillRect(0, 0, canvas.width, canvas.height);
            break;
        case "rect":
            if (command.fill) ctx.fillRect(command.x, command.y, command.w, command.h);
            if (command.stroke) ctx.strokeRect(command.x, command.y, command.w, command.h);
            break;
        case "line":
            ctx.beginPath();
            ctx.moveTo(command.x, command.y);
            ctx.lineTo(command.x2, command.y2);
            if (command.stroke) ctx.stroke();
            break;
        case "circle":
            ctx.beginPath();
            ctx.arc(command.x, command.y, command.r, 0, Math.PI * 2);
            if (command.fill) ctx.fill();
            if (command.stroke) ctx.stroke();
            break;
        case "ellipse":
            ctx.beginPath();
            ctx.ellipse(command.x, command.y, command.w, command.h, 0, 0, Math.PI * 2);
            if (command.fill) ctx.fill();
            if (command.stroke) ctx.stroke();
            break;
        case "poly":
            drawPoints(command.points, true);
            if (command.fill) ctx.fill();
            if (command.stroke) ctx.stroke();
            break;
        case "polyline":
            drawPoints(command.points, false);
            if (command.stroke) ctx.stroke();
            break;
        case "text":
            ctx.font = "700 16px system-ui, sans-serif";
            ctx.textBaseline = "top";
            if (command.fill) ctx.fillText(command.text ?? "", command.x, command.y);
            break;
    }

    ctx.restore();
}

function drawPoints(points, closePath) {
    ctx.beginPath();
    if (!points || points.length < 4) return;

    ctx.moveTo(points[0], points[1]);
    for (let i = 2; i < points.length - 1; i += 2) {
        ctx.lineTo(points[i], points[i + 1]);
    }

    if (closePath) ctx.closePath();
}

function updateStats() {
    const now = performance.now();
    frameMs = now - lastFrame;
    lastFrame = now;
    if (frameMs > 0) fps = (fps * 0.88) + ((1000 / frameMs) * 0.12);
}
