using System;
namespace TabgInstaller.Vehicles
{
    internal sealed class HoverBoost
    {
        internal const float Duration = 2f, Cooldown = 8f, SpeedMultiplier = 2f;
        private float _endsAt, _readyAt;
        internal bool Active(float now) => now < _endsAt;
        internal float Remaining(float now) => Math.Max(0f, _endsAt - now);
        internal float CooldownRemaining(float now) => Math.Max(0f, _readyAt - now);
        internal bool TryStart(float now, bool pressedThisFrame, bool canBoost)
        {
            if (!pressedThisFrame || !canBoost || now < _readyAt) return false;
            _endsAt = now + Duration;
            _readyAt = _endsAt + Cooldown;
            return true;
        }
        // Leaving the seat or cancelling a route stops thrust, but cannot reset cooldown.
        internal void Stop() { _endsAt = 0f; }
    }
}
