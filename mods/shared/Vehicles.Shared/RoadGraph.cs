using System;
using System.Collections.Generic;
namespace TabgInstaller.Vehicles
{
    internal struct RoadPosition
    {
        internal float X, Y, Z;
        internal RoadPosition(float x, float y, float z) { X = x; Y = y; Z = z; }
        internal float Distance(RoadPosition other) { float x = X-other.X, y = Y-other.Y, z = Z-other.Z; return (float)Math.Sqrt(x*x+y*y+z*z); }
    }
    internal sealed class RoadGraph
    {
        internal readonly List<RoadPosition> Points = new List<RoadPosition>();
        private readonly List<List<int>> _edges = new List<List<int>>();
        internal int Add(RoadPosition point) { Points.Add(point); _edges.Add(new List<int>()); return Points.Count-1; }
        internal void Connect(int a, int b) { if (a==b) return; if (!_edges[a].Contains(b)) _edges[a].Add(b); if (!_edges[b].Contains(a)) _edges[b].Add(a); }
        internal int Nearest(RoadPosition point)
        {
            int result=-1; float best=float.MaxValue;
            for (int i=0;i<Points.Count;i++) { float distance=Points[i].Distance(point); if(distance<best) {best=distance;result=i;} }
            return result;
        }
        internal List<int> Route(int start, int end)
        {
            if(start<0 || end<0 || start>=Points.Count || end>=Points.Count) return null;
            var distance=new float[Points.Count]; var previous=new int[Points.Count];
            for(int i=0;i<distance.Length;i++) {distance[i]=float.PositiveInfinity;previous[i]=-1;}
            var queue=new SortedSet<Tuple<float,int>>(Comparer<Tuple<float,int>>.Create((a,b)=> { int c=a.Item1.CompareTo(b.Item1);return c!=0?c:a.Item2.CompareTo(b.Item2); }));
            distance[start]=0; queue.Add(Tuple.Create(0f,start));
            while(queue.Count>0)
            {
                var current=queue.Min;queue.Remove(current); int node=current.Item2;
                if(node==end) {var path=new List<int>();for(int n=end;n!=-1;n=previous[n])path.Add(n);path.Reverse();return path;}
                foreach(int next in _edges[node])
                {
                    float cost=distance[node]+Points[node].Distance(Points[next]);
                    if(cost>=distance[next])continue;
                    queue.Remove(Tuple.Create(distance[next],next)); distance[next]=cost;previous[next]=node;queue.Add(Tuple.Create(cost,next));
                }
            }
            return null;
        }
    }
}
