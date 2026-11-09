using MealRoute.Core.Network;

namespace MealRoute.Core.Travel;

/// <summary>
/// A path reduced to maximal runs of equal road class. Because speed depends only on class and
/// time, driving the merged run is identical to driving its edges one after another.
/// </summary>
internal sealed class CompressedPath
{
    private readonly RoadClass[] _class;
    private readonly double[] _length;

    private CompressedPath(RoadClass[] cls, double[] length, double total)
    {
        _class = cls;
        _length = length;
        LengthM = total;
    }

    public double LengthM { get; }

    public static CompressedPath FromEdges(RoadNetwork g, IReadOnlyList<int> edges)
    {
        var cls = new List<RoadClass>();
        var len = new List<double>();
        double total = 0;
        foreach (var e in edges)
        {
            var c = g.EdgeClass(e);
            var l = g.EdgeLength(e);
            total += l;
            if (cls.Count > 0 && cls[^1] == c) len[^1] += l;
            else { cls.Add(c); len.Add(l); }
        }
        return new CompressedPath(cls.ToArray(), len.ToArray(), total);
    }

    public double Arrival(SpeedProfile profile, double t)
    {
        for (var i = 0; i < _class.Length; i++) t = profile.ArrivalTime(_class[i], _length[i], t);
        return t;
    }

    public bool SameAs(CompressedPath other) =>
        _class.AsSpan().SequenceEqual(other._class) && _length.AsSpan().SequenceEqual(other._length);
}
