let context;
let master;
let unlocked = false;
const minimumFrequency = 24;

export function initialize() {
    ensureContext();
}

export async function unlock() {
    ensureContext();
    if (context.state === "suspended") {
        await context.resume();
    }

    unlocked = true;
}

export function setVolume(value) {
    ensureContext();
    master.gain.value = Math.max(0, Math.min(1, value));
}

export function play(name) {
    ensureContext();
    if (!unlocked) {
        return;
    }

    const recipe = recipes[name] ?? recipes.menu;
    recipe();
}

function ensureContext() {
    if (context) {
        return;
    }

    context = new AudioContext();
    master = context.createGain();
    master.gain.value = 0.85;
    master.connect(context.destination);
}

function tone(frequency, duration, type = "square", gain = 0.08, sweep = 0, delay = 0) {
    const osc = context.createOscillator();
    const amp = context.createGain();
    const start = context.currentTime + Math.max(0, delay);
    const safeFrequency = Math.max(minimumFrequency, frequency);
    osc.type = type;
    osc.frequency.setValueAtTime(safeFrequency, start);
    if (sweep !== 0) {
        osc.frequency.exponentialRampToValueAtTime(Math.max(minimumFrequency, safeFrequency + sweep), start + duration);
    }

    amp.gain.setValueAtTime(0.0001, start);
    amp.gain.exponentialRampToValueAtTime(Math.max(0.0001, gain), start + 0.01);
    amp.gain.exponentialRampToValueAtTime(0.0001, start + duration);
    osc.connect(amp);
    amp.connect(master);
    osc.start(start);
    osc.stop(start + duration + 0.02);
}

function noise(duration, gain = 0.16, options = {}) {
    const length = Math.max(1, Math.floor(context.sampleRate * Math.max(0.01, duration)));
    const buffer = context.createBuffer(1, length, context.sampleRate);
    const data = buffer.getChannelData(0);
    for (let i = 0; i < data.length; i++) {
        data[i] = (Math.random() * 2 - 1) * (1 - i / data.length);
    }

    const source = context.createBufferSource();
    const amp = context.createGain();
    const start = context.currentTime + Math.max(0, options.delay ?? 0);
    amp.gain.setValueAtTime(Math.max(0.0001, gain), start);
    amp.gain.exponentialRampToValueAtTime(0.0001, start + Math.max(0.02, duration));
    source.buffer = buffer;
    source.connect(amp);

    if (options.filterType) {
        const filter = context.createBiquadFilter();
        filter.type = options.filterType;
        filter.frequency.setValueAtTime(Math.max(20, options.frequency ?? 900), start);
        filter.Q.setValueAtTime(Math.max(0.001, options.q ?? 0.8), start);
        amp.connect(filter);
        filter.connect(master);
    } else {
        amp.connect(master);
    }

    source.start(start);
    source.stop(start + duration + 0.02);
}

function rumble(frequency, duration, gain = 0.1, sweep = -20, delay = 0) {
    tone(frequency, duration, "sine", gain, sweep, delay);
    tone(frequency * 0.52, duration * 1.15, "triangle", gain * 0.58, sweep * 0.35, delay + 0.025);
}

function metallicClank(delay, frequency, gain = 0.04) {
    tone(frequency, 0.08, "triangle", gain, -frequency * 0.45, delay);
    tone(frequency * 1.47, 0.055, "square", gain * 0.34, -frequency * 0.38, delay + 0.012);
    noise(0.05, gain * 0.42, { delay, filterType: "bandpass", frequency: frequency * 1.8, q: 8 });
}

function dustTail(delay, duration, gain) {
    noise(duration, gain, { delay, filterType: "lowpass", frequency: 340, q: 0.55 });
}

const recipes = {
    menu: () => tone(640, 0.07, "triangle", 0.05, 180),
    fire: () => {
        tone(150, 0.18, "sawtooth", 0.09, -80);
        tone(72, 0.1, "triangle", 0.035, -24, 0.02);
        noise(0.12, 0.065, { filterType: "highpass", frequency: 740, q: 0.4 });
    },
    smallExplosion: () => {
        rumble(98, 0.18, 0.105, -44);
        noise(0.24, 0.15, { filterType: "lowpass", frequency: 880, q: 0.7 });
        noise(0.08, 0.045, { delay: 0.035, filterType: "bandpass", frequency: 1500, q: 2.4 });
    },
    largeExplosion: () => {
        rumble(70, 0.38, 0.15, -36);
        rumble(36, 0.72, 0.08, -10, 0.035);
        noise(0.42, 0.21, { filterType: "lowpass", frequency: 620, q: 0.72 });
        noise(0.16, 0.07, { delay: 0.045, filterType: "bandpass", frequency: 1650, q: 1.7 });
        metallicClank(0.14, 520, 0.022);
    },
    nuclear: () => {
        tone(440, 0.18, "sine", 0.09, -220);
        tone(118, 0.42, "sawtooth", 0.06, -48, 0.04);
        noise(0.14, 0.055, { delay: 0.025, filterType: "bandpass", frequency: 980, q: 1.4 });
    },
    finalDestruction: () => {
        rumble(64, 0.46, 0.18, -34);
        rumble(34, 1.12, 0.12, -8, 0.045);
        noise(0.38, 0.235, { filterType: "lowpass", frequency: 520, q: 0.86 });
        noise(0.18, 0.105, { delay: 0.065, filterType: "bandpass", frequency: 1850, q: 1.2 });
        tone(112, 0.22, "sawtooth", 0.052, -58, 0.18);
        dustTail(0.22, 1.05, 0.075);
        metallicClank(0.11, 760, 0.048);
        metallicClank(0.21, 520, 0.036);
        metallicClank(0.36, 910, 0.028);
        metallicClank(0.54, 390, 0.031);
        metallicClank(0.78, 640, 0.022);
    },
    "final-destruction": () => recipes.finalDestruction(),
    shield: () => tone(880, 0.16, "triangle", 0.07, -260),
    shieldHit: () => {
        tone(1180, 0.09, "triangle", 0.08, 320);
        setTimeout(() => tone(760, 0.11, "sine", 0.05, -260), 36);
        noise(0.055, 0.035, { filterType: "highpass", frequency: 960, q: 0.75 });
    },
    dirt: () => noise(0.18, 0.08, { filterType: "lowpass", frequency: 520, q: 0.65 }),
    damage: () => tone(180, 0.12, "square", 0.07, -60),
    win: () => {
        tone(520, 0.08, "triangle", 0.06);
        setTimeout(() => tone(780, 0.12, "triangle", 0.06), 90);
    },
    loss: () => tone(220, 0.32, "sawtooth", 0.07, -120)
};
