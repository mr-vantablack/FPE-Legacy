using HarmonyLib;
using Il2CppFishNet.Connection;
using Il2CppFishNet.Object;
using Il2CppFishNet.Serializing;
using Il2CppFishNet.Transporting;
using MelonLoader;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEngine;

namespace FPE_Legacy.Rpc
{
    public enum FunRPCKind
    {
        Server,
        Observers,
        Target
    }

    internal enum FunRPCValueType : byte
    {
        Null = 0,
        Boolean = 1,
        Byte = 2,
        SByte = 3,
        Int16 = 4,
        UInt16 = 5,
        Int32 = 6,
        UInt32 = 7,
        Int64 = 8,
        UInt64 = 9,
        Single = 10,
        Double = 11,
        String = 12,
        Char = 13,
        Vector2 = 20,
        Vector3 = 21,
        Vector4 = 22,
        Vector2Int = 23,
        Vector3Int = 24,
        Quaternion = 25,
        Color = 26,
        Color32 = 27,
        Enum = 40,
        Custom = 128
    }

    /// <summary>
    /// FishNet hashes reserved by the mod RPC bridge,they are not registered in FishNet RPC tables
    /// harmony consumes them before FishNet tries to dispatch them
    /// </summary>
    public static class FunRPCProtocol
    {
        public const uint ServerHash = 240;
        public const uint ObserversHash = 241;
        public const uint TargetHash = 242;

        public const byte Version = 2;
        public const string Magic = "FPE_LEGACY_MOD_RPC";

        public static uint GetHash(FunRPCKind kind)
        {
            switch (kind)
            {
                case FunRPCKind.Server: return ServerHash;
                case FunRPCKind.Observers: return ObserversHash;
                case FunRPCKind.Target: return TargetHash;
                default: throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }
    }

    /// <summary>
    /// Information about the RPC which is currently executing
    /// sender is only populated for Server RPCs
    /// </summary>
    public sealed class FunRPCContext
    {
        public FunRPCKind Kind { get; internal set; }
        public NetworkBehaviour Carrier { get; internal set; }
        public NetworkConnection Sender { get; internal set; }
        public Channel Channel { get; internal set; }

        public int SenderClientId
        {
            get { return Sender == null ? -1 : Sender.ClientId; }
        }
    }

    /// <summary>
    /// Type-safe access to RPC arguments
    /// </summary>
    public sealed class FunRPCArguments
    {
        private readonly object[] _values;

        internal FunRPCArguments(object[] values)
        {
            _values = values ?? Array.Empty<object>();
        }

        public int Count { get { return _values.Length; } }
        public object this[int index] { get { return _values[index]; } }
        public object[] ToArray() { return (object[])_values.Clone(); }

        public T Get<T>(int index)
        {
            if (index < 0 || index >= _values.Length)
                throw new IndexOutOfRangeException($"RPC argument {index} does not exist. Count={_values.Length}.");

            object value = _values[index];
            if (value == null)
            {
                Type requestedType = typeof(T);
                if (!requestedType.IsValueType || Nullable.GetUnderlyingType(requestedType) != null)
                    return default(T);

                throw new InvalidCastException($"RPC argument {index} is null but {requestedType.FullName} is a value type.");
            }

            if (value is T)
                return (T)value;

            Type wanted = typeof(T);
            if (wanted.IsEnum)
            {
                if (value.GetType().IsEnum)
                    return (T)Enum.ToObject(wanted, Convert.ToInt64(value, CultureInfo.InvariantCulture));

                if (value is IConvertible)
                    return (T)Enum.ToObject(wanted, Convert.ToInt64(value, CultureInfo.InvariantCulture));
            }

            if (value is IConvertible && typeof(IConvertible).IsAssignableFrom(wanted))
                return (T)Convert.ChangeType(value, wanted, CultureInfo.InvariantCulture);

            throw new InvalidCastException($"RPC argument {index} is {value.GetType().FullName}, cannot convert to {wanted.FullName}.");
        }
    }

    /// <summary>
    /// Optional send settings. Defaults mirror ordinary FishNet RPC behaviour as closely as possible
    /// </summary>
    public sealed class FunRPCSendOptions
    {
        public Channel Channel = Channel.Reliable;
        public bool SuppressLog = false;
        public DataOrderType OrderType = DataOrderType.Default;

        // ObserversRpc options.
        public bool BufferLast = false;
        public bool ExcludeServer = false;
        public bool ExcludeOwner = false;

        // TargetRpc option.
        public bool ValidateTarget = true;

