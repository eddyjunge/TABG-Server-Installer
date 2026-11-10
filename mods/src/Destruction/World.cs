using System.Collections.Generic;
using System.Text.RegularExpressions;
using MeshCombineStudio;
using UnityEngine;
using UnityEngine.Rendering;

namespace TabgInstaller.Destruction
{
    /// <summary>Copied mesh arrays, so workers can read them. Shared by every object using the mesh.</summary>
    internal sealed class MeshData
    {
        public Vector3[] Vertices;
        public Vector3[] Normals;
        public Vector4[] Tangents;
        public Vector2[] UV;
        public int[][] Submeshes;

        private static readonly Dictionary<Mesh, MeshData> Cache = new Dictionary<Mesh, MeshData>();

        /// <summary>Null for meshes the game doesn't keep in memory.</summary>
        public static MeshData Of(Mesh mesh)
        {
            if (mesh == null || !mesh.isReadable)
                return null;
            if (Cache.TryGetValue(mesh, out MeshData data))
                return data;
            data = new MeshData
            {
                Vertices = mesh.vertices,
                Normals = mesh.normals,
                Tangents = mesh.tangents,
                UV = mesh.uv,
                Submeshes = new int[mesh.subMeshCount][],
            };
            for (int i = 0; i < mesh.subMeshCount; i++)
                data.Submeshes[i] = mesh.GetTriangles(i);
            Cache[mesh] = data;
            return data;
        }

        public static void ClearCache() => Cache.Clear();

        public void AppendWorld(TriSoup soup, Matrix4x4 matrix, int[] materialIds)
        {
            Matrix4x4 normalMatrix = matrix.inverse.transpose;
            bool hasNormals = Normals.Length == Vertices.Length;
            bool hasTangents = Tangents.Length == Vertices.Length;
            bool hasUV = UV.Length == Vertices.Length;
            float winding = matrix.determinant < 0f ? -1f : 1f;
            for (int sub = 0; sub < Submeshes.Length && sub < materialIds.Length; sub++)
            {
                int[] tris = Submeshes[sub];
                for (int i = 0; i + 2 < tris.Length; i += 3)
                {
                    Vtx a = Corner(tris[i], matrix, normalMatrix, hasNormals, hasTangents, hasUV);
                    Vtx b = Corner(tris[i + 1], matrix, normalMatrix, hasNormals, hasTangents, hasUV);
                    Vtx c = Corner(tris[i + 2], matrix, normalMatrix, hasNormals, hasTangents, hasUV);
                    if (winding < 0f)
                        soup.Add(a, c, b, materialIds[sub]);
                    else
                        soup.Add(a, b, c, materialIds[sub]);
                }
            }
        }

        private Vtx Corner(int i, in Matrix4x4 m, in Matrix4x4 nm, bool normals, bool tangents, bool uv)
        {
            var v = new Vtx { P = m.MultiplyPoint3x4(Vertices[i]) };
            v.N = normals ? nm.MultiplyVector(Normals[i]).normalized : Vector3.up;
            if (tangents)
            {
                Vector3 t = m.MultiplyVector(Tangents[i]).normalized;
                v.T = new Vector4(t.x, t.y, t.z, Tangents[i].w);
            }
            v.UV = uv ? UV[i] : Vector2.zero;
            return v;
        }
    }

    /// <summary>
    /// Unity's static batching merges objects into one world-space mesh per area; each renderer then
    /// draws its own range of submeshes. These get one object's part back out.
    /// </summary>
    internal static class StaticBatch
    {
        private const System.Reflection.BindingFlags Hidden = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        private static readonly System.Reflection.PropertyInfo RootProperty = typeof(Renderer).GetProperty("staticBatchRootTransform", Hidden);

        public static Transform Root(Renderer r) => RootProperty?.GetValue(r) as Transform;

        /// <summary>Where the batch's vertices live: the batch root, or the world.</summary>
        public static Matrix4x4 Matrix(Renderer r)
        {
            Transform root = Root(r);
            return root != null ? root.localToWorldMatrix : Matrix4x4.identity;
        }

