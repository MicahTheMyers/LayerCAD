"use strict";

const triangleLimit = 250000;
const largeStlPreviewLimit = 40000;

function parseStl(buffer) {
    const view = new DataView(buffer);
    const count = buffer.byteLength >= 84 ? view.getUint32(80, true) : 0;
    if (count && 84 + count * 50 === buffer.byteLength) {
        const length = count > triangleLimit ? largeStlPreviewLimit : count;
        const triangles = Array.from({ length }, (_, sample) => {
            const i = Math.floor(sample * count / length);
            return (
            Array.from({ length: 3 }, (_, j) =>
                Array.from({ length: 3 }, (_, k) => view.getFloat32(96 + i * 50 + j * 12 + k * 4, true))));
        });
        return { triangles, total: count, skipped: 0 };
    }
    const source = new TextDecoder().decode(buffer);
    const vertices = () => source.matchAll(/^[ \t]*vertex[ \t]+(\S+)[ \t]+(\S+)[ \t]+(\S+)[ \t]*\r?$/gim);
    let vertexCount = 0;
    for (const vertex of vertices()) vertexCount++;
    if (!vertexCount || vertexCount % 3) throw new Error("Invalid STL file.");
    const total = vertexCount / 3;
    const length = total > triangleLimit ? largeStlPreviewLimit : total;
    const triangles = [];
    let index = 0;
    let triangle = [];
    for (const vertex of vertices()) {
        const face = Math.floor(index++ / 3);
        if (face !== Math.floor(triangles.length * total / length)) continue;
        triangle.push(vertex.slice(1).map(Number));
        if (triangle.length === 3) {
            triangles.push(triangle);
            triangle = [];
        }
    }
    return { triangles, total, skipped: 0 };
}

function parseProject(project) {
    if (!Array.isArray(project.Parts)) throw new Error("Invalid LayerCAD project.");
    const triangles = [];
    let skipped = 0;
    for (const part of project.Parts) {
        if (part.Hidden) continue;
        if (!Array.isArray(part.Mesh?.Triangles)) {
            skipped++;
            continue;
        }
        for (const triangle of part.Mesh.Triangles) {
            if (triangles.length >= triangleLimit) throw new Error("This project exceeds the viewer's triangle limit.");
            triangles.push([triangle.A, triangle.B, triangle.C].map(point =>
                [point.X + part.X, point.Y + part.Y, point.Z + part.Z]));
        }
    }
    return { triangles, skipped };
}

if (typeof module !== "undefined") module.exports = { parseStl, parseProject };
if (typeof document !== "undefined") startViewer();

