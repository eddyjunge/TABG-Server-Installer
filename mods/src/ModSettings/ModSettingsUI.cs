using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using BepInEx;
using BepInEx.Configuration;
using UnityEngine;

namespace TabgInstaller.ModSettings
{
    /// <summary>
    /// In-game mod settings menu. Press # to open.
    /// Blocks player input while open. Clean dark UI.
    /// Auto-discovers config entries from all TabgInstaller plugins.
    /// </summary>
    [BepInPlugin("tabginstaller.modsettings", "TABG Mod Settings", "1.0.0")]
    [BepInDependency("tabginstaller.flyingcontrols", BepInDependency.DependencyFlags.SoftDependency)]
    public class ModSettingsUI : BaseUnityPlugin
    {
        private ConfigEntry<KeyCode> _menuKey;
        private bool _isOpen = false;
        private bool _waitingForKey = false;
        private string _waitingForKeyId = null;
        private Vector2 _scrollPos;
        private bool _savedUsingInterface;
        private bool _hasSavedUsingInterface;

        // All registered settings
        private static readonly List<SettingEntry> _settings = new List<SettingEntry>();
        private static readonly Dictionary<string, SettingEntry> _settingsById = new Dictionary<string, SettingEntry>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> _editBuffers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private Rect _windowRect;
        private bool _dragging = false;

        private GUIStyle _windowStyle;
        private GUIStyle _headerStyle;
        private GUIStyle _labelStyle;
        private GUIStyle _valueStyle;
        private GUIStyle _buttonStyle;
        private GUIStyle _sliderStyle;
        private GUIStyle _sliderThumbStyle;
        private Texture2D _bgTex;
        private Texture2D _headerTex;
        private Texture2D _btnTex;
        private Texture2D _btnHoverTex;
        private bool _stylesInit = false;

        private class SettingEntry
        {
            public string Category;
            public string Name;
            public string Description;
            public string Id;
            public ConfigEntryBase Entry;
        }

        private void Awake()
        {
            _menuKey = Config.Bind("Menu", "OpenKey", KeyCode.F9,
                "Key to open/close mod settings");
            _windowRect = new Rect(Screen.width / 2 - 250, Screen.height / 2 - 250, 500, 500);
            Logger.LogInfo($"[ModSettings] Press {_menuKey.Value} for in-game settings");
        }

        /// <summary>Register a config entry to show in the settings menu.</summary>
        public static void Register(string category, string name, string description, ConfigEntryBase entry)
        {
            if (entry == null)
                return;

            string id = MakeSettingId(category, name, entry);
            var setting = new SettingEntry
            {
                Category = category,
                Name = name,
                Description = description,
                Id = id,
                Entry = entry
            };

            SettingEntry existing;
            if (_settingsById.TryGetValue(id, out existing))
            {
                existing.Category = category;
                existing.Name = name;
                existing.Description = description;
                existing.Entry = entry;
                return;
            }

            _settingsById[id] = setting;
            _settings.Add(setting);
        }

        private void Update()
        {
            if (_waitingForKey)
            {
                // Listen for any key press
                foreach (KeyCode kc in Enum.GetValues(typeof(KeyCode)))
                {
                    if (kc == KeyCode.None || kc == KeyCode.Mouse0 || kc == KeyCode.Escape) continue;
                    if (Input.GetKeyDown(kc))
                    {
                        // Find the setting and update it
                        foreach (var s in _settings)
                        {
                            if (s.Id == _waitingForKeyId && s.Entry is ConfigEntry<KeyCode> keyEntry)
                            {
                                keyEntry.Value = kc;
                                break;
                            }
                        }
                        _waitingForKey = false;
                        _waitingForKeyId = null;
                        return;
                    }
                }
                if (Input.GetKeyDown(KeyCode.Escape))
                {
                    _waitingForKey = false;
                    _waitingForKeyId = null;
                }
                return;
            }

            if (Input.GetKeyDown(_menuKey.Value))
            {
                if (_isOpen)
                    CloseMenu();
                else
                    OpenMenu();
            }
        }

