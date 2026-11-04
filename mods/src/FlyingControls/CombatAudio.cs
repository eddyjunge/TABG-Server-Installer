using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;
namespace TabgInstaller.FlyingControls
{
    public sealed class CombatAudio : MonoBehaviour
    {
        private static CombatAudio _instance;
        private static readonly Dictionary<string,AudioClip> Clips=new Dictionary<string,AudioClip>();
        private AudioSource _cues;private float _nextWarning,_nextHit,_nextLock;
        private void Awake(){_instance=this;_cues=gameObject.AddComponent<AudioSource>();_cues.playOnAwake=false;_cues.spatialBlend=0;_cues.volume=.85f;_cues.priority=32;}
        internal static AudioClip Clip(string name)
        {
            if(Clips.TryGetValue(name,out var clip))return clip;
            using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("TabgAudio."+name+".wav"))
            {
                if(stream==null){Debug.LogError("[CombatAudio] Missing sound "+name);return null;}
                using(var r=new BinaryReader(stream))
                {
                    // Bundled WAVs are generated as mono PCM16, 22050 Hz with a 44-byte header.
                    r.ReadBytes(44);var samples=new float[(stream.Length-44)/2];
                    for(int i=0;i<samples.Length;i++)samples[i]=r.ReadInt16()/32768f;
                    clip=AudioClip.Create("TABG "+name,samples.Length,1,22050,false);clip.SetData(samples,0);Clips[name]=clip;return clip;
                }
            }
        }
        internal static AudioSource Spatial(GameObject owner,string name,float volume,float near,float far)
        {
            var source=owner.AddComponent<AudioSource>();source.playOnAwake=false;source.clip=Clip(name);source.loop=true;
            source.spatialBlend=1;source.rolloffMode=AudioRolloffMode.Linear;source.minDistance=near;source.maxDistance=far;
            source.dopplerLevel=.35f;source.volume=volume;source.priority=100;if(source.clip)source.Play();return source;
        }
        internal static void Hit()
        {
            if(!_instance || Time.unscaledTime<_instance._nextHit)return;
            _instance._nextHit=Time.unscaledTime+.07f;_instance._cues.PlayOneShot(Clip("hit"),.9f);
        }
        internal static void Locked()
        {
            if(!_instance || Time.unscaledTime<_instance._nextLock)return;
            _instance._nextLock=Time.unscaledTime+.5f;_instance._cues.PlayOneShot(Clip("lock"),.65f);
        }
        internal static void Incoming(float distance)
        {
            if(!_instance || distance>180 || Time.unscaledTime<_instance._nextWarning)return;
            _instance._nextWarning=Time.unscaledTime+Mathf.Lerp(.2f,.8f,Mathf.Clamp01(distance/180));
            _instance._cues.PlayOneShot(Clip("warning"),.75f);
        }
    }
    public sealed class HelicopterAudio : MonoBehaviour
    {
        private Car _car;private AudioSource _rotor;private AudioLowPassFilter _filter;
        private float _power,_nextGround,_clearance,_emptySince;private Vector3 _last;
        private GameObject _sound;private Seat[] _seats;
        private void Start()
        {
            _car=GetComponent<Car>();_seats=_car.GetComponentsInChildren<Seat>(true);_last=transform.position;
            // Dedicated child: native vehicle sound handlers cannot stop or retune this source.
            _sound=new GameObject("Helicopter continuous rotor");_sound.transform.SetParent(transform,false);
            _rotor=CombatAudio.Spatial(_sound,"heli",0,18,360);_rotor.priority=70;
            _filter=_sound.AddComponent<AudioLowPassFilter>();_filter.lowpassResonanceQ=1;
            foreach(var old in GetComponentsInChildren<VehicleSoundHandler>(true))old.StopDrive();
        }
        private void Update()
        {
            if(!_car || !_rotor)return;
            bool occupied=false,inside=false;
            var root=Player.localPlayer?Player.localPlayer.transform.root:null;
            foreach(var seat in _seats)
                if(seat && seat.occupant){occupied=true;if(root && (seat.occupant==root || seat.occupant.IsChildOf(root)))inside=true;}
            float speed=Vector3.Distance(transform.position,_last)/Mathf.Max(.01f,Time.deltaTime);_last=transform.position;
            if(Time.unscaledTime>=_nextGround)
            {
                _nextGround=Time.unscaledTime+.5f;_clearance=100;
                foreach(var h in Physics.RaycastAll(transform.position+Vector3.up,Vector3.down,100,~0,QueryTriggerInteraction.Ignore))
                    if(!h.collider.transform.IsChildOf(transform) && !h.collider.GetComponentInParent<Player>())_clearance=Mathf.Min(_clearance,h.distance);
            }
            if(occupied)_emptySince=Time.unscaledTime;
            bool running=occupied || _clearance>6 || Time.unscaledTime-_emptySince<3;
            float wanted=running?1:0;
            _power=Mathf.MoveTowards(_power,wanted,Time.deltaTime*.8f);
            _rotor.volume=_power*(inside?.27f:1f);
            _rotor.pitch=Mathf.Lerp(.7f,1.03f,_power)+Mathf.Clamp(speed/180,0,.1f);
            _filter.cutoffFrequency=inside?950:22000;
            if(_power>.01f && !_rotor.isPlaying && _rotor.clip)_rotor.Play();
        }
        // Suppress only the obsolete continuous engine loop; retain entry/crash/other sounds.
        [HarmonyLib.HarmonyPatch(typeof(VehicleSoundHandler),"Drive")]
        internal static class LegacyRotor
        {
            static bool Prefix(VehicleSoundHandler __instance)
            {var car=__instance.GetComponentInParent<Car>();return !car || !TabgInstaller.Vehicles.MissileRules.IsHeli(car.name);}
        }
    }
}
