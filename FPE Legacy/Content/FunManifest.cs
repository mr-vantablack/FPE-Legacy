using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FPE_Legacy.Content
{
    public sealed class FunManifest
    {
        public int SchemaVersion { get; set; } = 1;
        public string PackageId { get; set; }
        public string Version { get; set; } = "1.0.0";
        public List<FunDependency> Dependencies { get; set; } = new();
        public List<FunAssetDefinition> Assets { get; set; } = new();
    }
    public sealed class FunDependency
    {
        public string PackageId { get; set; }
        public string Version { get; set; } // optional 
    }
    public sealed class FunAssetDefinition
    {
        public string Id { get; set; }
        public string Path { get; set; }
        public string Type { get; set; } = "GameObject";
        public bool Preload { get; set; }
        public FunNetworkOptions Network { get; set; } = new();
        public List<FunComponentDefinition> Components { get; set; } = new();
    }
    public sealed class FunComponentDefinition
    {
        public string Id { get; set; }
        public string Type { get; set; }
        public string Target { get; set; } = "";
        public string Mode { get; set; } = "getOrAdd";
        public int? ExistingIndex { get; set; }
        public Dictionary<string, JsonElement> Fields { get; set; } = new();
        public Dictionary<string, JsonElement> Properties { get; set; } = new();
    }
    public sealed class FunNetworkOptions
    {
        public bool Enabled { get; set; } = true;
        public string Authority { get; set; } = "server";
        public bool SyncPosition { get; set; } = true;
        public bool SyncRotation { get; set; } = true;
        public bool SyncScale { get; set; }
        public string Physics { get; set; } = "none";
        public float SendRate { get; set; } = 20;
        public float PositionThreshold { get; set; } = 0.001f;
        public float RotationThreshold { get; set; } = 0.1f;
        public float ScaleThreshold { get; set; } = 0.001f;
        public float InterpolationDelay { get; set; } = 0.1f;
        public bool Persistent { get; set; }
        public FunNetworkOptions Copy() => (FunNetworkOptions)MemberwiseClone();
        public void Validate()
        {
            if (Authority != "server") throw new FormatException("Only server Transform authority is supported.");
            if (Physics != "none" && Physics != "server") throw new FormatException("physics must be none or server.");
            if (!float.IsFinite(SendRate) || SendRate < 1 || SendRate > 60) throw new FormatException("sendRate must be 1..60 Hz.");
            if (!float.IsFinite(InterpolationDelay) || InterpolationDelay < 0 || InterpolationDelay > 1) throw new FormatException("Invalid interpolationDelay.");
            foreach (float v in new[] { PositionThreshold, RotationThreshold, ScaleThreshold })
                if (!float.IsFinite(v) || v < 0) throw new FormatException("Invalid Transform threshold.");
        }
    }
    internal static class FunJson
    {
        internal static readonly JsonSerializerOptions Options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            IncludeFields = true,
            MaxDepth = 32,
            Converters = { new JsonStringEnumConverter() }
        };
        internal static T Read<T>(string json) => JsonSerializer.Deserialize<T>(json, Options) ?? throw new FormatException("Empty JSON.");
        internal static string Write<T>(T value) => JsonSerializer.Serialize(value, Options);
        internal static JsonElement Element(object value) => JsonSerializer.SerializeToElement(value, Options);
    }
    public interface IFunContentReady { void OnContentReady(); }
    public interface IFunNetworkSpawned { void OnNetworkSpawned(); void OnNetworkDespawned(); }
}