        private void InitStyles()
        {
            if (_stylesInit) return;
            _stylesInit = true;

            _bgTex = MakeTex(1, 1, new Color(0.12f, 0.12f, 0.15f, 0.95f));
            _headerTex = MakeTex(1, 1, new Color(0.18f, 0.22f, 0.30f, 1f));
            _btnTex = MakeTex(1, 1, new Color(0.25f, 0.28f, 0.35f, 1f));
            _btnHoverTex = MakeTex(1, 1, new Color(0.35f, 0.40f, 0.50f, 1f));

            _windowStyle = new GUIStyle(GUI.skin.window);
            _windowStyle.normal.background = _bgTex;
            _windowStyle.onNormal.background = _bgTex;
            _windowStyle.padding = new RectOffset(10, 10, 10, 10);

            _headerStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 16,
                fontStyle = FontStyle.Bold,
                alignment = TextAnchor.MiddleLeft
            };
            _headerStyle.normal.textColor = new Color(0.5f, 0.8f, 1f);
            _headerStyle.normal.background = _headerTex;
            _headerStyle.padding = new RectOffset(8, 8, 4, 4);

            _labelStyle = new GUIStyle(GUI.skin.label) { fontSize = 13 };
            _labelStyle.normal.textColor = new Color(0.85f, 0.85f, 0.85f);

            _valueStyle = new GUIStyle(GUI.skin.label) { fontSize = 13, alignment = TextAnchor.MiddleRight };
            _valueStyle.normal.textColor = new Color(1f, 0.9f, 0.5f);

