using System;
namespace TabgInstaller.Vehicles
{
    internal static class VehicleBalance
    {
        internal const float MinSpeed = 15f, MaxSpeed = 45f, MinHeight = 25f, MaxHeight = 150f;
        internal const float AircraftHealth = 30f, StartProtection = 2f, CrashSpeed = 18f, RoadSpeed = 20f;
        internal static float Clamp(float value, float min, float max) => float.IsNaN(value) || float.IsInfinity(value) ? min : Math.Max(min, Math.Min(max, value));
        internal static float HeightForSpeed(float speed) => MaxHeight - (Clamp(speed, MinSpeed, MaxSpeed) - MinSpeed) / (MaxSpeed - MinSpeed) * (MaxHeight - MinHeight);
        internal static float SpeedForHeight(float height) => MaxSpeed - (Clamp(height, MinHeight, MaxHeight) - MinHeight) / (MaxHeight - MinHeight) * (MaxSpeed - MinSpeed);
        internal static float SurvivorHealth(float health) => health <= 0 ? health : Math.Max(Math.Min(1f, health), health - 50f);
        internal static bool IsAircraft(string name) => Has(name, "Ufo") || Has(name, "Heli");
        internal static bool IsTaxi(string name) => Has(name, "Hover_Bike") || Has(name, "Hover_Car");
        private static bool Has(string name, string part) => name != null && name.IndexOf(part, StringComparison.OrdinalIgnoreCase) >= 0;
    }
    internal sealed class AircraftLife
    {
        internal float Health = VehicleBalance.AircraftHealth;
        internal float Maximum=VehicleBalance.AircraftHealth;
        internal void SetGrounded(bool grounded)
        {if(Destroyed)return;float next=grounded?300:VehicleBalance.AircraftHealth;if(next==Maximum)return;Health=Health/Maximum*next;Maximum=next;}
        internal bool Destroyed, Started;
        internal float ProtectedUntil;
        internal AircraftLife(float now) { ProtectedUntil = now + VehicleBalance.StartProtection; }
        internal void Start(float now) { if (Started) return; Started = true; ProtectedUntil = now + VehicleBalance.StartProtection; }
        internal bool Damage(float amount, float now)
        {
            if (Destroyed || now < ProtectedUntil || float.IsNaN(amount) || float.IsInfinity(amount) || amount <= 0) return false;
            Health = Math.Max(0, Health - amount); Destroyed = Health == 0; return Destroyed;
        }
    }
}
