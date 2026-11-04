using TabgInstaller.Vehicles;
using UnityEngine;

namespace TabgInstaller.FlyingControls
{
    public sealed class HelicopterLanding : MonoBehaviour
    {
        private Car _car;
        private Rigidbody[] _bodies;
        private bool _active,_parked,_hasSpot;
        private Vector3 _destination,_center,_half;
        private float _bottom,_nextSearch,_noticeUntil,_cruiseHeight;
        private int _candidate;
        private static readonly float[] SearchRadii={0,10,20,35,55,80,110};
        private string _status;
        internal static HelicopterLanding Get(Car car)
        {
            var landing=car.GetComponent<HelicopterLanding>() ?? car.gameObject.AddComponent<HelicopterLanding>();landing._car=car;return landing;
        }
        internal static bool Protects(Car car)
        {
            var landing=car?car.GetComponent<HelicopterLanding>():null;
            return landing && landing._active && AircraftClient.IsLocalDriver(car);
        }
        private bool Own(Collider c)=>c && c.transform.IsChildOf(_car.transform);
        private void Update()
        {
            if(!_car || !AircraftClient.IsLocalDriver(_car)){_active=_parked=false;return;}
            if(Player.usingInterface || TABGChat.inChat)return;
            if(Input.GetKeyDown(FlyingControlsPlugin.LandKey.Value))
            {
                if(_active || _parked){Cancel();return;}
                StartLanding();return;
            }
            if((_active || _parked) && (Input.GetKeyDown(KeyCode.W) || Input.GetKeyDown(KeyCode.S) || Input.GetKeyDown(KeyCode.A) || Input.GetKeyDown(KeyCode.D) ||
                Input.GetKeyDown(FlyingControlsPlugin.LiftKey.Value) || Input.GetKeyDown(FlyingControlsPlugin.DescendKey.Value)))Cancel();
        }
        private void Cancel(){_active=_parked=false;_status="Manuelle Steuerung";_noticeUntil=Time.unscaledTime+2;}
        private void StartLanding()
        {
            _active=true;_parked=false;_hasSpot=false;_nextSearch=0;_candidate=0;
            // Take energy out of every connected body before the next physics contact.
            // Only automatic-landing collision reports are suppressed; weapons still damage it.
            _bodies=_car.GetComponentsInChildren<Rigidbody>(true);
            foreach(var rig in _bodies)if(rig && !rig.isKinematic)
            {rig.velocity=Vector3.ClampMagnitude(rig.velocity,LandingRules.MaxTravelSpeed);rig.angularVelocity=Vector3.ClampMagnitude(rig.angularVelocity,.5f);}
            Measure();_status="Landeplatz suchen";
            Debug.Log("[HeliLanding] Automatic landing engaged.");
        }
        private void Measure()
        {
            bool first=true;var bounds=new Bounds(_car.mainRig.position,new Vector3(7,3,8));
            foreach(var c in _car.GetComponentsInChildren<Collider>(true))
            {
                if(!c.enabled || c.isTrigger || c.GetComponentInParent<Player>())continue;
                if(first){bounds=c.bounds;first=false;}else bounds.Encapsulate(c.bounds);
            }
            _center=bounds.center-_car.mainRig.position;_half=bounds.extents;
            _half.x+=.35f;_half.z+=.35f;_bottom=_car.mainRig.position.y-bounds.min.y;
        }
        private bool Ground(Vector3 origin,out RaycastHit contact)
        {
            contact=default(RaycastHit);float first=float.PositiveInfinity;
            foreach(var hit in Physics.RaycastAll(origin,Vector3.down,10000,~0,QueryTriggerInteraction.Ignore))
            {
                if(Own(hit.collider))continue;
                if(hit.distance<first){contact=hit;first=hit.distance;}
            }
            return !float.IsInfinity(first) && contact.normal.y>.88f && !contact.collider.GetComponentInParent<Car>() &&
                !contact.collider.GetComponentInParent<Player>() && !contact.collider.GetComponentInParent<Landfall.Network.NetworkPlayer>();
        }
        private bool Clear(Vector3 position)
        {
            foreach(var c in Physics.OverlapBox(position+_center,_half,Quaternion.identity,~0,QueryTriggerInteraction.Ignore))if(!Own(c))return false;
            return true;
        }
        private bool PathClear(Vector3 start,Vector3 end)
        {
            var delta=end-start;if(delta.sqrMagnitude<.0001f)return true;
            foreach(var hit in Physics.BoxCastAll(start+_center,_half,delta.normalized,Quaternion.identity,delta.magnitude,~0,QueryTriggerInteraction.Ignore))
                if(!Own(hit.collider))return false;
            return true;
        }
        private bool FindSpot()
        {
            var origin=_car.mainRig.position;
            // Scan nearest rings first, capped at eight probes per update.
            for(int probe=0;probe<8;probe++)
            {
                int index=_candidate++;if(_candidate>=1+(SearchRadii.Length-1)*16)_candidate=0;
                float radius=index==0?0:SearchRadii[1+(index-1)/16];int angle=index==0?0:(index-1)%16;
                Vector3 offset=Quaternion.Euler(0,angle*22.5f,0)*Vector3.forward*radius;
                if(!Ground(origin+offset+Vector3.up*12,out var ground))continue;
                bool supported=true;float highest=ground.point.y;
                for(int x=-1;x<=1;x+=2)for(int z=-1;z<=1;z+=2)
                {
                    if(!Ground(ground.point+new Vector3(x*_half.x*.65f,3,z*_half.z*.65f),out var foot) || Mathf.Abs(foot.point.y-ground.point.y)>.8f)supported=false;
                    else highest=Mathf.Max(highest,foot.point.y);
                }
                if(!supported)continue;
                var spot=new Vector3(ground.point.x,highest+_bottom+.35f,ground.point.z);
                if(!Clear(spot))continue;
                foreach(float rise in new[]{0f,8f,18f})
                {
                    float height=Mathf.Max(origin.y,spot.y+6)+rise;
                    var climb=new Vector3(origin.x,height,origin.z);var approach=new Vector3(spot.x,height,spot.z);
                    if(!PathClear(origin,climb) || !PathClear(climb,approach) || !PathClear(approach,spot))continue;
                    _destination=spot;_cruiseHeight=height;return true;
                }
            }
            return false;
        }
        internal bool Tick()
        {
            if(!_active && !_parked)return false;
            if(!_car || !AircraftClient.IsLocalDriver(_car)){_active=_parked=false;return false;}
            var rig=_car.mainRig;if(!rig || rig.isKinematic)return false;
            if(_parked)
            {
                rig.velocity=new Vector3(0,Mathf.Min(0,rig.velocity.y),0);rig.angularVelocity=Vector3.zero;
                return true; // Native gravity lets the landing gear settle; no hover thrust.
            }
            if(!_hasSpot && Time.unscaledTime>=_nextSearch)
            {
                _nextSearch=Time.unscaledTime+.15f;Measure();_hasSpot=FindSpot();
                _status=_hasSpot?"Automatisch landen":"Kein freier Landeplatz – halte Position";
            }
            Vector3 velocity=Vector3.zero;
            if(_hasSpot)
            {
                var flat=Vector3.ProjectOnPlane(_destination-rig.position,Vector3.up);
                float clearance=rig.position.y-_destination.y;
                float climb=_cruiseHeight-rig.position.y;
                if(flat.magnitude>1 && climb>1){velocity=Vector3.up*Mathf.Min(6,climb*2);}
                else
                {
                    velocity=flat.normalized*LandingRules.Approach(flat.magnitude);
                    if(flat.magnitude<1)velocity.y=-LandingRules.Descent(clearance);
                }
                if(!Clear(_destination) || !PathClear(rig.position,rig.position+velocity*Mathf.Max(.4f,Time.fixedDeltaTime)))
                {_hasSpot=false;velocity=Vector3.zero;_status="Hindernis – neuen Landeplatz suchen";}
                else if(flat.magnitude<.4f && clearance<=.18f)
                {_active=false;_parked=true;velocity=Vector3.zero;_status="Gelandet – Flugtaste zum Starten";Debug.Log("[HeliLanding] Touchdown completed.");}
            }
            foreach(var body in _bodies)if(body && !body.isKinematic){body.velocity=velocity;body.angularVelocity=Vector3.ClampMagnitude(body.angularVelocity,.7f);}
            if(rig.useGravity && !_parked)rig.AddForce(-Physics.gravity,ForceMode.Acceleration);
            rig.AddTorque(Vector3.Cross(rig.transform.up,Vector3.up)*12-rig.angularVelocity*6,ForceMode.Acceleration);
            return true;
        }
        private void OnGUI()
        {
            if(!_car || !AircraftClient.IsLocalDriver(_car) || Player.usingInterface || TABGChat.inChat)return;
            string label=_active || _parked || Time.unscaledTime<_noticeUntil?_status: "Sicher landen";
            GUI.Label(new Rect(18,Screen.height-125,650,30),FlyingControlsPlugin.LandKey.Value+": "+label+(_active?" (erneut drücken: abbrechen)":""));
        }
    }
}
