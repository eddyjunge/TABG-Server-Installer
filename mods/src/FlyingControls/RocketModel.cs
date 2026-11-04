using System.Collections.Generic;
using UnityEngine;
namespace TabgInstaller.FlyingControls
{
    internal static class RocketModel
    {
        private static Mesh _mesh;private static Material _material;
        internal static Transform Create(Transform parent)
        {
            if(!_mesh)Build();
            var go=new GameObject("Visible rocket body");go.transform.SetParent(parent,false);
            go.AddComponent<MeshFilter>().sharedMesh=_mesh;
            var renderer=go.AddComponent<MeshRenderer>();renderer.sharedMaterial=_material;renderer.receiveShadows=false;
            return go.transform;
        }
        private static void Build()
        {
            var vertices=new List<Vector3>();var colors=new List<Color>();var triangles=new List<int>();
            var white=new Color(.93f,.93f,.84f);var red=new Color(.95f,.19f,.08f);var dark=new Color(.13f,.16f,.18f);
            System.Action<Vector3,Vector3,Vector3,Color> tri=(a,b,c,color)=>
            {int i=vertices.Count;vertices.Add(a);vertices.Add(b);vertices.Add(c);triangles.Add(i);triangles.Add(i+1);triangles.Add(i+2);colors.Add(color);colors.Add(color);colors.Add(color);};
            for(int i=0;i<12;i++)
            {
                float a=i*Mathf.PI/6,b=(i+1)*Mathf.PI/6;
                var radialA=new Vector3(Mathf.Cos(a),Mathf.Sin(a),0);var radialB=new Vector3(Mathf.Cos(b),Mathf.Sin(b),0);
                var backA=radialA*.16f+Vector3.back*.55f;var backB=radialB*.16f+Vector3.back*.55f;
                var frontA=radialA*.16f+Vector3.forward*.38f;var frontB=radialB*.16f+Vector3.forward*.38f;
                Color shade=white*(.78f+.22f*Mathf.Max(0,Mathf.Sin(a)));shade.a=1;
                tri(backA,frontA,frontB,shade);tri(backA,frontB,backB,shade);
                tri(frontA,Vector3.forward*.72f,frontB,red);
                tri(backB,Vector3.back*.57f,backA,dark);
                var stripeA=radialA*.164f+Vector3.forward*.15f;var stripeB=radialB*.164f+Vector3.forward*.15f;
                tri(stripeA,stripeA+Vector3.forward*.1f,stripeB+Vector3.forward*.1f,red);tri(stripeA,stripeB+Vector3.forward*.1f,stripeB,red);
            }
            for(int i=0;i<4;i++)
            {
                var radial=Quaternion.Euler(0,0,i*90)*Vector3.up;
                var a=radial*.13f+Vector3.back*.15f;var b=radial*.44f+Vector3.back*.57f;var c=radial*.13f+Vector3.back*.57f;
                tri(a,b,c,white);tri(c,b,a,white);
            }
            _mesh=new Mesh{name="Shared low-poly helicopter rocket"};_mesh.SetVertices(vertices);_mesh.SetTriangles(triangles,0);_mesh.SetColors(colors);_mesh.RecalculateNormals();_mesh.RecalculateBounds();
            _material=new Material(Shader.Find("Sprites/Default")){color=Color.white};
        }
    }
}
