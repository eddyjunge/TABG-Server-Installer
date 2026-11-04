using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Landfall.Network;
using UnityEngine;

namespace TabgInstaller.PerformanceClient
{
    [HarmonyPatch(typeof(TeamMateNames), "LateUpdate")]
    internal static class TeamMateNamesNoChurnPatch
    {
        private static readonly List<TeamMateNames.PlayerNameSpot> Spots = new List<TeamMateNames.PlayerNameSpot>(64);
        private static readonly Dictionary<int, string> LastNames = new Dictionary<int, string>();
        private static readonly FieldInfo AlivePlayersField = AccessTools.Field(typeof(PhotonServerHandler), "m_AlivePlayers");
        private static readonly FieldInfo DeadPlayersField = AccessTools.Field(typeof(PhotonServerHandler), "m_DeadPlayers");

        private static bool Prefix(
            TeamMateNames __instance,
            List<TeamMateNameUI> ___teamMateUI,
            ref Camera ___cam,
            PhotonServerHandler ___m_NetworkManager,
            TABGPlayerClient[] ___m_TeamMates)
        {
            if (!HotPathEnabled.Value)
                return true;

            Spots.Clear();
            var waiting = ClientGameHandler.CurrentGameState == GameState.WaitingForPlayers
                          || ClientGameHandler.CurrentGameState == GameState.CountDown;
            if (!waiting)
            {
                var debug = __instance.debugTeammates;
                if (debug != null)
                {
                    for (var index = 0; index < debug.Length; index++)
                        if (debug[index] != null)
                            Spots.Add(new TeamMateNames.PlayerNameSpot(debug[index]));
                }

                if (PhotonServerConnector.IsNetworkMatch && ___m_TeamMates != null)
                {
                    for (var index = 0; index < ___m_TeamMates.Length; index++)
                        if (___m_TeamMates[index] != null)
                            Spots.Add(new TeamMateNames.PlayerNameSpot(___m_TeamMates[index]));
                }
            }
            else if (___m_NetworkManager != null && Player.localPlayer != null)
            {
                var origin = Player.localPlayer.m_torso != null
                    ? Player.localPlayer.m_torso.transform.position
                    : Player.localPlayer.transform.position;
                AddNearby(AlivePlayersField.GetValue(___m_NetworkManager) as List<TABGPlayerClient>, ___m_NetworkManager, origin);
                AddNearby(DeadPlayersField.GetValue(___m_NetworkManager) as List<TABGPlayerClient>, ___m_NetworkManager, origin);
            }

            Render(__instance, ___teamMateUI, ref ___cam);
            return false;
        }

        private static void AddNearby(List<TABGPlayerClient> players, PhotonServerHandler manager, Vector3 origin)
        {
            if (players == null)
                return;
            for (var index = 0; index < players.Count; index++)
            {
                var player = players[index];
                if (player != null && player.PlayerObject != null && player != manager.LocalPlayer
                    && (player.PlayerPosition - origin).sqrMagnitude < 2500f)
                    Spots.Add(new TeamMateNames.PlayerNameSpot(player));
            }
        }

        private static void Render(TeamMateNames owner, List<TeamMateNameUI> entries, ref Camera camera)
        {
            while (entries.Count < Spots.Count)
            {
                var gameObject = Object.Instantiate(owner.playerNamePrefab, owner.namesParent);
                gameObject.SetActive(true);
                entries.Add(gameObject.GetComponent<TeamMateNameUI>());
            }

            if (camera == null || !camera.gameObject.activeInHierarchy)
            {
                var viewedPlayer = Player.localPlayer != null ? Player.localPlayer : Player.spectatingPlayer;
                if (viewedPlayer != null && viewedPlayer.m_playerCamera != null)
                    camera = viewedPlayer.m_playerCamera.GetComponent<Camera>();
            }

            for (var index = 0; index < entries.Count; index++)
            {
                var entry = entries[index];
                if (entry == null)
                    continue;
                if (index >= Spots.Count || camera == null)
                {
                    SetActiveIfChanged(entry.gameObject, false);
                    continue;
                }

                var spot = Spots[index];
                var visible = !spot.IsDead
                              && (ClientGameHandler.CurrentGameState == GameState.WaitingForPlayers
                                  || ClientGameHandler.CurrentGameState == GameState.CountDown
                                  || spot.HasDropped);
                if (!visible)
                {
                    SetActiveIfChanged(entry.gameObject, false);
                    continue;
                }

                entry.worldPos = spot.posistion;
                entry.transform.position = camera.WorldToScreenPoint(entry.worldPos);
                entry.SetDistance();
                visible = camera.transform.InverseTransformPoint(entry.worldPos + Vector3.up * 1.3f).z > 0f;
                SetActiveIfChanged(entry.gameObject, visible);
                if (!visible)
                    continue;

                var id = entry.GetInstanceID();
                string previous;
                if (!LastNames.TryGetValue(id, out previous) || previous != spot.name)
                {
                    entry.image.rectTransform.sizeDelta = new Vector2(5f + spot.name.Length * 8.1f, 14.87f);
                    entry.SetPlayerName(spot.name);
                    LastNames[id] = spot.name;
                }
            }
        }

