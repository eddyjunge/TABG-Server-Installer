using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using Landfall.Network;
using TabgInstaller.Vehicles;
using UnityEngine;

namespace TabgInstaller.FlyingControls
{
    public sealed class FpvClient : MonoBehaviour
    {
        internal static FpvClient Instance;
        private readonly Dictionary<int,FpvBody> _drones=new Dictionary<int,FpvBody>();
        private readonly HashSet<int> _finished=new HashSet<int>();
        private readonly Dictionary<int,Pickup> _thrown=new Dictionary<int,Pickup>();
        private FpvBody _controlled;
        private Camera _original,_camera;
        private RenderTexture _screen;
        private TABGPlayerClient _local;
        private bool _originalEnabled,_borrowed;
        private RenderTexture _originalTarget;
        private float _originalNear, _originalAspect;
        private Vector3 _originalLocalPosition;
        private Quaternion _originalLocalRotation;
        private CursorLockMode _cursorLock;
        private bool _cursorVisible;
        private GameObject _wire;
        private GameObject _ears;
        private readonly List<AudioListener> _listeners=new List<AudioListener>();
        private Behaviour _studioListener;
        private float Signal => _controlled && _ears ? DroneRules.Signal(Vector3.Distance(_controlled.transform.position,_ears.transform.position)) : 1;
        private float _pendingAt=-100,_nextItemCheck,_noticeUntil;
        private string _notice;
        private Texture2D _noise;
        private GUIStyle _label;
        internal static bool Controlling => Instance && Instance._controlled;
        internal static void Abort(){if(Instance && Controlling)Instance.Exit(true);}
        private static bool IsDriver(TABGPlayerClient player)=>player?.CurrentCar!=null && AircraftClient.IsLocalDriver(player.CurrentCar.CarReference);
        internal static bool HasDrones => Instance && Instance._drones.Count>0;
        private void Awake(){Instance=this;Debug.Log("[FPV] Client ready; assisted WASD flight, mouse aim, release to brake; Escape returns to player.");}
        internal static bool IsLocal(Component c)=>Player.localPlayer && c && c.transform.root==Player.localPlayer.transform.root;
        internal static void Send(byte kind,Action<BinaryWriter> write)
        {
            UnityEngine.Object.FindObjectOfType<ServerConnector>()?.SendMessageToServer((EventCode)VehicleProtocol.Event,VehicleProtocol.Message(kind,write),true);
        }
        internal static void Write(BinaryWriter w,Vector3 p){w.Write(p.x);w.Write(p.y);w.Write(p.z);}
        private static Vector3 Read(BinaryReader r)=>new Vector3(VehicleProtocol.ReadFinite(r),VehicleProtocol.ReadFinite(r),VehicleProtocol.ReadFinite(r));
        internal void Throw(Pickup pickup,byte owner)
        {
            _thrown[pickup.NetworkIndex]=pickup;
            var launch=pickup.gameObject.AddComponent<FpvThrown>();launch.Owner=owner;launch.Pickup=pickup;
        }
        internal void Launch(Pickup pickup)
        {
            var local=PhotonServerHandler.instance?.LocalPlayer;
            if(local==null || !DroneRules.CanControl(!local.IsDead,local.IsDowned,IsDriver(local),Controlling))return;
            _pendingAt=Time.unscaledTime;
            var camera=Camera.main;var direction=camera ? camera.transform.eulerAngles : Vector3.zero;
            Send(VehicleProtocol.DroneLaunch,w=>{w.Write(pickup.NetworkIndex);Write(w,pickup.transform.position);Write(w,new Vector3(0,direction.y,0));});
        }
        private void Update()
        {
            var local=PhotonServerHandler.instance?.LocalPlayer;
            if(_local!=local)
            {
                Exit(false);foreach(var d in _drones.Values)if(d)Destroy(d.gameObject);
                _drones.Clear();_finished.Clear();_thrown.Clear();_local=local;
            }
            if(Time.unscaledTime>=_nextItemCheck)
            {
                _nextItemCheck=Time.unscaledTime+3;
                FpvItem.Register(LootDatabase.Instance);
            }
            if(_pendingAt>0 && Time.unscaledTime-_pendingAt>3)
            {_pendingAt=-100;Notice("Keine FPV-Antwort vom Server. Client und Server brauchen Version 1.4.");}
            if(!_controlled)return;
            if(local==null || local.IsDead || local.IsDowned || IsDriver(local) || !Player.localPlayer)
            {Exit(true);return;}
            if(Input.GetKeyDown(KeyCode.Escape)){Exit(true);return;}
            ForceProne(Player.localPlayer.GetComponent<InputHandler>());
            if(Time.unscaledTime-_controlled.Seen>DroneRules.LinkTimeout+.5f)
            {Notice("FPV-Verbindung beendet");Exit(true);}
        }
        private void LateUpdate()
        {
            if(!_controlled || !_camera)return;
            if(!_screen || _screen.height!=Mathf.Min(720,Screen.height))CreateScreen();
            PositionCamera(_camera);
        }
        private void PositionCamera(Camera camera)
        {
            if(!_controlled || camera!=_camera)return;
            _camera.transform.SetPositionAndRotation(_controlled.transform.position+_controlled.transform.forward*.32f+Vector3.up*.1f,_controlled.transform.rotation);
        }
        private void CreateScreen()
        {
            if(_screen){_screen.Release();Destroy(_screen);}
            int height=Mathf.Min(720,Screen.height),width=Mathf.Max(320,Mathf.RoundToInt(height*(Screen.width/(float)Screen.height)/1.12f));
            _screen=new RenderTexture(width,height,24,_camera.allowHDR?RenderTextureFormat.DefaultHDR:RenderTextureFormat.Default){name="FPV analog picture"};_screen.Create();
            _camera.targetTexture=_screen;_camera.aspect=width/(float)height;
        }
        private void Enter(FpvBody drone)
        {
            if(_controlled)return;
            _original=Camera.main;
            if(!_original){Send(VehicleProtocol.DroneEnd,w=>{w.Write(drone.Id);w.Write(false);Write(w,drone.transform.position);});return;}
            _controlled=drone;_originalEnabled=_original.enabled;_borrowed=true;
            // Camera.CopyFrom omits image effects and command buffers (fog and contact shadows).
            // Borrow the actual game camera, keeping its complete rendering pipeline alive.
            _camera=_original;_originalTarget=_camera.targetTexture;
            _originalNear=_camera.nearClipPlane;_originalAspect=_camera.aspect;
            _originalLocalPosition=_camera.transform.localPosition;_originalLocalRotation=_camera.transform.localRotation;
            _cursorLock=Cursor.lockState;_cursorVisible=Cursor.visible;
            Cursor.lockState=CursorLockMode.Locked;Cursor.visible=false;
            _camera.nearClipPlane=.05f;
            CreateScreen();_camera.enabled=true;
            Camera.onPreCull+=PositionCamera;
            _wire=FpvWire.Create(_camera.transform);
            KeepPlayerHearing();
            PositionCamera(_camera);
            ForceProne(Player.localPlayer.GetComponent<InputHandler>());
            Debug.Log("[FPV] Entered native game camera with original fog/shadow effects; mouse pitch/yaw, 60s battery.");
        }
        private void Exit(bool notify)
        {
            var drone=_controlled;_controlled=null;
            if(notify && drone)Send(VehicleProtocol.DroneEnd,w=>{w.Write(drone.Id);w.Write(false);Write(w,drone.transform.position);});
            Camera.onPreCull-=PositionCamera;
            if(_ears){_ears.SetActive(false);Destroy(_ears);}_ears=null;
            foreach(var listener in _listeners)if(listener)listener.enabled=true;_listeners.Clear();
            if(_studioListener)_studioListener.enabled=true;_studioListener=null;
            if(_wire){_wire.SetActive(false);Destroy(_wire);}_wire=null;
            if(_camera)
            {
                _camera.targetTexture=_originalTarget;_camera.nearClipPlane=_originalNear;_camera.aspect=_originalAspect;
                _camera.transform.localPosition=_originalLocalPosition;_camera.transform.localRotation=_originalLocalRotation;
                _camera.enabled=_originalEnabled;
            }
            if(_borrowed){Cursor.lockState=_cursorLock;Cursor.visible=_cursorVisible;_borrowed=false;}
            if(_screen){_screen.Release();Destroy(_screen);}
            _camera=null;_screen=null;_original=null;_originalTarget=null;
            // Remain prone, but normal input resumes immediately. No permanent constraints are installed.
            if(drone)Debug.Log("[FPV] Returned to player; controls restored.");
        }
        private void OnDisable(){Exit(true);}
        private void KeepPlayerHearing()
        {
            _ears=new GameObject("FPV pilot hearing");_ears.SetActive(false);
            var head=Player.localPlayer.GetComponentInChildren<Head>();
            _ears.transform.SetParent(head?head.transform:Player.localPlayer.transform,false);
            foreach(var listener in FindObjectsOfType<AudioListener>())
                if(listener.enabled){_listeners.Add(listener);listener.enabled=false;}
            _ears.AddComponent<AudioListener>();
            var studio=AccessTools.Field(typeof(Player),"m_studioListener").GetValue(Player.localPlayer) as Behaviour;
            // FMOD normally already listens at the player. Only replace it if attached to the moved camera.
            if(studio && studio.enabled && studio.transform.IsChildOf(_camera.transform))
            {
                _studioListener=studio;studio.enabled=false;
                var copy=_ears.AddComponent(studio.GetType());
                AccessTools.Field(studio.GetType(),"ListenerNumber").SetValue(copy,AccessTools.Field(studio.GetType(),"ListenerNumber").GetValue(studio));
                AccessTools.Field(studio.GetType(),"attenuationObject").SetValue(copy,_ears);
            }
            _ears.SetActive(true);
            Debug.Log("[FPV] Hearing remains at pilot head; rotor is spatial for every player.");
        }
        internal static void ForceProne(InputHandler input)
        {
            if(!input)return;
            input.isProne=!(Player.localPlayer && Player.localPlayer.m_sitting && Player.localPlayer.m_sitting.isSeated);input.isCrouching=false;input.isSpringting=false;
            input.inputMovementDirection=Vector3.zero;input.lastInputDirection=Vector3.zero;
            input.isWalkingForward=false;input.isWalkingBackward=false;input.isStrafing=false;
            input.tappedMoveAbility=false;input.SprintWastPressed=false;
            AccessTools.Field(typeof(InputHandler),"m_DidJump").SetValue(input,false);
        }
        internal static void Receive(byte kind,BinaryReader r)
        {
            var self=Instance;if(!self)return;
            if(kind==VehicleProtocol.DroneDenied){self._pendingAt=-100;self.Notice("FPV-Start abgelehnt. Auf freiem Boden eine neue FPV Drone werfen.");return;}
            int id=r.ReadInt32();byte owner=r.ReadByte();
            if(kind==VehicleProtocol.DroneFinish)
            {
                Vector3 pos=Read(r);bool blast=r.ReadBoolean();
                if(!self._finished.Add(id))return;
                if(self._drones.TryGetValue(id,out var old))
                {
                    if(old==self._controlled){if(!blast)self.Notice(old.Remaining<1?"Akku leer – Einsatz beendet":self.Signal<.03f?"Funksignal verloren – Einsatz beendet":"Drohnenverbindung beendet");self.Exit(false);}
                    if(old)Destroy(old.gameObject);self._drones.Remove(id);
                }
                if(blast)FpvExplosion.Explode(pos,owner,false,id);
                return;
            }
            int thrown=r.ReadInt32(),sequence=r.ReadInt32();Vector3 position=Read(r);Quaternion rotation=Quaternion.Euler(Read(r));
            if(self._finished.Contains(id))return;
            if(self._thrown.TryGetValue(thrown,out var pickup))
            {if(pickup){pickup.gameObject.SetActive(false);Destroy(pickup.gameObject);}self._thrown.Remove(thrown);}
            bool local=PhotonServerHandler.instance?.LocalPlayer?.PlayerIndex==owner;
            if(!self._drones.TryGetValue(id,out var drone) || !drone)
            {
                drone=FpvBody.Create(id,owner,local,position,rotation);self._drones[id]=drone;
                if(local){self._pendingAt=-100;self.Enter(drone);}
            }
            drone.Sync(sequence,position,rotation);
        }
        private void Notice(string text){_notice=text;_noticeUntil=Time.unscaledTime+7;Debug.LogWarning("[FPV] "+text);}
        private void OnGUI()
        {
            if(_label==null){_label=new GUIStyle(GUI.skin.label){alignment=TextAnchor.MiddleCenter,fontSize=18};_label.normal.textColor=new Color(.8f,1,.83f);}
            if(Time.unscaledTime<_noticeUntil)GUI.Label(new Rect(20,Screen.height*.2f,Screen.width-40,60),_notice,_label);
            if(!_controlled || !_screen)return;
            int depth=GUI.depth;GUI.depth=-500;Color color=GUI.color;
            float signal=Signal,interference=1-signal;
            GUI.color=new Color(.88f,1,.91f,1);GUI.DrawTexture(new Rect(0,0,Screen.width,Screen.height),_screen,ScaleMode.StretchToFill);
            GUI.color=new Color(0,0,0,.16f+interference*.3f);
            for(int y=0;y<Screen.height;y+=4)GUI.DrawTexture(new Rect(0,y,Screen.width,1),Texture2D.whiteTexture);
            if(!_noise)
            {
                _noise=new Texture2D(128,64,TextureFormat.RGBA32,false);var pixels=new Color32[128*64];var random=new System.Random(421);
                for(int i=0;i<pixels.Length;i++){byte c=(byte)random.Next(255);pixels[i]=new Color32(c,c,c,255);}
                _noise.SetPixels32(pixels);_noise.wrapMode=TextureWrapMode.Repeat;_noise.Apply();
            }
            GUI.color=new Color(1,1,1,Mathf.Lerp(.055f,1,Mathf.Pow(interference,.8f)));
            GUI.DrawTextureWithTexCoords(new Rect(0,0,Screen.width,Screen.height),_noise,new Rect(Time.unscaledTime*3.7f,Time.unscaledTime*2.1f,8,8));
            GUI.color=new Color(1,1,1,.06f+interference*.6f);
            GUI.DrawTexture(new Rect(0,(Time.unscaledTime*80)%Screen.height,Screen.width,24),Texture2D.whiteTexture);
            if(interference>.15f)
            {
                GUI.color=new Color(0,0,0,interference*.65f);
                for(int i=0;i<4;i++)GUI.DrawTexture(new Rect(0,Mathf.Repeat(Time.unscaledTime*(110+i*33)+i*211,Screen.height),Screen.width,interference*(8+i*7)),Texture2D.whiteTexture);
            }
            GUI.color=Color.black;GUI.DrawTexture(new Rect(0,0,Screen.width,36),Texture2D.whiteTexture);GUI.DrawTexture(new Rect(0,Screen.height-54,Screen.width,54),Texture2D.whiteTexture);
            GUI.color=Color.white;
            if(_controlled.Remaining<=10)GUI.color=new Color(1,.45f,.2f);
            GUI.Label(new Rect(20,0,Screen.width-40,36),"FPV  •  LIVE     |     AKKU "+Mathf.CeilToInt(_controlled.Remaining)+" s     |     SIGNAL "+Mathf.RoundToInt(signal*100)+"%",_label);
            GUI.color=Color.white;
            GUI.Label(new Rect(20,Screen.height-52,Screen.width-40,50),"Maus: Zielen   W/S: Vor/zurück   A/D: Seitwärts   "+FlyingControlsPlugin.LiftKey.Value+" / "+FlyingControlsPlugin.DescendKey.Value+": Höhe   Loslassen: Bremsen   ESC: Zurück",_label);
            GUI.color=color;GUI.depth=depth;
        }
        [HarmonyPatch(typeof(NetworkPlayer),"RequestThrow")]
        internal static class SingleDroneThrowPatch
        {
            static void Prefix(int __0,ref int __1){__1=DroneRules.ThrowQuantity(__0,__1);}
        }
        [HarmonyPatch(typeof(InteractionHandler),"NetworkThrow")]
        internal static class ThrowPatch
        {
            static void Postfix(int __0,byte __5,Pickup __result)
            {if(__0==DroneRules.ItemId && __result && Instance)Instance.Throw(__result,__5);}
        }
        [HarmonyPatch(typeof(InputHandler),"Update")]
        internal static class InputPatch
        {
            static bool Prefix(InputHandler __instance){if(!Controlling || !IsLocal(__instance))return true;ForceProne(__instance);return false;}
        }
        [HarmonyPatch]
        internal static class ActionPatch
        {
            static IEnumerable<MethodBase> TargetMethods()
            {
                yield return AccessTools.Method(typeof(WeaponHandler),"PressAttack");
                yield return AccessTools.Method(typeof(WeaponHandler),"AltFireClick");
                yield return AccessTools.Method(typeof(InteractionHandler),"LateUpdate");
                yield return AccessTools.Method(typeof(InteractionHandler),"OnHoldingThrowObjectUpdate");
                yield return AccessTools.Method(typeof(InteractionHandler),"OnHoldingThrowObjectFixedUpdate");
                yield return AccessTools.Method(typeof(CameraMovement),"Update");
                yield return AccessTools.Method(typeof(CameraMovement),"LateUpdate");
                yield return AccessTools.Method(typeof(CameraMovement),"FixedUpdate");
                yield return AccessTools.Method(typeof(TABG.MouseYLook),"Update");
                yield return AccessTools.Method(typeof(TABG.RotateByMouseInput),"LateUpdate");
            }
            static bool Prefix(Component __instance)=>!Controlling || !IsLocal(__instance);
        }
    }
    public sealed class FpvThrown : MonoBehaviour
    {
        internal byte Owner;internal Pickup Pickup;
        private float _age;private bool _sent;
        private void Update()
        {
            _age+=Time.deltaTime;
            if(_age>6){Destroy(gameObject);return;}
            if(_sent || _age<DroneRules.ThrowDelay)return;
            _sent=true;
            if(PhotonServerHandler.instance?.LocalPlayer?.PlayerIndex==Owner && FpvClient.Instance)FpvClient.Instance.Launch(Pickup);
        }
    }
    public sealed class FpvBody : MonoBehaviour
    {
        internal int Id;internal byte Owner;internal float Seen;
        private bool _local,_ending;
        private Rigidbody _rig;
        private float _nextSend;
        private float _born,_pitch,_yaw;
        private AudioSource _motor;
        private static AudioClip _motorClip;
        private Collider _hull;
        private readonly List<Collider> _launchCarrier=new List<Collider>();
        internal float Remaining=>DroneRules.Remaining(_born,Time.unscaledTime);
        private int _sequence=1;
        private readonly DroneSequence _received=new DroneSequence();
        private Vector3 _target;private Quaternion _rotation;
        private readonly List<Transform> _rotors=new List<Transform>();
        internal static FpvBody Create(int id,byte owner,bool local,Vector3 position,Quaternion rotation)
        {
            var root=new GameObject("FPV Drone #"+id);root.transform.SetPositionAndRotation(position,rotation);
            var body=root.AddComponent<FpvBody>();body.Id=id;body.Owner=owner;body._local=local;
            body._born=Time.unscaledTime;body._yaw=rotation.eulerAngles.y;
            body._rig=root.AddComponent<Rigidbody>();body._rig.mass=1;body._rig.interpolation=RigidbodyInterpolation.Interpolate;body._rig.maxAngularVelocity=8;body._rig.useGravity=true;body._rig.isKinematic=!local;
            body._rig.collisionDetectionMode=local ? CollisionDetectionMode.ContinuousDynamic : CollisionDetectionMode.Discrete;
            var hit=root.AddComponent<SphereCollider>();hit.radius=DroneRules.Radius;body._hull=hit;
            var carrier=PhotonServerHandler.instance?.LocalPlayer?.CurrentCar?.CarReference;
            if(local && carrier)foreach(var c in carrier.GetComponentsInChildren<Collider>(true))
            {Physics.IgnoreCollision(hit,c);body._launchCarrier.Add(c);}
            var bulletTarget=new GameObject("FPV bullet target");bulletTarget.layer=2;bulletTarget.transform.SetParent(root.transform,false);
            var bulletHit=bulletTarget.AddComponent<SphereCollider>();bulletHit.isTrigger=true;bulletHit.radius=.65f;
            if(local && Player.localPlayer)foreach(var c in Player.localPlayer.GetComponentsInChildren<Collider>(true))Physics.IgnoreCollision(hit,c);
            body.Part(root.transform,new Vector3(0,0,0),new Vector3(.4f,.16f,.5f),PrimitiveType.Cube);
            for(int x=-1;x<=1;x+=2)for(int z=-1;z<=1;z+=2)
            {
                body.Part(root.transform,new Vector3(x*.22f,0,z*.22f),new Vector3(.52f,.055f,.055f),PrimitiveType.Cube).localRotation=Quaternion.Euler(0,-x*z*45,0);
                var rotor=body.Part(root.transform,new Vector3(x*.4f,.04f,z*.4f),new Vector3(.5f,.025f,.075f),PrimitiveType.Cube);body._rotors.Add(rotor);
            }
            body.StartMotor();body.Seen=Time.unscaledTime;return body;
        }
        private Transform Part(Transform parent,Vector3 position,Vector3 scale,PrimitiveType type)
        {
            var go=GameObject.CreatePrimitive(type);go.transform.SetParent(parent,false);go.transform.localPosition=position;go.transform.localScale=scale;
            go.GetComponent<Collider>().enabled=false;Destroy(go.GetComponent<Collider>());
            var renderer=go.GetComponent<Renderer>();renderer.material.color=new Color(.12f,.15f,.14f);
            return go.transform;
        }
        internal void Sync(int sequence,Vector3 position,Quaternion rotation)
        {
            if(!_received.Accept(sequence))return;
            Seen=Time.unscaledTime;_target=position;_rotation=rotation;
        }
        private void Update()
        {
            if(_launchCarrier.Count>0 && Time.unscaledTime-_born>.75f)
            {
                foreach(var c in _launchCarrier)if(c)Physics.IgnoreCollision(_hull,c,false);
                _launchCarrier.Clear();
            }
            foreach(var rotor in _rotors)rotor.Rotate(0,2200*Time.deltaTime,0,Space.Self);
            if(_local && !_ending && FpvClient.Controlling && !Player.usingInterface && !TABGChat.inChat)
            {
                // Mouse axes are per-frame deltas: read once here, never repeatedly in FixedUpdate.
                _yaw=Mathf.Repeat(_yaw+Input.GetAxisRaw("Mouse X")*1.5f,360);
                _pitch=Mathf.Clamp(_pitch-Input.GetAxisRaw("Mouse Y")*1.5f,-75,75);
            }
            if(_motor)
            {
                float effort=Mathf.Clamp01(_rig.velocity.magnitude/45f);
                if(!_local)effort=Mathf.Clamp01(Vector3.Distance(transform.position,_target)*4/45f);
                _motor.pitch=Mathf.Lerp(_motor.pitch,.9f+effort*.55f,Time.deltaTime*5);
            }
            if(!_local)
            {
                transform.position=Vector3.Lerp(transform.position,_target,1-Mathf.Exp(-18*Time.deltaTime));
                transform.rotation=Quaternion.Slerp(transform.rotation,_rotation,1-Mathf.Exp(-18*Time.deltaTime));
                if(Time.unscaledTime-Seen>DroneRules.LinkTimeout+1)Destroy(gameObject);
            }
        }
        private void FixedUpdate()
        {
            if(!_local || _ending || !FpvClient.Controlling)return;
            float forward=(Input.GetKey(KeyCode.W)?1:0)-(Input.GetKey(KeyCode.S)?1:0);
            float turn=(Input.GetKey(KeyCode.D)?1:0)-(Input.GetKey(KeyCode.A)?1:0);
            bool up=Input.GetKey(FlyingControlsPlugin.LiftKey.Value),down=Input.GetKey(FlyingControlsPlugin.DescendKey.Value);
            if(Player.usingInterface || TABGChat.inChat){forward=turn=0;up=down=false;}
            float height=float.PositiveInfinity;
            foreach(var h in Physics.RaycastAll(_rig.position,Vector3.down,10000,~0,QueryTriggerInteraction.Ignore))
            {
                if(h.collider.transform.IsChildOf(transform) || h.collider.GetComponentInParent<Player>() || h.collider.GetComponentInParent<NetworkPlayer>())continue;
                height=Mathf.Min(height,h.distance);
            }
            float left=float.IsPositiveInfinity(height)?FlyingControlsPlugin.MaxAltitude.Value:FlyingControlsPlugin.MaxAltitude.Value-height;
            if(left<=0){up=false;var v=_rig.velocity;v.y=Mathf.Min(v.y,-Mathf.Min(6,-left*2));_rig.velocity=v;}
            else if(left<5 && _rig.velocity.y>left){var v=_rig.velocity;v.y=left;_rig.velocity=v;up=false;}
            // Assisted FPV: aim and travel are independent. Releasing movement brakes
            // even when the camera looks down; thrust remains acceleration, not teleportation.
            var aim=Quaternion.Euler(_pitch,_yaw,0);
            Vector3 travel=aim*Vector3.forward*forward+Quaternion.Euler(0,_yaw,0)*Vector3.right*turn;
            travel=Vector3.ClampMagnitude(travel,1);
            Vector3 target=travel*FlyingControlsPlugin.MaxSpeed.Value;
            if(up!=down)target.y=up?7:-7;
            if(left<=0)target.y=Mathf.Min(target.y,-Mathf.Min(6,1-left*2));
            else if(left<5)target.y=Mathf.Min(target.y,left);
            target=Vector3.ClampMagnitude(target,FlyingControlsPlugin.MaxSpeed.Value);
            Vector3 acceleration=new Vector3(
                DroneRules.FlightAcceleration(_rig.velocity.x,target.x,Time.fixedDeltaTime),
                DroneRules.FlightAcceleration(_rig.velocity.y,target.y,Time.fixedDeltaTime),
                DroneRules.FlightAcceleration(_rig.velocity.z,target.z,Time.fixedDeltaTime));
            _rig.AddForce(Vector3.ClampMagnitude(acceleration,35)-Physics.gravity,ForceMode.Acceleration);
            var desired=Quaternion.Euler(_pitch,_yaw,-turn*10);
            var error=desired*Quaternion.Inverse(_rig.rotation);error.ToAngleAxis(out float angle,out Vector3 axis);
            if(angle>180)angle-=360;
            if(Mathf.Abs(angle)>.001f)_rig.AddTorque(axis*(angle*Mathf.Deg2Rad*36),ForceMode.Acceleration);
            _rig.AddTorque(-_rig.angularVelocity*12,ForceMode.Acceleration);
            _rig.velocity=Vector3.ClampMagnitude(_rig.velocity,FlyingControlsPlugin.MaxSpeed.Value);
            if(Time.unscaledTime>=_nextSend)
            {
                _nextSend=Time.unscaledTime+1f/15f;
                FpvClient.Send(VehicleProtocol.DroneMove,w=>{w.Write(Id);w.Write(_sequence++);FpvClient.Write(w,_rig.position);FpvClient.Write(w,_rig.rotation.eulerAngles);});
            }
        }
        private void OnCollisionEnter(Collision collision)
        {
            if(!_local || _ending || !FpvClient.Controlling)return;
            if(PhotonServerHandler.instance?.LocalPlayer?.PlayerObject && collision.transform.IsChildOf(PhotonServerHandler.instance.LocalPlayer.PlayerObject.transform))return;
            _ending=true;Vector3 point=collision.contactCount>0?collision.contacts[0].point:_rig.position;
            FpvClient.Send(VehicleProtocol.DroneEnd,w=>{w.Write(Id);w.Write(true);FpvClient.Write(w,point);});
            _rig.velocity=Vector3.zero;_rig.angularVelocity=Vector3.zero;_rig.isKinematic=true;
        }
        private void StartMotor()
        {
            if(!_motorClip)
            {
                const int rate=24000;var samples=new float[rate];var random=new System.Random(728);
                float noise=0;
                for(int i=0;i<samples.Length;i++)
                {
                    float t=i/(float)rate;noise=noise*.72f+((float)random.NextDouble()*2-1)*.28f;
                    samples[i]=(.24f*Mathf.Sin(2*Mathf.PI*160*t)+.13f*Mathf.Sin(2*Mathf.PI*320*t)+.07f*Mathf.Sin(2*Mathf.PI*640*t))*(.86f+.14f*Mathf.Sin(2*Mathf.PI*32*t))+noise*.15f*Mathf.Sin(Mathf.PI*t);
                }
                _motorClip=AudioClip.Create("FPV quad rotor loop",rate,1,rate,false);_motorClip.SetData(samples,0);
            }
            _motor=gameObject.AddComponent<AudioSource>();_motor.clip=_motorClip;_motor.loop=true;_motor.playOnAwake=false;
            _motor.spatialBlend=1;_motor.minDistance=5;_motor.maxDistance=150;_motor.rolloffMode=AudioRolloffMode.Linear;
            _motor.dopplerLevel=.4f;_motor.volume=.65f;_motor.Play();
        }
    }
    internal static class FpvExplosion
    {
        internal static void Explode(Vector3 position,byte attacker,bool missile=false,int blastId=0)
        {
            if(!GrenadeProfiles.Load()){Debug.LogError("[FPV] Native Dynamite profile unavailable.");return;}
            var obj=new GameObject(missile?"Heli missile grenade explosion":"FPV Dynamite explosion");obj.SetActive(false);obj.transform.position=position;
            var owner=obj.AddComponent<NetworkOwner>();
            var handler=PhotonServerHandler.instance;
            var player=handler==null?null:AccessTools.Method(typeof(PhotonServerHandler),"FindPlayer").Invoke(handler,new object[]{attacker}) as TABGPlayerClient;
            owner.Init(attacker,player?.PlayerObject?.transform);
            var blast=obj.AddComponent<Explosion>();var original=missile?GrenadeProfiles.Hand:GrenadeProfiles.Dynamite;
            foreach(var field in typeof(Explosion).GetFields(BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance))
                if(!field.IsInitOnly && (field.FieldType.IsPrimitive || field.FieldType.IsEnum || field.FieldType==typeof(LayerMask) || field.FieldType==typeof(PlayerEffect[])))field.SetValue(blast,field.GetValue(original));
            blast.auto=false;obj.transform.localScale=original.transform.lossyScale;
            if(missile)blast.damage=MissileRules.Damage;
            obj.SetActive(true);
            using(BlastHitmarker.Context(missile,blastId))blast.Explode(owner);
            var visual=missile?GrenadeProfiles.HandVisual:GrenadeProfiles.DynamiteVisual;
            if(visual!=null && visual.rootObject)
            {
                visual.rootObject.position=position;
                if(visual.particles!=null)foreach(var ps in visual.particles)if(ps){if(visual.emits>0)ps.Emit(visual.emits);else ps.Play();}
                if(visual.sound)visual.sound.Play(position);
            }
            UnityEngine.Object.Destroy(obj,2);
            Debug.Log($"[{(missile?"HeliMissiles":"FPV")}] Native grenade explosion at {position}, damage={blast.damage}, radius={original.radius}");
        }
    }
}
