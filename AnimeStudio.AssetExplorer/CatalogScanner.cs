using Newtonsoft.Json;

namespace AnimeStudio.AssetExplorer;

// Runs in an isolated worker process: the upstream loader has process-wide state.
internal static class CatalogScanner
{
    internal sealed record Shard(long Length, long Modified, bool Full, GameType Game, CatalogAsset[] Assets, CabEntry[] Cabs);
    public static Shard Scan(string path, GameType game, bool full, CancellationToken token)
    {
        var before = new FileInfo(path);
        var length = before.Length; var modified = before.LastWriteTimeUtc.Ticks;
        var manager = new AssetsManager { Game = GameManager.GetGameByType(game), SkipProcess = true, ResolveDependencies = false };
        try
        {
            manager.LoadFiles(new[] { path }, mergeSplitAssets: false);
            token.ThrowIfCancellationRequested();
            if (manager.assetsFileList.Count == 0) throw new InvalidDataException("未读取到 Unity CAB：" + path);
            var rows = new List<CatalogAsset>();
            var cabs = new List<CabEntry>();
            var containers = new List<(PPtr<AnimeStudio.Object> Ptr, string Path)>();
            var animators = new List<(PPtr<AnimeStudio.Object> Ptr, CatalogAsset Asset)>();
            var rowMap = new Dictionary<(SerializedFile, long), CatalogAsset>();
            foreach (var file in manager.assetsFileList)
            {
                cabs.Add(new(file.fileName, path, file.offset, file.m_Externals.Select(x => x.fileName).ToArray()));
                foreach (var info in file.m_Objects)
                {
                    token.ThrowIfCancellationRequested();
                    var reader = new ObjectReader(file.reader, file, info, manager.Game);
                    var obj = new AnimeStudio.Object(reader); // advances past the common Object header
                    var row = new CatalogAsset { Source = path, Cab = file.fileName, Offset = file.offset,
                        PathID = reader.m_PathID, Type = reader.type, Name = reader.type.ToString() };
                    var include = full;
                    switch (reader.type)
                    {
                        case ClassIDType.AssetBundle:
                            var bundle = new AssetBundle(reader);
                            row.Name = bundle.m_Name;
                            foreach (var item in bundle.m_Container)
                            {
                                containers.Add((item.Value.asset, item.Key));
                                var first = item.Value.preloadIndex;
                                var end = Math.Min((long)first + item.Value.preloadSize, bundle.m_PreloadTable.Count);
                                for (var i = Math.Max(0, first); i < end; i++) containers.Add((bundle.m_PreloadTable[i], item.Key));
                            }
                            break;
                        case ClassIDType.ResourceManager:
                            var resources = new ResourceManager(reader);
                            foreach (var item in resources.m_Container) containers.Add((item.Value, item.Key));
                            break;
                        case ClassIDType.GameObject:
                            var go = new GameObject(reader); obj = go; row.Name = go.m_Name; include = true; break;
                        case ClassIDType.Animator:
                            animators.Add((new PPtr<AnimeStudio.Object>(reader), row)); include = true; break;
                        case ClassIDType.MonoBehaviour:
                            var behaviour = new MonoBehaviour(reader); row.Name = string.IsNullOrWhiteSpace(behaviour.Name) ? "MonoBehaviour" : behaviour.Name;
                            include = true; break;
                        case ClassIDType.Font:
                        case ClassIDType.Material:
                        case ClassIDType.Mesh:
                        case ClassIDType.Sprite:
                        case ClassIDType.TextAsset:
                        case ClassIDType.Texture2D:
                        case ClassIDType.VideoClip:
                        case ClassIDType.AudioClip:
                        case ClassIDType.AnimationClip:
                        case ClassIDType.Avatar:
                        case ClassIDType.AnimatorController:
                        case ClassIDType.AnimatorOverrideController:
                            row.Name = reader.ReadAlignedString(); include = true; break;
                        case ClassIDType.Shader:
                            row.Name = reader.ReadAlignedString();
                            if (string.IsNullOrEmpty(row.Name)) row.Name = new SerializedShader(reader).m_Name;
                            include = true; break;
                    }
                    file.AddObject(obj);
                    rowMap[(file, row.PathID)] = row;
                    if (include) rows.Add(row);
                }
            }
            foreach (var item in containers)
                if (item.Ptr.TryGet(out var obj) && rowMap.TryGetValue((obj.assetsFile, obj.m_PathID), out var row)) row.Container = item.Path;
            foreach (var item in animators)
                if (item.Ptr.TryGet<GameObject>(out var go)) item.Asset.Name = go.m_Name;
            before.Refresh();
            if (before.Length != length || before.LastWriteTimeUtc.Ticks != modified) throw new IOException("扫描时文件发生变化：" + path);
            return new Shard(length, modified, full, game, rows.ToArray(), cabs.ToArray());
        }
        finally { manager.Clear(); }
    }
}
