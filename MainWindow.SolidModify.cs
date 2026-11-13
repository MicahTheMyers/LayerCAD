using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Media3D;

namespace LayerCAD;

public partial class MainWindow
{
    private sealed record BodyChoice(Part Body)
    {
        public override string ToString() => Body.Name;
    }

    private void OpenSolidModify(object sender, RoutedEventArgs e)
    {
        EndBuildFeature();
        EndSurfaceSelection();
        string tool = (string)((MenuItem)sender).Tag;
        var bodies = parts.Where(part => !part.Hidden && part.Kind != PartKind.Sketch &&
            (tool != "Shell" || CanShell(part))).Select(part => new BodyChoice(part)).ToArray();
        if (bodies.Length < (tool == "Combine" ? 2 : 1))
        {
            SetStatus(tool switch
            {
                "Combine" => "Show two solid bodies to use Combine.",
                "Shell" => "Show a plain box or cylinder to use Shell.",
                _ => "Show a solid body first."
            });
            return;
        }
        var dialog = new ModelingDialog(this, tool, units);
        int selected = Math.Max(0, Array.FindIndex(bodies, item => ReferenceEquals(item.Body, Selected)));
        var target = dialog.Choice("Target body", bodies, selected);
        var calculate = tool switch
        {
            "Combine" => CombineSetup(dialog, target, bodies, selected),
            "Split Body" => SplitSetup(dialog, target),
            "Shell" => ShellSetup(dialog, target),
            _ => ScaleSetup(dialog, target)
        };
        var edit = dialog.RunValue(calculate, "Apply");
        if (edit is null)
            return;
        int index = parts.FindIndex(item => ReferenceEquals(item, edit.Target));
        parts[index] = edit.Results[0];
        if (edit.Results.Length > 1)
            parts.InsertRange(index + 1, edit.Results.Skip(1));
        if (edit.Remove is { } consumed)
            parts.RemoveAll(item => ReferenceEquals(item, consumed));
        lassoSelection.Clear();
        MarkDirty();
        RefreshParts(parts.FindIndex(item => ReferenceEquals(item, edit.Results[0])));
        SetStatus($"{tool} applied to {edit.Target.Name}.");
    }

    private sealed record SolidEdit(Part Target, Part[] Results, Part? Remove = null);

    private static bool CanShell(Part part) => part.Kind is PartKind.Box or PartKind.Cylinder &&
        part.Bevel == 0 && part.BevelCuts is null && part.Holes is null && part.Threads is null;

    private static Func<SolidEdit> ShellSetup(ModelingDialog dialog, Func<BodyChoice> target)
    {
        dialog.Hint("Hollow out the body with uniform walls. Choose which end to open.");
        var thickness = dialog.Length("Wall thickness", 2);
        var opening = dialog.Choice("Opening", new[] { "Top", "Bottom", "None" });
        return () =>
        {
            var stock = target().Body;
            return new(stock, [ShellBody(stock, thickness(), opening())]);
        };
    }

    private static Part ShellBody(Part stock, double thickness, string opening)
    {
        double floor = opening == "Bottom" ? -1 : thickness;
        double ceiling = opening == "Top" ? stock.Height + 1 : stock.Height - thickness;
        Require(CanShell(stock) && Part.ValidDimension(thickness) && stock.Width > 2 * thickness &&
            (stock.Kind != PartKind.Box || stock.Depth > 2 * thickness) &&
            stock.Height > (opening == "None" ? 2 : 1) * thickness, "Reduce the thickness so a cavity fits.");
        var cavity = stock with
        {
            Width = stock.Width - 2 * thickness,
            Depth = stock.Kind == PartKind.Box ? stock.Depth - 2 * thickness : 0,
            Height = ceiling - floor,
            Z = stock.Z + floor
        };
        return ModifyMesh(stock, Geometry.Build(cavity), "Cut");
    }

