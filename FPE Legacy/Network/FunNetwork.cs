using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FPE_Legacy.Content;
using FPE_Legacy.Rpc;
using Il2CppFishNet.Transporting;
using UnityEngine;
using UnityEngine.SceneManagement;
using MelonLoader;

namespace FPE_Legacy.Networking
{
    public sealed class FunNetworkIdentity
    {
        public ulong Id { get; internal set; }
        public string PrefabId => Content.AssetId;
        public GameObject GameObject => Content.GameObject;
        public Transform Transform => GameObject.transform;
        public int OwnerClientId { get; internal set; } = -1;
        public bool IsOwner => OwnerClientId >= 0 && OwnerClientId == FunTransport.LocalClientId;
        public FunNetworkTransform NetworkTransform { get; internal set; }
        public FunNetworkOptions Settings => Options.Copy();
        internal FunNetworkOptions Options;
        internal FunContentInstance Content;
        internal FunNetworkFields Fields;
        internal uint Revision = 1, TransformRevision = 1, Sequence;
        public T GetComponent<T>(string id) where T : class => Content.Components.TryGetValue(id, out var value) ? value as T : null;
        public void Despawn() => FunNetwork.Despawn(this);
        public void SetOwner(int clientId) => FunNetwork.SetOwner(this, clientId);
    }
    public sealed class FunSpawnRequest
    {
        public int SenderClientId { get; internal set; }
        public string PrefabId { get; internal set; }
        public Vector3 Position { get; internal set; }
        public Quaternion Rotation { get; internal set; }
    }
    public static class FunNetwork
    {
        private sealed class Peer
        {
            internal int Id;
            internal string Nonce, Token;
            internal bool Ready;
            internal float LastSeen, SnapshotAt, NextRequest;
            internal long Carrier;
            internal Dictionary<ulong, uint> Known = new();
            internal HashSet<ulong> Materialized = new();
            internal Queue<FunPacket> SnapshotPackets = new();
            internal Queue<(ulong id, string rpc, Channel channel, object[] args, float expires)> RpcQueue = new();
        }
        private sealed class Pending
        {
            internal FunSpawnRecord Record;
            internal string Epoch, Token;
            internal Task Task;
            internal int DeferredCount;
            internal FunTransformSample Sample;
        }
        private static readonly Dictionary<ulong, FunNetworkIdentity> Objects = new();
        private static readonly Dictionary<int, Peer> Peers = new();
        private static readonly Dictionary<ulong, Pending> PendingSpawns = new();
        private static readonly HashSet<ulong> Tombstones = new();
        private static readonly Dictionary<long, (FunNetworkIdentity identity, string component)> Addresses = new();
        private static string _epoch, _nonce, _token;
        private static ulong _nextId;
        private static float _nextHello, _nextPing, _nextReconcile;
        private static int _sceneHandle = int.MinValue, _generation;
        private static bool _wasServer, _wasClient, _clientReady, _initialized;
        private static float _joinStarted, _lastPong;
        private const string TransformRpc = "__FPE_NET_TRANSFORM_V1";
        private const string ObjectRpc = "__FPE_NET_OBJECT_RPC_V1";
        public static bool IsServer => FunTransport.IsServer;
        public static bool IsClient => FunTransport.IsClient;
        public static bool IsReady => FunContent.IsReady && (IsServer || _clientReady);
        public static string LastError { get; private set; }
        public static IReadOnlyCollection<FunNetworkIdentity> Spawned => Objects.Values.ToArray();
        // default denies client spawn requests. Host code may approve specific prefab/position pairs
        public static Func<FunSpawnRequest, bool> ApproveSpawnRequest { get; set; }
        public static event Action<FunNetworkIdentity> ObjectSpawned;
        public static event Action<FunNetworkIdentity> ObjectDespawned;
        public static void Initialize()
        {
            if (_initialized) return; _initialized = true;
            FunWire.Initialize(); FunWire.Received = Receive;
            FunNetworkTransform.RegisterWire();
            FunRPCRuntime.RegisterTarget(TransformRpc, ReceiveTransforms);
            FunRPCRuntime.RegisterServer(ObjectRpc, ReceiveObjectRpc);
            FunRPCRuntime.RegisterTarget(ObjectRpc, ReceiveObjectRpc);
            ResetClient();
        }
        public static async Task<FunNetworkIdentity> SpawnAsync(string prefabId, Vector3 position, Quaternion rotation, FunNetworkOptions options = null, int ownerClientId = -1)
        {
            FunMainThread.Require();
            if (!IsServer) throw new InvalidOperationException("SpawnAsync is server-only; use RequestSpawn on a client.");
            int generation = _generation;
            await FunContent.Ready;
            var settings = options?.Copy() ?? FunContent.GetNetworkOptions(prefabId); settings.Validate();
            if (!settings.Enabled) throw new InvalidOperationException("This prefab is local-only.");
            CheckPose(position, rotation);
            if (Objects.Count >= 4096) throw new InvalidOperationException("Session object limit (4096) reached.");
            var content = await FunContent.PrepareInstance(prefabId, position, rotation);
            if (!IsServer || generation != _generation) { content.Dispose(); throw new OperationCanceledException("Session/scene changed during spawn."); }
            var identity = NewIdentity(++_nextId, content, settings, ownerClientId);
            try
            {
                Activate(identity, null); ObjectSpawned?.Invoke(identity); return identity;
            }
            catch { Remove(identity, false); throw; }
        }
		public static bool RequestSpawn(string prefabId, Vector3 position, Quaternion rotation)
		{
			FunMainThread.Require();
			CheckPose(position, rotation);

			if (!IsReady)
				return false;

			if (IsServer)
			{
				Observe(SpawnAsync(prefabId,position,rotation,ownerClientId: FunTransport.LocalClientId));
				return true;
			}

			return SendServer(new FunPacket
			{
				Op = "requestSpawn",
				Prefab = prefabId,
				Position = Vec(position),
				Rotation = Quat(rotation)
			});
		}
        public static FunNetworkIdentity Find(ulong id) => Objects.TryGetValue(id, out var value) ? value : null;
        public static FunNetworkIdentity GetIdentity(Component component)
        {
            if (component == null) return null;
            if (Addresses.TryGetValue(component.Pointer.ToInt64(), out var address)) return address.identity;
            Transform t = component.transform;
            while (t != null)
            {
                foreach (var identity in Objects.Values) if (identity.GameObject != null && identity.Transform.Pointer == t.Pointer) return identity;
                t = t.parent;
            }
            return null;
        }
        internal static bool TryAddress(object instance, out FunNetworkIdentity identity, out string component)
        {
            identity = null; component = null;
            if (instance is Component c && Addresses.TryGetValue(c.Pointer.ToInt64(), out var address)) { identity = address.identity; component = address.component; return true; }
            return false;
        }
        public static void Despawn(FunNetworkIdentity identity)
        {
            FunMainThread.Require(); if (!IsServer) throw new InvalidOperationException("Despawn is server-only.");
            if (identity != null && Find(identity.Id) == identity) Remove(identity, true);
        }
        public static void SetOwner(FunNetworkIdentity identity, int clientId)
        {
            FunMainThread.Require(); if (!IsServer || Find(identity.Id) != identity) throw new InvalidOperationException("SetOwner requires a live server object.");
            if (clientId >= 0 && FunTransport.Connection(clientId) == null) throw new ArgumentException("Unknown client.");
            identity.OwnerClientId = clientId; identity.Revision++;
        }
        private static FunNetworkIdentity NewIdentity(ulong id, FunContentInstance content, FunNetworkOptions options, int owner)
        {
            var identity = new FunNetworkIdentity { Id = id, Content = content, Options = options, OwnerClientId = owner };
            identity.NetworkTransform = new FunNetworkTransform(identity);
            Objects.Add(id, identity);
            foreach (var pair in content.Instances) if (pair.Value is Component c) Addresses.Add(c.Pointer.ToInt64(), (identity, pair.Key));
            try { identity.Fields = new FunNetworkFields(identity); }
            catch { Remove(identity, false); throw; }
            return identity;
        }
        private static void Activate(FunNetworkIdentity identity, FunSpawnRecord initial)
        {
            if (initial != null)
            {
                identity.OwnerClientId = initial.Owner; identity.Revision = initial.Revision; identity.TransformRevision = initial.TransformRevision;
                identity.Transform.localScale = FromVec(initial.Scale);
                identity.Fields.Apply(initial.Fields);
                identity.NetworkTransform.Reset(initial.TransformRevision);
            }
            if (identity.Options.Persistent) UnityEngine.Object.DontDestroyOnLoad(identity.GameObject);
            identity.Content.NotifyReady();
            if (!IsServer && identity.Options.Physics == "server")
                foreach (var body in identity.GameObject.GetComponentsInChildren<Rigidbody>(true)) { body.isKinematic = true; body.useGravity = false; }
            identity.GameObject.SetActive(true);
            foreach (var component in identity.Content.Instances.Values) if (component is IFunNetworkSpawned listener) listener.OnNetworkSpawned();
        }
        private static void Remove(FunNetworkIdentity identity, bool notify)
        {
            Objects.Remove(identity.Id);
            foreach (var key in Addresses.Where(p => p.Value.identity == identity).Select(p => p.Key).ToArray()) Addresses.Remove(key);
            try
            {
                if (notify)
                {
                    foreach (var component in identity.Content.Instances.Values)
                        if (component is IFunNetworkSpawned listener) { try { listener.OnNetworkDespawned(); } catch (Exception e) { Error(e.Message); } }
                    ObjectDespawned?.Invoke(identity);
                }
            }
            finally { identity.Content.Dispose(); }
        }
        private static void ClearObjects(bool keepPersistent = false)
        {
            PendingSpawns.Clear(); Tombstones.Clear();
            foreach (var identity in Objects.Values.ToArray()) if (!keepPersistent || !identity.Options.Persistent || identity.GameObject == null) Remove(identity, true);
        }
        private static void ResetClient()
        {
            _nonce = Guid.NewGuid().ToString("N"); _token = null; _clientReady = false;
            _nextHello = 0; _joinStarted = Time.unscaledTime; _nextPing = 0; _lastPong = Time.unscaledTime;
        }
        public static void Tick()
        {
            if (!_initialized) return;
            FunTransport.Poll();
            int scene = SceneManager.GetActiveScene().handle;
            bool sceneChanged = scene != _sceneHandle;
            if (IsServer && (!_wasServer || sceneChanged))
            {
                _generation++; _epoch = Guid.NewGuid().ToString("N");
                ClearObjects(_wasServer); Peers.Clear(); FunWire.Clear();
                foreach (var o in Objects.Values) { o.Sequence = 0; o.TransformRevision++; o.Revision++; }
            }
            else if ((!IsServer && _wasServer) || (!IsServer && (sceneChanged || (!IsClient && _wasClient))))
            { _generation++; ClearObjects(); Peers.Clear(); FunWire.Clear(); ResetClient(); _epoch = null; }
            if (!IsServer && IsClient && !_wasClient) { ResetClient(); _epoch = null; }
            _sceneHandle = scene; _wasServer = IsServer; _wasClient = IsClient;
            if (!FunContent.IsReady) return;
            if (IsServer) TickServer();
            else if (IsClient) TickClient();
            FunWire.Tick();
        }
        private static void TickServer()
        {
            var unreliable = new List<FunTransformSample>(); var reliable = new List<FunTransformSample>();
            foreach (var identity in Objects.Values.ToArray())
            {
                if (identity.GameObject == null) { Remove(identity, true); continue; }
                try
                {
                    if (identity.Fields.Tick()) identity.Revision++;
                    var sample = identity.NetworkTransform.TickServer(out bool key);
                    if (sample != null) (key ? reliable : unreliable).Add(sample);
                }
                catch (Exception e) { Error("Object " + identity.Id + ": " + e.Message); }
            }
            foreach (var peer in Peers.Values.ToArray())
            {
                var connection = FunTransport.Connection(peer.Id);
                if (connection == null || Time.unscaledTime - peer.LastSeen > 30)
                {
                    Peers.Remove(peer.Id); FunWire.Forget(peer.Id);
                    foreach (var o in Objects.Values.Where(o => o.OwnerClientId == peer.Id)) { o.OwnerClientId = -1; o.Revision++; }
                    continue;
                }
                long carrier = FunTransport.CarrierKey(peer.Id);
                if (carrier == 0) { peer.Ready = false; continue; }
                if (peer.Carrier != carrier || (!peer.Ready && Time.unscaledTime - peer.SnapshotAt > 60)) StartSnapshot(peer);
                if (!peer.Ready) { PumpSnapshot(peer); continue; }
                SendTransforms(peer, reliable, Channel.Reliable); SendTransforms(peer, unreliable, Channel.Unreliable);
                for (int budget = peer.RpcQueue.Count; budget > 0 && peer.RpcQueue.Count > 0; budget--)
                {
                    var call = peer.RpcQueue.Dequeue();
                    if (call.expires < Time.unscaledTime || !Objects.ContainsKey(call.id)) continue;
                    if (!peer.Materialized.Contains(call.id) || !FunTransport.ToClient(peer.Id, call.rpc, call.channel, call.args)) peer.RpcQueue.Enqueue(call);
                }
            }
            if (Time.unscaledTime < _nextReconcile) return;
            _nextReconcile = Time.unscaledTime + .1f;
            foreach (var peer in Peers.Values.Where(p => p.Ready).ToArray()) Reconcile(peer);
        }
        private static void TickClient()
        {
            if (_clientReady && Time.unscaledTime - _lastPong > 20) { ResetClient(); FunWire.Forget(-1); }
            if (!_clientReady)
            {
                if (Time.unscaledTime >= _nextHello && (_token == null || Time.unscaledTime - _joinStarted > 60))
                {
                    _nextHello = Time.unscaledTime + 3;
                    FunWire.Send(-1, new FunPacket { Op = "hello", Token = _nonce, Fingerprint = FunContent.Fingerprint, Scene = SceneManager.GetActiveScene().name });
                }
            }
            if (_token != null && Time.unscaledTime >= _nextPing)
            {
                _nextPing = Time.unscaledTime + 2; SendServer(new FunPacket { Op = "ping" });
            }
            foreach (var identity in Objects.Values.ToArray())
            {
                if (identity.GameObject == null) { Remove(identity, true); ResetClient(); continue; }
                try { identity.NetworkTransform.TickClient(); if (_clientReady) identity.Fields.Tick(); }
                catch (Exception e) { Error("Object " + identity.Id + ": " + e.Message); }
            }
        }
        private static void StartSnapshot(Peer peer)
        {
            if (FunTransport.CarrierKey(peer.Id) == 0) return;
            peer.Ready = false; peer.Token = Guid.NewGuid().ToString("N"); peer.Carrier = FunTransport.CarrierKey(peer.Id);
            peer.SnapshotAt = Time.unscaledTime; peer.Known.Clear(); peer.Materialized.Clear(); peer.RpcQueue.Clear(); FunWire.Forget(peer.Id);
            peer.SnapshotPackets.Clear();
            peer.SnapshotPackets.Enqueue(new FunPacket { Op = "begin", Nonce = peer.Nonce, Scene = SceneManager.GetActiveScene().name });
            foreach (var identity in Objects.Values.ToArray())
            {
                peer.SnapshotPackets.Enqueue(new FunPacket { Op = "spawn", Spawn = Record(identity) });
                peer.Known[identity.Id] = identity.Revision;
            }
            peer.SnapshotPackets.Enqueue(new FunPacket { Op = "end" });
            PumpSnapshot(peer);
        }
        private static void PumpSnapshot(Peer peer)
        {
            for (int budget = 0; budget < 128 && peer.SnapshotPackets.Count > 0; budget++)
            {
                if (!Send(peer, peer.SnapshotPackets.Peek())) break;
                peer.SnapshotPackets.Dequeue(); peer.SnapshotAt = Time.unscaledTime;
            }
        }
        private static void Reconcile(Peer peer)
        {
            foreach (var id in peer.Known.Keys.Where(id => !Objects.ContainsKey(id)).ToArray())
                if (Send(peer, new FunPacket { Op = "despawn", Id = id })) { peer.Known.Remove(id); peer.Materialized.Remove(id); }
            foreach (var identity in Objects.Values.ToArray())
            {
                if (peer.Known.TryGetValue(identity.Id, out uint revision) && revision == identity.Revision) continue;
                if (Send(peer, new FunPacket { Op = peer.Known.ContainsKey(identity.Id) ? "state" : "spawn", Spawn = Record(identity, peer.Id, !peer.Known.ContainsKey(identity.Id)) })) peer.Known[identity.Id] = identity.Revision;
            }
        }
        private static FunSpawnRecord Record(FunNetworkIdentity identity, int recipient = -1, bool initial = true) => new()
        {
            Id = identity.Id, Prefab = identity.PrefabId, Owner = identity.OwnerClientId, Revision = identity.Revision,
            TransformRevision = identity.TransformRevision, Position = Vec(identity.Transform.position), Rotation = Quat(identity.Transform.rotation),
            Scale = Vec(identity.Transform.localScale), Options = identity.Options.Copy(), Fields = identity.Fields.Capture(recipient, initial)
        };
        private static bool Send(Peer peer, FunPacket packet) { packet.Epoch = _epoch; packet.Token = peer.Token; return FunWire.Send(peer.Id, packet); }
        private static bool SendServer(FunPacket packet) { packet.Epoch = _epoch; packet.Token = _token; return FunWire.Send(-1, packet); }
        private static void Receive(FunRPCContext context, FunPacket packet)
        {
            if (context.Kind == FunRPCKind.Server) { if (IsServer) ReceiveServer(context, packet); }
            else if (!IsServer && IsClient) ReceiveClient(packet);
        }
        private static void ReceiveServer(FunRPCContext context, FunPacket packet)
        {
            if (context.Sender == null || context.Sender.IsLocalClient) return;
            int id = context.SenderClientId;
            if (packet.Op == "hello")
            {
                if (!FunContent.IsReady || packet.Token == null || packet.Token.Length != 32) return;
                if (packet.Scene != SceneManager.GetActiveScene().name) return;
                if (packet.Protocol != 1 || packet.Fingerprint != FunContent.Fingerprint)
                { FunWire.Send(id, new FunPacket { Op = "reject", Nonce = packet.Token, Error = "FPE protocol/content mismatch. Install identical mod DLLs and AssetBundles." }); return; }
                if (!Peers.TryGetValue(id, out var peer) || peer.Nonce != packet.Token)
                { Peers[id] = peer = new Peer { Id = id, Nonce = packet.Token }; peer.LastSeen = Time.unscaledTime; StartSnapshot(peer); }
                else { peer.LastSeen = Time.unscaledTime; if (!peer.Ready && Time.unscaledTime - peer.SnapshotAt > 60) StartSnapshot(peer); }
                return;
            }
            if (!Peers.TryGetValue(id, out var p) || packet.Epoch != _epoch || packet.Token != p.Token) return;
            p.LastSeen = Time.unscaledTime;
            if (packet.Op == "ack" && p.SnapshotPackets.Count == 0) { p.Ready = true; p.Materialized = new HashSet<ulong>(p.Known.Keys); Send(p, new FunPacket { Op = "pong" }); Reconcile(p); return; }
            if (packet.Op == "ping") { Send(p, new FunPacket { Op = "pong" }); return; }
            if (packet.Op == "objectAck") { if (p.Known.ContainsKey(packet.Id)) p.Materialized.Add(packet.Id); return; }
            if (!p.Ready) return;
            if (packet.Op == "ownerFields")
            {
                var identity = Find(packet.Id);
                if (identity == null || identity.OwnerClientId != id) return;
                identity.Fields.Apply(packet.Fields, true); identity.Revision++; return;
            }
            if (packet.Op == "requestSpawn" && Time.unscaledTime >= p.NextRequest)
            {
                p.NextRequest = Time.unscaledTime + .25f;
                var request = new FunSpawnRequest { SenderClientId = id, PrefabId = packet.Prefab, Position = FromVec(packet.Position), Rotation = FromQuat(packet.Rotation) };
                CheckPose(request.Position, request.Rotation);
                if (ApproveSpawnRequest?.Invoke(request) == true) Observe(SpawnAsync(request.PrefabId, request.Position, request.Rotation, ownerClientId: id));
            }
        }
        private static void ReceiveClient(FunPacket packet)
        {
            if (packet.Op == "reject") { if (packet.Nonce == _nonce) Error(packet.Error); return; }
            if (packet.Op == "begin")
            {
                if (packet.Nonce != _nonce || packet.Scene != SceneManager.GetActiveScene().name || string.IsNullOrEmpty(packet.Epoch)) return;
                ClearObjects(); _epoch = packet.Epoch; _token = packet.Token; _clientReady = false; _joinStarted = Time.unscaledTime; return;
            }
            if (packet.Epoch != _epoch || packet.Token != _token || _token == null) return;
            switch (packet.Op)
            {
                case "pong": _lastPong = Time.unscaledTime; break;
                case "end": Observe(FinishSnapshot(_epoch, _token)); break;
                case "spawn": case "state": ReceiveSpawn(packet.Spawn); break;
                case "despawn":
                    Tombstones.Add(packet.Id); PendingSpawns.Remove(packet.Id);
                    var identity = Find(packet.Id); if (identity != null) Remove(identity, true); break;
                case "teleport":
                    var r = packet.Spawn;
                    var s = new FunTransformSample { Id = r.Id, Sequence = 0, Revision = r.TransformRevision, Position = FromVec(r.Position), Rotation = FromQuat(r.Rotation), Scale = FromVec(r.Scale), Time = packet.ServerTime, Teleport = true };
                    ApplySample(s); break;
            }
        }
        private static void ReceiveSpawn(FunSpawnRecord record)
        {
            if (record == null || record.Id == 0 || Tombstones.Contains(record.Id)) return;
            var identity = Find(record.Id);
            if (identity != null)
            {
                if (record.Revision < identity.Revision) return;
                identity.Fields.Apply(record.Fields); identity.OwnerClientId = record.Owner; identity.Revision = record.Revision; return;
            }
            if (PendingSpawns.TryGetValue(record.Id, out var pending)) { if (record.Revision >= pending.Record.Revision) pending.Record = record; return; }
            if (PendingSpawns.Count >= 4096) throw new InvalidOperationException("Too many pending spawns.");
            pending = new Pending { Record = record, Epoch = _epoch, Token = _token }; PendingSpawns.Add(record.Id, pending);
            pending.Task = Materialize(pending); Observe(pending.Task);
        }
        private static async Task Materialize(Pending pending)
        {
            var r = pending.Record; r.Options.Validate();
            var content = await FunContent.PrepareInstance(r.Prefab, FromVec(r.Position), FromQuat(r.Rotation));
            if (pending.Epoch != _epoch || pending.Token != _token || !PendingSpawns.TryGetValue(r.Id, out var current) || current != pending)
            { content.Dispose(); return; }
            var identity = NewIdentity(r.Id, content, r.Options.Copy(), r.Owner);
            try
            {
                Activate(identity, pending.Record);
                if (pending.Sample != null) identity.NetworkTransform.Receive(pending.Sample);
                PendingSpawns.Remove(r.Id); SendServer(new FunPacket { Op = "objectAck", Id = r.Id }); ObjectSpawned?.Invoke(identity);
            }
            catch { Remove(identity, false); throw; }
        }
        private static async Task FinishSnapshot(string epoch, string token)
        {
            await Task.WhenAll(PendingSpawns.Values.Select(p => p.Task ?? Task.CompletedTask).ToArray());
            if (epoch != _epoch || token != _token) return;
            _clientReady = true; _lastPong = Time.unscaledTime; LastError = null; SendServer(new FunPacket { Op = "ack" });
        }
        internal static bool RequestField(FunNetworkIdentity identity, FunFieldValue value) => SendServer(new FunPacket { Op = "ownerFields", Id = identity.Id, Fields = new() { value } });
        internal static void SendTeleport(FunNetworkIdentity identity)
        {
            foreach (var peer in Peers.Values.Where(p => p.Ready)) Send(peer, new FunPacket { Op = "teleport", Spawn = Record(identity), ServerTime = Time.realtimeSinceStartupAsDouble });
        }
        private static void SendTransforms(Peer peer, List<FunTransformSample> samples, Channel channel)
        {
            var visible = samples.Where(s => peer.Known.ContainsKey(s.Id)).ToArray();
            for (int offset = 0; offset < visible.Length; offset += 8)
                FunTransport.ToClient(peer.Id, TransformRpc, channel, new FunTransformBatch { Epoch = _epoch, Token = peer.Token, Samples = visible.Skip(offset).Take(8).ToList() });
        }
        private static void ReceiveTransforms(FunRPCContext context, FunRPCArguments args)
        {
            if (IsServer || args.Count != 1) return;
            var batch = args.Get<FunTransformBatch>(0);
            if (batch.Epoch != _epoch || batch.Token != _token) return;
            foreach (var sample in batch.Samples) ApplySample(sample);
        }
        private static void ApplySample(FunTransformSample sample)
        {
            if (!FunNetworkTransform.Valid(sample)) return;
            var identity = Find(sample.Id);
            if (identity != null) identity.NetworkTransform.Receive(sample);
            else if (PendingSpawns.TryGetValue(sample.Id, out var pending) && (pending.Sample == null || sample.Revision > pending.Sample.Revision || (sample.Revision == pending.Sample.Revision && sample.Sequence > pending.Sample.Sequence))) pending.Sample = sample;
        }
        internal static bool SendObjectRpc(FunNetworkIdentity identity, string component, string name, FunRPCKind kind, FunRPCSendOptions options, object[] values)
        {
            if (kind == FunRPCKind.Server)
            {
                if (IsServer) return FunRPCAttributeRuntime.InvokeNetwork(name, kind, identity, component, new FunRPCContext { Kind = kind, Sender = FunTransport.Connection(FunTransport.LocalClientId) }, new FunRPCArguments(values));
                if (!_clientReady) return false;
                return FunRPCRuntime.SendServer(ObjectRpc, options, Envelope(_token));
            }
            if (!IsServer) return false;
            if (options.BufferLast) throw new NotSupportedException("BufferLast is not supported for object RPCs; persist state with FunSerializable.");
            bool sent = true;
            foreach (var peer in Peers.Values.Where(p => p.Ready).ToArray())
            {
                if (options.ExcludeOwner && peer.Id == identity.OwnerClientId) continue;
                if (kind == FunRPCKind.Target && (values.Length == 0 || peer.Id != Convert.ToInt32(values[0]))) continue;
                var envelope = Envelope(peer.Token);
                if (!peer.Materialized.Contains(identity.Id))
                {
                    if (peer.RpcQueue.Count >= 256) { sent = false; continue; }
                    peer.RpcQueue.Enqueue((identity.Id, ObjectRpc, options.Channel, envelope, Time.unscaledTime + 30));
                }
                else sent &= FunTransport.ToClient(peer.Id, ObjectRpc, options.Channel, envelope);
            }
            if (!options.ExcludeServer && (!options.ExcludeOwner || !identity.IsOwner) && (kind == FunRPCKind.Observers || (values.Length > 0 && Convert.ToInt32(values[0]) == FunTransport.LocalClientId)))
                FunRPCAttributeRuntime.InvokeNetwork(name, kind, identity, component, new FunRPCContext { Kind = kind }, new FunRPCArguments(values));
            return sent;
            object[] Envelope(string token)
            {
                var result = new object[6 + values.Length]; result[0] = _epoch; result[1] = token; result[2] = identity.Id; result[3] = component; result[4] = name; result[5] = (int)kind;
                Array.Copy(values, 0, result, 6, values.Length); return result;
            }
        }
        private static void ReceiveObjectRpc(FunRPCContext context, FunRPCArguments args)
        {
            if (args.Count < 6) return;
            string epoch = args.Get<string>(0), token = args.Get<string>(1);
            if (epoch != _epoch) return;
            var kind = (FunRPCKind)args.Get<int>(5);
            if (context.Kind == FunRPCKind.Server)
            { if (!IsServer || kind != FunRPCKind.Server || !Peers.TryGetValue(context.SenderClientId, out var peer) || !peer.Ready || peer.Token != token) return; }
            else if (IsServer || token != _token || (kind != FunRPCKind.Observers && kind != FunRPCKind.Target)) return;
            ulong id = args.Get<ulong>(2); string component = args.Get<string>(3), name = args.Get<string>(4);
            var values = new FunRPCArguments(args.ToArray().Skip(6).ToArray());
            var identity = Find(id);
            if (identity == null)
            {
                if (PendingSpawns.TryGetValue(id, out var pending) && pending.DeferredCount++ < 256) Observe(DeferredRpc(pending, component, name, kind, context, values));
                return;
            }
            FunRPCAttributeRuntime.InvokeNetwork(name, kind, identity, component, context, values);
        }
        private static async Task DeferredRpc(Pending pending, string component, string name, FunRPCKind kind, FunRPCContext context, FunRPCArguments values)
        {
            await pending.Task;
            if (pending.Token != _token || pending.Epoch != _epoch) return;
            var identity = Find(pending.Record.Id);
            if (identity != null) FunRPCAttributeRuntime.InvokeNetwork(name, kind, identity, component, context, values);
        }
        internal static void Observe(Task task)
        {
            if (task.IsCompleted) { if (task.IsFaulted) Error(task.Exception.GetBaseException().Message); return; }
            ObserveLater(task);
        }
        private static async void ObserveLater(Task task) { try { await task; } catch (Exception e) { Error(e.Message); } }
        private static void Error(string text) { if (LastError == text) return; LastError = text; MelonLogger.Error("[FunNetwork] " + text); }
        internal static float[] Vec(Vector3 v) => new[] { v.x, v.y, v.z };
        internal static float[] Quat(Quaternion q) => new[] { q.x, q.y, q.z, q.w };
        internal static Vector3 FromVec(float[] v) { if (v == null || v.Length != 3 || v.Any(x => !float.IsFinite(x))) throw new FormatException("Invalid Vector3."); return new Vector3(v[0], v[1], v[2]); }
        internal static Quaternion FromQuat(float[] v) { if (v == null || v.Length != 4 || v.Any(x => !float.IsFinite(x)) || v.Sum(x => x * x) < .0001f) throw new FormatException("Invalid Quaternion."); return new Quaternion(v[0], v[1], v[2], v[3]).normalized; }
        private static void CheckPose(Vector3 v, Quaternion q) { FromVec(Vec(v)); FromQuat(Quat(q)); }
    }
}
