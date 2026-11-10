using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Rendering;

namespace TabgInstaller.Destruction
{
    /// <summary>Everything the main thread has to do after one blast; prepared on a worker.</summary>
    internal sealed class Commit
    {
        public List<MergedMap.Rebuild> Merged;
        public readonly List<SectionData> Sections = new List<SectionData>();
        public readonly List<Piece> HideRenderers = new List<Piece>();
        public readonly List<ColliderSource> DisableColliders = new List<ColliderSource>();
        public readonly List<DebrisData> Debris = new List<DebrisData>();
        public readonly List<Vector3> Dust = new List<Vector3>();
        public string Summary;
    }

    internal sealed class MeshArrays
    {
        public Vector3[] Vertices;
        public Vector3[] Normals;
        public Vector4[] Tangents;
        public Vector2[] UV;
        public int[][] Submeshes;
        public int[] MaterialIds;

        public int Triangles
        {
            get
            {
                int n = 0;
                foreach (int[] s in Submeshes)
                    n += s.Length / 3;
                return n;
            }
        }
    }

    internal sealed class SectionData
    {
        public long Key;
        public MeshArrays Render;
        /// <summary>World-space collision triangles: (layer, physic material id) → corners.</summary>
        public readonly Dictionary<long, List<Vector3>> Colliders = new Dictionary<long, List<Vector3>>();
    }

    internal sealed class DebrisData
    {
        public Vector3 Centre;
        public MeshArrays Mesh;
        public Vector3 Size;
        public float Mass;
        public Vector3 Velocity;
        public Vector3 Spin;
        public float Life;
    }

    /// <summary>A building: its parts, what holds them up, and the meshes that replace the originals once damaged.</summary>
    internal sealed class Structure
    {
        private const float SectionSize = 16f;
        private const float PieceLinkMargin = 0.3f;
        private const float WallThickness = 0.22f;
        private const float GroundSlack = 0.8f;
        private const float FragmentSpacing = 1.4f;
        private const int MaxSeedsPerPiece = 400;
        private const float ClusterSize = 5f;
        private const int MaxBlastDebris = 40;
        private const int MaxCollapseDebris = 18;
        private const int MaxDustPerBlast = 14;
        private const float DebrisDistance = 160f;
        /// <summary>Don't bother cutting things smaller than this; they just go whole.</summary>
        private const float MinFracturableSize = 1.6f;

        public int Salt;
        public WorldIndex Index;
        public Vector3 Min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
        public Vector3 Max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
        public readonly List<Piece> Pieces = new List<Piece>();

        private readonly SupportGraph _graph = new SupportGraph();
        private readonly Dictionary<long, List<Item>> _items = new Dictionary<long, List<Item>>();
        private readonly Dictionary<long, GameObject> _sections = new Dictionary<long, GameObject>();
        private readonly ConcurrentQueue<Commit> _done = new ConcurrentQueue<Commit>();
        private List<MergedMap.Cell> _dirtyCells;
        private Task _work = Task.CompletedTask;
        private bool _captured;
        private bool _initialised;
        private int _nextGroup;

        private struct Item
        {
            public int Piece;
            public int Fragment;
        }

        public void Add(Piece p)
        {
            Pieces.Add(p);
            Min = Vector3.Min(Min, p.Min);
            Max = Vector3.Max(Max, p.Max);
        }

        public bool Overlaps(Vector3 c, float r) =>
            c.x + r >= Min.x && c.x - r <= Max.x && c.y + r >= Min.y && c.y - r <= Max.y && c.z + r >= Min.z && c.z - r <= Max.z;

        public void Enqueue(Blast blast, Vector3 viewer, float debrisAmount)
        {
            if (!_captured)
                Capture();
            _work = _work.ContinueWith(_ =>
            {
                try
                {
                    _done.Enqueue(Apply(blast, viewer, debrisAmount));
                }
                catch (Exception e)
                {
                    Debug.LogError("[Destruction] " + e);
                }
            }, TaskScheduler.Default);
        }

