using System;
using System.Collections.Generic;
using System.Linq;

namespace FPE_Legacy.Content
{
    // Detached display data: callers cannot edit the real content definitions.
    public sealed class FunSpawnEntry
    {
        public string Id { get; }
        public string Title { get; }
        public string PackageId { get; }
        public string AssetPath { get; }
        public bool IsFactory { get; }
        public bool ServerPhysics { get; }

        internal FunSpawnEntry(string id, string path, bool factory, bool physics)
        {
            Id = id;
            int separator = id.IndexOf(':');
            PackageId = separator < 0 ? "local" : id.Substring(0, separator);
            Title = (separator < 0 ? id : id.Substring(separator + 1)).Replace('_', ' ');
            AssetPath = path ?? "";
            IsFactory = factory;
            ServerPhysics = physics;
        }
    }

    public static partial class FunContent
    {
        public static IReadOnlyList<FunSpawnEntry> GetSpawnCatalogue()
        {
            FunMainThread.Require();
            if (!IsReady) return Array.Empty<FunSpawnEntry>();
            return Assets.Where(pair => pair.Value.Definition.Type == "GameObject"
                    && pair.Value.Definition.Network.Enabled)
                .Select(pair => new FunSpawnEntry(pair.Key, pair.Value.Definition.Path,
                    pair.Value.Factory != null, pair.Value.Definition.Network.Physics == "server"))
                .OrderBy(entry => entry.PackageId, StringComparer.Ordinal)
                .ThenBy(entry => entry.Title, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }
}
