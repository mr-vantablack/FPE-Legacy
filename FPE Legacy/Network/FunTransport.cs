using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FPE_Legacy.Rpc;
using Il2CppFishNet.Connection;
using Il2CppFishNet.Object;
using Il2CppFishNet.Transporting;
using UnityEngine;

namespace FPE_Legacy.Networking
{
    internal static class FunTransport
    {
        private static NetworkBehaviour _carrier;
        private static object _manager;
        private static float _nextFind;
        internal static bool IsServer { get; private set; }
        internal static bool IsClient { get; private set; }
        internal static int LocalClientId { get; private set; } = -1;
        internal static void Poll()
        {
            if (Time.unscaledTime >= _nextFind)
            {
                _nextFind = Time.unscaledTime + .5f;
                _carrier = UnityEngine.Object.FindObjectsOfType<NetworkBehaviour>().FirstOrDefault(n => n != null && n.IsSpawned && (n.IsServerInitialized || n.IsClientInitialized));
                if (_carrier != null) _manager = _carrier.NetworkManager;
            }
            object server = Get(_manager, "ServerManager"), client = Get(_manager, "ClientManager");
            IsServer = Bool(server, "Started", _carrier != null && _carrier.IsServerInitialized);
            IsClient = Bool(client, "Started", _carrier != null && _carrier.IsClientInitialized);
            var connection = Get(client, "Connection") as NetworkConnection;
            LocalClientId = connection?.ClientId ?? -1;
        }
        private static object Get(object o, string name) { try { return o?.GetType().GetProperty(name)?.GetValue(o); } catch { return null; } }
        private static bool Bool(object o, string name, bool fallback) => Get(o, name) is bool b ? b : fallback;
        internal static NetworkConnection Connection(int id) => FunRPCRuntime.GetServerConnection(id);
        internal static bool ToServer(string name, params object[] values)
        {
            if (!IsClient || _carrier == null || !_carrier.IsSpawned || !_carrier.IsClientInitialized) return false;
            return FunRPCRuntime.SendServer(name, Quiet(Channel.Reliable), values);
        }
        internal static FunRPCSendOptions Quiet(Channel channel) => new() { Channel = channel, ExcludeServer = true, ValidateTarget = true, SuppressLog = true };
        internal static NetworkBehaviour TargetCarrier(NetworkConnection target)
        {
            // Use the target's own object so the client can resolve the carrier.
            if (target?.FirstObject == null) return null;
            foreach (var component in target.FirstObject.GetComponents<NetworkBehaviour>())
                if (component != null && component.IsSpawned && component.IsServerInitialized) return component;
            return null;
        }
        internal static bool ToClient(int clientId, string name, Channel channel, params object[] values)
        {
            var target = Connection(clientId); var carrier = TargetCarrier(target);
            return carrier != null && FunRPCRuntime.SendTargetFrom(carrier, target, name, Quiet(channel), values);
        }
        internal static long CarrierKey(int id) => TargetCarrier(Connection(id))?.Pointer.ToInt64() ?? 0;
    }
}
