using System.Collections;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using Landfall.Network;
using TabgInstaller.Vehicles;
using UnityEngine;

namespace TabgInstaller.FlyingControls
{
    public sealed class AircraftSafetyClient : MonoBehaviour
    {
        private static AircraftSafetyClient _instance;
        private float _nextEject;
        private static readonly Dictionary<long,float> Shields=new Dictionary<long,float>();
        internal static IEnumerable<byte> Shielded(int car)
        {
            foreach(var pair in new Dictionary<long,float>(Shields))
            {
                if(Time.unscaledTime>=pair.Value){Shields.Remove(pair.Key);continue;}
                if((pair.Key>>8)==car)yield return (byte)(pair.Key&255);
            }
        }
        private static void Shield(int car,byte player)=>Shields[((long)car<<8)|player]=Time.unscaledTime+2.5f;
        private IEnumerator Separate(Car car,Player departing=null)
        {
            var player=departing?departing:Player.localPlayer;if(!player || !car)yield break;
            var changed=new List<KeyValuePair<Collider,Collider>>();
            foreach(var a in player.GetComponentsInChildren<Collider>(true))
            foreach(var b in car.GetComponentsInChildren<Collider>(true))
                if(a!=b && !Physics.GetIgnoreCollision(a,b)){Physics.IgnoreCollision(a,b);changed.Add(new KeyValuePair<Collider,Collider>(a,b));}
            yield return new WaitForSeconds(2.5f);
            foreach(var pair in changed)if(pair.Key && pair.Value)Physics.IgnoreCollision(pair.Key,pair.Value,false);
        }
        [HarmonyPatch(typeof(Sitting),"DoSharedGetOutStuff")]
        internal static class NormalExitSeparation
        {
            static void Prefix(Sitting __instance)
            {
                if(!_instance || !__instance.isSeated || !__instance.currentSeat)return;
                var car=__instance.currentSeat.GetComponentInParent<Car>();
                var player=__instance.GetComponentInParent<Player>();
                if(car && player && VehicleBalance.IsAircraft(car.name))_instance.StartCoroutine(_instance.Separate(car,player));
            }
        }
        private void Awake(){_instance=this;Debug.Log("[AircraftSafety] Ready: J ejects from Heli/UFO using native launchpad flight.");}
        private void Update()
        {
            var p=PhotonServerHandler.instance?.LocalPlayer;var car=p?.CurrentCar?.CarReference;
            if(!car || !VehicleBalance.IsAircraft(car.name) || p.IsDead || p.IsDowned || Player.usingInterface || TABGChat.inChat)return;
            if(Input.GetKeyDown(KeyCode.J) && Time.unscaledTime>=_nextEject)
            {_nextEject=Time.unscaledTime+3;Shield(p.CurrentCar.CarIndex,p.PlayerIndex);StartCoroutine(Separate(car));FpvClient.Send(VehicleProtocol.EjectRequest,w=>w.Write(p.CurrentCar.CarIndex));}
        }
        private void OnGUI()
        {
            var car=PhotonServerHandler.instance?.LocalPlayer?.CurrentCar?.CarReference;
            if(!car || !VehicleBalance.IsAircraft(car.name) || Player.usingInterface || TABGChat.inChat || FpvClient.Controlling)return;
            GUI.Label(new Rect(18,Screen.height-90,220,35),"J: Schleudersitz");
        }
        internal static void Receive(byte kind,BinaryReader r)
        {
            int id=r.ReadInt32();var car=AircraftClient.Cars?.Find(c=>c.CarIndex==id)?.CarReference;
            if(kind==VehicleProtocol.EjectShield){byte player=r.ReadByte();Shield(id,player);if(_instance && PhotonServerHandler.instance?.LocalPlayer?.PlayerIndex==player)_instance.StartCoroutine(_instance.Separate(car));return;}
            if(kind==VehicleProtocol.EjectLaunch){FpvClient.Abort();if(_instance)_instance.StartCoroutine(_instance.Launch(car));return;}
            if(kind==VehicleProtocol.PilotHandover){var velocity=Read(r);if(_instance)_instance.StartCoroutine(_instance.Handover(car,velocity));return;}
            bool falling=r.ReadBoolean();var position=Read(r);var rotation=Quaternion.Euler(Read(r));
            if(!car)return;
            var follower=car.GetComponent<AircraftFallView>();
            if(!falling){if(follower)Destroy(follower);car.UnFreeze();return;}
            if(!follower){follower=car.gameObject.AddComponent<AircraftFallView>();follower.Car=car;car.LocalDrive();}
            follower.Position=position;follower.Rotation=rotation;car.isSimulatedByMe=false;car.Freeze();
        }
        private static Vector3 Read(BinaryReader r)=>new Vector3(VehicleProtocol.ReadFinite(r),VehicleProtocol.ReadFinite(r),VehicleProtocol.ReadFinite(r));
        private IEnumerator Handover(Car car,Vector3 velocity)
        {
            float until=Time.unscaledTime+1;
            while(car && (!AircraftClient.IsLocalDriver(car) || car.GetComponent<AircraftFallView>()) && Time.unscaledTime<until)yield return null;
            if(!car || !AircraftClient.IsLocalDriver(car))yield break;
            car.UnFreeze();
            foreach(var rig in car.GetComponentsInChildren<Rigidbody>(true))if(!rig.isKinematic)rig.velocity=velocity;
            Debug.Log("[AircraftSafety] Pilot handover received; aircraft momentum preserved.");
        }
        private IEnumerator Launch(Car car)
        {
            float until=Time.unscaledTime+1;
            while(Player.localPlayer && Player.localPlayer.m_sitting.isSeated && Time.unscaledTime<until)yield return null;
            var player=Player.localPlayer;if(!player || player.m_sitting.isSeated || player.m_playerDeath.dead)yield break;
            var playerColliders=player.GetComponentsInChildren<Collider>(true);
            var carColliders=car?car.GetComponentsInChildren<Collider>(true):new Collider[0];
            var changed=new List<KeyValuePair<Collider,Collider>>();
            foreach(var a in playerColliders)foreach(var b in carColliders)
                if(!Physics.GetIgnoreCollision(a,b)){Physics.IgnoreCollision(a,b);changed.Add(new KeyValuePair<Collider,Collider>(a,b));}
            var sky=player.GetComponent<Skydiving>();
            if(sky)sky.NetworkLaunch(Vector3.up*2); // LaunchPad's native default multiplier, sound and network effect.
            Debug.Log("[AircraftSafety] Native launchpad ejection activated.");
            yield return new WaitForSeconds(1.2f);
            foreach(var pair in changed)if(pair.Key && pair.Value)Physics.IgnoreCollision(pair.Key,pair.Value,false);
        }
    }
    public sealed class AircraftFallView : MonoBehaviour
    {
        private static readonly System.Reflection.MethodInfo Move=AccessTools.Method(typeof(Car),"MoveCar");
        internal Car Car;internal Vector3 Position;internal Quaternion Rotation;
        private void LateUpdate()
        {
            if(!Car || !Car.mainRig)return;
            Car.isSimulatedByMe=false;Car.Freeze();float t=1-Mathf.Exp(-22*Time.deltaTime);
            Move.Invoke(Car,new object[]{Vector3.Lerp(Car.mainRig.position,Position,t),Quaternion.Slerp(Car.mainRig.rotation,Rotation,t)});
        }
    }
}
