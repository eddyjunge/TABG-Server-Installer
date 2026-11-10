using System.Collections.Generic;
using UnityEngine;

namespace TabgInstaller.Destruction
{
    /// <summary>One mesh corner, in world space.</summary>
    internal struct Vtx
    {
        public Vector3 P;
        public Vector3 N;
        public Vector4 T;
        public Vector2 UV;

        public static Vtx Lerp(in Vtx a, in Vtx b, float t) => new Vtx
        {
            P = Vector3.LerpUnclamped(a.P, b.P, t),
            N = Vector3.LerpUnclamped(a.N, b.N, t),
            T = Vector4.LerpUnclamped(a.T, b.T, t),
            UV = Vector2.LerpUnclamped(a.UV, b.UV, t),
        };
    }

    /// <summary>Triangles: three corners each in <see cref="V"/>, one material slot each in <see cref="Mat"/>.</summary>
    internal sealed class TriSoup
    {
        public readonly List<Vtx> V = new List<Vtx>();
        public readonly List<int> Mat = new List<int>();

        public void Add(in Vtx a, in Vtx b, in Vtx c, int mat)
        {
            V.Add(a);
            V.Add(b);
            V.Add(c);
            Mat.Add(mat);
        }

        public int Triangles => Mat.Count;
    }

    /// <summary>A cut edge of a fragment: on its other side lies the fragment <see cref="Other"/>.</summary>
    internal struct BorderEdge
    {
        public int Other;
        public Vtx A;
        public Vtx B;
        public int Mat;
    }

    internal sealed class Fragment
    {
        public readonly TriSoup Soup = new TriSoup();
        public readonly List<BorderEdge> Border = new List<BorderEdge>();
        public Vector3 Seed;
        public Vector3 Min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        public Vector3 Max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        public float Area;

        public bool IsEmpty => Soup.Triangles == 0;
    }

    /// <summary>
    /// Cuts a triangle soup into Voronoi cells: every point belongs to its nearest seed, so the cut
    /// lines are irregular and look like cracks. Seeds and cuts depend only on the input and the
    /// given salt, so every client gets exactly the same fragments.
    /// </summary>
    internal sealed class Fracturer
    {
        private const int MaxDepth = 32;
        /// <summary>Well above float rounding at map coordinates (around 2000 m, ~0.1 mm).</summary>
        private const float OnPlane = 1e-3f;
        private const int NoTag = -1;

        private readonly List<Vector3> _seeds;
        private readonly Fragment[] _fragments;
        private readonly float _spacing;
        /// <summary>Seeds in each grid cell's 3×3×3 neighbourhood: nearly every lookup needs only those.</summary>
        private readonly Dictionary<long, List<int>> _near = new Dictionary<long, List<int>>();
        private long _lastKey = long.MinValue;
        private List<int> _lastNear;
        /// <summary>Splits left; a safety net so no input can make a blast take seconds.</summary>
        private int _splitsLeft;

