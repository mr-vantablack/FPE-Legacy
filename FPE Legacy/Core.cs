using FPE_Legacy.Rpc;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(FPE_Legacy.Core), "FPE Legacy", "0.0.1", "mr_vantablack", null)]
[assembly: MelonGame("ZeoWorks", "Slendytubbies 3")]

namespace FPE_Legacy
{
    public class Core : MelonMod
    {
        public override void OnInitializeMelon()
        {
            LoggerInstance.Msg("Initialized.");

            FunRPCRuntime.Initialize();
            FunRPCAttributeRuntime.Initialize(typeof(Core).Assembly);
            FunSerializableRuntime.Initialize(typeof(Core).Assembly);

            //GameObject go = new GameObject("TestRpcController");
            //UnityEngine.Object.DontDestroyOnLoad(go);
           // go.AddComponent<Test>();

            LoggerInstance.Msg("[FunRPC] Numpad6 = client -> server");
            LoggerInstance.Msg("[FunRPC] Numpad7 = host -> observers");
            LoggerInstance.Msg("[FunRPC] Numpad8 = NetworkBehaviours + connections");
            LoggerInstance.Msg("[FunRPC] Numpad9 = host -> one client (TargetRpc)");
            LoggerInstance.Msg("[FunSerializable] Numpad1 = server changes SyncedHealth");
            LoggerInstance.Msg("[FunSerializable] Numpad2 = owner changes SelectedNumber");
        }

        public override void OnUpdate()
        {
            FunSerializableRuntime.Tick();
        }
    }
}