        public static FunRPCSendOptions Default
        {
            get { return new FunRPCSendOptions(); }
        }
    }

    internal interface IFunRPCCustomSerializer
    {
        byte TypeId { get; }
        Type ValueType { get; }
        void Write(PooledWriter writer, object value);
        object Read(PooledReader reader);
    }

    internal sealed class FunRPCCustomSerializer<T> : IFunRPCCustomSerializer
    {
        public byte TypeId { get; private set; }
        public Type ValueType { get { return typeof(T); } }

        private readonly Action<PooledWriter, T> _writer;
        private readonly Func<PooledReader, T> _reader;

        public FunRPCCustomSerializer(byte typeId, Action<PooledWriter, T> writer, Func<PooledReader, T> reader)
        {
            TypeId = typeId;
            _writer = writer;
            _reader = reader;
        }

        public void Write(PooledWriter writer, object value)
        {
            _writer(writer, (T)value);
        }

        public object Read(PooledReader reader)
        {
            return _reader(reader);
        }
    }

    /// <summary>
    /// Serializer used inside the custom RPC payload. Unlike FishNet Weaver this shit runs entirely at runtime
    /// </summary>
    public static class FunRPCSerializer
    {
        private static readonly Dictionary<Type, IFunRPCCustomSerializer> _customByType =
            new Dictionary<Type, IFunRPCCustomSerializer>();

        private static readonly Dictionary<byte, IFunRPCCustomSerializer> _customById =
            new Dictionary<byte, IFunRPCCustomSerializer>();

        /// <summary>
        /// Registers a custom data type. IDs 128..255 are reserved for user serializers
        /// both host and client must register the same id/type pair
        /// </summary>
        public static void RegisterCustom<T>(byte typeId, Action<PooledWriter, T> writer, Func<PooledReader, T> reader)
        {
            if (typeId < (byte)FunRPCValueType.Custom)
                throw new ArgumentOutOfRangeException(nameof(typeId), "Custom RPC type ids must be in range 128..255.");
            if (writer == null)
                throw new ArgumentNullException(nameof(writer));
            if (reader == null)
                throw new ArgumentNullException(nameof(reader));

            Type type = typeof(T);
            if (_customByType.ContainsKey(type))
                throw new InvalidOperationException($"Custom RPC serializer for {type.FullName} is already registered.");
            if (_customById.ContainsKey(typeId))
                throw new InvalidOperationException($"Custom RPC serializer id {typeId} is already registered.");

            FunRPCCustomSerializer<T> serializer = new FunRPCCustomSerializer<T>(typeId, writer, reader);
            _customByType.Add(type, serializer);
            _customById.Add(typeId, serializer);
        }

        public static void WriteArguments(PooledWriter writer, object[] args)
        {
            if (args == null)
            {
                writer.WriteUInt16(0);
                return;
            }

            if (args.Length > ushort.MaxValue)
                throw new InvalidOperationException("Too many RPC arguments.");

            writer.WriteUInt16((ushort)args.Length);
            for (int i = 0; i < args.Length; i++)
                WriteValue(writer, args[i]);
        }

        public static object[] ReadArguments(PooledReader reader)
        {
            ushort count = reader.ReadUInt16();
            object[] result = new object[count];

            for (int i = 0; i < count; i++)
                result[i] = ReadValue(reader);

            return result;
        }

