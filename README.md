# LayerCAD

LayerCAD is a Windows CAD prototype for dimensioned 3D-printing parts. It uses C#, WPF, and .NET 8. The source is available under the MIT license.

## Use LayerCAD

Use **File** for **New**, **Open**, **Save**, and **Import STL**. Import accepts binary and ASCII STL files, treats coordinates as millimeters, and places the mesh at the center of the build plate. Imported meshes are saved inside the project.

Open the gear in the top-right corner for **Settings**. Set your printer's X, Y, and Z build volume or switch the display and input units between millimeters and inches. The unit preference applies across projects. Each project stores its own build volume. STL import and export use millimeters.

1. Choose **Create Sketch**, draw a closed outline, then select **Finish Sketch**. Select **Extrude** in Build Mode, enter the depth, and apply it to create a solid.
2. Click a part in the viewport or select it in **Parts** to open its properties beside it. **Objects** and **Sketches** each have a collapsible group. Use the eye beside an item to hide or show it, and the arrow beside **Parts** to collapse or reopen the panel. Enter dimensions and positions in your preferred units and select **Apply**. Positions are relative to the center of the build plate. Select **Drop to bed** to rest the lowest point on the plate.
3. Set your printer's dimensions under **Settings**. Orange parts extend outside the build volume.
4. Save a `.layercad` project to keep editing later. Select a part and choose **Export selected STL** to open its mesh in your slicer. Each export contains one part centered in X and Y with its base at Z zero. Import STL dimensions as millimeters.

Middle-drag to orbit, hold Ctrl and middle-drag to pan, or scroll to zoom. These gestures work in both Build and Sketch modes. Press **F6** with viewport focus to fit visible geometry, or press **1** for window selection and **2** for lasso. In Build Mode, **I** toggles Measure. In Sketch Mode, **R**, **C**, and **O** open Rectangle, Circle, and Offset. Click a face, edge, or corner of the view cube to change the camera angle. Right-click the cube or focus it and press Enter for the six standard views and an isometric view. Round surfaces are tessellated into 96 segments.

## Build Mode tools

The **Build Mode** ribbon sits below the File menu. **Create** starts with Create Sketch, followed by Extrude, Revolve, Hole, and Thread. **Modify** puts Bevel and Move in the icon row. Click a group label to open its menu.

Use **Construct** to start a sketch offset from an origin plane or a selected planar face. **Inspect** measures between two points and shows the distance and XYZ differences in the status bar. **Insert** opens the STL importer.

Open **Primitives** for **Box**, **Cylinder**, **Sphere**, and **Torus**. Enter dimensions in the active units to place a body on the build plate without a sketch.

Open **Create** for sketch-based modeling tools and **Modify** for Pattern and Mirror.

| Tool | Inputs |
| --- | --- |
| Revolve | One closed profile on one side of a horizontal or vertical axis through its sketch origin. Enter an angle up to 360°. |
| Sweep | One closed profile and one open sketch polyline. The profile follows the path with mitered corners. |
| Loft | Two or more closed profiles on separate parallel planes, including profiles with inward corners. |
| Rib and Web | One open path for a rib, or intersecting open paths for a joined web. Enter wall thickness and height. |
| Pipe | An open sketch polyline, outside diameter, and wall thickness. |
| Coil | Centerline diameter, wire diameter, pitch, and turns for a round-wire helix. |
| Pattern | Repeat selected bodies along a linear step or around a vertical axis. Circular Hole Pattern repeats an existing hole around a center on its face, including its recess and modeled thread when present. |
| Mirror | One or more selected bodies and an XY, XZ, or YZ plane with an optional offset. |

Sphere, Torus, the new sketch-based solids, Coil, Mirror, and circular body copies produce mesh bodies. Box and Cylinder retain editable dimensions. Linear body copies retain their source geometry.

