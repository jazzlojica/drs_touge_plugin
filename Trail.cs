using System.Numerics;

namespace DrsTougePlugin;

/// <summary>
/// Breadcrumbs of the road the leader has driven. Lets us measure "how far ahead along the road is the leader"
/// and tell a real overtake from a chaser who is merely close by on the other side of a hairpin.
/// </summary>
public sealed class Trail
{
    private const float Step = 4f;
    private const int Max = 150;           // ~600 m of road
    private readonly List<Vector3> _pts = new();
    private readonly List<float> _cum = new();

    public int Count => _pts.Count;
    public Vector3 Point(int i) => _pts[i];

    public void Reset(Vector3 p) { _pts.Clear(); _cum.Clear(); _pts.Add(p); _cum.Add(0f); }

    public void Add(Vector3 p)
    {
        if (_pts.Count == 0) { Reset(p); return; }
        var d = Vector3.Distance(p, _pts[^1]);
        if (d < Step) return;
        _pts.Add(p); _cum.Add(_cum[^1] + d);
        if (_pts.Count > Max) { _pts.RemoveAt(0); _cum.RemoveAt(0); }
    }

    public int Nearest(Vector3 p)
    {
        var best = 0; var bestD = float.MaxValue;
        for (var i = 0; i < _pts.Count; i++)
        {
            var d = Vector3.DistanceSquared(p, _pts[i]);
            if (d < bestD) { bestD = d; best = i; }
        }
        return best;
    }

    /// <summary>Road distance from crumb i to the leader's current position.</summary>
    public float PathLead(int i) => _cum[^1] - _cum[i];
}
