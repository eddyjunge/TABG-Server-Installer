using BepInEx;
using BepInEx.Configuration;
using TabgInstaller.ModSettings;
using UnityEngine;

namespace TabgInstaller.CoordsDisplay
{
    [BepInPlugin("tabginstaller.coordsdisplay", "TABG Coords Display", "1.0.0")]
    [BepInDependency("tabginstaller.modsettings", BepInDependency.DependencyFlags.HardDependency)]
    public class CoordsDisplayPlugin : BaseUnityPlugin
    {
        private ConfigEntry<KeyCode> _toggleKey;
        private ConfigEntry<int> _fontSize;

        private bool _showCoords = false;
        private GUIStyle _style;
        private GUIStyle _shadowStyle;

        private void Awake()
        {
            _toggleKey = Config.Bind("Keybinds", "ToggleCoords", KeyCode.F5,
                "Key to toggle coordinate display on/off");
            _fontSize = Config.Bind("Display", "FontSize", 18,
                "Font size for coordinate text");

            // Register with mod settings UI
            ModSettings.ModSettingsUI.Register("Coords Display", "Toggle Key", "Key to show/hide coordinates", _toggleKey);
            ModSettings.ModSettingsUI.Register("Coords Display", "Font Size", "Size of coordinate text", _fontSize);

            Logger.LogInfo($"[CoordsDisplay] Loaded. Press {_toggleKey.Value} to toggle coords.");
        }

        private void Update()
        {
            if (Input.GetKeyDown(_toggleKey.Value))
                _showCoords = !_showCoords;
        }

        private void OnGUI()
        {
            if (!_showCoords || Player.localPlayer == null) return;

            if (_style == null || _style.fontSize != _fontSize.Value)
            {
                _style = new GUIStyle(GUI.skin.label)
                {
                    fontSize = _fontSize.Value,
                    fontStyle = FontStyle.Bold
                };
                _style.normal.textColor = Color.white;

                _shadowStyle = new GUIStyle(_style);
                _shadowStyle.normal.textColor = Color.black;
            }

            Vector3 pos = Player.localPlayer.m_hip != null
                ? Player.localPlayer.m_hip.transform.position
                : Player.localPlayer.transform.position;
            string text = $"X: {pos.x:F1}  Y: {pos.y:F1}  Z: {pos.z:F1}";

            GUI.Label(new Rect(12, 12, 500, 40), text, _shadowStyle);
            GUI.Label(new Rect(10, 10, 500, 40), text, _style);
        }
    }
}
