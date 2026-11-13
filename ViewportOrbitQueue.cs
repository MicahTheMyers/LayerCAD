using System.Windows;
using System.Windows.Media;

namespace LayerCAD;

internal sealed class ViewportOrbitQueue(Action<Vector> apply)
{
    private Vector pending;
    private bool scheduled;

    public void Add(Vector delta)
    {
        if (delta.LengthSquared == 0)
            return;
        pending += delta;
        if (!scheduled)
        {
            scheduled = true;
            CompositionTarget.Rendering += OnRendering;
        }
    }

    public void Flush()
    {
        if (!scheduled)
            return;
        var delta = pending;
        Cancel();
        if (delta.LengthSquared > 0)
            apply(delta);
    }

    public void Cancel()
    {
        if (scheduled)
            CompositionTarget.Rendering -= OnRendering;
        scheduled = false;
        pending = default;
    }

    private void OnRendering(object? sender, EventArgs e) => Flush();
}
