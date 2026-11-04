using System.Collections.Generic;
using UnityEngine;

namespace TabgInstaller.Vehicles
{
    // Prefab-local collision boxes, expressed in the main rigidbody's frame. Server
    // debug proxies are not the vehicle hull and must not be used for missile hits.
    internal sealed class VehicleGeometry
    {
        private static readonly Dictionary<int,VehicleGeometry> Cache=new Dictionary<int,VehicleGeometry>();
        private readonly List<Bounds> _boxes=new List<Bounds>();
        internal Vector3 Center;internal float GroundClearance=.7f;private float _radius;
        internal static VehicleGeometry Get(int type)
        {
            if(Cache.TryGetValue(type,out var geometry))return geometry;
            geometry=new VehicleGeometry();
            var prefab=CarDatabase.Instance?CarDatabase.Instance.GetDataEntry(type).prefab:null;
            var car=prefab?prefab.GetComponent<Car>():null;
            if(!car || !car.mainRig)return geometry; // Database can be unavailable during loading; retry later.
            var frame=car.mainRig.transform;float largest=0;
            foreach(var collider in prefab.GetComponentsInChildren<Collider>(true))
            {
                if(collider.isTrigger || !collider.enabled)continue;
                Bounds local;
                if(collider is BoxCollider box)local=new Bounds(box.center,box.size);
                else if(collider is SphereCollider sphere)local=new Bounds(sphere.center,Vector3.one*sphere.radius*2);
                else if(collider is CapsuleCollider capsule)
                {
                    var size=Vector3.one*capsule.radius*2;size[capsule.direction]=Mathf.Max(capsule.height,size[capsule.direction]);local=new Bounds(capsule.center,size);
                }
                else if(collider is MeshCollider mesh && mesh.sharedMesh)local=mesh.sharedMesh.bounds;
                else continue;
                var bounds=new Bounds();bool first=true;
                for(int i=0;i<8;i++)
                {
                    var corner=local.center+Vector3.Scale(local.extents,new Vector3((i&1)==0?-1:1,(i&2)==0?-1:1,(i&4)==0?-1:1));
                    var point=frame.InverseTransformPoint(collider.transform.TransformPoint(corner));
                    if(first){bounds=new Bounds(point,Vector3.zero);first=false;}else bounds.Encapsulate(point);
                }
                geometry._boxes.Add(bounds);
                geometry.GroundClearance=Mathf.Max(geometry.GroundClearance,-bounds.min.y+.15f);
                float volume=bounds.size.x*bounds.size.y*bounds.size.z;
                if(volume>largest){largest=volume;geometry.Center=bounds.center;}
            }
            if(geometry._boxes.Count==0)
            {
                // Conservative body fallback, only if this vehicle has no usable prefab colliders.
                geometry._boxes.Add(new Bounds(Vector3.zero,new Vector3(2,1.5f,3)));
                Debug.LogWarning($"[HeliMissiles] Vehicle type={type}: fallback hull");
            }
            foreach(var box in geometry._boxes)geometry._radius=Mathf.Max(geometry._radius,box.center.magnitude+box.extents.magnitude);
            Cache[type]=geometry;
            Debug.Log($"[HeliMissiles] Vehicle type={type}: {geometry._boxes.Count} hull boxes; aim={geometry.Center}");
            return geometry;
        }
        internal Vector3 Aim(Vector3 position,Quaternion rotation)=>position+rotation*Center;
        internal IEnumerable<Vector3> AimPoints(Vector3 observer,Vector3 direction,Vector3 position,Quaternion rotation)
        {
            var inverse=Quaternion.Inverse(rotation);var localObserver=inverse*(observer-position);var localDirection=inverse*direction.normalized;
            // Aim at the part under the sight, not a single prefab pivot which may be
            // under terrain or far from the visible body (particularly long vehicles).
            foreach(var box in _boxes)
            {
                float along=Mathf.Max(0,Vector3.Dot(box.center-localObserver,localDirection));
                yield return position+rotation*box.ClosestPoint(localObserver+localDirection*along);
            }
        }
        internal bool Hit(Vector3 start,Vector3 end,Vector3 position,Quaternion rotation,float radius,out float fraction)
        {
            fraction=1;
            var segment=end-start;float length=segment.sqrMagnitude;
            float closest=length>0?Mathf.Clamp01(Vector3.Dot(position-start,segment)/length):0;
            float broadRadius=_radius+radius*1.733f;
            if((start+segment*closest-position).sqrMagnitude>broadRadius*broadRadius)return false;
            var inverse=Quaternion.Inverse(rotation);var a=inverse*(start-position);var b=inverse*(end-position);
            bool hit=false;
            foreach(var box in _boxes)
            {
                var padding=Vector3.one*radius;
                if(MissilePoint.SegmentBox(P(a),P(b),P(box.min-padding),P(box.max+padding),out float t) && t<=fraction){hit=true;fraction=t;}
            }
            return hit;
        }
        private static MissilePoint P(Vector3 p)=>new MissilePoint(p.x,p.y,p.z);
    }
}
