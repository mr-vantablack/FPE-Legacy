using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using FPE_Legacy.Content;
using FPE_Legacy.Networking;
using Il2Cpp;
using MelonLoader;
using UnityEngine;

namespace FPE_Legacy.Sandbox
{
    public readonly struct FunSandboxSpawnPose
    {
        public Vector3 Position { get; }
        public Quaternion Rotation { get; }
        public FunSandboxSpawnPose(Vector3 position, Quaternion rotation)
        {
            Position = position;
            Rotation = rotation;
        }
    }

    // Only an adapter to the native Volume console. Creates no Canvas, EventSystem,
    // input module, menu marker or custom keyboard/cursor handler.
    public static class FunSandboxConsole
    {
        private const string ResourcePrefix = "fpe-content://";
        private sealed class Registration
        {
            internal Volume Console;
            internal readonly List<Volume.catagory> Categories = new();
            internal readonly FunSandboxScroll Scroll = new();
            internal bool Built, Reported;
        }

        private static readonly List<Registration> Consoles = new();
        private static readonly Dictionary<string, FunSpawnEntry> Entries = new(StringComparer.Ordinal);
        private static string _fingerprint;
        private static float _nextTick, _nextSpawn;
        private static bool _busy;
        private static int _lifetime;

        public static float SpawnDistance = 3f;
        public static Func<Camera> CameraProvider = () => Camera.main;
        public static Func<string, string> CategoryNameProvider = package => "FPE / " + package;
        public static Func<FunSpawnEntry, string> OptionNameProvider = entry => entry.Title;
        // Optional bridge to a game-specific placement point. Called only on confirmation.
        public static Func<Volume, FunSpawnEntry, FunSandboxSpawnPose> SpawnPoseProvider;

        public static void Attach(Volume console)
        {
            if (console == null) return;
            try
            {
                var registration = Find(console);
                if (registration == null)
                {
                    registration = new Registration { Console = console };
                    Consoles.Add(registration);
                }
                Install(registration);
            }
            catch (Exception e) { MelonLogger.Error("[FunSandbox] Category registration failed: " + e); }
        }

        private static Registration Find(Volume console)
        {
            for (int i = 0; i < Consoles.Count; i++)
                if (Consoles[i].Console != null && Consoles[i].Console.Pointer == console.Pointer)
                    return Consoles[i];
            return null;
        }

        private static bool ReadCatalogue()
        {
            if (!FunContent.IsReady) return false;
            if (_fingerprint == FunContent.Fingerprint) return true;
            Entries.Clear();
            foreach (var entry in FunContent.GetSpawnCatalogue()) Entries.Add(entry.Id, entry);
            _fingerprint = FunContent.Fingerprint;
            return true;
        }

        private static void Install(Registration registration)
        {
            var categories = registration.Console.PDKPIOHFCCK;
            if (categories == null || !ReadCatalogue()) return;
            if (!registration.Built)
            {
                // Construct locally first. A failed label callback cannot leave half a catalogue.
                var pending = new List<Volume.catagory>();
                foreach (var group in Entries.Values.GroupBy(e => e.PackageId)
                    .OrderBy(g => g.Key, StringComparer.Ordinal))
                {
                    var category = new Volume.catagory
                    {
                        catagoryName = CategoryNameProvider?.Invoke(group.Key) ?? "FPE / " + group.Key,
                        hostOnly = false,
                        isPlayAs = false,
                        isMusic = false,
                        isWeapon = false,
                        // Reuse the game's existing tag from the supplied example.
                        // FPE ownership is identified by registered pointers/resource IDs, not this tag.
                        tagName = "props",
                        options = new Il2CppSystem.Collections.Generic.List<Volume.option>()
                    };
                    foreach (var entry in group.OrderBy(e => e.Title, StringComparer.OrdinalIgnoreCase)
                        .ThenBy(e => e.Id, StringComparer.Ordinal))
                    {
                        category.options.Add(new Volume.option
                        {
                            image = null,
                            optionName = OptionNameProvider?.Invoke(entry) ?? entry.Title,
                            resourcePath = ResourcePrefix + entry.Id
                        });
                    }
                    pending.Add(category);
                }
                registration.Categories.AddRange(pending);
                registration.Built = true;
            }
            foreach (var category in registration.Categories)
            {
                bool present = false;
                for (int i = 0; i < categories.Count; i++)
                    if (categories[i] != null && categories[i].Pointer == category.Pointer)
                    { present = true; break; }
                if (!present) categories.Add(category);
            }
            if (!registration.Reported)
            {
                registration.Reported = true;
                MelonLogger.Msg($"[FunSandbox] Registered {registration.Categories.Count} categories / {Entries.Count} options.");
            }
        }

