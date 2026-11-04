using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace TabgInstaller.ProximityChat.Client
{
    public class VoicePlayback : IDisposable
    {
        private const int MaxSources = 32;
        private const int MaxQueuedFrames = 256;
        private const int MaxFramesPerTick = 128;

        private readonly Dictionary<int, PlayerVoiceSource> _sources = new Dictionary<int, PlayerVoiceSource>();
        private readonly List<int> _sourcesToRemove = new List<int>();
        private float _masterVolume;
        private float _minRange;
        private float _maxRange;
        private AudioRolloffMode _rolloffMode;

        private readonly ConcurrentQueue<QueuedAudio> _audioQueue = new ConcurrentQueue<QueuedAudio>();
        private int _queuedFrameCount;

        private struct QueuedAudio
        {
            public int SenderId;
            public ushort Sequence;
            public byte[] PcmData;
            public int PcmOffset;
            public int PcmLength;
        }

        private readonly Dictionary<int, Transform> _playerTransformCache = new Dictionary<int, Transform>();
        private float _nextPlayerCacheRefresh;

        public VoicePlayback(float masterVolume)
        {
            _masterVolume = masterVolume;
        }

        public void UpdateConfig(float minRange, float maxRange, byte falloffCurve, float masterVolume)
        {
            _minRange = minRange;
            _maxRange = maxRange;
            _rolloffMode = falloffCurve == 0 ? AudioRolloffMode.Linear : AudioRolloffMode.Logarithmic;
            _masterVolume = masterVolume;

            foreach (var kvp in _sources)
                kvp.Value.UpdateConfig(_minRange, _maxRange, _rolloffMode, _masterVolume);
        }

        public void EnqueueAudio(int senderId, ushort sequence, byte[] pcmData, int pcmOffset, int pcmLength)
        {
            _audioQueue.Enqueue(new QueuedAudio
            {
                SenderId = senderId,
                Sequence = sequence,
                PcmData = pcmData,
                PcmOffset = pcmOffset,
                PcmLength = pcmLength
            });

            if (Interlocked.Increment(ref _queuedFrameCount) > MaxQueuedFrames &&
                _audioQueue.TryDequeue(out _))
            {
                Interlocked.Decrement(ref _queuedFrameCount);
            }
        }

        public void Tick()
        {
            if (Time.unscaledTime >= _nextPlayerCacheRefresh)
            {
                RefreshPlayerCache();
                _nextPlayerCacheRefresh = Time.unscaledTime + 0.5f;
            }

            int processed = 0;
            while (processed < MaxFramesPerTick && _audioQueue.TryDequeue(out var queued))
            {
                Interlocked.Decrement(ref _queuedFrameCount);
                ProcessAudioOnMainThread(queued.SenderId, queued.Sequence, queued.PcmData, queued.PcmOffset, queued.PcmLength);
                processed++;
            }

            foreach (var kvp in _sources)
            {
                if (!kvp.Value.IsAttached && _playerTransformCache.TryGetValue(kvp.Key, out var transform))
                    kvp.Value.AttachToPlayer(transform);
            }

            foreach (var kvp in _sources)
                kvp.Value.FlushJitterBuffer();

            _sourcesToRemove.Clear();
            foreach (var kvp in _sources)
            {
                if (Time.unscaledTime - kvp.Value.LastReceiveTime > 0.5f)
                    _sourcesToRemove.Add(kvp.Key);
            }

            foreach (int id in _sourcesToRemove)
            {
                _sources[id].Dispose();
                _sources.Remove(id);
            }
        }

        private void ProcessAudioOnMainThread(int senderId, ushort sequence, byte[] pcmData, int pcmOffset, int pcmLength)
        {
            if (!_sources.TryGetValue(senderId, out var source))
            {
                if (_sources.Count >= MaxSources) return;
                source = new PlayerVoiceSource(senderId, _minRange, _maxRange, _rolloffMode, _masterVolume);
                _sources[senderId] = source;

                if (_playerTransformCache.TryGetValue(senderId, out var transform))
                    source.AttachToPlayer(transform);
            }

            source.BufferPcmFrame(sequence, pcmData, pcmOffset, pcmLength);
            source.LastReceiveTime = Time.unscaledTime;
        }

        private void RefreshPlayerCache()
        {
            _playerTransformCache.Clear();
            try
            {
                var handler = Landfall.Network.PhotonServerHandler.instance;
                if (handler == null) return;
                var players = handler.AllPlayers;
                if (players == null) return;
                foreach (var player in players)
                {
                    if (player != null && player.PlayerObject != null)
                        _playerTransformCache[(int)player.PlayerIndex] = player.PlayerObject.transform;
                }
            }
            catch (Exception ex) { Debug.LogWarning($"[VoicePlayback] Player transform cache failed: {ex.Message}"); }
        }

        public Dictionary<int, Transform> GetPlayerTransformCache()
        {
            return _playerTransformCache;
        }

        public bool IsPlayerTalking(int playerId)
        {
            return _sources.ContainsKey(playerId);
        }

        public IEnumerable<int> GetTalkingPlayerIds()
        {
            return _sources.Keys;
        }

        public void Dispose()
        {
            foreach (var kvp in _sources)
                kvp.Value.Dispose();
            _sources.Clear();
        }

        private class PlayerVoiceSource : IDisposable
        {
            private const int StartupJitterFrames = 2;
            private const int MaxJitterFrames = 8;

            private readonly GameObject _go;
            private readonly VoiceAudioFilter _filter;
            private readonly AudioSource _audioSource;
            private readonly SortedDictionary<ushort, PcmFrame> _jitterBuffer = new SortedDictionary<ushort, PcmFrame>();
            private ushort _expectedSequence;
            private ushort _firstBufferedSequence;
            private bool _hasExpectedSequence;
            private bool _hasFirstBufferedSequence;

            public float LastReceiveTime;
            public bool IsAttached { get; private set; }

            public PlayerVoiceSource(int playerId, float minDist, float maxDist, AudioRolloffMode rolloff, float masterVol)
            {
                _go = new GameObject($"VoiceSource_{playerId}");
                UnityEngine.Object.DontDestroyOnLoad(_go);

                // AudioSource needed for OnAudioFilterRead to fire
                _audioSource = _go.AddComponent<AudioSource>();
                _audioSource.loop = true;
                _audioSource.dopplerLevel = 0f;
                _audioSource.bypassEffects = true;
                _audioSource.bypassListenerEffects = true;
                _audioSource.bypassReverbZones = true;
                UpdateConfig(minDist, maxDist, rolloff, masterVol);

                // Play a 1-second silent clip so OnAudioFilterRead gets called continuously
                // Use 48kHz for the clip so Unity drives the audio thread at 48kHz
                _audioSource.clip = AudioClip.Create($"Silent_{playerId}", 48000, 1, 48000, false);
                _audioSource.Play();

                // VoiceAudioFilter reads from its ring buffer and writes into the audio pipeline
                _filter = _go.AddComponent<VoiceAudioFilter>();

                LastReceiveTime = Time.unscaledTime;
            }

            public void UpdateConfig(float minDist, float maxDist, AudioRolloffMode rolloff, float masterVol)
            {
                if (_audioSource == null) return;

                _audioSource.spatialBlend = 1f;
                _audioSource.volume = masterVol;
                _audioSource.minDistance = minDist;
                _audioSource.maxDistance = maxDist;
                _audioSource.rolloffMode = rolloff;
            }

            public void BufferPcmFrame(ushort sequence, byte[] pcmData, int pcmOffset, int pcmLength)
            {
                if (_filter == null || pcmData == null || pcmLength <= 0) return;

                if (_hasExpectedSequence && IsOlder(sequence, _expectedSequence))
                    return;

                if (!_jitterBuffer.ContainsKey(sequence))
                {
                    if (_jitterBuffer.Count >= MaxJitterFrames)
                        return;

                    _jitterBuffer.Add(sequence, new PcmFrame
                    {
                        Data = pcmData,
                        Offset = pcmOffset,
                        Length = pcmLength
                    });

                    if (!_hasFirstBufferedSequence)
                    {
                        _firstBufferedSequence = sequence;
                        _hasFirstBufferedSequence = true;
                    }
                }
            }

            public void FlushJitterBuffer()
            {
                if (_jitterBuffer.Count == 0)
                    return;

                if (!_hasExpectedSequence)
                {
                    if (_jitterBuffer.Count < StartupJitterFrames)
                        return;

                    _expectedSequence = _firstBufferedSequence;
                    _hasExpectedSequence = true;
                }

                int flushed = 0;
                while (_jitterBuffer.TryGetValue(_expectedSequence, out var frame))
                {
                    FeedFrame(frame);
                    _jitterBuffer.Remove(_expectedSequence);
                    _expectedSequence++;
                    flushed++;

                    if (flushed >= 4)
                        return;
                }

                if (_jitterBuffer.Count >= MaxJitterFrames - 2)
                {
                    _expectedSequence = FirstSequenceAtOrAfter(_expectedSequence);
                }
            }

            public void AttachToPlayer(Transform playerTransform)
            {
                if (_go != null && playerTransform != null)
                {
                    _go.transform.SetParent(playerTransform, false);
                    _go.transform.localPosition = Vector3.zero;
                    IsAttached = true;
                }
            }

            public void Dispose()
            {
                if (_go != null) UnityEngine.Object.Destroy(_go);
            }

            private ushort FirstSequenceAtOrAfter(ushort expected)
            {
                ushort bestSequence = expected;
                ushort bestDistance = ushort.MaxValue;

                foreach (ushort sequence in _jitterBuffer.Keys)
                {
                    ushort distance = (ushort)(sequence - expected);
                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        bestSequence = sequence;
                    }
                }

                return bestSequence;
            }

            private void FeedFrame(PcmFrame frame)
            {
                _filter.FeedPcmU8(frame.Data, frame.Offset, frame.Length);
            }

            private static bool IsOlder(ushort sequence, ushort expected)
            {
                return sequence != expected && (ushort)(expected - sequence) < 32768;
            }

            private struct PcmFrame
            {
                public byte[] Data;
                public int Offset;
                public int Length;
            }
        }
    }

    // MonoBehaviour that streams audio via OnAudioFilterRead.
    // Must be a public non-nested class so Unity's AddComponent can find it.
    public class VoiceAudioFilter : MonoBehaviour
    {
        private readonly float[] _buffer = new float[32000]; // 2 sec at 16kHz
        private int _writePos;
        private int _readPos;
        private int _available; // how many 16kHz samples are ready to play

        private int _outputCounter;
        private float _lastSample;

        public void FeedPcmU8(byte[] pcmData, int offset, int length)
        {
            if (pcmData == null || length <= 0) return;

            lock (this)
            {
                int end = Math.Min(offset + length, pcmData.Length);
                for (int i = offset; i < end; i++)
                {
                    _buffer[_writePos] = (pcmData[i] / 255f) * 2f - 1f;
                    _writePos = (_writePos + 1) % _buffer.Length;
                    _available = Math.Min(_available + 1, _buffer.Length);
                }
            }
        }

        // Called by Unity's audio thread at 48kHz.
        // Each 16kHz sample must be held for 3 output samples (48000/16000 = 3).
        private void OnAudioFilterRead(float[] data, int channels)
        {
            lock (this)
            {
                for (int i = 0; i < data.Length; i += channels)
                {
                    float sample = _lastSample;
                    if (_outputCounter == 0 && _available > 0)
                    {
                        sample = _buffer[_readPos];
                        _buffer[_readPos] = 0f;
                        _readPos = (_readPos + 1) % _buffer.Length;
                        _available--;
                        _lastSample = sample;
                    }
                    _outputCounter = (_outputCounter + 1) % 3; // 48000/16000

                    for (int ch = 0; ch < channels; ch++)
                        data[i + ch] = sample;
                }
            }
        }
    }
}
