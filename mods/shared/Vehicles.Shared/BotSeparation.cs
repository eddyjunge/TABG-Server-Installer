using System;
namespace TabgInstaller.Vehicles
{
    internal static class BotSeparation
    {
        internal const float BodyDistance=1.35f, LookAhead=3.5f;
        internal static bool Steer(float dx,float dz,float otherX,float otherZ,out float x,out float z)
        {
            x=dx;z=dz;float distance=(float)Math.Sqrt(otherX*otherX+otherZ*otherZ);
            if(distance>=LookAhead || (distance>BodyDistance && dx*otherX+dz*otherZ<=0))return false;
            if(distance<.001f){x=1;z=0;return true;}
            float weight=(LookAhead-distance)/LookAhead;
            // Pass to the right of the obstacle; opposite approaches naturally take opposite sides.
            x=dx+(otherZ/distance*1.8f-otherX/distance*2)*weight;
            z=dz+(-otherX/distance*1.8f-otherZ/distance*2)*weight;
            float length=(float)Math.Sqrt(x*x+z*z);if(length>.001f){x/=length;z/=length;}
            return true;
        }
        internal static bool SafeStep(float stepX,float stepZ,float otherX,float otherZ)
        {
            float old=otherX*otherX+otherZ*otherZ,newX=otherX-stepX,newZ=otherZ-stepZ;
            float after=newX*newX+newZ*newZ;
            if(old<BodyDistance*BodyDistance)return after>old+.00001f;
            float stepLength=stepX*stepX+stepZ*stepZ;
            float t=stepLength>.000001f?Math.Max(0,Math.Min(1,(otherX*stepX+otherZ*stepZ)/stepLength)):0;
            float x=otherX-stepX*t,z=otherZ-stepZ*t;
            return x*x+z*z>=BodyDistance*BodyDistance;
        }
    }
}
