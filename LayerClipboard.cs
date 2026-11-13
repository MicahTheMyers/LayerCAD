using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace LayerCAD;

internal static class LayerClipboard
{
    public const string Objects = "LayerCAD.Objects";
    public const string Profiles = "LayerCAD.Profiles";

    public static bool IsShortcut(KeyEventArgs e) =>
        (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Shift)) == ModifierKeys.Control &&
        e.Key is Key.A or Key.C or Key.V && e.OriginalSource is not TextBoxBase;

    public static bool Copy<T>(string format, T value)
    {
        try
        {
            Clipboard.SetDataObject(new DataObject(format, JsonSerializer.Serialize(value)), copy: true);
            return true;
        }
        catch (ExternalException)
        {
            return false;
        }
    }

    public static T? Paste<T>(string format) where T : class
    {
        try
        {
            return Clipboard.GetData(format) is string json ? JsonSerializer.Deserialize<T>(json) : null;
        }
        catch (Exception e) when (e is ExternalException or JsonException)
        {
            return null;
        }
    }
}
