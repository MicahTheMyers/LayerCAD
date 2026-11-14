"use strict";

const triangleLimit = 250000;
let geometryEngine;


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

function modelPoint(part, [x, y, z]) {
    let local;
    if (part.FaceFrame) {
        const { Horizontal: u, Vertical: v } = part.FaceFrame;
        local = [
            u.X*x + v.X*y + (u.Y*v.Z-u.Z*v.Y)*z,
            u.Y*x + v.Y*y + (u.Z*v.X-u.X*v.Z)*z,
            u.Z*x + v.Z*y + (u.X*v.Y-u.Y*v.X)*z
        ];
    } else {
        local = part.Plane === "XZ" ? [x,-z,y] : part.Plane === "YZ" ? [z,x,y] : [x,y,z];
    }
    return [local[0]+part.X, local[1]+part.Y, local[2]+part.Z];
}

function parseProject(project) {
    if (!Array.isArray(project.Parts)) throw new Error("Invalid LayerCAD project.");
    const triangles = [];
    const sketchTriangles = [];
    const extrude = typeof module === "undefined" ? extrudeProfile : require("./sketch.js").extrudeProfile;
    const makeShape = typeof module === "undefined" ? shapeProfile : require("./sketch.js").shapeProfile;
    let skipped = 0;
    for (const part of project.Parts) {
        if (part.Hidden) continue;
        if (["Box", "Cylinder", "Tube"].includes(part.Kind) && !part.Holes && !part.Threads && !part.Bevel && !part.BevelCuts) {
            const count = 96;
            const ring = radius => Array.from({ length: count }, (_, i) =>
                [radius * Math.cos(i * 2 * Math.PI / count), radius * Math.sin(i * 2 * Math.PI / count)]);
            const outline = part.Kind === "Box"
                ? makeShape("Rectangle", [[-part.Width/2,-part.Depth/2],[part.Width/2,part.Depth/2]])
                : ring(part.Width/2);
            let solid;
            if (part.Kind === "Tube") {
                solid = [];
                const inner = ring(part.Depth/2);
                const vertex = ([x,y], z) => [x,y,z];
                const quad = (a,b,c,d) => solid.push([a,b,c],[a,c,d]);
                for (let i=0; i<count; i++) {
                    const j=(i+1)%count;
                    const a=vertex(outline[i],0), b=vertex(outline[j],0);
                    const c=vertex(inner[i],0), d=vertex(inner[j],0);
                    const e=vertex(outline[i],part.Height), f=vertex(outline[j],part.Height);
                    const g=vertex(inner[i],part.Height), h=vertex(inner[j],part.Height);
                    quad(a,b,f,e); quad(d,c,g,h); quad(b,a,c,d); quad(e,f,h,g);
                }
            } else {
                solid = extrude(outline, part.Height);
            }
            if (triangles.length + solid.length > triangleLimit)
                throw new Error("This project exceeds the viewer's triangle limit.");
            for (const triangle of solid)
                triangles.push(triangle.map(([x,y,z]) => [x+part.X,y+part.Y,z+part.Z]));
            continue;
        }
        if (part.Kind === "Sketch" && part.Profile) {
            for (const profile of [part.Profile, ...(part.AdditionalProfiles ?? [])]) {
                const caps = extrude(profile.map(({X,Y}) => [X,Y]), 1).slice(0, 2 * (profile.length - 2));
                for (let i = 1; i < caps.length; i += 2)
                    sketchTriangles.push(caps[i].map(([x,y]) => modelPoint(part, [x,y,0.008])));
            }
            if (part.OpenPaths?.length) skipped++;
            continue;
        }
        if (part.Kind === "Extrusion" &&
            !part.Holes && !part.Threads && !part.Bevel && !part.BevelCuts && Array.isArray(part.Profile)) {
            const solid = extrude(part.Profile.map(({X,Y}) => [X,Y]), part.Height);
            if (triangles.length + solid.length > triangleLimit) throw new Error("This project exceeds the viewer's triangle limit.");
            for (const triangle of solid)
                triangles.push(triangle.map(point => modelPoint(part, point)));
            continue;
        }
        if (!Array.isArray(part.Mesh?.Triangles)) {
            if (geometryEngine && part.Kind !== "Sketch") {
                const mesh = JSON.parse(geometryEngine.buildPart(JSON.stringify(part)));
                if (triangles.length + mesh.length > triangleLimit)
                    throw new Error("This project exceeds the viewer's triangle limit.");
                for (const { A, B, C } of mesh)
                    triangles.push([A, B, C].map(({ X, Y, Z }) => [X, Y, Z]));
                continue;
            }
            skipped++;
            continue;
        }
        for (const triangle of part.Mesh.Triangles) {
            if (triangles.length >= triangleLimit) throw new Error("This project exceeds the viewer's triangle limit.");
            triangles.push([triangle.A, triangle.B, triangle.C].map(point =>
                [point.X + part.X, point.Y + part.Y, point.Z + part.Z]));
        }
    }
    return { triangles, sketchTriangles, skipped };
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
        uniform vec3 tint;
        void main() { gl_FragColor = vec4(tint * light, 1.0); }`);
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
    const tint = gl.getUniformLocation(program, "tint");
    let solidVertexCount = 0;
    const pointers = new Map();
    let tapStart = null;
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
        gl.uniform3f(tint, 0.76, 0.84, 0.94);
        gl.drawArrays(gl.TRIANGLES, 0, solidVertexCount);
        gl.uniform3f(tint, 0.29, 0.85, 0.72);
        gl.drawArrays(gl.TRIANGLES, solidVertexCount, vertexCount - solidVertexCount);
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
    let modelCenter = [0,0,0], modelRadius = 1, faceSketch = null, pickingFace = false;
    const emptyProject = () => ({ Version: 11, Bed: { X: 220, Y: 220, Z: 250 }, Parts: [] });
    let project = emptyProject(), selected = -1, editing = -1;
    const undo = [], redo = [];
    const byId = id => document.getElementById(id);
    const save = document.querySelector("#save");
    const modeling = byId("model-tool");
    const engineReady = import("./engine/engine.js?v=constraints-1").then(engine => {
        geometryEngine = engine;
        modeling.disabled = !editor.hidden;
        refreshParts();
    }).catch(() => {
        status.textContent = "The advanced geometry engine could not load. Basic editing is available.";
    });
    function upload(solids, sketches = []) {
    const triangles = solids.concat(sketches);
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
        solidVertexCount = solids.length * 3;
        modelCenter = triangles.length ? center : [0,0,0];
        modelRadius = triangles.length ? radius : 1;
        model = solids;
        document.querySelector("#export").disabled = !solids.length;
    }
    function refreshParts() {
        const panel = byId("parts");
        panel.replaceChildren();
        for (const [title, sketches] of [["Objects", false], ["Sketches", true]]) {
            const heading = document.createElement("h3");
            heading.textContent = title;
            panel.append(heading);
            project.Parts.forEach((part, index) => {
                if ((part.Kind === "Sketch") !== sketches) return;
                const row = document.createElement("div");
                row.className = "part-row";
                const pick = document.createElement("button");
                pick.textContent = part.Name;
                pick.setAttribute("aria-pressed", index === selected);
                pick.onclick = () => { selected = index; refreshParts(); };
                const visibility = document.createElement("button");
                visibility.textContent = part.Hidden ? "○" : "◉";
                visibility.setAttribute("aria-label", (part.Hidden ? "Show " : "Hide ") + part.Name);
                visibility.onclick = () => changeParts(project.Parts.map((item, i) =>
                    i === index ? { ...item, Hidden: !item.Hidden } : item));
                row.append(pick, visibility);
                panel.append(row);
            });
        }
        const part = project.Parts[selected];
        byId("edit-sketch").disabled = !editor.hidden || part?.Kind !== "Sketch" || !Array.isArray(part.Profile) ||
            !!part.Dimensions || !!part.AdditionalProfiles || !!part.OpenPaths;
        byId("extrude-part").disabled = !editor.hidden || part?.Kind !== "Sketch" || !part.Profile;
        byId("move-part").disabled = !editor.hidden || !part;
        byId("face-sketch").disabled = !editor.hidden || !geometryEngine || !model.length;
        byId("face-sketch").setAttribute("aria-pressed", pickingFace);
        byId("pattern").disabled = !editor.hidden || !part || part.Kind === "Sketch";
        byId("hole").disabled = !editor.hidden || !geometryEngine ||
            !["Box", "Cylinder", "Extrusion"].includes(part?.Kind);
        byId("bevel").disabled = !editor.hidden || !geometryEngine || part?.Kind !== "Box";
        byId("remove-part").disabled = !editor.hidden || !part;
        byId("undo").disabled = !editor.hidden || !undo.length;
        byId("redo").disabled = !editor.hidden || !redo.length;
        save.disabled = !project.Parts.length;
    }
    function loadScene(next) {
        const result = parseProject(next);
        upload(result.triangles, result.sketchTriangles);
        project = next;
        refreshParts();
        redraw();
        return result;
    }
    function changeParts(parts, nextSelection = selected, minimumVersion = 11) {
        const before = project;
        try {
            loadScene({ ...project, Version: Math.max(minimumVersion, project.Version), Parts: parts });
        } catch (error) {
            status.textContent = error.message;
            return false;
        }
        undo.push(before);
        if (undo.length > 24) undo.shift();
        redo.length = 0;
        selected = nextSelection;
        refreshParts();
        return true;
    }
    function stepHistory(from, to) {
        if (!from.length) return;
        const current = project;
        try { loadScene(from.at(-1)); } catch (error) { status.textContent = error.message; return; }
        from.pop();
        to.push(current);
        selected = -1;
        refreshParts();
    }
    byId("undo").onclick = () => stepHistory(undo, redo);
    byId("redo").onclick = () => stepHistory(redo, undo);
    function modifySelected(update, version) {
        try {
            const next = update(project.Parts[selected]);
            if (!next) return;
            geometryEngine.buildPart(JSON.stringify(next));
            changeParts(project.Parts.map((part, i) => i === selected ? next : part), selected, version);
        } catch (error) { status.textContent = error.message; }
    }
    byId("hole").onclick = () => modifySelected(part => {
        const answer = prompt("Hole center X, center Y, diameter in local millimeters", "0, 0, 5");
        if (answer === null) return;
        const [X, Y, Diameter, ...extra] = answer.split(",").map(Number);
        if (extra.length || ![X,Y,Diameter].every(Number.isFinite) || Diameter <= 0)
            throw new Error("Enter a center and a positive diameter.");
        return { ...part, Holes: [...(part.Holes ?? []), { Axis: part.Kind === "Extrusion" ? "Profile" : "Z",
            Center: { X,Y }, Diameter, Depth: 0, Through: true, FromEnd: true }] };
    }, 10);
    byId("bevel").onclick = () => modifySelected(part => {
        const answer = prompt("Top edge bevel size (mm)", "2");
        if (answer === null) return;
        const Amount = Number(answer);
        if (!Number.isFinite(Amount) || Amount <= 0) throw new Error("Enter a positive bevel size.");
        const cuts = (part.BevelCuts ?? []).filter(cut => cut.Target.Z !== 1 || cut.Target.ProfileKind);
        for (const [X,Y] of [[-1,0],[1,0],[0,-1],[0,1]])
            cuts.push({ Target: { X,Y,Z:1 }, Amount });
        return { ...part, Bevel: 0, BevelCuts: cuts };
    }, 7);
    modeling.onchange = () => {
        const tool = modeling.value;
        modeling.value = "";
        if (!tool || !geometryEngine) return;
        try {
            let sources = [], values = [];
            if (tool === "Loft") {
                sources = project.Parts.filter(part => part.Kind === "Sketch" && part.Profile && !part.Hidden);
                if (sources.length < 2) throw new Error("Show two or more closed sketches on parallel planes.");
                const guide = project.Parts[selected];
                if (guide?.Kind === "Sketch" && guide.OpenPaths?.length)
                    sources.push({ ...guide, Profile: null, AdditionalProfiles: null, Dimensions: null, Constraints: null });
            } else if (tool === "Sweep" || tool === "Pipe") {
                const path = project.Parts[selected];
                if (path?.Kind !== "Sketch" || !path.OpenPaths?.length)
                    throw new Error("Select a sketch containing an open path.");
                if (tool === "Sweep") {
                    const profile = project.Parts.find(part => part.Kind === "Sketch" && part.Profile && !part.Hidden && part !== path);
                    if (!profile) throw new Error("Show a separate closed profile for Sweep.");
                    sources = [profile, path];
                } else {
                    const answer = prompt("Outer radius and wall thickness (mm)", "5, 1");
                    if (answer === null) return;
                    values = answer.split(",").map(Number);
                    if (values.length !== 2 || !values.every(Number.isFinite)) throw new Error("Enter a radius and wall thickness.");
                    sources = [path];
                }
            } else {
                const fields = {
                    Sphere: ["Diameter (mm)", "30"],
                    Torus: ["Center radius and tube radius (mm)", "20, 5"],
                    Coil: ["Center radius, wire radius, pitch (mm), turns", "15, 2, 6, 4"],
                    Revolve: ["Angle (degrees), axis (0 for vertical, 1 for horizontal)", "360, 0"]
                };
                if (tool === "Revolve") {
                    const sketch = project.Parts[selected];
                    if (sketch?.Kind !== "Sketch" || !sketch.Profile) throw new Error("Select a closed sketch to revolve.");
                    sources = [sketch];
                }
                const answer = prompt(...fields[tool]);
                if (answer === null) return;
                values = answer.split(",").map(Number);
                if (values.length !== fields[tool][1].split(",").length || !values.every(Number.isFinite))
                    throw new Error("Enter the requested numeric values.");
            }
            const [a = 0, b = 0, c = 0, d = 0] = values;
            const triangles = JSON.parse(geometryEngine.modelPart(tool, JSON.stringify(sources), a, b, c, d));
            let lowest = Infinity;
            for (const { A, B, C } of triangles) lowest = Math.min(lowest, A.Z, B.Z, C.Z);
            if (changeParts([...project.Parts, { Kind: "Mesh", Name: tool, X: 0, Y: 0,
                Z: sources.length ? 0 : -lowest, Width: 0, Depth: 0, Height: 0, Mesh: { Triangles: triangles } }],
                project.Parts.length)) fit();
        } catch (error) { status.textContent = error.message; }
    };
    byId("primitive").onclick = () => {
        const kind = prompt("Primitive type: Box, Cylinder, or Tube", "Box")?.trim().toLowerCase();
        const types = { box: "Box", cylinder: "Cylinder", tube: "Tube" };
        if (!kind || !Object.hasOwn(types, kind)) return;
        const raw = prompt(kind === "box" ? "Width, depth, height (mm)" :
            kind === "tube" ? "Outer diameter, inner diameter, height (mm)" :
                "Diameter, height (mm)", kind === "cylinder" ? "30, 20" : "30, 20, 10");
        if (raw === null) return;
        const dimensions = raw.split(",").map(Number);
        if (dimensions.length !== (kind === "cylinder" ? 2 : 3) ||
            !dimensions.every(value => Number.isFinite(value) && value >= 0.01 && value <= 10000) ||
            kind === "tube" && dimensions[0] - dimensions[1] < 0.02) {
            status.textContent = "Enter valid dimensions. A tube needs a larger outer diameter.";
            return;
        }
        const Kind = types[kind], Width = dimensions[0], Height = dimensions.at(-1);
        const Depth = kind === "cylinder" ? Width : dimensions[1];
        if (changeParts([...project.Parts, { Kind, Width, Depth, Height, X: 0, Y: 0, Z: 0,
            Name: `${Kind} ${project.Parts.length + 1}` }], project.Parts.length)) fit();
    };
    byId("pattern").onclick = () => {
        const source = project.Parts[selected];
        const input = prompt("Number of copies, then X, Y, Z spacing (mm)", "3, 30, 0, 0");
        if (input === null) return;
        const values = input.split(",").map(Number);
        const [count, x, y, z] = values;
        if (values.length !== 4 || !values.every(Number.isFinite) || !Number.isInteger(count) ||
            count < 1 || count > 40 || ![x,y,z].some(value => value !== 0) ||
            ![source.X+x*count,source.Y+y*count,source.Z+z*count].every(value => Math.abs(value) <= 10000)) {
            status.textContent = "Enter 1–40 copies and a nonzero spacing within the project bounds.";
            return;
        }
        const copies = Array.from({ length: count }, (_, i) => ({ ...source,
            X: source.X+x*(i+1), Y: source.Y+y*(i+1), Z: source.Z+z*(i+1),
            Name: `${source.Name} copy ${i+1}`, SketchId: undefined, SketchProfileIndex: undefined }));
        if (changeParts([...project.Parts, ...copies])) fit();
    };
    byId("extrude-part").onclick = () => {
        const value = prompt("Extrusion distance (mm)", "10");
        if (value === null) return;
        const source = project.Parts[selected];
        const height = Number(value);
        const link = source.SketchId ?? crypto.randomUUID();
        const parts = [...project.Parts];
        parts[selected] = { ...source, SketchId: link, Hidden: !source.AdditionalProfiles?.length && !source.OpenPaths?.length };
        parts.push({ Kind: "Extrusion", Name: `Extrusion ${parts.length + 1}`, X: source.X, Y: source.Y, Z: source.Z,
            Width: 0, Depth: 0, Height: height, Profile: source.Profile, SketchId: link,
            Plane: source.Plane, FaceFrame: source.FaceFrame });
        if (changeParts(parts, parts.length - 1)) fit();
    };
    byId("move-part").onclick = () => {
        const part = project.Parts[selected];
        const value = prompt("Move by X, Y, Z (mm)", "0, 0, 0");
        const offsets = value?.split(",").map(Number);
        if (!offsets || offsets.length !== 3 || !offsets.every(Number.isFinite)) return;
        changeParts(project.Parts.map(item => item === part || part.Kind === "Sketch" && part.SketchId && item.SketchId === part.SketchId
            ? { ...item, X: item.X + offsets[0], Y: item.Y + offsets[1], Z: item.Z + offsets[2] } : item));
    };
    byId("remove-part").onclick = () => {
        const removed = project.Parts[selected];
        changeParts(project.Parts.filter(part => part !== removed).map(part =>
            removed.Kind === "Sketch" && removed.SketchId && part.SketchId === removed.SketchId
                ? { ...part, SketchId: undefined, SketchProfileIndex: undefined } : part), -1);
    };
    const editor = document.querySelector("#sketch");
    const plane = document.querySelector("#sketch-plane");
    const outline = document.querySelector("#outline");
    const vertices = document.querySelector("#vertices");
    const points = [];
    let rules = [];
    const rulePicker = byId("constraint-rule");
    const vertexPicker = byId("constraint-vertex");
    rulePicker.onchange = () => {
        const rule = rulePicker.value;
        rulePicker.value = "";
        if (!rule) return;
        if (rule === "clear") { rules = []; drawSketch(); return; }
        const Vertex = Number(vertexPicker.value), Kind = Number(rule);
        if (rules.some(item => item.Vertex === Vertex && item.Kind === Kind)) return;
        try {
            const next = [...rules, { Vertex, Kind }];
            const solved = JSON.parse(geometryEngine.constrain(JSON.stringify({
                Points: points.map(([X,Y]) => ({ X,Y })), Rules: next, Preferred: Vertex
            })));
            points.splice(0, points.length, ...solved.map(({X,Y}) => [X,Y]));
            rules = next;
            drawSketch();
            status.textContent = "Constraint applied. Clear constraints to change the outline.";
        } catch (error) { status.textContent = error.message; }
    };
    const shape = document.querySelector("#shape");
    const undoPoint = document.querySelector("#undo-point");
    const referenceOverlay = byId("reference-overlay");
    let reference = null, referenceSequence = 0, calibration = null;

    function drawReference() {
        for (const id of ["reference-settings", "reference-calibrate", "reference-remove"])
            byId(id).disabled = !reference;
        if (!reference) {
            referenceOverlay.removeAttribute("href");
            return;
        }
        referenceOverlay.setAttribute("href", "data:image/png;base64," + reference.Png);
        referenceOverlay.setAttribute("x", 110 + reference.X - reference.Width / 2);
        referenceOverlay.setAttribute("y", 110 - reference.Y - reference.Height / 2);
        referenceOverlay.setAttribute("width", reference.Width);
        referenceOverlay.setAttribute("height", reference.Height);
        referenceOverlay.setAttribute("opacity", reference.Opacity);
    }

    function updateReference(scale, x, y, opacity) {
        const Width = reference.Width * scale, Height = reference.Height * scale;
        if (![Width, Height].every(value => value >= 0.01 && value <= 10000) ||
            ![x, y].every(value => Number.isFinite(value) && Math.abs(value) <= 10000) ||
            !(opacity >= 0 && opacity <= 1)) {
            status.textContent = "Enter a valid image size, position, and opacity.";
            return false;
        }
        reference = { ...reference, Width, Height, X: x, Y: y, Opacity: opacity };
        drawReference();
        return true;
    }

    byId("reference-file").onchange = async event => {
        const file = event.target.files[0];
        event.target.value = "";
        if (!file) return;
        const sequence = ++referenceSequence;
        const url = URL.createObjectURL(file);
        try {
            if (file.size > 32 * 1024 * 1024) throw new Error("Choose an image up to 32 MB.");
            const image = new Image();
            image.src = url;
            await image.decode();
            if (sequence !== referenceSequence || editor.hidden) return;
            const bitmap = document.createElement("canvas");
            const ratio = Math.min(1, 1200 / Math.max(image.naturalWidth, image.naturalHeight));
            bitmap.width = Math.max(1, Math.round(image.naturalWidth * ratio));
            bitmap.height = Math.max(1, Math.round(image.naturalHeight * ratio));
            bitmap.getContext("2d").drawImage(image, 0, 0, bitmap.width, bitmap.height);
            const span = Math.max(bitmap.width, bitmap.height);
            reference = { Png: bitmap.toDataURL("image/png").split(",")[1],
                Width: bitmap.width * 100 / span, Height: bitmap.height * 100 / span, X: 0, Y: 0, Opacity: 0.55 };
            calibration = null;
            drawReference();
            status.textContent = "Image attached. Adjust its settings or calibrate from two points.";
        } catch (error) {
            if (sequence === referenceSequence) status.textContent = error.message || "Could not decode the image.";
        } finally {
            URL.revokeObjectURL(url);
        }
    };
    byId("reference-settings").onclick = () => {
        const answer = prompt("Image width, center X, center Y (mm), and opacity (%)",
            [reference.Width, reference.X, reference.Y, reference.Opacity * 100].join(", "));
        if (answer === null) return;
        const [width, x, y, opacity] = answer.split(",").map(Number);
        updateReference(width / reference.Width, x, y, opacity / 100);
    };
    byId("reference-calibrate").onclick = () => {
        calibration = [];
        status.textContent = "Tap two points on the image a known distance apart.";
    };
    byId("reference-remove").onclick = () => {
        referenceSequence++;
        reference = calibration = null;
        drawReference();
    };
    function drawSketch() {
        const fixedShape = shape.value === "Rectangle" || shape.value === "Circle";
        const profile = fixedShape && points.length === 2 ? shapeProfile(shape.value, points) : points;
        outline.setAttribute("points", profile.map(([x, y]) => [x + 110, 110 - y].join(",")).join(" "));
        vertices.replaceChildren();
        for (const [x, y] of points) {
            const dot = document.createElementNS("http://www.w3.org/2000/svg", "circle");
            dot.setAttribute("cx", x + 110);
            dot.setAttribute("cy", 110 - y);
            dot.setAttribute("r", 1.4);
            vertices.append(dot);
        }
        undoPoint.disabled = !points.length || !!rules.length;
        shape.disabled = !!rules.length;
        const vertex = vertexPicker.value;
        vertexPicker.replaceChildren(...points.map((_, i) => new Option(String(i + 1), String(i))));
        if (vertex && Number(vertex) < points.length) vertexPicker.value = vertex;
        rulePicker.disabled = !geometryEngine || shape.value !== "Polygon" || points.length < 3;
    }
    function showSketch(active) {
        ++referenceSequence;
        calibration = null;
        if (active) {
            reference = project.Parts[editing]?.ReferenceImage ?? null;
            drawReference();
            const guide = faceSketch?.Guide ?? [];
            byId("face-guide").setAttribute("d", guide.map(([a,b]) =>
                `M ${110+a.X} ${110-a.Y} L ${110+b.X} ${110-b.Y}`).join(" "));
        }
        editor.hidden = !active;
        canvas.hidden = active;
        document.querySelector("#start-sketch").disabled = active;
        document.querySelector("#fit").disabled = active;
        byId("mode").textContent = active ? "Sketch Mode" : "Build Mode";
        byId("primitive").disabled = active;
        modeling.disabled = active || !geometryEngine;
        refreshParts();
        if (!active) redraw();
    }
    document.querySelector("#start-sketch").addEventListener("click", () => {
        ++loadSequence;
        editing = -1;
        rules = [];
        shape.value = "Polygon";
        faceSketch = null;
        pickingFace = false;
        byId("plane-choice").disabled = byId("plane-offset").disabled = false;
        byId("plane-choice").value = "XY";
        byId("plane-offset").value = 0;
        points.length = 0;
        drawSketch();
        showSketch(true);
        sketchHint();
    });
    byId("edit-sketch").onclick = () => {
        ++loadSequence;
        editing = selected;
        faceSketch = null;
        pickingFace = false;
        const part = project.Parts[selected];
        rules = part.Constraints?.[0] ?? [];
        byId("plane-choice").value = part.Plane ?? "XY";
        byId("plane-offset").value = part.Plane === "XZ" ? -part.Y : part.Plane === "YZ" ? part.X : part.Z;
        byId("plane-choice").disabled = byId("plane-offset").disabled = true;
        shape.value = "Polygon";
        points.splice(0, points.length, ...project.Parts[selected].Profile.map(({X,Y}) => [X,Y]));
        drawSketch();
        showSketch(true);
        status.textContent = "Undo points or append vertices, then finish the sketch.";
    };
    function sketchHint() {
        status.textContent = {
            Polygon: "Tap the outline points in order. Extrude closes the outline.",
            Rectangle: "Tap two opposite corners of the rectangle.",
            Circle: "Tap the center, then a point on the circle.",
            "Open path": "Tap the path points in order, then choose Finish sketch."
        }[shape.value];
    }
    shape.addEventListener("change", () => { points.length = 0; drawSketch(); sketchHint(); });
    plane.addEventListener("pointerup", event => {
        if (!event.isPrimary || event.button !== 0) return;
        const point = new DOMPoint(event.clientX, event.clientY).matrixTransform(plane.getScreenCTM().inverse());
        if (calibration) {
            const x = point.x - 110, y = 110 - point.y;
            if (Math.abs(x - reference.X) > reference.Width / 2 || Math.abs(y - reference.Y) > reference.Height / 2) {
                status.textContent = "Pick a point inside the reference image.";
                return;
            }
            if (!calibration.length) {
                calibration.push([x,y]);
                status.textContent = "Tap the second calibration point.";
                return;
            }
            const [[a,b]] = calibration;
            const distance = Math.hypot(x-a, y-b);
            if (distance < 0.01) return;
            calibration = null;
            const answer = prompt("Actual distance (mm)", distance.toFixed(2));
            if (answer === null) { sketchHint(); return; }
            const scale = Number(answer) / distance;
            if (updateReference(scale, a + (reference.X-a)*scale, b + (reference.Y-b)*scale, reference.Opacity))
                status.textContent = "Reference image calibrated.";
            return;
        }
        if (points.length >= 256 || rules.length) return;
        const x = Math.round(point.x), y = Math.round(point.y);
        if (x < 0 || x > 220 || y < 0 || y > 220) return;
        if ((shape.value === "Rectangle" || shape.value === "Circle") && points.length === 2) points.length = 0;
        points.push([x - 110, 110 - y]);
        drawSketch();
    });
    undoPoint.addEventListener("click", () => { points.pop(); drawSketch(); });
    document.querySelector("#cancel-sketch").addEventListener("click", () => {
        showSketch(false);
        status.textContent = "Sketch canceled.";
    });
    byId("finish-sketch").onclick = () => finishSketch(false);
    byId("extrude").onclick = () => finishSketch(true);
    function finishSketch(extrude) {
        try {
            const height = document.querySelector("#height").valueAsNumber;
            const open = shape.value === "Open path";
            if (open && (extrude || points.length < 2 || points.some((point, i) =>
                i && point[0] === points[i-1][0] && point[1] === points[i-1][1])))
                throw new Error("Finish an open path with at least two distinct consecutive points.");
            const profile = open ? points : shapeProfile(shape.value, points);
            if (!open) extrudeProfile(profile, extrude ? height : 1);
            const plane = byId("plane-choice").value;
            const offset = byId("plane-offset").valueAsNumber;
            if (editing < 0 && (!Number.isFinite(offset) || Math.abs(offset) > 10000))
                throw new Error("Enter a plane offset between −10,000 and 10,000 mm.");
            const [X,Y,Z] = editing < 0 ? modelPoint({ Plane: plane, X: 0, Y: 0, Z: 0 }, [0,0,offset]) : [0,0,0];
            const position = { X, Y, Z, Plane: plane, Width: 0, Depth: 0 };
            if (faceSketch) Object.assign(position, faceSketch.Center, { FaceFrame: faceSketch.Frame });
            const outline = profile.map(([X,Y]) => ({X,Y}));
            const list = [...project.Parts];
            const previous = list[editing];
            const link = previous?.SketchId ?? crypto.randomUUID();
            const index = editing < 0 ? list.length : editing;
            list[index] = { ...position, Kind: "Sketch", Name: `Sketch ${index + 1}`, Height: 0,
                ...previous, Profile: open ? null : outline, OpenPaths: open ? [outline] : previous?.OpenPaths,
                SketchId: link, Hidden: extrude || previous?.Hidden || false, ReferenceImage: reference ?? undefined,
                Constraints: rules.length ? [rules] : undefined };
            if (previous?.SketchId) {
                for (let i = 0; i < list.length; i++)
                    if (list[i].Kind === "Extrusion" && list[i].SketchId === link && !list[i].SketchProfileIndex)
                        list[i] = { ...list[i], Profile: outline };
            }
            if (extrude) list.push({ ...position, X: list[index].X, Y: list[index].Y, Z: list[index].Z,
                Plane: list[index].Plane, FaceFrame: list[index].FaceFrame, Kind: "Extrusion",
                Name: `Extrusion ${list.length + 1}`, Height: height, Profile: outline, SketchId: link });
            if (!changeParts(list, extrude ? list.length - 1 : index, reference ? 22 : rules.length ? 21 : open ? 17 : 11)) return;
            showSketch(false);
            fit();
            status.textContent = extrude ? `Extruded ${height} mm · Drag to orbit · Pinch or scroll to zoom` : "Sketch saved.";
        } catch (error) {
            status.textContent = error.message;
        }
    }
    document.querySelector("#export").addEventListener("click", () => {
        if (!model.length) return;
        download(new Blob([binaryStl(model)], { type: "model/stl" }), "LayerCAD-model.stl");
    });
    save.addEventListener("click", () => {
        if (project) download(new Blob([JSON.stringify(project)], { type: "application/json" }), "LayerCAD-model.layercad");
    });
    function download(blob, filename) {
        const url = URL.createObjectURL(blob);
        const link = document.createElement("a");
        link.href = url;
        link.download = filename;
        document.body.append(link);
        link.click();
        link.remove();
        setTimeout(() => URL.revokeObjectURL(url), 60000);
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
            const opened = projectFile ? JSON.parse(await file.text()) : null;
            if (projectFile) await engineReady;
            const result = projectFile ? parseProject(opened) : parseStl(await file.arrayBuffer());
            if (sequence !== loadSequence) return;
            if (!projectFile && !result.triangles.length) throw new Error("This file has no triangles.");
            upload(result.triangles, result.sketchTriangles);
            project = opened ?? { ...emptyProject(), Parts: [{ Kind: "Mesh", Name: file.name, Width: 0, Depth: 0, Height: 0, X: 0, Y: 0, Z: 0,
                Mesh: { Triangles: result.triangles.map(([a,b,c]) => {
                    const point = ([X,Y,Z]) => ({X,Y,Z});
                    return { A: point(a), B: point(b), C: point(c) };
                }) } }] };
            selected = -1;
            undo.length = redo.length = 0;
            pickingFace = false;
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
        tapStart = pointers.size === 1 && event.button === 0 ? [event.clientX, event.clientY] : null;
    });
    byId("face-sketch").onclick = () => {
        pickingFace = !pickingFace;
        refreshParts();
        status.textContent = pickingFace ? "Click or tap a planar face to start a sketch." : "Face selection canceled.";
    };
    canvas.addEventListener("click", event => {
        if (!pickingFace || !tapStart || Math.hypot(event.clientX-tapStart[0], event.clientY-tapStart[1]) > 5) return;
        tapStart = null;
        try {
            const bounds = canvas.getBoundingClientRect();
            const fit = Math.min(bounds.width, bounds.height) * 0.84 * zoom;
            const x = (2 * (event.clientX - bounds.left) - bounds.width) / fit;
            const y = (bounds.height - 2 * (event.clientY - bounds.top)) / fit;
            const cy = Math.cos(yaw), sy = Math.sin(yaw), cp = Math.cos(pitch), sp = Math.sin(pitch);
            const direction = [sy * cp, cy * cp, -sp];
            const origin = modelCenter.map((value, i) => value + modelRadius *
                ([cy * x + sy * sp * y, -sy * x + cy * sp * y, cp * y][i] - 2 * direction[i]));
            const vector = ([X,Y,Z]) => ({ X,Y,Z });
            let face = null;
            for (const part of project.Parts) {
                if (part.Hidden || part.Kind === "Sketch") continue;
                const hit = JSON.parse(geometryEngine.pickFace(JSON.stringify({
                    Part: part, Origin: vector(origin), Direction: vector(direction)
                })));
                if (hit && (!face || hit.Distance < face.Distance)) face = hit;
            }
            if (!face) { status.textContent = "Choose a visible planar face."; return; }
            pickingFace = false;
            byId("start-sketch").click();
            faceSketch = face;
            byId("plane-choice").disabled = byId("plane-offset").disabled = true;
            showSketch(true);
            status.textContent = "Sketch origin is at the face center.";
        } catch (error) { status.textContent = error.message; }
    });
    canvas.addEventListener("pointermove", event => {
        const old = pointers.get(event.pointerId);
        if (!old) return;
        const next = [event.clientX, event.clientY];
        pointers.set(event.pointerId, next);
        if (tapStart && Math.hypot(next[0]-tapStart[0], next[1]-tapStart[1]) > 5) tapStart = null;
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
    refreshParts();
}
