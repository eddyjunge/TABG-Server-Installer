using System;
namespace TabgInstaller.Vehicles
{
    internal static class DroneRules
    {
        internal static int ThrowQuantity(int item,int requested)=>item==ItemId && requested>0?1:requested;
        internal static bool ConsumedOne(int before,int after)=>before>0 && after==before-1;
        internal const int ItemId = 30001;
        internal const float ThrowDelay = .35f, LinkTimeout = 2f, Radius = .35f;
        internal const float FlightSeconds = 60f;
        internal const float ClearRange = 350f, MaxRange = 1000f;
        internal static float Remaining(float born, float now) => Math.Max(0, FlightSeconds - Math.Max(0, now - born));
        internal static float Signal(float distance) => Math.Max(0,Math.Min(1,(MaxRange-distance)/(MaxRange-ClearRange)));
        internal static float LiftAcceleration(float velocity,float targetVelocity,float upCosine,float gravity=9.81f)
            => Math.Max(0,Math.Min(38,(gravity+(targetVelocity-velocity)*3f)/Math.Max(.35f,upCosine)));
        internal static float FlightAcceleration(float velocity,float target,float dt)
        {
            if(dt<=0)return 0;
            // Exact exponential velocity response avoids timestep-dependent overshoot.
            float response=target==0?5f:3.5f;
            return (target-velocity)*(1-(float)Math.Exp(-response*dt))/dt;
        }
        internal static bool ValidStep(float distance, float elapsed) => elapsed >= 0 && distance <= 50f * Math.Min(elapsed, LinkTimeout) + 2f;
        internal static bool CanControl(bool alive, bool downed, bool driving, bool alreadyFlying) => alive && !downed && !driving && !alreadyFlying;
    }
    internal sealed class DroneSequence
    {
        private int _sequence = -1;
        internal bool Finished { get; private set; }
        internal bool Accept(int sequence)
        {
            if(Finished || sequence <= _sequence)return false;
            _sequence=sequence;return true;
        }
        internal bool Finish() { if(Finished)return false;Finished=true;return true; }
    }
}
