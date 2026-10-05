using FPE_Legacy.Networking;
using FPE_Legacy.Rpc;
using Il2Cpp;
using Il2CppFishNet.Connection;
using Il2CppFishNet.Object;
using MelonLoader;
using System;
using UnityEngine;

namespace FPE_Legacy
{
    [RegisterTypeInIl2Cpp]
    public class Test : MonoBehaviour
    {
        [FunSerializable(OnChange = nameof(OnSyncedHealthChanged))]
        public int SyncedHealth = 100;

        [FunSerializable(WritePermission = FunSerializableWritePermission.Owner, OnChange = nameof(OnSelectedNumberChanged))]
        public int SelectedNumber = 0;

        public Test(IntPtr ptr) : base(ptr) { }

        public void Update()
        {
            try
            {
                // CLIENT -> SERVER.
                if (!FunRPCRuntime.IsLocalOwner(this)) return;
                if (Input.GetKeyDown(KeyCode.Keypad6))
                {
                    DemoServerRPC("Hello from CLIENT!", 123, 45.5f, true, new Vector3(1.25f, 2.5f, 3.75f));
                }

                // SERVER -> ALL OBSERVERS.
                if (Input.GetKeyDown(KeyCode.Keypad7))
                {
                    DemoObserversRPC("Hello from HOST!", 777, new Vector2(12f, 34f), Color.cyan);
                }

                if (Input.GetKeyDown(KeyCode.Keypad8))
                {
                    FunRPCRuntime.PrintNetworkBehaviours();
                }

                // SERVER -> REMOTE CLIENT.
                if (Input.GetKeyDown(KeyCode.Keypad1))
                {
                    var camera = Camera.main;
                    var position = camera.transform.position + camera.transform.forward * 3f;
                    FunNetwork.Observe(FunNetwork.SpawnAsync("fpe:cube",position,Quaternion.identity));
                }

                if (Input.GetKeyDown(KeyCode.Keypad2))
                {
                    var camera = Camera.main;
                    var position = camera.transform.position + camera.transform.forward * 3f;
                    FunNetwork.RequestSpawn("balls:blue_ball", position, Quaternion.identity);
                }
                if (Input.GetKeyDown(KeyCode.Keypad3))
                {
                    var camera = Camera.main;
                    var position = camera.transform.position + camera.transform.forward * 3f;
                    FunNetwork.RequestSpawn("balls:red_ball", position, Quaternion.identity);
                }

                if (Input.GetKeyDown(KeyCode.Keypad9))
                {
                    NetworkConnection target = FunRPCRuntime.GetFirstRemoteServerConnection();
                    if (target == null)
                    {
                        MelonLogger.Warning("[FunRPC] No remote client found for TargetRPC.");
                    }
                    else
                    {
                        DemoTargetRPC(target.ClientId, "This message is ONLY for you", Time.time, new Vector3(9f, 8f, 7f));
                    }
                }
            }
            catch (Exception e)
            {
                MelonLogger.Error("[FunRPC] Update exception:\n" + e);
            }
        }

        // CLIENT -> SERVER

        [FunServerRPC]
        private void DemoServerRPC(string text, int number, float value, bool flag, Vector3 position)
        {
            int senderClientId = FunRPCAttributeRuntime.CurrentContext == null
                ? -1
                : FunRPCAttributeRuntime.CurrentContext.SenderClientId;

            MelonLogger.Msg($"[FunRPC] SERVER RPC EXECUTED. " + $"sender={senderClientId}, text='{text}', int={number}, " + $"float={value}, bool={flag}, pos={position}");
        }

        // SERVER -> OBSERVERS

        [FunObserversRPC(ExcludeServer = true)]
        private void DemoObserversRPC(string text, int number, Vector2 position, Color color)
        {
            MelonLogger.Msg($"[FunRPC] OBSERVERS RPC EXECUTED. " + $"text='{text}', int={number}, vec2={position}, color={color}");
        }

        // SERVER -> rEMOTE CLIENT

        [FunTargetRPC]
        private void DemoTargetRPC(int clientId, string text, float serverTime, Vector3 position)
        {
            MelonLogger.Msg($"[FunRPC] TARGET RPC EXECUTED. " + $"targetClientId={clientId}, text='{text}', " + $"serverTime={serverTime}, pos={position}");
        }

        private void OnSyncedHealthChanged(int oldValue, int newValue)
        {
            MelonLogger.Msg($"[FunSerializable] SyncedHealth {oldValue} -> {newValue} on {gameObject.name}");
        }

        private void OnSelectedNumberChanged(int oldValue, int newValue)
        {
            MelonLogger.Msg($"[FunSerializable] SelectedNumber {oldValue} -> {newValue} on {gameObject.name}");
        }
    }
}
