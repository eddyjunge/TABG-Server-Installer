using System.Collections.Generic;
using UnityEngine;

namespace TabgInstaller.Destruction
{
    /// <summary>
    /// Falling pieces and dust. Local eye candy: not synchronised, and on a layer that can't touch
    /// the local player, so what you see fall can't move you differently from everyone else.
    /// </summary>
    internal sealed class DebrisSpawner
    {
        private const int DebrisLayer = 22; // DontCollideWithLocalPlayer
        private const int MaxAlive = 220;

        private readonly List<GameObject> _alive = new List<GameObject>();
        private readonly Transform _root;
        private Material _dust;
        private bool _lookedForDust;

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
                    break;
                Create(d, index);
            }
            if (DestructionPlugin.Instance.Dust.Value)
            {
                foreach (Vector3 p in dust)
                    CreateDust(p);
            }
        }

        private void Create(DebrisData d, WorldIndex index)
        {
            var go = new GameObject("TABG Destruction debris") { layer = DebrisLayer };
            go.transform.SetParent(_root, false);
            go.transform.position = d.Centre;
            Mesh mesh = MeshBuilder.ToMesh(d.Mesh);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = go.AddComponent<MeshRenderer>();
            var materials = new Material[d.Mesh.MaterialIds.Length];
            for (int i = 0; i < materials.Length; i++)
                materials[i] = d.Mesh.MaterialIds[i] >= 0 ? index.Materials[d.Mesh.MaterialIds[i]] : null;
            renderer.sharedMaterials = materials;

            Vector3 size = new Vector3(Mathf.Max(d.Size.x, 0.2f), Mathf.Max(d.Size.y, 0.2f), Mathf.Max(d.Size.z, 0.2f));
            go.AddComponent<BoxCollider>().size = size * 0.85f;
            var body = go.AddComponent<Rigidbody>();
            body.mass = d.Mass;
            body.velocity = d.Velocity;
            body.angularVelocity = d.Spin;
            body.collisionDetectionMode = CollisionDetectionMode.Discrete;
            go.AddComponent<DebrisLife>().Begin(d.Life, mesh, this);
            _alive.Add(go);
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

    internal sealed class DebrisLife : MonoBehaviour
    {
        private const float ShrinkTime = 1.2f;

        private float _dies;
        private Mesh _mesh;
        private DebrisSpawner _spawner;
        private bool _puffed;

        public void Begin(float life, Mesh mesh, DebrisSpawner spawner)
        {
            _dies = Time.time + life;
            _mesh = mesh;
            _spawner = spawner;
        }

        private void OnCollisionEnter(Collision c)
        {
            if (_puffed || c.relativeVelocity.sqrMagnitude < 16f || !DestructionPlugin.Instance.Dust.Value)
                return;
            _puffed = true;
            _spawner.Puff(transform.position);
        }

        private void Update()
        {
            float left = _dies - Time.time;
            if (left < ShrinkTime)
                transform.localScale = Vector3.one * Mathf.Max(0.02f, left / ShrinkTime);
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
