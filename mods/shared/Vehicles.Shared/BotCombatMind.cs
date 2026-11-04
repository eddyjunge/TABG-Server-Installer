using System;
namespace TabgInstaller.Vehicles
{
    // Original TABG rules; SAIN was studied as a behavioral reference, not copied.
    internal sealed class BotCombatMind
    {
        internal readonly int Personality;
        internal float Pressure { get; private set; }
        internal string Name => Personality==0?"Draufgaenger":Personality==1?"Taktiker":"Vorsichtig";
        internal BotCombatMind(int choice){Personality=Math.Abs(choice%3);}
        internal void Tick(float dt){if(!float.IsNaN(dt) && !float.IsInfinity(dt) && dt>0)Pressure=Math.Max(0,Pressure-dt*.18f);}
        internal void AddPressure(float value){if(!float.IsNaN(value) && !float.IsInfinity(value) && value>0)Pressure=Math.Min(1,Pressure+value);}
        internal float PushBias => (Personality==0?14:Personality==1?2:-12)-Pressure*28;
        internal float CoverBias => (Personality==2?14:Personality==1?4:-4)+Pressure*40;
        internal float AimErrorMultiplier => 1+Pressure*.9f;
        internal float Reaction(float skill,float distance)=>Math.Max(.18f,.55f-Math.Max(0,Math.Min(1,skill))*.25f)+Math.Max(0,Math.Min(150,distance))*.002f+Pressure*.22f;
        internal static float IncomingFire(float distance,float aimDot,bool lineClear)=>lineClear && distance>=0 && distance<=70 && aimDot>=.985f?.16f:0;
        internal static float AllianceChance(int first,int second,bool sharedEnemy,bool recentHostility)
        {
            if(recentHostility)return 0;
            float chance=.04f;
            if(first!=second && (first==1 || second==1))chance+=.18f;
            else if(first==2 && second==2)chance+=.12f;
            else if(first==1 && second==1)chance+=.10f;
            if(sharedEnemy)chance+=.28f;
            return Math.Min(.6f,chance);
        }
        internal static bool CanFlank(bool allyEngaged,bool armed,bool visible,float health,float pressure,float distance,float ringDanger)
            =>allyEngaged && armed && visible && health>=45 && pressure<.55f && distance>=12 && distance<=85 && ringDanger<.4f;
    }
}
