using HarmonyLib;
using Il2CppFishNet.Connection;
using Il2CppFishNet.Object;
using Il2CppFishNet.Transporting;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using MelonLoader;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using UnityEngine;

namespace FPE_Legacy.Rpc
{
    public enum FunSerializableWritePermission
    {
        Server,
        Owner
    }

    /// <summary>
    /// Runtime FunSerializable for modded components
    ///
    /// Server is authoritative by default. Owner permission lets the owning client propose changes
    /// the server validates ownership, applies the value, and then broadcasts the authoritative value to other clients
    /// </summary>
    [AttributeUsage(AttributeTargets.Field, AllowMultiple = false, Inherited = true)]
    public sealed class FunSerializableAttribute : Attribute
    {
        /// <summary>Who can change this value. Default: server only</summary>
        public FunSerializableWritePermission WritePermission { get; set; } = FunSerializableWritePermission.Server;

        /// <summary>Optional instance method invoked after a value changes: (), (newValue), or (oldValue, newValue)</summary>
        public string OnChange { get; set; }

        /// <summary>Minimum seconds between network sends for this field. 0 means the next sync tick</summary>
        public float SendRate { get; set; }

        /// <summary>Use FishNet Unreliable channel for deltas/owner writes. Snapshots are always Reliable</summary>
        public bool Unreliable { get; set; }

        /// <summary>Do not send server deltas back to the owning client. Usually leave false. Idk when this might be needed</summary>
        public bool ExcludeOwner { get; set; }
    }

    internal sealed class FunSerializableFieldDefinition
    {
        public FieldInfo Field;
        public FunSerializableAttribute Attribute;
        public string Name;
        public Type ValueType;
        public MethodInfo OnChangeMethod;
        public bool WrappedField;
    }

    internal sealed class FunSerializableTypeDefinition
    {
        public Type ComponentType;
        public string TypeKey;
        public Il2CppSystem.Type Il2CppType;
        public readonly Dictionary<string, FunSerializableFieldDefinition> Fields = new Dictionary<string, FunSerializableFieldDefinition>(StringComparer.Ordinal);
    }

    internal sealed class FunSerializableFieldState
    {
        public object AuthoritativeValue;
        public object LastObservedValue;
        public float NextSendTime;
        public bool WarnedUnauthorizedWrite;
        public bool HasAuthoritativeValue;
    }

    internal sealed class FunSerializableInstanceState
    {
        public string Key;
        public long NativePointer;
        public object Instance;
        public Component Component;
        public NetworkBehaviour Carrier;
        public FunSerializableTypeDefinition Definition;
        public bool SnapshotRequested;
        public bool SnapshotReceived;
        public float LastSeenTime;
        public readonly Dictionary<string, FunSerializableFieldState> Fields = new Dictionary<string, FunSerializableFieldState>(StringComparer.Ordinal);
    }

    /// <summary>
    /// Runtime field synchronization built on top of FunRPC.
    /// Supports server authority, owner writes, delta updates, OnChange callbacks, late-join snapshots and other bullshit
    /// </summary>
    public static class FunSerializableRuntime
    {
        private const string OwnerSetRpc = "__FUNSERIALIZABLE_OWNER_SET_V1";
        private const string DeltaRpc = "__FUNSERIALIZABLE_DELTA_V1";
        private const string SnapshotRequestRpc = "__FUNSERIALIZABLE_SNAPSHOT_REQUEST_V1";
        private const string SnapshotValueRpc = "__FUNSERIALIZABLE_SNAPSHOT_VALUE_V1";

        private const float DiscoveryInterval = 0.35f;
        private const float DeadStateDelay = 1.5f;

        private static readonly Dictionary<string, FunSerializableTypeDefinition> _definitions = new Dictionary<string, FunSerializableTypeDefinition>(StringComparer.Ordinal);
        private static readonly Dictionary<Type, FunSerializableTypeDefinition> _definitionsByType = new Dictionary<Type, FunSerializableTypeDefinition>();
        private static readonly Dictionary<string, FunSerializableInstanceState> _instances = new Dictionary<string, FunSerializableInstanceState>(StringComparer.Ordinal);
        private static readonly HashSet<Assembly> _scannedAssemblies = new HashSet<Assembly>();
        private static readonly List<string> _removeKeys = new List<string>();

