using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Landfall.Network;
using UnityEngine;

namespace TabgInstaller.PerformanceClient
{
    [HarmonyPatch(typeof(PhotonServerHandler), "HandlePlayerUpdate")]
    internal static class PlayerUpdateReaderPatch
    {
        private delegate TABGPlayerClient FindAlivePlayerDelegate(PhotonServerHandler handler, byte index);

        private static readonly FindAlivePlayerDelegate FindAlivePlayer = CreateFindAlivePlayerDelegate();
        private static readonly MethodInfo RespawnPlayer = AccessTools.Method(typeof(PhotonServerHandler), "RespawnPlayer");
        private static readonly Dictionary<int, TABGCarClient> CarsByIndex = new Dictionary<int, TABGCarClient>();
        private static int _cachedCarCount = -1;

        private static bool Prefix(
            PhotonServerHandler __instance,
            ClientPackage data,
            ref float ___m_LastTimeStamp,
            List<TABGCarClient> ___m_Cars)
        {
            if (!HotPathEnabled.Value)
                return true;
            var buffer = data != null ? data.Buffer : null;
            if (buffer == null || buffer.Length < 6 || FindAlivePlayer == null)
                return false;

            var offset = 0;
            var timestamp = ClientPacketReader.ReadFloat(buffer, ref offset);
            if (timestamp < ___m_LastTimeStamp)
                return false;

            var playerCount = buffer[offset++];
            for (var entityIndex = 0; entityIndex < playerCount; entityIndex++)
            {
                if (offset + 3 > buffer.Length)
                    return false;
                var playerIndex = buffer[offset++];
                var flags = (PacketContainerFlags)buffer[offset++];
                var drivingState = (DrivingState)buffer[offset++];
                var hasPosition = Has(flags, PacketContainerFlags.PlayerPosition);
                var hasRotation = Has(flags, PacketContainerFlags.PlayerRotation);
                var hasDirection = Has(flags, PacketContainerFlags.PlayerDirection);
                var hasCarPosition = Has(flags, PacketContainerFlags.CarPosition);
                var hasCarRotation = Has(flags, PacketContainerFlags.CarRotation);
                var hasCarInput = Has(flags, PacketContainerFlags.CarInput);

                if ((drivingState & DrivingState.Driving) == DrivingState.Driving)
                {
                    var carPosition = hasCarPosition ? ClientPacketReader.ReadVector3(buffer, ref offset) : Vector3.zero;
                    var carRotation = hasCarRotation ? ClientPacketReader.ReadQuaternion(buffer, ref offset) : Quaternion.identity;
                    var carInput = hasCarInput ? ClientPacketReader.ReadDirection(buffer, ref offset) : Vector3.zero;
                    var playerRotation = hasRotation ? ClientPacketReader.ReadVector2(buffer, ref offset) : Vector2.zero;
                    var carState = (CarDrivingState)buffer[offset++];
                    var player = FindAlivePlayer(__instance, playerIndex);
                    if (player == null || player == __instance.LocalPlayer)
                        continue;

                    var currentCar = player.CurrentCar;
                    if (currentCar == null)
                        continue;
                    if (hasRotation)
                        player.UpdateRotation(playerRotation);
                    var car = currentCar.CarReference;
                    if (car == null)
                    {
                        if (hasCarPosition)
                            player.UpdatePosition(carPosition);
                        continue;
                    }
                    if (!hasCarPosition)
                        carPosition = car.recievedPosition;
                    if (!hasCarRotation)
                        carRotation = car.recievedRotation;
                    if (!hasCarInput)
                        carInput = car.input.inputDirection;
                    car.NetworkUpdate(carPosition, carRotation, carInput, carState);
                    continue;
                }

                var position = hasPosition ? ClientPacketReader.ReadVector3(buffer, ref offset) : Vector3.zero;
                if (drivingState != DrivingState.Slow)
                {
                    var rotation = hasRotation ? ClientPacketReader.ReadVector2(buffer, ref offset) : Vector2.zero;
                    var ads = buffer[offset++] != 0;
                    var moveDirection = hasDirection ? ClientPacketReader.ReadDirection(buffer, ref offset) : Vector3.zero;
                    var movementType = buffer[offset++];
                    var player = FindAlivePlayer(__instance, playerIndex);
                    if (player == null || player == __instance.LocalPlayer)
                        continue;

                    if (player.PlayerObject == null)
                    {
                        var spawnPosition = hasPosition ? position : player.PlayerPosition;
                        RespawnPlayer?.Invoke(__instance, new object[]
                        {
                            player,
                            player.Health,
                            spawnPosition + Vector3.up * -0.9387f,
                            player.PlayerIndex,
                            0f
                        });
                    }

                    if (hasPosition)
                        player.UpdatePosition(position);
                    if (hasDirection)
                        player.UpdateMovementDirection(moveDirection);
                    player.UpdateMovementType(movementType);
                    if (hasRotation)
                        player.UpdateRotation(rotation);
                    player.ChangeAimDownSightState(ads);
                    var networkPlayer = player.NetworkPlayerHandler;
                    if (networkPlayer != null && movementType.IsBitSet(7))
                        networkPlayer.NetworkJump();
                }
                else
                {
                    var player = FindAlivePlayer(__instance, playerIndex);
                    var yaw = ClientPacketReader.ReadFloat(buffer, ref offset);
                    if (player != null && playerIndex != __instance.LocalPlayer.PlayerIndex)
                    {
                        var rotation = player.PlayerRotation;
                        rotation.y = yaw;
                        if (hasPosition)
                            player.UpdatePosition(position);
                        player.UpdateRotation(rotation);
                    }
                }
            }

            ___m_LastTimeStamp = timestamp;
            if (offset >= buffer.Length)
                return false;
            var carCount = buffer[offset++];
            EnsureCarLookup(___m_Cars);
            for (var index = 0; index < carCount; index++)
            {
                var carIndex = ClientPacketReader.ReadInt(buffer, ref offset);
                var position = ClientPacketReader.ReadVector3(buffer, ref offset);
                var rotation = ClientPacketReader.ReadQuaternion(buffer, ref offset);
                TABGCarClient clientCar;
                if (CarsByIndex.TryGetValue(carIndex, out clientCar) && clientCar != null && clientCar.CarReference != null)
                    clientCar.CarReference.NetworkUpdate(position, rotation, Vector3.zero, CarDrivingState.None);
            }
            return false;
        }