        /// <summary>Everything that needs Unity objects, once, when this building is first hit.</summary>
        private void Capture()
        {
            _captured = true;
            var merged = new List<MeshRenderer>();
            foreach (Piece p in Pieces)
            {
                // "Combined Mesh" = static batching: one renderer's slice of a shared buffer, not its shape.
                p.Data = p.Mesh.name.StartsWith("Combined Mesh") ? null : MeshData.Of(p.Mesh);
                if (p.Data == null)
                    p.Standalone = true;
                if (!p.Standalone)
                {
                    p.Materials = p.Renderer.sharedMaterials;
                    p.MaterialIds = new int[p.Materials.Length];
                    for (int i = 0; i < p.Materials.Length; i++)
                        p.MaterialIds[i] = Index.MaterialId(p.Materials[i]);
                    if (p.Merged)
                        merged.Add(p.Renderer);
                }
                Strength(p.Renderer.name, out p.Health, out p.CapacityBase);
                p.Anchored = IsAnchored(p);
                foreach (ColliderSource c in p.Colliders)
                    CaptureCollider(c);
            }
            _dirtyCells = Index.Merged.Exclude(merged);
        }

        private bool IsAnchored(Piece p)
        {
            if (p.Min.y <= Min.y + 1.2f)
                return true;
            Vector3 centre = (p.Min + p.Max) * 0.5f;
            centre.y = p.Min.y - 0.15f;
            Vector3 half = new Vector3((p.Max.x - p.Min.x) * 0.45f, 0.35f, (p.Max.z - p.Min.z) * 0.45f);
            foreach (Collider c in Physics.OverlapBox(centre, half, Quaternion.identity, ~0, QueryTriggerInteraction.Ignore))
            {
                if (c is TerrainCollider)
                    return true;
            }
            return false;
        }

        private static void Strength(string name, out float health, out float capacity)
        {
            string n = name.ToLowerInvariant();
            if (n.Contains("wood") || n.Contains("barn") || n.Contains("shack") || n.Contains("single") || n.Contains("double"))
            {
                health = 70f;
                capacity = 260f;
            }
            else if (n.Contains("castle") || n.Contains("bunker") || n.Contains("area51") || n.Contains("rockwall"))
            {
                health = 170f;
                capacity = 900f;
            }
            else
            {
                health = 110f;
                capacity = 500f;
            }
        }

        private static void CaptureCollider(ColliderSource c)
        {
            switch (c.Collider)
            {
                case BoxCollider box:
                    c.Soup = BoxSoup(box.transform.localToWorldMatrix, box.center, box.size);
                    break;
                case MeshCollider mesh:
                    MeshData data = MeshData.Of(mesh.sharedMesh);
                    if (data != null)
                    {
                        c.Soup = new TriSoup();
                        data.AppendWorld(c.Soup, mesh.transform.localToWorldMatrix, new int[data.Submeshes.Length]);
                    }
                    break;
            }
        }

        private static TriSoup BoxSoup(Matrix4x4 m, Vector3 centre, Vector3 size)
        {
            var soup = new TriSoup();
            Vector3 h = size * 0.5f;
            Vtx C(float x, float y, float z) => new Vtx { P = m.MultiplyPoint3x4(centre + new Vector3(x * h.x, y * h.y, z * h.z)) };
            void Quad(Vtx a, Vtx b, Vtx c, Vtx d)
            {
                soup.Add(a, b, c, 0);
                soup.Add(a, c, d, 0);
            }
            Quad(C(-1, -1, -1), C(-1, 1, -1), C(1, 1, -1), C(1, -1, -1));
            Quad(C(1, -1, 1), C(1, 1, 1), C(-1, 1, 1), C(-1, -1, 1));
            Quad(C(-1, -1, 1), C(-1, 1, 1), C(-1, 1, -1), C(-1, -1, -1));
            Quad(C(1, -1, -1), C(1, 1, -1), C(1, 1, 1), C(1, -1, 1));
            Quad(C(-1, 1, -1), C(-1, 1, 1), C(1, 1, 1), C(1, 1, -1));
            Quad(C(-1, -1, 1), C(-1, -1, -1), C(1, -1, -1), C(1, -1, 1));
            return soup;
        }

