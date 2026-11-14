"use strict";

const triangleLimit = 250000;


function parseStl(buffer) {
    const view = new DataView(buffer);
    const count = buffer.byteLength >= 84 ? view.getUint32(80, true) : 0;
    if (count && 84 + count * 50 === buffer.byteLength) {

        const triangles = Array.from({ length: count }, (_, i) => {
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
    const triangles = [];
    let triangle = [];
    for (const vertex of vertices()) {
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
    const status = document.querySelector("#status");
    const gl = canvas.getContext("webgl");
    if (!gl) {
        status.textContent = "This browser needs WebGL enabled to display models.";
        return;
    }
    const program = gl.createProgram();
    function attach(type, source) {
        const shader = gl.createShader(type);
        gl.shaderSource(shader, source);
        gl.compileShader(shader);
        gl.attachShader(program, shader);
        gl.deleteShader(shader);
    }
    attach(gl.VERTEX_SHADER, `
        attribute vec3 position;
        attribute vec3 normal;
        uniform vec4 angles;
        uniform vec3 scale;
        varying float light;
        vec3 rotate(vec3 p) {
            float depth = p.x * angles.y + p.y * angles.x;
            return vec3(p.x * angles.x - p.y * angles.y,
                p.z * angles.z + depth * angles.w, depth * angles.z - p.z * angles.w);
        }
        void main() {
            gl_Position = vec4(rotate(position) * scale, 1.0);
            light = 0.42 + 0.58 * abs(dot(rotate(normal), normalize(vec3(-0.35, 0.5, -1.0))));
        }`);
    attach(gl.FRAGMENT_SHADER, `
        precision mediump float;
        varying float light;
        void main() { gl_FragColor = vec4(vec3(0.76, 0.84, 0.94) * light, 1.0); }`);
    gl.linkProgram(program);
    if (!gl.getProgramParameter(program, gl.LINK_STATUS)) {
        status.textContent = "The browser could not initialize the 3D renderer.";
        return;
    }
    gl.useProgram(program);
    gl.enable(gl.DEPTH_TEST);
    const buffer = gl.createBuffer();
    gl.bindBuffer(gl.ARRAY_BUFFER, buffer);
    for (const [name, offset] of [["position", 0], ["normal", 12]]) {
        const location = gl.getAttribLocation(program, name);
        gl.enableVertexAttribArray(location);
        gl.vertexAttribPointer(location, 3, gl.FLOAT, false, 24, offset);
    }
    const angles = gl.getUniformLocation(program, "angles"), scale = gl.getUniformLocation(program, "scale");
    const pointers = new Map();
    let vertexCount = 0, yaw = -0.6, pitch = 0.5, zoom = 1, pending = false, loadSequence = 0;

    function draw() {
        pending = false;
        const ratio = Math.min(devicePixelRatio || 1, 2);
        const width = Math.round(canvas.clientWidth * ratio), height = Math.round(canvas.clientHeight * ratio);
        if (!width || !height) return;
        if (canvas.width !== width) canvas.width = width;
        if (canvas.height !== height) canvas.height = height;
        gl.viewport(0, 0, width, height);
        gl.clearColor(10 / 255, 20 / 255, 34 / 255, 1);
        gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT);
        const fit = Math.min(width, height) * 0.84 * zoom;
        gl.uniform4f(angles, Math.cos(yaw), Math.sin(yaw), Math.cos(pitch), Math.sin(pitch));
        gl.uniform3f(scale, fit / width, fit / height, 0.9);
        gl.drawArrays(gl.TRIANGLES, 0, vertexCount);
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
    let model = [];
    function upload(triangles) {
    const min = [Infinity, Infinity, Infinity], max = [-Infinity, -Infinity, -Infinity];
    for (const triangle of triangles) for (const point of triangle) for (let i = 0; i < 3; i++) {
        if (!Number.isFinite(point[i])) throw new Error("The model contains invalid coordinates.");
        min[i] = Math.min(min[i], point[i]);
        max[i] = Math.max(max[i], point[i]);
    }
    const center = min.map((value, i) => (value + max[i]) / 2);
    const radius = Math.max(0.001, Math.hypot(...max.map((value, i) => value - center[i])));
    const data = new Float32Array(triangles.length * 18);
    let offset = 0;
    for (const [a,b,c] of triangles) {
        const x = b[0]-a[0], y = b[1]-a[1], z = b[2]-a[2];
        const u = c[0]-a[0], v = c[1]-a[1], w = c[2]-a[2];
        const normal = [y*w-z*v, z*u-x*w, x*v-y*u];
        const length = Math.hypot(...normal) || 1;
        for (const point of [a,b,c]) {
            for (let i=0;i<3;i++) data[offset++] = (point[i]-center[i])/radius;
            for (const component of normal) data[offset++] = component/length;
        }
    }
    gl.bufferData(gl.ARRAY_BUFFER, data, gl.STATIC_DRAW);
    if (gl.getError() !== gl.NO_ERROR) throw new Error("The model exceeds available graphics memory.");
    vertexCount = data.length / 6;
        model = triangles;
        document.querySelector("#export").disabled = false;
    }
    const editor = document.querySelector("#sketch");
    const plane = document.querySelector("#sketch-plane");
    const outline = document.querySelector("#outline");
    const vertices = document.querySelector("#vertices");
    const points = [];
    const undoPoint = document.querySelector("#undo-point");
    function drawSketch() {
        outline.setAttribute("points", points.map(([x, y]) => [x + 110, 110 - y].join(",")).join(" "));
        vertices.replaceChildren();
        for (const [x, y] of points) {
            const dot = document.createElementNS("http://www.w3.org/2000/svg", "circle");
            dot.setAttribute("cx", x + 110);
            dot.setAttribute("cy", 110 - y);
            dot.setAttribute("r", 1.4);
            vertices.append(dot);
        }
        undoPoint.disabled = !points.length;
    }
    function showSketch(active) {
        editor.hidden = !active;
        canvas.hidden = active;
        document.querySelector("#start-sketch").disabled = active;
        document.querySelector("#fit").disabled = active;
        if (!active) redraw();
    }
    document.querySelector("#start-sketch").addEventListener("click", () => {
        ++loadSequence;
        points.length = 0;
        drawSketch();
        showSketch(true);
        status.textContent = "Tap the grid to place outline points. Extrude closes the outline.";
    });
    plane.addEventListener("pointerup", event => {
        if (!event.isPrimary || event.button !== 0 || points.length >= 256) return;
        const point = new DOMPoint(event.clientX, event.clientY).matrixTransform(plane.getScreenCTM().inverse());
        const x = Math.round(point.x), y = Math.round(point.y);
        if (x < 0 || x > 220 || y < 0 || y > 220) return;
        points.push([x - 110, 110 - y]);
        drawSketch();
    });
    undoPoint.addEventListener("click", () => { points.pop(); drawSketch(); });
    document.querySelector("#cancel-sketch").addEventListener("click", () => {
        showSketch(false);
        status.textContent = "Sketch canceled.";
    });
    document.querySelector("#extrude").addEventListener("click", () => {
        try {
            const height = document.querySelector("#height").valueAsNumber;
            upload(extrudeProfile(points, height));
            showSketch(false);
            fit();
            status.textContent = `Extruded ${height} mm · Drag to orbit · Pinch or scroll to zoom`;
        } catch (error) {
            status.textContent = error.message;
        }
    });
    document.querySelector("#export").addEventListener("click", () => {
        if (!model.length) return;
        const url = URL.createObjectURL(new Blob([binaryStl(model)], { type: "model/stl" }));
        const link = document.createElement("a");
        link.href = url;
        link.download = "LayerCAD-model.stl";
        document.body.append(link);
        link.click();
        link.remove();
        setTimeout(() => URL.revokeObjectURL(url), 60000);
    });
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
            upload(result.triangles);
            showSketch(false);
            status.textContent = `${file.name} · ${result.triangles.length.toLocaleString()} triangles · Drag to orbit · Pinch or scroll to zoom` +
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