        private static void WriteValue(PooledWriter writer, object value)
        {
            if (value == null)
            {
                writer.WriteUInt8Unpacked((byte)FunRPCValueType.Null);
                return;
            }

            Type type = value.GetType();
            IFunRPCCustomSerializer custom;
            if (_customByType.TryGetValue(type, out custom))
            {
                writer.WriteUInt8Unpacked(custom.TypeId);
                custom.Write(writer, value);
                return;
            }

            if (type.IsEnum)
            {
                writer.WriteUInt8Unpacked((byte)FunRPCValueType.Enum);
                writer.WriteString(type.AssemblyQualifiedName);
                writer.WriteInt64(Convert.ToInt64(value, CultureInfo.InvariantCulture));
                return;
            }

            if (value is bool)
            {
                writer.WriteUInt8Unpacked((byte)FunRPCValueType.Boolean);
                writer.WriteBoolean((bool)value);
            }
            else if (value is byte)
            {
                writer.WriteUInt8Unpacked((byte)FunRPCValueType.Byte);
                writer.WriteUInt8Unpacked((byte)value);
            }
            else if (value is sbyte)
            {
                writer.WriteUInt8Unpacked((byte)FunRPCValueType.SByte);
                writer.WriteInt8Unpacked((sbyte)value);
            }
            else if (value is short)
            {
                writer.WriteUInt8Unpacked((byte)FunRPCValueType.Int16);
                writer.WriteInt16((short)value);
            }
            else if (value is ushort)
            {
                writer.WriteUInt8Unpacked((byte)FunRPCValueType.UInt16);
                writer.WriteUInt16((ushort)value);
            }
            else if (value is int)
            {
                writer.WriteUInt8Unpacked((byte)FunRPCValueType.Int32);
                writer.WriteInt32((int)value);
            }
            else if (value is uint)
            {
                writer.WriteUInt8Unpacked((byte)FunRPCValueType.UInt32);
                writer.WriteUInt32((uint)value);
            }
            else if (value is long)
            {
                writer.WriteUInt8Unpacked((byte)FunRPCValueType.Int64);
                writer.WriteInt64((long)value);
            }
            else if (value is ulong)
            {
                writer.WriteUInt8Unpacked((byte)FunRPCValueType.UInt64);
                writer.WriteUInt64((ulong)value);
            }
            else if (value is float)
            {
                writer.WriteUInt8Unpacked((byte)FunRPCValueType.Single);
                writer.WriteSingle((float)value);
            }
            else if (value is double)
            {
                writer.WriteUInt8Unpacked((byte)FunRPCValueType.Double);
                writer.WriteDouble((double)value);
            }
            else if (value is string)
            {
                writer.WriteUInt8Unpacked((byte)FunRPCValueType.String);
                writer.WriteString((string)value);
            }
            else if (value is char)
            {
                writer.WriteUInt8Unpacked((byte)FunRPCValueType.Char);
                writer.WriteChar((char)value);
            }
            else if (value is Vector2)
            {
                writer.WriteUInt8Unpacked((byte)FunRPCValueType.Vector2);
                writer.WriteVector2((Vector2)value);
            }
            else if (value is Vector3)
            {
                writer.WriteUInt8Unpacked((byte)FunRPCValueType.Vector3);
                writer.WriteVector3((Vector3)value);
            }
            else if (value is Vector4)
            {
                writer.WriteUInt8Unpacked((byte)FunRPCValueType.Vector4);
                writer.WriteVector4((Vector4)value);
            }
            else if (value is Vector2Int)
            {
                writer.WriteUInt8Unpacked((byte)FunRPCValueType.Vector2Int);
                writer.WriteVector2Int((Vector2Int)value);
            }
            else if (value is Vector3Int)
            {
                writer.WriteUInt8Unpacked((byte)FunRPCValueType.Vector3Int);
                writer.WriteVector3Int((Vector3Int)value);
            }
            else if (value is Quaternion)
            {
                writer.WriteUInt8Unpacked((byte)FunRPCValueType.Quaternion);
                writer.WriteQuaternionUnpacked((Quaternion)value);
            }
            else if (value is Color)
            {
                writer.WriteUInt8Unpacked((byte)FunRPCValueType.Color);
                writer.WriteColorUnpacked((Color)value);
            }
            else if (value is Color32)
            {
                writer.WriteUInt8Unpacked((byte)FunRPCValueType.Color32);
                writer.WriteColor32((Color32)value);
            }
            else
            {
                throw new NotSupportedException($"RPC type {type.FullName} is not supported. Register it with FunRPCSerializer.RegisterCustom<T>().");
            }
        }

        private static object ReadValue(PooledReader reader)
        {
            byte rawType = reader.ReadUInt8Unpacked();
            if (rawType >= (byte)FunRPCValueType.Custom)
            {
                IFunRPCCustomSerializer custom;
                if (!_customById.TryGetValue(rawType, out custom))
                    throw new InvalidOperationException($"No custom RPC serializer registered for type id {rawType}.");

                return custom.Read(reader);
            }