        /// <summary>Runs on a worker, one blast at a time, always in the server's order.</summary>
        private Commit Apply(Blast blast, Vector3 viewer, float debrisAmount)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var commit = new Commit();
            var dirty = new HashSet<long>();
            if (!_initialised)
                Initialise(commit, dirty);

            for (int i = 0; i < Pieces.Count; i++)
            {
                Piece p = Pieces[i];
                if (p.Fragments != null || !p.Fracturable || p.Standalone || !_graph.Nodes[p.WholeNode].Alive)
                    continue;
                if (SupportGraph.Distance(_graph.Nodes[p.WholeNode], blast.Position) <= blast.Radius)
                    FracturePiece(i, commit, dirty);
            }

            var deaths = new List<Death>();
            _graph.Blast(blast.Position, blast.Radius, blast.Power, blast.Seq, deaths);
            _graph.Settle(deaths, ref _nextGroup);

            foreach (Death d in deaths)
            {
                Node n = _graph.Nodes[d.Node];
                Piece p = Pieces[n.Piece];
                dirty.Add(SectionOf(n.Centre));
                // Neighbours now show a broken edge, and they can be in the next section over.
                foreach (int linked in n.Links)
                    dirty.Add(SectionOf(_graph.Nodes[linked].Centre));
                if (n.Fragment < 0 || FragmentIsLast(p))
                {
                    if (p.Standalone)
                        commit.HideRenderers.Add(p);
                    foreach (ColliderSource c in p.Colliders)
                        commit.DisableColliders.Add(c);
                }
            }

            if ((blast.Position - viewer).sqrMagnitude < DebrisDistance * DebrisDistance)
                MakeDebris(blast, deaths, commit, debrisAmount);

            foreach (long key in dirty)
                commit.Sections.Add(BuildSection(key));

            int fractured = 0, standalone = 0, merged = 0, near = 0;
            foreach (Piece p in Pieces)
            {
                if (p.Fragments != null) fractured++;
                if (p.Standalone) standalone++;
                if (p.Merged) merged++;
                if (p.Fracturable && p.Min.x <= blast.Position.x + blast.Radius && p.Max.x >= blast.Position.x - blast.Radius
                    && p.Min.z <= blast.Position.z + blast.Radius && p.Max.z >= blast.Position.z - blast.Radius) near++;
            }
            commit.Summary = $"blast {blast.Seq}: {Pieces.Count} parts ({merged} merged, {standalone} standalone), {near} walls near, "
                + $"{fractured} cut, {_graph.Nodes.Count} nodes, {deaths.Count} destroyed, {commit.Sections.Count} sections, "
                + $"{commit.Merged?.Count ?? 0} merged meshes rebuilt, {commit.Debris.Count} debris, prepared in {watch.ElapsedMilliseconds} ms";
            return commit;
        }

        private bool FragmentIsLast(Piece p)
        {
            foreach (int node in p.FragmentNodes)
            {
                if (node >= 0 && _graph.Nodes[node].Alive)
                    return false;
            }
            return true;
        }

