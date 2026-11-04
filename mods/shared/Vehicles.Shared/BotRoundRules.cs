using System;
namespace TabgInstaller.Vehicles
{
    internal static class BotRoundRules
    {
        internal const float WalkSpeed=3.6f,SprintSpeed=6.2f,SearchSeconds=32,InitialLootSeconds=22,MaxLootSeconds=65;
        internal static bool CanRevive(int maxTeamSize)=>maxTeamSize>1;
        internal static int ReserveTarget(int magazine)=>Math.Min(120,Math.Max(12,magazine*3));
        internal static bool GrenadeOpportunity(float distance,bool cover,int enemies,bool pushing,float roll)
            =>distance>=9 && distance<=36 && (cover || enemies>=2 || pushing || roll<.55f);
        internal static bool ImmediateDanger(bool attacked,bool visible,bool armedEnemy,float distance)
            =>attacked || visible && armedEnemy && distance<30;
        internal static bool Ready(bool armed,int ammunition,int magazine,float looting)
            =>armed && ammunition>0 && (ammunition>=Math.Min(30,Math.Max(2,magazine*2)) || looting>=40);
        internal static bool LeaveLoot(bool everyoneReady,float looting)
            =>looting>=MaxLootSeconds || everyoneReady && looting>=InitialLootSeconds;
        internal static bool Sprint(float distance,bool visibleEnemy,bool busy,bool travelling)
            =>travelling && !busy && !visibleEnemy && distance>7;
        internal static byte SprintFlag(byte movement,bool sprint)
            =>(byte)(sprint && (movement&1)!=0?movement|8:movement&~8);
        internal static bool Rotate(float outsideSafeDistance,float secondsRemaining,bool moving)
            =>outsideSafeDistance>0 && (moving || outsideSafeDistance/SprintSpeed+18>=secondsRemaining);
        internal static float PoiScore(float distance,float popularity,float recentVisit,float variation,float flightDistance)
            =>distance*.24f-popularity*65+Math.Max(0,180-recentVisit)*2+variation*85+Math.Min(400,flightDistance)*.08f;
        internal static bool TripFits(float distance,float secondsRemaining,float safety=20)
            =>!float.IsNaN(distance) && distance>=0 && distance/SprintSpeed+safety<secondsRemaining;
    }
}
