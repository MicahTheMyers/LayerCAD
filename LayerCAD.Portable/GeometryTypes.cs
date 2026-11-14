using System.Text.Json.Serialization;

namespace System.Windows
{
    internal readonly record struct Vector(double X, double Y)
    {
        public double Length => Math.Sqrt(X * X + Y * Y);
        public static double Multiply(Vector a, Vector b) => a.X * b.X + a.Y * b.Y;
    }
}

namespace System.Windows.Media.Media3D
{
    public record struct Point3D(double X, double Y, double Z)
    {
        public static Point3D operator +(Point3D p, Vector3D v) => new(p.X + v.X, p.Y + v.Y, p.Z + v.Z);
        public static Point3D operator -(Point3D p, Vector3D v) => p + -v;
        public static Vector3D operator -(Point3D a, Point3D b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static explicit operator Vector3D(Point3D p) => new(p.X, p.Y, p.Z);
    }

    public record struct Vector3D(double X, double Y, double Z)
    {
        [JsonIgnore] public readonly double LengthSquared => DotProduct(this, this);
        [JsonIgnore] public readonly double Length => Math.Sqrt(LengthSquared);
        public void Normalize()
        {
            double max = Math.Max(Math.Abs(X), Math.Max(Math.Abs(Y), Math.Abs(Z)));
            var scaled = this / max;
            this = scaled / scaled.Length;
        }
        public static double DotProduct(Vector3D a, Vector3D b) => a.X*b.X + a.Y*b.Y + a.Z*b.Z;
        public static Vector3D CrossProduct(Vector3D a, Vector3D b) => new(a.Y*b.Z-a.Z*b.Y, a.Z*b.X-a.X*b.Z, a.X*b.Y-a.Y*b.X);
        public static Vector3D operator +(Vector3D a, Vector3D b) => new(a.X+b.X, a.Y+b.Y, a.Z+b.Z);
        public static Vector3D operator -(Vector3D a, Vector3D b) => a + -b;
        public static Vector3D operator -(Vector3D v) => new(-v.X,-v.Y,-v.Z);
        public static Vector3D operator *(Vector3D v, double s) => new(v.X*s,v.Y*s,v.Z*s);
        public static Vector3D operator *(double s, Vector3D v) => v*s;
        public static Vector3D operator /(Vector3D v, double s) => new(v.X/s,v.Y/s,v.Z/s);
        public static explicit operator Point3D(Vector3D v) => new(v.X,v.Y,v.Z);
    }

    internal readonly record struct Size3D(double X, double Y, double Z);

    internal record struct Rect3D(double X, double Y, double Z, double SizeX, double SizeY, double SizeZ)
    {
        public Rect3D(Point3D point, Size3D size) : this(point.X, point.Y, point.Z,size.X,size.Y,size.Z) {}
        public static Rect3D Empty => new(0,0,0,double.NegativeInfinity,double.NegativeInfinity,double.NegativeInfinity);
        public readonly bool IsEmpty => SizeX < 0;
        public void Union(Point3D point) => Union(new Rect3D(point, default));
        public void Union(Rect3D other)
        {
            if (other.IsEmpty) return;
            if (IsEmpty) { this = other; return; }
            double x = Math.Min(X, other.X), y = Math.Min(Y, other.Y), z = Math.Min(Z, other.Z);
            this = new(x,y,z,Math.Max(X+SizeX,other.X+other.SizeX)-x,
                Math.Max(Y+SizeY,other.Y+other.SizeY)-y,Math.Max(Z+SizeZ,other.Z+other.SizeZ)-z);
        }
        public readonly bool IntersectsWith(Rect3D other) => !IsEmpty && !other.IsEmpty &&
            X <= other.X+other.SizeX && other.X <= X+SizeX &&
            Y <= other.Y+other.SizeY && other.Y <= Y+SizeY &&
            Z <= other.Z+other.SizeZ && other.Z <= Z+SizeZ;
    }
}
