using System;
namespace TabgInstaller.Vehicles
{
    internal static class LandingRules
    {
        internal const float MaxTravelSpeed=10,MaxDescentSpeed=12,TouchdownSpeed=.7f;
        internal static float Descent(float clearance)
            => clearance<=.12f?0:Math.Min(MaxDescentSpeed,Math.Max(TouchdownSpeed,(float)Math.Sqrt(6*Math.Max(0,clearance-.12f))));
        internal static float Approach(float distance)=>Math.Min(MaxTravelSpeed,Math.Max(0,distance)*1.5f);
    }
}