- **Extrude** starts from a closed sketch profile or a supported solid face. **Add** creates a new solid from a sketch or extends the selected face. **Cut** removes material from every visible solid along the extrusion path. **Merge** combines the extrusion and the solids it meets into one body. The preview uses green for added material and red for removed material. Use **Reverse direction** to point a sketch extrusion into a solid behind the sketch. Boolean results are stored as mesh bodies, and one undo restores the original solids.
- Drag the extrusion arrow in **1 mm** or **0.1 in** increments, depending on display units. Type an exact distance in the field.
- **Move** translates the current object or selection. Drag the object to move it along X and Y at its current height, drag a colored arrow to move along one axis, or enter exact X, Y, and Z offsets. Select **Apply** to commit the preview.
- **Hole** previews the bore outline and center as you hover over a face. Click the desired location or enable **Center on face**. Choose **Straight**, **Counterbore** with a recess depth, or **Countersink** with an included angle. Set a blind depth or choose **Through all**. Straight bores on perpendicular faces of a rectangular solid can meet to form one passage. Recessed holes require clearance from neighboring bores and the stock edges. Use **Thread** on the cylindrical shaft to model a threaded hole.
- **Thread** cuts a printable helical groove into a round extrusion or bore. Click a curved wall or a hole opening and select a metric coarse preset or enter a custom pitch and depth. The groove follows the existing cylinder diameter and the panel reports the resulting diameters. For nominal metric dimensions, use an outside diameter equal to the preset size or a starting bore at or below its internal minor diameter. Choose the length, handedness, and starting end. The preview supports up to 64 turns per thread. [ISO 261](https://www.iso.org/standard/4165.html), [ISO 724](https://www.iso.org/standard/85104.html)
- **Bevel** works on edges, corners, and face perimeters of rectangular solids and convex polyline extrusions, and on the end rims of circular solids. An existing hole is preserved when it clears the bevel. Use **Centered** for equal flat setbacks, or clear it to enter an angle. Flat bevels can extend past half the thickness when the cut leaves valid solid geometry. Enable **Rounded** to enter a radius, or **Continue around face** to pick the full perimeter from one edge on a rectangular or convex polyline solid.

## Sketch and extrude

Select **Create Sketch**, the first button on the Build Mode toolbar. The **XY**, **XZ**, and **YZ** origin planes appear through solid objects and can be selected directly. Click a flat face to sketch on it, using **Alt-click** where an origin plane overlaps. The click sets the origin on the face, and its boundary appears as a dashed guide. The sketch opens facing the selected plane. Both viewports use the same perspective camera, middle-drag orbit, wheel zoom, view cube, and **Reset view**. **Face sketch plane** returns to the straight-on drawing view.

The sketch workspace shows **Polyline**, **Circle**, **Rectangle**, **Triangle**, and **Hexagon** across the top. Click the grid to snap to 1 mm or 0.05 in, depending on the selected units, or enter coordinates along the two sketch axes. Use **Rectangle** for both rectangles and squares. Enter equal width and height to make a square. For a circle, equilateral triangle, or regular hexagon, place the center and a point on the perimeter. The polyline tool connects the last vertex to the first.

Click a shape icon in the **Drawing** tab to activate its default tool. Hover over it or click its arrow to see alternatives, including an orthogonal polyline, a circle defined by two diameter endpoints, an ellipse, a centered rectangle, a diamond, a right triangle, a pentagon, and an octagon.

Left-drag to start at the press point, or click each point separately. The preview follows the pointer after release until you click the canvas or press **Enter**. Use **Tab** to cycle through the dimension fields and type to lock a value. Click a dimension number in the face-on view to edit it on the canvas. With Polyline, click the first vertex to close the profile, or press **Enter**, **Esc**, or **End polyline** to end it. A polyline that crosses a closed outline splits it into separately extrudable profiles.

**Shift-click** a vertex to set an alignment reference. Nearby drawing points snap to the horizontal or vertical guides through that vertex. **Shift-click** it again to clear the guides.

Press **L** to activate Line. The Rectangle menu includes a **Three-point rectangle** and **Center-to-center slot**. Circle defaults to **Center and diameter**, with **Center and radius**, **Two-point diameter**, and **Three-point circle** available from its menu. The **Arc** button creates an open path through the specified start, end, and third point.

Press **T** or click **Trim**, hover over the segment to preview the removal, then click. Intersections bound the removed portion. A line with no intersections trims to its endpoints. Press **Esc** to leave Trim. Hover over a circle in Sketch Mode to highlight its full circumference.

Choose **Move** in the sketch toolbar to translate all profiles along the sketch axes. Each sketch can hold multiple closed profiles, and the profile dropdown lets you choose which vertices to edit.

Select **Finish Sketch** to save the profiles. In Build Mode, click a profile and choose **Extrude** to use that profile. You can also switch profiles by clicking the sketch during the preview or using the profile dropdown. Each extrusion creates a separate solid. Right-click a visible profile in a multi-profile sketch to move or delete it, then finish the sketch to commit the edit. Newly drawn circles and ellipses and modeled bores use 192 segments.

Dimension arrows show the outline's overall spans in the selected display units. The center dot marks the area centroid of a closed outline and updates as you edit it. The blue origin marker stays at the sketch plane's zero coordinates.

Select a solid and choose **Edit sketch** to edit the source vertices and rebuild the extrusion. Each profile can be concave and must form a closed loop with distinct corners. In Build Mode, hover over a flat face to preview the selection, or click a face, edge, or corner to select it. With a flat face selected, choose **Create Sketch** to open the sketch directly on that face. Right-click an object in the viewport or its Parts row to access **Move** and **Delete**.

In Build Mode, Ctrl-click faces or sketches in the viewport to toggle them. Ctrl-click a row in **Parts** to toggle a whole item. Left-drag from empty viewport space to box-select, or choose **Lasso** for freehand selection. Hold **Ctrl** or **Shift** to extend an area selection. In Sketch Mode, choose **Select** for box selection or **Lasso** for freehand selection. Ctrl-click toggles drawings and Ctrl+A selects all in either selection mode. Active drawing tools snap to lines and points. Press **Esc** to return to Select. Middle-drag orbits the camera in both modes.

Use **Undo** and **Redo** at the right of the toolbar to step through project changes or edits in the open sketch. Right-click a drawing in the sketch editor to move or delete that profile. The build plate stays visible as a reference during sketch editing. **Enter** confirms the active selection or preview. **Delete** and **Backspace** cancel the current tool outside text fields.

Use **Ctrl+A** to select visible items and planar faces in Build Mode, or all completed drawings in the open sketch. Use **Ctrl+C** to copy, **Ctrl+V** to paste, and **Ctrl+Z** to undo.

In Build Mode, use **Ctrl+E** for Extrude and **Ctrl+B** for Bevel.

Use **Sketch → Modify → Profile Dimensions** to save the dimensions of a closed profile. Choose **Center on sketch origin** to keep the outline centered. **Follow profile size** moves enclosed sketch circles proportionally as the outline is resized and preserves their diameters. Finish the sketch to rebuild linked extrusions.

In Sketch Mode, choose a drawing and select a vertex in **Vertices**. Use **Modify → Horizontal Segment** or **Vertical Segment** for the segment that starts at that vertex, or **Coincident with Origin** to pin the vertex to sketch coordinates zero. **Move selected** maintains these relationships. Use **Clear Vertex Constraints** to remove the constraints from the drawing.

## Build from source

Install the .NET 8 SDK on Windows, then run these commands from the project directory.

```powershell
dotnet run --project LayerCAD.csproj
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeAllContentForSelfExtract=true -p:DebugType=None
```

The second command produces a standalone x64 executable under `bin/Release/net8.0-windows/win-x64/publish/`.

For a release shared between computers, increment `Version` in `LayerCAD.csproj` and distribute the same published executable to both machines. The Settings window displays the installed build version.

## GitHub updates

The portable Windows x64 executable checks GitHub Releases at startup. Use **File → Check for Updates** to check again. When you approve an update, LayerCAD verifies the release asset's SHA-256 digest, prompts for unsaved project changes, replaces the executable, and relaunches. The previous executable remains alongside it as `.previous`.

Increment `Version` in `LayerCAD.csproj` and push to `main` to publish a new Windows release automatically. The workflow also accepts explicit version tags. It skips versions that already have a release.

The Windows x64 solid geometry library is built from [Manifold 3.5.4](https://github.com/elalish/manifold/releases/tag/v3.5.4). To rebuild it, pass the path to the extracted source and the path to Zig `0.14.0-dev.2456` to `Native/Build-Manifold.ps1` using `-ManifoldSource` and `-Zig`.

## Current scope

The editor supports sketches on the three standard planes and planar faces, solid extrusions, and mesh-based extrusion cuts and merges. Sketch constraints, analytic curves, assemblies, and slicing are outside the current implementation.
# Cross-platform viewer preview

The `Web/` folder contains a browser-based LayerCAD viewer intended for Android, iOS, and macOS. Serve the folder with a static web server and open its address in the device browser. It needs no backend and reads models locally on the device.

Choose **Open model** to load a binary or ASCII STL, or a `.layercad` project containing visible mesh bodies. Use one finger to orbit, two fingers to pinch to zoom, or the mouse wheel on a Mac. For Windows sketches and parametric solids, export STL from LayerCAD first. Sketch editing and solid modeling are Windows features in this preview.