        private static MethodInfo _getMonoObjectFromIl2CppPointer;
        private static float _nextDiscoveryTime;
        private static bool _initialized;

        public static void Initialize(params Assembly[] assemblies)
        {
            if (!_initialized)
            {
                _initialized = true;
                ResolveInteropHelpers();
                RegisterInternalHandlers();
                AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
            }

            if (assemblies != null)
            {
                foreach (Assembly assembly in assemblies)
                    RegisterAssembly(assembly);
            }

            MelonLogger.Msg($"[FunSerializable] Runtime ready. Components={_definitions.Count}, fields={CountFields()}");
        }

        public static void Tick()
        {
            if (!_initialized)
                return;

            float now = Time.unscaledTime;
            if (now >= _nextDiscoveryTime)
            {
                _nextDiscoveryTime = now + DiscoveryInterval;
                DiscoverInstances(now);
                CleanupDeadStates(now);
            }

            if (_instances.Count == 0)
                return;

            FunSerializableInstanceState[] states = new FunSerializableInstanceState[_instances.Count];
            _instances.Values.CopyTo(states, 0);

            foreach (FunSerializableInstanceState state in states)
            {
                if (state == null || state.Instance == null || state.Component == null)
                    continue;

                try
                {
                    TickInstance(state, now);
                }
                catch (Exception e)
                {
                    MelonLogger.Warning($"[FunSerializable] Tick failed for {state.Definition?.TypeKey}: {e.Message}");
                }
            }
        }

        public static void RegisterAssembly(Assembly assembly)
        {
            if (assembly == null || _scannedAssemblies.Contains(assembly))
                return;

            _scannedAssemblies.Add(assembly);

            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException e) { types = e.Types; }
            catch { return; }

            if (types == null)
                return;

            foreach (Type type in types)
            {
                if (type == null || !typeof(Component).IsAssignableFrom(type))
                    continue;

                FieldInfo[] fields;
                try { fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); }
                catch { continue; }

                FunSerializableTypeDefinition definition = null;

