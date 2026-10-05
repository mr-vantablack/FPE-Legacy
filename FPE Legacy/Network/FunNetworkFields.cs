using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using FPE_Legacy.Content;
using FPE_Legacy.Rpc;
using UnityEngine;

namespace FPE_Legacy.Networking
{
    internal sealed class FunFieldValue
    {
        public string Component { get; set; }
        public string Field { get; set; }
        public JsonElement Value { get; set; }
    }
    internal sealed class FunNetworkFields
    {
        private sealed class Field
        {
            internal string Component;
            internal object Instance;
            internal FieldInfo Info;
            internal Type Type;
            internal FunSerializableAttribute Attribute;
            internal JsonElement Canonical;
            internal string Observed;
            internal float Next;
        }
        private readonly FunNetworkIdentity _identity;
        private readonly List<Field> _fields = new();
        internal FunNetworkFields(FunNetworkIdentity identity)
        {
            _identity = identity;
            foreach (var pair in identity.Content.Instances)
                for (var type = pair.Value.GetType(); type != null && type != typeof(MonoBehaviour); type = type.BaseType)
                    foreach (var info in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                    {
                        var attribute = info.GetCustomAttribute<FunSerializableAttribute>(true);
                        if (attribute == null) continue;
                        if (info.IsInitOnly || _fields.Any(f => f.Component == pair.Key && f.Info.Name == info.Name)) throw new InvalidOperationException("Readonly or shadowed synchronized field: " + info.Name);
                        var value = FunJson.Element(FunValues.Encode(FunValues.GetField(pair.Value, info)));
                        _fields.Add(new Field { Component = pair.Key, Instance = pair.Value, Info = info, Type = FunValues.UnwrapType(info.FieldType), Attribute = attribute, Canonical = value, Observed = value.GetRawText() });
                    }
        }
        internal List<FunFieldValue> Capture(int recipient = -1, bool initial = true)
        {
            return _fields.Where(f => initial || !f.Attribute.ExcludeOwner || recipient != _identity.OwnerClientId).Select(f => new FunFieldValue { Component = f.Component, Field = f.Info.Name, Value = FunJson.Element(FunValues.Encode(FunValues.GetField(f.Instance, f.Info))) }).ToList();
        }
        internal bool Tick()
        {
            bool changed = false;
            foreach (var f in _fields)
            {
                var current = FunJson.Element(FunValues.Encode(FunValues.GetField(f.Instance, f.Info)));
                string text = current.GetRawText();
                if (text == f.Observed || Time.unscaledTime < f.Next) continue;
                f.Next = Time.unscaledTime + Math.Max(0.02f, f.Attribute.SendRate);
                object old = FunValues.Decode(f.Canonical, f.Type);
                if (FunNetwork.IsServer)
                {
                    f.Canonical = current; f.Observed = text; changed = true;
                    Changed(f, old, FunValues.GetField(f.Instance, f.Info));
                }
                else if (_identity.IsOwner && f.Attribute.WritePermission == FunSerializableWritePermission.Owner)
                {
                    // Authoritative echo is always delivered, including to the owner.
                    if (FunNetwork.RequestField(_identity, new FunFieldValue { Component = f.Component, Field = f.Info.Name, Value = current })) f.Observed = text;
                }
                else { FunValues.SetField(f.Instance, f.Info, old); f.Observed = f.Canonical.GetRawText(); }
            }
            return changed;
        }
        internal void Apply(List<FunFieldValue> values, bool ownerRequest = false)
        {
            if (values == null) return;
            if (values.Count > _fields.Count) throw new FormatException("Too many field values.");
            var decoded = new List<(Field field, object value, JsonElement json)>();
            var seen = new HashSet<string>();
            foreach (var item in values)
            {
                if (!seen.Add(item.Component + "\n" + item.Field)) throw new FormatException("Duplicate field.");
                var field = _fields.FirstOrDefault(f => f.Component == item.Component && f.Info.Name == item.Field) ?? throw new FormatException("Unknown synchronized component/field.");
                if (ownerRequest && field.Attribute.WritePermission != FunSerializableWritePermission.Owner) throw new InvalidOperationException("Client wrote a server-only field.");
                var value = FunValues.Decode(item.Value, field.Type);
                decoded.Add((field, value, FunJson.Element(FunValues.Encode(value))));
            }
            foreach (var item in decoded)
            {
                var f = item.field; object old = FunValues.GetField(f.Instance, f.Info);
                bool changed = FunJson.Write(FunValues.Encode(old)) != item.json.GetRawText();
                FunValues.SetField(f.Instance, f.Info, item.value); f.Canonical = item.json; f.Observed = item.json.GetRawText();
                if (changed) Changed(f, old, item.value);
            }
        }
        private static void Changed(Field field, object old, object value)
        {
            if (string.IsNullOrEmpty(field.Attribute.OnChange)) return;
            try
            {
                var methods = field.Instance.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                    .Where(m => m.Name == field.Attribute.OnChange && m.ReturnType == typeof(void));
                foreach (var method in methods)
                {
                    var p = method.GetParameters();
                    if (p.Length > 2 || p.Any(a => !a.ParameterType.IsAssignableFrom(field.Type))) continue;
                    method.Invoke(field.Instance, p.Length == 0 ? null : p.Length == 1 ? new[] { value } : new[] { old, value }); return;
                }
                throw new MissingMethodException(field.Attribute.OnChange);
            }
            catch (Exception e) { MelonLoader.MelonLogger.Error("[FunNetwork] OnChange: " + e); }
        }
    }
}