        private static bool Has(PacketContainerFlags value, PacketContainerFlags flag)
        {
            return value == PacketContainerFlags.All || (value & flag) != PacketContainerFlags.Nothing;
        }

        private static FindAlivePlayerDelegate CreateFindAlivePlayerDelegate()
        {
            var method = AccessTools.Method(typeof(PhotonServerHandler), "FindAlivePlayer");
            return method == null
                ? null
                : (FindAlivePlayerDelegate)Delegate.CreateDelegate(typeof(FindAlivePlayerDelegate), null, method);
        }

        private static void EnsureCarLookup(List<TABGCarClient> cars)
        {
            if (cars == null || cars.Count == _cachedCarCount)
                return;
            CarsByIndex.Clear();
            for (var index = 0; index < cars.Count; index++)
            {
                var car = cars[index];
                if (car != null)
                    CarsByIndex[car.CarIndex] = car;
            }
            _cachedCarCount = cars.Count;
        }
    }

    internal static class ClientPacketReader
    {
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
        private struct FloatInt
        {
            [System.Runtime.InteropServices.FieldOffset(0)] internal float Float;
            [System.Runtime.InteropServices.FieldOffset(0)] internal int Int;
        }

        internal static int ReadInt(byte[] source, ref int offset)
        {
            var value = source[offset]
                        | source[offset + 1] << 8
                        | source[offset + 2] << 16
                        | source[offset + 3] << 24;
            offset += 4;
            return value;
        }

        internal static short ReadShort(byte[] source, ref int offset)
        {
            var value = (short)(source[offset] | source[offset + 1] << 8);
            offset += 2;
            return value;
        }

        internal static float ReadFloat(byte[] source, ref int offset)
        {
            var converter = new FloatInt { Int = ReadInt(source, ref offset) };
            return converter.Float;
        }

        internal static Vector2 ReadVector2(byte[] source, ref int offset)
        {
            return new Vector2(ReadFloat(source, ref offset), ReadFloat(source, ref offset));
        }

        internal static Vector3 ReadVector3(byte[] source, ref int offset)
        {
            return new Vector3(
                ReadFloat(source, ref offset),
                ReadFloat(source, ref offset),
                ReadFloat(source, ref offset));
        }

        internal static Vector3 ReadDirection(byte[] source, ref int offset)
        {
            var result = new Vector3(
                (source[offset] - 100f) / 100f,
                (source[offset + 1] - 100f) / 100f,
                (source[offset + 2] - 100f) / 100f);
            offset += 3;
            return result;
        }

        internal static Quaternion ReadQuaternion(byte[] source, ref int offset)
        {
            var largest = source[offset++];
            if (largest >= 4)
            {
                return new Quaternion(
                    largest == 4 ? 1f : 0f,
                    largest == 5 ? 1f : 0f,
                    largest == 6 ? 1f : 0f,
                    largest == 7 ? 1f : 0f);
            }

            var first = ReadShort(source, ref offset) / 10000f;
            var second = ReadShort(source, ref offset) / 10000f;
            var third = ReadShort(source, ref offset) / 10000f;
            var missing = Mathf.Sqrt(Mathf.Max(0f, 1f - first * first - second * second - third * third));
            switch (largest)
            {
                case 0: return new Quaternion(missing, first, second, third);
                case 1: return new Quaternion(first, missing, second, third);
                case 2: return new Quaternion(first, second, missing, third);
                default: return new Quaternion(first, second, third, missing);
            }
        }
    }
}