    private static Func<SolidEdit> CombineSetup(ModelingDialog dialog, Func<BodyChoice> target, BodyChoice[] bodies, int selected)
    {
        var tool = dialog.Choice("Tool body", bodies, selected == 0 ? 1 : 0);
        var operation = dialog.Choice("Operation", new[] { "Join", "Cut", "Intersect" });
        var keep = dialog.Choice("Tool body after applying", new[] { "Keep", "Consume" });
        return () =>
        {
            var stock = target().Body;
            var cutter = tool().Body;
            Require(!ReferenceEquals(stock, cutter), "Choose two different bodies.");
            var result = ModifyMesh(stock, Geometry.Build(cutter), operation());
            return new(stock, [result], keep() == "Consume" ? cutter : null);
        };
    }

    private static Func<SolidEdit> ScaleSetup(ModelingDialog dialog, Func<BodyChoice> target)
    {
        var factor = dialog.Number("Uniform scale factor", 1, 0.001, 1000);
        return () =>
        {
            var stock = target().Body;
            double size = factor();
            Require(size != 1, "Enter a scale factor other than 1.");
            var origin = new Point3D(stock.X, stock.Y, stock.Z);
            Point3D Scale(Point3D point) => origin + (point - origin) * size;
            return new(stock, [MeshBody(stock.Name, Geometry.Build(stock)
                .Select(triangle => new Triangle(Scale(triangle.A), Scale(triangle.B), Scale(triangle.C))), validateSolid: false)]);
        };
    }

    private Func<SolidEdit> SplitSetup(ModelingDialog dialog, Func<BodyChoice> target)
    {
        var plane = dialog.Choice("Plane", new[] { "XY", "XZ", "YZ" });
        var offset = dialog.Length("Plane coordinate", 0, signed: true);
        return () =>
        {
            var stock = target().Body;
            var bounds = PartWorldBounds(stock);
            double coordinate = offset();
            double[] low = [bounds.X - 1, bounds.Y - 1, bounds.Z - 1];
            double[] high = [bounds.X + bounds.SizeX + 1, bounds.Y + bounds.SizeY + 1, bounds.Z + bounds.SizeZ + 1];
            int axis = plane() switch { "YZ" => 0, "XZ" => 1, _ => 2 };
            Require(coordinate > low[axis] + 1 && coordinate < high[axis] - 1, "The plane must cross the body.");
            double limit = high[axis];
            high[axis] = coordinate;
            var first = ModifyMesh(stock, Cutter(), "Intersect");
            low[axis] = coordinate;
            high[axis] = limit;
            var second = ModifyMesh(stock, Cutter(), "Intersect") with { Name = NextModeledName("Split") };
            return new(stock, [first, second]);

            List<Triangle> Cutter() => Geometry.Build(new Part(PartKind.Box, "Split", high[0] - low[0], high[1] - low[1],
                high[2] - low[2], (high[0] + low[0]) / 2, (high[1] + low[1]) / 2, low[2]));
        };
    }

    private static Part ModifyMesh(Part stock, List<Triangle> cutter, string operation)
    {
        using var left = SolidBoolean.Create(Geometry.Build(stock));
        using var right = SolidBoolean.Create(cutter);
        Require(!left.IsInvalid && !right.IsInvalid, "Choose closed, watertight bodies.");
        int outcome = SolidBoolean.Apply(left, right, operation == "Join" ? 1 : 0, out var result);
        using (result)
        {
            Require(outcome == 1, "The bodies must overlap or share a face for this operation.");
            if (operation != "Intersect")
                return MeshBody(stock.Name, SolidBoolean.ReadMesh(result, default), validateSolid: false);
            var remainder = SolidBoolean.ReadMesh(result, default);
            Require(remainder is not null, "Could not read the intersection.");
            if (remainder!.Length == 0)
                return MeshBody(stock.Name, Geometry.Build(stock), validateSolid: false);
            int intersected = SolidBoolean.Apply(left, result, 0, out var overlap);
            using (overlap)
            {
                Require(intersected == 1, "Could not compute the shared volume.");
                return MeshBody(stock.Name, SolidBoolean.ReadMesh(overlap, default), validateSolid: false);
            }
        }
    }
}
