using System;
using System.Collections.Generic;
using System.Linq;
using FPE_Legacy.Content;
using FPE_Legacy.Rpc;
using Il2CppFishNet.Transporting;
using UnityEngine;

namespace FPE_Legacy.Networking
{
    internal sealed class FunPacket
    {
        public string Op { get; set; }
        public string Nonce { get; set; }
        public double ServerTime { get; set; }
        public string Epoch { get; set; }
        public string Token { get; set; }
        public string Fingerprint { get; set; }
        public string Scene { get; set; }
        public string Error { get; set; }
        public int Protocol { get; set; } = 1;
        public ulong Id { get; set; }
        public uint Revision { get; set; }
        public FunSpawnRecord Spawn { get; set; }
        public List<FunFieldValue> Fields { get; set; }
        public List<ulong> Ids { get; set; }
        public string Prefab { get; set; }
        public float[] Position { get; set; }
        public float[] Rotation { get; set; }
    }
    internal sealed class FunSpawnRecord
    {
        public ulong Id { get; set; }
        public string Prefab { get; set; }
        public int Owner { get; set; } = -1;
        public uint Revision { get; set; }
        public uint TransformRevision { get; set; }
        public float[] Position { get; set; }
        public float[] Rotation { get; set; }
        public float[] Scale { get; set; }
        public FunNetworkOptions Options { get; set; }
        public List<FunFieldValue> Fields { get; set; }
    }
    internal static class FunWire
    {
        private const string Rpc = "__FPE_NET_CHUNK_V1";
        private const int ChunkSize = 160, MaxParts = 4096, MaxQueue = 12000;
        private sealed class Outgoing { internal string Id; internal string[] Parts; internal int Next; }
        private sealed class Incoming { internal string[] Parts; internal int Count; internal float Expires; }
        private static readonly Dictionary<int, Queue<Outgoing>> Queues = new();
        private static readonly Dictionary<string, Incoming> Partial = new();
        private static long _serial;
        internal static Action<FunRPCContext, FunPacket> Received;
        internal static void Initialize()
        {
            FunRPCRuntime.RegisterServer(Rpc, OnChunk); FunRPCRuntime.RegisterTarget(Rpc, OnChunk);
        }
        internal static bool Send(int client, FunPacket packet)
        {
            string json = FunJson.Write(packet);
            int count = Math.Max(1, (json.Length + ChunkSize - 1) / ChunkSize);
            if (count > MaxParts) throw new InvalidOperationException("Network message exceeds 640 KiB; reduce initial field data.");
            if (!Queues.TryGetValue(client, out var queue)) Queues[client] = queue = new();
            if (queue.Sum(q => q.Parts.Length - q.Next) + count > MaxQueue) return false;
            var parts = new string[count];
            for (int i = 0; i < count; i++) parts[i] = json.Substring(i * ChunkSize, Math.Min(ChunkSize, json.Length - i * ChunkSize));
            queue.Enqueue(new Outgoing { Id = (++_serial).ToString(), Parts = parts }); return true;
        }
        internal static void Forget(int client) { Queues.Remove(client); }
        internal static void Clear() { Queues.Clear(); Partial.Clear(); }
        internal static void Tick()
        {
            foreach (var key in Partial.Where(p => p.Value.Expires < Time.unscaledTime).Select(p => p.Key).ToArray()) Partial.Remove(key);
            foreach (var pair in Queues.ToArray())
            {
                var queue = pair.Value;
                // Per-connection work limit; a slow peer cannot stop other peers.
                for (int budget = 0; budget < 24 && queue.Count != 0; budget++)
                {
                    var current = queue.Peek();
                    object[] args = { current.Id, current.Next, current.Parts.Length, current.Parts[current.Next] };
                    bool sent = pair.Key < 0 ? FunTransport.ToServer(Rpc, args) : FunTransport.ToClient(pair.Key, Rpc, Channel.Reliable, args);
                    if (!sent) break;
                    if (++current.Next == current.Parts.Length) queue.Dequeue();
                }
                if (queue.Count == 0) Queues.Remove(pair.Key);
            }
        }
        private static void OnChunk(FunRPCContext context, FunRPCArguments args)
        {
            if (args.Count != 4) return;
            string id = args.Get<string>(0), part = args.Get<string>(3);
            int index = args.Get<int>(1), count = args.Get<int>(2);
            if (id == null || id.Length > 32 || count < 1 || count > MaxParts || index < 0 || index >= count || part == null || part.Length > ChunkSize) return;
            string senderKey = context.Kind == FunRPCKind.Server ? "s:" + context.SenderClientId : "c";
            string key = senderKey + ":" + id;
            if (!Partial.TryGetValue(key, out var incoming))
            {
                if (Partial.Count >= 64 || Partial.Keys.Count(k => k.StartsWith(senderKey + ":", StringComparison.Ordinal)) >= 4) return;
                Partial[key] = incoming = new Incoming { Parts = new string[count], Expires = Time.unscaledTime + 60 };
            }
            if (incoming.Parts.Length != count) { Partial.Remove(key); return; }
            if (incoming.Parts[index] == null) { incoming.Parts[index] = part; incoming.Count++; }
            if (incoming.Count != count) return;
            Partial.Remove(key);
            try { Received?.Invoke(context, FunJson.Read<FunPacket>(string.Concat(incoming.Parts))); }
            catch (Exception e) { MelonLoader.MelonLogger.Warning("[FunNetwork] Invalid control packet: " + e.Message); }
        }
    }
}
