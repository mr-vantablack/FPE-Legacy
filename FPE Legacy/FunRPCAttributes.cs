using HarmonyLib;
using Il2CppFishNet.Connection;
using Il2CppFishNet.Object;
using Il2CppFishNet.Transporting;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using MelonLoader;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace FPE_Legacy.Rpc
{
    /// <summary>
    /// Base attribute for FPE runtime RPC methods
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
    public abstract class FunRPCAttribute : Attribute
    {
        /// <summary>
        /// If true, the method body is also executed immediately on the sending side
        /// Default is false
        /// </summary>
        public bool RunLocally { get; set; }

        /// <summary>
        /// Use FishNet unreliable channel instead of reliable
        /// </summary>
        public bool Unreliable { get; set; }

        internal abstract FunRPCKind Kind { get; }
    }

    /// <summary>
    /// Client -> server RPC
    /// Example:
    /// [FunServerRPC(RequireOwnership = false)]
    /// private void Damage(int amount, Vector3 point) { blabla }
    /// </summary>
    public sealed class FunServerRPCAttribute : FunRPCAttribute
    {
        /// <summary>
        /// When true, the selected FishNet carrier must be owned by this client
        /// FishNet normal ServerRpc defaults to ownership required, so this also defaults to true
        /// </summary>
        public bool RequireOwnership { get; set; } = true;

        internal override FunRPCKind Kind => FunRPCKind.Server;
    }

    /// <summary>
    /// Server -> observers RPC
    /// </summary>
    public sealed class FunObserversRPCAttribute : FunRPCAttribute
    {
        public bool BufferLast { get; set; }
        public bool ExcludeServer { get; set; }
        public bool ExcludeOwner { get; set; }

        internal override FunRPCKind Kind => FunRPCKind.Observers;
    }

    /// <summary>
    /// Server -> remote client RPC
    /// The FIRST method argument must be int clientId
    /// </summary>
    public sealed class FunTargetRPCAttribute : FunRPCAttribute
    {
        public bool ExcludeServer { get; set; }
        public bool ValidateTarget { get; set; } = true;

        internal override FunRPCKind Kind => FunRPCKind.Target;
    }

    internal sealed class FunRPCDefinition
    {
        public MethodInfo Method;
        public FunRPCAttribute Attribute;
        public string RpcName;
        public ParameterInfo[] Parameters;

        public FunRPCKind Kind => Attribute.Kind;
    }

    /// <summary>
    /// Runtime replacement for the part FishNet Weaver normally generates from RPC attributes
    ///
    /// At startup it scans mod assemblies, finds methods marked with FunRPC attributes,
    /// and registers a receiving handler with FunRPCRuntime
    /// </summary>
    public static class FunRPCAttributeRuntime
    {
        private const string HarmonyId = "FPE_Legacy.FunRPC.Attributes";

        private static readonly HarmonyLib.Harmony _harmony = new HarmonyLib.Harmony(HarmonyId);
        private static readonly Dictionary<MethodBase, FunRPCDefinition> _byMethod =
            new Dictionary<MethodBase, FunRPCDefinition>();
        private static readonly Dictionary<string, FunRPCDefinition> _byRpcName =
            new Dictionary<string, FunRPCDefinition>(StringComparer.Ordinal);
        private static readonly HashSet<Assembly> _scannedAssemblies = new HashSet<Assembly>();

        [ThreadStatic]
        private static MethodBase _remoteMethod;

        [ThreadStatic]
        private static FunRPCContext _currentContext;

        private static readonly MethodInfo _outgoingPrefix = AccessTools.Method(typeof(FunRPCAttributeRuntime), nameof(OutgoingPrefix));

        private static bool _initialized;

        /// <summary>
        /// Context of the RPC currently being executed remotely
        /// For ServerRpc, CurrentContext.SenderClientId contains the sender
        /// Null when the method is not currently executing as a received RPC
        /// </summary>
        public static FunRPCContext CurrentContext => _currentContext;

        public static void Initialize(params Assembly[] assemblies)
        {
            if (!_initialized)
            {
                _initialized = true;
                AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
            }

            if (assemblies != null)
            {
                foreach (Assembly assembly in assemblies)
                    RegisterAssembly(assembly);
            }

            MelonLogger.Msg($"[FunRPC] Attribute runtime ready. Methods={_byMethod.Count}");
        }

        private static void OnAssemblyLoad(object sender, AssemblyLoadEventArgs e)
        {
            try
            {
                RegisterAssembly(e.LoadedAssembly);
            }
            catch (Exception ex)
            {
                MelonLogger.Warning($"[FunRPC] Could not scan newly loaded assembly {e.LoadedAssembly?.GetName().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// Scan an mod assembly for [FunServerRPC], [FunObserversRPC] and [FunTargetRPC]
        /// Call this manually if RPC methods live in another plugin/mod assembly loaded before this runtime
        /// </summary>
        public static void RegisterAssembly(Assembly assembly)
        {
            if (assembly == null || _scannedAssemblies.Contains(assembly))
                return;

            _scannedAssemblies.Add(assembly);

            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                types = e.Types;
            }
            catch
            {
                return;
            }

            if (types == null)
                return;

            foreach (Type type in types)
            {
                if (type == null)
                    continue;

                MethodInfo[] methods;
                try
                {
                    methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                }
                catch
                {
                    continue;
                }

                foreach (MethodInfo method in methods)
                {
                    FunRPCAttribute attribute = null;
                    try
                    {
                        attribute = method.GetCustomAttribute<FunRPCAttribute>(true);
                    }
                    catch
                    {
                    }

                    if (attribute == null)
                        continue;

                    RegisterMethod(method, attribute);
                }
            }
        }

        private static void RegisterMethod(MethodInfo method, FunRPCAttribute attribute)
        {
            if (_byMethod.ContainsKey(method))
                return;

            ValidateMethod(method, attribute);

            FunRPCDefinition definition = new FunRPCDefinition
            {
                Method = method,
                Attribute = attribute,
                RpcName = BuildRpcName(method),
                Parameters = method.GetParameters()
            };

            if (_byRpcName.TryGetValue(definition.RpcName, out FunRPCDefinition collision))
            {
                throw new InvalidOperationException($"RPC name collision between {FormatMethod(collision.Method)} and {FormatMethod(method)}.");
            }

            _byMethod.Add(method, definition);
            _byRpcName.Add(definition.RpcName, definition);

            HarmonyMethod prefix = new HarmonyMethod(_outgoingPrefix);
            _harmony.Patch(method, prefix: prefix);

            switch (definition.Kind)
            {
                case FunRPCKind.Server:
                    FunRPCRuntime.RegisterServer(definition.RpcName, (ctx, args) => InvokeRemote(definition, ctx, args));
                    break;

                case FunRPCKind.Observers:
                    FunRPCRuntime.RegisterObservers(definition.RpcName, (ctx, args) => InvokeRemote(definition, ctx, args));
                    break;

                case FunRPCKind.Target:
                    FunRPCRuntime.RegisterTarget(definition.RpcName, (ctx, args) => InvokeRemote(definition, ctx, args));
                    break;
            }

            MelonLogger.Msg($"[FunRPC] Attribute {definition.Kind} registered: {FormatMethod(method)} -> '{definition.RpcName}'");
        }

        private static void ValidateMethod(MethodInfo method, FunRPCAttribute attribute)
        {
            if (method.IsAbstract)
                throw new InvalidOperationException($"RPC method {FormatMethod(method)} cannot be abstract.");

            if (method.IsGenericMethodDefinition || method.ContainsGenericParameters)
                throw new InvalidOperationException($"RPC method {FormatMethod(method)} cannot be generic.");

            if (method.ReturnType != typeof(void))
                throw new InvalidOperationException($"RPC method {FormatMethod(method)} must return void.");

            ParameterInfo[] parameters = method.GetParameters();
            foreach (ParameterInfo parameter in parameters)
            {
                if (parameter.ParameterType.IsByRef || parameter.IsOut)
                {
                    throw new InvalidOperationException($"RPC method {FormatMethod(method)} cannot use ref/out parameter '{parameter.Name}'.");
                }
            }

            if (attribute is FunTargetRPCAttribute)
            {
                if (parameters.Length == 0 || parameters[0].ParameterType != typeof(int))
                {
                    throw new InvalidOperationException($"Target RPC {FormatMethod(method)} must have int clientId as its first parameter. " + "Example: [FunTargetRPC] void ShowFor(int clientId, string text).");
                }
            }
        }

        /// <summary>
        /// Harmony prefix installed on every attributed RPC method
        /// Local call -> serialize and send, received call -> permit original body to run
        /// </summary>
        public static bool OutgoingPrefix(object __instance, MethodBase __originalMethod, object[] __args)
        {
            // We are intentionally invoking this exact method as the result of a received RPC.
            if (_remoteMethod == __originalMethod)
                return true;

            if (!_byMethod.TryGetValue(__originalMethod, out FunRPCDefinition definition))
                return true;

            try
            {
                return SendInvocation(definition, __instance, __args ?? Array.Empty<object>());
            }
            catch (Exception e)
            {
                MelonLogger.Error($"[FunRPC] Failed to invoke attributed RPC {FormatMethod(definition.Method)}:\n{e}");
                return definition.Attribute.RunLocally;
            }
        }

        private static bool SendInvocation(FunRPCDefinition definition, object instance, object[] args)
        {
            FunRPCSendOptions options = BuildOptions(definition.Attribute);
            if (FPE_Legacy.Networking.FunNetwork.TryAddress(instance, out var identity, out var componentId))
            {
                if (definition.Attribute is FunServerRPCAttribute server && server.RequireOwnership && !identity.IsOwner && !FPE_Legacy.Networking.FunNetwork.IsServer)
                    return false;
                options.SuppressLog = true;
                FPE_Legacy.Networking.FunNetwork.SendObjectRpc(identity, componentId, definition.RpcName, definition.Kind, options, args);
                // Network dispatch executes the authoritative/local recipient when appropriate.
                bool dispatchedHere = FPE_Legacy.Networking.FunNetwork.IsServer &&
                    (definition.Kind == FunRPCKind.Server || (!options.ExcludeServer && (!options.ExcludeOwner || !identity.IsOwner) && (definition.Kind == FunRPCKind.Observers ||
                     (definition.Kind == FunRPCKind.Target && args.Length > 0 && Convert.ToInt32(args[0]) == FPE_Legacy.Networking.FunTransport.LocalClientId))));
                return definition.Attribute.RunLocally && !dispatchedHere;
            }
            if (instance is Component unbound && FPE_Legacy.Networking.FunNetwork.GetIdentity(unbound) != null)
                throw new InvalidOperationException("Declare RPC component in the prefab manifest so it has a stable component ID.");
            bool sent = false;

            switch (definition.Kind)
            {
                case FunRPCKind.Server:
                {
                    NetworkBehaviour carrier = FunRPCRuntime.GetCarrierForInstance(instance, FunRPCKind.Server);
                    FunServerRPCAttribute attr = (FunServerRPCAttribute)definition.Attribute;

                    if (attr.RequireOwnership && carrier != null && !carrier.IsOwner)
                    {
                        MelonLogger.Warning($"[FunRPC] ServerRpc {FormatMethod(definition.Method)} was blocked because the selected carrier is not owned by this client.");
                        return attr.RunLocally;
                    }

                    sent = FunRPCRuntime.SendServerFrom(carrier, definition.RpcName, options, args);
                    break;
                }

                case FunRPCKind.Observers:
                {
                    NetworkBehaviour carrier = FunRPCRuntime.GetCarrierForInstance(instance, FunRPCKind.Observers);
                    sent = FunRPCRuntime.SendObserversFrom(carrier, definition.RpcName, options, args);
                    break;
                }

                case FunRPCKind.Target:
                {
                    if (args.Length == 0)
                    {
                        MelonLogger.Error($"[FunRPC] TargetRpc {FormatMethod(definition.Method)} has no target clientId argument.");
                        return definition.Attribute.RunLocally;
                    }

                    int clientId = Convert.ToInt32(args[0]);
                    NetworkConnection target = FunRPCRuntime.GetServerConnection(clientId);
                    if (target == null)
                    {
                        MelonLogger.Error($"[FunRPC] TargetRpc {FormatMethod(definition.Method)} could not find ClientId={clientId}.");
                        return definition.Attribute.RunLocally;
                    }

                    NetworkBehaviour carrier = FunRPCRuntime.GetCarrierForInstance(instance, FunRPCKind.Target, target);
                    sent = FunRPCRuntime.SendTargetFrom(carrier, target, definition.RpcName, options, args);
                    break;
                }
            }

            if (!sent)
            {
                MelonLogger.Warning($"[FunRPC] Attributed {definition.Kind} '{definition.RpcName}' was not sent.");
            }

            // false = skip the method body on the sender
            // true = execute body locally as well
            return definition.Attribute.RunLocally;
        }

        private static FunRPCSendOptions BuildOptions(FunRPCAttribute attribute)
        {
            FunRPCSendOptions options = new FunRPCSendOptions
            {
                Channel = attribute.Unreliable ? Channel.Unreliable : Channel.Reliable
            };

            if (attribute is FunObserversRPCAttribute observers)
            {
                options.BufferLast = observers.BufferLast;
                options.ExcludeServer = observers.ExcludeServer;
                options.ExcludeOwner = observers.ExcludeOwner;
            }
            else if (attribute is FunTargetRPCAttribute target)
            {
                options.ExcludeServer = target.ExcludeServer;
                options.ValidateTarget = target.ValidateTarget;
            }

            return options;
        }

        private static void InvokeRemote(FunRPCDefinition definition, FunRPCContext context, FunRPCArguments incoming)
        {
            if (!ValidateIncomingCall(definition, context))
                return;

            object target = null;
            if (!definition.Method.IsStatic)
            {
                target = ResolveRemoteInstance(definition.Method.DeclaringType, context);
                if (target == null)
                {
                    MelonLogger.Error($"[FunRPC] Received {definition.Kind} {FormatMethod(definition.Method)}, " + $"but no remote component instance of {definition.Method.DeclaringType.FullName} could be found.");
                    return;
                }
            }

            object[] arguments = ConvertArguments(definition, incoming);

            MethodBase previousMethod = _remoteMethod;
            FunRPCContext previousContext = _currentContext;

            try
            {
                _remoteMethod = definition.Method;
                _currentContext = context;
                definition.Method.Invoke(target, arguments);
            }
            catch (TargetInvocationException e)
            {
                Exception real = e.InnerException ?? e;
                MelonLogger.Error($"[FunRPC] Remote RPC body {FormatMethod(definition.Method)} threw:\n{real}");
            }
            catch (Exception e)
            {
                MelonLogger.Error($"[FunRPC] Could not execute remote RPC {FormatMethod(definition.Method)}:\n{e}");
            }
            finally
            {
                _remoteMethod = previousMethod;
                _currentContext = previousContext;
            }
        }

        internal static bool InvokeNetwork(string rpcName, FunRPCKind kind, FPE_Legacy.Networking.FunNetworkIdentity identity,
            string componentId, FunRPCContext context, FunRPCArguments incoming)
        {
            if (!_byRpcName.TryGetValue(rpcName, out var definition) || definition.Kind != kind || definition.Method.IsStatic) return false;
            if (!identity.Content.Instances.TryGetValue(componentId, out object target) || !definition.Method.DeclaringType.IsInstanceOfType(target)) return false;
            if (definition.Attribute is FunServerRPCAttribute server && server.RequireOwnership &&
                (context.Sender == null || context.Sender.ClientId != identity.OwnerClientId))
            {
                // Local server calls are authoritative even for server-owned objects.
                if (!FPE_Legacy.Networking.FunNetwork.IsServer || context.Carrier != null) return false;
            }
            MethodBase oldMethod = _remoteMethod;
            FunRPCContext oldContext = _currentContext;
            try
            {
                _remoteMethod = definition.Method; _currentContext = context;
                context.Kind = kind;
                definition.Method.Invoke(target, ConvertArguments(definition, incoming));
                return true;
            }
            catch (Exception e) { MelonLogger.Error("[FunNetwork] RPC " + rpcName + ": " + (e.InnerException ?? e)); return false; }
            finally { _remoteMethod = oldMethod; _currentContext = oldContext; }
        }

        private static bool ValidateIncomingCall(FunRPCDefinition definition, FunRPCContext context)
        {
            if (!(definition.Attribute is FunServerRPCAttribute server) || !server.RequireOwnership)
                return true;

            if (context == null || context.Sender == null || context.Carrier == null)
            {
                MelonLogger.Warning($"[FunRPC] Rejected ServerRpc {FormatMethod(definition.Method)} because sender/carrier information is missing.");
                return false;
            }

            try
            {
                PropertyInfo ownerIdProperty = AccessTools.Property(typeof(NetworkBehaviour), "OwnerId");
                if (ownerIdProperty != null)
                {
                    int ownerId = Convert.ToInt32(ownerIdProperty.GetValue(context.Carrier));
                    if (ownerId != context.Sender.ClientId)
                    {
                        MelonLogger.Warning($"[FunRPC] Rejected ServerRpc {FormatMethod(definition.Method)} from ClientId={context.Sender.ClientId}: " + $"carrier OwnerId={ownerId}.");
                        return false;
                    }

                    return true;
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[FunRPC] Could not validate ServerRpc ownership through OwnerId: {e.Message}");
            }

            MelonLogger.Warning($"[FunRPC] OwnerId is unavailable; rejected {FormatMethod(definition.Method)}.");
            return false;
        }

        private static object[] ConvertArguments(FunRPCDefinition definition, FunRPCArguments incoming)
        {
            if (incoming.Count != definition.Parameters.Length)
            {
                throw new InvalidOperationException($"RPC {FormatMethod(definition.Method)} expected {definition.Parameters.Length} arguments, " + $"but received {incoming.Count}.");
            }

            object[] result = new object[definition.Parameters.Length];
            for (int i = 0; i < result.Length; i++)
            {
                result[i] = ConvertArgument(incoming[i], definition.Parameters[i].ParameterType, i, definition.Method);
            }

            return result;
        }

        private static object ConvertArgument(object value, Type targetType, int index, MethodInfo method)
        {
            if (value == null)
            {
                if (!targetType.IsValueType || Nullable.GetUnderlyingType(targetType) != null)
                    return null;

                throw new InvalidCastException($"RPC argument {index} for {FormatMethod(method)} is null but {targetType.FullName} is a value type.");
            }

            if (targetType.IsInstanceOfType(value))
                return value;

            Type nullable = Nullable.GetUnderlyingType(targetType);
            if (nullable != null)
                targetType = nullable;

            if (targetType.IsEnum)
                return Enum.ToObject(targetType, Convert.ToInt64(value));

            if (value is IConvertible && typeof(IConvertible).IsAssignableFrom(targetType))
                return Convert.ChangeType(value, targetType);

            throw new InvalidCastException($"RPC argument {index} for {FormatMethod(method)} is {value.GetType().FullName}, " + $"cannot convert to {targetType.FullName}.");
        }

        private static object ResolveRemoteInstance(Type declaringType, FunRPCContext context)
        {
            if (!typeof(Component).IsAssignableFrom(declaringType))
            {
                MelonLogger.Error($"[FunRPC] Instance RPC type {declaringType.FullName} is not a Unity Component. " + "Use a static RPC method or place it on a MonoBehaviour component.");
                return null;
            }

            Il2CppSystem.Type il2cppType;
            try
            {
                il2cppType = Il2CppType.From(declaringType, false);
            }
            catch
            {
                il2cppType = null;
            }

            if (il2cppType == null)
            {
                MelonLogger.Error($"[FunRPC] {declaringType.FullName} is not registered in IL2CPP. " + "Add [RegisterTypeInIl2Cpp] to the component.");
                return null;
            }

            // if the RPC component sits on the same networked GameObject as the
            // selected FishNet carrier, resolve that exact corresponding remote component
            try
            {
                if (context != null && context.Carrier != null && context.Carrier.gameObject != null)
                {
                    Component component = context.Carrier.gameObject.GetComponent(il2cppType);
                    object wrapped = WrapInjectedComponent(declaringType, component);
                    if (wrapped != null)
                        return wrapped;
                }
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[FunRPC] Could not resolve RPC component on carrier GameObject: {e.Message}");
            }

            // fallback for global/singleton components such as a DontDestroyOnLoad shit
            try
            {
                UnityEngine.Object found = UnityEngine.Object.FindObjectOfType(il2cppType);
                object wrapped = WrapInjectedComponent(declaringType, found as Component);
                if (wrapped != null)
                    return wrapped;
            }
            catch (Exception e)
            {
                MelonLogger.Warning($"[FunRPC] Could not resolve global RPC component {declaringType.FullName}: {e.Message}");
            }

            return null;
        }

        private static object WrapInjectedComponent(Type declaringType, Component component)
        {
            if (component == null)
                return null;

            if (declaringType.IsInstanceOfType(component))
                return component;

            if (!(component is Il2CppObjectBase il2cppObject))
                return null;

            ConstructorInfo pointerCtor = declaringType.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(IntPtr) }, null);

            if (pointerCtor == null)
            {
                MelonLogger.Error($"[FunRPC] Injected component {declaringType.FullName} has no IntPtr constructor.");
                return null;
            }

            return pointerCtor.Invoke(new object[] { il2cppObject.Pointer });
        }

        private static string BuildRpcName(MethodInfo method)
        {
            ParameterInfo[] parameters = method.GetParameters();
            string parameterSignature = string.Empty;
            for (int i = 0; i < parameters.Length; i++)
            {
                if (i != 0)
                    parameterSignature += ",";

                parameterSignature += parameters[i].ParameterType.FullName;
            }

            return
                "ATTR|" +
                method.DeclaringType.Assembly.GetName().Name + "|" +
                method.DeclaringType.FullName + "|" +
                method.Name + "|" +
                parameterSignature;
        }

        private static string FormatMethod(MethodInfo method)
        {
            return method.DeclaringType.FullName + "." + method.Name;
        }
    }
}
