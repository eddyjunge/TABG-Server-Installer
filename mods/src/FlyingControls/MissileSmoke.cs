using UnityEngine;
namespace TabgInstaller.FlyingControls
{
    // Sparse world-space exhaust; detached on impact so it fades naturally.
    public sealed class MissileSmoke : MonoBehaviour
    {
        private static Material _material;
        private ParticleSystem _smoke;
        private void Awake()
        {
            if(!_material)
            {
                var texture=new Texture2D(32,32,TextureFormat.RGBA32,false);
                for(int y=0;y<32;y++)for(int x=0;x<32;x++)
                {
                    float radius=new Vector2((x-15.5f)/15.5f,(y-15.5f)/15.5f).magnitude;
                    texture.SetPixel(x,y,new Color(1,1,1,Mathf.Pow(Mathf.Clamp01(1-radius),1.5f)));
                }
                texture.Apply();texture.wrapMode=TextureWrapMode.Clamp;
                _material=new Material(Shader.Find("Particles/Alpha Blended") ?? Shader.Find("Sprites/Default")){mainTexture=texture};
            }
            var go=new GameObject("Missile smoke");go.transform.SetParent(transform,false);go.transform.localPosition=Vector3.back*.65f;
            _smoke=go.AddComponent<ParticleSystem>();_smoke.Stop(true,ParticleSystemStopBehavior.StopEmittingAndClear);
            var main=_smoke.main;main.loop=true;main.simulationSpace=ParticleSystemSimulationSpace.World;
            main.startLifetime=.85f;main.startSpeed=.15f;main.startSize=.5f;main.startColor=new Color(.65f,.65f,.65f,.45f);main.maxParticles=50;
            var emission=_smoke.emission;emission.rateOverTime=22;
            var shape=_smoke.shape;shape.enabled=false;
            var size=_smoke.sizeOverLifetime;size.enabled=true;size.size=new ParticleSystem.MinMaxCurve(1,new AnimationCurve(new Keyframe(0,.4f),new Keyframe(1,1.7f)));
            var color=_smoke.colorOverLifetime;color.enabled=true;
            var gradient=new Gradient();gradient.SetKeys(new[]{new GradientColorKey(Color.white,0),new GradientColorKey(Color.white,1)},new[]{new GradientAlphaKey(.8f,0),new GradientAlphaKey(0,1)});color.color=new ParticleSystem.MinMaxGradient(gradient);
            go.GetComponent<ParticleSystemRenderer>().sharedMaterial=_material;
            _smoke.Play();
        }
        private void OnDestroy()
        {
            if(!_smoke)return;
            _smoke.transform.SetParent(null,true);_smoke.Stop(true,ParticleSystemStopBehavior.StopEmitting);Destroy(_smoke.gameObject,1);
        }
    }
}