            _buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = 12 };
            _buttonStyle.normal.background = _btnTex;
            _buttonStyle.hover.background = _btnHoverTex;
            _buttonStyle.normal.textColor = Color.white;
            _buttonStyle.hover.textColor = Color.white;
            _buttonStyle.active.background = _btnHoverTex;
            _buttonStyle.padding = new RectOffset(8, 8, 4, 4);
        }

        private Texture2D MakeTex(int w, int h, Color c)
        {
            var t = new Texture2D(w, h);
            for (int x = 0; x < w; x++)
                for (int y = 0; y < h; y++)
                    t.SetPixel(x, y, c);
            t.Apply();
            return t;
        }

        private void OnGUI()
        {
            if (!_isOpen) return;
            InitStyles();

            _windowRect = GUI.Window(98765, _windowRect, DrawWindow, "", _windowStyle);
        }

        private void DrawWindow(int id)
        {
            // Title bar
            GUILayout.BeginHorizontal();
            GUILayout.Label("MOD SETTINGS", _headerStyle, GUILayout.Height(30));
            if (GUILayout.Button("X", _buttonStyle, GUILayout.Width(30), GUILayout.Height(30)))
            {
                CloseMenu();
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(8);

            _scrollPos = GUILayout.BeginScrollView(_scrollPos);

            string currentCategory = "";
            foreach (var s in _settings)
            {
                if (s.Category != currentCategory)
                {
                    if (currentCategory != "") GUILayout.Space(8);
                    currentCategory = s.Category;
                    GUILayout.Label(currentCategory, _headerStyle, GUILayout.Height(26));
                    GUILayout.Space(4);
                }

                GUILayout.BeginHorizontal();

                if (s.Entry is ConfigEntry<KeyCode> keyEntry)
                {
                    GUILayout.Label(s.Name, _labelStyle, GUILayout.Width(200));
                    bool isWaiting = _waitingForKey && _waitingForKeyId == s.Id;
                    string btnText = isWaiting ? ">> Press a key <<" : keyEntry.Value.ToString();
                    if (GUILayout.Button(btnText, _buttonStyle, GUILayout.Width(160)))
                    {
                        _waitingForKey = true;
                        _waitingForKeyId = s.Id;
                    }
                }
                else if (s.Entry is ConfigEntry<bool> boolEntry)
                {
                    GUILayout.Label(s.Name, _labelStyle, GUILayout.Width(200));
                    bool newVal = GUILayout.Toggle(boolEntry.Value, boolEntry.Value ? "ON" : "OFF", _buttonStyle, GUILayout.Width(60));
                    boolEntry.Value = newVal;
                }
                else if (s.Entry.SettingType.IsEnum)
                {
                    DrawEnumSetting(s);
                }
                else if (s.Entry is ConfigEntry<float> floatEntry)
                {
                    DrawFloatSetting(s, floatEntry);
                }
                else if (s.Entry is ConfigEntry<int> intEntry)
                {
                    DrawIntSetting(s, intEntry);
                }
                else if (s.Entry is ConfigEntry<string> stringEntry)
                {
                    DrawStringSetting(s, stringEntry);
                }
                else
                {
                    DrawUnsupportedSetting(s);
                }

                GUILayout.EndHorizontal();

                if (!string.IsNullOrEmpty(s.Description))
                {
                    var descStyle = new GUIStyle(_labelStyle) { fontSize = 10 };
                    descStyle.normal.textColor = new Color(0.5f, 0.5f, 0.5f);
                    GUILayout.Label("  " + s.Description, descStyle);
                }

                GUILayout.Space(2);
            }

            GUILayout.Space(10);
            var footerStyle = new GUIStyle(_labelStyle) { fontSize = 10, alignment = TextAnchor.MiddleCenter };
            footerStyle.normal.textColor = new Color(0.4f, 0.4f, 0.4f);
            GUILayout.Label($"Press {_menuKey.Value} to close  |  Settings save automatically", footerStyle);

            GUILayout.EndScrollView();
            GUI.DragWindow();
        }

        private void OpenMenu()
        {
            _isOpen = true;
            _savedUsingInterface = Player.usingInterface;
            _hasSavedUsingInterface = true;
            Player.usingInterface = true;
        }

        private void CloseMenu()
        {
            _isOpen = false;
            _waitingForKey = false;
            _waitingForKeyId = null;

            if (_hasSavedUsingInterface)
                Player.usingInterface = _savedUsingInterface;

            _hasSavedUsingInterface = false;
        }

        private void OnDestroy()
        {
            if (_isOpen)
                CloseMenu();
        }

        private static string MakeSettingId(string category, string name, ConfigEntryBase entry)
        {
            string section = entry.Definition.Section ?? string.Empty;
            string key = entry.Definition.Key ?? string.Empty;
            return (category ?? string.Empty) + "." + (name ?? string.Empty) + "." + section + "." + key;
        }

        private void DrawFloatSetting(SettingEntry setting, ConfigEntry<float> entry)
        {
            GUILayout.Label(setting.Name, _labelStyle, GUILayout.Width(160));

            float min;
            float max;
            if (TryGetAcceptableRange(entry, out min, out max))
            {
                float newVal = GUILayout.HorizontalSlider(entry.Value, min, max, GUILayout.Width(140));
                if (Mathf.Abs(newVal - entry.Value) > 0.0001f)
                    entry.Value = Mathf.Clamp(newVal, min, max);
                GUILayout.Label(entry.Value.ToString("0.###", CultureInfo.InvariantCulture), _valueStyle, GUILayout.Width(70));
                return;
            }

            string text = TextBuffer(setting, entry.Value.ToString("0.###", CultureInfo.InvariantCulture));
            string updated = GUILayout.TextField(text, GUILayout.Width(140));
            if (!string.Equals(updated, text, StringComparison.Ordinal))
            {
                _editBuffers[setting.Id] = updated;
                float parsed;
                if (float.TryParse(updated, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
                    entry.Value = parsed;
            }
            GUILayout.Label(entry.Value.ToString("0.###", CultureInfo.InvariantCulture), _valueStyle, GUILayout.Width(70));
        }

        private void DrawIntSetting(SettingEntry setting, ConfigEntry<int> entry)
        {
            GUILayout.Label(setting.Name, _labelStyle, GUILayout.Width(160));

            float min;
            float max;
            if (TryGetAcceptableRange(entry, out min, out max))
            {
                float newVal = GUILayout.HorizontalSlider(entry.Value, min, max, GUILayout.Width(140));
                int rounded = Mathf.RoundToInt(newVal);
                if (rounded != entry.Value)
                    entry.Value = Mathf.Clamp(rounded, Mathf.RoundToInt(min), Mathf.RoundToInt(max));
                GUILayout.Label(entry.Value.ToString(CultureInfo.InvariantCulture), _valueStyle, GUILayout.Width(70));
                return;
            }

            string text = TextBuffer(setting, entry.Value.ToString(CultureInfo.InvariantCulture));
            string updated = GUILayout.TextField(text, GUILayout.Width(140));
            if (!string.Equals(updated, text, StringComparison.Ordinal))
            {
                _editBuffers[setting.Id] = updated;
                int parsed;
                if (int.TryParse(updated, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed))
                    entry.Value = parsed;
            }
            GUILayout.Label(entry.Value.ToString(CultureInfo.InvariantCulture), _valueStyle, GUILayout.Width(70));
        }

        private void DrawStringSetting(SettingEntry setting, ConfigEntry<string> entry)
        {
            GUILayout.Label(setting.Name, _labelStyle, GUILayout.Width(160));
            string current = entry.Value ?? string.Empty;
            string updated = GUILayout.TextField(current, GUILayout.Width(210));
            if (!string.Equals(updated, current, StringComparison.Ordinal))
                entry.Value = updated;
        }

        private void DrawEnumSetting(SettingEntry setting)
        {
            GUILayout.Label(setting.Name, _labelStyle, GUILayout.Width(160));
            Array values = Enum.GetValues(setting.Entry.SettingType);
            string[] names = Enum.GetNames(setting.Entry.SettingType);
            int selected = Array.IndexOf(values, setting.Entry.BoxedValue);
            if (selected < 0)
                selected = 0;

            int next = GUILayout.SelectionGrid(selected, names, Math.Min(names.Length, 4), _buttonStyle, GUILayout.Width(220));
            if (next >= 0 && next < values.Length && next != selected)
                setting.Entry.BoxedValue = values.GetValue(next);
        }

        private void DrawUnsupportedSetting(SettingEntry setting)
        {
            GUILayout.Label(setting.Name, _labelStyle, GUILayout.Width(160));
            GUILayout.Label(setting.Entry.BoxedValue != null ? setting.Entry.BoxedValue.ToString() : "", _valueStyle, GUILayout.Width(210));
        }

        private static string TextBuffer(SettingEntry setting, string currentValue)
        {
            string buffer;
            if (!_editBuffers.TryGetValue(setting.Id, out buffer))
            {
                buffer = currentValue;
                _editBuffers[setting.Id] = buffer;
            }

            return buffer;
        }

        private static bool TryGetAcceptableRange(ConfigEntryBase entry, out float min, out float max)
        {
            min = 0f;
            max = 0f;

            var acceptableValues = entry.Description.AcceptableValues;
            if (acceptableValues == null)
                return false;

            Type type = acceptableValues.GetType();
            PropertyInfo minProperty = type.GetProperty("MinValue");
            PropertyInfo maxProperty = type.GetProperty("MaxValue");
            if (minProperty == null || maxProperty == null)
                return false;

            try
            {
                min = Convert.ToSingle(minProperty.GetValue(acceptableValues, null), CultureInfo.InvariantCulture);
                max = Convert.ToSingle(maxProperty.GetValue(acceptableValues, null), CultureInfo.InvariantCulture);
                return max > min;
            }
            catch
            {
                return false;
            }
        }
    }
}
