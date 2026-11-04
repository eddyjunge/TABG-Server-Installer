using System;

namespace TabgInstaller.ProximityChat.Client
{
    public static class MicFrameProcessor
    {
        public const int SampleRate = 48000;
        public const int TargetRate = 16000;
        public const int DownsampleFactor = 3;
        public const int FrameSizeMs = 20;
        public const int FrameSamples48k = SampleRate * FrameSizeMs / 1000;
        public const int FrameSamplesTarget = TargetRate * FrameSizeMs / 1000;

        public static bool TryEncodeFrame(
            float[] sampleBuffer,
            byte[] pcmFrameBuffer,
            float sensitivity,
            bool transmissionAllowed,
            bool bypassVoiceActivation,
            out int pcmLength)
        {
            pcmLength = 0;
            if (!transmissionAllowed || sampleBuffer == null || pcmFrameBuffer == null)
                return false;
            if (sampleBuffer.Length < FrameSamples48k || pcmFrameBuffer.Length < FrameSamplesTarget)
                return false;

            if (!bypassVoiceActivation)
            {
                float rms = CalculateRms(sampleBuffer);
                float threshold = Clamp(sensitivity, 0.0001f, 1f);
                if (rms < threshold)
                    return false;
            }

            for (int i = 0; i < FrameSamplesTarget; i++)
            {
                float sample = Clamp(sampleBuffer[i * DownsampleFactor], -1f, 1f);
                pcmFrameBuffer[i] = (byte)((sample * 0.5f + 0.5f) * 255f);
            }

            pcmLength = FrameSamplesTarget;
            return true;
        }

        private static float CalculateRms(float[] sampleBuffer)
        {
            float rms = 0f;
            for (int i = 0; i < FrameSamples48k; i++)
                rms += sampleBuffer[i] * sampleBuffer[i];

            return (float)Math.Sqrt(rms / FrameSamples48k);
        }

        private static float Clamp(float value, float min, float max)
        {
            if (value < min)
                return min;
            if (value > max)
                return max;
            return value;
        }
    }
}
