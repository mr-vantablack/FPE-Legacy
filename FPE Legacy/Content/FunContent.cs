using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using UnityEngine;
using MelonLoader;
using MelonLoader.Utils;

namespace FPE_Legacy.Content
{
    public sealed class FunAssetLease<T> : IDisposable where T : UnityEngine.Object
    {
        public T Asset { get; }
        private string _id;
        internal FunAssetLease(string id, T asset) { _id = id; Asset = asset; }
        public void Dispose() { if (_id != null) { FunContent.ReleaseAsset(_id); _id = null; } }
    }
    public sealed class FunContentInstance : IDisposable
    {
        public GameObject GameObject { get; internal set; }
        public string AssetId { get; internal set; }
        public IReadOnlyDictionary<string, object> Components => Instances;
        internal Dictionary<string, object> Instances = new(StringComparer.Ordinal);
        internal bool Disposed;
        internal bool ReadyInvoked;
        internal void NotifyReady()
        {
            if (ReadyInvoked) return; ReadyInvoked = true;
            foreach (var instance in Instances.Values) if (instance is IFunContentReady ready) ready.OnContentReady();
        }
        public void Dispose()
        {
            FunMainThread.Require(); if (Disposed) return; Disposed = true;
            if (GameObject != null) { GameObject.SetActive(false); UnityEngine.Object.Destroy(GameObject); }
            FunContent.ReleaseInstance(this);
        }
    }
    public static partial class FunContent
    {
        private sealed class Package
        {
            internal string Id, File, Hash, Alias;
            internal FunManifest Manifest;
            internal FunBundleBackend Bundle;
            internal Task<FunBundleBackend> Opening;
        }
        private sealed class Asset
        {
            internal Package Package;
            internal FunAssetDefinition Definition;
            internal Func<GameObject> Factory;
            internal string FactoryVersion;
        }
        private static readonly Dictionary<string, Package> Packages = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, Asset> Assets = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, Task<UnityEngine.Object>> Loads = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, UnityEngine.Object> Loaded = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, (string bundle, string path)> Manual = new(StringComparer.Ordinal);
        private static readonly List<FunContentInstance> Live = new();
        private static readonly Dictionary<string, int> ExternalPins = new(StringComparer.Ordinal);
        private static TaskCompletionSource<bool> _ready = new();
        private static bool _initialized;
        private static GameObject _staging;
        public static Task Ready => _ready.Task;
        public static bool IsReady => _ready.Task.IsCompletedSuccessfully;
        public static string RootDirectory { get; private set; }
        public static string Fingerprint { get; private set; }
        public static IReadOnlyCollection<string> AssetIds => Assets.Keys.ToArray();
        public static event Action<string> Diagnostic;
        public static void Initialize(string directory = null)
        {
            if (_initialized) return;
            _initialized = true; FunMainThread.Initialize(); FunComponents.Defaults();
            RootDirectory = Path.GetFullPath(directory ?? Path.Combine(MelonEnvironment.GameRootDirectory, "Mods", "FunPlusEssentials", "Assets"));
            Directory.CreateDirectory(RootDirectory);
        }
        // Call after registering all types and factories. Core indexes at the end of initialization.
        // The false mode omits per-bundle frame yields; it never blocks on a pending Task.
        public static Task ScanAsync() => ScanAsync(yieldBetweenBundles: true);
        public static async Task ScanAsync(bool yieldBetweenBundles)
        {
            FunMainThread.Require();
            if (_scanStarted) { await Ready; return; }
            _scanStarted = true;
            try
            {
                foreach (var file in Directory.EnumerateFiles(RootDirectory, "*", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
                {
                    if (!IsBundle(file)) continue;
                    FunBundleBackend backend = null;
                    try
                    {
                        backend = await FunBundleBackend.Open(file);
                        string[] manifests = backend.Names().Where(n => string.Equals(Path.GetFileName(n), "fpe_manifest.json", StringComparison.OrdinalIgnoreCase)).ToArray();
                        if (manifests.Length > 1) throw new FormatException("More than one fpe_manifest.json.");
                        string alias = Path.ChangeExtension(Path.GetRelativePath(RootDirectory, file), null).Replace('\\', '/');
                        FunManifest manifest = null;
                        if (manifests.Length == 1)
                        {
                            var text = (await backend.Load(manifests[0], typeof(TextAsset))).Cast<TextAsset>();
                            if (text.text.Length > 1024 * 1024) throw new FormatException("Manifest exceeds 1 MiB.");
                            manifest = FunJson.Read<FunManifest>(text.text);
                            ValidateManifest(manifest);
                        }
                        string packageId = manifest?.PackageId ?? "loose/" + alias;
                        if (Packages.ContainsKey(packageId)) throw new FormatException("Duplicate packageId: " + packageId);
                        using var stream = File.OpenRead(file);
                        using var sha = SHA256.Create();
                        string hash = Convert.ToHexString(sha.ComputeHash(stream));
                        var package = new Package { Id = packageId, File = file, Hash = hash, Alias = alias, Manifest = manifest, Bundle = backend };
                        Packages.Add(packageId, package); backend = null;
                        if (manifest != null)
                            foreach (var definition in manifest.Assets) Assets.Add(packageId + ":" + definition.Id, new Asset { Package = package, Definition = definition });
                        Report("Indexed " + packageId + " (" + (manifest?.Assets.Count ?? 0) + " assets).");
                    }
                    catch
                    {
                        TryUnload(backend);
                        throw;
                    }
                    if (yieldBetweenBundles) await FunMainThread.NextFrame();
                }
                foreach (var registration in Manual)
                {
                    var package = ResolveBundle(registration.Value.bundle);
                    Assets.Add(registration.Key, new Asset { Package = package, Definition = new FunAssetDefinition { Id = registration.Key, Path = registration.Value.path } });
                }
                foreach (var package in Packages.Values) CheckDependencies(package, new HashSet<string>(), new HashSet<string>());
                foreach (string id in Assets.Keys) CheckAssetReferences(id, new HashSet<string>(), new HashSet<string>());
                foreach (var asset in Assets.Values)
                {
                    asset.Definition.Network.Validate();
                    foreach (var component in asset.Definition.Components) FunComponents.Get(component.Type);
                }
                Fingerprint = HashText(string.Join("\n", Packages.Values.OrderBy(p => p.Id).Select(p => p.Id + "=" + p.Hash)) + "\n" +
                    string.Join("\n", Assets.OrderBy(p => p.Key).Select(p => p.Key + "=" + FunJson.Write(p.Value.Definition) + ":" + p.Value.FactoryVersion)) + "\n" + FunComponents.Fingerprint() + "\n" + typeof(FunContent).Assembly.ManifestModule.ModuleVersionId);
                // Catalogue is complete. Internal warmup avoids awaiting Ready recursively.
                foreach (var pair in Assets.Where(p => p.Value.Definition.Preload).ToArray()) await LoadInternal(pair.Key, new HashSet<string>());
                _ready.TrySetResult(true); Report("Content ready. Assets=" + Assets.Count + ", fingerprint=" + Fingerprint);
            }
            catch (Exception e)
            {
                // Initialization is all-or-nothing: do not leave bundles loaded after a failure.
                foreach (var package in Packages.Values) TryUnload(package.Bundle);
                Packages.Clear();
                foreach (var id in Assets.Where(p => p.Value.Factory == null).Select(p => p.Key).ToArray())
                    Assets.Remove(id);
                Loads.Clear();
                Loaded.Clear();
                Fingerprint = null;
                _ready.TrySetException(e);
                _ = _ready.Task.Exception; // Retain failure for awaiters without an unobserved duplicate.
                Report("Content initialization FAILED: " + e);
                throw;
            }
        }
        private static void TryUnload(FunBundleBackend backend)
        {
            try { backend?.Unload(true); }
            catch (Exception e) { MelonLogger.Warning("[FunContent] Bundle cleanup failed: " + e); }
        }

        private static bool _scanStarted;
        internal static bool CatalogueLocked => _scanStarted;
        public static void RegisterPrefab(string id, string bundle, string asset)
        {
            FunMainThread.Require(); if (_scanStarted) throw new InvalidOperationException("Register before ScanAsync.");
            ValidateId(id, true); Manual.Add(id, (bundle, asset));
        }
        public static void RegisterFactory(string id, string version, Func<GameObject> factory, FunNetworkOptions options = null)
        {
            FunMainThread.Require(); if (_scanStarted) throw new InvalidOperationException("Register before ScanAsync.");
            ValidateId(id, true); if (factory == null || string.IsNullOrWhiteSpace(version)) throw new ArgumentException("Factory and version are required.");
            Assets.Add(id, new Asset { Factory = factory, FactoryVersion = version, Definition = new FunAssetDefinition { Id = id, Network = options?.Copy() ?? new() } });
        }
        public static async Task PreloadAsync(string id) { FunMainThread.Require(); await Ready; await LoadInternal(id, new HashSet<string>()); }
        public static async Task<T> LoadAssetAsync<T>(string id) where T : UnityEngine.Object
        {
            FunMainThread.Require(); await Ready;
            var value = await LoadInternal(id, new HashSet<string>());
            if (value == null) throw new InvalidOperationException("Factories create instances; use InstantiateAsync/SpawnAsync.");
            var typed = value.Cast<T>(); Pin(id); return typed;
        }
        public static async Task<T> LoadAssetAsync<T>(string bundle, string path) where T : UnityEngine.Object
        {
            FunMainThread.Require(); await Ready;
            var package = ResolveBundle(bundle); await EnsureOpen(package);
            string key = "raw:" + package.Id + ":" + path + ":" + typeof(T).FullName;
            if (Loads.TryGetValue(key, out var cached) && cached.IsCompletedSuccessfully && cached.Result == null)
            {
                Loads.Remove(key);
                Report("Reloading invalid cached asset: " + key);
            }
            if (!Loads.TryGetValue(key, out var task)) { task = package.Bundle.Load(path, typeof(T)); Loads.Add(key, task); }
            var typed = (await task).Cast<T>(); Pin(key); return typed;
        }
        public static async Task<FunAssetLease<T>> AcquireAssetAsync<T>(string id) where T : UnityEngine.Object
            => new FunAssetLease<T>(id, await LoadAssetAsync<T>(id));
        private static void Pin(string id) { ExternalPins.TryGetValue(id, out int count); ExternalPins[id] = count + 1; }
        public static void ReleaseAsset(string id)
        {
            FunMainThread.Require();
            if (!ExternalPins.TryGetValue(id, out int count)) throw new InvalidOperationException("No asset lease for " + id);
            if (count == 1) ExternalPins.Remove(id); else ExternalPins[id] = count - 1;
        }
        public static void ReleaseAsset<T>(string bundle, string path) where T : UnityEngine.Object
            => ReleaseAsset("raw:" + ResolveBundle(bundle).Id + ":" + path + ":" + typeof(T).FullName);
        public static FunNetworkOptions GetNetworkOptions(string id) => GetAsset(id).Definition.Network.Copy();
        private static Asset GetAsset(string id) => Assets.TryGetValue(id, out var asset) ? asset : throw new KeyNotFoundException("Unknown asset: " + id);
        private static async Task<UnityEngine.Object> LoadInternal(string id, HashSet<string> ancestry)
        {
            if (ancestry.Contains(id)) throw new FormatException("Cyclic $asset reference: " + id);
            if (Loads.TryGetValue(id, out var task))
            {
                // Factory entries intentionally complete with null; they have no cached prefab.
                if (!task.IsCompletedSuccessfully || task.Result != null || GetAsset(id).Factory != null)
                    return await task;
                Loads.Remove(id);
                Loaded.Remove(id);
                Report("Reloading invalid cached asset: " + id);
            }
            var source = new TaskCompletionSource<UnityEngine.Object>(); Loads[id] = source.Task;
            var chain = new HashSet<string>(ancestry) { id };
            try
            {
                var entry = GetAsset(id);
                if (entry.Factory != null) { source.TrySetResult(null); return null; }
                await EnsureOpen(entry.Package);
                var asset = await entry.Package.Bundle.Load(entry.Definition.Path, AssetType(entry.Definition.Type));
                foreach (var component in entry.Definition.Components)
                    foreach (var json in component.Fields.Values.Concat(component.Properties.Values))
                        foreach (string reference in AssetReferences(json)) await LoadInternal(reference, chain);
                Loaded[id] = asset; source.TrySetResult(asset); return asset;
            }
            catch (Exception e) { Loads.Remove(id); source.TrySetException(e); _ = source.Task.Exception; throw; }
        }
        internal static object GetLoaded(string id, Type type)
        {
            if (!Loaded.TryGetValue(id, out var value) || value == null) throw new InvalidOperationException("Asset reference not preloaded: " + id);
            // Cast via Il2CppInterop to preserve requested native wrapper type.
            var cast = typeof(Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase).GetMethods().First(m => m.Name == "Cast" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
            if (!typeof(UnityEngine.Object).IsAssignableFrom(type)) throw new FormatException("$asset target must be a Unity object.");
            return cast.MakeGenericMethod(type).Invoke(value, null);
        }
        private static IEnumerable<string> AssetReferences(JsonElement json)
        {
            if (json.ValueKind == JsonValueKind.Object)
                foreach (var p in json.EnumerateObject())
                { if (p.Name == "$asset") yield return p.Value.GetString(); else foreach (var r in AssetReferences(p.Value)) yield return r; }
            if (json.ValueKind == JsonValueKind.Array)
                foreach (var v in json.EnumerateArray()) foreach (var r in AssetReferences(v)) yield return r;
        }
        public static async Task<FunContentInstance> InstantiateAsync(string id, Vector3 position, Quaternion rotation)
        {
            var instance = await PrepareInstance(id, position, rotation);
            try { instance.NotifyReady(); instance.GameObject.SetActive(true); return instance; }
            catch { instance.Dispose(); throw; }
        }
        internal static async Task<FunContentInstance> PrepareInstance(string id, Vector3 position, Quaternion rotation)
        {
            FunMainThread.Require(); await Ready;
            var asset = GetAsset(id); UnityEngine.Object prefab = await LoadInternal(id, new HashSet<string>());
            GameObject go = null;
            try
            {
                if (_staging == null) { _staging = new GameObject("FPE.Content.Staging"); _staging.SetActive(false); UnityEngine.Object.DontDestroyOnLoad(_staging); }
                if (asset.Factory != null)
                {
                    go = asset.Factory() ?? throw new InvalidOperationException("Factory returned null.");
                    go.SetActive(false); // Factory MUST return inactive if it contains scripts which rely on configured fields.
                }
                else
                {
                    if (asset.Definition.Type != "GameObject") throw new InvalidOperationException("Only GameObject assets can be instantiated as prefabs.");
                    if (prefab == null)
                        throw new InvalidOperationException("Prefab is missing or was unloaded before spawn: " + id);
                    var template = prefab.TryCast<GameObject>();
                    if (template == null)
                        throw new InvalidOperationException("Loaded asset is not a live GameObject prefab: " + id);
                    var parent = _staging.transform;
                    if (parent == null)
                        throw new InvalidOperationException("Content staging Transform is unavailable: " + id);
                    try
                    {
                        go = UnityEngine.Object.Instantiate(template, parent, false);
                    }
                    catch (Exception e)
                    {
                        throw new InvalidOperationException(
                            $"Instantiate failed for '{id}', asset='{asset.Definition.Path}', " +
                            $"Unity={Application.unityVersion}. CLR null={ReferenceEquals(template, null)}, " +
                            $"Unity null={template == null}, parent Unity null={parent == null}. " +
                            "If all are false, check the generated Unity/Il2Cpp bindings and the complete inner exception.", e);
                    }
                    if (go == null)
                        throw new InvalidOperationException("Instantiate returned no live GameObject: " + id);
                    // The retention flag belongs to the cached asset, not to scene instances.
                    go.hideFlags &= ~HideFlags.DontUnloadUnusedAsset;
                    go.SetActive(false);
                }
                go.transform.SetParent(null, false);
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(go, UnityEngine.SceneManagement.SceneManager.GetActiveScene());
                go.transform.position = position; go.transform.rotation = rotation;
                var components = FunComponents.Create(go, asset.Definition.Components);
                FunComponents.Configure(go, asset.Definition.Components, components);
                var instance = new FunContentInstance { AssetId = id, GameObject = go, Instances = components };
                Live.Add(instance); return instance;
            }
            catch { if (go != null) UnityEngine.Object.Destroy(go); throw; }
        }
        // Clears all caches as a unit so cross-package $asset references cannot outlive dependencies
        public static void UnloadUnused()
        {
            FunMainThread.Require(); if (!IsReady) throw new InvalidOperationException("Wait for content initialization.");
            Live.RemoveAll(i => i.Disposed || i.GameObject == null);
            if (Live.Count != 0 || ExternalPins.Count != 0 || Loads.Values.Any(t => !t.IsCompleted) || Packages.Values.Any(p => p.Opening != null)) throw new InvalidOperationException("Content is in use; dispose all instances, release externally loaded assets and finish pending loads first.");
            foreach (var package in Packages.Values) { package.Bundle?.Unload(true); package.Bundle = null; }
            Loads.Clear(); Loaded.Clear();
        }
        internal static void ReleaseInstance(FunContentInstance instance) => Live.Remove(instance);
        internal static void Tick() { FunMainThread.Tick(); Live.RemoveAll(i => i.Disposed || i.GameObject == null); }
        private static async Task EnsureOpen(Package package)
        {
            if (package.Manifest != null)
                foreach (var dependency in package.Manifest.Dependencies) await EnsureOpen(Packages[dependency.PackageId]);
            if (package.Bundle == null)
            {
                if (package.Opening == null) package.Opening = FunBundleBackend.Open(package.File);
                var opening = package.Opening;
                try { package.Bundle = await opening; }
                finally { if (package.Opening == opening) package.Opening = null; }
            }
        }
        private static Package ResolveBundle(string id)
        {
            if (Packages.TryGetValue(id, out var package)) return package;
            var matches = Packages.Values.Where(p => string.Equals(p.Alias, id.Replace('\\', '/'), StringComparison.Ordinal)).ToArray();
            return matches.Length == 1 ? matches[0] : throw new KeyNotFoundException("Missing/ambiguous bundle: " + id);
        }
        private static void CheckDependencies(Package package, HashSet<string> visiting, HashSet<string> visited)
        {
            if (visited.Contains(package.Id)) return;
            if (!visiting.Add(package.Id)) throw new FormatException("Cyclic bundle dependencies: " + package.Id);
            if (package.Manifest != null)
                foreach (var d in package.Manifest.Dependencies)
                {
                    if (!Packages.TryGetValue(d.PackageId, out var other)) throw new FormatException("Missing dependency " + d.PackageId + " for " + package.Id);
                    if (d.Version != null && other.Manifest?.Version != d.Version) throw new FormatException("Dependency version mismatch: " + d.PackageId);
                    CheckDependencies(other, visiting, visited);
                }
            visiting.Remove(package.Id); visited.Add(package.Id);
        }
        private static void CheckAssetReferences(string id, HashSet<string> visiting, HashSet<string> visited)
        {
            if (visited.Contains(id)) return;
            if (!visiting.Add(id)) throw new FormatException("Cyclic $asset references: " + id);
            var entry = GetAsset(id);
            foreach (var c in entry.Definition.Components)
                foreach (var value in c.Fields.Values.Concat(c.Properties.Values))
                    foreach (var reference in AssetReferences(value)) CheckAssetReferences(reference, visiting, visited);
            visiting.Remove(id); visited.Add(id);
        }
        private static void ValidateManifest(FunManifest manifest)
        {
            if (manifest.SchemaVersion != 1) throw new FormatException("Unsupported manifest schemaVersion.");
            ValidateId(manifest.PackageId, false);
            if (string.IsNullOrWhiteSpace(manifest.Version) || manifest.Assets == null || manifest.Dependencies == null) throw new FormatException("Invalid manifest.");
            var ids = new HashSet<string>();
            foreach (var asset in manifest.Assets)
            {
                ValidateId(asset.Id, false);
                if (!ids.Add(asset.Id) || string.IsNullOrWhiteSpace(asset.Path) || asset.Network == null || asset.Components == null) throw new FormatException("Invalid/duplicate asset: " + asset.Id);
                AssetType(asset.Type); asset.Network.Validate();
                if (asset.Type != "GameObject" && asset.Components.Count != 0) throw new FormatException("Components require GameObject asset.");
                var componentIds = new HashSet<string>();
                foreach (var c in asset.Components)
                    if (c == null || string.IsNullOrWhiteSpace(c.Id) || !componentIds.Add(c.Id) || c.Fields == null || c.Properties == null) throw new FormatException("Invalid/duplicate component id.");
            }
        }
        private static void ValidateId(string id, bool qualified)
        {
            if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || id.Any(c => !(c <= 127 && (char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.' || (qualified && c == ':'))))) throw new FormatException("Invalid id: " + id);
            if (qualified && (id.Count(c => c == ':') != 1 || id.StartsWith(":") || id.EndsWith(":"))) throw new FormatException("Use package:asset id.");
        }
        private static Type AssetType(string name) => name switch
        {
            "GameObject" => typeof(GameObject), "Texture2D" => typeof(Texture2D), "Material" => typeof(Material),
            "AudioClip" => typeof(AudioClip), "TextAsset" => typeof(TextAsset), "Mesh" => typeof(Mesh),
            "Shader" => typeof(Shader), "Sprite" => typeof(Sprite), "AnimationClip" => typeof(AnimationClip),
            _ => throw new FormatException("Unsupported asset type: " + name)
        };
        private static bool IsBundle(string path)
        {
            using var stream = File.OpenRead(path); byte[] header = new byte[8]; int count = stream.Read(header, 0, header.Length);
            string value = Encoding.ASCII.GetString(header, 0, count);
            return value.StartsWith("UnityFS\0") || value.StartsWith("UnityRaw") || value.StartsWith("UnityWeb");
        }
        internal static string HashText(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
        private static void Report(string message) { MelonLogger.Msg("[FunContent] " + message); Diagnostic?.Invoke(message); }
    }
}
