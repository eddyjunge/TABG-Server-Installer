using System;
using System.IO;
using UnityEngine;

namespace TabgInstaller.Destruction
{
    internal struct Blast
    {
        public uint Seq;
        public Vector3 Position;
        public float Radius;
        public float Power;
    }

    internal enum DestructionOp : byte
    {
        Hello = 1,
        Accepted = 2,
        Denied = 3,
        Report = 4,
        Blast = 5,
        Reset = 6,
    }

    /// <summary>Event 246, <c>DSTR</c> v1. See crates/tabg-game/src/room/destruction.rs.</summary>
    internal static class DestructionProtocol
    {
        public const byte EventCode = 246;
        public const byte Version = 1;
        private const int HeaderLength = 6;
        private const int ReportLength = HeaderLength + 20;
        private const int BlastLength = HeaderLength + 24;

        public static byte[] Hello() => Header(DestructionOp.Hello);

        public static byte[] Report(Vector3 position, float radius, float power)
        {
            var data = new byte[ReportLength];
            WriteHeader(data, DestructionOp.Report);
            using (var w = new BinaryWriter(new MemoryStream(data, HeaderLength, 20)))
            {
                w.Write(position.x);
                w.Write(position.y);
                w.Write(position.z);
                w.Write(radius);
                w.Write(power);
            }
            return data;
        }

        public static bool TryRead(byte[] data, out DestructionOp op, out Blast blast)
        {
            op = 0;
            blast = default;
            if (data == null || data.Length < HeaderLength || data[0] != 'D' || data[1] != 'S'
                || data[2] != 'T' || data[3] != 'R' || data[4] != Version)
                return false;
            op = (DestructionOp)data[5];
            if (op != DestructionOp.Blast)
                return true;
            if (data.Length != BlastLength)
                return false;
            blast.Seq = BitConverter.ToUInt32(data, 6);
            blast.Position = new Vector3(BitConverter.ToSingle(data, 10), BitConverter.ToSingle(data, 14), BitConverter.ToSingle(data, 18));
            blast.Radius = BitConverter.ToSingle(data, 22);
            blast.Power = BitConverter.ToSingle(data, 26);
            return true;
        }

        private static byte[] Header(DestructionOp op)
        {
            var data = new byte[HeaderLength];
            WriteHeader(data, op);
            return data;
        }

        private static void WriteHeader(byte[] data, DestructionOp op)
        {
            data[0] = (byte)'D';
            data[1] = (byte)'S';
            data[2] = (byte)'T';
            data[3] = (byte)'R';
            data[4] = Version;
            data[5] = (byte)op;
        }
    }
}