function startViewer() {
    const canvas = document.querySelector("#view");
    const context = canvas.getContext("2d");
    const status = document.querySelector("#status");
    const pointers = new Map();
    let triangles = [], center = [0, 0, 0], radius = 1;
    let yaw = -0.6, pitch = 0.5, zoom = 1, pending = false, loadSequence = 0;

    function draw() {
        pending = false;
        const ratio = Math.min(devicePixelRatio || 1, 2);
        const width = Math.round(canvas.clientWidth * ratio), height = Math.round(canvas.clientHeight * ratio);
        if (canvas.width !== width) canvas.width = width;
        if (canvas.height !== height) canvas.height = height;
        context.clearRect(0, 0, width, height);
        const scale = Math.min(width, height) * 0.42 * zoom / radius;
        const cy = Math.cos(yaw), sy = Math.sin(yaw), cp = Math.cos(pitch), sp = Math.sin(pitch);
        const project = point => {
            const x = point[0] - center[0], y = point[1] - center[1], z = point[2] - center[2];
            const depth = x * sy + y * cy;
            return [width / 2 + (x * cy - y * sy) * scale, height / 2 - (z * cp + depth * sp) * scale, depth * cp - z * sp];
        };
        const faces = triangles.map(triangle => {
            const vertices = triangle.map(project);
            const [a, b, c] = vertices;
            const winding = (b[0] - a[0]) * (c[1] - a[1]) - (b[1] - a[1]) * (c[0] - a[0]);
            return { vertices, winding, depth: a[2] + b[2] + c[2] };
        }).filter(face => face.winding < 0).sort((a, b) => b.depth - a.depth);
        for (const { vertices: [a, b, c] } of faces) {
            const dx = b[0] - a[0], dy = b[1] - a[1], dz = (b[2] - a[2]) * scale;
            const ex = c[0] - a[0], ey = c[1] - a[1], ez = (c[2] - a[2]) * scale;
            const nx = dy * ez - dz * ey, ny = dz * ex - dx * ez, nz = dx * ey - dy * ex;
            const light = Math.max(0, (nx * 0.3 - ny * 0.4 - nz * 0.85) / Math.hypot(nx, ny, nz));
            const tone = Math.round(110 + 125 * light);
            context.fillStyle = `rgb(${tone - 14} ${tone} ${Math.min(255, tone + 20)})`;
            context.beginPath();
            context.moveTo(a[0], a[1]);
            context.lineTo(b[0], b[1]);
            context.lineTo(c[0], c[1]);
            context.closePath();
            context.fill();
        }
    }
    function redraw() {
        if (!pending) {
            pending = true;
            requestAnimationFrame(draw);
        }
    }
    function fit() {
        yaw = -0.6;
        pitch = 0.5;
        zoom = 1;
        redraw();
    }
    function scaleBy(factor) {
        zoom = Math.max(0.15, Math.min(20, zoom * factor));
        redraw();
    }
    document.querySelector("#fit").addEventListener("click", fit);
    document.querySelector("#file").addEventListener("change", async event => {
        const file = event.target.files[0];
        if (!file) return;
        event.target.value = "";
        const sequence = ++loadSequence;
        try {
            const projectFile = file.name.toLowerCase().endsWith(".layercad");
            const sizeLimit = projectFile ? 64 : 256;
            if (file.size > sizeLimit * 1024 * 1024) throw new Error(`Choose a file up to ${sizeLimit} MB.`);
            const result = projectFile
                ? parseProject(JSON.parse(await file.text()))
                : parseStl(await file.arrayBuffer());
            if (sequence !== loadSequence) return;
            if (!result.triangles.length) throw new Error("This project has no visible mesh bodies. Export an STL from LayerCAD to view its solid geometry.");
            const min = [Infinity, Infinity, Infinity], max = [-Infinity, -Infinity, -Infinity];
            for (const triangle of result.triangles) for (const point of triangle) for (let i = 0; i < 3; i++) {
                if (!Number.isFinite(point[i])) throw new Error("The model contains invalid coordinates.");
                min[i] = Math.min(min[i], point[i]);
                max[i] = Math.max(max[i], point[i]);
            }
            triangles = result.triangles;
            center = min.map((value, i) => (value + max[i]) / 2);
            radius = Math.max(0.001, Math.hypot(...max.map((value, i) => value - center[i])));
            const detail = result.total > triangles.length
                ? `Sampled preview · ${triangles.length.toLocaleString()} of ${result.total.toLocaleString()} triangles`
                : `${triangles.length.toLocaleString()} triangles`;
            status.textContent = `${file.name} · ${detail} · Drag to orbit · Pinch or scroll to zoom` +
                (result.skipped ? ` · ${result.skipped} visible non-mesh items omitted` : "");
            fit();
        } catch (error) {
            if (sequence === loadSequence) status.textContent = error.message;
        }
    });
    canvas.addEventListener("pointerdown", event => {
        canvas.setPointerCapture(event.pointerId);
        pointers.set(event.pointerId, [event.clientX, event.clientY]);
    });
    canvas.addEventListener("pointermove", event => {
        const old = pointers.get(event.pointerId);
        if (!old) return;
        const next = [event.clientX, event.clientY];
        pointers.set(event.pointerId, next);
        if (pointers.size === 1) {
            yaw += (next[0] - old[0]) * 0.012;
            pitch = Math.max(-Math.PI / 2, Math.min(Math.PI / 2, pitch + (next[1] - old[1]) * 0.012));
            redraw();
        } else if (pointers.size === 2) {
            const other = [...pointers.entries()].find(([id]) => id !== event.pointerId)[1];
            const before = Math.hypot(old[0] - other[0], old[1] - other[1]);
            if (before > 0) scaleBy(Math.hypot(next[0] - other[0], next[1] - other[1]) / before);
        }
    });
    for (const type of ["pointerup", "pointercancel", "lostpointercapture"]) {
        canvas.addEventListener(type, event => pointers.delete(event.pointerId));
    }
    canvas.addEventListener("wheel", event => {
        event.preventDefault();
        scaleBy(Math.exp(-event.deltaY * 0.001));
    }, { passive: false });
    new ResizeObserver(redraw).observe(canvas);
}