        /// <summary>This renderer's triangles as a mesh of their own; null if the batch can't be read.</summary>
        public static MeshData Slice(MeshRenderer r, Mesh batch)
        {
            MeshData all = MeshData.Of(batch);
            int first = r.subMeshStartIndex, count = r.sharedMaterials.Length;
            if (all == null || first < 0 || count <= 0 || first + count > all.Submeshes.Length)
                return null;
            var remap = new Dictionary<int, int>();
            var used = new List<int>();
            var subs = new int[count][];
            for (int s = 0; s < count; s++)
            {
                int[] tris = all.Submeshes[first + s];
                var mine = new int[tris.Length];
                for (int i = 0; i < tris.Length; i++)
                {
                    if (!remap.TryGetValue(tris[i], out int v))
                    {
                        v = used.Count;
                        remap[tris[i]] = v;
                        used.Add(tris[i]);
                    }
                    mine[i] = v;
                }
                subs[s] = mine;
            }
            bool normals = all.Normals.Length == all.Vertices.Length;
            bool tangents = all.Tangents.Length == all.Vertices.Length;
            bool uv = all.UV.Length == all.Vertices.Length;
            var data = new MeshData
            {
                Vertices = new Vector3[used.Count],
                Normals = normals ? new Vector3[used.Count] : new Vector3[0],
                Tangents = tangents ? new Vector4[used.Count] : new Vector4[0],
                UV = uv ? new Vector2[used.Count] : new Vector2[0],
                Submeshes = subs,
            };
            for (int i = 0; i < used.Count; i++)
            {
                int o = used[i];
                data.Vertices[i] = all.Vertices[o];
                if (normals) data.Normals[i] = all.Normals[o];
                if (tangents) data.Tangents[i] = all.Tangents[o];
                if (uv) data.UV[i] = all.UV[o];
            }
            return data;
        }
    }

    internal sealed class ColliderSource
    {
        public Collider Collider;
        public int Layer;
        public int PhysicId;
        /// <summary>Boxes and readable meshes as world triangles; null for anything else.</summary>
        public TriSoup Soup;
        public Fragment[] Fragments;
    }

    internal sealed class Piece
    {
        public MeshRenderer Renderer;
        public Mesh Mesh;
        public Material[] Materials;
        public int[] MaterialIds;
        public Matrix4x4 Matrix;
        public Vector3 Min;
        public Vector3 Max;
        public bool Fracturable;
        /// <summary>Keeps its own renderer: an unreadable mesh or something that moves (doors).</summary>
        public bool Standalone;
        public bool Merged;
        public bool Anchored;
        /// <summary>A tree (or bush): never cut, falls over whole when it breaks.</summary>
        public bool Tree;
        /// <summary>The lower-detail versions of this renderer (LOD1+), hidden with it.</summary>
        public Renderer[] LodSiblings;
        public bool[] LodSiblingsEnabled;
        /// <summary>Drawn from a static batch: its triangles are submeshes of a shared, world-space mesh.</summary>
        public bool Batched;
        public MeshData Data;
        public TriSoup Soup;
        public float Area;
        public float Health;
        public float CapacityBase;
        public int WholeNode = -1;
        public Fragment[] Fragments;
        public int[] FragmentNodes;
        public readonly List<ColliderSource> Colliders = new List<ColliderSource>();
    }

