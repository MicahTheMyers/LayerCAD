import { dotnet } from "./_framework/dotnet.js";

const runtime = await dotnet.create();
const exports = await runtime.getAssemblyExports(runtime.getConfig().mainAssemblyName);
export const buildPart = exports.LayerCAD.BrowserGeometry.BuildPart;
export const modelPart = exports.LayerCAD.BrowserGeometry.Model;
export const pickFace = exports.LayerCAD.BrowserGeometry.PickFace;
export const constrain = exports.LayerCAD.BrowserGeometry.Constrain;
export const bevel = exports.LayerCAD.BrowserGeometry.Bevel;
export const hole = exports.LayerCAD.BrowserGeometry.Hole;
export const bevelPreview = exports.LayerCAD.BrowserGeometry.BevelPreview;
export const holePreview = exports.LayerCAD.BrowserGeometry.HolePreview;
export const thread = exports.LayerCAD.BrowserGeometry.Thread;
