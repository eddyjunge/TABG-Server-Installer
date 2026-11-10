using System.Collections.Generic;
using UnityEngine;

namespace TabgInstaller.Destruction
{
    internal sealed class Node
    {
        public Vector3 Min;
        public Vector3 Max;
        public float Mass;
        /// <summary>Hits left, in blast-power units.</summary>
        public float Health;
        /// <summary>The weight it can carry before it gives way.</summary>
        public float Capacity;
        public bool Anchored;
        public bool Alive = true;
        public int Piece;
        public int Fragment = -1;
        public readonly List<int> Links = new List<int>();

        public Vector3 Centre => (Min + Max) * 0.5f;
    }

    internal enum DeathCause { Blast, Overloaded, Unsupported }

    internal struct Death
    {
        public int Node;
        public DeathCause Cause;
        /// <summary>Everything unsupported that came down together shares a group.</summary>
        public int Group;
    }

    /// <summary>
    /// What stands on what. Nodes are building parts; a part stays up if a chain of parts connects
    /// it to something anchored in the ground, and the weight resting on each part is no more than
    /// it can bear. Pure logic, in a fixed order, so all clients agree.
    /// </summary>
    internal sealed class SupportGraph
    {
        /// <summary>Parts in the inner part of the blast are gone regardless of how strong they are.</summary>
        public const float CoreFraction = 0.55f;
        private const int MaxSettlePasses = 16;
        /// <summary>How far above its lowest point a floating group counts as resting on something.</summary>
        private const float FloatingGroupBase = 0.8f;
        /// <summary>Spare strength every part starts with, so a little damage nearby doesn't bring it down.</summary>
        private const float StartingMargin = 1.3f;

        public readonly List<Node> Nodes = new List<Node>();

        public int Add(Node node)
        {
            Nodes.Add(node);
            return Nodes.Count - 1;
        }

        public void Link(int a, int b)
        {
            if (a == b || Nodes[a].Links.Contains(b))
                return;
            Nodes[a].Links.Add(b);
            Nodes[b].Links.Add(a);
        }

        public static bool Touching(Node a, Node b, float margin) =>
            a.Min.x <= b.Max.x + margin && b.Min.x <= a.Max.x + margin
            && a.Min.y <= b.Max.y + margin && b.Min.y <= a.Max.y + margin
            && a.Min.z <= b.Max.z + margin && b.Min.z <= a.Max.z + margin;

        public static float Distance(Node n, Vector3 p)
        {
            Vector3 closest = Vector3.Max(n.Min, Vector3.Min(p, n.Max));
            return (closest - p).magnitude;
        }

        /// <summary>Hurts everything in the sphere. Ragged edge: each part's distance gets a little noise.</summary>
        public void Blast(Vector3 centre, float radius, float power, uint salt, List<Death> deaths)
        {
            for (int i = 0; i < Nodes.Count; i++)
            {
                Node n = Nodes[i];
                if (!n.Alive)
                    continue;
                float d = Distance(n, centre);
                if (d > radius)
                    continue;
                uint h = Hash.Mix((uint)i, salt, 7, 11);
                float noisy = d * (0.8f + 0.4f * Hash.Unit(ref h));
                float t = Mathf.Clamp01(noisy / radius);
                if (t <= CoreFraction)
                    n.Health = 0f;
                else
                    n.Health -= power * (1f - (t - CoreFraction) / (1f - CoreFraction));
                if (n.Health <= 0f)
                    Kill(i, DeathCause.Blast, -1, deaths);
            }
        }

        public void Kill(int node, DeathCause cause, int group, List<Death> deaths)
        {
            if (!Nodes[node].Alive)
                return;
            Nodes[node].Alive = false;
            deaths.Add(new Death { Node = node, Cause = cause, Group = group });
        }

        /// <summary>
        /// Declares the current state stable, for nodes from <paramref name="firstNew"/> on. What stands
        /// in the map stands for reasons this graph can't see (a rock, a part outside the building), so
        /// only damage may bring things down: a group with no anchor gets one at its lowest point, and
        /// no part starts out overloaded.
        /// </summary>
        public void Stabilise(int firstNew)
        {
            AnchorFloatingGroups(firstNew);
            int[] depth = Depths(out _);
            float[] load = Loads(depth, out _);
            for (int i = firstNew; i < Nodes.Count; i++)
            {
                if (Nodes[i].Alive && depth[i] >= 0)
                    Nodes[i].Capacity = Mathf.Max(Nodes[i].Capacity, load[i] * StartingMargin);
            }
        }

