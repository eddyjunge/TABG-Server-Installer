using System;

namespace TabgInstaller.ProximityChat.Client
{
    internal static class VoicePacket
    {
        public const byte EventCode = 240;
        public const byte Version = 1;
        public const byte UnknownSender = byte.MaxValue;
        public const byte FormatPcmU8 = 1;
        public const int SampleRate = 16000;
        public const int Channels = 1;
        public const int FrameSamples = 320;
        public const int HeaderSize = 10;
        public const int MaxPcmBytes = 1024;

        public static byte[] Create(byte senderId, ushort sequence, byte[] pcmData, int pcmLength)
        {
            if (pcmData == null) throw new ArgumentNullException(nameof(pcmData));
            if (pcmLength < 0 || pcmLength > pcmData.Length || pcmLength > MaxPcmBytes)
                throw new ArgumentOutOfRangeException(nameof(pcmLength));

            byte[] packet = new byte[HeaderSize + pcmLength];
            packet[0] = Version;
            packet[1] = senderId;
            WriteUInt16(packet, 2, sequence);
            WriteUInt16(packet, 4, SampleRate);
            packet[6] = FormatPcmU8;
            packet[7] = Channels;
            WriteUInt16(packet, 8, FrameSamples);
            Buffer.BlockCopy(pcmData, 0, packet, HeaderSize, pcmLength);
            return packet;
        }

        public static bool TryRead(byte[] packet, out byte senderId, out ushort sequence, out int pcmOffset, out int pcmLength)
        {
            senderId = 0;
            sequence = 0;
            pcmOffset = 0;
            pcmLength = 0;

            if (packet == null || packet.Length < HeaderSize || packet[0] != Version)
                return false;

            ushort sampleRate = ReadUInt16(packet, 4);
            ushort frameSamples = ReadUInt16(packet, 8);
            int payloadLength = packet.Length - HeaderSize;
            if (packet[6] != FormatPcmU8 ||
                packet[7] != Channels ||
                sampleRate != SampleRate ||
                frameSamples != FrameSamples ||
                payloadLength <= 0 ||
                payloadLength > MaxPcmBytes)
            {
                return false;
            }

            senderId = packet[1];
            sequence = ReadUInt16(packet, 2);
            pcmOffset = HeaderSize;
            pcmLength = payloadLength;
            return true;
        }

        private static void WriteUInt16(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)value;
            buffer[offset + 1] = (byte)(value >> 8);
        }

        private static ushort ReadUInt16(byte[] buffer, int offset)
        {
            return (ushort)(buffer[offset] | (buffer[offset + 1] << 8));
        }
    }
}