        // Returns true only when a custom option was consumed, including rejected requests.
        // Never pass an fpe-content:// path to native Resources.Load/game RPCs.
        public static bool TryHandleOption(Volume console, int categoryIndex, int optionIndex)
        {
            bool custom = false;
            try
            {
                if (console == null) return false;
                var categories = console.PDKPIOHFCCK;
                if (categories == null || categoryIndex < 0 || categoryIndex >= categories.Count) return false;
                var category = categories[categoryIndex];
                if (category == null) return false;
                var registration = Find(console);
                custom = registration != null && registration.Categories.Any(c => c.Pointer == category.Pointer);
                if (category.options == null || optionIndex < 0 || optionIndex >= category.options.Count)
                {
                    if (custom) MelonLogger.Warning("[FunSandbox] Invalid custom option index: " + optionIndex);
                    return custom;
                }
                var option = category.options[optionIndex];
                string resource = option?.resourcePath;
                bool marked = resource != null && resource.StartsWith(ResourcePrefix, StringComparison.Ordinal);
                if (!custom && !marked) return false;
                custom = true;
                if (!marked)
                {
                    MelonLogger.Warning("[FunSandbox] Custom option has no FPE resource ID.");
                    return true;
                }
                if (!ReadCatalogue() || !FunNetwork.IsReady)
                {
                    MelonLogger.Warning("[FunSandbox] Content/network is not ready yet.");
                    return true;
                }
                string id = resource.Substring(ResourcePrefix.Length);
                if (!Entries.TryGetValue(id, out var entry))
                {
                    MelonLogger.Warning("[FunSandbox] Unknown or non-networked content: " + id);
                    return true;
                }
                if (_busy || Time.unscaledTime < _nextSpawn) return true;
                FunSandboxSpawnPose pose;
                if (SpawnPoseProvider != null) pose = SpawnPoseProvider(console, entry);
                else
                {
                    var camera = CameraProvider?.Invoke();
                    if (camera == null) throw new InvalidOperationException("No camera. Set FunSandboxConsole.CameraProvider.");
                    if (!float.IsFinite(SpawnDistance) || SpawnDistance <= 0)
                        throw new InvalidOperationException("SpawnDistance must be positive and finite.");
                    pose = new FunSandboxSpawnPose(camera.transform.position + camera.transform.forward * SpawnDistance,
                        Quaternion.identity);
                }
                _nextSpawn = Time.unscaledTime + .35f;
                if (FunNetwork.IsServer)
                    FunNetwork.Observe(SpawnHostAsync(id, pose));
                else if (FunNetwork.RequestSpawn(id, pose.Position, pose.Rotation))
                    MelonLogger.Msg("[FunSandbox] Spawn requested: " + id + ". Waiting for the server decision.");
                else
                    MelonLogger.Warning("[FunSandbox] Spawn request was not sent: " + id);
                return true;
            }
            catch (Exception e)
            {
                MelonLogger.Error("[FunSandbox] SendOption failed: " + e);
                return custom;
            }
        }

        private static async Task SpawnHostAsync(string id, FunSandboxSpawnPose pose)
        {
            int lifetime = _lifetime;
            _busy = true;
            try
            {
                var identity = await FunNetwork.SpawnAsync(id, pose.Position, pose.Rotation,
                    ownerClientId: FunTransport.LocalClientId);
                MelonLogger.Msg($"[FunSandbox] Spawned {id}, object={identity.Id}, owner={identity.OwnerClientId}.");
            }
            finally { if (lifetime == _lifetime) _busy = false; }
        }

        public static void RefreshScroll(Volume console)
        {
            if (console == null) return;
            try { Find(console)?.Scroll.Refresh(console, true); }
            catch (Exception e) { MelonLogger.Warning("[FunSandbox] Scroll setup: " + e.Message); }
        }

        public static void Tick()
        {
            if (Time.unscaledTime < _nextTick) return;
            _nextTick = Time.unscaledTime + .25f;
            for (int i = Consoles.Count - 1; i >= 0; i--)
            {
                var registration = Consoles[i];
                if (registration.Console == null) { Consoles.RemoveAt(i); continue; }
                try
                {
                    // Also covers a console whose Awake ran before content initialization.
                    Install(registration);
                    registration.Scroll.Refresh(registration.Console, false);
                }
                catch (Exception e) { MelonLogger.Warning("[FunSandbox] Console update: " + e.Message); }
            }
        }

        public static void Shutdown()
        {
            _lifetime++; _busy = false;
            foreach (var registration in Consoles)
            {
                try
                {
                    if (registration.Console == null) continue;
                    var categories = registration.Console.PDKPIOHFCCK;
                    if (categories == null) continue;
                    for (int i = categories.Count - 1; i >= 0; i--)
                    {
                        var category = categories[i];
                        if (category != null && registration.Categories.Any(c => c.Pointer == category.Pointer))
                            categories.RemoveAt(i);
                    }
                }
                catch (Exception e) { MelonLogger.Warning("[FunSandbox] Cleanup: " + e.Message); }
            }
            Consoles.Clear(); Entries.Clear(); _fingerprint = null;
            _nextTick = _nextSpawn = 0;
        }
    }
}
