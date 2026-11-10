using System.Collections.Generic;
using FluentAssertions;
using TabgInstaller.Destruction;
using UnityEngine;
using Xunit;

namespace ClientMods.Tests
{
    public class FractureTests
    {
        private static Vtx V(float x, float y, float z) => new Vtx { P = new Vector3(x, y, z), N = Vector3.back };

        /// <summary>A flat wall in the XY plane, facing -Z.</summary>
        private static TriSoup Wall(float width, float height)
        {
            var soup = new TriSoup();
            soup.Add(V(0, 0, 0), V(0, height, 0), V(width, height, 0), 0);
            soup.Add(V(0, 0, 0), V(width, height, 0), V(width, 0, 0), 0);
            return soup;
        }

        private static float Area(TriSoup soup)
        {
            float area = 0f;
            for (int t = 0; t < soup.Triangles; t++)
                area += Vector3.Cross(soup.V[t * 3 + 1].P - soup.V[t * 3].P, soup.V[t * 3 + 2].P - soup.V[t * 3].P).magnitude * 0.5f;
            return area;
        }

        private static (Fragment[] fragments, List<Vector3> seeds) Cut(TriSoup soup, int salt = 5)
        {
            float spacing = 1.4f;
            List<Vector3> seeds = Fracturer.MakeSeeds(soup, ref spacing, 400, salt);
            return (Fracturer.Split(soup, seeds, spacing), seeds);
        }

        [Fact]
        public void Cutting_keeps_the_whole_surface()
        {
            TriSoup wall = Wall(12f, 6f);
            var (fragments, _) = Cut(wall);
            float total = 0f;
            foreach (Fragment f in fragments)
                total += Area(f.Soup);
            fragments.Should().HaveCountGreaterThan(10);
            total.Should().BeApproximately(Area(wall), 0.01f);
        }

        [Fact]
        public void Every_point_ends_up_in_its_nearest_cell()
        {
            var (fragments, seeds) = Cut(Wall(9f, 5f));
            for (int i = 0; i < fragments.Length; i++)
            {
                TriSoup soup = fragments[i].Soup;
                for (int t = 0; t < soup.Triangles; t++)
                {
                    Vector3 centroid = (soup.V[t * 3].P + soup.V[t * 3 + 1].P + soup.V[t * 3 + 2].P) / 3f;
                    int nearest = 0;
                    for (int s = 0; s < seeds.Count; s++)
                    {
                        if ((centroid - seeds[s]).sqrMagnitude < (centroid - seeds[nearest]).sqrMagnitude)
                            nearest = s;
                    }
                    nearest.Should().Be(i);
                }
            }
        }

        [Fact]
        public void Cuts_are_known_to_both_sides()
        {
            // Where three cells meet, a sliver of an edge can belong to the third one; that's fine.
            var (fragments, _) = Cut(Wall(9f, 5f));
            int edges = 0, matched = 0;
            for (int i = 0; i < fragments.Length; i++)
            {
                foreach (BorderEdge edge in fragments[i].Border)
                {
                    edge.Other.Should().NotBe(i);
                    edges++;
                    if (fragments[edge.Other].Border.Exists(b => b.Other == i))
                        matched++;
                }
            }
            edges.Should().BeGreaterThan(20);
            ((float)matched / edges).Should().BeGreaterThan(0.85f);
        }

        [Fact]
        public void Same_input_same_fragments_for_everyone()
        {
            var (a, _) = Cut(Wall(8f, 4f), 77);
            var (b, _) = Cut(Wall(8f, 4f), 77);
            a.Length.Should().Be(b.Length);
            for (int i = 0; i < a.Length; i++)
            {
                a[i].Seed.Should().Be(b[i].Seed);
                a[i].Soup.Triangles.Should().Be(b[i].Soup.Triangles);
            }
        }

