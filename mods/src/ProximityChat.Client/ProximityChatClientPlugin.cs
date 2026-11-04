using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using Landfall.Network;
using UnityEngine;

namespace TabgInstaller.ProximityChat.Client
{
    [BepInPlugin("tabginstaller.proximitychat.client", "Proximity Voice Chat Client", "1.0.0")]
    [BepInDependency("tabginstaller.modsettings", BepInDependency.DependencyFlags.SoftDependency)]
    public class ProximityChatClientPlugin : BaseUnityPlugin
    {
        public static ConfigEntry<bool> Enabled;
        public static ConfigEntry<float> MicSensitivity;
        public static ConfigEntry<float> MasterVolume;
        public static ConfigEntry<string> MicrophoneDevice;
        public static ConfigEntry<bool> PushToTalkEnabled;
        public static ConfigEntry<KeyCode> PushToTalkKey;
        public static ConfigEntry<float> MinRange;
        public static ConfigEntry<float> MaxRange;
        public static ConfigEntry<string> FalloffCurve;

        private MicCapture _micCapture;
        private VoicePlayback _playback;
        private SpeakerIcon _speakerIcon;
        private Harmony _harmony;
        private bool _started;
        private ushort _nextSequence;

        internal static ProximityChatClientPlugin Instance;

        private void Awake()
        {
            Instance = this;
            Enabled = Config.Bind("ProximityChat", "Enabled", true, "Enable/disable voice chat. Turning this off immediately stops microphone recording and network sends.");
            MicSensitivity = Config.Bind("ProximityChat", "MicSensitivity", 0.01f,
                new ConfigDescription("Voice activity detection threshold (RMS). Lower values transmit quieter audio.",
                    new AcceptableValueRange<float>(0.0001f, 0.25f)));
            MasterVolume = Config.Bind("ProximityChat", "MasterVolume", 1.0f,
                new ConfigDescription("Overall voice chat volume.", new AcceptableValueRange<float>(0f, 2f)));
            MicrophoneDevice = Config.Bind("ProximityChat", "MicrophoneDevice", "", "Microphone device name (empty = system default)");
            PushToTalkEnabled = Config.Bind("ProximityChat", "PushToTalkEnabled", false, "Require PushToTalkKey to transmit. When false, MicSensitivity is used as the voice activation threshold.");
            PushToTalkKey = Config.Bind("ProximityChat", "PushToTalkKey", KeyCode.V, "Key held while transmitting when push-to-talk is enabled.");
            MinRange = Config.Bind("ProximityChat", "MinRange", 5f,
                new ConfigDescription("Distance within which received voice is full volume.", new AcceptableValueRange<float>(0f, 500f)));
            MaxRange = Config.Bind("ProximityChat", "MaxRange", 50f,
                new ConfigDescription("Distance at which received voice becomes inaudible.", new AcceptableValueRange<float>(1f, 1000f)));
            FalloffCurve = Config.Bind("ProximityChat", "FalloffCurve", "Linear", "Volume falloff: Linear or Logarithmic");
            Enabled.SettingChanged += (_, __) =>
            {
                if (!Enabled.Value)
                    StopVoice();
            };

            try
            {
                TabgInstaller.ModSettings.ModSettingsUI.Register("Proximity Chat", "Enabled", "Toggle voice chat on/off", Enabled);
                TabgInstaller.ModSettings.ModSettingsUI.Register("Proximity Chat", "Mic Sensitivity", "VAD threshold", MicSensitivity);
                TabgInstaller.ModSettings.ModSettingsUI.Register("Proximity Chat", "Push To Talk", "Hold a key to transmit instead of voice activation", PushToTalkEnabled);
                TabgInstaller.ModSettings.ModSettingsUI.Register("Proximity Chat", "Push Key", "Push-to-talk transmit key", PushToTalkKey);
                TabgInstaller.ModSettings.ModSettingsUI.Register("Proximity Chat", "Master Volume", "Voice chat volume", MasterVolume);
            }
            catch (Exception ex) { Logger.LogDebug($"[ProximityChat] ModSettings registration failed: {ex.Message}"); }

            _speakerIcon = new SpeakerIcon();
            _playback = new VoicePlayback(MasterVolume.Value);
            ApplyPlaybackConfig();

            // Patch client message receiving to intercept voice packets
            _harmony = new Harmony("tabginstaller.proximitychat.client");
            _harmony.PatchAll(typeof(VoiceReceivePatch));

            Logger.LogInfo("[ProximityChat] Client plugin loaded — using game network.");
        }

        private void Update()
        {
            if (!Enabled.Value)
            {
                if (_started)
                    StopVoice();
                return;
            }

            // Start mic when entering a game session
            if (!_started && IsInGameSession())
            {
                StartVoice();
            }

            if (_started)
            {
                bool pushToTalkHeld = PushToTalkEnabled.Value && Input.GetKey(PushToTalkKey.Value);
                bool transmissionAllowed = !PushToTalkEnabled.Value || pushToTalkHeld;
                _micCapture?.ProcessMicData(MicSensitivity.Value, transmissionAllowed, PushToTalkEnabled.Value);
                ApplyPlaybackConfig();
                _playback?.Tick();

                if (_playback != null)
                {
                    _speakerIcon.UpdatePlayerCache(_playback.GetPlayerTransformCache());
                    foreach (int id in _playback.GetTalkingPlayerIds())
                        _speakerIcon.SetTalking(id, true);
                }
            }

            // Cleanup when leaving
            if (_started && !IsInGameSession())
            {
                StopVoice();
            }
        }

