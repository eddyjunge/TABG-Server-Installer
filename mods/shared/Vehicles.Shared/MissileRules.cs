using System;
using System.Collections.Generic;

namespace TabgInstaller.Vehicles
{
    internal static class MissileRules
    {
        internal const float Damage = 40, Cooldown = 4, LockSeconds = .65f;
        internal const float GuidedSpeed = 25, StraightSpeed = 55, Lifetime = 8, Range = 300;
        internal const float TurnRadians = 1.8f, HitRadius = .65f, IncomingHitRadius = 1.4f, IncomingVisualScale = 2;
        internal static float FlightLifetime(bool guided) => guided ? 11.2f : Lifetime;
        internal const int BulletHits = 3;
        internal static int VehicleTarget(int carIndex) => 256+carIndex;
        internal static bool IsVehicleTarget(int target) => target>=256;
        internal static int CarIndex(int target) => target-256;
        internal static bool IsHeli(string name) => name != null && string.Equals(name.Replace("(Clone)", "").Trim(), "Heli", StringComparison.OrdinalIgnoreCase);
    }

    // Owned by a player, not a seat: changing seats or reopening the sight never reloads it.
    internal sealed class MissileWeapon
    {
        private float _readyAt;
        private float _lockStart, _lastAim = -100;
        private int _target = -1;
        internal float Remaining(float now) => Math.Max(0, _readyAt - now);
        internal float Aim(float now, int target, bool valid)
        {
            if (!valid || target < 0) { _target = -1; _lastAim = now; return 0; }
            if (_target != target || now - _lastAim > .35f) _lockStart = now;
            _target = target; _lastAim = now;
            return Math.Min(1, (now - _lockStart) / MissileRules.LockSeconds);
        }
        internal bool Locked(float now, int target) => target >= 0 && target == _target && now - _lastAim <= .35f && now - _lockStart >= MissileRules.LockSeconds;
        internal bool TryFire(float now, bool eligible, bool guided, int target)
        {
            if (!eligible || Remaining(now) > 0 || (guided && !Locked(now, target))) return false;
            _readyAt = now + MissileRules.Cooldown;
            return true;
        }
    }

    internal sealed class MissileArmor
    {
        private readonly HashSet<long> _hits = new HashSet<long>();
        internal int Hits { get; private set; }
        internal bool Hit(byte player, int bullet)
        {
            long key = ((long)player << 32) | (uint)bullet;
            if (Hits >= MissileRules.BulletHits || !_hits.Add(key)) return false;
            Hits++; return true;
        }
        internal bool Destroyed => Hits >= MissileRules.BulletHits;
    }

    internal struct MissilePoint
    {
        internal float X, Y, Z;
        internal MissilePoint(float x, float y, float z) { X = x; Y = y; Z = z; }
        public static MissilePoint operator +(MissilePoint a, MissilePoint b) => new MissilePoint(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
        public static MissilePoint operator -(MissilePoint a, MissilePoint b) => new MissilePoint(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
        public static MissilePoint operator *(MissilePoint a, float f) => new MissilePoint(a.X * f, a.Y * f, a.Z * f);
        internal float Length => (float)Math.Sqrt(Dot(this, this));
        internal MissilePoint Normal => Length > .00001f ? this * (1 / Length) : new MissilePoint(0, 0, 1);
        internal static float Dot(MissilePoint a, MissilePoint b) => a.X * b.X + a.Y * b.Y + a.Z * b.Z;
        internal static MissilePoint Turn(MissilePoint direction, MissilePoint desired, float maxAngle)
        {
            direction = direction.Normal; desired = desired.Normal;
            float dot = Math.Max(-1, Math.Min(1, Dot(direction, desired)));
            float angle = (float)Math.Acos(dot);
            if (angle <= maxAngle) return desired;
            var tangent = desired - direction * dot;
            if (tangent.Length < .0001f)
            {
                var axis = Math.Abs(direction.Y) < .9f ? new MissilePoint(0, 1, 0) : new MissilePoint(1, 0, 0);
                tangent = axis - direction * Dot(axis, direction);
            }
            return (direction * (float)Math.Cos(maxAngle) + tangent.Normal * (float)Math.Sin(maxAngle)).Normal;
        }
        // Slab intersection includes initial overlap and exact endpoint contact.
        internal static bool SegmentBox(MissilePoint start,MissilePoint end,MissilePoint min,MissilePoint max,out float fraction)
        {
            float enter=0,leave=1;var d=end-start;
            bool hit=Slab(start.X,d.X,min.X,max.X,ref enter,ref leave) &&
                Slab(start.Y,d.Y,min.Y,max.Y,ref enter,ref leave) &&
                Slab(start.Z,d.Z,min.Z,max.Z,ref enter,ref leave);
            fraction=enter;return hit;
        }
        private static bool Slab(float start,float delta,float min,float max,ref float enter,ref float leave)
        {
            if(Math.Abs(delta)<.000001f)return start>=min && start<=max;
            float a=(min-start)/delta,b=(max-start)/delta;
            enter=Math.Max(enter,Math.Min(a,b));leave=Math.Min(leave,Math.Max(a,b));return enter<=leave;
        }
        // Earliest contact along a swept segment. Stops fast missiles tunnelling through a player.
        internal static bool SegmentHit(MissilePoint start, MissilePoint end, MissilePoint center, float radius, out float fraction)
        {
            fraction = 0;
            var delta = end - start; var offset = start - center;
            float c = Dot(offset, offset) - radius * radius;
            if (c <= 0) return true;
            float a = Dot(delta, delta), b = Dot(offset, delta);
            if (a < .000001f || b >= 0) return false;
            float d = b * b - a * c;
            if (d < 0) return false;
            fraction = (-b - (float)Math.Sqrt(d)) / a;
            return fraction >= 0 && fraction <= 1;
        }
    }
}