        private void AnchorFloatingGroups(int firstNew)
        {
            int[] depth = Depths(out _);
            var seen = new bool[Nodes.Count];
            for (int i = firstNew; i < Nodes.Count; i++)
            {
                if (!Nodes[i].Alive || depth[i] >= 0 || seen[i])
                    continue;
                var group = new List<int>();
                var stack = new Stack<int>();
                stack.Push(i);
                seen[i] = true;
                float lowest = float.MaxValue;
                while (stack.Count > 0)
                {
                    int n = stack.Pop();
                    group.Add(n);
                    lowest = Mathf.Min(lowest, Nodes[n].Min.y);
                    foreach (int m in Nodes[n].Links)
                    {
                        if (Nodes[m].Alive && !seen[m])
                        {
                            seen[m] = true;
                            stack.Push(m);
                        }
                    }
                }
                foreach (int n in group)
                {
                    if (Nodes[n].Min.y <= lowest + FloatingGroupBase)
                        Nodes[n].Anchored = true;
                }
            }
        }

        /// <summary>Takes down everything that no longer stands. Call after <see cref="Blast"/>.</summary>
        public void Settle(List<Death> deaths, ref int nextGroup)
        {
            for (int pass = 0; pass < MaxSettlePasses; pass++)
            {
                int[] depth = Depths(out bool anyAnchor);
                bool changed = DropUnsupported(depth, anyAnchor, deaths, ref nextGroup);
                if (!changed)
                    changed = BreakOverloaded(depth, deaths);
                if (!changed)
                    return;
            }
        }

        private int[] Depths(out bool anyAnchor)
        {
            var depth = new int[Nodes.Count];
            var queue = new Queue<int>();
            anyAnchor = false;
            for (int i = 0; i < Nodes.Count; i++)
            {
                depth[i] = -1;
                if (Nodes[i].Alive && Nodes[i].Anchored)
                {
                    depth[i] = 0;
                    queue.Enqueue(i);
                    anyAnchor = true;
                }
            }
            while (queue.Count > 0)
            {
                int n = queue.Dequeue();
                foreach (int m in Nodes[n].Links)
                {
                    if (Nodes[m].Alive && depth[m] < 0)
                    {
                        depth[m] = depth[n] + 1;
                        queue.Enqueue(m);
                    }
                }
            }
            return depth;
        }

        private bool DropUnsupported(int[] depth, bool anyAnchor, List<Death> deaths, ref int nextGroup)
        {
            bool changed = false;
            var seen = new bool[Nodes.Count];
            for (int i = 0; i < Nodes.Count; i++)
            {
                if (!Nodes[i].Alive || depth[i] >= 0 || seen[i])
                    continue;
                int group = nextGroup++;
                var stack = new Stack<int>();
                stack.Push(i);
                seen[i] = true;
                while (stack.Count > 0)
                {
                    int n = stack.Pop();
                    Kill(n, DeathCause.Unsupported, group, deaths);
                    foreach (int m in Nodes[n].Links)
                    {
                        if (Nodes[m].Alive && !seen[m])
                        {
                            seen[m] = true;
                            stack.Push(m);
                        }
                    }
                }
                changed = true;
            }
            return changed;
        }

        /// <summary>
        /// Weight flows down the shortest paths to the ground, shared between all the parts one step
        /// closer to it. The first part that can't bear its share breaks, then everything is
        /// recomputed: a building gives way at its weakest point, not everywhere at once.
        /// </summary>
        private bool BreakOverloaded(int[] depth, List<Death> deaths)
        {
            Loads(depth, out int worst);
            if (worst < 0)
                return false;
            Kill(worst, DeathCause.Overloaded, -1, deaths);
            return true;
        }

        private float[] Loads(int[] depth, out int worst)
        {
            var order = new List<int>();
            for (int i = 0; i < Nodes.Count; i++)
            {
                if (Nodes[i].Alive && depth[i] >= 0)
                    order.Add(i);
            }
            order.Sort((a, b) => depth[b] != depth[a] ? depth[b] - depth[a] : a - b);

            var load = new float[Nodes.Count];
            worst = -1;
            float worstRatio = 1f;
            foreach (int n in order)
            {
                load[n] += Nodes[n].Mass;
                float ratio = load[n] / Nodes[n].Capacity;
                if (ratio > worstRatio)
                {
                    worstRatio = ratio;
                    worst = n;
                }
                int parents = 0;
                foreach (int m in Nodes[n].Links)
                {
                    if (Nodes[m].Alive && depth[m] == depth[n] - 1)
                        parents++;
                }
                if (parents == 0)
                    continue;
                float share = load[n] / parents;
                foreach (int m in Nodes[n].Links)
                {
                    if (Nodes[m].Alive && depth[m] == depth[n] - 1)
                        load[m] += share;
                }
            }
            return load;
        }
    }
}
