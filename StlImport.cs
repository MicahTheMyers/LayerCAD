using System.Globalization;
using System.IO;
using System.Text;
using System.Windows.Media.Media3D;

namespace LayerCAD;

internal static class StlImport
{
    public static MeshData Read(Stream stream)
    {
        using var binary = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        byte[] header = binary.ReadBytes(80);
        bool asciiHeader = Encoding.ASCII.GetString(header).TrimStart().StartsWith("solid", StringComparison.OrdinalIgnoreCase);
        var triangles = new List<Triangle>();
        if (header.Length == 80 && stream.Length >= 84)
        {
            uint count = binary.ReadUInt32();
            long expectedLength = 84L + count * 50L;
            if (expectedLength == stream.Length || (!asciiHeader && expectedLength < stream.Length))
                ReadBinary(binary, count, triangles);
            else
                ReadAscii(stream, triangles);
        }
        else
            ReadAscii(stream, triangles);

        if (triangles.Count == 0)
            throw new InvalidDataException("The STL contains no usable triangles.");

        var bounds = MeshData.GetBounds(triangles);
        var center = new Vector3D(-(bounds.X + bounds.SizeX / 2), -(bounds.Y + bounds.SizeY / 2), -bounds.Z);
        var mesh = new MeshData(triangles.Select(triangle => triangle.Translate(center)).ToArray());
        if (!mesh.IsValid())
            throw new InvalidDataException("The STL coordinates cannot be represented at their current scale.");
        return mesh;
    }

    private static void ReadBinary(BinaryReader reader, uint count, List<Triangle> triangles)
    {
        for (uint index = 0; index < count; index++)
        {
            reader.BaseStream.Seek(12, SeekOrigin.Current);
            var triangle = new Triangle(ReadPoint(), ReadPoint(), ReadPoint());
            reader.ReadUInt16();
            if (triangle.IsValid())
                triangles.Add(triangle);
        }

        Point3D ReadPoint() => Point(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
    }

    private static void ReadAscii(Stream stream, List<Triangle> triangles)
    {
        stream.Position = 0;
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        bool solid = false;
        bool facet = false;
        int count = 0;
        var vertices = new Point3D[3];
        while (reader.ReadLine() is { } line)
        {
            string[] tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
                continue;

            switch (tokens[0].ToLowerInvariant())
            {
                case "solid" when !solid:
                    solid = true;
                    break;
                case "facet" when solid && !facet:
                    facet = true;
                    count = 0;
                    break;
                case "outer" or "endloop" when facet:
                    break;
                case "vertex" when facet && count < 3 && tokens.Length == 4:
                    vertices[count++] = Point(Parse(tokens[1]), Parse(tokens[2]), Parse(tokens[3]));
                    break;
                case "endfacet" when facet && count == 3:
                    var triangle = new Triangle(vertices[0], vertices[1], vertices[2]);
                    if (triangle.IsValid())
                        triangles.Add(triangle);
                    facet = false;
                    break;
                case "endsolid" when solid && !facet:
                    solid = false;
                    break;
                default:
                    throw new InvalidDataException("This file is not a complete ASCII or binary STL.");
            }
        }

        if (solid || facet)
            throw new InvalidDataException("The ASCII STL is incomplete.");
    }

    private static float Parse(string value) =>
        float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out float coordinate)
            ? coordinate
            : throw new InvalidDataException("The STL contains an invalid vertex.");

    private static Point3D Point(float x, float y, float z)
    {
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
            throw new InvalidDataException("The STL contains a non-finite vertex.");
        return new Point3D(x, y, z);
    }
}
