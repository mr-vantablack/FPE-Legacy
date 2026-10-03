using FPE_Legacy.Rpc;
using HarmonyLib;
using Il2Cpp;
using Il2CppFishNet.Connection;
using Il2CppFishNet.Object;
using Il2CppFishNet.Serializing;
using Il2CppFishNet.Transporting;
using MelonLoader;

namespace FPE_Legacy.Patches
{
    [HarmonyPatch(typeof(PlayerDamage), "Awake")]
    public static class PlayerDamageAwake
    {
        [HarmonyPostfix]
        private static void Postfix(PlayerDamage __instance)
        {
            if (__instance.gameObject.GetComponent<Test>() == null)
                __instance.gameObject.AddComponent<Test>();
        }
    }

    [HarmonyPatch]
    public static class ReadServerRpcPatch
    {
        private static System.Reflection.MethodBase TargetMethod()
        {
            System.Reflection.MethodInfo method = AccessTools.Method(typeof(NetworkBehaviour), "ReadServerRpc", new System.Type[] { typeof(int), typeof(bool), typeof(uint), typeof(PooledReader), typeof(NetworkConnection), typeof(Channel) });

            if (method == null)
                MelonLogger.Error("[FunRPC] FishNet ReadServerRpc not found!");
            else
                MelonLogger.Msg("[FunRPC] Patched FishNet ReadServerRpc: " + method);

            return method;
        }

        private static bool Prefix(NetworkBehaviour __instance, int readerPositionAfterDebug, bool fromRpcLink, uint hash, PooledReader reader, NetworkConnection sendingClient, Channel channel)
        {
            bool consumed = FunRPCRuntime.TryConsumeIncoming(FunRPCKind.Server, __instance, fromRpcLink, hash, reader, sendingClient, channel);

            return !consumed;
        }
    }

    [HarmonyPatch]
    public static class ReadObserversRpcPatch
    {
        private static System.Reflection.MethodBase TargetMethod()
        {
            System.Reflection.MethodInfo method = AccessTools.Method(typeof(NetworkBehaviour), "ReadObserversRpc", new System.Type[] { typeof(int), typeof(bool), typeof(uint), typeof(PooledReader), typeof(Channel) });

            if (method == null)
                MelonLogger.Error("[FunRPC] FishNet ReadObserversRpc not found!");
            else
                MelonLogger.Msg("[FunRPC] Patched FishNet ReadObserversRpc: " + method);

            return method;
        }

        private static bool Prefix(NetworkBehaviour __instance, int readerPositionAfterDebug, bool fromRpcLink, uint hash, PooledReader reader, Channel channel)
        {
            bool consumed = FunRPCRuntime.TryConsumeIncoming(FunRPCKind.Observers, __instance, fromRpcLink, hash, reader, null, channel);

            return !consumed;
        }
    }

    [HarmonyPatch]
    public static class ReadTargetRpcPatch
    {
        private static System.Reflection.MethodBase TargetMethod()
        {
            System.Reflection.MethodInfo method = AccessTools.Method(typeof(NetworkBehaviour), "ReadTargetRpc", new System.Type[] { typeof(int), typeof(bool), typeof(uint), typeof(PooledReader), typeof(Channel) });

            if (method == null)
                MelonLogger.Error("[FunRPC] FishNet ReadTargetRpc not found!");
            else
                MelonLogger.Msg("[FunRPC] Patched FishNet ReadTargetRpc: " + method);

            return method;
        }

        private static bool Prefix(NetworkBehaviour __instance, int readerPositionAfterDebug, bool fromRpcLink, uint hash, PooledReader reader, Channel channel)
        {
            bool consumed = FunRPCRuntime.TryConsumeIncoming(FunRPCKind.Target, __instance, fromRpcLink, hash, reader, null, channel);

            return !consumed;
        }
    }
}