        private void Initialise(Commit commit, HashSet<long> dirty)
        {
            _initialised = true;
            if (_dirtyCells != null && _dirtyCells.Count > 0)
            {
                commit.Merged = new List<MergedMap.Rebuild>();
                foreach (MergedMap.Cell cell in _dirtyCells)
                    commit.Merged.AddRange(MergedMap.Build(cell));
            }

            for (int i = 0; i < Pieces.Count; i++)
            {
                Piece p = Pieces[i];
                p.Soup = new TriSoup();
                if (!p.Standalone)
                {
                    p.Data.AppendWorld(p.Soup, p.Matrix, p.MaterialIds);
                    p.Area = SurfaceArea(p.Soup);
                    if (!p.Merged)
                        commit.HideRenderers.Add(p);
                    long key = SectionOf((p.Min + p.Max) * 0.5f);
                    Items(key).Add(new Item { Piece = i, Fragment = -1 });
                    dirty.Add(key);
                }
                else
                {
                    Vector3 s = p.Max - p.Min;
                    p.Area = 2f * (s.x * s.y + s.y * s.z + s.z * s.x);
                }
                p.WholeNode = _graph.Add(new Node
                {
                    Min = p.Min,
                    Max = p.Max,
                    Mass = p.Area,
                    Health = p.Health,
                    Capacity = p.CapacityBase * Mathf.Max(1f, p.Area / 6f),
                    Anchored = p.Anchored,
                    Piece = i,
                });
            }

            var grid = new Dictionary<long, List<int>>();
            for (int i = 0; i < Pieces.Count; i++)
            {
                Node a = _graph.Nodes[Pieces[i].WholeNode];
                int x0 = Mathf.FloorToInt(a.Min.x / 8f), x1 = Mathf.FloorToInt(a.Max.x / 8f);
                int z0 = Mathf.FloorToInt(a.Min.z / 8f), z1 = Mathf.FloorToInt(a.Max.z / 8f);
                for (int x = x0; x <= x1; x++)
                {
                    for (int z = z0; z <= z1; z++)
                    {
                        long key = ((long)x << 32) ^ (uint)z;
                        if (!grid.TryGetValue(key, out List<int> list))
                            grid[key] = list = new List<int>();
                        foreach (int j in list)
                        {
                            if (SupportGraph.Touching(a, _graph.Nodes[Pieces[j].WholeNode], PieceLinkMargin))
                                _graph.Link(Pieces[i].WholeNode, Pieces[j].WholeNode);
                        }
                        list.Add(i);
                    }
                }
            }
            _graph.Stabilise(0);
        }

        private static float SurfaceArea(TriSoup soup)
        {
            float area = 0f;
            for (int t = 0; t < soup.Triangles; t++)
                area += Vector3.Cross(soup.V[t * 3 + 1].P - soup.V[t * 3].P, soup.V[t * 3 + 2].P - soup.V[t * 3].P).magnitude * 0.5f;
            return area;
        }

