using FPE_Legacy.Content;
using FPE_Legacy.Networking;
using FPE_Legacy.Rpc;
using MelonLoader;
using UnityEngine;

[assembly: MelonInfo(typeof(FPE_Legacy.Core), "FPE Legacy", "1.0.0", "mr_vantablack", null)]
[assembly: MelonGame("ZeoWorks", "Slendytubbies 3")]
namespace FPE_Legacy
{
    public class Core : MelonMod
    {
        private bool _scanStarted;
        public override void OnInitializeMelon()
        {
            FunRPCRuntime.Initialize();
            FunRPCAttributeRuntime.Initialize(typeof(Core).Assembly);
            FunSerializableRuntime.Initialize(typeof(Core).Assembly);
            FunContent.Initialize();
            FunNetwork.Initialize();
            FunNetwork.ApproveSpawnRequest = request => true;
            FunComponents.Register<Examples.FunBreakable>("fpe.breakable");
            FunComponents.Register<Examples.FunSpinner>("fpe.spinner");
            FunComponents.Register<Examples.FunRandomColor>("fpe.randomcolor");
            FunContent.RegisterFactory("fpe:cube", "1", () =>
            {
                var go = UnityEngine.GameObject.CreatePrimitive(UnityEngine.PrimitiveType.Cube);
                go.SetActive(false);
                go.name = "FunCube";

                var spinner = go.AddComponent<Examples.FunSpinner>();
                spinner.DegreesPerSecond = new Vector3(0f, 90f, 0f);
                return go;
            });
            LoggerInstance.Msg("[FPE] Content root: " + FunContent.RootDirectory);
        }
        public override void OnUpdate()
        {
            FunContent.Tick();
            if (!_scanStarted) { _scanStarted = true; FunNetwork.Observe(FunContent.ScanAsync()); }
            FunNetwork.Tick();
            FunSerializableRuntime.Tick();
        }
    }
}