        private Fracturer(List<Vector3> seeds, float spacing)
        {
            _seeds = seeds;
            _spacing = spacing;
            for (int i = 0; i < seeds.Count; i++)
            {
                int x = Mathf.FloorToInt(seeds[i].x / spacing);
                int y = Mathf.FloorToInt(seeds[i].y / spacing);
                int z = Mathf.FloorToInt(seeds[i].z / spacing);
                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        for (int dz = -1; dz <= 1; dz++)
                        {
                            long key = KeyOf(x + dx, y + dy, z + dz);
                            if (!_near.TryGetValue(key, out List<int> list))
                                _near[key] = list = new List<int>();
                            list.Add(i);
                        }
                    }
                }
            }
            _fragments = new Fragment[seeds.Count];
            for (int i = 0; i < seeds.Count; i++)
                _fragments[i] = new Fragment { Seed = seeds[i] };
        }

        /// <summary>
        /// One seed per occupied cell of a <paramref name="spacing"/> grid, jittered so the pattern
        /// isn't a grid. A wall gets one seed through its thickness, not one per face.
        /// </summary>
        public static List<Vector3> MakeSeeds(TriSoup soup, ref float spacing, int maxSeeds, int salt)
        {
            for (int attempt = 0; attempt < 8; attempt++, spacing *= 1.5f)
            {
                var cells = OccupiedCells(soup, spacing, maxSeeds);
                if (cells == null)
                    continue;
                var keys = new List<long>(cells);
                keys.Sort();
                var seeds = new List<Vector3>(keys.Count);
                foreach (long key in keys)
                    seeds.Add(SeedFor(key, spacing, salt));
                return seeds;
            }
            // Very spread-out geometry: a single piece.
            var all = new Vector3();
            foreach (Vtx v in soup.V)
                all += v.P;
            return new List<Vector3> { soup.V.Count > 0 ? all / soup.V.Count : Vector3.zero };
        }

        /// <summary>Splits <paramref name="soup"/>; empty fragments stay in the array, so indices match the seeds.</summary>
        public static Fragment[] Split(TriSoup soup, List<Vector3> seeds, float spacing)
        {
            var f = new Fracturer(seeds, spacing) { _splitsLeft = 200000 + soup.Triangles * 64 };
            for (int t = 0; t < soup.Triangles; t++)
                f.Emit(soup.V[t * 3], soup.V[t * 3 + 1], soup.V[t * 3 + 2], NoTag, NoTag, NoTag, soup.Mat[t], 0);
            foreach (Fragment fragment in f._fragments)
                f.Finish(fragment);
            return f._fragments;
        }

        private static HashSet<long> OccupiedCells(TriSoup soup, float spacing, int maxSeeds)
        {
            var cells = new HashSet<long>();
            float step = spacing * 0.4f;
            for (int t = 0; t < soup.Triangles; t++)
            {
                Vector3 a = soup.V[t * 3].P, b = soup.V[t * 3 + 1].P, c = soup.V[t * 3 + 2].P;
                float longest = Mathf.Max((b - a).magnitude, (c - a).magnitude, (c - b).magnitude);
                int n = Mathf.Clamp(Mathf.CeilToInt(longest / step), 1, 400);
                for (int i = 0; i <= n; i++)
                {
                    for (int j = 0; j <= n - i; j++)
                    {
                        float u = (float)i / n, v = (float)j / n;
                        cells.Add(CellKey(a + (b - a) * u + (c - a) * v, spacing));
                        if (cells.Count > maxSeeds)
                            return null;
                    }
                }
            }
            return cells;
        }

        private static long CellKey(Vector3 p, float spacing) =>
            KeyOf(Mathf.FloorToInt(p.x / spacing), Mathf.FloorToInt(p.y / spacing), Mathf.FloorToInt(p.z / spacing));

        private static long KeyOf(int cellX, int cellY, int cellZ)
        {
            long x = cellX + (1L << 20);
            long y = cellY + (1L << 20);
            long z = cellZ + (1L << 20);
            return (x << 42) | (y << 21) | z;
        }

        private static Vector3 SeedFor(long key, float spacing, int salt)
        {
            long mask = (1L << 21) - 1;
            int x = (int)((key >> 42) & mask) - (1 << 20);
            int y = (int)((key >> 21) & mask) - (1 << 20);
            int z = (int)(key & mask) - (1 << 20);
            uint h = Hash.Mix((uint)x, (uint)y, (uint)z, (uint)salt);
            // Stays inside its grid cell, so neighbouring seeds can't swap or coincide.
            float jx = 0.15f + 0.7f * Hash.Unit(ref h);
            float jy = 0.15f + 0.7f * Hash.Unit(ref h);
            float jz = 0.15f + 0.7f * Hash.Unit(ref h);
            return new Vector3((x + jx) * spacing, (y + jy) * spacing, (z + jz) * spacing);
        }

        private int Nearest(Vector3 p)
        {
            long key = CellKey(p, _spacing);
            if (key != _lastKey)
            {
                _lastKey = key;
                _near.TryGetValue(key, out _lastNear);
            }
            if (_lastNear != null)
            {
                int best = -1;
                float bestD = float.MaxValue;
                foreach (int i in _lastNear)
                {
                    float d = (p - _seeds[i]).sqrMagnitude;
                    if (d < bestD)
                    {
                        bestD = d;
                        best = i;
                    }
                }
                // Anything outside the neighbourhood is at least one cell away.
                if (bestD <= _spacing * _spacing)
                    return best;
            }
            return NearestOfAll(p);
        }

        private int NearestOfAll(Vector3 p)
        {
            int best = 0;
            float bestD = float.MaxValue;
            for (int i = 0; i < _seeds.Count; i++)
            {
                float d = (p - _seeds[i]).sqrMagnitude;
                if (d < bestD)
                {
                    bestD = d;
                    best = i;
                }
            }
            return best;
        }

        /// <summary>Tags say which fragment lies across each edge, or <see cref="NoTag"/> for an original edge.</summary>
        private void Emit(Vtx a, Vtx b, Vtx c, int tagAB, int tagBC, int tagCA, int mat, int depth)
        {
            int ia = Nearest(a.P), ib = Nearest(b.P), ic = Nearest(c.P);
            Vector3 centroid = (a.P + b.P + c.P) / 3f;

            if (depth >= MaxDepth || _splitsLeft <= 0)
            {
                Accept(Nearest(centroid), a, b, c, tagAB, tagBC, tagCA, mat);
                return;
            }
            if (ia == ib && ib == ic)
            {
                // A big triangle can hide a cell in its middle, with all corners in another.
                if (Longest(a.P, b.P, c.P) > _spacing * 1.2f && HidesAnotherCell(ia, a.P, b.P, c.P, centroid))
                {
                    _splitsLeft--;
                    SplitLongestEdge(a, b, c, tagAB, tagBC, tagCA, mat, depth);
                }
                else
                {
                    Accept(ia, a, b, c, tagAB, tagBC, tagCA, mat);
                }
                return;
            }

            // Only cut along a plane that really has corners on both sides. A corner that was just
            // made by a cut sits on the plane, and comparing it again would split forever.
            if (!FindCut(a.P, b.P, c.P, ia, ib, ic, out int p, out int q, out float da, out float db, out float dc))
            {
                Accept(Nearest(centroid), a, b, c, tagAB, tagBC, tagCA, mat);
                return;
            }
            _splitsLeft--;
            bool sa = da > 0f, sb = db > 0f, sc = dc > 0f;

            // Rotate so that `a` is the corner alone on its side of the plane.
            int turns = sa == sb ? 2 : sa == sc ? 1 : 0;
            for (int i = 0; i < turns; i++)
                Rotate(ref a, ref b, ref c, ref tagAB, ref tagBC, ref tagCA, ref da, ref db, ref dc);
            sa = da > 0f;

            float tx = da / (da - db);
            float ty = da / (da - dc);
            Vtx x = Vtx.Lerp(a, b, Mathf.Clamp01(tx));
            Vtx y = Vtx.Lerp(a, c, Mathf.Clamp01(ty));
            int alone = sa ? q : p;
            int rest = sa ? p : q;

            Emit(a, x, y, tagAB, rest, tagCA, mat, depth + 1);
            Emit(x, b, c, tagAB, tagBC, NoTag, mat, depth + 1);
            Emit(x, c, y, NoTag, tagCA, alone, mat, depth + 1);
        }

        private bool FindCut(Vector3 a, Vector3 b, Vector3 c, int ia, int ib, int ic, out int p, out int q, out float da, out float db, out float dc)
        {
            return TryCut(a, b, c, ia, ib, out p, out q, out da, out db, out dc)
                || TryCut(a, b, c, ib, ic, out p, out q, out da, out db, out dc)
                || TryCut(a, b, c, ia, ic, out p, out q, out da, out db, out dc);
        }

        private bool TryCut(Vector3 a, Vector3 b, Vector3 c, int first, int second, out int p, out int q, out float da, out float db, out float dc)
        {
            p = first;
            q = second;
            da = db = dc = 0f;
            if (first == second)
                return false;
            Vector3 normal = (_seeds[q] - _seeds[p]).normalized;
            Vector3 point = (_seeds[p] + _seeds[q]) * 0.5f;
            da = Vector3.Dot(a - point, normal);
            db = Vector3.Dot(b - point, normal);
            dc = Vector3.Dot(c - point, normal);
            float high = Mathf.Max(da, db, dc), low = Mathf.Min(da, db, dc);
            if (high <= OnPlane || low >= -OnPlane)
                return false;
            // Corners within the tolerance count as being on the plane's positive side.
            if (Mathf.Abs(da) <= OnPlane) da = OnPlane;
            if (Mathf.Abs(db) <= OnPlane) db = OnPlane;
            if (Mathf.Abs(dc) <= OnPlane) dc = OnPlane;
            return true;
        }

        private static void Rotate(ref Vtx a, ref Vtx b, ref Vtx c, ref int ab, ref int bc, ref int ca, ref float da, ref float db, ref float dc)
        {
            Vtx v = a; a = b; b = c; c = v;
            int t = ab; ab = bc; bc = ca; ca = t;
            float d = da; da = db; db = dc; dc = d;
        }

        private static float Longest(Vector3 a, Vector3 b, Vector3 c) =>
            Mathf.Max((b - a).magnitude, (c - b).magnitude, (a - c).magnitude);

        private bool HidesAnotherCell(int cell, Vector3 a, Vector3 b, Vector3 c, Vector3 centroid) =>
            Nearest(centroid) != cell
            || Nearest((a + b) * 0.5f) != cell
            || Nearest((b + c) * 0.5f) != cell
            || Nearest((c + a) * 0.5f) != cell;

        private void SplitLongestEdge(Vtx a, Vtx b, Vtx c, int tagAB, int tagBC, int tagCA, int mat, int depth)
        {
            float ab = (b.P - a.P).sqrMagnitude, bc = (c.P - b.P).sqrMagnitude, ca = (a.P - c.P).sqrMagnitude;
            if (ab >= bc && ab >= ca)
            {
                Vtx m = Vtx.Lerp(a, b, 0.5f);
                Emit(a, m, c, tagAB, NoTag, tagCA, mat, depth + 1);
                Emit(m, b, c, tagAB, tagBC, NoTag, mat, depth + 1);
            }
            else if (bc >= ca)
            {
                Vtx m = Vtx.Lerp(b, c, 0.5f);
                Emit(b, m, a, tagBC, NoTag, tagAB, mat, depth + 1);
                Emit(m, c, a, tagBC, tagCA, NoTag, mat, depth + 1);
            }
            else
            {
                Vtx m = Vtx.Lerp(c, a, 0.5f);
                Emit(c, m, b, tagCA, NoTag, tagBC, mat, depth + 1);
                Emit(m, a, b, tagCA, tagAB, NoTag, mat, depth + 1);
            }
        }

        private void Accept(int cell, in Vtx a, in Vtx b, in Vtx c, int tagAB, int tagBC, int tagCA, int mat)
        {
            Vector3 cross = Vector3.Cross(b.P - a.P, c.P - a.P);
            float area = cross.magnitude * 0.5f;
            if (area < 1e-8f)
                return;
            Fragment f = _fragments[cell];
            f.Soup.Add(a, b, c, mat);
            f.Area += area;
            Vector3 centroid = (a.P + b.P + c.P) / 3f;
            AddBorder(f, cell, a, b, tagAB, mat, centroid);
            AddBorder(f, cell, b, c, tagBC, mat, centroid);
            AddBorder(f, cell, c, a, tagCA, mat, centroid);
        }

        private void AddBorder(Fragment f, int cell, in Vtx a, in Vtx b, int tag, int mat, Vector3 centroid)
        {
            if (tag == NoTag)
                return;
            // A cut edge may have been split again further on; find who is really across it.
            Vector3 middle = (a.P + b.P) * 0.5f;
            Vector3 outward = (middle - centroid).normalized;
            int other = Nearest(middle + outward * 0.02f);
            if (other == cell)
                other = tag;
            if (other != cell)
                f.Border.Add(new BorderEdge { Other = other, A = a, B = b, Mat = mat });
        }

        private void Finish(Fragment f)
        {
            foreach (Vtx v in f.Soup.V)
            {
                f.Min = Vector3.Min(f.Min, v.P);
                f.Max = Vector3.Max(f.Max, v.P);
            }
        }
    }

    internal static class Hash
    {
        public static uint Mix(uint a, uint b, uint c, uint d)
        {
            uint h = 2166136261;
            foreach (uint v in new[] { a, b, c, d })
            {
                h = (h ^ v) * 16777619;
                h = (h << 13) | (h >> 19);
            }
            return h;
        }

        /// <summary>Next value in 0..1 from an xorshift step.</summary>
        public static float Unit(ref uint state)
        {
            state ^= state << 13;
            state ^= state >> 17;
            state ^= state << 5;
            return (state & 0xFFFFFF) / 16777216f;
        }
    }
}