        private bool IsInGameSession()
        {
            try
            {
                // Try multiple detection methods
                var connector = ServerConnector.Instance;
                if (connector != null) return true;
                var handler = PhotonServerHandler.instance;
                if (handler != null) return true;
            }
            catch (Exception ex) { Logger.LogDebug($"[ProximityChat] Server detection failed: {ex.Message}"); }
            return false;
        }

        private void StartVoice()
        {
            if (!Enabled.Value)
                return;

            try
            {
                if (Microphone.devices.Length > 0)
                {
                    _micCapture = new MicCapture(MicrophoneDevice.Value);
                    _micCapture.OnPcmFrameReady += OnPcmFrameReady;
                    _micCapture.StartRecording();
                    Logger.LogInfo("[ProximityChat] Microphone started.");
                }
                else
                {
                    Logger.LogWarning("[ProximityChat] No microphone — receive-only mode.");
                }
                _started = true;
            }
            catch (Exception ex)
            {
                Logger.LogError($"[ProximityChat] Start failed: {ex.Message}");
            }
        }

        private int _sendCount;
        private void OnPcmFrameReady(byte[] pcmData, int pcmLength)
        {
            try
            {
                if (!Enabled.Value)
                    return;

                var connector = ServerConnector.Instance;
                if (connector == null) return;

                byte[] packet = VoicePacket.Create(VoicePacket.UnknownSender, _nextSequence++, pcmData, pcmLength);
                connector.SendMessageToServer((EventCode)VoicePacket.EventCode, packet, false);
                _sendCount++;
                if (_sendCount % 250 == 1)
                    Logger.LogInfo($"[ProximityChat] Sent PCM voice frame #{_sendCount} ({pcmLength} bytes)");
            }
            catch (Exception ex) { Logger.LogDebug($"[ProximityChat] Voice send failed: {ex.Message}"); }
        }

        /// <summary>
        /// Harmony prefix patch on ServerConnector.OnEvent to intercept incoming voice packets (EventCode 240).
        /// Parses PCM voice packets and enqueues them for jitter-buffered playback.
        /// </summary>
        [HarmonyPatch(typeof(ServerConnector), "OnEvent")]
        internal static class VoiceReceivePatch
        {
            private static int _recvCount;
            static bool Prefix(ClientPackage clientPackage)
            {
                if ((byte)clientPackage.Code != VoicePacket.EventCode) return true;

                try
                {
                    _recvCount++;
                if (Instance == null || Instance._playback == null)
                {
                    return false;
                }
                    if (!Enabled.Value)
                    {
                        if (Instance._started)
                            Instance.StopVoice();
                        return false;
                    }
                    // Auto-start if we receive voice but haven't started yet
                    if (!Instance._started)
                    {
                        Instance._started = true;
                        Instance.Logger.LogInfo("[ProximityChat] Auto-started on first voice receive.");
                    }

                    byte[] data = clientPackage.Buffer;
                    if (!VoicePacket.TryRead(data, out byte senderIndex, out ushort sequence, out int pcmOffset, out int pcmLength))
                    {
                        return false;
                    }

                    Instance._playback.EnqueueAudio(senderIndex, sequence, data, pcmOffset, pcmLength);
                    if (_recvCount % 50 == 1)
                        Instance.Logger.LogInfo($"[ProximityChat] Received PCM voice #{_recvCount} from player {senderIndex} seq {sequence} ({pcmLength} bytes)");
                }
                catch (Exception ex)
                {
                    if (Instance != null) Instance.Logger.LogError($"[ProximityChat] Receive error: {ex}");
                }

                return false;
            }
        }

        private void StopVoice()
        {
            _micCapture?.Dispose();
            _micCapture = null;
            _playback?.Dispose();
            _playback = new VoicePlayback(MasterVolume.Value); // Reset for next game
            ApplyPlaybackConfig();
            _started = false;
        }

        private void ApplyPlaybackConfig()
        {
            float minRange = Mathf.Max(0f, MinRange.Value);
            float maxRange = Mathf.Max(minRange + 1f, MaxRange.Value);
            byte falloff = string.Equals(FalloffCurve.Value, "Logarithmic", StringComparison.OrdinalIgnoreCase) ? (byte)1 : (byte)0;
            _playback?.UpdateConfig(minRange, maxRange, falloff, MasterVolume.Value);
        }

        private void OnGUI()
        {
            if (Enabled.Value && _started)
                _speakerIcon?.OnGUI();
        }

        private void OnDestroy()
        {
            _micCapture?.Dispose();
            _playback?.Dispose();
            _harmony?.UnpatchSelf();
        }
    }
}
