namespace LayerCAD;

internal sealed class EditHistory<T> where T : class
{
    private const int Limit = 64;
    private readonly List<T> undo = [];
    private readonly List<T> redo = [];
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;

    public void Reset()
    {
        undo.Clear();
        redo.Clear();
    }

    public void Record(T previous)
    {
        if (undo.Count == Limit)
            undo.RemoveAt(0);
        undo.Add(previous);
        redo.Clear();
    }

    public T? Step(T current, bool forward)
    {
        var source = forward ? redo : undo;
        if (source.Count == 0)
            return null;
        var target = source[^1];
        source.RemoveAt(source.Count - 1);
        (forward ? undo : redo).Add(current);
        return target;
    }
}
