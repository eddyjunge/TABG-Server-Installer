using System.IO;
namespace TabgInstaller.Vehicles
{
    // Native PlayerUpdate layout. Even one driving player is followed by the unoccupied-car count.
    internal static class BotMovementPacket
    {
        internal static void WriteDriving(BinaryWriter writer,float time,byte player,
            float x,float y,float z,byte[] rotation,byte[] input,float pitch,float yaw,byte carState)
        {
            writer.Write(time);writer.Write((byte)1);writer.Write(player);
            writer.Write((byte)64); // PacketContainerFlags.All
            writer.Write((byte)2); // DrivingState.Driving
            writer.Write(x);writer.Write(y);writer.Write(z);writer.Write(rotation);writer.Write(input);
            writer.Write(pitch);writer.Write(yaw);writer.Write(carState);
            writer.Write((byte)0); // No additional unoccupied cars.
        }
    }
}
