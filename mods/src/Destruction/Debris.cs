using System.Collections.Generic;
using UnityEngine;

namespace TabgInstaller.Destruction
{
    /// <summary>
    /// Falling pieces and dust. Not synchronised (each client simulates its own), but solid: players
    /// stand on them, push them and shoot them. A piece stays while anyone is near and goes once
    /// nobody has been close to it for a while.
    /// </summary>
    internal sealed class DebrisSpawner
    {
        private const int MaxAlive = 450;
        private const float NearDistance = 90f;
        private const float GoneAfter = 25f;
        private const float CheckEvery = 2f;
        private const int MaxConvexVertices = 2500;

        private readonly List<DebrisLife> _alive = new List<DebrisLife>();
        private readonly List<Vector3> _players = new List<Vector3>();
        private readonly Transform _root;
        private Material _dust;
        private bool _lookedForDust;
        private float _nextCheck;

        public DebrisSpawner(Transform root)
        {
            _root = root;
        }

        public void Spawn(List<DebrisData> debris, List<Vector3> dust, WorldIndex index)
        {
            _alive.RemoveAll(g => g == null);
            foreach (DebrisData d in debris)
            {
                if (_alive.Count >= MaxAlive)
                    DropFarthest();
                Create(d, index);
            }
            if (DestructionPlugin.Instance.Dust.Value)
            {
                foreach (Vector3 p in dust)
                    CreateDust(p);
            }
        }

        public void Clear()
        {
            foreach (DebrisLife d in _alive)
            {
                if (d != null)
                    Object.Destroy(d.gameObject);
            }
            _alive.Clear();
        }

        /// <summary>Main thread, every frame: removes what nobody has been near for a while.</summary>
        public void Tick()
        {
            if (Time.time < _nextCheck || _alive.Count == 0)
                return;
            _nextCheck = Time.time + CheckEvery;
            _alive.RemoveAll(g => g == null);
            FindPlayers();
            foreach (DebrisLife d in _alive)
            {
                Vector3 at = d.transform.position;
                if (at.y < -200f)
                {
                    d.Vanish();
                    continue;
                }
                foreach (Vector3 p in _players)
                {
                    if ((p - at).sqrMagnitude < NearDistance * NearDistance)
                    {
                        d.LastSeen = Time.time;
                        break;
                    }
                }
                if (Time.time - d.LastSeen > GoneAfter)
                    d.Vanish();
            }
        }

        private void FindPlayers()
        {
            _players.Clear();
            if (Camera.main != null)
                _players.Add(Camera.main.transform.position);
            foreach (Player p in Object.FindObjectsOfType<Player>())
            {
                if (p == null)
                    continue;
                Transform t = p.Torso != null ? p.Torso.transform : p.transform;
                _players.Add(t.position);
            }
        }

        private void DropFarthest()
        {
            Vector3 viewer = Camera.main != null ? Camera.main.transform.position : Vector3.zero;
            int far = -1;
            float best = -1f;
            for (int i = 0; i < _alive.Count; i++)
            {
                if (_alive[i] == null || _alive[i].Vanishing)
                    continue;
                float d = (_alive[i].transform.position - viewer).sqrMagnitude;
                if (d > best)
                {
                    best = d;
                    far = i;
                }
            }
            if (far < 0)
                return;
            Object.Destroy(_alive[far].gameObject);
            _alive.RemoveAt(far);
        }