                foreach (FieldInfo field in fields)
                {
                    FunSerializableAttribute attribute = null;
                    try { attribute = field.GetCustomAttribute<FunSerializableAttribute>(true); }
                    catch { }

                    if (attribute == null)
                        continue;

                    if (field.IsStatic)
                        throw new InvalidOperationException($"[FunSerializable] field {type.FullName}.{field.Name} cannot be static.");

                    if (definition == null)
                    {
                        definition = GetOrCreateDefinition(type);
                        if (definition == null)
                            break;
                    }

                    RegisterField(definition, field, attribute);
                }
            }
        }

        private static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs e)
        {
            try { RegisterAssembly(e.LoadedAssembly); }
            catch (Exception ex) { MelonLogger.Warning($"[FunSerializable] Could not scan assembly {e.LoadedAssembly?.GetName().Name}: {ex.Message}"); }
        }

        private static FunSerializableTypeDefinition GetOrCreateDefinition(Type type)
        {
            if (_definitionsByType.TryGetValue(type, out FunSerializableTypeDefinition existing))
                return existing;

            Il2CppSystem.Type il2cppType;
            try { il2cppType = Il2CppType.From(type, false); }
            catch { il2cppType = null; }

            string key = BuildTypeKey(type);
            FunSerializableTypeDefinition definition = new FunSerializableTypeDefinition { ComponentType = type, TypeKey = key, Il2CppType = il2cppType };
            _definitionsByType[type] = definition;
            _definitions[key] = definition;
            return definition;
        }

        private static void RegisterField(FunSerializableTypeDefinition definition, FieldInfo field, FunSerializableAttribute attribute)
        {
            if (definition.Fields.ContainsKey(field.Name))
                return;

            Type valueType = field.FieldType;
            bool wrapped = TryGetWrappedFieldValueType(field.FieldType, out Type wrappedType);
            if (wrapped)
                valueType = wrappedType;

            if (attribute.SendRate < 0f)
                attribute.SendRate = 0f;

            MethodInfo onChange = null;
            if (!string.IsNullOrWhiteSpace(attribute.OnChange))
            {
                onChange = FindOnChangeMethod(definition.ComponentType, attribute.OnChange, valueType);
                if (onChange == null)
                    throw new InvalidOperationException($"[FunSerializable] OnChange method '{attribute.OnChange}' was not found or has an invalid signature for {definition.ComponentType.FullName}.{field.Name}.");
            }

            FunSerializableFieldDefinition fieldDefinition = new FunSerializableFieldDefinition
            {
                Field = field,
                Attribute = attribute,
                Name = field.Name,
                ValueType = valueType,
                OnChangeMethod = onChange,
                WrappedField = wrapped
            };

            definition.Fields.Add(field.Name, fieldDefinition);
            MelonLogger.Msg($"[FunSerializable] Registered {definition.ComponentType.FullName}.{field.Name} ({valueType.FullName}), permission={attribute.WritePermission}");
        }

        private static MethodInfo FindOnChangeMethod(Type componentType, string name, Type valueType)
        {
            MethodInfo[] methods = componentType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            foreach (MethodInfo method in methods)
            {
                if (!string.Equals(method.Name, name, StringComparison.Ordinal) || method.ReturnType != typeof(void))
                    continue;

                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length == 0)
                    return method;
                if (parameters.Length == 1 && IsCompatibleCallbackType(parameters[0].ParameterType, valueType))
                    return method;
                if (parameters.Length == 2 && IsCompatibleCallbackType(parameters[0].ParameterType, valueType) && IsCompatibleCallbackType(parameters[1].ParameterType, valueType))
                    return method;
            }

            return null;
        }

        private static bool IsCompatibleCallbackType(Type parameterType, Type valueType)
        {
            return parameterType == valueType || parameterType == typeof(object) || parameterType.IsAssignableFrom(valueType);
        }

        private static bool TryGetWrappedFieldValueType(Type fieldType, out Type valueType)
        {
            valueType = null;

            if (fieldType.FullName == "Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppStringField")
            {
                valueType = typeof(string);
                return true;
            }

            if (!fieldType.IsGenericType)
                return false;

            Type generic = fieldType.GetGenericTypeDefinition();
            string fullName = generic.FullName;
            if (fullName == "Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppValueField`1" || fullName == "Il2CppInterop.Runtime.InteropTypes.Fields.Il2CppReferenceField`1")
            {
                valueType = fieldType.GetGenericArguments()[0];
                return true;
            }

            return false;
        }

        private static void ResolveInteropHelpers()
        {
            try
            {
                Type classInjectorBase = AccessTools.TypeByName("Il2CppInterop.Runtime.Injection.ClassInjectorBase");
                if (classInjectorBase != null)
                    _getMonoObjectFromIl2CppPointer = AccessTools.Method(classInjectorBase, "GetMonoObjectFromIl2CppPointer", new[] { typeof(IntPtr) });
            }
            catch (Exception e)
            {
                MelonLogger.Warning("[FunSerializable] Could not resolve ClassInjectorBase.GetMonoObjectFromIl2CppPointer: " + e.Message);
            }

            if (_getMonoObjectFromIl2CppPointer == null)
                MelonLogger.Warning("[FunSerializable] Managed injected-instance lookup is unavailable. Plain managed [FunSerializable] fields may not track correctly across wrapper recreation.");
        }

        private static void RegisterInternalHandlers()
        {
            FunRPCRuntime.RegisterServer(OwnerSetRpc, OnOwnerSetReceived);
            FunRPCRuntime.RegisterServer(SnapshotRequestRpc, OnSnapshotRequestReceived);
            FunRPCRuntime.RegisterObservers(DeltaRpc, OnDeltaReceived);
            FunRPCRuntime.RegisterTarget(SnapshotValueRpc, OnSnapshotValueReceived);
        }

        private static void DiscoverInstances(float now)
        {
            foreach (FunSerializableTypeDefinition definition in _definitions.Values)
            {
                try
                {
                    if (definition.Il2CppType == null)
                    {
                        try { definition.Il2CppType = Il2CppType.From(definition.ComponentType, false); }
                        catch { definition.Il2CppType = null; }

                        if (definition.Il2CppType == null)
                            continue;
                    }

                    var found = UnityEngine.Object.FindObjectsOfType(definition.Il2CppType);
                    foreach (UnityEngine.Object unityObject in found)
                    {
                        Component component = unityObject as Component;
                        if (component == null)
                            continue;

                        object managed = ResolveManagedInstance(definition.ComponentType, component);
                        if (managed == null)
                            continue;

                        NetworkBehaviour carrier = GetBestCarrier(managed);
                        if (carrier == null || !carrier.IsSpawned)
                            continue;

                        FunSerializableInstanceState state = EnsureInstanceState(definition, managed, component, carrier, now);
                        state.LastSeenTime = now;

                        if (!carrier.IsServerInitialized && carrier.IsClientInitialized && !state.SnapshotRequested)
                            RequestSnapshot(state);
                    }
                }
                catch (Exception e)
                {
                    MelonLogger.Warning($"[FunSerializable] Discovery failed for {definition.ComponentType.FullName}: {e.Message}");
                }
            }
        }

        private static NetworkBehaviour GetBestCarrier(object instance)
        {
            NetworkBehaviour serverCarrier = FunRPCRuntime.GetCarrierForInstance(instance, FunRPCKind.Observers);
            if (serverCarrier != null && serverCarrier.IsSpawned && serverCarrier.IsServerInitialized)
                return serverCarrier;

            NetworkBehaviour clientCarrier = FunRPCRuntime.GetCarrierForInstance(instance, FunRPCKind.Server);
            if (clientCarrier != null && clientCarrier.IsSpawned && clientCarrier.IsClientInitialized)
                return clientCarrier;

            return null;
        }

        private static FunSerializableInstanceState EnsureInstanceState(FunSerializableTypeDefinition definition, object instance, Component component, NetworkBehaviour carrier, float now)
        {
            long pointer = GetPointer(component);
            string key = definition.TypeKey + "|" + pointer.ToString("X16", CultureInfo.InvariantCulture);

            if (_instances.TryGetValue(key, out FunSerializableInstanceState existing))
            {
                existing.Instance = instance;
                existing.Component = component;
                existing.Carrier = carrier;
                existing.LastSeenTime = now;
                return existing;
            }

            FunSerializableInstanceState state = new FunSerializableInstanceState
            {
                Key = key,
                NativePointer = pointer,
                Instance = instance,
                Component = component,
                Carrier = carrier,
                Definition = definition,
                LastSeenTime = now,
                SnapshotReceived = carrier.IsServerInitialized
            };

            foreach (FunSerializableFieldDefinition field in definition.Fields.Values)
            {
                object value = GetFieldValue(state.Instance, field);
                state.Fields[field.Name] = new FunSerializableFieldState
                {
                    AuthoritativeValue = value,
                    LastObservedValue = value,
                    NextSendTime = 0f,
                    HasAuthoritativeValue = carrier.IsServerInitialized
                };
            }

            _instances.Add(key, state);
            return state;
        }

        private static void CleanupDeadStates(float now)
        {
            _removeKeys.Clear();
            foreach (KeyValuePair<string, FunSerializableInstanceState> pair in _instances)
            {
                if (now - pair.Value.LastSeenTime > DeadStateDelay)
                    _removeKeys.Add(pair.Key);
            }

            foreach (string key in _removeKeys)
                _instances.Remove(key);
        }

        private static void TickInstance(FunSerializableInstanceState state, float now)
        {
            NetworkBehaviour carrier = state.Carrier;
            if (carrier == null || !carrier.IsSpawned)
                return;

            if (carrier.IsServerInitialized)
            {
                TickServerInstance(state, now);
                return;
            }

            if (carrier.IsClientInitialized)
                TickClientInstance(state, now);
        }

        private static void TickServerInstance(FunSerializableInstanceState state, float now)
        {
            foreach (FunSerializableFieldDefinition field in state.Definition.Fields.Values)
            {
                FunSerializableFieldState fieldState = state.Fields[field.Name];
                object current = GetFieldValue(state.Instance, field);

                if (ValuesEqual(current, fieldState.AuthoritativeValue))
                {
                    fieldState.LastObservedValue = current;
                    continue;
                }

                if (now < fieldState.NextSendTime)
                    continue;

                object oldValue = fieldState.AuthoritativeValue;
                fieldState.AuthoritativeValue = current;
                fieldState.LastObservedValue = current;
                fieldState.NextSendTime = now + field.Attribute.SendRate;

                InvokeOnChange(state.Instance, field, oldValue, current);
                SendDelta(state.Carrier, state.Definition, field, current);
            }
        }

        private static void TickClientInstance(FunSerializableInstanceState state, float now)
        {
            foreach (FunSerializableFieldDefinition field in state.Definition.Fields.Values)
            {
                FunSerializableFieldState fieldState = state.Fields[field.Name];
                object current = GetFieldValue(state.Instance, field);

                if (ValuesEqual(current, fieldState.LastObservedValue))
                    continue;

                if (field.Attribute.WritePermission == FunSerializableWritePermission.Owner && state.Carrier.IsOwner)
                {
                    if (now < fieldState.NextSendTime)
                        continue;

                    object oldValue = fieldState.LastObservedValue;
                    fieldState.LastObservedValue = current;
                    fieldState.NextSendTime = now + field.Attribute.SendRate;
                    fieldState.WarnedUnauthorizedWrite = false;

                    InvokeOnChange(state.Instance, field, oldValue, current);
                    SendOwnerWrite(state, field, current);
                }
                else
                {
                    if (!fieldState.HasAuthoritativeValue)
                    {
                        fieldState.LastObservedValue = current;
                        continue;
                    }

                    if (!fieldState.WarnedUnauthorizedWrite)
                    {
                        MelonLogger.Warning($"[FunSerializable] Reverted unauthorized local write to {state.Definition.ComponentType.FullName}.{field.Name}. Permission={field.Attribute.WritePermission}, owner={state.Carrier.IsOwner}.");
                        fieldState.WarnedUnauthorizedWrite = true;
                    }

                    SetFieldValue(state.Instance, field, fieldState.AuthoritativeValue);
                    fieldState.LastObservedValue = fieldState.AuthoritativeValue;
                }
            }
        }

        private static void RequestSnapshot(FunSerializableInstanceState state)
        {
            FunRPCSendOptions options = new FunRPCSendOptions { Channel = Channel.Reliable };
            bool sent = FunRPCRuntime.SendServerFrom(state.Carrier, SnapshotRequestRpc, options, state.Definition.TypeKey);
            if (sent)
            {
                state.SnapshotRequested = true;
                MelonLogger.Msg($"[FunSerializable] Snapshot requested for {state.Definition.ComponentType.FullName} via ObjectId={state.Carrier.ObjectId}.");
            }
        }

        private static void SendOwnerWrite(FunSerializableInstanceState state, FunSerializableFieldDefinition field, object value)
        {
            FunRPCSendOptions options = new FunRPCSendOptions { Channel = field.Attribute.Unreliable ? Channel.Unreliable : Channel.Reliable };
            FunRPCRuntime.SendServerFrom(state.Carrier, OwnerSetRpc, options, state.Definition.TypeKey, field.Name, value);
        }

        private static void SendDelta(NetworkBehaviour carrier, FunSerializableTypeDefinition definition, FunSerializableFieldDefinition field, object value)
        {
            FunRPCSendOptions options = new FunRPCSendOptions
            {
                Channel = field.Attribute.Unreliable ? Channel.Unreliable : Channel.Reliable,
                ExcludeServer = true,
                ExcludeOwner = field.Attribute.ExcludeOwner
            };

            FunRPCRuntime.SendObserversFrom(carrier, DeltaRpc, options, definition.TypeKey, field.Name, value);
        }

        private static void OnOwnerSetReceived(FunRPCContext context, FunRPCArguments args)
        {
            if (context == null || context.Carrier == null || context.Sender == null || args.Count != 3)
                return;

            string typeKey = args.Get<string>(0);
            string fieldName = args.Get<string>(1);
            object incomingValue = args[2];

            if (!TryGetDefinitionAndField(typeKey, fieldName, out FunSerializableTypeDefinition definition, out FunSerializableFieldDefinition field))
                return;

            if (field.Attribute.WritePermission != FunSerializableWritePermission.Owner)
            {
                MelonLogger.Warning($"[FunSerializable] ClientId={context.Sender.ClientId} attempted to write server-only field {typeKey}.{fieldName}.");
                return;
            }

            if (!CarrierOwnedBy(context.Carrier, context.Sender.ClientId))
            {
                MelonLogger.Warning($"[FunSerializable] Rejected owner write for {typeKey}.{fieldName}: ClientId={context.Sender.ClientId} does not own ObjectId={context.Carrier.ObjectId}.");
                return;
            }

            if (!TryResolveInstanceOnCarrier(definition, context.Carrier, out object instance, out Component component))
                return;

            FunSerializableInstanceState state = EnsureInstanceState(definition, instance, component, context.Carrier, Time.unscaledTime);
            object converted = ConvertValue(incomingValue, field.ValueType);
            object oldValue = GetFieldValue(instance, field);

            if (!ValuesEqual(oldValue, converted))
            {
                SetFieldValue(instance, field, converted);
                InvokeOnChange(instance, field, oldValue, converted);
            }

            FunSerializableFieldState fieldState = state.Fields[field.Name];
            fieldState.AuthoritativeValue = converted;
            fieldState.LastObservedValue = converted;
            fieldState.NextSendTime = Time.unscaledTime + field.Attribute.SendRate;
            fieldState.HasAuthoritativeValue = true;

            // Broadcast even when the server already had the same value: this is the authoritative echo
            // which confirms an owner-side write and refreshes the owner's cached value.
            SendDelta(context.Carrier, definition, field, converted);
        }

        private static void OnDeltaReceived(FunRPCContext context, FunRPCArguments args)
        {
            if (context == null || context.Carrier == null || args.Count != 3)
                return;

            string typeKey = args.Get<string>(0);
            string fieldName = args.Get<string>(1);
            object value = args[2];
            ApplyIncomingValue(context.Carrier, typeKey, fieldName, value, false);
        }

        private static void OnSnapshotRequestReceived(FunRPCContext context, FunRPCArguments args)
        {
            if (context == null || context.Carrier == null || context.Sender == null || args.Count != 1)
                return;

            string typeKey = args.Get<string>(0);
            if (!_definitions.TryGetValue(typeKey, out FunSerializableTypeDefinition definition))
                return;

            if (!TryResolveInstanceOnCarrier(definition, context.Carrier, out object instance, out Component component))
                return;

            EnsureInstanceState(definition, instance, component, context.Carrier, Time.unscaledTime);

            foreach (FunSerializableFieldDefinition field in definition.Fields.Values)
            {
                object value = GetFieldValue(instance, field);
                FunRPCSendOptions options = new FunRPCSendOptions { Channel = Channel.Reliable, ExcludeServer = true, ValidateTarget = true };
                FunRPCRuntime.SendTargetFrom(context.Carrier, context.Sender, SnapshotValueRpc, options, definition.TypeKey, field.Name, value);
            }
        }

        private static void OnSnapshotValueReceived(FunRPCContext context, FunRPCArguments args)
        {
            if (context == null || context.Carrier == null || args.Count != 3)
                return;

            string typeKey = args.Get<string>(0);
            string fieldName = args.Get<string>(1);
            object value = args[2];
            ApplyIncomingValue(context.Carrier, typeKey, fieldName, value, true);
        }

        private static void ApplyIncomingValue(NetworkBehaviour carrier, string typeKey, string fieldName, object incomingValue, bool snapshot)
        {
            if (!TryGetDefinitionAndField(typeKey, fieldName, out FunSerializableTypeDefinition definition, out FunSerializableFieldDefinition field))
                return;

            if (!TryResolveInstanceOnCarrier(definition, carrier, out object instance, out Component component))
                return;

            FunSerializableInstanceState state = EnsureInstanceState(definition, instance, component, carrier, Time.unscaledTime);
            object converted = ConvertValue(incomingValue, field.ValueType);
            object oldValue = GetFieldValue(instance, field);

            if (!ValuesEqual(oldValue, converted))
            {
                SetFieldValue(instance, field, converted);
                InvokeOnChange(instance, field, oldValue, converted);
            }

            FunSerializableFieldState fieldState = state.Fields[field.Name];
            fieldState.AuthoritativeValue = converted;
            fieldState.LastObservedValue = converted;
            fieldState.WarnedUnauthorizedWrite = false;
            fieldState.HasAuthoritativeValue = true;

            if (snapshot)
                state.SnapshotReceived = true;
        }

        private static bool TryGetDefinitionAndField(string typeKey, string fieldName, out FunSerializableTypeDefinition definition, out FunSerializableFieldDefinition field)
        {
            field = null;
            if (string.IsNullOrEmpty(typeKey) || string.IsNullOrEmpty(fieldName) || !_definitions.TryGetValue(typeKey, out definition))
            {
                MelonLogger.Warning($"[FunSerializable] Unknown synchronized component '{typeKey}'.");
                definition = null;
                return false;
            }

            if (!definition.Fields.TryGetValue(fieldName, out field))
            {
                MelonLogger.Warning($"[FunSerializable] Unknown synchronized field '{typeKey}.{fieldName}'.");
                return false;
            }

            return true;
        }

        private static bool TryResolveInstanceOnCarrier(FunSerializableTypeDefinition definition, NetworkBehaviour carrier, out object instance, out Component component)
        {
            instance = null;
            component = null;

            try
            {
                component = carrier.gameObject.GetComponent(definition.Il2CppType);
                if (component == null)
                {
                    MelonLogger.Warning($"[FunSerializable] Could not find {definition.ComponentType.FullName} on ObjectId={carrier.ObjectId}.");
                    return false;
                }

                instance = ResolveManagedInstance(definition.ComponentType, component);
                if (instance == null)
                {
                    MelonLogger.Warning($"[FunSerializable] Could not resolve managed instance of {definition.ComponentType.FullName} on ObjectId={carrier.ObjectId}.");
                    return false;
                }

                return true;
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[FunSerializable] Component resolution failed for {definition.ComponentType.FullName}: {e.Message}");
                return false;
            }
        }

        private static object ResolveManagedInstance(Type expectedType, Component component)
        {
            if (component == null)
                return null;

            if (expectedType.IsInstanceOfType(component))
                return component;

            if (component is Il2CppObjectBase il2cppObject)
            {
                if (_getMonoObjectFromIl2CppPointer != null)
                {
                    try
                    {
                        object managed = _getMonoObjectFromIl2CppPointer.Invoke(null, new object[] { il2cppObject.Pointer });
                        if (managed != null && expectedType.IsInstanceOfType(managed))
                            return managed;
                    }
                    catch { }
                }

                try
                {
                    ConstructorInfo pointerCtor = expectedType.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(IntPtr) }, null);
                    if (pointerCtor != null)
                        return pointerCtor.Invoke(new object[] { il2cppObject.Pointer });
                }
                catch { }
            }

            return null;
        }

        private static object GetFieldValue(object instance, FunSerializableFieldDefinition definition)
        {
            object raw = definition.Field.GetValue(instance);
            if (!definition.WrappedField)
                return raw;

            if (raw == null)
                return null;

            PropertyInfo valueProperty = raw.GetType().GetProperty("Value", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return valueProperty == null ? null : valueProperty.GetValue(raw);
        }

        private static void SetFieldValue(object instance, FunSerializableFieldDefinition definition, object value)
        {
            object converted = ConvertValue(value, definition.ValueType);
            if (!definition.WrappedField)
            {
                definition.Field.SetValue(instance, converted);
                return;
            }

            object raw = definition.Field.GetValue(instance);
            if (raw == null)
                throw new InvalidOperationException($"Wrapped IL2CPP field {definition.Field.DeclaringType.FullName}.{definition.Field.Name} is null.");

            PropertyInfo valueProperty = raw.GetType().GetProperty("Value", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (valueProperty == null || !valueProperty.CanWrite)
                throw new InvalidOperationException($"Wrapped IL2CPP field {definition.Field.DeclaringType.FullName}.{definition.Field.Name} has no writable Value property.");

            valueProperty.SetValue(raw, converted);
        }

        private static object ConvertValue(object value, Type targetType)
        {
            if (value == null)
            {
                if (!targetType.IsValueType || Nullable.GetUnderlyingType(targetType) != null)
                    return null;
                throw new InvalidCastException($"Cannot assign null to {targetType.FullName}.");
            }

            if (targetType.IsInstanceOfType(value))
                return value;

            Type nullable = Nullable.GetUnderlyingType(targetType);
            if (nullable != null)
                targetType = nullable;

            if (targetType.IsEnum)
                return Enum.ToObject(targetType, Convert.ToInt64(value, CultureInfo.InvariantCulture));

            if (value is IConvertible && typeof(IConvertible).IsAssignableFrom(targetType))
                return Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);

            throw new InvalidCastException($"Cannot convert synchronized value {value.GetType().FullName} to {targetType.FullName}.");
        }

        private static bool ValuesEqual(object a, object b)
        {
            if (ReferenceEquals(a, b))
                return true;
            if (a == null || b == null)
                return false;
            return a.Equals(b);
        }

        private static void InvokeOnChange(object instance, FunSerializableFieldDefinition field, object oldValue, object newValue)
        {
            MethodInfo method = field.OnChangeMethod;
            if (method == null)
                return;

            try
            {
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length == 0)
                    method.Invoke(instance, null);
                else if (parameters.Length == 1)
                    method.Invoke(instance, new[] { ConvertValue(newValue, parameters[0].ParameterType) });
                else
                    method.Invoke(instance, new[] { ConvertValue(oldValue, parameters[0].ParameterType), ConvertValue(newValue, parameters[1].ParameterType) });
            }
            catch (TargetInvocationException e)
            {
                MelonLogger.Error($"[FunSerializable] OnChange {method.DeclaringType.FullName}.{method.Name} threw:\n{e.InnerException ?? e}");
            }
            catch (Exception e)
            {
                MelonLogger.Error($"[FunSerializable] Could not invoke OnChange {method.DeclaringType.FullName}.{method.Name}:\n{e}");
            }
        }

        private static bool CarrierOwnedBy(NetworkBehaviour carrier, int clientId)
        {
            if (carrier == null)
                return false;

            try
            {
                PropertyInfo ownerIdProperty = AccessTools.Property(typeof(NetworkBehaviour), "OwnerId");
                if (ownerIdProperty != null)
                    return Convert.ToInt32(ownerIdProperty.GetValue(carrier), CultureInfo.InvariantCulture) == clientId;
            }
            catch { }

            return false;
        }

        private static long GetPointer(Component component)
        {
            if (component is Il2CppObjectBase il2cppObject)
                return il2cppObject.Pointer.ToInt64();
            return component.GetInstanceID();
        }

        private static string BuildTypeKey(Type type)
        {
            return type.Assembly.GetName().Name + "|" + type.FullName;
        }

        private static int CountFields()
        {
            int count = 0;
            foreach (FunSerializableTypeDefinition definition in _definitions.Values)
                count += definition.Fields.Count;
            return count;
        }
    }
}
