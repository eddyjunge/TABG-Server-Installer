using System;
using System.Collections.Generic;

namespace TabgInstaller.Vehicles
{
    // Continuous progress along a polyline. A missed vertex is never a target to return to.
    internal sealed class RoadFollower
    {
        private readonly List<RoadPosition> _points = new List<RoadPosition>();
        private readonly List<float> _distance = new List<float>();
        internal float Progress { get; private set; }
        internal float Length => _distance[_distance.Count - 1];
        internal bool Arrived { get; private set; }
        internal int Segment { get; private set; }
        private const float Brake = 12f, LateralAcceleration = 12f;

        internal RoadFollower(IList<RoadPosition> points)
        {
            if (points == null || points.Count == 0) throw new ArgumentException("Route is empty");
            foreach (var point in points)
            {
                float length = _points.Count == 0 ? 0f : Distance(_points[_points.Count - 1], point);
                if (_points.Count > 0 && length < .01f) continue;
                _points.Add(point); _distance.Add(_distance.Count == 0 ? 0f : Length + length);
            }
        }
        internal static float Distance(RoadPosition a, RoadPosition b) => Magnitude(a.X-b.X, a.Z-b.Z);
        private static float Magnitude(float x, float z) => (float)Math.Sqrt(x*x+z*z);
        private static float Clamp(float x, float a, float b) => Math.Max(a, Math.Min(b, x));
        private RoadPosition Sample(float at)
        {
            at = Clamp(at, 0f, Length);
            int found = _distance.BinarySearch(at);
            int i = found >= 0 ? found : Math.Max(0, (~found) - 1);
            if (i+1 >= _points.Count) return _points[i];
            float t = (at-_distance[i])/(_distance[i+1]-_distance[i]);
            return new RoadPosition(_points[i].X+(_points[i+1].X-_points[i].X)*t, _points[i].Y+(_points[i+1].Y-_points[i].Y)*t,
                _points[i].Z+(_points[i+1].Z-_points[i].Z)*t);
        }
        internal RoadPosition PointAhead(float distance) => Sample(Progress + distance);

        private void Project(RoadPosition position, float speed, float dt)
        {
            float horizon = Math.Min(Length, Progress + Math.Max(8f, speed*dt*3f));
            float best = float.MaxValue, progress = Progress;
            // Local forward search avoids jumping to a nearby later hairpin or crossing.
            for (int i=Segment; i+1<_points.Count && _distance[i]<=horizon; i++)
            {
                var a=_points[i];var b=_points[i+1];
                float dx=b.X-a.X,dz=b.Z-a.Z;
                float length=_distance[i+1]-_distance[i];
                float t=Clamp(((position.X-a.X)*dx+(position.Z-a.Z)*dz)/(length*length),0,1);
                float at=Clamp(_distance[i]+t*length,Progress,horizon);
                var point=Sample(at);float d=Distance(position,point);
                if(d<best){best=d;progress=at;}
            }
            Progress=progress;
            while(Segment+1<_distance.Count-1 && _distance[Segment+1]<=Progress+.01f)Segment++;
        }
        internal RoadPosition Step(RoadPosition position, RoadPosition velocity, float maxSpeed, float acceleration, float dt)
        {
            float currentSpeed=Magnitude(velocity.X,velocity.Z);
            Project(position,currentSpeed,dt);
            float remaining=Length-Progress, endDistance=Distance(position,_points[_points.Count-1]);
            Arrived=remaining<2f && endDistance<2.5f;
            if(Arrived)return new RoadPosition(0,0,0);
            float lookAhead=Clamp(4f+currentSpeed*.3f,6f,14f);
            var target=Sample(Progress+lookAhead);
            float dx=target.X-position.X,dz=target.Z-position.Z;
            float desiredAngle=(float)Math.Atan2(dz,dx);
            float speedLimit=maxSpeed;
            // Sample curvature ahead and include the distance needed to brake into it.
            float preview=Math.Min(Length,Progress+Math.Max(20f,currentSpeed*currentSpeed/(2f*Brake)+12f));
            for(float at=Progress;at<=preview;at+=3f)
            {
                var a=Sample(at-4f);var b=Sample(at);var c=Sample(at+4f);
                float ab=Distance(a,b),bc=Distance(b,c),ac=Distance(a,c);
                if(ab<.1f || bc<.1f || ac<.1f)continue;
                float cross=Math.Abs((b.X-a.X)*(c.Z-a.Z)-(b.Z-a.Z)*(c.X-a.X));
                float curvature=2f*cross/(ab*bc*ac);
                if(curvature<.001f)continue;
                float cornerSpeed=(float)Math.Sqrt(LateralAcceleration/curvature);
                float allowed=(float)Math.Sqrt(cornerSpeed*cornerSpeed+2f*Brake*Math.Max(0,at-Progress-4f));
                speedLimit=Math.Min(speedLimit,allowed);
            }
            float stoppingDistance=remaining+Math.Max(0,endDistance-remaining);
            speedLimit=Math.Min(speedLimit,(float)Math.Sqrt(2f*Brake*stoppingDistance));
            speedLimit=Math.Min(speedLimit,Math.Max(2f,stoppingDistance*1.5f));
            float angle=currentSpeed>.1f ? (float)Math.Atan2(velocity.Z,velocity.X) : desiredAngle;
            float delta=desiredAngle-angle;
            while(delta>Math.PI)delta-=(float)(2*Math.PI);
            while(delta<-Math.PI)delta+=(float)(2*Math.PI);
            // Rejoining at an angle reduces speed smoothly instead of forcing a stop/pivot.
            speedLimit=Math.Min(speedLimit,maxSpeed*Math.Max(.2f,1f-Math.Abs(delta)/(float)Math.PI));
            float rate=speedLimit<currentSpeed ? Brake : acceleration;
            float speed=currentSpeed+Clamp(speedLimit-currentSpeed,-rate*dt,rate*dt);
            float maxTurn=Math.Min(2.2f,LateralAcceleration/Math.Max(4f,speed))*dt;
            angle+=Clamp(delta,-maxTurn,maxTurn);
            return new RoadPosition((float)Math.Cos(angle)*speed,0,(float)Math.Sin(angle)*speed);
        }
    }
}