        private void Create(DebrisData d, WorldIndex index)
        {
            var go = new GameObject("TABG Destruction debris") { layer = 0 };
            go.transform.SetParent(_root, false);
            Mesh mesh;
            Material[] materials;
            bool ownMesh;
            bool batched = false;
            if (d.Whole != null)
            {
                Piece p = d.Whole;
                if (p.Batched)
                {
                    // The batch's vertices are already in place; this object sits at the piece's centre.
                    go.transform.position = d.Centre;
                }
                else
                {
                    go.transform.SetPositionAndRotation(p.Matrix.GetColumn(3), p.Matrix.rotation);
                    go.transform.localScale = p.Matrix.lossyScale;
                }
                mesh = p.Mesh;
                materials = p.Renderer != null ? p.Renderer.sharedMaterials : new Material[0];
                ownMesh = false;
                batched = p.Batched;
            }
            else
            {
                go.transform.position = d.Centre;
                mesh = MeshBuilder.ToMesh(d.Mesh);
                materials = new Material[d.Mesh.MaterialIds.Length];
                for (int i = 0; i < materials.Length; i++)
                    materials[i] = d.Mesh.MaterialIds[i] >= 0 ? index.Materials[d.Mesh.MaterialIds[i]] : null;
                ownMesh = true;
            }
            if (batched)
            {
                // Part of a static batch: draw this object's submeshes of the shared mesh, moved by this transform.
                var source = (MeshRenderer)d.Whole.Renderer;
                Matrix4x4 offset = Matrix4x4.Translate(-d.Centre) * StaticBatch.Matrix(source);
                go.AddComponent<BatchedSlice>().Begin(mesh, source.subMeshStartIndex, materials, go.layer, offset);
            }
            else
            {
                go.AddComponent<MeshFilter>().sharedMesh = mesh;
                go.AddComponent<MeshRenderer>().sharedMaterials = materials;
            }

            if (batched || !AddConvexCollider(go, mesh))
            {
                var box = go.AddComponent<BoxCollider>();
                if (batched)
                {
                    box.center = Vector3.zero;
                    box.size = d.Size;
                }
                else if (d.Whole != null)
                {
                    box.center = mesh.bounds.center;
                    box.size = mesh.bounds.size;
                }
                else
                {
                    box.size = new Vector3(Mathf.Max(d.Size.x, 0.2f), Mathf.Max(d.Size.y, 0.2f), Mathf.Max(d.Size.z, 0.2f)) * 0.85f;
                }
            }
            var body = go.AddComponent<Rigidbody>();
            body.mass = d.Mass;
            body.velocity = d.Velocity;
            body.angularVelocity = d.Spin;
            body.maxAngularVelocity = 7f;
            body.collisionDetectionMode = CollisionDetectionMode.Discrete;
            var life = go.AddComponent<DebrisLife>();
            life.Begin(ownMesh ? mesh : null, this);
            _alive.Add(life);
        }

        /// <summary>A collider in the real shape, where Unity can make one; false to fall back to a box.</summary>
        private static bool AddConvexCollider(GameObject go, Mesh mesh)
        {
            if (mesh == null || !mesh.isReadable || mesh.vertexCount < 4 || mesh.vertexCount > MaxConvexVertices)
                return false;
            Vector3 size = mesh.bounds.size;
            if (Mathf.Min(size.x, Mathf.Min(size.y, size.z)) < 0.02f)
                return false;
            try
            {
                var c = go.AddComponent<MeshCollider>();
                c.convex = true;
                c.sharedMesh = mesh;
                return true;
            }
            catch (System.Exception)
            {
                return false;
            }
        }

        internal void Puff(Vector3 at) => CreateDust(at);

