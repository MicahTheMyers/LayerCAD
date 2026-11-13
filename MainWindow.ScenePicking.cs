using System.Windows.Media.Media3D;

namespace LayerCAD;

public partial class MainWindow
{
    private sealed record ScenePickEntry(GeometryModel3D Model, Part Part, List<Triangle> Triangles,
        Rect3D Bounds, Vector3D Translation, int Order)
    {
        public bool IsSketch => Part.Kind == PartKind.Sketch;
    }

    private sealed record ScenePickNode(Rect3D Bounds, bool HasSolids, ScenePickEntry[]? Entries,
        ScenePickNode? Left = null, ScenePickNode? Right = null);

    private void RebuildScenePickIndex()
    {
        var entries = pickableParts.Select((pair, index) => new ScenePickEntry(pair.Key, pair.Value.Part,
            pair.Value.Triangles, pair.Key.Bounds, previewTranslations.GetValueOrDefault(pair.Key), index))
            .Where(entry => !entry.Bounds.IsEmpty).ToArray();
        scenePickRoot = entries.Length == 0 ? null : Build(entries);

        static ScenePickNode Build(ScenePickEntry[] entries)
        {
            var bounds = Rect3D.Empty;
            bool hasSolids = false;
            foreach (var entry in entries)
            {
                bounds.Union(entry.Bounds);
                hasSolids |= !entry.IsSketch;
            }
            if (entries.Length <= 8)
                return new ScenePickNode(bounds, hasSolids, entries);

            int axis = bounds.SizeX >= bounds.SizeY && bounds.SizeX >= bounds.SizeZ ? 0 :
                bounds.SizeY >= bounds.SizeZ ? 1 : 2;
            Array.Sort(entries, (a, b) => Center(a.Bounds, axis).CompareTo(Center(b.Bounds, axis)));
            int middle = entries.Length / 2;
            return new ScenePickNode(bounds, hasSolids, null, Build(entries[..middle]), Build(entries[middle..]));
        }

        static double Center(Rect3D bounds, int axis) => axis switch
        {
            0 => bounds.X + bounds.SizeX / 2,
            1 => bounds.Y + bounds.SizeY / 2,
            _ => bounds.Z + bounds.SizeZ / 2
        };
    }
}