            FunRPCValueType type = (FunRPCValueType)rawType;
            switch (type)
            {
                case FunRPCValueType.Null: return null;
                case FunRPCValueType.Boolean: return reader.ReadBoolean();
                case FunRPCValueType.Byte: return reader.ReadUInt8Unpacked();
                case FunRPCValueType.SByte: return reader.ReadInt8Unpacked();
                case FunRPCValueType.Int16: return reader.ReadInt16();
                case FunRPCValueType.UInt16: return reader.ReadUInt16();
                case FunRPCValueType.Int32: return reader.ReadInt32();
                case FunRPCValueType.UInt32: return reader.ReadUInt32();
                case FunRPCValueType.Int64: return reader.ReadInt64();
                case FunRPCValueType.UInt64: return reader.ReadUInt64();
                case FunRPCValueType.Single: return reader.ReadSingle();
                case FunRPCValueType.Double: return reader.ReadDouble();
                case FunRPCValueType.String: return reader.ReadStringAllocated();
                case FunRPCValueType.Char: return reader.ReadChar();
                case FunRPCValueType.Vector2: return reader.ReadVector2();
                case FunRPCValueType.Vector3: return reader.ReadVector3();
                case FunRPCValueType.Vector4: return reader.ReadVector4();
                case FunRPCValueType.Vector2Int: return reader.ReadVector2Int();
                case FunRPCValueType.Vector3Int: return reader.ReadVector3Int();
                case FunRPCValueType.Quaternion: return reader.ReadQuaternionUnpacked();
                case FunRPCValueType.Color: return reader.ReadColorUnpacked();
                case FunRPCValueType.Color32: return reader.ReadColor32();
                case FunRPCValueType.Enum:
                {
                    string typeName = reader.ReadStringAllocated();
                    long rawValue = reader.ReadInt64();
                    Type enumType = Type.GetType(typeName, false);
                    if (enumType != null && enumType.IsEnum)
                        return Enum.ToObject(enumType, rawValue);

                    // The enum type may be unavailable on this side; preserving the numeric value is safer.
                    return rawValue;
                }
                default:
                    throw new InvalidOperationException($"Unknown RPC value type {rawType}.");
            }
        }
    }

    public static class FishNetPrivate
    {
        private static MethodInfo _readRpcHash;

        public static uint ReadRpcHash(NetworkBehaviour behaviour, PooledReader reader)
        {
            if (_readRpcHash == null)
            {
                _readRpcHash = AccessTools.Method(typeof(NetworkBehaviour), "ReadRpcHash", new System.Type[] { typeof(PooledReader) });

                if (_readRpcHash == null)
                    throw new MissingMethodException("FishNet NetworkBehaviour.ReadRpcHash was not found.");
            }

            object result = _readRpcHash.Invoke(behaviour, new object[] { reader });
            return Convert.ToUInt32(result, CultureInfo.InvariantCulture);
        }
    }

    public static class FunRPCRuntime
    {
        private static NetworkBehaviour _clientCarrier;
        private static NetworkBehaviour _serverCarrier;

        private static readonly Dictionary<string, Action<FunRPCContext, FunRPCArguments>> _serverHandlers =
            new Dictionary<string, Action<FunRPCContext, FunRPCArguments>>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Action<FunRPCContext, FunRPCArguments>> _observersHandlers =
            new Dictionary<string, Action<FunRPCContext, FunRPCArguments>>(StringComparer.Ordinal);
        private static readonly Dictionary<string, Action<FunRPCContext, FunRPCArguments>> _targetHandlers =
            new Dictionary<string, Action<FunRPCContext, FunRPCArguments>>(StringComparer.Ordinal);

        public static void Initialize()
        {
            MelonLogger.Msg("[FunRPC] Runtime v2 ready.");
            MelonLogger.Msg($"[FunRPC] Hashes: Server={FunRPCProtocol.ServerHash}, Observers={FunRPCProtocol.ObserversHash}, Target={FunRPCProtocol.TargetHash}");
        }

        // registration

        public static void RegisterServer(string name, Action<FunRPCContext, FunRPCArguments> handler)
        {
            RegisterHandler(_serverHandlers, name, handler, FunRPCKind.Server);
        }

        public static void RegisterObservers(string name, Action<FunRPCContext, FunRPCArguments> handler)
        {
            RegisterHandler(_observersHandlers, name, handler, FunRPCKind.Observers);
        }

        public static void RegisterTarget(string name, Action<FunRPCContext, FunRPCArguments> handler)
        {
            RegisterHandler(_targetHandlers, name, handler, FunRPCKind.Target);
        }

        public static bool Unregister(FunRPCKind kind, string name)
        {
            return GetHandlers(kind).Remove(name);
        }

        private static void RegisterHandler(Dictionary<string, Action<FunRPCContext, FunRPCArguments>> handlers, string name, Action<FunRPCContext, FunRPCArguments> handler, FunRPCKind kind)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("RPC name cannot be empty.", nameof(name));
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            handlers[name] = handler;
            MelonLogger.Msg($"[FunRPC] Registered {kind} handler '{name}'.");
        }