        private static void SetActiveIfChanged(GameObject gameObject, bool active)
        {
            if (gameObject.activeSelf != active)
                gameObject.SetActive(active);
        }
    }

    [HarmonyPatch(typeof(HealthBarHandler), "Update")]
    internal static class HealthBarChangeOnlyPatch
    {
        private sealed class State
        {
            internal int Health = int.MinValue;
            internal int Maximum = int.MinValue;
        }

        private static readonly Dictionary<int, State> States = new Dictionary<int, State>();

        private static bool Prefix(
            HealthBarHandler __instance,
            bool ___m_IsMainHealth,
            PlayerDeath ___death,
            Player ___player,
            ref float ___barVelocity)
        {
            if (!HotPathEnabled.Value || (___death == null && !__instance.specificHP))
                return true;

            if (___death != null)
            {
                __instance.displayDowned = ___death.isDown;
                SetActiveIfPresent(__instance.deadObj, ___death.dead);
            }

            if (__instance.displayDowned)
            {
                __instance.colorVelocity = Mathf.Clamp(__instance.colorVelocity, -1f, -0.7f);
                SetEnabledIfPresent(__instance.downedIMG, true);
                SetActiveIfPresent(__instance.deadObj, true);
            }
            else
            {
                SetEnabledIfPresent(__instance.downedIMG, false);
                SetActiveIfPresent(__instance.deadObj, false);
            }

            __instance.bar.color = Color.Lerp(
                __instance.redColor,
                Color.white,
                __instance.colorCurve.Evaluate(__instance.bar.fillAmount) + __instance.colorVelocity);
            __instance.bar.fillAmount += ___barVelocity * 4f * Time.deltaTime;

            if (___m_IsMainHealth && ___player != null && ___death != null)
            {
                var maximum = 100f * (___player.stats.healthAdd + ___player.stats.healthMultiplier);
                var health = ___death.health * (___player.stats.healthAdd + ___player.stats.healthMultiplier);
                if (__instance.barBG != null)
                    __instance.barBG.transform.localScale = new Vector3(1f + Mathf.Clamp(___player.stats.healthAdd, 0f, 3f), 1f, 1f);

                var roundedHealth = Mathf.RoundToInt(health);
                var roundedMaximum = Mathf.RoundToInt(maximum);
                State state;
                var id = __instance.GetInstanceID();
                if (!States.TryGetValue(id, out state))
                {
                    state = new State();
                    States.Add(id, state);
                }
                if (__instance.healthText != null && (state.Health != roundedHealth || state.Maximum != roundedMaximum))
                {
                    __instance.healthText.text = roundedHealth + " / " + roundedMaximum;
                    state.Health = roundedHealth;
                    state.Maximum = roundedMaximum;
                }
            }

            if (__instance.bar.fillAmount == 1f && ___barVelocity > 0.1f)
                ___barVelocity *= -0.8f;
            return false;
        }

        private static void SetActiveIfPresent(GameObject gameObject, bool active)
        {
            if (gameObject != null && gameObject.activeSelf != active)
                gameObject.SetActive(active);
        }

        private static void SetEnabledIfPresent(UnityEngine.UI.Image image, bool enabled)
        {
            if (image != null && image.enabled != enabled)
                image.enabled = enabled;
        }
    }
}
