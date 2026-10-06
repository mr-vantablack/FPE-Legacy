using System;
using System.IO;
using System.Threading.Tasks;
using Il2CppInterop.Runtime;
using MelonLoader;
using UnityEngine;

namespace FPE_Legacy.Content
{
    internal sealed class FunBundleBackend
    {
        private Il2CppAssetBundle _bundle;
        private readonly string _file;

        private FunBundleBackend(Il2CppAssetBundle bundle, string file)
        {
            _bundle = bundle;
            _file = file;
        }

        internal static Task<FunBundleBackend> Open(string path)
        {
            FunMainThread.Require();
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Bundle path is empty.", nameof(path));

            string file = Path.GetFullPath(path);
            var info = new FileInfo(file);
            if (!info.Exists)
                throw new FileNotFoundException("AssetBundle file does not exist.", file);
            if (info.Length == 0)
                throw new InvalidDataException("AssetBundle file is empty: " + file);

            MelonLogger.Msg($"[FunContent] Opening '{file}' ({info.Length} bytes). " +
                $"Unity={Application.unityVersion}, platform={Application.platform}, process={IntPtr.Size * 8}-bit.");

            Il2CppAssetBundle bundle;
            try
            {
                bundle = Il2CppAssetBundleManager.LoadFromFile(file);
            }
            catch (Exception e)
            {
                throw new InvalidOperationException(
                    "Il2CppAssetBundleManager.LoadFromFile failed: " + file +
                    ". Use the utility DLL shipped with the installed MelonLoader; see the inner exception.", e);
            }

            if (bundle == null)
                throw new InvalidOperationException(
                    "Il2CppAssetBundleManager.LoadFromFile returned null: " + file +
                    ". Unity rejected the bundle. Check the Unity log immediately before this error: " +
                    "possible causes include incompatible Unity version/build target, a damaged bundle, " +
                    "or another loaded copy of the same bundle. Game Unity=" + Application.unityVersion +
                    ", platform=" + Application.platform + ".");

            return Task.FromResult(new FunBundleBackend(bundle, file));
        }

        internal string[] Names()
        {
            FunMainThread.Require();
            var bundle = RequireBundle();
            if (bundle.isStreamedSceneAssetBundle)
                throw new NotSupportedException("Scene bundles cannot be used as prefab packages: " + _file);

            var names = bundle.GetAllAssetNames();
            if (names == null)
                throw new InvalidOperationException("GetAllAssetNames returned null: " + _file);

            var result = new string[names.Length];
            for (int i = 0; i < result.Length; i++)
                result[i] = names[i];
            return result;
        }

        internal Task<UnityEngine.Object> Load(string path, Type type)
        {
            FunMainThread.Require();
            var bundle = RequireBundle();
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("Asset path is empty.", nameof(path));
            if (type == null || !typeof(UnityEngine.Object).IsAssignableFrom(type))
                throw new ArgumentException("Asset type must inherit UnityEngine.Object.", nameof(type));
            if (!bundle.Contains(path))
                throw new InvalidOperationException(
                    $"Asset '{path}' is missing from '{_file}'. Check the path in fpe_manifest.json.");

 
            var asset = bundle.LoadAsset(path, Il2CppType.From(type));
            if (asset == null)
                throw new InvalidOperationException(
                    $"Cannot load asset '{path}' as {type.FullName} from '{_file}'. " +
                    "Check its type, bundle dependencies and the Unity log.");

            asset.hideFlags |= HideFlags.DontUnloadUnusedAsset;
            return Task.FromResult(asset);
        }

        internal void Unload(bool destroyAssets)
        {
            FunMainThread.Require();
            if (_bundle == null)
                return;
            _bundle.Unload(destroyAssets);
            _bundle = null;
        }

        private Il2CppAssetBundle RequireBundle() => _bundle ??
            throw new ObjectDisposedException(nameof(FunBundleBackend), "Bundle was unloaded: " + _file);
    }
}