        // attribute RPC support
        public static bool IsLocalOwner(object instance)
        {
            Component component = instance as Component;
            var customIdentity = FPE_Legacy.Networking.FunNetwork.GetIdentity(component);
            if (customIdentity != null) return customIdentity.IsOwner;
            if (component == null)
                return false;

            try
            {
                NetworkBehaviour[] behaviours = component.gameObject.GetComponents<NetworkBehaviour>();

                foreach (NetworkBehaviour behaviour in behaviours)
                {
                    if (behaviour == null)
                        continue;

                    if (behaviour.IsSpawned && behaviour.IsClientInitialized && behaviour.IsOwner)
                        return true;
                }

                NetworkBehaviour parent = component.GetComponentInParent<NetworkBehaviour>();

                if (parent != null && parent.IsSpawned && parent.IsClientInitialized && parent.IsOwner)
                    return true;
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[FunRPC] IsLocalOwner failed: " + e.Message);
            }

            return false;
        }
        internal static NetworkBehaviour GetCarrierForInstance(object instance, FunRPCKind kind, NetworkConnection target = null)
        {
            Component component = instance as Component;
            if (component != null)
            {
                try
                {
                    NetworkBehaviour[] localBehaviours = component.gameObject.GetComponents<NetworkBehaviour>();
                    foreach (NetworkBehaviour nb in localBehaviours)
                    {
                        if (kind == FunRPCKind.Server)
                        {
                            if (IsValidClientCarrier(nb))
                                return nb;
                        }
                        else
                        {
                            if (IsValidServerCarrier(nb))
                                return nb;
                        }
                    }

                    NetworkBehaviour parent = component.GetComponentInParent<NetworkBehaviour>();
                    if (parent != null)
                    {
                        if (kind == FunRPCKind.Server && IsValidClientCarrier(parent))
                            return parent;
                        if (kind != FunRPCKind.Server && IsValidServerCarrier(parent))
                            return parent;
                    }
                }
                catch (Exception e)
                {
                    MelonLogger.Warning($"[FunRPC] Could not select carrier from component {component.GetType().FullName}: {e.Message}");
                }
            }

            if (kind == FunRPCKind.Server)
                return GetClientCarrier();

            if (kind == FunRPCKind.Target && target != null)
                return FindTargetCarrier(target);

            return GetServerCarrier();
        }

        internal static bool SendServerFrom(NetworkBehaviour carrier, string rpcName, FunRPCSendOptions options, params object[] args)
        {
            if (!IsValidClientCarrier(carrier))
            {
                MelonLogger.Error($"[FunRPC] Invalid CLIENT carrier for attributed ServerRpc '{rpcName}'.");
                return false;
            }

            return SendPayload(FunRPCKind.Server, carrier, null, rpcName, options ?? FunRPCSendOptions.Default, args);
        }

        internal static bool SendObserversFrom(NetworkBehaviour carrier, string rpcName, FunRPCSendOptions options, params object[] args)
        {
            if (!IsValidServerCarrier(carrier))
            {
                MelonLogger.Error($"[FunRPC] Invalid SERVER carrier for attributed ObserversRpc '{rpcName}'.");
                return false;
            }

            return SendPayload(FunRPCKind.Observers, carrier, null, rpcName, options ?? FunRPCSendOptions.Default, args);
        }

        internal static bool SendTargetFrom(NetworkBehaviour carrier, NetworkConnection target, string rpcName, FunRPCSendOptions options, params object[] args)
        {
            if (target == null)
            {
                MelonLogger.Error($"[FunRPC] Target is null for attributed TargetRpc '{rpcName}'.");
                return false;
            }

            if (!IsValidServerCarrier(carrier))
                carrier = FindTargetCarrier(target);

            if (!IsValidServerCarrier(carrier))
            {
                MelonLogger.Error($"[FunRPC] Invalid SERVER carrier for attributed TargetRpc '{rpcName}'.");
                return false;
            }

            return SendPayload(FunRPCKind.Target, carrier, target, rpcName, options ?? FunRPCSendOptions.Default, args);
        }


        public static bool SendServer(string rpcName, params object[] args)
        {
            return SendServer(rpcName, FunRPCSendOptions.Default, args);
        }

        public static bool SendServer(string rpcName, FunRPCSendOptions options, params object[] args)
        {
            NetworkBehaviour carrier = GetClientCarrier();
            if (carrier == null)
            {
                MelonLogger.Error("[FunRPC] No suitable CLIENT carrier found.");
                return false;
            }

            if (options == null)
                options = FunRPCSendOptions.Default;

            return SendPayload(FunRPCKind.Server, carrier, null, rpcName, options, args);
        }


