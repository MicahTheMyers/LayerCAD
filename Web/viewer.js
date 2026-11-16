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
    const starts = [];
    const drawingRanges = [];
    const extrude = typeof module === "undefined" ? extrudeProfile : require("./sketch.js").extrudeProfile;
    const makeShape = typeof module === "undefined" ? shapeProfile : require("./sketch.js").shapeProfile;
    let skipped = 0;
    for (const part of project.Parts) {
        starts.push([triangles.length, sketchTriangles.length]);
        const drawings = [];
        drawingRanges.push(drawings);
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
        if (part.Kind === "Sketch") {
            for (const profile of [part.Profile, ...(part.AdditionalProfiles ?? [])].filter(Boolean)) {
                const start = sketchTriangles.length;
                const caps = extrude(profile.map(({X,Y}) => [X,Y]), 1).slice(0, 2 * (profile.length - 2));
                for (let i = 1; i < caps.length; i += 2)
                    sketchTriangles.push(caps[i].map(([x,y]) => modelPoint(part, [x,y,0.008])));
                drawings.push([start, sketchTriangles.length]);
            }
            for (const path of part.OpenPaths ?? []) {
                const start = sketchTriangles.length;
                for (let i = 1; i < path.length; i++) {
                    const {X:x,Y:y} = path[i-1], {X:u,Y:v} = path[i];
                    const length = Math.hypot(u-x,v-y);
                    if (!length) continue;
                    const a = -(v-y)*0.18/length, b = (u-x)*0.18/length;
                    const [p,q,r,s] = [[x-a,y-b],[u-a,v-b],[u+a,v+b],[x+a,y+b]]
                        .map(([X,Y]) => modelPoint(part,[X,Y,0.008]));
                    sketchTriangles.push([p,q,r],[p,r,s]);
                }
                drawings.push([start, sketchTriangles.length]);
            }
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
    const ranges = starts.map(([solid, sketch], i) => {
        const [nextSolid, nextSketch] = starts[i + 1] ?? [triangles.length, sketchTriangles.length];
        return solid < nextSolid ? [solid, nextSolid] :
            [triangles.length + sketch, triangles.length + nextSketch];
    });
    return { triangles, sketchTriangles, ranges, skipped,
        drawings: drawingRanges.map(items => items.map(range => range.map(i => i + triangles.length))) };
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
        uniform mat3 camera;
        uniform vec3 cameraTarget;
        uniform vec3 scale;
        varying float light;
        void main() {
            gl_Position = vec4(camera * (position - cameraTarget) * scale, 1.0);
            light = 0.42 + 0.58 * abs(dot(camera * normal, normalize(vec3(-0.35, 0.5, -1.0))));
        }`);
    attach(gl.FRAGMENT_SHADER, `
        precision mediump float;
        varying float light;
        uniform vec3 tint;
        uniform float opacity;
        void main() { gl_FragColor = vec4(tint * light, opacity); }`);
    gl.linkProgram(program);
    if (!gl.getProgramParameter(program, gl.LINK_STATUS)) {
        status.textContent = "The browser could not initialize the 3D renderer.";
        return;
    }
    gl.useProgram(program);
    gl.enable(gl.DEPTH_TEST);
    const buffer = gl.createBuffer();
    const plateBuffer = gl.createBuffer();
    const previewBuffer = gl.createBuffer();
    const attributes = ["position", "normal"].map(name => gl.getAttribLocation(program, name));
    function bindGeometry(source) {
        gl.bindBuffer(gl.ARRAY_BUFFER, source);
        attributes.forEach((location, index) => {
            gl.enableVertexAttribArray(location);
            gl.vertexAttribPointer(location, 3, gl.FLOAT, false, 24, index * 12);
        });
    }
    let plateVertices = 0, plateDepth = 1;
    const camera = gl.getUniformLocation(program, "camera"), scale = gl.getUniformLocation(program, "scale");
    const cameraTarget = gl.getUniformLocation(program, "cameraTarget");
    const tint = gl.getUniformLocation(program, "tint");
    const opacity = gl.getUniformLocation(program, "opacity");
    let solidVertexCount = 0;
    let sceneTriangles = [], partRanges = [];
    let drawingRanges = [], selectedDrawing = -1;
    let extrusionPreview = null;
    const pointers = new Map();
    let tapStart = null;
    let vertexCount = 0, yaw = -0.6, pitch = 0.5, zoom = 1, pending = false, loadSequence = 0;
    let buildView = null, roll = 0;
    let cameraPan = [0,0,0], touchGesture = null;
    let selectedDetail = null, choosingSketchPlane = false;
    const dot = (a,b) => a[0]*b[0] + a[1]*b[1] + a[2]*b[2];
    const subtract = (a,b) => a.map((value,i) => value - b[i]);
    function cameraRows() {
        const cy = Math.cos(yaw), sy = Math.sin(yaw), cp = Math.cos(pitch), sp = Math.sin(pitch);
        const right = [cy,-sy,0], up = [sy*sp,cy*sp,cp];
        const c = Math.cos(roll), s = Math.sin(roll);
        return [right.map((value,i) => c*value + s*up[i]),
            up.map((value,i) => c*value - s*right[i]), [sy*cp,cy*cp,-sp]];
    }
    function cameraFrame() {
        let rows = cameraRows();
        let origin = modelCenter, basis;
        if (!editor.hidden) {
            const part = sketchFrame();
            origin = modelPoint(part, [0,0,0]);
            basis = [[1,0,0],[0,1,0],[0,0,1]].map(axis => subtract(modelPoint(part, axis), origin));
            rows = rows.map(row => row.reduce((result, value, i) =>
                result.map((component, axis) => component + value * basis[i][axis]), [0,0,0]));
        }
        const target = origin.map((value,i) => value + cameraPan[i]);
        return {
            rows, target, origin, basis,
            depth: plateDepth + Math.abs(dot(rows[2],subtract(target,modelCenter))) / modelRadius
        };
    }
    function viewportScale(width, height) {
        return Math.min(width, height) * 0.88 * zoom / plateDepth;
    }
    function positionSketch(frame, width, height) {
        const [u,v] = frame.basis;
        const [right, up] = frame.rows;
        const k = viewportScale(width, height) / (2 * modelRadius);
        const a = k*dot(right,u), b = -k*dot(up,u);
        const c = -k*dot(right,v), d = k*dot(up,v);
        const [x,y] = screenProjector(frame,width,height)(frame.origin);
        sketchSurface.setAttribute("transform",
            `matrix(${a} ${b} ${c} ${d} ${x-110*(a+c)} ${y-110*(b+d)})`);
    }

    function draw() {
        pending = false;
        drawViewCube();
        const ratio = Math.min(devicePixelRatio || 1, 2);
        const width = Math.round(canvas.clientWidth * ratio), height = Math.round(canvas.clientHeight * ratio);
        if (!width || !height) return;
        if (canvas.width !== width) canvas.width = width;
        if (canvas.height !== height) canvas.height = height;
        gl.viewport(0, 0, width, height);
        gl.clearColor(10 / 255, 20 / 255, 34 / 255, 1);
        gl.clear(gl.COLOR_BUFFER_BIT | gl.DEPTH_BUFFER_BIT);
        const fit = viewportScale(width, height);
        const frame = cameraFrame();
        gl.uniformMatrix3fv(camera, false, [0,1,2].flatMap(axis => frame.rows.map(row => row[axis])));
        gl.uniform3fv(cameraTarget, subtract(frame.target, modelCenter).map(value => value / modelRadius));
        gl.uniform3f(scale, fit / width, fit / height, 0.9 / Math.max(frame.depth,
            extrusionPreview ? extrusionPreview.radius + Math.hypot(...subtract(frame.target,modelCenter))/modelRadius : 0));
        gl.uniform1f(opacity, 1);
        if (!editor.hidden) positionSketch(frame, canvas.clientWidth, canvas.clientHeight);
        drawDetails(frame, canvas.clientWidth, canvas.clientHeight);
        bindGeometry(plateBuffer);
        gl.uniform3f(tint, 0.16, 0.24, 0.34);
        gl.enable(gl.POLYGON_OFFSET_FILL);
        gl.polygonOffset(1, 1);
        gl.drawArrays(gl.TRIANGLES, 0, 6);
        gl.disable(gl.POLYGON_OFFSET_FILL);
        gl.uniform3f(tint, 0.32, 0.43, 0.56);
        gl.drawArrays(gl.LINES, 6, plateVertices - 10);
        gl.uniform3f(tint, 0.78, 0.35, 0.33);
        gl.drawArrays(gl.LINES, plateVertices - 4, 2);
        gl.uniform3f(tint, 0.35, 0.67, 0.47);
        gl.drawArrays(gl.LINES, plateVertices - 2, 2);
        bindGeometry(buffer);
        const range = selectedDrawing >= 0 ? drawingRanges[selected]?.[selectedDrawing] :
            drawingRanges[selected]?.length > 1 ? null : partRanges[selected];
        const [start, end] = ((selectedDetail ? null : range) ?? [0, 0]).map(index => index * 3);
        function drawRange(first, last, color) {
            gl.uniform3f(tint, ...color);
            gl.drawArrays(gl.TRIANGLES, first, last - first);
        }
        for (const [first, last, color] of [
            [0, solidVertexCount, [0.76, 0.84, 0.94]],
            [solidVertexCount, vertexCount, [0.29, 0.85, 0.72]]
        ]) {
            const from = Math.min(last, Math.max(first, start));
            const to = Math.min(last, Math.max(first, end));
            drawRange(first, from, color);
            if (from < to) drawRange(from, to, [1, 0.75, 0.53]);
            drawRange(to, last, color);
        }
        if (extrusionPreview) {
            bindGeometry(previewBuffer);
            gl.enable(gl.BLEND);
            gl.blendFunc(gl.SRC_ALPHA,gl.ONE_MINUS_SRC_ALPHA);
            gl.depthMask(false);
            if (extrusionPreview.cut) gl.disable(gl.DEPTH_TEST);
            gl.uniform1f(opacity,0.32);
            gl.uniform3fv(tint,extrusionPreview.color);
            gl.drawArrays(gl.TRIANGLES,0,extrusionPreview.count);
            if (extrusionPreview.cut) gl.enable(gl.DEPTH_TEST);
            gl.depthMask(true);
            gl.disable(gl.BLEND);
        }
        drawExtrudeArrow(frame,canvas.clientWidth,canvas.clientHeight);
    }
    function redraw() {
        if (!pending) {
            pending = true;
            requestAnimationFrame(draw);
        }
    }
    function fit() {
        yaw = editor.hidden ? -0.6 : 0;
        pitch = editor.hidden ? 0.5 : Math.PI / 2;
        zoom = 1;
        roll = 0;
        cameraPan = [0,0,0];
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
    const collapsedGroups = new Set();
    const byId = id => document.getElementById(id);
    const save = document.querySelector("#save");
    let booleanEngine;
    import("./boolean.mjs?v=3").then(module => {
        booleanEngine = module;
        refreshParts();
    }).catch(() => {
        status.textContent = "The solid boolean engine could not load.";
    });
    const modeling = byId("model-tool");
    const engineReady = import("./engine/engine.js?v=features-3").then(engine => {
        geometryEngine = engine;
        modeling.disabled = !editor.hidden;
        refreshParts();
    }).catch(() => {
        status.textContent = "The advanced geometry engine could not load. Basic editing is available.";
    });
    function meshBuffer(triangles, center, radius) {
        const data = new Float32Array(triangles.length * 18);
        let offset = 0;
        for (const [a,b,c] of triangles) {
            const [x,y,z] = subtract(b,a), [u,v,w] = subtract(c,a);
            const normal = [y*w-z*v,z*u-x*w,x*v-y*u];
            const length = Math.hypot(...normal) || 1;
            for (const point of [a,b,c]) {
                for (let i = 0; i < 3; i++) data[offset++] = (point[i]-center[i])/radius;
                for (const value of normal) data[offset++] = value/length;
            }
        }
        return data;
    }
    function upload(solids, sketches = [], ranges = [[0, solids.length]], drawings = []) {
    const triangles = solids.concat(sketches);
    const min = [Infinity, Infinity, Infinity], max = [-Infinity, -Infinity, -Infinity];
    for (const triangle of triangles) for (const point of triangle) for (let i = 0; i < 3; i++) {
        if (!Number.isFinite(point[i])) throw new Error("The model contains invalid coordinates.");
        min[i] = Math.min(min[i], point[i]);
        max[i] = Math.max(max[i], point[i]);
    }
    const center = min.map((value, i) => (value + max[i]) / 2);
    const radius = Math.max(0.001, Math.hypot(...max.map((value, i) => value - center[i])));
    const data = meshBuffer(triangles,center,radius);
    gl.bindBuffer(gl.ARRAY_BUFFER, buffer);
    gl.bufferData(gl.ARRAY_BUFFER, data, gl.STATIC_DRAW);
    if (gl.getError() !== gl.NO_ERROR) throw new Error("The model exceeds available graphics memory.");
    vertexCount = data.length / 6;
        sceneTriangles = triangles;
        partRanges = ranges;
        drawingRanges = drawings;
        selectedDrawing = -1;
        solidVertexCount = solids.length * 3;
        modelCenter = triangles.length ? center : [0,0,0];
        modelRadius = triangles.length ? radius : 1;
        model = solids;
        document.querySelector("#export").disabled = !solids.length;
    }
    function uploadPlate() {
        const bed = project.Bed ?? emptyProject().Bed;
        byId("volume-label").textContent = `${bed.X} × ${bed.Y} × ${bed.Z} mm build volume`;
        const dimension = value => Number.isFinite(value) && value > 0 ? value : 220;
        const x = dimension(project.Bed?.X) / 2, y = dimension(project.Bed?.Y) / 2;
        if (!sceneTriangles.length) modelRadius = Math.hypot(x, y) * 0.8;
        const data = [];
        const point = (a, b, z) => {
            const p = [a, b, z].map((value, axis) => (value - modelCenter[axis]) / modelRadius);
            plateDepth = Math.max(plateDepth, Math.hypot(...p));
            data.push(...p, 0, 0, 1);
        };
        plateDepth = 1;
        for (const [a,b] of [[-x,-y],[x,-y],[x,y],[-x,-y],[x,y],[-x,y]]) point(a,b,-0.04);
        const line = (a,b,c,d) => { point(a,b,-0.04); point(c,d,-0.04); };
        for (const a of [-x, x]) line(a,-y,a,y);
        for (const b of [-y, y]) line(-x,b,x,b);
        for (let a = 10; a < x && a <= 1000; a += 10) { line(a,-y,a,y); line(-a,-y,-a,y); }
        for (let b = 10; b < y && b <= 1000; b += 10) { line(-x,b,x,b); line(-x,-b,x,-b); }
        line(-x,0,x,0);
        line(0,-y,0,y);
        gl.bindBuffer(gl.ARRAY_BUFFER, plateBuffer);
        gl.bufferData(gl.ARRAY_BUFFER, new Float32Array(data), gl.STATIC_DRAW);
        plateVertices = data.length / 6;
    }
    function refreshParts() {
        redraw();
        const panel = byId("parts");
        panel.replaceChildren();
        for (const [title, sketches] of [["Objects", false], ["Sketches", true]]) {
            const section = document.createElement("details");
            const heading = document.createElement("summary");
            heading.textContent = `${title} ${project.Parts.filter(part => (part.Kind === "Sketch") === sketches).length}`;
            section.open = !collapsedGroups.has(title);
            section.ontoggle = () => section.open ? collapsedGroups.delete(title) : collapsedGroups.add(title);
            section.append(heading);
            panel.append(section);
            project.Parts.forEach((part, index) => {
                if ((part.Kind === "Sketch") !== sketches) return;
                const row = document.createElement("div");
                row.className = "part-row";
                const pick = document.createElement("button");
                pick.textContent = part.Name;
                pick.setAttribute("aria-pressed", index === selected);
                pick.onclick = () => selectPart(index);
                const visibility = document.createElement("button");
                visibility.textContent = part.Hidden ? "○" : "◉";
                visibility.setAttribute("aria-label", (part.Hidden ? "Show " : "Hide ") + part.Name);
                visibility.onclick = () => changeParts(project.Parts.map((item, i) =>
                    i === index ? { ...item, Hidden: !item.Hidden } : item));
                row.append(pick, visibility);
                section.append(row);
                const count = sketchProfiles(part).length;
                const paths = part.OpenPaths?.length ?? 0;
                if (count + paths < 2) return;
                for (let drawing = 0; drawing < count + paths; drawing++) {
                    const item = document.createElement("button");
                    item.className = "drawing-row";
                    item.textContent = drawing < count ? `Profile ${drawing + 1}` : `Path ${drawing - count + 1}`;
                    item.setAttribute("aria-pressed", index === selected && drawing === selectedDrawing);
                    item.onclick = () => selectPart(index,drawing);
                    section.append(item);
                }
            });
        }
        const part = project.Parts[selected];
        byId("edit-sketch").disabled = !editor.hidden || part?.Kind !== "Sketch" || !!part.Dimensions ||
            !(part.Profile?.length || part.OpenPaths?.length);
        byId("extrude-part").disabled = !editor.hidden;
        updateExtrudeTarget();
        byId("move-part").disabled = !editor.hidden || !part;
        byId("face-sketch").disabled = !editor.hidden || !geometryEngine || !model.length;
        byId("face-sketch").setAttribute("aria-pressed", pickingFace);
        byId("pattern").disabled = !editor.hidden || !part || part.Kind === "Sketch";
        byId("hole").disabled = !editor.hidden || !geometryEngine ||
            !["Box", "Cylinder", "Tube", "Extrusion"].includes(part?.Kind);
        byId("bevel").disabled = !editor.hidden || !geometryEngine || !["Box","Cylinder","Extrusion"].includes(part?.Kind);
        byId("thread").disabled = !editor.hidden || !geometryEngine ||
            !(part?.Kind === "Cylinder" || part?.Kind === "Tube" || part?.Holes?.length ||
                part?.Kind === "Extrusion" && part.Profile?.length >= 96);
        byId("combine").disabled = !editor.hidden || !booleanEngine || !part || part.Hidden ||
            part.Kind === "Sketch" || !project.Parts.some(item => item !== part && !item.Hidden && item.Kind !== "Sketch");
        byId("shell").disabled = !editor.hidden || !booleanEngine || !canShell(part);
        const solid = editor.hidden && part && !part.Hidden && part.Kind !== "Sketch";
        byId("split-body").disabled = !solid || !booleanEngine;
        byId("scale-body").disabled = !solid;
        byId("mirror-body").disabled = !solid;
        byId("circular-pattern").disabled = !solid;
        byId("remove-part").disabled = !editor.hidden || !part;
        byId("undo").disabled = !editor.hidden || !undo.length;
        byId("redo").disabled = !editor.hidden || !redo.length;
        save.disabled = !project.Parts.length;
    }
    function loadScene(next) {
        const result = parseProject(next);
        upload(result.triangles, result.sketchTriangles, result.ranges, result.drawings);
        project = next;
        extrudeFace = selectedDetail = null;
        uploadPlate();
        refreshParts();
        redraw();
        return result;
    }
    function changeParts(parts, nextSelection = selected, minimumVersion = 11, bed = project.Bed) {
        const before = project;
        try {
            loadScene({ ...project, Version: Math.max(minimumVersion, project.Version), Parts: parts, Bed: bed });
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
    function canShell(part) {
        return part && !part.Hidden && ["Box","Cylinder"].includes(part.Kind) &&
            !part.Bevel && !part.BevelCuts && !part.Holes && !part.Threads;
    }
    function solidMesh(part) {
        const mesh = parseProject({ Parts: [part] }).triangles;
        if (!mesh.length) throw new Error("The selected body has no supported solid geometry.");
        return mesh;
    }
    function meshBody(name, triangles) {
        return { Kind: "Mesh", Name: name, X: 0, Y: 0, Z: 0, Width: 0, Depth: 0, Height: 0,
            Mesh: { Triangles: triangles } };
    }
    function transformMesh(mesh, transform, reverse = false) {
        const point = vertex => {
            const [X,Y,Z] = transform(vertex);
            return { X,Y,Z };
        };
        return mesh.map(([a,b,c]) => ({ A: point(a), B: point(reverse ? c : b), C: point(reverse ? b : c) }));
    }
    const meshVectors = triangles => triangles.map(({ A, B, C }) => [A, B, C].map(({ X,Y,Z }) => [X,Y,Z]));
    const sketchProfiles = part => [part?.Profile, ...(part?.AdditionalProfiles ?? [])].filter(Boolean);
    function selectPart(index,drawing = -1) {
        extrudeFace = selectedDetail = null;
        selected = index;
        selectedDrawing = drawing;
        refreshParts();
    }
    function extrudeSketch(parts, sketchIndex, distance, operation, bodyIndex = -1, profileIndex = -1) {
        if (!["add", "cut", "merge"].includes(operation) || !Number.isFinite(distance) ||
            Math.abs(distance) < 0.01 || Math.abs(distance) > 10000)
            throw new Error("Choose Add, Cut, or Merge and a signed distance of 0.01–10,000 mm.");
        const source = parts[sketchIndex];
        const profiles = sketchProfiles(source);
        const link = source.SketchId ?? crypto.randomUUID();
        const prisms = profiles.flatMap((Profile, index) => {
            if (profileIndex >= 0 && index !== profileIndex) return [];
            const prism = { Kind: "Extrusion", Name: `Extrusion ${parts.length + index + 1}`,
                X: source.X, Y: source.Y, Z: source.Z, Width: 0, Depth: 0, Height: Math.abs(distance),
                Profile, SketchId: link, SketchProfileIndex: index, Plane: source.Plane, FaceFrame: source.FaceFrame };
            if (distance > 0) return prism;
            const [X,Y,Z] = modelPoint(source, [0,0,distance]);
            return meshBody(prism.Name, transformMesh(solidMesh({ ...prism, X,Y,Z }), point => point));
        });
        if (operation === "add") {
            parts[sketchIndex] = { ...source, SketchId: link, Hidden: source.Hidden ||
                (!source.OpenPaths?.length && (profileIndex < 0 || profiles.length === 1)) };
            if (bodyIndex >= 0) {
                if (!booleanEngine) throw new Error("The solid modeling engine is still loading.");
                const body = parts[bodyIndex];
                let vertices = solidMesh(body), triangles;
                for (const prism of prisms) {
                    triangles = booleanEngine.applyExtrusion(vertices, solidMesh(prism), "merge");
                    if (!triangles?.length) throw new Error("The extrusion must connect to the selected body.");
                    vertices = meshVectors(triangles);
                }
                parts[bodyIndex] = meshBody(body.Name, triangles);
                return parts;
            }
            return [...parts, ...prisms];
        }
        if (!booleanEngine) throw new Error("The solid modeling engine is still loading.");
        const profilesToApply = prisms.map(solidMesh);
        if (operation === "cut") {
            let changed = false;
            const result = parts.flatMap(part => {
                if (part.Hidden || part.Kind === "Sketch") return [part];
                let replacement = null;
                for (const tool of profilesToApply) {
                    if (replacement?.length === 0) break;
                    const cut = booleanEngine.applyExtrusion(replacement ? meshVectors(replacement) : solidMesh(part), tool, "cut");
                    if (cut !== null) replacement = cut;
                }
                if (replacement === null) return [part];
                changed = true;
                return replacement.length ? [meshBody(part.Name, replacement)] : [];
            });
            if (!changed) throw new Error("The extrusion does not cut any visible solid.");
            return result;
        }
        const result = [...parts];
        let merged = false;
        for (const tool of profilesToApply) {
            let vertices = tool, triangles = null, name = "Extrusion";
            for (let i = 0; i < result.length; i++) {
                const part = result[i];
                if (part.Hidden || part.Kind === "Sketch") continue;
                const joined = booleanEngine.applyExtrusion(solidMesh(part), vertices, "merge");
                if (joined === null) continue;
                name = triangles ? name : part.Name;
                triangles = joined;
                vertices = meshVectors(joined);
                result.splice(i, 1);
                i = -1;
            }
            if (triangles) merged = true;
            result.push(meshBody(name, triangles ?? transformMesh(vertices, point => point)));
        }
        if (!merged) throw new Error("The extrusion does not join any visible solid.");
        return result;
    }
    byId("mirror-body").onclick = () => {
        const answer = prompt("Mirror plane and position (mm): XY, XZ, or YZ", "YZ, 0");
        if (answer === null) return;
        const [plane, distance, ...extra] = answer.toUpperCase().split(",").map(value => value.trim());
        const axis = new Map([["XY",2],["XZ",1],["YZ",0]]).get(plane);
        const offset = Number(distance);
        if (axis === undefined || !distance || extra.length || !Number.isFinite(offset)) {
            status.textContent = "Choose a plane and a numeric position.";
            return;
        }
        try {
            const part = project.Parts[selected];
            const mesh = transformMesh(solidMesh(part),
                vertex => vertex.map((value, i) => i === axis ? 2*offset-value : value), true);
            if (changeParts([...project.Parts, meshBody(part.Name + " mirror", mesh)], project.Parts.length)) fit();
        } catch (error) { status.textContent = error.message; }
    };
    byId("circular-pattern").onclick = () => {
        const answer = prompt("Copies, angle between copies (degrees), center X, center Y (mm)", "3, 90, 0, 0");
        if (answer === null) return;
        const values = answer.split(",").map(Number);
        const [count, angle, cx, cy] = values;
        if (values.length !== 4 || !values.every(Number.isFinite) ||
            !Number.isInteger(count) || count < 1 || count > 40 || !angle || Math.abs(angle)*count >= 360) {
            status.textContent = "Enter 1–40 copies and a nonzero spacing spanning less than 360°.";
            return;
        }
        try {
            const part = project.Parts[selected], original = solidMesh(part);
            if (original.length * (count + 1) > triangleLimit)
                throw new Error("Reduce the copy count to fit the project's triangle limit.");
            const copies = Array.from({ length: count }, (_, i) => {
                const radians = angle * (i + 1) * Math.PI / 180;
                const cos = Math.cos(radians), sin = Math.sin(radians);
                return meshBody(`${part.Name} copy ${i+1}`, transformMesh(original, ([x,y,z]) =>
                    [cx + (x-cx)*cos - (y-cy)*sin, cy + (x-cx)*sin + (y-cy)*cos, z]));
            });
            if (changeParts([...project.Parts, ...copies])) fit();
        } catch (error) { status.textContent = error.message; }
    };
    byId("split-body").onclick = () => {
        const input = prompt("Split plane and position (mm): XY, XZ, or YZ", "XY, 5");
        if (input === null) return;
        const [plane, distance, ...extra] = input.toUpperCase().split(",").map(value => value.trim());
        const axis = new Map([["XY",[0,0,1]],["XZ",[0,1,0]],["YZ",[1,0,0]]]).get(plane);
        if (!axis || !distance || extra.length || !Number.isFinite(Number(distance))) {
            status.textContent = "Choose a plane and a numeric position.";
            return;
        }
        try {
            const stock = project.Parts[selected];
            const pieces = booleanEngine.split(solidMesh(stock), axis, Number(distance));
            if (pieces.some(piece => !piece.length)) throw new Error("Move the plane so it crosses the body.");
            const parts = [...project.Parts];
            parts.splice(selected, 1, ...pieces.map((mesh,i) => meshBody(`${stock.Name} ${i+1}`, mesh)));
            if (changeParts(parts)) status.textContent = "Body split into two pieces.";
        } catch (error) { status.textContent = error.message; }
    };
    byId("scale-body").onclick = () => {
        const input = prompt("Scale factors X, Y, Z", "1, 1, 1");
        if (input === null) return;
        const factors = input.split(",").map(Number);
        if (factors.length !== 3 || !factors.every(value => value >= 0.001 && value <= 1000)) {
            status.textContent = "Enter three scale factors between 0.001 and 1000.";
            return;
        }
        try {
            const part = project.Parts[selected], pivot = [part.X, part.Y, part.Z];
            const triangles = solidMesh(part).map(triangle => {
                const point = vertex => {
                    const [X,Y,Z] = vertex.map((value,i) => pivot[i] + (value-pivot[i]) * factors[i]);
                    return { X,Y,Z };
                };
                return { A: point(triangle[0]), B: point(triangle[1]), C: point(triangle[2]) };
            });
            if (changeParts(project.Parts.map(item => item === part ? meshBody(part.Name, triangles) : item))) fit();
        } catch (error) { status.textContent = error.message; }
    };
    function applyBoolean(stock, tool, operation, consumeTool) {
        try {
            const triangles = booleanEngine.combine(solidMesh(stock), solidMesh(tool), operation);
            if (!triangles.length) throw new Error("The operation leaves no solid geometry.");
            const result = meshBody(stock.Name, triangles);
            const parts = project.Parts.filter(part => !consumeTool || part !== tool)
                .map(part => part === stock ? result : part);
            if (changeParts(parts, parts.indexOf(result))) status.textContent = "Solid operation applied.";
        } catch (error) { status.textContent = error.message || "The selected meshes must be closed solids."; }
    }
    byId("combine").onclick = () => {
        const stock = project.Parts[selected];
        const tools = project.Parts.filter(part => part !== stock && !part.Hidden && part.Kind !== "Sketch");
        const selection = prompt("Choose a tool body by number:\n" + tools.map((part,i) => `${i+1}. ${part.Name}`).join("\n"), "1");
        if (selection === null) return;
        const tool = tools[Number(selection)-1];
        if (!tool) { status.textContent = "Choose an available body number."; return; }
        const operation = prompt("Operation: Join, Cut, or Intersect", "Join")?.trim().toLowerCase();
        if (!operation) return;
        const method = new Map([["join","add"],["cut","subtract"],["intersect","intersect"]]).get(operation);
        if (!method) { status.textContent = "Choose Join, Cut, or Intersect."; return; }
        applyBoolean(stock, tool, method, true);
    };
    byId("shell").onclick = () => {
        const stock = project.Parts[selected];
        const answer = prompt("Wall thickness (mm)", "2");
        if (answer === null) return;
        const thickness = Number(answer);
        if (!(thickness >= 0.01 && thickness < stock.Height && 2*thickness < stock.Width &&
            (stock.Kind !== "Box" || 2*thickness < stock.Depth))) {
            status.textContent = "Reduce the wall thickness so the cavity fits.";
            return;
        }
        const cavity = { ...stock, Width: stock.Width-2*thickness, Depth: stock.Depth-2*thickness,
            Z: stock.Z+thickness, Height: stock.Height-thickness+1 };
        applyBoolean(stock, cavity, "subtract", false);
    };
    const featureDialog = byId("feature-dialog");
    function makePopupDraggable(panel, container = null) {
        const title = panel.querySelector(".popup-title");
        let x = 0, y = 0, drag = null;
        function move(nextX, nextY) {
            if (!panel.getClientRects().length) return;
            const rect = panel.getBoundingClientRect();
            const bounds = container?.getBoundingClientRect() ??
                { left: 0, top: 0, right: document.documentElement.clientWidth, bottom: document.documentElement.clientHeight };
            const clamp = (value, minimum, maximum) => Math.max(minimum, Math.min(value, maximum));
            x = clamp(nextX, x + bounds.left - rect.left, x + bounds.right - rect.right);
            y = clamp(nextY, y + bounds.top - rect.top, y + bounds.bottom - rect.bottom);
            panel.style.translate = `${x}px ${y}px`;
        }
        title.addEventListener("pointerdown", event => {
            if (!event.isPrimary || event.button !== 0) return;
            drag = { id: event.pointerId, startX: event.clientX - x, startY: event.clientY - y };
            title.setPointerCapture(event.pointerId);
            event.preventDefault();
        });
        title.addEventListener("pointermove", event => {
            if (drag?.id === event.pointerId) move(event.clientX - drag.startX, event.clientY - drag.startY);
        });
        title.addEventListener("lostpointercapture", () => { drag = null; });
        title.addEventListener("keydown", event => {
            const direction = { ArrowLeft: [-1,0], ArrowRight: [1,0], ArrowUp: [0,-1], ArrowDown: [0,1] }[event.key];
            if (!direction) return;
            event.preventDefault();
            const [dx,dy] = direction;
            move(x + 12*dx,y + 12*dy);
        });
        const clampToBounds = () => move(x,y);
        const observer = new ResizeObserver(clampToBounds);
        observer.observe(panel);
        if (container) observer.observe(container);
        window.addEventListener("resize", clampToBounds);
    }
    makePopupDraggable(featureDialog);
    const featureAmount = byId("feature-amount");
    const featureSurface = byId("feature-surface");
    const featureMetric = byId("feature-metric");
    function customThread() {
        const custom = featureMetric.value.trim().toLowerCase() === "custom";
        byId("feature-pitch-label").hidden = byId("feature-depth-label").hidden = !custom;
        return custom;
    }
    featureMetric.oninput = customThread;
    let activeFeature = null;
    const holeHandle = byId("hole-handle");
    let holeDrag = null;
    const worldPoint = ({ X,Y,Z }) => [X,Y,Z];
    function holeOpen() {
        return featureDialog.open && activeFeature?.tool === "hole";
    }
    function openFeature(tool) {
        const part = project.Parts[selected];
        if (!part) return;
        const detail = selectedDetail;
        if (tool === "hole" && !detail?.face) {
            status.textContent = "Select a flat face, then choose Hole.";
            return;
        }
        if (featureDialog.open) featureDialog.close();
        closeExtrude();
        const thread = tool === "thread";
        const labels = { bevel: "Size (mm)", hole: "Diameter (mm)", thread: "Length (mm, 0 for auto)" };
        const names = { bevel: "Bevel", hole: "Hole", thread: "Thread" };
        byId("feature-title").textContent = names[tool];
        byId("feature-amount-label").firstElementChild.textContent = labels[tool];
        featureAmount.value = { bevel: 2, hole: 5, thread: 0 }[tool];
        byId("feature-error").textContent = "";
        byId("apply-feature").disabled = false;
        byId("hole-hint").hidden = tool !== "hole";
        byId("feature-surface-label").hidden = byId("feature-metric-label").hidden = !thread;
        byId("feature-bevel-style-label").hidden = tool !== "bevel";
        byId("feature-bevel-style").value = "Flat";
        featureMetric.value = "";
        customThread();
        if (thread) {
            const surfaces = (part.Holes ?? []).map((_, i) => new Option("Hole " + (i+1), i));
            if (part.Kind === "Cylinder" || part.Kind === "Tube" || part.Kind === "Extrusion" && part.Profile?.length >= 96)
                surfaces.push(new Option("Outer cylindrical surface", -1));
            if (part.Kind === "Tube") surfaces.push(new Option("Tube bore", -2));
            featureSurface.replaceChildren(...surfaces);
        }
        const point = tool === "hole" ? detail.face.Center : detail?.point ??
            (detail?.kind === "Edge" ? detail.edges[0][0].map((value,i) => (value+detail.edges[0][1][i])/2) : null);
        const asPoint = ([X,Y,Z]) => ({ X,Y,Z });
        activeFeature = { tool, part, index: selected, request: {
            Part: part, Kind: detail?.kind, Frame: detail?.face.Frame,
            Point: Array.isArray(point) ? asPoint(point) : point
        }};
        featureDialog.classList.toggle("with-preview", !thread);
        featureDialog.classList.toggle("hole-placement",tool === "hole");
        if (tool === "hole") {
            featureDialog.show();
            byId("feature-title").focus({ preventScroll: true });
        } else {
            featureDialog.showModal();
            featureAmount.select();
        }
        if (!updateFeaturePreview() && tool === "hole") {
            activeFeature.request.Point = detail.face.Point;
            if (!updateFeaturePreview()) {
                activeFeature.request.Point = detail.face.Center;
                updateFeaturePreview();
            }
        }
    }
    function updateFeaturePreview() {
        if (!featureDialog.open || activeFeature?.tool === "thread") return true;
        const errorText = byId("feature-error");
        errorText.textContent = "";
        extrusionPreview = null;
        try {
            const { tool, request } = activeFeature;
            const amount = featureAmount.valueAsNumber;
            if (Number.isFinite(amount) && amount > 0) {
                const json = JSON.stringify(request);
                const data = JSON.parse(tool === "bevel" ?
                    geometryEngine.bevelPreview(json, amount, byId("feature-bevel-style").value === "Rounded") :
                    geometryEngine.holePreview(json, amount));
                setToolPreview(meshVectors(data.Region), worldPoint(data.Origin), worldPoint(data.End), true);
            }
        } catch (error) {
            errorText.textContent = error.message;
        }
        byId("apply-feature").disabled = !extrusionPreview;
        redraw();
        return !!extrusionPreview;
    }
    featureAmount.oninput = updateFeaturePreview;
    byId("feature-bevel-style").onchange = updateFeaturePreview;
    featureDialog.onclose = () => {
        if (featureDialog.open) return;
        activeFeature = holeDrag = null;
        updateExtrudePreview();
    };
    holeHandle.addEventListener("pointerdown", event => {
        if (!holeOpen() || !event.isPrimary || event.button !== 0) return;
        if (featureDialog.contains(document.activeElement))
            byId("feature-title").focus({ preventScroll: true });
        const bounds = canvas.getBoundingClientRect();
        holeDrag = { id: event.pointerId, x: event.clientX-bounds.left-Number(holeHandle.getAttribute("cx")),
            y: event.clientY-bounds.top-Number(holeHandle.getAttribute("cy")),
            startX: event.clientX, startY: event.clientY, readyAt: performance.now()+250, moving: false };
        holeHandle.setPointerCapture(event.pointerId);
        event.preventDefault();
    });
    holeHandle.addEventListener("pointermove", event => {
        if (!holeOpen() || event.pointerId !== holeDrag?.id) return;
        if (!(event.buttons & 1)) { holeDrag = null; return; }
        if (!holeDrag.moving) {
            if (performance.now() < holeDrag.readyAt) {
                if (Math.hypot(event.clientX-holeDrag.startX,event.clientY-holeDrag.startY) > 7) holeDrag = null;
                return;
            }
            holeDrag.moving = true;
        }
        const bounds = canvas.getBoundingClientRect();
        const frame = cameraFrame();
        const [right,up,depth] = frame.rows;
        const scale = viewportScale(bounds.width,bounds.height)/(2*modelRadius);
        if (!scale) return;
        const x = (event.clientX-bounds.left-holeDrag.x-bounds.width/2)/scale;
        const y = (bounds.height/2-event.clientY+bounds.top+holeDrag.y)/scale;
        const point = frame.target.map((value,i) => value+x*right[i]+y*up[i]);
        const request = activeFeature.request;
        const u = worldPoint(request.Frame.Horizontal), v = worldPoint(request.Frame.Vertical);
        const normal = [u[1]*v[2]-u[2]*v[1],u[2]*v[0]-u[0]*v[2],u[0]*v[1]-u[1]*v[0]];
        const denominator = dot(normal,depth);
        if (Math.abs(denominator) < 0.02) return;
        const distance = dot(normal,subtract(worldPoint(request.Point),point))/denominator;
        const [X,Y,Z] = point.map((value,i) => value+distance*depth[i]);
        request.Point = { X,Y,Z };
        updateFeaturePreview();
    });
    for (const type of ["pointerup","pointercancel","lostpointercapture"])
        holeHandle.addEventListener(type, () => { holeDrag = null; });
    for (const tool of ["bevel","hole","thread"]) byId(tool).onclick = () => openFeature(tool);
    byId("cancel-feature").onclick = () => featureDialog.close();
    byId("feature-form").onsubmit = event => {
        event.preventDefault();
        const { tool, part, index, request } = activeFeature;
        const amount = featureAmount.valueAsNumber;
        const errorText = byId("feature-error");
        try {
            if (project.Parts[index] !== part) throw new Error("The selected body changed. Reopen the tool.");
            const output = tool === "thread"
                ? geometryEngine.thread(JSON.stringify(part), Number(featureSurface.value),
                    customThread() ? "custom" : featureMetric.value, amount,
                    byId("feature-pitch").valueAsNumber, byId("feature-depth").valueAsNumber)
                : tool === "bevel" ? geometryEngine.bevel(JSON.stringify(request), amount,
                    byId("feature-bevel-style").value === "Rounded") : geometryEngine.hole(JSON.stringify(request), amount);
            const updated = JSON.parse(output);
            const version = tool === "thread" ? 13 : tool === "hole" ? 17 : 18;
            if (!changeParts(project.Parts.map((item,i) => i === index ? updated : item), index, version)) {
                errorText.textContent = status.textContent;
                return;
            }
            featureDialog.close();
            status.textContent = byId("feature-title").textContent + " applied to " + part.Name + ".";
        } catch (error) { errorText.textContent = error.message; }
    };
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
            } else if (tool === "Rib" || tool === "Web") {
                const paths = project.Parts.flatMap(part => part.Hidden || part.Kind !== "Sketch" ? [] :
                    (part.OpenPaths ?? []).map(path => ({ ...part, Profile: null, AdditionalProfiles: null,
                        Constraints: null, Dimensions: null, OpenPaths: [path] })));
                if (paths.length < (tool === "Web" ? 2 : 1)) throw new Error("Show the open sketch paths to reinforce.");
                const answer = prompt(`Thickness and height (mm), then ${tool === "Web" ? "two or more path numbers" : "one path number"}\n` +
                    paths.map((part, index) => `${index + 1}. ${part.Name}`).join("\n"),
                    tool === "Web" ? "2, 8, 1, 2" : "2, 8, 1");
                if (answer === null) return;
                const [thickness, height, ...indices] = answer.split(",").map(Number);
                if (!(thickness >= 0.01 && thickness <= 10000 && height >= 0.01 && height <= 10000) ||
                    (tool === "Rib" ? indices.length !== 1 : indices.length < 2) ||
                    new Set(indices).size !== indices.length || indices.some(i => !Number.isInteger(i) || !paths[i - 1]))
                    throw new Error("Enter positive dimensions and the requested number of distinct paths.");
                if (tool === "Web" && !booleanEngine) throw new Error("The solid modeling engine is still loading.");
                const walls = indices.map(i => JSON.parse(geometryEngine.modelPart("Rib", JSON.stringify([paths[i - 1]]),
                    thickness, height, 0, 0)));
                const triangles = walls.reduce((wall, next) =>
                    booleanEngine.combine(meshVectors(wall), meshVectors(next), "add"));
                if (changeParts([...project.Parts, meshBody(tool, triangles)], project.Parts.length)) fit();
                return;
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
    const extrudePanel = byId("build-extrude");
    makePopupDraggable(extrudePanel, byId("viewport"));
    let extrudeFace = null;
    const canExtrude = part => part?.Kind === "Sketch" && !!part.Profile;
    function buildExtrudeSource() {
        if (extrudeFace) return { part: extrudeFace, profileIndex: 0 };
        const part = project.Parts[selected];
        const count = sketchProfiles(part).length;
        const profileIndex = selectedDrawing < 0 && count === 1 ? 0 : selectedDrawing;
        return canExtrude(part) && profileIndex >= 0 && profileIndex < count ? { part, profileIndex } : null;
    }
    function updateExtrudePreview() {
        if (holeOpen()) return;
        extrusionPreview = null;
        const distance = byId("build-extrude-distance").valueAsNumber;
        const target = !extrudePanel.hidden && buildExtrudeSource();
        if (target && Math.abs(distance) >= 0.01 && Math.abs(distance) <= 10000) {
            const { part, profileIndex } = target;
            const profile = sketchProfiles(part)[profileIndex].map(({X,Y}) => [X,Y]);
            const triangles = extrudeProfile(profile,Math.abs(distance)).map(triangle =>
                triangle.map(([x,y,z]) => modelPoint(part,[x,y,z + Math.min(0,distance)])));
            const center = profile.reduce(([x,y],[u,v]) => [x+u/profile.length,y+v/profile.length],[0,0]);
            setToolPreview(triangles, modelPoint(part,[...center,0]), modelPoint(part,[...center,distance]),
                byId("build-extrude-operation").value === "Cut");
        }
        redraw();
    }
    function setToolPreview(triangles, origin, end, cut) {
        let radius = 0;
        for (const triangle of triangles) for (const point of triangle)
            radius = Math.max(radius,Math.hypot(...subtract(point,modelCenter))/modelRadius);
        gl.bindBuffer(gl.ARRAY_BUFFER,previewBuffer);
        gl.bufferData(gl.ARRAY_BUFFER,meshBuffer(triangles,modelCenter,modelRadius),gl.DYNAMIC_DRAW);
        extrusionPreview = { count: triangles.length*3, radius, origin, end,
            color: cut ? [1,0.32,0.36] : [0.28,0.95,0.56], cut };
    }
    function drawExtrudeArrow(frame,width,height) {
        const arrow = byId("extrude-arrow");
        const project = screenProjector(frame,width,height);
        holeHandle.toggleAttribute("hidden",!holeOpen());
        if (holeOpen()) {
            const [x,y] = project(worldPoint(activeFeature.request.Point));
            holeHandle.setAttribute("cx",x);
            holeHandle.setAttribute("cy",y);
        }
        if (!extrusionPreview) {
            arrow.removeAttribute("d");
            return;
        }
        const a = project(extrusionPreview.origin), b = project(extrusionPreview.end);
        const [x,y] = subtract(b,a), length = Math.hypot(x,y);
        arrow.classList.toggle("cut",extrusionPreview.cut);
        if (length < 2) {
            arrow.setAttribute("d",`M${b[0]-4},${b[1]} a4,4 0 1 0 8,0 a4,4 0 1 0 -8,0`);
            return;
        }
        const u = x/length, v = y/length;
        const tip = [a[0]+Math.max(25,length+10)*u,a[1]+Math.max(25,length+10)*v];
        arrow.setAttribute("d",`M${a} L${tip} M${tip[0]-12*u-5*v},${tip[1]-12*v+5*u} L${tip} L${tip[0]-12*u+5*v},${tip[1]-12*v-5*u}`);
    }
    byId("build-extrude-distance").oninput = updateExtrudePreview;
    byId("build-extrude-operation").onchange = updateExtrudePreview;
    function updateExtrudeTarget() {
        const source = buildExtrudeSource();
        const target = source ? source.part.Name + (sketchProfiles(source.part).length > 1 ?
            ` · Profile ${source.profileIndex + 1}` : "") : "";
        byId("extrude-part").setAttribute("aria-pressed", !extrudePanel.hidden);
        byId("extrude-target").textContent = target || "Select a closed drawing or a planar face.";
        byId("apply-extrude").disabled = !target;
        updateExtrudePreview();
    }
    function closeExtrude() {
        extrudePanel.hidden = true;
        extrudeFace = null;
        updateExtrudeTarget();
    }
    function faceExtrusionSource(face, part) {
        const edges = new Map();
        const key = ({ X,Y }) => `${X},${Y}`;
        for (const [start, end] of face.Guide) {
            const id = key(start);
            if (edges.has(id)) throw new Error("Choose a face with one closed boundary.");
            edges.set(id, [start, end]);
        }
        const profile = [];
        const first = edges.keys().next().value;
        let next = first;
        while (edges.has(next)) {
            const [start, end] = edges.get(next);
            profile.push(start);
            edges.delete(next);
            next = key(end);
        }
        if (next !== first || edges.size || profile.length < 3)
            throw new Error("Choose a face with one closed boundary.");
        return { ...face.Center, Kind: "Sketch", Name: part.Name + " · planar face",
            Width: 0, Depth: 0, Height: 0, Profile: profile, FaceFrame: face.Frame, SketchId: crypto.randomUUID() };
    }
    function extendRegularBody(body, source, amount) {
        if (!(amount >= 0.01 && amount <= 10000) || body.Bevel || body.BevelCuts?.length ||
            body.Holes?.length || body.Threads?.length) return null;
        const origin = modelPoint(source,[0,0,0]);
        const normal = subtract(modelPoint(source,[0,0,1]),origin);
        if (body.Kind === "Box") {
            const axis = normal.findIndex(value => Math.abs(value) > 0.999999);
            if (axis < 0 || source.Profile.length !== 4) return null;
            const dimension = ["Width","Depth","Height"][axis], coordinate = ["X","Y","Z"][axis];
            if (body[dimension]+amount > 10000) return null;
            const shift = axis === 2 ? Math.min(0,normal[axis])*amount : normal[axis]*amount/2;
            return { ...body, [dimension]: body[dimension]+amount, [coordinate]: body[coordinate]+shift };
        }
        if (body.Kind === "Extrusion" || body.Kind === "Cylinder") {
            const axis = subtract(modelPoint(body,[0,0,1]),modelPoint(body,[0,0,0]));
            const direction = dot(normal,axis);
            if (Math.abs(direction) < 0.999999 || body.Height+amount > 10000) return null;
            const [X,Y,Z] = modelPoint(body,[0,0,Math.min(0,direction)*amount]);
            return { ...body, X,Y,Z, Height: body.Height+amount, SketchId: direction < 0 ? undefined : body.SketchId,
                SketchProfileIndex: direction < 0 ? undefined : body.SketchProfileIndex };
        }
        return null;
    }
    function screenProjector(frame, width, height) {
        const length = viewportScale(width,height) / (2*modelRadius);
        return point => {
            const delta = subtract(point,frame.target);
            return [width/2 + length*dot(frame.rows[0],delta), height/2 - length*dot(frame.rows[1],delta)];
        };
    }
    function worldFaceEdges(face) {
        const part = { ...face.Center, FaceFrame: face.Frame };
        return face.Guide.map(edge => edge.map(({ X,Y }) => modelPoint(part,[X,Y,0])));
    }
    function pickDetail(face, x, y, bounds) {
        const edges = worldFaceEdges(face);
        const project = screenProjector(cameraFrame(), bounds.width, bounds.height);
        const pointer = [x,y];
        const pointDistance = point => Math.hypot(...subtract(project(point),pointer));
        const key = point => point.join(",");
        const starts = new Map(edges.map(edge => [key(edge[0]),edge]));
        const ends = new Map(edges.map(edge => [key(edge[1]),edge]));
        function smooth(first, second) {
            if (!first || !second) return false;
            const a = subtract(first[1],first[0]), b = subtract(second[1],second[0]);
            return dot(a,b) > 0.94*Math.hypot(...a)*Math.hypot(...b);
        }
        const corner = edges.map(edge => edge[0]).filter(point => !smooth(ends.get(key(point)),starts.get(key(point))))
            .filter(point => pointDistance(point) <= 8).sort((a,b) => pointDistance(a)-pointDistance(b))[0];
        if (corner) return { face, kind: "Corner", point: corner };
        const edgeDistance = ([a,b]) => {
            const [u,v] = [project(a),project(b)];
            const d = subtract(v,u), p = subtract(pointer,u);
            const length = d[0]**2+d[1]**2;
            const t = length ? Math.max(0,Math.min(1,(p[0]*d[0]+p[1]*d[1])/length)) : 0;
            return Math.hypot(p[0]-t*d[0],p[1]-t*d[1]);
        };
        const edge = edges.filter(item => edgeDistance(item) <= 7).sort((a,b) => edgeDistance(a)-edgeDistance(b))[0];
        if (!edge) return { face, kind: "Face", edges };
        const chain = new Set([edge]);
        for (let next = starts.get(key(edge[1])), previous = edge; smooth(previous,next) && !chain.has(next);
            previous = next, next = starts.get(key(next[1]))) chain.add(next);
        for (let previous = ends.get(key(edge[0])), next = edge; smooth(previous,next) && !chain.has(previous);
            next = previous, previous = ends.get(key(previous[0]))) chain.add(previous);
        return { face, kind: "Edge", edges: [...chain] };
    }
    function drawDetails(frame,width,height) {
        const overlay = byId("detail-overlay");
        overlay.replaceChildren();
        const targets = byId("plane-targets");
        targets.replaceChildren();
        if (choosingSketchPlane) {
            const project = screenProjector(frame,width,height);
            const extent = modelRadius * plateDepth * 0.23;
            for (const plane of ["XY","XZ","YZ"]) {
                const part = { Plane: plane, X:0, Y:0, Z:0 };
                const polygon = document.createElementNS(svgNamespace,"polygon");
                const points = [[0.23,0.23],[1,0.23],[1,1],[0.23,1]];
                polygon.setAttribute("points",points.map(([x,y]) => project(modelPoint(part,[x*extent,y*extent,0])).join(",")).join(" "));
                polygon.setAttribute("class",plane);
                polygon.onclick = () => beginSketch(plane);
                targets.append(polygon);
            }
        }
        if (!editor.hidden || !selectedDetail) return;
        const project = screenProjector(frame,width,height);
        const { kind, point, edges } = selectedDetail;
        if (point) {
            const dot = document.createElementNS(svgNamespace,"circle");
            const [x,y] = project(point);
            for (const [name,value] of Object.entries({cx:x,cy:y,r:5})) dot.setAttribute(name,value);
            overlay.append(dot);
            return;
        }
        const path = document.createElementNS(svgNamespace,"path");
        if (kind === "Edge") {
            path.setAttribute("class","edge");
            path.setAttribute("d",edges.map(([a,b]) => `M${project(a)} L${project(b)}`).join(" "));
        } else {
            const ring = new Map(edges.map(([a,b]) => [a.join(","),[a,b]]));
            let data = "";
            while (ring.size) {
                let key = ring.keys().next().value;
                data += `M${project(ring.get(key)[0])}`;
                while (ring.has(key)) {
                    const end = ring.get(key)[1];
                    ring.delete(key);
                    data += ` L${project(end)}`;
                    key = end.join(",");
                }
                data += " Z ";
            }
            path.setAttribute("d",data);
        }
        overlay.append(path);
    }
    byId("extrude-part").onclick = () => {
        if (holeOpen()) featureDialog.close();
        if (!extrudePanel.hidden) { closeExtrude(); return; }
        pickingFace = false;
        extrudePanel.hidden = false;
        byId("extrude-error").textContent = "";
        if (selectedDetail?.kind === "Face") {
            try { extrudeFace = faceExtrusionSource(selectedDetail.face, project.Parts[selected]); }
            catch (error) { byId("extrude-error").textContent = error.message; }
        }
        refreshParts();
        if (canExtrude(project.Parts[selected])) byId("build-extrude-distance").select();
    };
    byId("cancel-extrude").onclick = closeExtrude;
    extrudePanel.onsubmit = event => {
        event.preventDefault();
        const errorText = byId("extrude-error");
        errorText.textContent = "";
        try {
            const distance = byId("build-extrude-distance").valueAsNumber;
            const operation = byId("build-extrude-operation").value.toLowerCase();
            const target = buildExtrudeSource();
            if (!target) throw new Error("Select a closed drawing or a planar face.");
            const { part: source, profileIndex } = target;
            const initial = [...project.Parts];
            const index = extrudeFace ? initial.push(source) - 1 : selected;
            const bodyIndex = extrudeFace && operation === "add" ? selected : -1;
            const extended = bodyIndex >= 0 ? extendRegularBody(initial[bodyIndex],source,distance) : null;
            if (extended) initial[bodyIndex] = extended;
            let parts = extended ? initial : extrudeSketch(initial, index, distance, operation, bodyIndex, profileIndex);
            if (extrudeFace) {
                parts = parts.filter(part => !(part.Kind === "Sketch" && part.SketchId === source.SketchId))
                    .map(part => part.SketchId === source.SketchId ? { ...part, SketchId: undefined } : part);
            }
            if (!changeParts(parts, bodyIndex >= 0 ? bodyIndex : operation === "cut" ? parts.indexOf(source) : parts.length - 1)) {
                errorText.textContent = status.textContent;
                return;
            }
            closeExtrude();
            fit();
            status.textContent = `Extruded ${distance} mm · ${operation}`;
        } catch (error) { errorText.textContent = error.message; }
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
    const sketchSurface = byId("sketch-surface");
    function sketchFrame() {
        if (project.Parts[editing]) return project.Parts[editing];
        if (faceSketch) return { ...faceSketch.Center, FaceFrame: faceSketch.Frame };
        const part = { Plane: byId("plane-choice").value, X: 0, Y: 0, Z: 0 };
        const [X,Y,Z] = modelPoint(part, [0,0,byId("plane-offset").valueAsNumber || 0]);
        return { ...part, X, Y, Z };
    }
    byId("plane-choice").onchange = fit;
    byId("plane-offset").oninput = redraw;
    const outline = document.querySelector("#outline");
    const preview = byId("sketch-preview");
    const vertices = document.querySelector("#vertices");
    const points = [];
    const redoPoints = [];
    const drawings = [];
    let activeDrawing = -1;
    const drawingCenters = byId("drawing-centers");
    let drawingDrag = null, drawingMove = null;
    let rules = [];
    const rulePicker = byId("constraint-rule");
    const vertexPicker = byId("constraint-vertex");
    rulePicker.onchange = () => {
        const rule = rulePicker.value;
        rulePicker.value = "";
        if (!rule) return;
        if (rule === "clear") { rules = []; drawingMove = null; drawSketch(); return; }
        const Vertex = Number(vertexPicker.value), Kind = Number(rule);
        if (rules.some(item => item.Vertex === Vertex && item.Kind === Kind)) return;
        try {
            const next = [...rules, { Vertex, Kind }];
            const solved = JSON.parse(geometryEngine.constrain(JSON.stringify({
                Points: points.map(([X,Y]) => ({ X,Y })), Rules: next, Preferred: Vertex
            })));
            points.splice(0, points.length, ...solved.map(({X,Y}) => [X,Y]));
            drawingMove = null;
            redoPoints.length = 0;
            rules = next;
            drawSketch();
            status.textContent = "Constraint applied. Clear constraints to change the outline.";
        } catch (error) { status.textContent = error.message; }
    };
    const shape = document.querySelector("#shape");
    const shapeTools = [...document.querySelectorAll("[data-shape]")];
    for (const button of shapeTools) button.onclick = () => {
        if (shape.value !== button.dataset.shape) startDrawing(button.dataset.shape);
    };
    const twoPointShape = () => ["Rectangle", "Circle", "Triangle"].includes(shape.value);
    const requiresBothAxes = () => ["Rectangle", "Triangle"].includes(shape.value);
    const undoPoint = document.querySelector("#undo-point");
    const drawingChoice = byId("sketch-drawing-choice");
    function currentDrawing() {
        const open = shape.value === "Open path";
        const outline = open ? points : shapeProfile(shape.value, points);
        if (open && (outline.length < 2 || outline.some((point,i) => i &&
            point.every((value, axis) => value === outline[i-1][axis]))))
            throw new Error("Place at least two distinct consecutive points for the open path.");
        if (!open) extrudeProfile(outline,1);
        return { open, points: points.slice(), outline: outline.map(([X,Y]) => ({X,Y})), shape: shape.value, rules };
    }
    function allDrawings() {
        const result = [...drawings];
        if (activeDrawing >= 0) result[activeDrawing] = currentDrawing();
        else if (points.length) result.push(currentDrawing());
        if (result.length > 64) throw new Error("A sketch can contain up to 64 drawings.");
        return result;
    }
    function activateDrawing(index, tool = shape.value) {
        drawingMove = null;
        activeDrawing = index;
        const drawing = drawings[index];
        shape.value = drawing?.shape ?? tool;
        rules = drawing?.rules ?? [];
        points.splice(0,points.length,...(drawing?.points ?? []));
        redoPoints.length = 0;
        drawSketch();
    }
    function switchDrawing(index, tool) {
        try {
            const next = allDrawings();
            drawings.splice(0,drawings.length,...next);
            activateDrawing(index,tool);
            sketchHint();
            return true;
        } catch (error) {
            drawingChoice.value = String(activeDrawing);
            status.textContent = error.message;
            return false;
        }
    }
    function startDrawing(tool = shape.value) {
        return switchDrawing(-1,tool);
    }
    byId("add-sketch-drawing").onclick = () => startDrawing();
    drawingChoice.onchange = () => switchDrawing(Number(drawingChoice.value));
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
    function addDrawingCenter(profile, open, index) {
        if (profile.length < (open ? 2 : 3)) return;
        let x = 0, y = 0, weight = 0;
        for (let i = 0; i < profile.length - (open ? 1 : 0); i++) {
            const [a,b] = profile[i], [c,d] = profile[(i+1)%profile.length];
            const w = open ? Math.hypot(c-a,d-b) : a*d-c*b;
            x += (a+c)*w;
            y += (b+d)*w;
            weight += w;
        }
        if (Math.abs(weight) < 1e-9) return;
        const divisor = (open ? 2 : 3)*weight;
        const marker = document.createElementNS("http://www.w3.org/2000/svg","circle");
        marker.dataset.drawing = index;
        marker.setAttribute("cx",110+x/divisor);
        marker.setAttribute("cy",110-y/divisor);
        marker.setAttribute("r",3.4);
        const title = document.createElementNS("http://www.w3.org/2000/svg","title");
        title.textContent = "Hold to move drawing";
        marker.append(title);
        drawingCenters.append(marker);
    }
    function drawingPoints(index) {
        return index === activeDrawing ? points : drawings[index].points;
    }
    function setDrawingPoints(index, next) {
        if (index === activeDrawing) {
            points.splice(0,points.length,...next);
        } else {
            const drawing = drawings[index];
            const [dx,dy] = subtract(next[0],drawing.points[0]);
            drawings[index] = { ...drawing, points: next,
                outline: drawing.outline.map(({X,Y}) => ({ X:X+dx,Y:Y+dy })) };
        }
    }
    drawingCenters.addEventListener("pointerdown",event => {
        const marker = event.target.closest("[data-drawing]");
        if (!marker || !event.isPrimary || event.button !== 0 || calibration) return;
        event.stopPropagation();
        event.preventDefault();
        const index = Number(marker.dataset.drawing);
        const constraints = index === activeDrawing ? rules : drawings[index].rules;
        if (constraints.some(rule => Number(rule.Kind) === 2)) {
            status.textContent = "Clear the drawing's origin constraint to move it.";
            return;
        }
        drawingDrag = { id:event.pointerId, index, readyAt:performance.now()+250,
            start:[event.clientX,event.clientY], position:sketchPoint(event), before:drawingPoints(index).slice(), moving:false };
        drawingCenters.setPointerCapture(event.pointerId);
    });
    drawingCenters.addEventListener("pointermove",event => {
        if (event.pointerId !== drawingDrag?.id) return;
        event.stopPropagation();
        if (!(event.buttons & 1)) return;
        if (!drawingDrag.moving && performance.now() < drawingDrag.readyAt) {
            if (Math.hypot(event.clientX-drawingDrag.start[0],event.clientY-drawingDrag.start[1]) > 7)
                drawingDrag = null;
            return;
        }
        drawingDrag.moving = true;
        const [dx,dy] = subtract(sketchPoint(event),drawingDrag.position);
        setDrawingPoints(drawingDrag.index,drawingDrag.before.map(([x,y]) => [x+dx,y+dy]));
        drawSketch();
    });
    drawingCenters.addEventListener("pointerup",event => {
        if (calibration) return;
        event.stopPropagation();
        if (event.pointerId !== drawingDrag?.id) return;
        const { index,before,moving } = drawingDrag;
        if (moving) {
            drawingMove = { index,before,after:drawingPoints(index).slice(),undone:false };
            redoPoints.length = 0;
        }
        drawingDrag = null;
        drawSketch();
    });
    for (const event of ["pointercancel","lostpointercapture"]) drawingCenters.addEventListener(event,() => {
        if (drawingDrag?.moving) {
            setDrawingPoints(drawingDrag.index,drawingDrag.before);
            drawSketch();
        }
        drawingDrag = null;
    });
    function drawSketch() {
        preview.removeAttribute("points");
        const fixedShape = twoPointShape();
        const profile = fixedShape && points.length === 2 ? shapeProfile(shape.value, points) : points;
        const open = shape.value === "Open path";
        outline.setAttribute("points", svgPoints(open ? [] : profile));
        if (open) preview.setAttribute("points", svgPoints(profile));
        const drawingGroup = byId("sketch-drawings");
        drawingGroup.replaceChildren();
        drawingCenters.replaceChildren();
        drawings.forEach((drawing,index) => {
            if (index === activeDrawing) return;
            const element = document.createElementNS("http://www.w3.org/2000/svg", drawing.open ? "polyline" : "polygon");
            element.setAttribute("points",svgPoints(drawing.outline.map(({X,Y}) => [X,Y])));
            drawingGroup.append(element);
            addDrawingCenter(drawing.outline.map(({X,Y}) => [X,Y]),drawing.open,index);
        });
        addDrawingCenter(profile,open,activeDrawing);
        drawingChoice.replaceChildren(...drawings.map((drawing,index) =>
            new Option(`${index + 1}. ${drawing.shape}`,String(index))), new Option("New drawing","-1"));
        drawingChoice.value = String(activeDrawing);
        byId("add-sketch-drawing").disabled = !points.length;
        vertices.replaceChildren();
        for (const [x, y] of points) {
            const dot = document.createElementNS("http://www.w3.org/2000/svg", "circle");
            dot.setAttribute("cx", x + 110);
            dot.setAttribute("cy", 110 - y);
            dot.setAttribute("r", 1.4);
            vertices.append(dot);
        }
        undoPoint.disabled = !(drawingMove && !drawingMove.undone) && (!points.length || !!rules.length);
        byId("redo-point").disabled = !drawingMove?.undone && (!redoPoints.length || !!rules.length);
        shape.disabled = !!rules.length;
        for (const button of shapeTools) {
            button.setAttribute("aria-pressed", button.dataset.shape === shape.value);
        }
        const vertex = vertexPicker.value;
        vertexPicker.replaceChildren(...points.map(([x,y], i) => new Option(`${i + 1}   ${x}, ${y}`, String(i))));
        if (vertex && Number(vertex) < points.length) vertexPicker.value = vertex;
        rulePicker.disabled = !geometryEngine || shape.value !== "Polygon" || points.length < 3;
        for (const id of ["move-vertex", "remove-vertex"]) byId(id).disabled = !points.length || !!rules.length;
        byId("add-vertex").disabled = !!rules.length;
    }
    function showSketch(active) {
        if (holeOpen()) featureDialog.close();
        chooseSketchPlane(false);
        ++referenceSequence;
        calibration = null;
        if (active) {
            if (editor.hidden) buildView = [yaw,pitch,roll,zoom,cameraPan];
            reference = project.Parts[editing]?.ReferenceImage ?? null;
            drawReference();
            const guide = faceSketch?.Guide ?? [];
            byId("face-guide").setAttribute("d", guide.map(([a,b]) =>
                `M ${110+a.X} ${110-a.Y} L ${110+b.X} ${110-b.Y}`).join(" "));
        }
        editor.hidden = !active;
        if (active) fit();
        else if (buildView) {
            [yaw,pitch,roll,zoom,cameraPan] = buildView;
            buildView = null;
        }
        closeExtrude();
        byId("build-tools").hidden = active;
        byId("sketch-tools").hidden = !active;
        byId("vertices-panel").hidden = !active;
        byId("parts-panel").hidden = active;
        byId("volume-label").hidden = active;
        for (const detail of byId("sketch-tools").querySelectorAll("details")) detail.open = false;
        document.querySelector("#start-sketch").disabled = active;
        byId("mode").textContent = active ? "Sketch" : "Build Mode";
        byId("primitive").disabled = active;
        modeling.disabled = active || !geometryEngine;
        refreshParts();
        if (!active) redraw();
    }
    function chooseSketchPlane(active) {
        choosingSketchPlane = active;
        byId("plane-selector").hidden = !active;
        byId("start-sketch").setAttribute("aria-pressed",active);
        redraw();
    }
    function beginSketch(sketchPlane = "XY", face = null) {
        ++loadSequence;
        editing = -1;
        rules = [];
        shape.value = "Polygon";
        faceSketch = face;
        pickingFace = false;
        byId("plane-choice").disabled = byId("plane-offset").disabled = !!face;
        byId("plane-choice").value = sketchPlane;
        byId("plane-offset").value = 0;
        drawings.length = 0;
        drawingMove = null;
        activeDrawing = -1;
        points.length = redoPoints.length = 0;
        drawSketch();
        showSketch(true);
        if (face) status.textContent = "Sketch origin is at the face center.";
        else sketchHint();
    }
    byId("start-sketch").onclick = () => {
        if (selectedDetail?.kind === "Face") {
            beginSketch("XY",selectedDetail.face);
            return;
        }
        closeExtrude();
        pickingFace = !choosingSketchPlane;
        chooseSketchPlane(pickingFace);
        refreshParts();
        status.textContent = pickingFace ? "Select an origin plane or click a planar face." : "Plane selection canceled.";
    };
    for (const button of document.querySelectorAll("[data-plane]"))
        button.onclick = () => beginSketch(button.dataset.plane);
    byId("cancel-plane").onclick = () => {
        pickingFace = false;
        chooseSketchPlane(false);
        refreshParts();
        status.textContent = "Plane selection canceled.";
    };
    byId("edit-sketch").onclick = () => {
        ++loadSequence;
        editing = selected;
        faceSketch = null;
        pickingFace = false;
        const part = project.Parts[selected];
        byId("plane-choice").value = part.Plane ?? "XY";
        byId("plane-offset").value = part.Plane === "XZ" ? -part.Y : part.Plane === "YZ" ? part.X : part.Z;
        byId("plane-choice").disabled = byId("plane-offset").disabled = true;
        const closed = [part.Profile,...(part.AdditionalProfiles ?? [])].filter(Boolean);
        const paths = part.OpenPaths ?? [];
        drawings.splice(0,drawings.length,...[...closed,...paths].map((outline,index) => ({
            open: index >= closed.length, outline, points: outline.map(({X,Y}) => [X,Y]),
            shape: index < closed.length ? "Polygon" : "Open path", rules: part.Constraints?.[index] ?? []
        })));
        activateDrawing(0);
        showSketch(true);
        status.textContent = "Undo points or append vertices, then finish the sketch.";
    };
    function sketchHint() {
        status.textContent = {
            Polygon: "Tap the outline points in order. Extrude closes the outline.",
            Rectangle: "Tap two opposite corners of the rectangle.",
            Triangle: "Tap opposite corners of the triangle's bounding box.",
            Circle: "Tap the center, then a point on the circle.",
            "Open path": "Place path points, then press Enter to end the path."
        }[shape.value];
    }
    const svgPoints = vertices => vertices.map(([x,y]) => `${x + 110},${110 - y}`).join(" ");
    function sketchPoint(event) {
        const point = new DOMPoint(event.clientX, event.clientY).matrixTransform(sketchSurface.getScreenCTM().inverse());
        return [Math.round(point.x) - 110, 110 - Math.round(point.y)];
    }
    plane.addEventListener("pointermove", event => {
        if (!event.isPrimary || pointers.get(event.pointerId)?.orbit || !points.length || rules.length || calibration) return;
        const next = sketchPoint(event), fixed = twoPointShape();
        if (fixed && points.length !== 1) return;
        let draft;
        if (fixed) {
            const matchedAxes = next.filter((value, i) => value === points[0][i]).length;
            if (matchedAxes >= (requiresBothAxes() ? 1 : 2)) {
                preview.removeAttribute("points");
                return;
            }
            draft = shapeProfile(shape.value, [points[0], next]);
            draft.push(draft[0]);
        } else {
            draft = shape.value === "Open path" ? [...points, next] : [points.at(-1), next];
        }
        preview.setAttribute("points", svgPoints(draft));
    });
    plane.addEventListener("pointerleave", drawSketch);
    plane.addEventListener("pointerup", event => {
        if (!event.isPrimary || event.button !== 0 || pointers.get(event.pointerId)?.moved) return;
        const point = new DOMPoint(event.clientX, event.clientY).matrixTransform(sketchSurface.getScreenCTM().inverse());
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
        addSketchPoint(sketchPoint(event));
    });
    function addSketchPoint(next) {
        if (next.some(value => Math.abs(value) > 110)) return;
        const last = points.at(-1);
        if (last && next.every((value, i) => value === last[i])) return;
        if (requiresBothAxes() && points.length === 1 && next.some((value, i) => value === last[i])) return;
        if (twoPointShape() && points.length === 2 && !startDrawing()) return;
        if (drawings.length >= 64 && activeDrawing < 0) {
            status.textContent = "A sketch can contain up to 64 drawings.";
            return;
        }
        redoPoints.length = 0;
        drawingMove = null;
        points.push(next);
        drawSketch();
    }
    const vertexInput = () => ["vertex-x", "vertex-y"].map(id => byId(id).valueAsNumber);
    vertexPicker.onchange = () => {
        const point = points[vertexPicker.selectedIndex];
        if (point) [byId("vertex-x").value, byId("vertex-y").value] = point;
    };
    byId("add-vertex").onclick = () => {
        const next = vertexInput();
        if (points.length < 256 && next.every(Number.isFinite)) addSketchPoint(next);
    };
    byId("move-vertex").onclick = () => {
        const i = vertexPicker.selectedIndex, next = vertexInput();
        if (i < 0 || !next.every(value => Number.isFinite(value) && Math.abs(value) <= 110)) return;
        if (twoPointShape() && points.length === 2 &&
            next.filter((value, axis) => value === points[1-i][axis]).length >= (requiresBothAxes() ? 1 : 2)) return;
        points[i] = next;
        drawingMove = null;
        redoPoints.length = 0;
        drawSketch();
    };
    byId("remove-vertex").onclick = () => {
        if (vertexPicker.selectedIndex < 0) return;
        points.splice(vertexPicker.selectedIndex, 1);
        drawingMove = null;
        redoPoints.length = 0;
        drawSketch();
    };
    byId("redo-point").onclick = () => {
        if (drawingMove?.undone) {
            setDrawingPoints(drawingMove.index,drawingMove.after);
            drawingMove.undone = false;
        } else if (redoPoints.length && !rules.length) points.push(redoPoints.pop());
        drawSketch();
    };
    undoPoint.addEventListener("click", () => {
        if (drawingMove && !drawingMove.undone) {
            setDrawingPoints(drawingMove.index,drawingMove.before);
            drawingMove.undone = true;
            drawSketch();
            return;
        }
        if (!points.length || rules.length) return;
        drawingMove = null;
        redoPoints.push(points.pop());
        drawSketch();
    });
    document.addEventListener("keydown", event => {
        if (event.defaultPrevented || event.target.closest?.("input, textarea, select, [contenteditable]")) return;
        const key = event.key.toLowerCase(), command = event.ctrlKey || event.metaKey;
        let action;
        if (command && (key === "z" || key === "y")) {
            const forward = key === "y" || event.shiftKey;
            action = () => byId(editor.hidden ? (forward ? "redo" : "undo") :
                (forward ? "redo-point" : "undo-point")).click();
        } else if (editor.hidden) {
            if (key === "escape" && holeOpen()) action = () => featureDialog.close();
            if (key === "escape" && choosingSketchPlane) action = () => byId("cancel-plane").click();
            if (key === "escape" && !extrudePanel.hidden) action = closeExtrude;
            if (command && key === "e") action = () => byId("extrude-part").click();
            if (command && key === "b") action = () => byId("bevel").click();
            if (key === "backspace" || key === "delete") action = () => byId("remove-part").click();
        } else if (key === "enter" && !event.target.closest?.("button")) {
            action = shape.value === "Open path" && points.length ? () => startDrawing() :
                () => byId("finish-sketch").click();
        } else if (key === "escape") {
            action = () => byId("cancel-sketch").click();
        }
        if (!action) return;
        event.preventDefault();
        action();
    });
    document.querySelector("#cancel-sketch").addEventListener("click", () => {
        showSketch(false);
        status.textContent = "Sketch canceled.";
    });
    byId("finish-sketch").onclick = () => finishSketch(false);
    byId("extrude").onclick = () => finishSketch(true);
    function finishSketch(extrude) {
        try {
            const height = document.querySelector("#height").valueAsNumber;
            const drawingsToSave = allDrawings();
            if (!drawingsToSave.length) throw new Error("Draw at least one profile or open path.");
            const closed = drawingsToSave.filter(drawing => !drawing.open);
            const paths = drawingsToSave.filter(drawing => drawing.open);
            if (extrude && !closed.length) throw new Error("Extrude requires a closed profile.");
            const profiles = closed.map(drawing => drawing.outline);
            const constraints = [...closed,...paths].map(drawing => drawing.rules.length ? drawing.rules : null);
            const plane = byId("plane-choice").value;
            const offset = byId("plane-offset").valueAsNumber;
            if (editing < 0 && (!Number.isFinite(offset) || Math.abs(offset) > 10000))
                throw new Error("Enter a plane offset between −10,000 and 10,000 mm.");
            const [X,Y,Z] = editing < 0 ? modelPoint({ Plane: plane, X: 0, Y: 0, Z: 0 }, [0,0,offset]) : [0,0,0];
            const position = { X, Y, Z, Plane: plane, Width: 0, Depth: 0 };
            if (faceSketch) Object.assign(position, faceSketch.Center, { FaceFrame: faceSketch.Frame });
            const list = [...project.Parts];
            const previous = list[editing];
            const link = previous?.SketchId ?? crypto.randomUUID();
            const index = editing < 0 ? list.length : editing;
            list[index] = { ...position, Kind: "Sketch", Name: `Sketch ${index + 1}`, Height: 0,
                ...previous, Profile: profiles[0] ?? null, AdditionalProfiles: profiles.length > 1 ? profiles.slice(1) : undefined,
                OpenPaths: paths.length ? paths.map(drawing => drawing.outline) : undefined,
                SketchId: link, Hidden: previous?.Hidden || false, ReferenceImage: reference ?? undefined,
                Constraints: constraints.some(Boolean) ? constraints : undefined };
            if (previous?.SketchId) {
                for (let i = 0; i < list.length; i++) {
                    const item = list[i];
                    if (item.Kind !== "Extrusion" || item.SketchId !== link) continue;
                    list[i] = { ...item, Profile: profiles[item.SketchProfileIndex ?? 0] };
                }
            }
            const operation = byId("sketch-extrude-operation").value.toLowerCase();
            const bodyIndex = faceSketch?.Owner && operation === "add" ? list.indexOf(faceSketch.Owner) : -1;
            const parts = extrude ? extrudeSketch(list, index, height, operation, bodyIndex) : list;
            if (!changeParts(parts, extrude && bodyIndex >= 0 ? bodyIndex :
                !extrude || operation === "cut" ? parts.indexOf(list[index]) : parts.length - 1,
                reference ? 22 : constraints.some(Boolean) ? 21 : paths.length ? 17 : profiles.length > 1 ? 14 : 11)) return;
            showSketch(false);
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
    byId("fit").onclick = fit;
    const cube = byId("view-cube");
    const svgNamespace = "http://www.w3.org/2000/svg";
    const cubeFaces = [
        ["Front", 1, -1, 0, 0, "#3b495e"], ["Back", 1, 1, Math.PI, 0, "#3b495e"],
        ["Right", 0, 1, -Math.PI/2, 0, "#2d394b"], ["Left", 0, -1, Math.PI/2, 0, "#2d394b"],
        ["Top", 2, 1, 0, Math.PI/2, "#4e5e76"], ["Bottom", 2, -1, 0, -Math.PI/2, "#3e4d63"]
    ].map(([name, axis, sign, horizontal, vertical, color]) => {
        const group = document.createElementNS(svgNamespace, "g");
        group.setAttribute("role", "button");
        group.setAttribute("aria-label", name + " view");
        const polygon = document.createElementNS(svgNamespace, "polygon");
        polygon.setAttribute("fill", color);
        const label = document.createElementNS(svgNamespace, "text");
        label.textContent = name.toUpperCase();
        group.append(polygon, label);
        cube.append(group);
        const select = () => {
            yaw = horizontal;
            pitch = !editor.hidden && vertical === 0 ? 0.035 : vertical;
            roll = 0;
            redraw();
        };
        group.addEventListener("keydown", event => {
            if (!["Enter", " "].includes(event.key)) return;
            event.preventDefault();
            select();
        });
        return { group, polygon, label, axis, sign, select };
    });
    function drawViewCube() {
        const [right,up,depth] = cameraRows();
        const project = point => [44 + 21*dot(right,point), 44 - 21*dot(up,point)];
        for (const { group, polygon, label, axis, sign } of cubeFaces) {
            const facing = -depth[axis] * sign;
            group.style.display = facing > 0.001 ? "" : "none";
            group.setAttribute("tabindex", facing > 0.001 ? "0" : "-1");
            if (facing <= 0.001) continue;
            const vertex = (a, b) => {
                const point = [a,b];
                point.splice(axis, 0, sign);
                return project(point);
            };
            polygon.setAttribute("points", [[-1,-1],[1,-1],[1,1],[-1,1]].map(pair => vertex(...pair).join(",")).join(" "));
            const [x,y] = vertex(0,0);
            label.setAttribute("x", x);
            label.setAttribute("y", y);
            label.style.display = facing >= 0.36 ? "" : "none";
        }
    }
    let cubeDrag = null;
    cube.addEventListener("pointerdown", event => {
        if (!event.isPrimary || event.button !== 0) return;
        const face = cubeFaces.find(item => item.group.contains(event.target));
        cubeDrag = { id: event.pointerId, x: event.clientX, y: event.clientY, face, moved: false };
        cube.setPointerCapture(event.pointerId);
        event.preventDefault();
    });
    cube.addEventListener("pointermove", event => {
        if (cubeDrag?.id !== event.pointerId) return;
        const dx = event.clientX - cubeDrag.x, dy = event.clientY - cubeDrag.y;
        if (!cubeDrag.moved && Math.hypot(dx,dy) < 4) return;
        Object.assign(cubeDrag, { x: event.clientX, y: event.clientY, moved: true });
        orbit(dx, dy);
    });
    cube.addEventListener("pointerup", event => {
        if (cubeDrag?.id === event.pointerId && !cubeDrag.moved) cubeDrag.face?.select();
    });
    cube.addEventListener("lostpointercapture", () => { cubeDrag = null; });
    byId("toggle-parts").onclick = event => {
        const collapsed = byId("parts-panel").classList.toggle("collapsed");
        event.currentTarget.textContent = collapsed ? "›" : "‹";
        event.currentTarget.setAttribute("aria-expanded", !collapsed);
        event.currentTarget.setAttribute("aria-label", collapsed ? "Expand Parts" : "Collapse Parts");
    };
    byId("new-project").onclick = event => {
        if (project.Parts.length && !confirm("Start a new project? Save any changes you want to keep first.")) return;
        ++loadSequence;
        loadScene(emptyProject());
        selected = editing = -1;
        undo.length = redo.length = 0;
        pickingFace = false;
        showSketch(false);
        fit();
        event.currentTarget.closest("details").open = false;
        status.textContent = "New project.";
    };
    byId("build-volume").onclick = event => {
        const { X, Y, Z } = project.Bed ?? emptyProject().Bed;
        const answer = prompt("Build volume X, Y, Z (mm)", `${X}, ${Y}, ${Z}`);
        if (answer === null) return;
        const dimensions = answer.split(",").map(Number);
        if (dimensions.length !== 3 || !dimensions.every(value => value >= 1 && value <= 10000)) {
            status.textContent = "Enter three dimensions between 1 and 10,000 mm.";
            return;
        }
        const [x,y,z] = dimensions;
        changeParts(project.Parts, selected, 11, { X: x, Y: y, Z: z });
        event.currentTarget.closest("details").open = false;
    };
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
            upload(result.triangles, result.sketchTriangles, result.ranges, result.drawings);
            project = opened ?? { ...emptyProject(), Parts: [{ Kind: "Mesh", Name: file.name, Width: 0, Depth: 0, Height: 0, X: 0, Y: 0, Z: 0,
                Mesh: { Triangles: result.triangles.map(([a,b,c]) => {
                    const point = ([X,Y,Z]) => ({X,Y,Z});
                    return { A: point(a), B: point(b), C: point(c) };
                }) } }] };
            selected = -1;
            undo.length = redo.length = 0;
            uploadPlate();
            pickingFace = false;
            showSketch(false);
            status.textContent = `${file.name} · ${result.triangles.length.toLocaleString()} triangles · Drag to orbit · Pinch or scroll to zoom` +
                (result.skipped ? ` · ${result.skipped} visible non-mesh items omitted` : "");
            fit();
        } catch (error) {
            if (sequence === loadSequence) status.textContent = error.message;
        }
    });
    byId("face-sketch").onclick = () => {
        closeExtrude();
        chooseSketchPlane(false);
        pickingFace = !pickingFace;
        refreshParts();
        status.textContent = pickingFace ? "Click or tap a planar face to start a sketch." : "Face selection canceled.";
    };
    canvas.addEventListener("click", event => {
        if (!tapStart || Math.hypot(event.clientX-tapStart[0], event.clientY-tapStart[1]) > 5) return;
        tapStart = null;
        try {
            const bounds = canvas.getBoundingClientRect();
            const fit = viewportScale(bounds.width, bounds.height);
            const x = (2 * (event.clientX - bounds.left) - bounds.width) / fit;
            const y = (bounds.height - 2 * (event.clientY - bounds.top)) / fit;
            const frame = cameraFrame();
            const [right,up,direction] = frame.rows;
            if (!pickingFace) {
                extrudeFace = selectedDetail = null;
                byId("extrude-error").textContent = "";
                const projectPoint = point => {
                    const relative = subtract(point,frame.target);
                    return frame.rows.map(row => dot(row,relative) / modelRadius);
                };
                let nearest = Infinity;
                let nearestTriangle = -1;
                selected = -1;
                selectedDrawing = -1;
                for (const [index, [start, end]] of partRanges.entries()) {
                    for (let i = start; i < end; i++) {
                        const [a,b,c] = sceneTriangles[i].map(projectPoint);
                        const ux = b[0]-a[0], uy = b[1]-a[1], vx = c[0]-a[0], vy = c[1]-a[1];
                        const denominator = ux*vy - uy*vx;
                        if (Math.abs(denominator) < 1e-12) continue;
                        const u = ((x-a[0])*vy - (y-a[1])*vx) / denominator;
                        const v = (ux*(y-a[1]) - uy*(x-a[0])) / denominator;
                        if (u < 0 || v < 0 || u+v > 1) continue;
                        const depth = a[2] + u*(b[2]-a[2]) + v*(c[2]-a[2]);
                        if (depth >= nearest) continue;
                        nearest = depth;
                        nearestTriangle = i;
                        selected = index;
                    }
                }
                if (selected >= 0) {
                    selectedDrawing = drawingRanges[selected]?.findIndex(([start,end]) => nearestTriangle >= start && nearestTriangle < end) ?? -1;
                }
                refreshParts();
                if (selected < 0 || project.Parts[selected].Kind === "Sketch") return;
            }
            const origin = frame.target.map((value, i) => value + modelRadius *
                (right[i]*x + up[i]*y - (frame.depth+1)*direction[i]));
            const vector = ([X,Y,Z]) => ({ X,Y,Z });
            if (!geometryEngine) {
                if (!extrudePanel.hidden || pickingFace) throw new Error("Face picking requires the advanced geometry engine.");
                return;
            }
            let face = null;
            const targets = pickingFace ? project.Parts : [project.Parts[selected]];
            for (const part of targets) {
                if (part.Hidden || part.Kind === "Sketch") continue;
                const hit = JSON.parse(geometryEngine.pickFace(JSON.stringify({
                    Part: part, Origin: vector(origin), Direction: vector(direction)
                })));
                if (hit && (!face || hit.Distance < face.Distance)) face = { ...hit, Owner: part };
            }
            if (!pickingFace) {
                if (extrudePanel.hidden) {
                    if (face) {
                        selectedDetail = pickDetail(face,event.clientX-bounds.left,event.clientY-bounds.top,bounds);
                        status.textContent = `${selectedDetail.kind} selected · ${project.Parts[selected].Name}`;
                        redraw();
                    }
                    return;
                }
                if (!face) throw new Error("Select a planar face or a closed sketch.");
                extrudeFace = faceExtrusionSource(face, project.Parts[selected]);
                updateExtrudeTarget();
                return;
            }
            if (!face) { status.textContent = "Choose a visible planar face."; return; }
            beginSketch("XY",face);
        } catch (error) {
            (extrudePanel.hidden ? status : byId("extrude-error")).textContent = error.message;
        }
    });
    function orbit(dx, dy) {
        yaw += dx * 0.012;
        pitch = Math.max(-Math.PI / 2, Math.min(Math.PI / 2, pitch + dy * 0.012));
        if (!editor.hidden && Math.abs(pitch) < 0.035) pitch = (Math.sign(pitch) || 1) * 0.035;
        redraw();
    }
    function pan(dx,dy,from = cameraPan) {
        const scale = viewportScale(canvas.clientWidth,canvas.clientHeight) / (2 * modelRadius);
        if (!scale) return;
        const [right,up] = cameraFrame().rows;
        cameraPan = from.map((value,i) => value + (-dx*right[i] + dy*up[i]) / scale);
        redraw();
    }
    function touchPair() {
        const touches = [...pointers.values()].filter(pointer => pointer.touch);
        if (touches.length !== 2) return null;
        const [a,b] = touches.map(pointer => pointer.last);
        const [dx,dy] = subtract(b,a);
        return { center: a.map((value,i) => (value+b[i])/2), span: Math.hypot(dx,dy), angle: Math.atan2(dy,dx) };
    }
    const scrollPreference = byId("viewport-scroll");
    try {
        const preference = localStorage.getItem("layercad.viewportScroll");
        if (["pan","zoom"].includes(preference)) scrollPreference.value = preference;
    } catch { /* Browser storage is optional. */ }
    scrollPreference.onchange = () => {
        try { localStorage.setItem("layercad.viewportScroll",scrollPreference.value); }
        catch { /* The current session can still use the setting. */ }
    };
    function handleViewportScroll(event) {
        event.preventDefault();
        const mode = event.deltaMode;
        const unit = mode === 1 ? 16 : mode === 2 ? canvas.clientHeight : 1;
        const x = event.deltaX * unit, y = event.deltaY * unit;
        // Browsers report trackpad scrolling and mouse wheel ticks through the same event.
        const fineScroll = mode === 0 && (x !== 0 || Math.abs(y) < 40 || !Number.isInteger(y));
        if (!event.ctrlKey && (scrollPreference.value === "pan" || scrollPreference.value === "auto" && fineScroll)) {
            pan(-x,-y);
        } else if (y) {
            scaleBy(Math.exp(-y * 0.001));
        }
    }
    for (const surface of [canvas, plane]) {
        surface.addEventListener("pointerdown", event => {
            if (![0,1].includes(event.button)) return;
            surface.setPointerCapture(event.pointerId);
            const orbit = event.button === 1 || event.pointerType === "touch";
            if (event.button === 1) event.preventDefault();
            const start = [event.clientX,event.clientY];
            pointers.set(event.pointerId, { start, last: start, orbit, moved: false,
                touch: event.pointerType === "touch", middle: event.button === 1 });
            tapStart = pointers.size === 1 && event.button === 0 ? start : null;
            if (pointers.size > 1) for (const pointer of pointers.values()) pointer.moved = pointer.multi = true;
            const pair = touchPair();
            touchGesture = pair ? { ...pair, roll, zoom, pan: cameraPan } : null;
        });
        surface.addEventListener("pointermove", event => {
            const pointer = pointers.get(event.pointerId);
            if (!pointer) return;
            const next = [event.clientX,event.clientY];
            const delta = next.map((coordinate,i) => coordinate - pointer.last[i]);
            pointer.last = next;
            if (Math.hypot(...next.map((coordinate,i) => coordinate - pointer.start[i])) > 5 && pointer.orbit) {
                pointer.moved = true;
                tapStart = null;
            }
            if (pointers.size === 1 && pointer.orbit && !pointer.multi && (pointer.moved || !pointer.touch)) {
                if (pointer.middle && event.ctrlKey) pan(...delta);
                else orbit(...delta);
            } else if (pointer.touch && touchGesture) {
                const pair = touchPair();
                if (!pair) return;
                zoom = Math.max(0.15,Math.min(20,touchGesture.zoom * (touchGesture.span ? pair.span/touchGesture.span : 1)));
                const twist = touchGesture.span >= 8 && pair.span >= 8 ? pair.angle-touchGesture.angle : 0;
                const c = Math.cos(twist), s = Math.sin(twist);
                roll = (touchGesture.roll + twist) % (2*Math.PI);
                const bounds = canvas.getBoundingClientRect();
                const center = [bounds.left+bounds.width/2,bounds.top+bounds.height/2];
                const ratio = zoom / touchGesture.zoom;
                const [x,y] = subtract(touchGesture.center,center);
                const rotated = [c*x-s*y,s*x+c*y];
                const shift = pair.center.map((value,i) => value-center[i]-ratio*rotated[i]);
                pan(...shift,touchGesture.pan);
            }
        });
        for (const type of ["pointerup", "pointercancel", "lostpointercapture"])
            surface.addEventListener(type, event => {
                if (pointers.delete(event.pointerId)) touchGesture = null;
            });
    }
    byId("viewport").addEventListener("wheel", event => {
        if (event.target.closest("#view, #sketch-plane, #plane-targets")) handleViewportScroll(event);
    }, { passive: false });
    document.addEventListener("contextmenu", event => {
        if (event.target.closest("#drawing-centers, #hole-handle, .popup-title") ||
            event.pointerType === "touch" && event.target.closest("#view, #sketch-plane"))
            event.preventDefault();
    });
    new ResizeObserver(redraw).observe(canvas);
    uploadPlate();
    refreshParts();
}
