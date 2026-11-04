using System;
using System.IO;
namespace TabgInstaller.Vehicles
{
    internal static class VehicleProtocol
    {
        internal const byte Event = 245;
        internal const uint Magic = 0x56474854;
        internal const byte Version = 2, Hello = 0, Spawn = 1, Ack = 2, Crash = 3, Detonate = 4, MissileAim = 5, MissileFire = 6, MissileState = 7, MissileRemove = 8, MissileHit = 9, MissileReply = 10, DroneLaunch = 11, DroneState = 12, DroneMove = 13, DroneEnd = 14, DroneFinish = 15, DroneDenied = 16, DroneHit = 17, AircraftFall = 18, EjectRequest = 19, EjectLaunch = 20, PilotHandover = 21, EjectShield = 22, BlastConfirmed = 23, TaxiDestination = 24, TaxiBoost = 25, TaxiStatus = 26, AircraftHealth = 27;
        internal static byte[] Message(byte kind, Action<BinaryWriter> write = null)
        {
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream))
                {
                    writer.Write(Magic); writer.Write(Version); writer.Write(kind); write?.Invoke(writer);
                    return stream.ToArray();
                }
            }
        }
        internal static BinaryReader Open(byte[] data, out byte kind)
        {
            kind = 255;
            if (data == null || data.Length < 6 || data.Length > 4096) return null;
            var reader = new BinaryReader(new MemoryStream(data, false));
            if (reader.ReadUInt32() != Magic || reader.ReadByte() != Version) { reader.Dispose(); return null; }
            kind = reader.ReadByte(); return reader;
        }
        internal static float ReadFinite(BinaryReader reader)
        {
            float value = reader.ReadSingle();
            if (float.IsNaN(value) || float.IsInfinity(value)) throw new InvalidDataException("Invalid coordinate");
            return value;
        }
    }
}