        private void CreateDust(Vector3 at)
        {
            Material material = DustMaterial();
            if (material == null)
                return;
            var go = new GameObject("TABG Destruction dust");
            go.transform.SetParent(_root, false);
            go.transform.position = at;
            var ps = go.AddComponent<ParticleSystem>();
            ps.Stop(true, ParticleSystemStopBehavior.StopEmittingAndClear);
            ParticleSystem.MainModule main = ps.main;
            main.loop = false;
            main.duration = 0.4f;
            main.startLifetime = new ParticleSystem.MinMaxCurve(2.2f, 4.5f);
            main.startSpeed = new ParticleSystem.MinMaxCurve(0.8f, 3f);
            main.startSize = new ParticleSystem.MinMaxCurve(1.5f, 4f);
            main.startColor = new Color(0.72f, 0.66f, 0.57f, 0.55f);
            main.gravityModifier = 0.03f;
            main.simulationSpace = ParticleSystemSimulationSpace.World;
            main.maxParticles = 30;
            ParticleSystem.EmissionModule emission = ps.emission;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 18) });
            ParticleSystem.ShapeModule shape = ps.shape;
            shape.shapeType = ParticleSystemShapeType.Sphere;
            shape.radius = 1.3f;
            ParticleSystem.SizeOverLifetimeModule size = ps.sizeOverLifetime;
            size.enabled = true;
            size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 0.6f, 1f, 2.2f));
            ParticleSystem.ColorOverLifetimeModule colour = ps.colorOverLifetime;
            colour.enabled = true;
            var fade = new Gradient();
            fade.SetKeys(
                new[] { new GradientColorKey(Color.white, 0f), new GradientColorKey(Color.white, 1f) },
                new[] { new GradientAlphaKey(0f, 0f), new GradientAlphaKey(1f, 0.1f), new GradientAlphaKey(0f, 1f) });
            colour.color = fade;
            go.GetComponent<ParticleSystemRenderer>().sharedMaterial = material;
            ps.Play();
            Object.Destroy(go, 6f);
        }

        /// <summary>A soft round puff, made here so it doesn't depend on any of the game's textures.</summary>
        private Material DustMaterial()
        {
            if (_lookedForDust)
                return _dust;
            _lookedForDust = true;
            Shader shader = Shader.Find("Particles/Alpha Blended") ?? Shader.Find("Sprites/Default");
            if (shader == null)
                return null;
            const int Size = 64;
            var texture = new Texture2D(Size, Size, TextureFormat.RGBA32, false) { wrapMode = TextureWrapMode.Clamp };
            var pixels = new Color32[Size * Size];
            for (int y = 0; y < Size; y++)
            {
                for (int x = 0; x < Size; x++)
                {
                    float dx = (x + 0.5f) / Size * 2f - 1f, dy = (y + 0.5f) / Size * 2f - 1f;
                    float edge = Mathf.Clamp01(1f - Mathf.Sqrt(dx * dx + dy * dy));
                    float alpha = edge * edge * (3f - 2f * edge);
                    pixels[y * Size + x] = new Color32(255, 255, 255, (byte)(alpha * 255f));
                }
            }
            texture.SetPixels32(pixels);
            texture.Apply(false, true);
            _dust = new Material(shader) { mainTexture = texture };
            return _dust;
        }
    }

    /// <summary>Draws a range of submeshes of a shared (statically batched) mesh with this object's transform.</summary>
    internal sealed class BatchedSlice : MonoBehaviour
    {
        private Mesh _mesh;
        private int _first;
        private Material[] _materials;
        private int _layer;
        private Matrix4x4 _offset;

        /// <param name="offset">From the batch's space to this object's space.</param>
        public void Begin(Mesh mesh, int first, Material[] materials, int layer, Matrix4x4 offset)
        {
            _mesh = mesh;
            _first = first;
            _materials = materials;
            _layer = layer;
            _offset = offset;
        }

        private void LateUpdate()
        {
            if (_mesh == null)
                return;
            Matrix4x4 m = transform.localToWorldMatrix * _offset;
            for (int i = 0; i < _materials.Length; i++)
            {
                if (_materials[i] != null && _first + i < _mesh.subMeshCount)
                    Graphics.DrawMesh(_mesh, m, _materials[i], _layer, null, _first + i);
            }
        }
    }

    internal sealed class DebrisLife : MonoBehaviour
    {
        private const float ShrinkTime = 1.2f;

        public float LastSeen;
        public bool Vanishing => _vanishAt > 0f;
        private float _vanishAt;
        private Mesh _mesh;
        private DebrisSpawner _spawner;
        private bool _puffed;

        /// <param name="ownMesh">A mesh made for this piece, destroyed with it; null when it shows a map mesh.</param>
        public void Begin(Mesh ownMesh, DebrisSpawner spawner)
        {
            LastSeen = Time.time;
            _mesh = ownMesh;
            _spawner = spawner;
        }

        public void Vanish()
        {
            if (_vanishAt <= 0f)
                _vanishAt = Time.time + ShrinkTime;
        }

        private void OnCollisionEnter(Collision c)
        {
            if (_puffed || c.relativeVelocity.sqrMagnitude < 16f || !DestructionPlugin.Instance.Dust.Value)
                return;
            _puffed = true;
            _spawner.Puff(transform.position);
        }

        private float _born = -1f;

        private void Update()
        {
            if (_born < 0f)
                _born = Time.time;
            else if (_born > 0f && Time.time - _born > 3f && DestructionPlugin.Instance.Verbose)
            {
                _born = 0f;
                var r = GetComponent<Renderer>();
                Debug.Log($"[Destruction] debris after 3 s: at {transform.position} tilt {Vector3.Angle(transform.up, Vector3.up):F0} deg, "
                    + $"visible {r != null && r.isVisible}, bounds {(r != null ? r.bounds.ToString() : "-")}, sleeping {GetComponent<Rigidbody>()?.IsSleeping()}");
            }
            if (_vanishAt <= 0f)
                return;
            float left = _vanishAt - Time.time;
            transform.localScale *= Mathf.Clamp01(1f - Time.deltaTime / Mathf.Max(left, 0.05f));
            if (left <= 0f)
                Destroy(gameObject);
        }

        private void OnDestroy()
        {
            if (_mesh != null)
                Destroy(_mesh);
        }
    }
}
