using UnityEngine;
using UnityEngine.Rendering;

namespace TabgInstaller.FlyingControls
{
    // Actual curved tubing in camera space: world lighting, round highlights, no collision logic.
    public sealed class FpvWire : MonoBehaviour
    {
        private Material _copper, _steel;
        internal static GameObject Create(Transform camera)
        {
            var root=new GameObject("FPV contact wires");root.transform.SetParent(camera,false);
            var wire=root.AddComponent<FpvWire>();
            var shader=Shader.Find("Standard") ?? Shader.Find("Diffuse");
            wire._copper=new Material(shader){color=new Color(.38f,.21f,.095f)};
            wire._copper.SetFloat("_Metallic",.78f);wire._copper.SetFloat("_Glossiness",.38f);
            wire._steel=new Material(shader){color=new Color(.5f,.53f,.49f)};
            wire._steel.SetFloat("_Metallic",.85f);wire._steel.SetFloat("_Glossiness",.5f);
            wire.Tube(new Vector3(-.14f,-.21f,.36f),new Vector3(-.09f,-.04f,.33f),new Vector3(-.08f,-.038f,.29f),new Vector3(-.006f,-.026f,.29f));
            wire.Tube(new Vector3(.15f,-.22f,.37f),new Vector3(.12f,-.07f,.32f),new Vector3(.064f,-.018f,.31f),new Vector3(.006f,-.028f,.295f));
            return root;
        }
        private void Tube(Vector3 a,Vector3 b,Vector3 c,Vector3 d)
        {
            Vector3 previous=a;
            for(int i=1;i<=28;i++)
            {
                float t=i/28f,u=1-t;
                Vector3 next=u*u*u*a+3*u*u*t*b+3*u*t*t*c+t*t*t*d;
                var segment=GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                var collider=segment.GetComponent<Collider>();collider.enabled=false;Destroy(collider);
                segment.transform.SetParent(transform,false);segment.transform.localPosition=(previous+next)*.5f;
                segment.transform.localRotation=Quaternion.FromToRotation(Vector3.up,next-previous);
                float diameter=i<5?.0045f:.0028f;
                segment.transform.localScale=new Vector3(diameter,(next-previous).magnitude*.52f,diameter);
                var renderer=segment.GetComponent<Renderer>();renderer.sharedMaterial=i>25?_steel:_copper;
                renderer.shadowCastingMode=ShadowCastingMode.Off;
                previous=next;
            }
        }
        private void OnDestroy(){if(_copper)Destroy(_copper);if(_steel)Destroy(_steel);}
    }
}
