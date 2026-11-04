using System;
namespace TabgInstaller.Vehicles
{
    internal static class BotSkillRules
    {
        // Mostly ordinary players, with occasional beginners and pros; fixed for the bot's lifetime.
        internal static int Roll(float value)=>value<.15f?1:value<.4f?2:value<.75f?3:value<.93f?4:5;
        internal static string Name(int level)=>level<=1?"Anfaenger":level==2?"Amateur":level==3?"Intermediate":level==4?"Veteran":"Pro";
        internal static float MistakeChance(int level)=>level<=1?.35f:level==2?.22f:level==3?.12f:level==4?.05f:.015f;
        internal static float RescueRisk(int level,float health,bool exposed,float enemyDistance)
            => health<20?0:exposed && enemyDistance<10?(level>=3?0:.35f):exposed?(level>=4?.35f:.7f):1;
        internal static bool Upgrade(float current,float found,bool unarmed,bool ammoNeeded,float ownedRange,float foundRange,float distance)
            =>unarmed || found>current+8 || (ammoNeeded && found>=current-15) || (distance>0 && found>=current-8 && Math.Abs(foundRange-distance)+15<Math.Abs(ownedRange-distance));
    }
}