        public static bool SendObservers(string rpcName, params object[] args)
        {
            return SendObservers(rpcName, FunRPCSendOptions.Default, args);
        }

        public static bool SendObservers(string rpcName, FunRPCSendOptions options, params object[] args)
        {
            NetworkBehaviour carrier = GetServerCarrier();
            if (carrier == null)
            {
                MelonLogger.Error("[FunRPC] No suitable SERVER carrier found. This must be called by the host/server.");
                return false;
            }

            if (options == null)
                options = FunRPCSendOptions.Default;

            return SendPayload(FunRPCKind.Observers, carrier, null, rpcName, options, args);
        }

        public static bool SendTarget(NetworkConnection target, string rpcName, params object[] args)
        {
            return SendTarget(target, rpcName, FunRPCSendOptions.Default, args);
        }

        public static bool SendTarget(NetworkConnection target, string rpcName, FunRPCSendOptions options, params object[] args)
        {
            if (target == null)
            {
                MelonLogger.Error("[FunRPC] SendTarget target is null.");
                return false;
            }

            if (options == null)
                options = FunRPCSendOptions.Default;

            NetworkBehaviour carrier = FindTargetCarrier(target);
            if (carrier == null)
            {
                MelonLogger.Error($"[FunRPC] No suitable TargetRpc carrier found for ClientId={target.ClientId}.");
                return false;
            }

            return SendPayload(FunRPCKind.Target, carrier, target, rpcName, options, args);
        }

        public static bool SendTarget(int clientId, string rpcName, params object[] args)
        {
            NetworkConnection target = GetServerConnection(clientId);
            if (target == null)
            {
                MelonLogger.Error($"[FunRPC] Server connection ClientId={clientId} was not found.");
                return false;
            }

            return SendTarget(target, rpcName, args);
        }

        public static NetworkConnection GetServerConnection(int clientId)
        {
            NetworkBehaviour serverCarrier = GetServerCarrier();
            if (serverCarrier == null || serverCarrier.NetworkManager == null || serverCarrier.NetworkManager.ServerManager == null)
                return null;

            try
            {
                var clients = serverCarrier.NetworkManager.ServerManager.Clients;
                if (clients != null && clients.ContainsKey(clientId))
                    return clients[clientId];
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[FunRPC] Could not read ServerManager.Clients: {e.Message}");
            }

            return null;
        }

