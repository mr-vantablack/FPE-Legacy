#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

// Copy ONLY this file into Assets/Editor in the matching Unity Editor project.
public sealed class FunContentBuilderWindow : EditorWindow
{
    [Serializable] private sealed class ComponentEntry
    {
        public string id = "component", type = "fpe.breakable", target = "", mode = "getOrAdd";
        public int existingIndex = -1;
        public string fields = "{}", properties = "{}";
    }
    [Serializable] private sealed class Entry
    {
        public UnityEngine.Object asset;
        public string id = "asset";
        public bool preload, networked = true, syncPosition = true, syncRotation = true, syncScale, serverPhysics, persistent;
        public float sendRate = 20, interpolationDelay = .1f;
        public List<ComponentEntry> components = new List<ComponentEntry>();
    }
    [SerializeField] private string packageId = "my_props", version = "1.0.0";
    [SerializeField] private List<Entry> entries = new List<Entry>();
    private Vector2 scroll;
    [MenuItem("FunPlusEssentials/Content Builder")]
    private static void Open() { GetWindow<FunContentBuilderWindow>("FPE Content Builder"); }
    private void OnGUI()
    {
        EditorGUILayout.HelpBox("Build with the Unity version and target platform used by the game. Components are added by FPE at runtime; no mod DLL is needed in this Editor project.", MessageType.Info);
        packageId = EditorGUILayout.TextField("Package ID", packageId);
        version = EditorGUILayout.TextField("Version", version);
        EditorGUILayout.LabelField("Build target", EditorUserBuildSettings.activeBuildTarget.ToString());
        scroll = EditorGUILayout.BeginScrollView(scroll);
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i]; EditorGUILayout.BeginVertical("box");
            e.asset = EditorGUILayout.ObjectField("Asset", e.asset, typeof(UnityEngine.Object), false);
            e.id = EditorGUILayout.TextField("Asset ID", e.id); e.preload = EditorGUILayout.Toggle("Preload", e.preload);
            if (e.asset is GameObject)
            {
                e.networked = EditorGUILayout.Toggle("Networked", e.networked);
                e.syncPosition = EditorGUILayout.Toggle("Sync position", e.syncPosition);
                e.syncRotation = EditorGUILayout.Toggle("Sync rotation", e.syncRotation);
                e.syncScale = EditorGUILayout.Toggle("Sync scale", e.syncScale);
                e.serverPhysics = EditorGUILayout.Toggle("Server physics", e.serverPhysics);
                e.persistent = EditorGUILayout.Toggle("Persistent", e.persistent);
                e.sendRate = EditorGUILayout.Slider("Send rate (Hz)", e.sendRate, 1, 60);
                e.interpolationDelay = EditorGUILayout.Slider("Interpolation (s)", e.interpolationDelay, 0, 1);
                for (int c = 0; c < e.components.Count; c++)
                {
                    var component = e.components[c]; EditorGUILayout.BeginVertical("box");
                    component.id = EditorGUILayout.TextField("Component ID", component.id);
                    component.type = EditorGUILayout.TextField("Registered type", component.type);
                    component.target = EditorGUILayout.TextField("Child path (empty=root)", component.target);
                    string[] modes = { "getOrAdd", "add", "require" };
                    component.mode = modes[EditorGUILayout.Popup("Mode", Math.Max(0, Array.IndexOf(modes, component.mode)), modes)];
                    component.existingIndex = EditorGUILayout.IntField("Existing index (-1=auto)", component.existingIndex);
                    EditorGUILayout.LabelField("Fields JSON"); component.fields = EditorGUILayout.TextArea(component.fields, GUILayout.MinHeight(45));
                    EditorGUILayout.LabelField("Properties JSON"); component.properties = EditorGUILayout.TextArea(component.properties, GUILayout.MinHeight(45));
                    if (GUILayout.Button("Remove component")) { e.components.RemoveAt(c); c--; }
                    EditorGUILayout.EndVertical();
                }
                if (GUILayout.Button("Add component")) e.components.Add(new ComponentEntry());
                if (GUILayout.Button("Add Rigidbody + Breakable preset"))
                {
                    e.serverPhysics = true;
                    e.components.Add(new ComponentEntry { id = "body", type = "unity.rigidbody", properties = "{\"mass\":5,\"useGravity\":true}" });
                    e.components.Add(new ComponentEntry { id = "breakable", type = "fpe.breakable", fields = "{\"MaxHealth\":150,\"Health\":150,\"DestroyOnDeath\":true,\"Body\":{\"$component\":\"body\"}}" });
                }
            }
            if (GUILayout.Button("Remove asset")) { entries.RemoveAt(i); i--; }
            EditorGUILayout.EndVertical();
        }
        EditorGUILayout.EndScrollView();
        if (GUILayout.Button("Add asset")) entries.Add(new Entry());
        if (GUILayout.Button("Build bundle"))
        {
            try { Build(); } catch (Exception ex) { Debug.LogException(ex); EditorUtility.DisplayDialog("FPE build failed", ex.Message, "OK"); }
        }
    }
    private void Build()
    {
        ValidateId(packageId); if (string.IsNullOrWhiteSpace(version)) throw new Exception("Version is empty.");
        if (entries.Count == 0) throw new Exception("Add at least one asset.");
        var ids = new HashSet<string>(); var paths = new HashSet<string>(); var assetsJson = new List<string>();
        foreach (var entry in entries)
        {
            ValidateId(entry.id); if (!ids.Add(entry.id)) throw new Exception("Duplicate asset id: " + entry.id);
            string path = AssetDatabase.GetAssetPath(entry.asset);
            if (string.IsNullOrEmpty(path) || Directory.Exists(path)) throw new Exception("Select a project asset for " + entry.id);
            if (entry.asset is SceneAsset) throw new Exception("Scene bundles are not supported by this prefab builder.");
            if (Path.GetFileName(path).Equals("fpe_manifest.json", StringComparison.OrdinalIgnoreCase)) throw new Exception("Manifest is generated automatically.");
            string assetType = entry.asset.GetType().Name;
            if (!new[] { "GameObject", "Texture2D", "Material", "AudioClip", "TextAsset", "Mesh", "Shader", "Sprite", "AnimationClip" }.Contains(assetType))
                throw new Exception("Unsupported catalogue asset type: " + assetType);
            paths.Add(path);
            var components = new List<string>(); var componentIds = new HashSet<string>();
            foreach (var c in entry.components)
            {
                ValidateId(c.id); if (!componentIds.Add(c.id)) throw new Exception("Duplicate component ID: " + c.id);
                if (string.IsNullOrWhiteSpace(c.type)) throw new Exception("Empty component type.");
                ValidateJsonObject(c.fields); ValidateJsonObject(c.properties);
                components.Add("{\"id\":" + Quote(c.id) + ",\"type\":" + Quote(c.type) + ",\"target\":" + Quote(c.target) + ",\"mode\":" + Quote(c.mode) +
                    (c.existingIndex >= 0 ? ",\"existingIndex\":" + c.existingIndex : "") + ",\"fields\":" + c.fields + ",\"properties\":" + c.properties + "}");
            }
            var network = "{\"enabled\":" + Bool(entry.networked && entry.asset is GameObject) + ",\"authority\":\"server\",\"syncPosition\":" + Bool(entry.syncPosition) +
                ",\"syncRotation\":" + Bool(entry.syncRotation) + ",\"syncScale\":" + Bool(entry.syncScale) + ",\"physics\":" + Quote(entry.serverPhysics ? "server" : "none") +
                ",\"persistent\":" + Bool(entry.persistent) + ",\"sendRate\":" + Number(entry.sendRate) + ",\"interpolationDelay\":" + Number(entry.interpolationDelay) + "}";
            assetsJson.Add("{\"id\":" + Quote(entry.id) + ",\"path\":" + Quote(path.ToLowerInvariant()) + ",\"type\":" + Quote(entry.asset.GetType().Name) +
                ",\"preload\":" + Bool(entry.preload) + ",\"network\":" + network + ",\"components\":[" + string.Join(",", components.ToArray()) + "]}");
        }
        string json = "{\"schemaVersion\":1,\"packageId\":" + Quote(packageId) + ",\"version\":" + Quote(version) + ",\"dependencies\":[],\"assets\":[" + string.Join(",", assetsJson.ToArray()) + "]}";
        string output = EditorUtility.OpenFolderPanel("Bundle output folder", "", ""); if (string.IsNullOrEmpty(output)) return;
        string temporary = "Assets/__FPEBuild_" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(temporary);
        try
        {
            string manifestPath = temporary + "/fpe_manifest.json"; File.WriteAllText(manifestPath, json, new UTF8Encoding(false));
            AssetDatabase.Refresh(); paths.Add(manifestPath);
            var build = new AssetBundleBuild { assetBundleName = packageId.ToLowerInvariant() + ".bundle", assetNames = paths.ToArray() };
            var manifest = BuildPipeline.BuildAssetBundles(output, new[] { build }, BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.StrictMode, EditorUserBuildSettings.activeBuildTarget);
            if (manifest == null) throw new Exception("Unity AssetBundle build failed. See Console.");
            if (manifest.GetAllDependencies(build.assetBundleName).Length != 0) throw new Exception("Unexpected external dependencies. Build a self-contained bundle or supply a complete manual manifest.");
            EditorUtility.DisplayDialog("FPE bundle ready", "Copy " + build.assetBundleName + " to Mods/FunPlusEssentials/Assets on every participant's PC.", "OK");
        }
        finally { AssetDatabase.DeleteAsset(temporary); AssetDatabase.Refresh(); }
    }
    private static string Bool(bool value) { return value ? "true" : "false"; }
    private static string Number(float value) { return value.ToString(System.Globalization.CultureInfo.InvariantCulture); }
    private static string Quote(string s)
    {
        var b = new StringBuilder("\"");
        foreach (char c in s ?? "") { if (c == '"' || c == '\\') b.Append('\\').Append(c); else if (c < 32) b.Append("\\u").Append(((int)c).ToString("x4")); else b.Append(c); }
        return b.Append('"').ToString();
    }
    private static void ValidateId(string id)
    { if (string.IsNullOrWhiteSpace(id) || id.Length > 128 || id.Any(c => c > 127 || !(char.IsLetterOrDigit(c) || c == '_' || c == '-' || c == '.'))) throw new Exception("Invalid ID: " + id); }
    // Uses Unity's shipped JSON parser to reject malformed objects before building.
    [Serializable] private sealed class JsonProbe { public string unused; }
    private static void ValidateJsonObject(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || !json.TrimStart().StartsWith("{") || !json.TrimEnd().EndsWith("}")) throw new Exception("Fields/properties must be JSON objects.");
        JsonUtility.FromJson<JsonProbe>(json);
    }
}
#endif
