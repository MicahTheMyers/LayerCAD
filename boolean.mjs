import Module from "./vendor/manifold/manifold.js";

const runtime = await Module();
runtime.setup();
const { Manifold, Mesh } = runtime;

function toSolid(triangles) {
    const coordinates = [], indices = [], vertices = new Map();
    for (const triangle of triangles) {
        const face = triangle.map(point => {
            const key = point.map(value => Math.round(value * 1e9)).join(",");
            if (!vertices.has(key)) {
                vertices.set(key, coordinates.length / 3);
                coordinates.push(...point);
            }
            return vertices.get(key);
        });
        if (new Set(face).size === 3) indices.push(...face);
    }
    const origin = coordinates.slice(0, 3);
    const point = i => origin.map((value, axis) => coordinates[i * 3 + axis] - value);
    let volume = 0;
    for (let i = 0; i < indices.length; i += 3) {
        const [x,y,z] = point(indices[i]), [a,b,c] = point(indices[i+1]), [d,e,f] = point(indices[i+2]);
        volume += x*(b*f-c*e) + y*(c*d-a*f) + z*(a*e-b*d);
    }
    if (volume < 0)
        for (let i = 0; i < indices.length; i += 3)
            [indices[i+1], indices[i+2]] = [indices[i+2], indices[i+1]];
    return new Manifold(new Mesh({
        numProp: 3, vertProperties: Float32Array.from(coordinates), triVerts: Uint32Array.from(indices)
    }));
}

function read(solid) {
    const mesh = solid.getMesh();
    return Array.from({ length: mesh.triVerts.length / 3 }, (_, i) => {
        const point = j => {
            const start = mesh.triVerts[i * 3 + j] * mesh.numProp;
            const [X,Y,Z] = mesh.vertProperties.subarray(start, start + 3);
            return { X,Y,Z };
        };
        return { A: point(0), B: point(1), C: point(2) };
    });
}

export function combine(stock, tool, operation) {
    if (!["add", "subtract", "intersect"].includes(operation)) throw new Error("Choose Join, Cut, or Intersect.");
    let first, second, result;
    try {
        first = toSolid(stock);
        second = toSolid(tool);
        result = first[operation](second);
        return read(result);
    } finally {
        result?.delete();
        second?.delete();
        first?.delete();
    }
}

export function split(stock, axis, offset) {
    let source, pieces;
    try {
        source = toSolid(stock);
        pieces = source.splitByPlane(axis, offset);
        return pieces.map(read);
    } finally {
        for (const piece of pieces ?? []) piece.delete();
        source?.delete();
    }
}

export function applyExtrusion(stock, tool, operation) {
    if (operation !== "cut" && operation !== "merge") throw new Error("Choose Cut or Merge.");
    let first, second, overlap, result;
    const components = solid => {
        const pieces = solid.decompose();
        try { return pieces.length; }
        finally { for (const piece of pieces) piece.delete(); }
    };
    try {
        first = toSolid(stock);
        second = toSolid(tool);
        if (operation === "cut") {
            overlap = first.intersect(second);
            if (overlap.isEmpty() || overlap.volume() <= 0) return null;
            result = first.subtract(second);
        } else {
            result = first.add(second);
            if (components(result) >= components(first) + components(second)) return null;
        }
        return read(result);
    } finally {
        result?.delete();
        overlap?.delete();
        second?.delete();
        first?.delete();
    }
}