    /// <summary>Finds everything breakable in the loaded map and what it is made of. Runs once per map.</summary>
    internal sealed class WorldIndex
    {
        /// <summary>The ground itself and things that aren't solid: everything else in the map can break.</summary>
        private static readonly Regex NeverBreaks = new Regex(
            @"Road|Sidewalk|Terrain|Cloud|Mountain|Cliff|Water|Ocean|Decal|Logo|Skybox|^FX_|Grass|Gound|Ground|Floor_Ground|Shadow$",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex TreeName = new Regex(
            @"(?<!s)Tree|Fir_|_Fir|Spruce|Pine|Palm|Birch|Oak|Cactus|Bush",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        /// <summary>Layers that hold visible map geometry (not water, UI, effects).</summary>
        private const int RenderLayers = ~((1 << 4) | (1 << 5));
        private static readonly Regex Helper = new Regex(@"^(Cube|Cylinder|Capsule|Sphere|Quad|Plane)\b|Collision|RaycastCube|Foundation",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private const int WorldLayers = (1 << 0) | (1 << 9) | (1 << 11);
        private const float TouchMargin = 0.35f;
        private const float GridSize = 12f;
        private const float MaxPieceSize = 90f;
        private const float MaxBuildingSize = 56f;
        private const float TileSize = 40f;

        public readonly List<Structure> Structures = new List<Structure>();
        /// <summary>Structures that were hit at least once: the only ones with work to pump.</summary>
        public readonly List<Structure> Active = new List<Structure>();
        private const float StructureCell = 32f;
        private readonly Dictionary<long, List<Structure>> _structureGrid = new Dictionary<long, List<Structure>>();
        private int _stamp;
        public readonly MergedMap Merged = new MergedMap();
        public readonly List<Material> Materials = new List<Material>();
        public readonly List<PhysicMaterial> PhysicMaterials = new List<PhysicMaterial>();
        private readonly Dictionary<Material, int> _materialIds = new Dictionary<Material, int>();
        private readonly Dictionary<PhysicMaterial, int> _physicIds = new Dictionary<PhysicMaterial, int>();

        public int MaterialId(Material m)
        {
            if (m == null)
                return -1;
            if (!_materialIds.TryGetValue(m, out int id))
            {
                id = Materials.Count;
                Materials.Add(m);
                _materialIds[m] = id;
            }
            return id;
        }

        private int PhysicId(PhysicMaterial m)
        {
            if (m == null)
                return -1;
            if (!_physicIds.TryGetValue(m, out int id))
            {
                id = PhysicMaterials.Count;
                PhysicMaterials.Add(m);
                _physicIds[m] = id;
            }
            return id;
        }

        public static WorldIndex Build()
        {
            var index = new WorldIndex();
            index.Scan();
            return index;
        }

        private void Scan()
        {
            Merged.Scan();
            var pieces = new List<Piece>();
            var byTransform = new Dictionary<Transform, Piece>();
            Dictionary<Renderer, List<Renderer>> lods = LodSiblings(out HashSet<Renderer> lowerLods);
            foreach (MeshRenderer r in Resources.FindObjectsOfTypeAll<MeshRenderer>())
            {
                if (!r.gameObject.scene.IsValid() || r.hideFlags != HideFlags.None)
                    continue;
                if (((1 << r.gameObject.layer) & RenderLayers) == 0 || lowerLods.Contains(r))
                    continue;
                if (r.GetComponent<CachedComponents>() != null)
                    continue;
                if (IsHelper(r.transform))
                    continue;
                var filter = r.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null)
                    continue;
                // Doors, loot and cars move by themselves; the game owns them.
                if (r.GetComponentInParent<Rigidbody>() != null)
                    continue;
                string name = r.name;
                if (NeverBreaks.IsMatch(name))
                    continue;
                bool tree = TreeName.IsMatch(name) || (r.transform.parent != null && TreeName.IsMatch(r.transform.parent.name));
                var piece = new Piece
                {
                    Renderer = r,
                    Mesh = filter.sharedMesh,
                    Matrix = r.transform.localToWorldMatrix,
                    Fracturable = !tree,
                    Tree = tree,
                    Merged = !r.enabled,
                    Standalone = name.IndexOf("Door", System.StringComparison.OrdinalIgnoreCase) >= 0,
                    Batched = r.isPartOfStaticBatch,
                };
                if (lods.TryGetValue(r, out List<Renderer> siblings))
                {
                    piece.LodSiblings = siblings.ToArray();
                    piece.LodSiblingsEnabled = new bool[siblings.Count];
                    for (int i = 0; i < siblings.Count; i++)
                        piece.LodSiblingsEnabled[i] = siblings[i].enabled;
                }
                if (piece.Batched)
                {
                    // The filter holds the whole batch; the renderer knows its own box.
                    piece.Min = r.bounds.min;
                    piece.Max = r.bounds.max;
                }
                else
                {
                    BoundsOf(piece.Mesh.bounds, piece.Matrix, out piece.Min, out piece.Max);
                }
                Vector3 size = piece.Max - piece.Min;
                if (Mathf.Max(size.x, size.y, size.z) > MaxPieceSize)
                    continue;
                pieces.Add(piece);
                byTransform[r.transform] = piece;
            }
            // Scan order differs between machines; everything downstream must not.
            pieces.Sort(StableOrder);
            Cluster(pieces);
            foreach (Structure s in Structures)
            {
                s.Index = this;
                ForStructureCells(s.Min, s.Max, key =>
                {
                    if (!_structureGrid.TryGetValue(key, out List<Structure> list))
                        _structureGrid[key] = list = new List<Structure>();
                    list.Add(s);
                });
            }
            AttachColliders(byTransform);
        }

        /// <summary>For each LOD0 renderer, the renderers of its other LOD levels.</summary>
        private static Dictionary<Renderer, List<Renderer>> LodSiblings(out HashSet<Renderer> lower)
        {
            var map = new Dictionary<Renderer, List<Renderer>>();
            lower = new HashSet<Renderer>();
            foreach (LODGroup g in Resources.FindObjectsOfTypeAll<LODGroup>())
            {
                if (!g.gameObject.scene.IsValid())
                    continue;
                LOD[] levels = g.GetLODs();
                if (levels.Length < 2)
                    continue;
                Renderer main = null;
                foreach (Renderer r in levels[0].renderers)
                {
                    if (r is MeshRenderer)
                    {
                        main = r;
                        break;
                    }
                }
                if (main == null)
                    continue;
                for (int i = 1; i < levels.Length; i++)
                {
                    foreach (Renderer r in levels[i].renderers)
                    {
                        if (r == null || r == main || !lower.Add(r))
                            continue;
                        if (!map.TryGetValue(main, out List<Renderer> list))
                            map[main] = list = new List<Renderer>();
                        list.Add(r);
                    }
                }
            }
            return map;
        }

        private static void ForStructureCells(Vector3 min, Vector3 max, System.Action<long> action)
        {
            int x0 = Mathf.FloorToInt(min.x / StructureCell), x1 = Mathf.FloorToInt(max.x / StructureCell);
            int z0 = Mathf.FloorToInt(min.z / StructureCell), z1 = Mathf.FloorToInt(max.z / StructureCell);
            for (int x = x0; x <= x1; x++)
            {
                for (int z = z0; z <= z1; z++)
                    action(((long)x << 32) ^ (uint)z);
            }
        }

        /// <summary>Structures whose box may reach within <paramref name="r"/> of <paramref name="c"/>, each once.</summary>
        public List<Structure> Near(Vector3 c, float r)
        {
            var found = new List<Structure>();
            int stamp = ++_stamp;
            ForStructureCells(c - new Vector3(r, 0f, r), c + new Vector3(r, 0f, r), key =>
            {
                if (!_structureGrid.TryGetValue(key, out List<Structure> list))
                    return;
                foreach (Structure s in list)
                {
                    if (s.Stamp == stamp)
                        continue;
                    s.Stamp = stamp;
                    if (s.Overlaps(c, r))
                        found.Add(s);
                }
            });
            // Grid order isn't stable; keep the scan order.
            found.Sort((a, b) => a.Salt.CompareTo(b.Salt));
            return found;
        }

        private static int StableOrder(Piece a, Piece b)
        {
            int c = Coordinate(a.Min.x).CompareTo(Coordinate(b.Min.x));
            if (c == 0) c = Coordinate(a.Min.z).CompareTo(Coordinate(b.Min.z));
            if (c == 0) c = Coordinate(a.Min.y).CompareTo(Coordinate(b.Min.y));
            if (c == 0) c = Coordinate(a.Max.x).CompareTo(Coordinate(b.Max.x));
            if (c == 0) c = Coordinate(a.Max.z).CompareTo(Coordinate(b.Max.z));
            return c != 0 ? c : string.CompareOrdinal(a.Renderer.name, b.Renderer.name);
        }

        private static long Coordinate(float v) => (long)Mathf.Round(v * 100f);

        private static bool IsHelper(Transform t)
        {
            for (int depth = 0; t != null && depth < 3; depth++, t = t.parent)
            {
                if (Helper.IsMatch(t.name))
                    return true;
            }
            return false;
        }

        internal static void BoundsOf(Bounds local, in Matrix4x4 m, out Vector3 min, out Vector3 max)
        {
            min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            max = new Vector3(float.MinValue, float.MinValue, float.MinValue);
            Vector3 c = local.center, e = local.extents;
            for (int i = 0; i < 8; i++)
            {
                var corner = new Vector3((i & 1) == 0 ? -1 : 1, (i & 2) == 0 ? -1 : 1, (i & 4) == 0 ? -1 : 1);
                Vector3 world = m.MultiplyPoint3x4(c + Vector3.Scale(e, corner));
                min = Vector3.Min(min, world);
                max = Vector3.Max(max, world);
            }
        }

        private void Cluster(List<Piece> pieces)
        {
            var parent = new int[pieces.Count];
            for (int i = 0; i < parent.Length; i++)
                parent[i] = i;
            int Find(int x)
            {
                while (parent[x] != x)
                {
                    parent[x] = parent[parent[x]];
                    x = parent[x];
                }
                return x;
            }

            var grid = new Dictionary<long, List<int>>();
            for (int i = 0; i < pieces.Count; i++)
            {
                Piece a = pieces[i];
                ForCells(a.Min, a.Max, key =>
                {
                    if (!grid.TryGetValue(key, out List<int> list))
                        grid[key] = list = new List<int>();
                    foreach (int j in list)
                    {
                        if (Overlaps(a, pieces[j], TouchMargin))
                            parent[Find(i)] = Find(j);
                    }
                    list.Add(i);
                });
            }

            // Touching buildings chain up into whole towns. Work per building has to stay small, so
            // a very large group is divided into tiles; a collapse doesn't cross a tile border.
            var extents = new Dictionary<int, (Vector3 min, Vector3 max)>();
            for (int i = 0; i < pieces.Count; i++)
            {
                int root = Find(i);
                extents[root] = extents.TryGetValue(root, out var e)
                    ? (Vector3.Min(e.min, pieces[i].Min), Vector3.Max(e.max, pieces[i].Max))
                    : (pieces[i].Min, pieces[i].Max);
            }
            var groups = new Dictionary<(int, int, int), Structure>();
            for (int i = 0; i < pieces.Count; i++)
            {
                int root = Find(i);
                var e = extents[root];
                int tileX = 0, tileZ = 0;
                if (Mathf.Max(e.max.x - e.min.x, e.max.z - e.min.z) > MaxBuildingSize)
                {
                    Vector3 centre = (pieces[i].Min + pieces[i].Max) * 0.5f;
                    tileX = Mathf.FloorToInt(centre.x / TileSize);
                    tileZ = Mathf.FloorToInt(centre.z / TileSize);
                }
                var key = (root, tileX, tileZ);
                if (!groups.TryGetValue(key, out Structure s))
                {
                    s = new Structure { Salt = i };
                    groups[key] = s;
                    Structures.Add(s);
                }
                s.Add(pieces[i]);
            }
        }

        private static void ForCells(Vector3 min, Vector3 max, System.Action<long> action)
        {
            int x0 = Mathf.FloorToInt(min.x / GridSize), x1 = Mathf.FloorToInt(max.x / GridSize);
            int z0 = Mathf.FloorToInt(min.z / GridSize), z1 = Mathf.FloorToInt(max.z / GridSize);
            for (int x = x0; x <= x1; x++)
            {
                for (int z = z0; z <= z1; z++)
                    action(((long)x << 32) ^ (uint)z);
            }
        }

        private static bool Overlaps(Piece a, Piece b, float margin) =>
            a.Min.x <= b.Max.x + margin && b.Min.x <= a.Max.x + margin
            && a.Min.y <= b.Max.y + margin && b.Min.y <= a.Max.y + margin
            && a.Min.z <= b.Max.z + margin && b.Min.z <= a.Max.z + margin;

        private void AttachColliders(Dictionary<Transform, Piece> byTransform)
        {
            foreach (Collider c in Resources.FindObjectsOfTypeAll<Collider>())
            {
                if (!c.gameObject.scene.IsValid() || c.isTrigger || c is TerrainCollider)
                    continue;
                if (((1 << c.gameObject.layer) & WorldLayers) == 0 || c.attachedRigidbody != null || c.GetComponentInParent<Rigidbody>() != null)
                    continue;
                Piece owner = null;
                for (Transform t = c.transform; t != null && owner == null; t = t.parent)
                    byTransform.TryGetValue(t, out owner);
                if (owner == null)
                    owner = PieceContaining(ColliderCentre(c));
                if (owner == null)
                    continue;
                owner.Colliders.Add(new ColliderSource
                {
                    Collider = c,
                    Layer = c.gameObject.layer,
                    PhysicId = PhysicId(c.sharedMaterial),
                });
            }
        }

        private Piece PieceContaining(Vector3 p)
        {
            foreach (Structure s in Near(p, 0f))
            {
                if (p.x < s.Min.x || p.x > s.Max.x || p.y < s.Min.y || p.y > s.Max.y || p.z < s.Min.z || p.z > s.Max.z)
                    continue;
                Piece best = null;
                float bestSize = float.MaxValue;
                foreach (Piece piece in s.Pieces)
                {
                    if (p.x < piece.Min.x || p.x > piece.Max.x || p.y < piece.Min.y || p.y > piece.Max.y || p.z < piece.Min.z || p.z > piece.Max.z)
                        continue;
                    float size = (piece.Max - piece.Min).sqrMagnitude;
                    if (size < bestSize)
                    {
                        best = piece;
                        bestSize = size;
                    }
                }
                if (best != null)
                    return best;
            }
            return null;
        }

        private static Vector3 ColliderCentre(Collider c)
        {
            switch (c)
            {
                case BoxCollider b: return b.transform.TransformPoint(b.center);
                case SphereCollider s: return s.transform.TransformPoint(s.center);
                case CapsuleCollider cap: return cap.transform.TransformPoint(cap.center);
                case MeshCollider m when m.sharedMesh != null: return m.transform.TransformPoint(m.sharedMesh.bounds.center);
                default: return c.transform.position;
            }
        }
    }

    /// <summary>
    /// The game bakes most static objects into merged, unreadable per-area meshes. A building that
    /// gets damaged has to come out of those, so the area's mesh is rebuilt from its source objects
    /// without that building.
    /// </summary>
    internal sealed class MergedMap
    {
        internal sealed class Child
        {
            public MeshFilter Filter;
            public MeshRenderer Renderer;
            public Material Material;
            public int Layer;
            public Mesh Original;
            public bool Replaced;
            /// <summary>Captured on the main thread; transforms can't be read from workers.</summary>
            public Matrix4x4 ToLocal;
        }

        internal sealed class Source
        {
            public MeshRenderer Renderer;
            public MeshData Data;
            public Mesh Mesh;
            public Material[] Materials;
            public int Layer;
            public Matrix4x4 Matrix;
        }

        internal sealed class Cell
        {
            public Vector3 Centre;
            public Transform Root;
            public readonly List<Child> Children = new List<Child>();
            public readonly List<Source> Sources = new List<Source>();
            public readonly HashSet<MeshRenderer> Excluded = new HashSet<MeshRenderer>();
        }

        /// <summary>A rebuilt mesh for one child, ready to hand to Unity.</summary>
        internal sealed class Rebuild
        {
            public Child Child;
            public Vector3[] Vertices;
            public Vector3[] Normals;
            public Vector4[] Tangents;
            public Vector2[] UV;
            public int[] Triangles;
        }

        private readonly List<Cell> _cells = new List<Cell>();

        public int CellCount => _cells.Count;

        public void Scan()
        {
            var byRoot = new Dictionary<Transform, Cell>();
            foreach (CachedComponents c in Resources.FindObjectsOfTypeAll<CachedComponents>())
            {
                if (!c.gameObject.scene.IsValid() || c.mr == null || c.mf == null)
                    continue;
                Transform root = c.transform.parent;
                if (root == null)
                    continue;
                if (!byRoot.TryGetValue(root, out Cell cell))
                {
                    cell = new Cell { Root = root, Centre = root.position };
                    byRoot[root] = cell;
                    _cells.Add(cell);
                }
                cell.Children.Add(new Child
                {
                    Filter = c.mf,
                    Renderer = c.mr,
                    Material = c.mr.sharedMaterial,
                    Layer = c.gameObject.layer,
                    Original = c.mf.sharedMesh,
                    ToLocal = c.mf.transform.worldToLocalMatrix,
                });
            }
            if (_cells.Count == 0)
                return;

            foreach (MeshRenderer r in Resources.FindObjectsOfTypeAll<MeshRenderer>())
            {
                if (r.enabled || !r.gameObject.scene.IsValid() || r.GetComponent<CachedComponents>() != null)
                    continue;
                var filter = r.GetComponent<MeshFilter>();
                if (filter == null || filter.sharedMesh == null || !filter.sharedMesh.isReadable)
                    continue;
                string name = r.name;
                if (name.StartsWith("Cube") || name.StartsWith("Cylinder") || name.StartsWith("Capsule") || name.Contains("Collision")
                    || (r.transform.parent != null && r.transform.parent.name.Contains("Collision")))
                    continue;
                Cell cell = Nearest(r.transform.position);
                if (cell != null)
                {
                    cell.Sources.Add(new Source
                    {
                        Renderer = r,
                        Mesh = filter.sharedMesh,
                        Materials = r.sharedMaterials,
                        Layer = r.gameObject.layer,
                        Matrix = r.transform.localToWorldMatrix,
                    });
                }
            }
        }

        private Cell Nearest(Vector3 p)
        {
            Cell best = null;
            float bestD = float.MaxValue;
            foreach (Cell c in _cells)
            {
                float dx = Mathf.Abs(p.x - c.Centre.x);
                float dz = Mathf.Abs(p.z - c.Centre.z);
                float d = Mathf.Max(dx, dz);
                if (d < bestD)
                {
                    bestD = d;
                    best = c;
                }
            }
            return best;
        }

        /// <summary>Takes objects out of the merged meshes; returns the cells that need rebuilding.</summary>
        public List<Cell> Exclude(IEnumerable<MeshRenderer> renderers)
        {
            var dirty = new HashSet<Cell>();
            var wanted = new HashSet<MeshRenderer>(renderers);
            foreach (Cell cell in _cells)
            {
                foreach (Source s in cell.Sources)
                {
                    if (!wanted.Contains(s.Renderer) || !cell.Excluded.Add(s.Renderer))
                        continue;
                    dirty.Add(cell);
                }
            }
            // Mesh data must be read on the main thread.
            foreach (Cell cell in dirty)
            {
                foreach (Source s in cell.Sources)
                {
                    if (s.Data == null)
                        s.Data = MeshData.Of(s.Mesh);
                }
            }
            return new List<Cell>(dirty);
        }

        /// <summary>Worker-side: the new contents of each child of the cell. Uses every core: a town has millions of vertices.</summary>
        public static List<Rebuild> Build(Cell cell)
        {
            int n = cell.Children.Count;
            var result = new Rebuild[n];
            System.Threading.Tasks.Parallel.For(0, n, i =>
            {
                // Same material and layer, split only by the game's 65k limit: the first takes it all.
                bool duplicate = false;
                for (int j = 0; j < i; j++)
                    duplicate |= cell.Children[j].Material == cell.Children[i].Material && cell.Children[j].Layer == cell.Children[i].Layer;
                result[i] = BuildChild(cell, cell.Children[i], duplicate);
            });
            return new List<Rebuild>(result);
        }

        private static Rebuild BuildChild(Cell cell, Child child, bool empty)
        {
            int vertices = 0, indices = 0;
            var parts = new List<(Source source, int sub)>();
            if (!empty)
            {
                foreach (Source s in cell.Sources)
                {
                    if (s.Layer != child.Layer || s.Data == null || cell.Excluded.Contains(s.Renderer))
                        continue;
                    for (int sub = 0; sub < s.Materials.Length && sub < s.Data.Submeshes.Length; sub++)
                    {
                        if (s.Materials[sub] != child.Material)
                            continue;
                        parts.Add((s, sub));
                        vertices += s.Data.Vertices.Length;
                        indices += s.Data.Submeshes[sub].Length;
                    }
                }
            }

            var b = new Rebuild
            {
                Child = child,
                Vertices = new Vector3[vertices],
                Normals = new Vector3[vertices],
                Tangents = new Vector4[vertices],
                UV = new Vector2[vertices],
                Triangles = new int[indices],
            };
            Matrix4x4 toLocal = child.ToLocal;
            int v = 0, t = 0;
            foreach ((Source s, int sub) in parts)
            {
                Matrix4x4 m = toLocal * s.Matrix;
                Matrix4x4 nm = m.inverse.transpose;
                MeshData d = s.Data;
                bool normals = d.Normals.Length == d.Vertices.Length;
                bool tangents = d.Tangents.Length == d.Vertices.Length;
                bool uv = d.UV.Length == d.Vertices.Length;
                int start = v;
                for (int i = 0; i < d.Vertices.Length; i++, v++)
                {
                    b.Vertices[v] = m.MultiplyPoint3x4(d.Vertices[i]);
                    if (normals)
                        b.Normals[v] = nm.MultiplyVector(d.Normals[i]).normalized;
                    if (tangents)
                    {
                        Vector3 tangent = m.MultiplyVector(d.Tangents[i]).normalized;
                        b.Tangents[v] = new Vector4(tangent.x, tangent.y, tangent.z, d.Tangents[i].w);
                    }
                    if (uv)
                        b.UV[v] = d.UV[i];
                }
                int[] tris = d.Submeshes[sub];
                bool flip = m.determinant < 0f;
                for (int i = 0; i + 2 < tris.Length; i += 3, t += 3)
                {
                    b.Triangles[t] = start + tris[i];
                    b.Triangles[t + 1] = start + tris[flip ? i + 2 : i + 1];
                    b.Triangles[t + 2] = start + tris[flip ? i + 1 : i + 2];
                }
            }
            return b;
        }

        public static void Apply(List<Rebuild> rebuilds)
        {
            foreach (Rebuild b in rebuilds)
            {
                var mesh = new Mesh { name = "TABG Destruction merged", indexFormat = IndexFormat.UInt32 };
                mesh.vertices = b.Vertices;
                mesh.normals = b.Normals;
                mesh.tangents = b.Tangents;
                mesh.uv = b.UV;
                mesh.triangles = b.Triangles;
                mesh.RecalculateBounds();
                b.Child.Filter.sharedMesh = mesh;
                b.Child.Replaced = true;
                b.Child.Renderer.enabled = b.Triangles.Length > 0;
            }
        }

        public void Restore()
        {
            foreach (Cell cell in _cells)
            {
                cell.Excluded.Clear();
                foreach (Child child in cell.Children)
                {
                    if (!child.Replaced)
                        continue;
                    Object.Destroy(child.Filter.sharedMesh);
                    child.Filter.sharedMesh = child.Original;
                    child.Renderer.enabled = true;
                    child.Replaced = false;
                }
            }
        }

        /// <summary>Does the rebuild reproduce the game's merge? Logged once, per map.</summary>
        public string Statistics()
        {
            long game = 0, ours = 0;
            foreach (Cell cell in _cells)
            {
                foreach (Child child in cell.Children)
                {
                    if (child.Original != null)
                        game += child.Original.vertexCount;
                }
                foreach (Source s in cell.Sources)
                {
                    bool used = false;
                    foreach (Child child in cell.Children)
                    {
                        if (child.Layer == s.Layer && System.Array.IndexOf(s.Materials, child.Material) >= 0)
                            used = true;
                    }
                    if (used)
                        ours += s.Mesh.vertexCount;
                }
            }
            return $"{_cells.Count} merged cells: {game} vertices merged by the game, {ours} from sources";
        }
    }
}
