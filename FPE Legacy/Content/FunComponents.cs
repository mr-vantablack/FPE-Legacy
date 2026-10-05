using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;

namespace FPE_Legacy.Content
{
    [AttributeUsage(AttributeTargets.Field)]
    public sealed class FunContentFieldAttribute : Attribute { }
    public static class FunValueConverters
    {
        private static readonly Dictionary<Type, Func<JsonElement, object>> Readers = new();
        private static readonly Dictionary<Type, Func<object, object>> Writers = new();
        public static void Register<T>(Func<JsonElement, T> read, Func<T, object> write = null)
        {
            FunMainThread.Require();
            if (read == null) throw new ArgumentNullException(nameof(read));
            Readers[typeof(T)] = j => read(j);
            if (write != null) Writers[typeof(T)] = value => write((T)value);
        }
        internal static bool Read(Type type, JsonElement json, out object value)
        { if (Readers.TryGetValue(type, out var reader)) { value = reader(json); return true; } value = null; return false; }
        internal static bool Write(object value, out object result)
        { if (Writers.TryGetValue(value.GetType(), out var writer)) { result = writer(value); return true; } result = null; return false; }
    }
    public static class FunComponents
    {
        internal sealed class Registration
        {
            internal Type Type;
            internal Func<GameObject, Component> Add;
            internal Func<GameObject, Component[]> Find;
        }
        private static readonly Dictionary<string, Registration> Types = new(StringComparer.Ordinal);
        public static void Register<T>(string id) where T : Component
        {
            FunMainThread.Require();
            if (FunContent.CatalogueLocked) throw new InvalidOperationException("Register components before ScanAsync.");
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Component id is empty.");
            if (Types.TryGetValue(id, out var old) && old.Type != typeof(T)) throw new InvalidOperationException("Duplicate component type id: " + id);
            //Types[id] = new Registration { Type = typeof(T), Add = go => go.AddComponent<T>(), Find = go => go.GetComponents<T>().Cast<Component>().ToArray() };
            Types[id] = new Registration
            {
                Type = typeof(T),
                Add = go => go.AddComponent<T>(),
                Find = go => System.Linq.Enumerable
                    .Cast<Component>(go.GetComponents<T>())
                    .ToArray()
            };
        }
        internal static Registration Get(string id) => Types.TryGetValue(id ?? "", out var value) ? value : throw new KeyNotFoundException("Register this component type before scanning content: " + id);
        internal static string Fingerprint() => string.Join("\n", Types.OrderBy(p => p.Key).Select(p => p.Key + "=" + p.Value.Type.FullName + ":" + (p.Key.StartsWith("unity.", StringComparison.Ordinal) ? "builtin" : p.Value.Type.Assembly.ManifestModule.ModuleVersionId.ToString())));
        internal static void Defaults()
        {
            Register<Rigidbody>("unity.rigidbody"); Register<BoxCollider>("unity.boxCollider");
            Register<SphereCollider>("unity.sphereCollider"); Register<CapsuleCollider>("unity.capsuleCollider");
            Register<AudioSource>("unity.audioSource"); Register<Light>("unity.light");
        }
        internal static object Managed(Type type, Component component)
        {
            // Native proxy wrappers must resolve back to the ORIGINAL managed injected instance
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var helper = assembly.GetType("Il2CppInterop.Runtime.Injection.ClassInjectorBase");
                var method = helper?.GetMethod("GetMonoObjectFromIl2CppPointer", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                if (method == null) continue;
                try { var managed = method.Invoke(null, new object[] { component.Pointer }); if (managed != null && type.IsInstanceOfType(managed)) return managed; }
                catch { }
                break;
            }
            if (type.IsInstanceOfType(component)) return component;
            var ctor = type.GetConstructor(new[] { typeof(IntPtr) });
            if (ctor != null && type.Assembly != typeof(FunComponents).Assembly) return ctor.Invoke(new object[] { component.Pointer });
            throw new InvalidOperationException("Cannot resolve managed component instance: " + type.FullName);
        }
        internal static Dictionary<string, object> Create(GameObject root, List<FunComponentDefinition> definitions)
        {
            var result = new Dictionary<string, object>(StringComparer.Ordinal);
            foreach (var definition in definitions)
            {
                if (string.IsNullOrWhiteSpace(definition.Id) || result.ContainsKey(definition.Id)) throw new FormatException("Duplicate/empty component id: " + definition.Id);
                var registration = Get(definition.Type);
                var target = FunValues.FindTransform(root, definition.Target).gameObject;
                var found = registration.Find(target);
                Component component = null;
                if (definition.Mode != "add")
                {
                    if (definition.Mode != "getOrAdd" && definition.Mode != "require") throw new FormatException("Unknown component mode: " + definition.Mode);
                    if (definition.ExistingIndex.HasValue)
                    {
                        int index = definition.ExistingIndex.Value;
                        if (index < 0 || index >= found.Length) throw new FormatException("existingIndex out of range: " + definition.Id);
                        component = found[index];
                    }
                    else if (found.Length > 1) throw new FormatException("Ambiguous component. Specify existingIndex: " + definition.Id);
                    else if (found.Length == 1) component = found[0];
                    if (component == null && definition.Mode == "require") throw new FormatException("Required component is absent: " + definition.Id);
                }
                if (component == null)
                {
                    component = registration.Add(target);
                    if (component == null) throw new InvalidOperationException("AddComponent failed: " + definition.Type);
                    if (definition.Mode == "add" && found.Any(c => c.Pointer == component.Pointer)) throw new InvalidOperationException("This type disallows multiple components: " + definition.Type);
                }
                if (result.Values.OfType<Component>().Any(c => c.Pointer == component.Pointer)) throw new FormatException("Two component IDs address the same component: " + definition.Id);
                result.Add(definition.Id, Managed(registration.Type, component));
            }
            return result;
        }
        internal static void Configure(GameObject root, List<FunComponentDefinition> definitions, Dictionary<string, object> instances)
        {
            foreach (var definition in definitions)
            {
                object instance = instances[definition.Id];
                foreach (var pair in definition.Fields)
                {
                    try
                    {
                        var field = FunValues.Field(instance.GetType(), pair.Key);
                        if (field == null || field.IsStatic || field.IsInitOnly || (!field.IsPublic && !field.IsDefined(typeof(FunContentFieldAttribute), true) && !field.GetCustomAttributes(true).Any(a => a.GetType().FullName == "UnityEngine.SerializeField")))
                            throw new MissingFieldException("A writable public or [FunContentField] instance field is required.");
                        Type valueType = FunValues.UnwrapType(field.FieldType);
                        FunValues.SetField(instance, field, FunValues.Decode(pair.Value, valueType, root, instances));
                    }
                    catch (Exception e) { throw new FormatException($"Component '{definition.Id}', field '{pair.Key}': {e.Message}", e); }
                }
                foreach (var pair in definition.Properties)
                {
                    try
                    {
                        var property = instance.GetType().GetProperty(pair.Key, BindingFlags.Instance | BindingFlags.Public);
                        if (property?.SetMethod == null || !property.SetMethod.IsPublic || property.GetIndexParameters().Length != 0)
                            throw new MissingMemberException("A public writable non-indexed property is required.");
                        property.SetValue(instance, FunValues.Decode(pair.Value, property.PropertyType, root, instances));
                    }
                    catch (Exception e) { throw new FormatException($"Component '{definition.Id}', property '{pair.Key}': {e.Message}", e); }
                }
            }
        }
    }
    internal static class FunValues
    {
        internal static FieldInfo Field(Type type, string name)
        {
            while (type != null) { var f = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly); if (f != null) return f; type = type.BaseType; }
            return null;
        }
        internal static Type UnwrapType(Type type)
        {
            if (type.FullName == "Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppStringField") return typeof(string);
            if (type.IsGenericType && type.Namespace == "Il2CppInterop.Runtime.InteropTypes.Fields") return type.GetGenericArguments()[0];
            return type;
        }
        internal static object GetField(object obj, FieldInfo field)
        {
            var value = field.GetValue(obj); return UnwrapType(field.FieldType) == field.FieldType ? value : value?.GetType().GetProperty("Value")?.GetValue(value);
        }
        internal static void SetField(object obj, FieldInfo field, object value)
        {
            if (UnwrapType(field.FieldType) == field.FieldType) field.SetValue(obj, value);
            else { var wrapper = field.GetValue(obj) ?? throw new InvalidOperationException("Null Il2Cpp field wrapper."); wrapper.GetType().GetProperty("Value").SetValue(wrapper, value); }
        }
        internal static Transform FindTransform(GameObject root, string path)
        {
            if (string.IsNullOrEmpty(path)) return root.transform;
            Transform current = root.transform;
            foreach (string part in path.Split('/'))
            {
                if (part == "" || part == "." || part == "..") throw new FormatException("Invalid child path: " + path);
                Transform match = null;
                for (int i = 0; i < current.childCount; i++)
                {
                    var child = current.GetChild(i);
                    if (child.name != part) continue;
                    if (match != null) throw new FormatException("Ambiguous child path: " + path);
                    match = child;
                }
                current = match ?? throw new FormatException("Child not found: " + path);
            }
            return current;
        }
        internal static object Decode(JsonElement json, Type type, GameObject root = null, Dictionary<string, object> components = null)
        {
            if (json.ValueKind == JsonValueKind.Null)
            {
                if (type.IsValueType && Nullable.GetUnderlyingType(type) == null) throw new FormatException("Null for value type.");
                return null;
            }
            type = Nullable.GetUnderlyingType(type) ?? type;
            if (FunValueConverters.Read(type, json, out var customValue)) return customValue;
            if (json.ValueKind == JsonValueKind.Object)
            {
                if (json.TryGetProperty("$component", out var component))
                {
                    if (components == null || !components.TryGetValue(component.GetString(), out var instance) || !type.IsInstanceOfType(instance)) throw new FormatException("Invalid component reference/type.");
                    return instance;
                }
                if (json.TryGetProperty("$object", out var obj))
                {
                    if (root == null) throw new FormatException("Object references require a prefab instance.");
                    var target = FindTransform(root, obj.GetString());
                    if (type == typeof(GameObject)) return target.gameObject;
                    if (type == typeof(Transform)) return target;
                    throw new FormatException("$object supports GameObject/Transform; use $component for components.");
                }
                if (json.TryGetProperty("$asset", out var asset)) return FunContent.GetLoaded(asset.GetString(), type);
            }
            if (type == typeof(Vector2)) return new Vector2(Number(json, "x"), Number(json, "y"));
            if (type == typeof(Vector3)) return new Vector3(Number(json, "x"), Number(json, "y"), Number(json, "z"));
            if (type == typeof(Vector4)) return new Vector4(Number(json, "x"), Number(json, "y"), Number(json, "z"), Number(json, "w"));
            if (type == typeof(Quaternion)) return new Quaternion(Number(json, "x"), Number(json, "y"), Number(json, "z"), Number(json, "w"));
            if (type == typeof(Color)) return new Color(Number(json, "r"), Number(json, "g"), Number(json, "b"), json.TryGetProperty("a", out var a) ? a.GetSingle() : 1);
            if (type == typeof(Color32)) return new Color32(json.GetProperty("r").GetByte(), json.GetProperty("g").GetByte(), json.GetProperty("b").GetByte(), json.TryGetProperty("a", out var alpha) ? alpha.GetByte() : (byte)255);
            if (type == typeof(Vector2Int)) return new Vector2Int(json.GetProperty("x").GetInt32(), json.GetProperty("y").GetInt32());
            if (type == typeof(Vector3Int)) return new Vector3Int(json.GetProperty("x").GetInt32(), json.GetProperty("y").GetInt32(), json.GetProperty("z").GetInt32());
            if (type.IsEnum) return json.ValueKind == JsonValueKind.String ? Enum.Parse(type, json.GetString(), false) : Enum.ToObject(type, json.GetInt64());
            if (type.IsArray || (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(List<>)))
            {
                var element = type.IsArray ? type.GetElementType() : type.GetGenericArguments()[0];
                var items = json.EnumerateArray().Select(j => Decode(j, element, root, components)).ToArray();
                if (type.IsArray) { var array = Array.CreateInstance(element, items.Length); for (int i = 0; i < items.Length; i++) array.SetValue(items[i], i); return array; }
                var list = (IList)Activator.CreateInstance(type); foreach (var item in items) list.Add(item); return list;
            }
            if (type.IsPrimitive || type == typeof(string) || type == typeof(decimal)) return JsonSerializer.Deserialize(json.GetRawText(), type, FunJson.Options);
            throw new NotSupportedException("Unsupported field type: " + type.FullName + ". Register a dedicated adapter instead of silently constructing it.");
        }
        internal static float Number(JsonElement json, string key)
        { float value = json.GetProperty(key).GetSingle(); if (!float.IsFinite(value)) throw new FormatException("Non-finite number."); return value; }
        internal static object Encode(object value)
        {
            if (value == null) return null;
            if (FunValueConverters.Write(value, out var customValue)) return customValue;
            if (value is Vector2 v2) return new { x = v2.x, y = v2.y };
            if (value is Vector3 v3) return new { x = v3.x, y = v3.y, z = v3.z };
            if (value is Vector4 v4) return new { x = v4.x, y = v4.y, z = v4.z, w = v4.w };
            if (value is Quaternion q) return new { x = q.x, y = q.y, z = q.z, w = q.w };
            if (value is Color c) return new { r = c.r, g = c.g, b = c.b, a = c.a };
            if (value is Color32 c32) return new { r = c32.r, g = c32.g, b = c32.b, a = c32.a };
            if (value is Vector2Int i2) return new { x = i2.x, y = i2.y };
            if (value is Vector3Int i3) return new { x = i3.x, y = i3.y, z = i3.z };
            if (value.GetType().IsEnum) return value.ToString();
            if (value is string || value.GetType().IsPrimitive || value is decimal) return value;
            if (value is IList list) { var result = new List<object>(); foreach (var item in list) result.Add(Encode(item)); return result; }
            throw new NotSupportedException("Unsupported synchronized field value: " + value.GetType().FullName);
        }
    }
}