        private void FracturePiece(int index, Commit commit, HashSet<long> dirty)
        {
            Piece p = Pieces[index];
            Vector3 size = p.Max - p.Min;
            float spacing = Mathf.Max(FragmentSpacing, Mathf.Min(size.x, size.y, size.z) * 0.1f);
            Node whole = _graph.Nodes[p.WholeNode];

            List<Vector3> seeds;
            if (Mathf.Max(size.x, size.y, size.z) < MinFracturableSize)
                seeds = new List<Vector3> { (p.Min + p.Max) * 0.5f };
            else
                seeds = Fracturer.MakeSeeds(p.Soup, ref spacing, MaxSeedsPerPiece, Salt * 31 + index);

            p.Fragments = Fracturer.Split(p.Soup, seeds, spacing);
            p.FragmentNodes = new int[p.Fragments.Length];
            foreach (ColliderSource c in p.Colliders)
            {
                if (c.Soup == null)
                    continue;
                c.Fragments = Fracturer.Split(c.Soup, seeds, spacing);
                commit.DisableColliders.Add(c);
            }

            long oldKey = SectionOf((p.Min + p.Max) * 0.5f);
            Items(oldKey).RemoveAll(it => it.Piece == index);
            dirty.Add(oldKey);

            int firstNew = _graph.Nodes.Count;
            var nodes = new List<int>();
            for (int f = 0; f < p.Fragments.Length; f++)
            {
                Fragment fragment = p.Fragments[f];
                p.FragmentNodes[f] = -1;
                if (fragment.IsEmpty)
                    continue;
                int n = _graph.Add(new Node
                {
                    Min = fragment.Min,
                    Max = fragment.Max,
                    Mass = fragment.Area,
                    Health = p.Health,
                    Capacity = p.CapacityBase,
                    Anchored = p.Anchored && fragment.Min.y <= p.Min.y + GroundSlack,
                    Piece = index,
                    Fragment = f,
                });
                p.FragmentNodes[f] = n;
                nodes.Add(n);
                long key = SectionOf(_graph.Nodes[n].Centre);
                Items(key).Add(new Item { Piece = index, Fragment = f });
                dirty.Add(key);
            }

            // Inside the piece: fragments that share a cut. Outside: whatever the whole piece touched.
            for (int f = 0; f < p.Fragments.Length; f++)
            {
                if (p.FragmentNodes[f] < 0)
                    continue;
                foreach (BorderEdge edge in p.Fragments[f].Border)
                {
                    if (p.FragmentNodes[edge.Other] >= 0)
                        _graph.Link(p.FragmentNodes[f], p.FragmentNodes[edge.Other]);
                }
            }
            foreach (int neighbour in whole.Links)
            {
                Node other = _graph.Nodes[neighbour];
                if (!other.Alive)
                    continue;
                foreach (int n in nodes)
                {
                    if (SupportGraph.Touching(_graph.Nodes[n], other, PieceLinkMargin))
                        _graph.Link(n, neighbour);
                }
            }
            // Replaced by its fragments, not destroyed: no debris.
            whole.Alive = false;
            whole.Links.Clear();
            _graph.Stabilise(firstNew);
        }

        private List<Item> Items(long key)
        {
            if (!_items.TryGetValue(key, out List<Item> list))
                _items[key] = list = new List<Item>();
            return list;
        }

        private static long SectionOf(Vector3 p)
        {
            int x = Mathf.FloorToInt(p.x / SectionSize);
            int z = Mathf.FloorToInt(p.z / SectionSize);
            return ((long)x << 32) ^ (uint)z;
        }

        private void MakeDebris(Blast blast, List<Death> deaths, Commit commit, float amount)
        {
            uint rng = blast.Seq * 2654435761u + 1;
            int small = 0;
            var collapsing = new Dictionary<long, List<Death>>();
            foreach (Death d in deaths)
            {
                Node n = _graph.Nodes[d.Node];
                if (d.Cause == DeathCause.Unsupported)
                {
                    long key = ClusterKey(d.Group, n.Centre);
                    if (!collapsing.TryGetValue(key, out List<Death> list))
                        collapsing[key] = list = new List<Death>();
                    list.Add(d);
                    continue;
                }
                if (small < MaxBlastDebris * amount)
                {
                    Vector3 away = n.Centre - blast.Position;
                    Vector3 velocity = away.normalized * (4f + 7f * Hash.Unit(ref rng)) + Vector3.up * (2f + 4f * Hash.Unit(ref rng));
                    DebrisData debris = MakeChunk(new[] { d }, velocity, ref rng);
                    if (debris != null)
                    {
                        commit.Debris.Add(debris);
                        small++;
                    }
                }
                if (commit.Dust.Count < MaxDustPerBlast && Hash.Unit(ref rng) < 0.35f)
                    commit.Dust.Add(n.Centre);
            }

            int big = 0;
            var keys = new List<long>(collapsing.Keys);
            keys.Sort();
            foreach (long key in keys)
            {
                List<Death> group = collapsing[key];
                if (big < MaxCollapseDebris * amount)
                {
                    DebrisData debris = MakeChunk(group, new Vector3(0f, -1f, 0f), ref rng);
                    if (debris != null)
                    {
                        commit.Debris.Add(debris);
                        big++;
                    }
                }
                if (commit.Dust.Count < MaxDustPerBlast * 2)
                    commit.Dust.Add(_graph.Nodes[group[0].Node].Centre);
            }
        }

