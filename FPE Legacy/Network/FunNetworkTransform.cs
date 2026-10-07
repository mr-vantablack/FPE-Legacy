using System;
using System.Collections.Generic;
using System.Linq;
using FPE_Legacy.Content;
using FPE_Legacy.Rpc;
using Il2CppFishNet.Transporting;
using UnityEngine;

namespace FPE_Legacy.Networking
{
    internal sealed class FunTransformSample
    {
        internal ulong Id;
        internal uint Sequence, Revision;
        internal double Time;
        internal Vector3 Position, Scale;
        internal Quaternion Rotation;
        internal bool Teleport;
    }
    internal sealed class FunTransformBatch
    {
        internal string Epoch, Token;
        internal List<FunTransformSample> Samples = new();
    }
    public sealed class FunNetworkTransform
    {
        private readonly FunNetworkIdentity _identity;
        private readonly List<FunTransformSample> _samples = new();
        private uint _lastSequence;
        private uint _revision;
        private double _clockOffset;
        private double _playback = double.NegativeInfinity;
        private bool _hasTime;
        private float _nextSend, _nextKey;
        private Vector3 _sentPosition, _sentScale;
        private Quaternion _sentRotation;
        private bool _sent;
        internal FunNetworkTransform(FunNetworkIdentity identity) { _identity = identity; }
        public void Teleport(Vector3 position, Quaternion rotation)
        {
            if (!FunNetwork.IsServer) throw new InvalidOperationException("Teleport is server-only.");
            _identity.Transform.SetPositionAndRotation(position, rotation);
            _identity.TransformRevision++; _sent = false;
            FunNetwork.SendTeleport(_identity);
        }
        internal FunTransformSample Capture(bool teleport = false)
        {
            var t = _identity.Transform;
            return new FunTransformSample { Id = _identity.Id, Sequence = ++_identity.Sequence, Revision = _identity.TransformRevision,
                Time = Time.realtimeSinceStartupAsDouble, Position = t.position, Rotation = t.rotation, Scale = t.localScale, Teleport = teleport };
        }
        internal FunTransformSample TickServer(out bool reliable)
        {
            reliable = false; float now = Time.unscaledTime;
            if (now < _nextSend) return null;
            var o = _identity.Options; _nextSend = now + 1f / o.SendRate;
            var t = _identity.Transform;
            bool changed = !_sent || (o.SyncPosition && Vector3.Distance(_sentPosition, t.position) > o.PositionThreshold) ||
                (o.SyncRotation && Quaternion.Angle(_sentRotation, t.rotation) > o.RotationThreshold) || (o.SyncScale && Vector3.Distance(_sentScale, t.localScale) > o.ScaleThreshold);
            reliable = now >= _nextKey;
            if (!changed && !reliable) return null;
            if (reliable) _nextKey = now + 1f;
            _sentPosition = t.position; _sentRotation = t.rotation; _sentScale = t.localScale; _sent = true;
            return Capture();
        }
        internal void Reset(uint revision)
        { _revision = revision; _lastSequence = 0; _samples.Clear(); _hasTime = false; _playback = double.NegativeInfinity; }
        internal void Receive(FunTransformSample sample)
        {
            if (!Valid(sample) || sample.Revision < _revision || (sample.Revision == _revision && sample.Sequence <= _lastSequence)) return;
            bool reset = sample.Teleport || sample.Revision > _revision;
            _lastSequence = sample.Sequence; _revision = sample.Revision;
            double offset = Time.realtimeSinceStartupAsDouble - sample.Time;
            // Use the smallest observed clock offset to reduce jitter from network delays.
            if (!_hasTime || reset) { _clockOffset = offset; _hasTime = true; _playback = double.NegativeInfinity; }
            else _clockOffset = Math.Min(_clockOffset, offset);
            if (reset) { _samples.Clear(); Apply(sample); }
            _samples.Add(sample); if (_samples.Count > 64) _samples.RemoveAt(0);
        }
        internal void TickClient()
        {
            if (_samples.Count == 0) return;
            double target = Time.realtimeSinceStartupAsDouble - _clockOffset - _identity.Options.InterpolationDelay;
            _playback = Math.Max(_playback, target);
            while (_samples.Count > 2 && _samples[1].Time <= _playback) _samples.RemoveAt(0);
            if (_samples.Count == 1 || _playback <= _samples[0].Time) { Apply(_samples[0]); return; }
            var a = _samples[0]; var b = _samples[1];
            float factor = b.Time <= a.Time ? 1 : Mathf.Clamp01((float)((_playback - a.Time) / (b.Time - a.Time)));
            Apply(new FunTransformSample { Position = Vector3.Lerp(a.Position, b.Position, factor), Rotation = Quaternion.Slerp(a.Rotation, b.Rotation, factor), Scale = Vector3.Lerp(a.Scale, b.Scale, factor) });
        }
        private void Apply(FunTransformSample sample)
        {
            var t = _identity.Transform; var o = _identity.Options;
            if (o.SyncPosition) t.position = sample.Position;
            if (o.SyncRotation) t.rotation = sample.Rotation;
            if (o.SyncScale) t.localScale = sample.Scale;
        }
        internal static bool Valid(FunTransformSample s) => double.IsFinite(s.Time) && Finite(s.Position) && Finite(s.Scale) && float.IsFinite(s.Rotation.x) && float.IsFinite(s.Rotation.y) && float.IsFinite(s.Rotation.z) && float.IsFinite(s.Rotation.w);
        private static bool Finite(Vector3 v) => float.IsFinite(v.x) && float.IsFinite(v.y) && float.IsFinite(v.z);
        internal static void RegisterWire()
        {
            // ID 180 must match on the host and clients; check for conflicts with other mods.
            FunRPCSerializer.RegisterCustom<FunTransformBatch>(180, (w, batch) =>
            {
                w.WriteString(batch.Epoch); w.WriteString(batch.Token); w.WriteUInt8Unpacked((byte)batch.Samples.Count);
                foreach (var s in batch.Samples)
                { w.WriteUInt64(s.Id); w.WriteUInt32(s.Sequence); w.WriteUInt32(s.Revision); w.WriteDouble(s.Time); w.WriteBoolean(s.Teleport); w.WriteVector3(s.Position); w.WriteQuaternionUnpacked(s.Rotation); w.WriteVector3(s.Scale); }
            }, r =>
            {
                var b = new FunTransformBatch { Epoch = r.ReadStringAllocated(), Token = r.ReadStringAllocated() };
                int count = r.ReadUInt8Unpacked(); if (count > 8) throw new FormatException("Oversized Transform batch.");
                for (int i = 0; i < count; i++) b.Samples.Add(new FunTransformSample { Id = r.ReadUInt64(), Sequence = r.ReadUInt32(), Revision = r.ReadUInt32(), Time = r.ReadDouble(), Teleport = r.ReadBoolean(), Position = r.ReadVector3(), Rotation = r.ReadQuaternionUnpacked(), Scale = r.ReadVector3() });
                return b;
            });
        }
    }
}
