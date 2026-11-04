using System;
namespace TabgInstaller.Vehicles
{
    internal static class BotShieldRules
    {
        // Offset from the sphere center, unit direction, finite segment.
        internal static float FirstHit(float x,float y,float z,float dx,float dy,float dz,float radius,float distance)
        {
            float b=x*dx+y*dy+z*dz,c=x*x+y*y+z*z-radius*radius,disc=b*b-c;
            if(disc<0)return float.PositiveInfinity;
            float root=(float)Math.Sqrt(disc),near=-b-root,far=-b+root;
            float hit=near>=0?near:far;
            return hit>=0 && hit<=distance?hit:float.PositiveInfinity;
        }
    }
}