        private static long ClusterKey(int group, Vector3 p)
        {
            int x = Mathf.FloorToInt(p.x / ClusterSize) & 0xFFFFF;
            int y = Mathf.FloorToInt(p.y / ClusterSize) & 0xFFFFF;
            int z = Mathf.FloorToInt(p.z / ClusterSize) & 0xFFFFF;
            return ((long)group << 48) ^ ((long)x << 32) ^ ((long)y << 16) ^ (uint)z;
        }

        private DebrisData MakeChunk(IList<Death> group, Vector3 velocity, ref uint rng)
        {
            var builder = new MeshBuilder();
            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            float mass = 0f;
            foreach (Death d in group)
            {
                Node n = _graph.Nodes[d.Node];
                Piece p = Pieces[n.Piece];
                if (p.Standalone)
                    continue;
                if (n.Fragment < 0)
                {
                    builder.Add(p.Soup);
                }
                else
                {
                    Fragment f = p.Fragments[n.Fragment];
                    builder.Add(f.Soup);
                    foreach (BorderEdge edge in f.Border)
                        AddCap(builder, edge, f.Seed, p.Fragments[edge.Other].Seed);
                }
                min = Vector3.Min(min, n.Min);
                max = Vector3.Max(max, n.Max);
                mass += n.Mass;
            }
            if (builder.IsEmpty)
                return null;
            Vector3 centre = (min + max) * 0.5f;
            return new DebrisData
            {
                Centre = centre,
                Mesh = builder.Build(-centre),
                Size = max - min,
                Mass = Mathf.Clamp(mass * 8f, 2f, 4000f),
                Velocity = velocity,
                Spin = new Vector3(Hash.Unit(ref rng) - 0.5f, Hash.Unit(ref rng) - 0.5f, Hash.Unit(ref rng) - 0.5f) * 3f,
                Life = 7f + 6f * Hash.Unit(ref rng),
            };
        }

        /// <summary>The broken wall edge: a strip going inward from a cut, facing the hole.</summary>
        private static void AddCap(MeshBuilder builder, in BorderEdge edge, Vector3 own, Vector3 other)
        {
            Vector3 inward = -(edge.A.N + edge.B.N).normalized * WallThickness;
            Vector3 towardsHole = (other - own).normalized;
            Vtx a = edge.A, b = edge.B, c = edge.B, d = edge.A;
            c.P += inward;
            d.P += inward;
            a.N = b.N = c.N = d.N = towardsHole;
            Vector3 faceNormal = Vector3.Cross(b.P - a.P, c.P - a.P);
            if (Vector3.Dot(faceNormal, towardsHole) < 0f)
            {
                builder.Add(a, c, b, edge.Mat);
                builder.Add(a, d, c, edge.Mat);
            }
            else
            {
                builder.Add(a, b, c, edge.Mat);
                builder.Add(a, c, d, edge.Mat);
            }
        }

        private SectionData BuildSection(long key)
        {
            var data = new SectionData { Key = key };
            var builder = new MeshBuilder();
            foreach (Item item in Items(key))
            {
                Piece p = Pieces[item.Piece];
                if (item.Fragment < 0)
                {
                    if (_graph.Nodes[p.WholeNode].Alive)
                        builder.Add(p.Soup);
                    continue;
                }
                if (!_graph.Nodes[p.FragmentNodes[item.Fragment]].Alive)
                    continue;
                Fragment f = p.Fragments[item.Fragment];
                builder.Add(f.Soup);
                foreach (BorderEdge edge in f.Border)
                {
                    int across = p.FragmentNodes[edge.Other];
                    if (across < 0 || !_graph.Nodes[across].Alive)
                        AddCap(builder, edge, f.Seed, p.Fragments[edge.Other].Seed);
                }
                foreach (ColliderSource c in p.Colliders)
                {
                    if (c.Fragments == null)
                        continue;
                    long group = ((long)c.Layer << 32) | (uint)(c.PhysicId + 1);
                    if (!data.Colliders.TryGetValue(group, out List<Vector3> list))
                        data.Colliders[group] = list = new List<Vector3>();
                    foreach (Vtx v in c.Fragments[item.Fragment].Soup.V)
                        list.Add(v.P);
                }
            }
            data.Render = builder.IsEmpty ? null : builder.Build(Vector3.zero);
            return data;
        }

