using System;
namespace TabgInstaller.Vehicles
{
    internal static class HoverClearance
    {
        internal static float HullClearance(float measured) => float.IsNaN(measured) || float.IsInfinity(measured) ? 1.5f : Math.Max(1.2f,Math.Min(4f,measured));
        internal static float Ceiling(float normalHeight) => normalHeight+60f;
        // Relative to the actual surface, never relative to the last frame's lifted position.
        internal static float Target(float normalHeight,float obstacleHeight) => Math.Max(normalHeight,Math.Min(Ceiling(normalHeight),obstacleHeight));
    }
}