        public static NetworkConnection GetFirstRemoteServerConnection()
        {
            NetworkBehaviour serverCarrier = GetServerCarrier();
            if (serverCarrier == null || serverCarrier.NetworkManager == null || serverCarrier.NetworkManager.ServerManager == null)
                return null;

            try
            {
                foreach (NetworkConnection connection in serverCarrier.NetworkManager.ServerManager.Clients.Values)
                {
                    if (connection != null && !connection.IsLocalClient)
                        return connection;
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[FunRPC] Could not enumerate ServerManager.Clients: {e.Message}");
            }

            return null;
        }

        private static bool SendPayload(FunRPCKind kind, NetworkBehaviour carrier, NetworkConnection target, string rpcName, FunRPCSendOptions options, object[] args)
        {
            if (string.IsNullOrWhiteSpace(rpcName))
            {
                MelonLogger.Error("[FunRPC] RPC name cannot be empty.");
                return false;
            }

            PooledWriter writer = WriterPool.Retrieve();
            try
            {
                WriteEnvelope(writer, rpcName, args);
                uint hash = FunRPCProtocol.GetHash(kind);

                switch (kind)
                {
                    case FunRPCKind.Server:
                        carrier.SendServerRpc(hash, writer, options.Channel, options.OrderType);
                        break;

                    case FunRPCKind.Observers:
                        carrier.SendObserversRpc(hash, writer, options.Channel, options.OrderType, options.BufferLast, options.ExcludeServer, options.ExcludeOwner);
                        break;

                    case FunRPCKind.Target:
                        carrier.SendTargetRpc(hash, writer, options.Channel, options.OrderType, target, options.ExcludeServer, options.ValidateTarget);
                        break;

                    default:
                        throw new ArgumentOutOfRangeException(nameof(kind));
                }

                if (!options.SuppressLog) MelonLogger.Msg($"[FunRPC] Sent {kind} '{rpcName}' args={args?.Length ?? 0} " + $"via {carrier.gameObject.name}[{carrier.ObjectId}:{carrier.ComponentIndex}]" + (target == null ? string.Empty : $" -> ClientId={target.ClientId}"));

                return true;
            }
            catch (Exception e)
            {
                MelonLogger.Error($"[FunRPC] Failed to send {kind} '{rpcName}':\n{e}");

                if (kind == FunRPCKind.Server)
                    _clientCarrier = null;
                else
                    _serverCarrier = null;

                return false;
            }
            finally
            {
                writer.Store();
            }
        }

        private static void WriteEnvelope(PooledWriter writer, string rpcName, object[] args)
        {
            writer.WriteString(FunRPCProtocol.Magic);
            writer.WriteUInt8Unpacked(FunRPCProtocol.Version);
            writer.WriteString(rpcName);
            FunRPCSerializer.WriteArguments(writer, args);
        }

        public static bool TryConsumeIncoming(FunRPCKind kind, NetworkBehaviour carrier, bool fromRpcLink, uint hash, PooledReader reader, NetworkConnection sender, Channel channel)
        {
            int startPosition = reader.Position;
            bool recognized = false;

            try
            {
                uint actualHash = hash;
                if (!fromRpcLink)
                    actualHash = FishNetPrivate.ReadRpcHash(carrier, reader);

                if (actualHash != FunRPCProtocol.GetHash(kind))
                {
                    reader.Position = startPosition;
                    return false;
                }

                string magic = reader.ReadStringAllocated();
                if (magic != FunRPCProtocol.Magic)
                {
                    // same numerical hash belongs to a real FishNet RPC. Let FishNet handle it
                    reader.Position = startPosition;
                    return false;
                }

                recognized = true;
                byte version = reader.ReadUInt8Unpacked();
                if (version != FunRPCProtocol.Version)
                {
                    MelonLogger.Error($"[FunRPC] Received unsupported RPC protocol version {version}; expected {FunRPCProtocol.Version}.");
                    return true;
                }

                string rpcName = reader.ReadStringAllocated();
                object[] values = FunRPCSerializer.ReadArguments(reader);

                FunRPCContext context = new FunRPCContext
                {
                    Kind = kind,
                    Carrier = carrier,
                    Sender = sender,
                    Channel = channel
                };

                Dispatch(kind, rpcName, context, new FunRPCArguments(values));
                return true;
            }
            catch (Exception e)
            {
                if (!recognized) { reader.Position = startPosition; return false; }
                // once magic has been matched the packet is ours. Do not send it into FishNet's regular dispatch table or it would report an unknown hash and corrupt the remaining packet stream
                MelonLogger.Error($"[FunRPC] Failed to read incoming {kind} RPC:\n{e}");
                return true;
            }
        }

        private static void Dispatch(FunRPCKind kind, string rpcName, FunRPCContext context, FunRPCArguments args)
        {
            Dictionary<string, Action<FunRPCContext, FunRPCArguments>> handlers = GetHandlers(kind);
            Action<FunRPCContext, FunRPCArguments> handler;

            if (!handlers.TryGetValue(rpcName, out handler))
            {
                MelonLogger.Warning($"[FunRPC] No {kind} handler registered for '{rpcName}'. Payload was consumed safely.");
                return;
            }

            try
            {
                handler(context, args);
            }
            catch (Exception e)
            {
                MelonLogger.Error($"[FunRPC] Handler '{rpcName}' ({kind}) threw:\n{e}");
            }
        }

        private static Dictionary<string, Action<FunRPCContext, FunRPCArguments>> GetHandlers(FunRPCKind kind)
        {
            switch (kind)
            {
                case FunRPCKind.Server: return _serverHandlers;
                case FunRPCKind.Observers: return _observersHandlers;
                case FunRPCKind.Target: return _targetHandlers;
                default: throw new ArgumentOutOfRangeException(nameof(kind));
            }
        }

        // carrier selection

        private static NetworkBehaviour GetClientCarrier()
        {
            if (IsValidClientCarrier(_clientCarrier))
                return _clientCarrier;

            _clientCarrier = FindClientCarrier();
            return _clientCarrier;
        }

        private static NetworkBehaviour GetServerCarrier()
        {
            if (IsValidServerCarrier(_serverCarrier))
                return _serverCarrier;

            _serverCarrier = FindServerCarrier();
            return _serverCarrier;
        }

        private static NetworkBehaviour FindTargetCarrier(NetworkConnection target)
        {
            try
            {
                // target own FirstObject is the best carrier cuz the client necessarily knows it
                if (target.FirstObject != null)
                {
                    NetworkBehaviour[] ownedBehaviours = target.FirstObject.GetComponents<NetworkBehaviour>();
                    foreach (NetworkBehaviour nb in ownedBehaviours)
                    {
                        if (IsValidServerCarrier(nb))
                            return nb;
                    }
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[FunRPC] Could not use target FirstObject as carrier: {e.Message}");
            }

            return GetServerCarrier();
        }

        private static NetworkBehaviour FindClientCarrier()
        {
            NetworkBehaviour[] behaviours = UnityEngine.Object.FindObjectsOfType<NetworkBehaviour>();
            NetworkBehaviour fallback = null;

            foreach (NetworkBehaviour nb in behaviours)
            {
                if (nb == null)
                    continue;

                try
                {
                    if (!nb.IsSpawned || !nb.IsClientInitialized)
                        continue;

                    if (nb.IsOwner)
                    {
                        MelonLogger.Msg("[FunRPC] Selected CLIENT carrier: " + Describe(nb));
                        return nb;
                    }

                    if (fallback == null)
                        fallback = nb;
                }
                catch
                {
                }
            }

            if (fallback != null)
                MelonLogger.Warning("[FunRPC] No owned client carrier found; using fallback: " + Describe(fallback));

            return fallback;
        }

        private static NetworkBehaviour FindServerCarrier()
        {
            NetworkBehaviour[] behaviours = UnityEngine.Object.FindObjectsOfType<NetworkBehaviour>();
            NetworkBehaviour fallback = null;

            foreach (NetworkBehaviour nb in behaviours)
            {
                if (nb == null)
                    continue;

                try
                {
                    if (!nb.IsSpawned || !nb.IsServerInitialized)
                        continue;

                    if (nb.IsOwner)
                    {
                        MelonLogger.Msg("[FunRPC] Selected SERVER carrier: " + Describe(nb));
                        return nb;
                    }

                    if (fallback == null)
                        fallback = nb;
                }
                catch
                {
                }
            }

            if (fallback != null)
                MelonLogger.Warning("[FunRPC] Using SERVER fallback carrier: " + Describe(fallback));

            return fallback;
        }

        private static bool IsValidClientCarrier(NetworkBehaviour nb)
        {
            if (nb == null)
                return false;

            try { return nb.IsSpawned && nb.IsClientInitialized; }
            catch { return false; }
        }

        private static bool IsValidServerCarrier(NetworkBehaviour nb)
        {
            if (nb == null)
                return false;

            try { return nb.IsSpawned && nb.IsServerInitialized; }
            catch { return false; }
        }

        // debugggggg

        public static void PrintNetworkBehaviours()
        {
            MelonLogger.Msg("========== [FunRPC] NetworkBehaviours ==========");
            NetworkBehaviour[] behaviours = UnityEngine.Object.FindObjectsOfType<NetworkBehaviour>();
            int index = 0;

            foreach (NetworkBehaviour nb in behaviours)
            {
                if (nb == null)
                    continue;

                try
                {
                    MelonLogger.Msg($"[{index++}] {Describe(nb)}");
                }
                catch (Exception e)
                {
                    MelonLogger.Warning("[FunRPC] Cannot inspect behaviour: " + e.Message);
                }
            }

            MelonLogger.Msg($"[FunRPC] Total behaviours: {index}");

            NetworkBehaviour serverCarrier = GetServerCarrier();
            if (serverCarrier != null && serverCarrier.NetworkManager != null && serverCarrier.NetworkManager.ServerManager != null)
            {
                try
                {
                    MelonLogger.Msg("---------- [FunRPC] Server connections ----------");
                    foreach (NetworkConnection connection in serverCarrier.NetworkManager.ServerManager.Clients.Values)
                    {
                        if (connection != null)
                            MelonLogger.Msg($"ClientId={connection.ClientId}, Local={connection.IsLocalClient}, FirstObject={(connection.FirstObject == null ? "NULL" : connection.FirstObject.gameObject.name)}");
                    }
                }
                catch (Exception e)
                {
                    MelonLogger.Warning("[FunRPC] Cannot print connections: " + e.Message);
                }
            }

            MelonLogger.Msg("=================================================");
        }

        private static string Describe(NetworkBehaviour nb)
        {
            return
                $"Name={nb.gameObject.name}, " +
                $"Type={nb.GetType().FullName}, " +
                $"ObjectId={nb.ObjectId}, " +
                $"ComponentIndex={nb.ComponentIndex}, " +
                $"Spawned={nb.IsSpawned}, " +
                $"Owner={nb.IsOwner}, " +
                $"Client={nb.IsClientInitialized}, " +
                $"Server={nb.IsServerInitialized}";
        }
    }
}