        [Fact]
        public void Detailed_walls_far_from_the_origin_are_quick_and_lose_nothing()
        {
            // Real map coordinates, where float rounding is about 0.1 mm: this used to recurse forever.
            var offset = new Vector3(686.15f, 119.22f, 608.59f);
            var soup = new TriSoup();
            const int Quads = 60;
            float size = 16f / Quads;
            for (int x = 0; x < Quads; x++)
            {
                for (int y = 0; y < Quads; y++)
                {
                    Vtx a = V(x * size, y * size, 0), b = V(x * size, (y + 1) * size, 0);
                    Vtx c = V((x + 1) * size, (y + 1) * size, 0), d = V((x + 1) * size, y * size, 0);
                    a.P += offset; b.P += offset; c.P += offset; d.P += offset;
                    soup.Add(a, b, c, 0);
                    soup.Add(a, c, d, 0);
                }
            }
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var (fragments, _) = Cut(soup);
            watch.ElapsedMilliseconds.Should().BeLessThan(1500);
            float total = 0f;
            int triangles = 0;
            foreach (Fragment f in fragments)
            {
                total += Area(f.Soup);
                triangles += f.Soup.Triangles;
            }
            total.Should().BeApproximately(Area(soup), 0.05f);
            triangles.Should().BeLessThan(soup.Triangles * 4);
        }

        [Fact]
        public void Thin_walls_get_one_cell_through_their_thickness()
        {
            var soup = Wall(6f, 3f);
            soup.Add(V(0, 0, 0.2f), V(6, 3, 0.2f), V(0, 3, 0.2f), 0);
            soup.Add(V(0, 0, 0.2f), V(6, 0, 0.2f), V(6, 3, 0.2f), 0);
            float spacing = 1.4f;
            int oneFace = Fracturer.MakeSeeds(Wall(6f, 3f), ref spacing, 400, 1).Count;
            spacing = 1.4f;
            int twoFaces = Fracturer.MakeSeeds(soup, ref spacing, 400, 1).Count;
            twoFaces.Should().BeLessOrEqualTo(oneFace * 2);
        }
    }

    public class SupportGraphTests
    {
        private static Node Box(float x, float y, float z, bool anchored = false, float mass = 1f, float capacity = 1000f, float health = 100f) => new Node
        {
            Min = new Vector3(x, y, z),
            Max = new Vector3(x + 1, y + 1, z + 1),
            Mass = mass,
            Health = health,
            Capacity = capacity,
            Anchored = anchored,
        };

        private static SupportGraph Tower(int height, float capacity = 1000f)
        {
            var graph = new SupportGraph();
            for (int y = 0; y < height; y++)
            {
                graph.Add(Box(0, y, 0, y == 0, 1f, capacity));
                if (y > 0)
                    graph.Link(y - 1, y);
            }
            return graph;
        }

        [Fact]
        public void A_tower_stands_until_its_base_goes()
        {
            SupportGraph tower = Tower(10);
            var deaths = new List<Death>();
            int groups = 0;
            tower.Settle(deaths, ref groups);
            deaths.Should().BeEmpty();

            tower.Kill(4, DeathCause.Blast, -1, deaths);
            tower.Settle(deaths, ref groups);
            tower.Nodes[3].Alive.Should().BeTrue();
            for (int y = 5; y < 10; y++)
                tower.Nodes[y].Alive.Should().BeFalse();
            deaths.Should().Contain(d => d.Cause == DeathCause.Unsupported);
            deaths.FindAll(d => d.Cause == DeathCause.Unsupported).ConvertAll(d => d.Group).Should().AllBeEquivalentTo(0);
        }

        [Fact]
        public void What_stands_when_the_map_loads_stays_up_until_damaged()
        {
            // A floating group (it rests on something the graph doesn't know) and an overloaded column.
            var graph = new SupportGraph();
            graph.Add(Box(0, 5, 0, false));
            graph.Add(Box(0, 6, 0, false));
            graph.Link(0, 1);
            graph.Add(Box(5, 0, 0, true, 1f, capacity: 2f));
            graph.Add(Box(5, 1, 0, false, 100f, capacity: 500f));
            graph.Link(2, 3);
            graph.Stabilise(0);

            var deaths = new List<Death>();
            int groups = 0;
            graph.Settle(deaths, ref groups);
            deaths.Should().BeEmpty();

            // Its lowest part became the base, so losing that still brings the rest down.
            graph.Kill(0, DeathCause.Blast, -1, deaths);
            graph.Settle(deaths, ref groups);
            graph.Nodes[1].Alive.Should().BeFalse();
            graph.Nodes[2].Alive.Should().BeTrue();
        }

