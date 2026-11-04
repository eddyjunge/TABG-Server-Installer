using System;
using System.Collections.Generic;
using System.Linq;
using EasyRoads3Dv3;
using UnityEngine;
using UnityEngine.SceneManagement;
using TabgInstaller.Vehicles;

namespace TabgInstaller.FlyingControls
{
    internal static class RoadNetwork
    {
        private static RoadGraph _graph;
        private static int _scene = -1;
        internal static RoadPosition Point(Vector3 v) => new RoadPosition(v.x,v.y,v.z);
        internal static Vector3 Vector(RoadPosition p) => new Vector3(p.X,p.Y,p.Z);
        private sealed class End { internal int Node; internal UnityEngine.Object Crossing; internal int Road; }
        internal static List<Vector3> Route(Vector3 start, Vector3 destination)
        {
            int scene=SceneManager.GetActiveScene().handle;
            if(_graph==null || scene!=_scene) Build(scene);
            int first=_graph.Nearest(Point(start)), last=_graph.Nearest(Point(destination));
            var route=_graph.Route(first,last);
            if(route==null)return null;
            var result=route.Select(i=>Vector(_graph.Points[i])).ToList();
            // Only the approach/departure leave the street; never shortcut the street route.
            if((destination-result[result.Count-1]).sqrMagnitude>4f)result.Add(destination);
            return result;
        }
        private static bool IsRoad(Transform transform)
        {
            for(int i=0;i<3 && transform;i++,transform=transform.parent)
            {
                string name=transform.name.ToLowerInvariant();
                if(name.Contains("road") || name.Contains("crossing") || name.Contains("junction"))return true;
            }
            return false;
        }
        private static void Build(int scene)
        {
            _scene=scene;_graph=new RoadGraph();var ends=new List<End>();int number=0;
            foreach(var road in Resources.FindObjectsOfTypeAll<ERModularRoad>())
            {
                if(!road.gameObject.scene.IsValid() || road.splinePoints==null || road.splinePoints.Count<2)continue;
                int first=-1,last=-1;Vector3 previous=default(Vector3);
                for(int i=0;i<road.splinePoints.Count;i++)
                {
                    Vector3 p=road.splinePoints[i];
                    if(last>=0 && Vector3.Distance(previous,p)<3f && i<road.splinePoints.Count-1)continue;
                    int node=_graph.Add(Point(p));if(first<0)first=node;if(last>=0)_graph.Connect(last,node);last=node;previous=p;
                }
                if(first<0 || first==last)continue;
                if(road.closedTrack)_graph.Connect(first,last);
                ends.Add(new End{Node=first,Crossing=road.startPrefabScript,Road=number});
                ends.Add(new End{Node=last,Crossing=road.endPrefabScript,Road=number});number++;
            }
            if(number==0)
            {
                // Release maps strip editor road components but retain their collision strips.
                // Each verified UV row contains the two road edges; average them into a centreline.
                foreach(var collider in Resources.FindObjectsOfTypeAll<MeshCollider>())
                {
                    if(!collider.gameObject.scene.IsValid() || !IsRoad(collider.transform))continue;
                    var mesh=collider.sharedMesh;
                    if(!mesh || !mesh.isReadable)continue;
                    var vertices=mesh.vertices;var uv=mesh.uv;
                    if(vertices.Length<4 || uv.Length!=vertices.Length)continue;
                    // Mesh optimization can reorder/weld vertices. Group by longitudinal UV,
                    // then take the midpoint of the outer edges of each cross-section.
                    var rows=Enumerable.Range(0,vertices.Length).GroupBy(i=>Math.Round(uv[i].y,3)).OrderBy(g=>g.Key);
                    int first=-1,last=-1;Vector3 previous=default(Vector3);
                    foreach(var row in rows)
                    {
                        var ordered=row.OrderBy(i=>uv[i].x).ToArray();
                        if(ordered.Length<2 || Mathf.Abs(uv[ordered[0]].x-uv[ordered[ordered.Length-1]].x)<.01f)continue;
                        Vector3 point=collider.transform.TransformPoint((vertices[ordered[0]]+vertices[ordered[ordered.Length-1]])*.5f);
                        int node=_graph.Add(Point(point));if(first<0)first=node;
                        if(last>=0)_graph.Connect(last,node);last=node;previous=point;
                    }
                    if(first>=0){ends.Add(new End{Node=first,Road=number});ends.Add(new End{Node=last,Road=number});number++;}
                }
            }
            // Joining by known crossing preserves overpasses; proximity only joins close ends
            // on the same elevation, never two streets crossing at different heights.
            for(int a=0;a<ends.Count;a++)for(int b=a+1;b<ends.Count;b++)
            {
                var x=ends[a];var y=ends[b];if(x.Road==y.Road)continue;
                var p=_graph.Points[x.Node];var q=_graph.Points[y.Node];
                if((x.Crossing && x.Crossing==y.Crossing) || (p.Distance(q)<8f && Math.Abs(p.Y-q.Y)<2f))_graph.Connect(x.Node,y.Node);
            }
            // T-junctions may terminate beside the middle of another strip. Connect only
            // across a short piece of verified road surface, not across grass or overpasses.
            foreach(var end in ends)
            {
                int ownStart=ends.Find(e=>e.Road==end.Road).Node;
                int ownEnd=ends.FindLast(e=>e.Road==end.Road).Node;
                var p=Vector(_graph.Points[end.Node]);
                for(int node=0;node<_graph.Points.Count;node++)
                {
                    if(node>=ownStart && node<=ownEnd)continue;
                    var q=Vector(_graph.Points[node]);
                    float d=Vector3.Distance(p,q);
                    if(d<.1f || d>18f || Mathf.Abs(p.y-q.y)>2.5f)continue;
                    bool onRoad=true;
                    for(float t=0;t<=1;t+=.2f)
                    {
                        var at=Vector3.Lerp(p,q,t);
                        if(!Physics.RaycastAll(at+Vector3.up*2,Vector3.down,4f,~0,QueryTriggerInteraction.Ignore).Any(h=>IsRoad(h.collider.transform))){onRoad=false;break;}
                    }
                    if(onRoad)_graph.Connect(end.Node,node);
                }
            }
            Debug.Log($"[VehicleBalance] Road network: {number} roads, {_graph.Points.Count} route points");
        }
    }
}
