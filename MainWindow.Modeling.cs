using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media.Media3D;

namespace LayerCAD;

public partial class MainWindow
{
    private sealed record ModelProfile(Part Sketch, SketchPoint[] Points, string Label)
    {
        public override string ToString() => Label;
    }

    private void OpenModelingMenu(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        if (button.ContextMenu is not { } menu)
            return;
        menu.PlacementTarget = button;
        menu.Placement = PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void OpenModelingTool(object sender, RoutedEventArgs e)
    {
        string tool = (string)((FrameworkElement)sender).Tag;
        EndBuildFeature();
        EndSurfaceSelection();
        CancelLasso();
        var dialog = new ModelingDialog(this, tool, units);
        if (tool == "Hole Pattern")
        {
            OpenHolePattern(dialog);
            return;
        }
        var sketches = parts.Where(part => part.Kind == PartKind.Sketch).ToArray();
        var profiles = sketches.SelectMany(sketch => sketch.SketchProfiles.Select((profile, index) =>
            new ModelProfile(sketch, profile, $"{sketch.Name} · Profile {index + 1}"))).ToArray();
        var paths = sketches.SelectMany(sketch => (sketch.OpenPaths ?? []).Select((path, index) =>
            new ModelProfile(sketch, path, $"{sketch.Name} · Path {index + 1}"))).ToArray();
        var bodies = (lassoSelection.Count > 0 ? parts.Where(lassoSelection.Contains) : Selected is { } part ? [part] : [])
            .Where(part => !part.Hidden && part.Kind != PartKind.Sketch).ToArray();
        int selectedProfile = Array.FindIndex(profiles, profile => ReferenceEquals(profile.Sketch, Selected) &&
            ReferenceEquals(profile.Points, profile.Sketch.SketchProfiles.ElementAt(SelectedSketchProfile(profile.Sketch))));
        int selectedPath = Array.FindIndex(paths, path => ReferenceEquals(path.Sketch, Selected));

        var create = tool switch
        {
            "Box" or "Cylinder" or "Sphere" or "Torus" => PrimitiveTool(dialog, tool),
            "Revolve" when profiles.Length > 0 => RevolveTool(dialog, profiles, selectedProfile),
            "Sweep" when profiles.Length > 0 && paths.Length > 0 => SweepTool(dialog, profiles, paths, selectedProfile, selectedPath),
            "Loft" when profiles.Length >= 2 => LoftTool(dialog, profiles, paths),
            "Pipe" when paths.Length > 0 => PipeTool(dialog, paths, selectedPath),
            "Coil" => CoilTool(dialog),
            "Rib" when paths.Length > 0 => RibTool(dialog, paths, selectedPath),
            "Web" when paths.Length > 0 => WebTool(dialog, paths),
            "Pattern" when bodies.Length > 0 => PatternTool(dialog, bodies),
            "Circular Pattern" when bodies.Length > 0 => CircularPatternTool(dialog, bodies),
            "Mirror" when bodies.Length > 0 => MirrorTool(dialog, bodies),
            _ => null
        };
        if (create is null)
        {
            SetStatus(tool switch
            {
                "Revolve" => "Create a closed sketch profile to revolve.",
                "Sweep" => "Create a closed profile and an open sketch path for Sweep.",
                "Loft" => "Create closed profiles on at least two parallel sketch planes for Loft.",
                "Rib" or "Web" or "Pipe" => $"Create an open sketch polyline for {tool}.",
                _ => $"Select one or more solid bodies for {tool}."
            });
            return;
        }
        if (dialog.Run(create) is not { } added)
            return;
        parts.AddRange(added);
        MarkDirty();
        RefreshParts(parts.Count - added.Length);
        if (added.Length > 1)
            SetLassoSelection(added, extend: false);
        SetStatus(added.Length == 1 ? $"Added {added[0].Name}." : $"{tool} added {added.Length} bodies.");
    }

    private Func<Part[]> PrimitiveTool(ModelingDialog dialog, string tool)
    {
        if (tool == "Box")
        {
            var width = dialog.Length("Width", 30);
            var depth = dialog.Length("Depth", 30);
            var height = dialog.Length("Height", 20);
            return () => [new(PartKind.Box, NextPartName(PartKind.Box), width(), depth(), height(), 0, 0, 0)];
        }
        if (tool == "Torus")
        {
            var center = dialog.Length("Centerline diameter", 30);
            var tube = dialog.Length("Tube diameter", 6);
            return () =>
            {
                double radius = center() / 2, thickness = tube() / 2;
                Require(radius > thickness, "The centerline diameter must exceed the tube diameter.");
                return [MeshBody(NextModeledName(tool), ModelingMeshes.Torus(radius, thickness), restOnBed: true)];
            };
        }
        var diameter = dialog.Length("Diameter", 25);
        if (tool == "Cylinder")
        {
            var height = dialog.Length("Height", 20);
            return () => [new(PartKind.Cylinder, NextPartName(PartKind.Cylinder), diameter(), 0, height(), 0, 0, 0)];
        }
        return () => [MeshBody(NextModeledName(tool), ModelingMeshes.Sphere(diameter() / 2), restOnBed: true)];
    }

    private Func<Part[]> RevolveTool(ModelingDialog dialog, ModelProfile[] profiles, int index)
    {
        dialog.Hint("The axis passes through the sketch origin. Keep the profile on one side of the axis.");
        var profile = dialog.Choice("Profile", profiles, index);
        var axis = dialog.Choice("Sketch axis", new[] { "Horizontal", "Vertical" }, 1);
        var angle = dialog.Number("Angle (°)", 360, 0.1, 360);
        return () =>
        {
            var selected = profile();
            return [MeshBody(NextModeledName("Revolve"),
                ModelingMeshes.Revolve(selected.Sketch, selected.Points, axis() == "Horizontal", angle()))];
        };
    }

    private Func<Part[]> SweepTool(ModelingDialog dialog, ModelProfile[] profiles, ModelProfile[] paths, int profileIndex, int pathIndex)
    {
        dialog.Hint("Sweep centers the profile on the open path and turns it at each corner.");
        var profile = dialog.Choice("Profile", profiles, profileIndex);
        var path = dialog.Choice("Path", paths, pathIndex);
        return () =>
        {
            var selected = path();
            return [MeshBody(NextModeledName("Sweep"),
                ModelingMeshes.Sweep(selected.Sketch, selected.Points, profile().Points, centerProfile: true))];
        };
    }

    private Func<Part[]> LoftTool(ModelingDialog dialog, ModelProfile[] profiles, ModelProfile[] paths)
    {
        dialog.Hint("Ctrl-click two or more closed profiles on separate parallel planes.");
        var chosen = dialog.MultiChoice("Profiles", profiles);
        if (paths.Length > 0)
            dialog.Hint("A guide is optional. It must meet a vertex on each profile.");
        var guide = paths.Length == 0 ? null : dialog.Choice("Guide curve",
            new[] { "None" }.Concat(paths.Select(path => path.Label)).ToArray());
        return () => [MeshBody(NextModeledName("Loft"),
            ModelingMeshes.Loft(chosen().Select(profile => (profile.Sketch, profile.Points)).ToArray(),
                guide?.Invoke() is { } label && label != "None" && paths.FirstOrDefault(path => path.Label == label) is { } rail
                    ? (rail.Sketch, rail.Points) : null))];
    }

    private Func<Part[]> PipeTool(ModelingDialog dialog, ModelProfile[] paths, int index)
    {
        var path = dialog.Choice("Path", paths, index);
        var diameter = dialog.Length("Outside diameter", 8);
        var thickness = dialog.Length("Wall thickness", 1);
        return () =>
        {
            var selected = path();
            double radius = diameter() / 2, wall = thickness();
            Require(wall < radius, "The wall thickness must be less than the outside radius.");
            return [MeshBody(NextModeledName("Pipe"), ModelingMeshes.Pipe(selected.Sketch, selected.Points, radius, wall))];
        };
    }

    private Func<Part[]> CoilTool(ModelingDialog dialog)
    {
        var center = dialog.Length("Centerline diameter", 20);
        var wire = dialog.Length("Wire diameter", 2);
        var pitch = dialog.Length("Pitch", 5);
        var turns = dialog.Number("Turns", 4, 0.25, 32);
        return () =>
        {
            double radius = center() / 2, thickness = wire() / 2, spacing = pitch();
            Require(radius > thickness && spacing > 2 * thickness, "Use a centerline diameter and pitch larger than the wire diameter.");
            return [MeshBody(NextModeledName("Coil"), ModelingMeshes.Coil(radius, thickness, spacing, turns()), restOnBed: true)];
        };
    }

    private Func<Part[]> RibTool(ModelingDialog dialog, ModelProfile[] paths, int index)
    {
        dialog.Hint("Build a wall along an open path. Height extends along the sketch normal.");
        var path = dialog.Choice("Path", paths, index);
        var width = dialog.Length("Thickness", 2);
        var height = dialog.Length("Height", 8);
        return () => [MeshBody(NextModeledName("Rib"), Wall(path(), width(), height()))];
    }

    private Func<Part[]> WebTool(ModelingDialog dialog, ModelProfile[] paths)
    {
        dialog.Hint("Ctrl-click intersecting open paths to join their walls into one reinforcing body.");
        var selected = dialog.MultiChoice("Paths", paths);
        var width = dialog.Length("Thickness", 2);
        var height = dialog.Length("Height", 8);
        return () =>
        {
            var chosen = selected();
            Require(chosen.Length >= 2, "Choose at least two intersecting paths.");
            double thickness = width(), rise = height();
            var walls = chosen.Select(path => Wall(path, thickness, rise)).OfType<List<Triangle>>().ToArray();
            Require(walls.Length == chosen.Length, "One of the paths cannot form a wall.");
            return [MeshBody(NextModeledName("Web"), ModelingMeshes.Join(walls))];
        };
    }

    private static List<Triangle>? Wall(ModelProfile path, double thickness, double height)
    {
        double half = thickness / 2;
        return ModelingMeshes.Sweep(path.Sketch, path.Points,
            [new(-half, 0), new(half, 0), new(half, height), new(-half, height)], centerProfile: false);
    }

    private Func<Part[]> PatternTool(ModelingDialog dialog, Part[] bodies)
    {
        dialog.Hint("Copies the selected bodies in a straight line. The original counts as the first instance.");
        var count = dialog.Number("Instances", 3, 2, 64, whole: true);
        var x = dialog.Length("X step", 35, signed: true);
        var y = dialog.Length("Y step", 0, signed: true);
        var z = dialog.Length("Z step", 0, signed: true);
        return () =>
        {
            var step = new Vector3D(x(), y(), z());
            Require(step.LengthSquared > 0, "Enter a step along at least one axis.");
            int copies = (int)count() - 1;
            Require(copies * bodies.Length <= 512, "A pattern can create up to 512 bodies at a time.");
            string name = NextModeledName("Pattern");
            return Enumerable.Range(1, copies).SelectMany(instance => bodies.Select((body, i) => body with
            {
                Name = $"{name} · {instance}.{i + 1}",
                X = body.X + step.X * instance, Y = body.Y + step.Y * instance, Z = body.Z + step.Z * instance,
                SketchId = null, SketchProfileIndex = 0
            })).ToArray();
        };
    }

    private Func<Part[]> MirrorTool(ModelingDialog dialog, Part[] bodies)
    {
        var axis = dialog.Choice("Plane", new[] { "YZ", "XZ", "XY" });
        var offset = dialog.Length("Plane offset", 0, signed: true);
        return () =>
        {
            string plane = axis();
            double distance = offset();
            string name = NextModeledName("Mirror");
            return bodies.Select((body, i) => MeshBody($"{name} · {i + 1}", Geometry.Build(body).Select(triangle =>
                new Triangle(Reflect(triangle.A), Reflect(triangle.C), Reflect(triangle.B))), validateSolid: false)).ToArray();

            Point3D Reflect(Point3D p) => plane switch
            {
                "YZ" => new(2 * distance - p.X, p.Y, p.Z),
                "XZ" => new(p.X, 2 * distance - p.Y, p.Z),
                _ => new(p.X, p.Y, 2 * distance - p.Z)
            };
        };
    }

    private Func<Part[]> CircularPatternTool(ModelingDialog dialog, Part[] bodies)
    {
        dialog.Hint("Copies the selected bodies around a vertical axis. The original is the first instance.");
        var count = dialog.Number("Instances", 6, 2, 64, whole: true);
        var angle = dialog.Number("Total angle (°)", 360, 0.1, 360);
        var x = dialog.Length("Axis X", 0, signed: true);
        var y = dialog.Length("Axis Y", 0, signed: true);
        return () =>
        {
            var center = new Point3D(x(), y(), 0);
            int instances = (int)count();
            Require((instances - 1) * bodies.Length <= 512, "A pattern can create up to 512 bodies at a time.");
            var inputs = bodies.Select(body => Geometry.Build(body)).ToArray();
            string name = NextModeledName("Circular Pattern");
            return PatternAngles(instances, angle()).SelectMany((degrees, instance) =>
            {
                var rotation = new RotateTransform3D(new AxisAngleRotation3D(new Vector3D(0, 0, 1), degrees), center);
                return inputs.Select((mesh, i) => MeshBody($"{name} · {instance + 1}.{i + 1}",
                    mesh.Select(triangle => new Triangle(rotation.Transform(triangle.A), rotation.Transform(triangle.B),
                        rotation.Transform(triangle.C))), validateSolid: false));
            }).ToArray();
        };
    }

    private void OpenHolePattern(ModelingDialog dialog)
    {
        if (Selected is not { Hidden: false, Holes: { Length: > 0 } holes } body)
        {
            SetStatus("Select a solid that already has a hole to repeat.");
            return;
        }
        dialog.Hint("Repeats a hole around a center on the same face. The original counts as the first instance.");
        var holeNames = Enumerable.Range(1, holes.Length).Select(i => $"Hole {i}").ToArray();
        var source = dialog.Choice("Hole", holeNames);
        var count = dialog.Number("Instances", 4, 2, 64, whole: true);
        var angle = dialog.Number("Total angle (°)", 360, 0.1, 360);
        var u = dialog.Length("Center U", 0, signed: true);
        var v = dialog.Length("Center V", 0, signed: true);
        var result = dialog.Run(() =>
        {
            int index = Array.IndexOf(holeNames, source());
            var hole = holes[index];
            double centerU = u(), centerV = v();
            double x = hole.Center.X - centerU, y = hole.Center.Y - centerV;
            Require(x * x + y * y > 1e-12, "The pattern center must be away from the original hole.");
            var copies = PatternAngles((int)count(), angle()).Select(degrees =>
            {
                double theta = degrees * Math.PI / 180;
                return hole with { Center = new(centerU + x * Math.Cos(theta) - y * Math.Sin(theta),
                    centerV + x * Math.Sin(theta) + y * Math.Cos(theta)) };
            }).ToArray();
            var threads = body.Threads;
            if (threads?.FirstOrDefault(thread => thread.Surface == index) is { } sourceThread)
                threads = [..threads, ..copies.Select((_, i) => sourceThread with { Surface = holes.Length + i })];
            var changed = body with { Holes = [..holes, ..copies], Threads = threads };
            Require(changed.IsValid(), "The repeated holes must fit on the face and clear neighboring features.");
            return [changed];
        });
        if (result is null)
            return;
        int partIndex = parts.FindIndex(part => ReferenceEquals(part, body));
        parts[partIndex] = result[0];
        MarkDirty();
        RefreshParts(partIndex);
        SetStatus($"Added {result[0].Holes!.Length - holes.Length} holes to {body.Name}.");
    }

    private static IEnumerable<double> PatternAngles(int instances, double angle)
    {
        double step = angle / (angle == 360 ? instances : instances - 1);
        return Enumerable.Range(1, instances - 1).Select(i => i * step);
    }

    private string NextModeledName(string tool)
    {
        int number = 1;
        while (parts.Any(part => part.Name == $"{tool} {number}" || part.Name.StartsWith($"{tool} {number} · ", StringComparison.Ordinal)))
            number++;
        return $"{tool} {number}";
    }

    private static Part MeshBody(string name, IEnumerable<Triangle>? triangles, bool restOnBed = false, bool validateSolid = true)
    {
        var mesh = triangles?.ToArray();
        if (mesh is not { Length: > 0 })
            throw new ArgumentException("The inputs do not form a closed solid. Check the profiles, paths, and dimensions.");
        if (validateSolid)
        {
            using var solid = SolidBoolean.Create(mesh);
            Require(!solid.IsInvalid, "The inputs do not form a closed solid. Check for overlapping or degenerate geometry.");
        }
        var bounds = MeshData.GetBounds(mesh);
        var origin = new Vector3D(bounds.X + bounds.SizeX / 2, bounds.Y + bounds.SizeY / 2, bounds.Z);
        return new Part(PartKind.Mesh, name, 0, 0, 0, origin.X, origin.Y, restOnBed ? 0 : origin.Z,
            Mesh: new MeshData(mesh.Select(triangle => triangle.Translate(-origin)).ToArray()));
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new ArgumentException(message);
    }
}
