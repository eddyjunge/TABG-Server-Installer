using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace TabgInstaller.PerformanceClient
{
    internal static class RemotePhysicsLod
    {
        private sealed class PlayerState
        {
            internal Player Player;
            internal bool OriginalSimplified;
        }

        private static readonly List<PlayerState> Players = new List<PlayerState>(64);
        private static float _nextUpdate;

        internal static void Register(Player player)
        {
            if (player == null)
                return;
            for (var index = 0; index < Players.Count; index++)
                if (Players[index].Player == player)
                    return;

            Players.Add(new PlayerState
            {
                Player = player,
                OriginalSimplified = player.isSimplified
            });
        }

        internal static void Unregister(Player player)
        {
            for (var index = Players.Count - 1; index >= 0; index--)
            {
                if (Players[index].Player != player)
                    continue;
                if (player != null)
                    player.isSimplified = Players[index].OriginalSimplified;
                Players.RemoveAt(index);
            }
        }

        internal static void UpdateIfDue(float fullPhysicsDistance)
        {
            if (!HotPathEnabled.Value || Time.unscaledTime < _nextUpdate)
                return;
            _nextUpdate = Time.unscaledTime + 0.25f;

            var local = Player.localPlayer;
            if (local == null)
                return;
            var localPosition = local.m_hip != null ? local.m_hip.transform.position : local.transform.position;
            var distanceSquared = fullPhysicsDistance * fullPhysicsDistance;

            for (var index = Players.Count - 1; index >= 0; index--)
            {
                var state = Players[index];
                var player = state.Player;
                if (player == null)
                {
                    Players.RemoveAt(index);
                    continue;
                }

                if (player == local || player == Player.spectatingPlayer)
                {
                    player.isSimplified = state.OriginalSimplified;
                    continue;
                }

                var playerPosition = player.m_hip != null ? player.m_hip.transform.position : player.transform.position;
                player.isSimplified = state.OriginalSimplified
                                      || (playerPosition - localPosition).sqrMagnitude > distanceSquared;

                if (player.isSimplified && player.rigHolder != null)
                {
                    var bodies = player.rigHolder.AllRigs;
                    for (var bodyIndex = 0; bodyIndex < bodies.Count; bodyIndex++)
                    {
                        var body = bodies[bodyIndex];
                        if (body != null && body.velocity.sqrMagnitude < 0.04f && body.angularVelocity.sqrMagnitude < 0.04f)
                            body.Sleep();
                    }
                }
            }
        }

        internal static void RestoreAll()
        {
            for (var index = 0; index < Players.Count; index++)
            {
                var state = Players[index];
                if (state.Player != null)
                    state.Player.isSimplified = state.OriginalSimplified;
            }
            Players.Clear();
        }
    }

    [HarmonyPatch(typeof(Player), "Start")]
    internal static class PlayerPhysicsLodRegisterPatch
    {
        private static void Postfix(Player __instance)
        {
            RemotePhysicsLod.Register(__instance);
        }
    }

    [HarmonyPatch(typeof(Player), "OnDestroy")]
    internal static class PlayerPhysicsLodUnregisterPatch
    {
        private static void Prefix(Player __instance)
        {
            RemotePhysicsLod.Unregister(__instance);
        }
    }

    [HarmonyPatch(typeof(Gravity), "FixedUpdate")]
    internal static class SimplifiedGravityPatch
    {
        private static bool Prefix(Gravity __instance)
        {
            if (!HotPathEnabled.Value)
                return true;
            var player = __instance.GetComponent<Player>();
            return player == null || !player.isSimplified;
        }
    }

    [HarmonyPatch(typeof(Swimming), "FixedUpdate")]
    internal static class SimplifiedSwimmingPatch
    {
        private static bool Prefix(Swimming __instance)
        {
            if (!HotPathEnabled.Value)
                return true;
            var player = __instance.GetComponent<Player>();
            return player == null || !player.isSimplified;
        }
    }

    [HarmonyPatch(typeof(Skydiving), "FixedUpdate")]
    internal static class SimplifiedSkydivingPatch
    {
        private static bool Prefix(Skydiving __instance)
        {
            if (!HotPathEnabled.Value)
                return true;
            var player = __instance.GetComponent<Player>();
            return player == null || !player.isSimplified;
        }
    }
}
