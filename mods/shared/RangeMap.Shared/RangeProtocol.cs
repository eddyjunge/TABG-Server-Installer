using System;
using System.IO;

namespace TabgInstaller.RangeMap
{
    internal static class RangeProtocol
    {
        internal const byte EventCode = 242;
        internal const byte Version = 2;
        internal const byte Hello = 1;
        internal const byte Accepted = 2;
        internal const byte GiveItem = 3;
        internal const byte ItemGranted = 4;
        internal const byte ItemDenied = 5;

        private static readonly byte[] Magic = { (byte)'R', (byte)'N', (byte)'G', (byte)'E' };

        internal static byte[] CreateHello()
        {
            return CreateHeader(Hello);
        }

        internal static byte[] CreateAccepted()
        {
            return CreateHeader(Accepted);
        }

        internal static byte[] CreateGiveItem(int itemId, byte quantity)
        {
            return CreateItemMessage(GiveItem, itemId, quantity);
        }

        internal static byte[] CreateItemGranted(int itemId, byte quantity)
        {
            return CreateItemMessage(ItemGranted, itemId, quantity);
        }

        internal static byte[] CreateItemDenied(int itemId)
        {
            return CreateItemMessage(ItemDenied, itemId, 0);
        }

        private static byte[] CreateItemMessage(byte operation, int itemId, byte quantity)
        {
            using (var stream = new MemoryStream())
            using (var writer = new BinaryWriter(stream))
            {
                WriteHeader(writer, operation);
                writer.Write(itemId);
                writer.Write(quantity);
                return stream.ToArray();
            }
        }

        internal static bool TryRead(byte[] data, out byte operation, out int itemId, out byte quantity)
        {
            operation = 0;
            itemId = -1;
            quantity = 0;
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
                    if (operation == GiveItem || operation == ItemGranted || operation == ItemDenied)
                    {
                        if (stream.Length - stream.Position < 5)
                            return false;
                        itemId = reader.ReadInt32();
                        quantity = reader.ReadByte();
                    }

                    return true;
                }
            }
            catch (Exception)
            {
                return false;
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