        /// <summary>Main thread. Applies at most one finished blast per call.</summary>
        public void Pump(Transform root, DebrisSpawner debris)
        {
            if (!_done.TryDequeue(out Commit c))
                return;
            var watch = System.Diagnostics.Stopwatch.StartNew();
            if (c.Merged != null)
                MergedMap.Apply(c.Merged);
            foreach (Piece p in c.HideRenderers)
            {
                if (p.Renderer != null)
                    p.Renderer.enabled = false;
            }
            foreach (ColliderSource col in c.DisableColliders)
            {
                if (col.Collider != null)
                    col.Collider.enabled = false;
            }
            foreach (SectionData s in c.Sections)
                ApplySection(s, root);
            debris.Spawn(c.Debris, c.Dust, Index);
            if (DestructionPlugin.Instance.Verbose)
                Debug.Log($"[Destruction] {c.Summary}; applied in {watch.ElapsedMilliseconds} ms");
        }

        private void ApplySection(SectionData s, Transform root)
        {
            _sections.TryGetValue(s.Key, out GameObject go);
            if (s.Render == null && s.Colliders.Count == 0)
            {
                if (go != null)
                {
                    DestroyMeshes(go);
                    UnityEngine.Object.Destroy(go);
                    _sections.Remove(s.Key);
                }
                return;
            }
            if (go == null)
            {
                go = new GameObject("TABG Destruction section");
                go.transform.SetParent(root, false);
                go.layer = 9;
                go.AddComponent<MeshFilter>();
                go.AddComponent<MeshRenderer>();
                _sections[s.Key] = go;
            }
            else
            {
                DestroyMeshes(go);
                for (int i = go.transform.childCount - 1; i >= 0; i--)
                    UnityEngine.Object.Destroy(go.transform.GetChild(i).gameObject);
            }

            var filter = go.GetComponent<MeshFilter>();
            var renderer = go.GetComponent<MeshRenderer>();
            if (s.Render != null)
            {
                filter.sharedMesh = MeshBuilder.ToMesh(s.Render);
                renderer.sharedMaterials = MaterialsFor(s.Render);
                renderer.enabled = true;
            }
            else
            {
                filter.sharedMesh = null;
                renderer.enabled = false;
            }

            foreach (KeyValuePair<long, List<Vector3>> group in s.Colliders)
            {
                if (group.Value.Count == 0)
                    continue;
                int layer = (int)(group.Key >> 32);
                int physic = (int)(uint)group.Key - 1;
                var mesh = new Mesh { indexFormat = IndexFormat.UInt32 };
                mesh.SetVertices(group.Value);
                var triangles = new int[group.Value.Count];
                for (int i = 0; i < triangles.Length; i++)
                    triangles[i] = i;
                mesh.triangles = triangles;
                var child = new GameObject("TABG Destruction collider") { layer = layer };
                child.transform.SetParent(go.transform, false);
                var collider = child.AddComponent<MeshCollider>();
                collider.sharedMesh = mesh;
                if (physic >= 0)
                    collider.sharedMaterial = Index.PhysicMaterials[physic];
            }
        }

        internal Material[] MaterialsFor(MeshArrays arrays)
        {
            var materials = new Material[arrays.MaterialIds.Length];
            for (int i = 0; i < materials.Length; i++)
                materials[i] = arrays.MaterialIds[i] >= 0 ? Index.Materials[arrays.MaterialIds[i]] : null;
            return materials;
        }

