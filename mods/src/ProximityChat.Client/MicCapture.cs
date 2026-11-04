using System;
using UnityEngine;

namespace TabgInstaller.ProximityChat.Client
{
    public class MicCapture : IDisposable
    {
        private AudioClip _micClip;
        private int _lastSamplePos;
        private readonly float[] _sampleBuffer = new float[MicFrameProcessor.FrameSamples48k];
        private readonly byte[] _pcmFrameBuffer = new byte[MicFrameProcessor.FrameSamplesTarget];
        private readonly string _deviceName;
        private bool _recording;

        public event Action<byte[], int> OnPcmFrameReady;

        public MicCapture(string deviceName)
        {
            _deviceName = string.IsNullOrEmpty(deviceName) ? null : deviceName;
        }

        public void StartRecording()
        {
            if (_recording) return;
            string device = _deviceName;
            _micClip = Microphone.Start(device, true, 2, MicFrameProcessor.SampleRate);
            _lastSamplePos = 0;
            _recording = true;
        }

        public void StopRecording()
        {
            if (!_recording) return;
            Microphone.End(_deviceName);
            _recording = false;
        }

        public void ProcessMicData(float sensitivity, bool transmissionAllowed, bool bypassVoiceActivation)
        {
            if (!_recording || _micClip == null) return;

            int currentPos = Microphone.GetPosition(_deviceName);
            if (currentPos == _lastSamplePos) return;

            int available;
            if (currentPos > _lastSamplePos)
                available = currentPos - _lastSamplePos;
            else
                available = (_micClip.samples - _lastSamplePos) + currentPos;

            while (available >= MicFrameProcessor.FrameSamples48k)
            {
                _micClip.GetData(_sampleBuffer, _lastSamplePos);
                _lastSamplePos = (_lastSamplePos + MicFrameProcessor.FrameSamples48k) % _micClip.samples;
                available -= MicFrameProcessor.FrameSamples48k;

                if (MicFrameProcessor.TryEncodeFrame(
                    _sampleBuffer,
                    _pcmFrameBuffer,
                    sensitivity,
                    transmissionAllowed,
                    bypassVoiceActivation,
                    out int pcmLength))
                {
                    OnPcmFrameReady?.Invoke(_pcmFrameBuffer, pcmLength);
                }
            }
        }

        public void Dispose()
        {
            StopRecording();
        }
    }
}
