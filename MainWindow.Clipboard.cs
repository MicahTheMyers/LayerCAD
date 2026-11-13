using System.Windows;
using System.Windows.Input;

namespace LayerCAD;

public partial class MainWindow
{
    private void HandleBuildClipboard(Key key)
    {
        if (key == Key.A)
        {
            EndBuildFeature();
            EndSurfaceSelection();
            SetLassoSelection(parts.Where(part => !part.Hidden).ToArray(), extend: false);
            var bounds = new Rect(new Point(), ModelViewport.RenderSize);
            SelectFacesInRegion(ViewportSelection.Geometry(ViewportSelection.Box(bounds.TopLeft, bounds.BottomRight)), extend: false);
        }
        else if (key == Key.C)
        {
            var selection = lassoSelection.Count > 0 ? parts.Where(lassoSelection.Contains).ToArray() :
                Selected is { } selected ? [selected] : [];
            if (selection.Length == 0)
            {
                SetStatus("Select an object or sketch to copy.");
                return;
            }
            SetStatus(LayerClipboard.Copy(LayerClipboard.Objects, selection)
                ? $"Copied {selection.Length} {(selection.Length == 1 ? "item" : "items")}." : "The clipboard is unavailable.");
        }
        else
            PasteObjects();
    }

    private void PasteObjects()
    {
        var copied = LayerClipboard.Paste<Part[]>(LayerClipboard.Objects);
        if (copied is not { Length: > 0 } || copied.Any(part => part is null || !part.IsValid()))
        {
            SetStatus("The clipboard has no LayerCAD objects or sketches.");
            return;
        }

        var links = new Dictionary<Guid, Guid>();
        var names = parts.Select(part => part.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var sketch in copied.Where(part => part.Kind == PartKind.Sketch && part.SketchId is not null))
            links.TryAdd(sketch.SketchId!.Value, Guid.NewGuid());
        double dx = Offset(part => part.X), dy = Offset(part => part.Y);
        var pasted = copied.Select(part =>
        {
            Guid? link = part.SketchId is Guid id && links.TryGetValue(id, out var newId) ? newId : null;
            if (part.Kind == PartKind.Sketch && link is null)
                link = Guid.NewGuid();
            return part with
            {
                Name = CopyName(part.Name), X = part.X + dx, Y = part.Y + dy, Hidden = false,
                SketchId = link, SketchProfileIndex = link is null ? 0 : part.SketchProfileIndex
            };
        }).ToArray();
        if (!new Project(19, bed, pasted).IsValid())
        {
            SetStatus("The copied items could not be pasted.");
            return;
        }

        EndBuildFeature();
        EndSurfaceSelection();
        parts.AddRange(pasted);
        MarkDirty();
        RefreshParts(parts.Count - pasted.Length);
        var solids = pasted.Where(part => part.Kind != PartKind.Sketch).ToArray();
        SetLassoSelection(solids.Length > 0 ? solids : pasted, extend: false);
        SetStatus($"Pasted {pasted.Length} {(pasted.Length == 1 ? "item" : "items")}.");

        double Offset(Func<Part, double> coordinate) => copied.All(part => Part.ValidOffset(coordinate(part) + 5)) ? 5 :
            copied.All(part => Part.ValidOffset(coordinate(part) - 5)) ? -5 : 0;

        string CopyName(string name)
        {
            string baseName = $"{name} copy";
            string candidate = baseName;
            for (int number = 2; !names.Add(candidate); number++)
                candidate = $"{baseName} {number}";
            return candidate;
        }
    }
}