        [Fact]
        public void A_blast_only_reaches_its_radius_and_damages_the_rim()
        {
            var graph = new SupportGraph();
            graph.Add(Box(0, 0, 0, true));
            graph.Add(Box(4, 0, 0, true));
            graph.Add(Box(20, 0, 0, true));
            var deaths = new List<Death>();
            graph.Blast(new Vector3(0.5f, 0.5f, 0.5f), 5f, 40f, 1, deaths);
            graph.Nodes[0].Alive.Should().BeFalse("it was at the centre");
            graph.Nodes[1].Alive.Should().BeTrue();
            graph.Nodes[1].Health.Should().BeLessThan(100f, "it was within the radius");
            graph.Nodes[2].Health.Should().Be(100f);
        }

        [Fact]
        public void Too_much_weight_breaks_the_weakest_part_and_brings_down_the_rest()
        {
            // One thin column under a heavy block: the block is fine, the column isn't.
            var graph = new SupportGraph();
            graph.Add(Box(0, 0, 0, true, 1f, capacity: 100f));
            graph.Add(Box(0, 1, 0, false, 1f, capacity: 100f));
            graph.Add(Box(0, 2, 0, false, 500f, capacity: 10000f));
            graph.Link(0, 1);
            graph.Link(1, 2);
            var deaths = new List<Death>();
            int groups = 0;
            graph.Settle(deaths, ref groups);
            graph.Nodes[0].Alive.Should().BeFalse("everything rests on it");
            graph.Nodes[1].Alive.Should().BeFalse();
            graph.Nodes[2].Alive.Should().BeFalse();
        }

        [Fact]
        public void Shared_weight_spreads_over_all_supports()
        {
            // The same block on four columns is fine.
            var graph = new SupportGraph();
            for (int i = 0; i < 4; i++)
                graph.Add(Box(i * 2, 0, 0, true, 1f, capacity: 200f));
            int block = graph.Add(Box(0, 1, 0, false, 500f, capacity: 10000f));
            for (int i = 0; i < 4; i++)
                graph.Link(i, block);
            var deaths = new List<Death>();
            int groups = 0;
            graph.Settle(deaths, ref groups);
            deaths.Should().BeEmpty();
        }
    }

    public class DestructionProtocolTests
    {
        [Fact]
        public void Hello_is_what_the_server_expects()
        {
            DestructionProtocol.Hello().Should().Equal((byte)'D', (byte)'S', (byte)'T', (byte)'R', 1, 1);
        }

        [Fact]
        public void A_blast_reads_back()
        {
            var data = new byte[] { (byte)'D', (byte)'S', (byte)'T', (byte)'R', 1, 5,
                7, 0, 0, 0,
                0, 0, 128, 63, 0, 0, 0, 64, 0, 0, 64, 64,
                0, 0, 128, 64, 0, 0, 160, 65 };
            DestructionProtocol.TryRead(data, out DestructionOp op, out Blast blast).Should().BeTrue();
            op.Should().Be(DestructionOp.Blast);
            blast.Seq.Should().Be(7u);
            blast.Position.Should().Be(new Vector3(1f, 2f, 3f));
            blast.Radius.Should().Be(4f);
            blast.Power.Should().Be(20f);
        }

        [Fact]
        public void Other_messages_are_ignored()
        {
            DestructionProtocol.TryRead(new byte[] { 1, 2, 3 }, out _, out _).Should().BeFalse();
            DestructionProtocol.TryRead(new byte[] { (byte)'D', (byte)'S', (byte)'T', (byte)'R', 9, 5 }, out _, out _).Should().BeFalse();
        }
    }
}
