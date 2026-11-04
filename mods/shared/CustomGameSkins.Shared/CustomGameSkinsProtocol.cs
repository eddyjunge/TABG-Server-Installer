using System;
using System.IO;

namespace TabgInstaller.CustomGameSkins
{
    internal static class CustomGameSkinsProtocol
    {
        internal const byte EventCode = 244;
        internal const byte Version = 1;
        internal const byte Hello = 1;
        internal const byte Accepted = 2;
        internal const byte ApplyOutfit = 3;
        internal const byte OutfitApplied = 4;
        internal const byte Denied = 5;
        internal const int GearValueCount = 12;

        internal const byte DeniedDisabled = 1;
        internal const byte DeniedNotAuthorized = 2;
        internal const byte DeniedPlayerNotReady = 3;
        internal const byte DeniedInvalidOutfit = 4;
        internal const byte DeniedRateLimited = 5;

        private static readonly byte[] Magic = { (byte)'S', (byte)'K', (byte)'I', (byte)'N' };

        internal static byte[] CreateHello() => CreateHeader(Hello);

        internal static byte[] CreateAccepted() => CreateHeader(Accepted);

        internal static byte[] CreateApplyOutfit(int[] gear) => CreateOutfitMessage(ApplyOutfit, gear);

        internal static byte[] CreateOutfitApplied(int[] gear) => CreateOutfitMessage(OutfitApplied, gear);

        internal static byte[] CreateDenied(byte reason)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                WriteHeader(writer, Denied);
                writer.Write(reason);
                return stream.ToArray();
            }
        }

        internal static bool TryRead(byte[] data, out byte operation, out int[] gear, out byte reason)
        {
            operation = 0;
            gear = null;
            reason = 0;
            if (data == null || data.Length < Magic.Length + 2)
                return false;

            try
            {
                using (var stream = new MemoryStream(data, false))
                using (var reader = new BinaryReader(stream))
                {
                    for (var i = 0; i < Magic.Length; i++)
                    {
                        if (reader.ReadByte() != Magic[i])
                            return false;
                    }

                    if (reader.ReadByte() != Version)
                        return false;

                    operation = reader.ReadByte();
                    if (operation == Hello || operation == Accepted)
                        return stream.Position == stream.Length;

                    if (operation == Denied)
                    {
                        if (stream.Length - stream.Position != 1)
                            return false;
                        reason = reader.ReadByte();
                        return true;
                    }

                    if (operation != ApplyOutfit && operation != OutfitApplied)
                        return false;
                    if (stream.Length - stream.Position < sizeof(int))
                        return false;

                    var count = reader.ReadInt32();
                    if (count != GearValueCount || stream.Length - stream.Position != count * sizeof(int))
                        return false;

                    gear = new int[count];
                    for (var i = 0; i < count; i++)
                        gear[i] = reader.ReadInt32();
                    return true;
                }
            }
            catch (Exception)
            {
                return false;
            }
        }

        private static byte[] CreateOutfitMessage(byte operation, int[] gear)
        {
            if (gear == null || gear.Length != GearValueCount)
                throw new ArgumentException("A TABG outfit must contain exactly 12 gear values.", nameof(gear));

            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                WriteHeader(writer, operation);
                writer.Write(gear.Length);
                for (var i = 0; i < gear.Length; i++)
                    writer.Write(gear[i]);
                return stream.ToArray();
            }
        }

        private static byte[] CreateHeader(byte operation)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                WriteHeader(writer, operation);
                return stream.ToArray();
            }
        }

        private static void WriteHeader(BinaryWriter writer, byte operation)
        {
            writer.Write(Magic);
            writer.Write(Version);
            writer.Write(operation);
        }
    }
}
