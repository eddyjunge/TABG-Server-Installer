using System;
namespace TabgInstaller.Vehicles
{
    internal static class FlightMotion
    {
        // Braking never changes the sign: releasing descent cannot cause ascent.
        internal static float BrakeVertical(float velocity, float braking, float deltaTime)
        {
            // Old configurations often set HoverForce=0 to fight the former constant lift.
            // Keep a minimum damping rate so those configurations stop promptly too.
            float remaining = velocity * (float)Math.Exp(-Math.Max(8f, braking) * Math.Max(0f, deltaTime));
            return Math.Abs(remaining) < 0.02f ? 0f : remaining;
        }
    }
}
