using FluentAssertions;
using TabgInstaller.ProximityChat.Client;
using Xunit;

namespace TabgInstaller.Tests.ProximityChat
{
    public class MicFrameProcessorTests
    {
        [Fact]
        public void TryEncodeFrame_WhenTransmissionBlocked_DrainsWithoutEmitting()
        {
            float[] samples = FilledFrame(0.5f);
            byte[] pcm = new byte[MicFrameProcessor.FrameSamplesTarget];

            bool emitted = MicFrameProcessor.TryEncodeFrame(
                samples,
                pcm,
                sensitivity: 0.01f,
                transmissionAllowed: false,
                bypassVoiceActivation: true,
                out int pcmLength);

            emitted.Should().BeFalse();
            pcmLength.Should().Be(0);
            pcm.Should().OnlyContain(value => value == 0);
        }

        [Fact]
        public void TryEncodeFrame_WhenVoiceActivationEnabled_UsesSensitivityThreshold()
        {
            float[] quietSamples = FilledFrame(0.001f);
            byte[] pcm = new byte[MicFrameProcessor.FrameSamplesTarget];

            bool emitted = MicFrameProcessor.TryEncodeFrame(
                quietSamples,
                pcm,
                sensitivity: 0.01f,
                transmissionAllowed: true,
                bypassVoiceActivation: false,
                out int pcmLength);

            emitted.Should().BeFalse();
            pcmLength.Should().Be(0);
        }

        [Fact]
        public void TryEncodeFrame_WhenPushToTalkAllowed_BypassesVoiceActivation()
        {
            float[] quietSamples = FilledFrame(0.001f);
            byte[] pcm = new byte[MicFrameProcessor.FrameSamplesTarget];

            bool emitted = MicFrameProcessor.TryEncodeFrame(
                quietSamples,
                pcm,
                sensitivity: 0.01f,
                transmissionAllowed: true,
                bypassVoiceActivation: true,
                out int pcmLength);

            emitted.Should().BeTrue();
            pcmLength.Should().Be(MicFrameProcessor.FrameSamplesTarget);
            pcm.Should().Contain(value => value != 0);
        }

        private static float[] FilledFrame(float sampleValue)
        {
            var samples = new float[MicFrameProcessor.FrameSamples48k];
            for (int i = 0; i < samples.Length; i++)
                samples[i] = sampleValue;

            return samples;
        }
    }
}
