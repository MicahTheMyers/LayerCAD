import { dotnet } from "./_framework/dotnet.js";

const runtime = await dotnet.create();
const exports = await runtime.getAssemblyExports(runtime.getConfig().mainAssemblyName);
export const buildPart = exports.LayerCAD.BrowserGeometry.BuildPart;
export const modelPart = exports.LayerCAD.BrowserGeometry.Model;
export const pickFace = exports.LayerCAD.BrowserGeometry.PickFace;
export const constrain = exports.LayerCAD.BrowserGeometry.Constrain;
