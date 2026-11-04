using System;
namespace TabgInstaller.Vehicles
{
    internal static class BotTravelRules
    {
        internal static bool FitsSquad(int seats,int members) => members>0 && seats>=members;

        // Compare walking effort with the detour to a pad and its useful flight distance.
        internal static float LaunchCost(float walk,float remaining,float alignment)
        {
            if(float.IsNaN(walk) || float.IsNaN(remaining) || float.IsNaN(alignment) ||
                walk<0 || remaining<0 || walk>=350 || alignment<-.2f)return float.PositiveInfinity;
            return walk+Math.Max(0,remaining-260)+35;
        }
    }
}
