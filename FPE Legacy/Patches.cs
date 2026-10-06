using FPE_Legacy.Rpc;
using FPE_Legacy.Sandbox;
using HarmonyLib;
using Il2Cpp;
using Il2CppFishNet.Connection;
using Il2CppFishNet.Object;
using Il2CppFishNet.Serializing;
using Il2CppFishNet.Transporting;
using Il2CppInterop.Runtime;
using MelonLoader;
using UnityEngine;
using UnityEngine.UI;

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
    [HarmonyPatch(typeof(Volume), "Awake")]
    public static class SandboxConsolePatch
    {
        // Fill an already deserialized list before native Awake builds UI.
        [HarmonyPrefix]
        private static void Prefix(Volume __instance)
        {
            FunSandboxConsole.Attach(__instance);
        }

        // Also handles games where Awake creates/replaces the categories list itself.
        [HarmonyPostfix]
        private static void Postfix(Volume __instance)
        {
            FunSandboxConsole.Attach(__instance);
            FunSandboxConsole.RefreshScroll(__instance);
        }
    }

    [HarmonyPatch(typeof(Volume), "SendOption")]
    public static class VolumeSendOption
    {
        [HarmonyPrefix]
        private static bool Prefix(Volume __instance, ref int theCatagory, ref int theOption)
        {
            // true: vanilla option, let the game handle it.
            // false: our option was consumed, never forward an FPE resource ID to the game.
            return !FunSandboxConsole.TryHandleOption(__instance, theCatagory, theOption);
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
