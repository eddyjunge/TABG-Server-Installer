using System;
namespace TabgInstaller.Vehicles
{
    internal static class BotBlastRules
    {
        // Verified from this game's level0 Ex_TNT (11495) and Ex_Default (11681).
        // Dedicated-server builds omit EffectPool. Both native profiles have
        // flatDamage=false, scale=false, ignoreWalls=false, needWalls=false.
        internal const float DynamiteDamage=100, DynamiteRadius=16, HandRadius=10;
        internal static float Damage(float damage,float radius,float distance,bool flat,bool ignoreWalls,bool needWalls,bool blocked)
        {
            if(float.IsNaN(damage) || float.IsInfinity(damage) || damage<=0 || float.IsNaN(radius) || float.IsInfinity(radius) || radius<=0 || float.IsNaN(distance) || float.IsInfinity(distance) || distance<0 || distance>radius)return 0;
            if(needWalls?!blocked:!ignoreWalls && blocked)return 0;
            return damage*(flat?1:Math.Max(0,1-distance/radius));
        }
    }
}