        private static void DestroyMeshes(GameObject go)
        {
            var filter = go.GetComponent<MeshFilter>();
            if (filter != null && filter.sharedMesh != null)
                UnityEngine.Object.Destroy(filter.sharedMesh);
            foreach (MeshCollider c in go.GetComponentsInChildren<MeshCollider>())
            {
                if (c.sharedMesh != null)
                    UnityEngine.Object.Destroy(c.sharedMesh);
            }
        }

        /// <summary>Everything back to how the map was loaded (a new match on the same map).</summary>
        public void Restore()
        {
            _work.Wait(2000);
            while (_done.TryDequeue(out Commit _)) { }
            foreach (GameObject go in _sections.Values)
            {
                DestroyMeshes(go);
                UnityEngine.Object.Destroy(go);
            }
            _sections.Clear();
            if (_captured)
            {
                foreach (Piece p in Pieces)
                {
                    if (p.Renderer != null && !p.Merged)
                        p.Renderer.enabled = true;
                    foreach (ColliderSource c in p.Colliders)
                    {
                        if (c.Collider != null)
                            c.Collider.enabled = true;
                        c.Fragments = null;
                    }
                    p.Fragments = null;
                    p.FragmentNodes = null;
                    p.Soup = null;
                    p.WholeNode = -1;
                }
            }
            _graph.Nodes.Clear();
            _items.Clear();
            _captured = false;
            _initialised = false;
            _nextGroup = 0;
            _dirtyCells = null;
            _work = Task.CompletedTask;
        }
    }

    /// <summary>Gathers triangles into a mesh with one submesh per material.</summary>
    internal sealed class MeshBuilder
    {
        private readonly List<Vtx> _vertices = new List<Vtx>();
        private readonly Dictionary<int, List<int>> _submeshes = new Dictionary<int, List<int>>();

        public bool IsEmpty => _vertices.Count == 0;

        public void Add(TriSoup soup)
        {
            for (int t = 0; t < soup.Triangles; t++)
                Add(soup.V[t * 3], soup.V[t * 3 + 1], soup.V[t * 3 + 2], soup.Mat[t]);
        }

        public void Add(in Vtx a, in Vtx b, in Vtx c, int material)
        {
            if (material < 0)
                return; // a renderer slot with no material: nothing to draw, and it would be pink
            if (!_submeshes.TryGetValue(material, out List<int> list))
                _submeshes[material] = list = new List<int>();
            int at = _vertices.Count;
            _vertices.Add(a);
            _vertices.Add(b);
            _vertices.Add(c);
            list.Add(at);
            list.Add(at + 1);
            list.Add(at + 2);
        }

        public MeshArrays Build(Vector3 offset)
        {
            int n = _vertices.Count;
            var arrays = new MeshArrays
            {
                Vertices = new Vector3[n],
                Normals = new Vector3[n],
                Tangents = new Vector4[n],
                UV = new Vector2[n],
                MaterialIds = new int[_submeshes.Count],
                Submeshes = new int[_submeshes.Count][],
            };
            for (int i = 0; i < n; i++)
            {
                Vtx v = _vertices[i];
                arrays.Vertices[i] = v.P + offset;
                arrays.Normals[i] = v.N;
                arrays.Tangents[i] = v.T;
                arrays.UV[i] = v.UV;
            }
            var keys = new List<int>(_submeshes.Keys);
            keys.Sort();
            for (int s = 0; s < keys.Count; s++)
            {
                arrays.MaterialIds[s] = keys[s];
                arrays.Submeshes[s] = _submeshes[keys[s]].ToArray();
            }
            return arrays;
        }

        public static Mesh ToMesh(MeshArrays a)
        {
            var mesh = new Mesh { indexFormat = IndexFormat.UInt32 };
            mesh.vertices = a.Vertices;
            mesh.normals = a.Normals;
            mesh.tangents = a.Tangents;
            mesh.uv = a.UV;
            mesh.subMeshCount = a.Submeshes.Length;
            for (int i = 0; i < a.Submeshes.Length; i++)
                mesh.SetTriangles(a.Submeshes[i], i, false);
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
