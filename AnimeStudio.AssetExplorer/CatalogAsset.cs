namespace AnimeStudio.AssetExplorer;

// Source + bundle offset + CAB + PathID identifies an object; names are not identifiers.
public sealed record CatalogAsset
{
    public string Name { get; set; } = "";
    public ClassIDType Type { get; set; }
    public string Container { get; set; } = "";
    public string Source { get; set; } = "";
    public long PathID { get; set; }
    public long Offset { get; set; } = -1;
    public string Cab { get; set; } = "";
}

public interface IMeshCatalog
{
    IReadOnlyDictionary<string, CatalogAsset[]> FindMeshes(IEnumerable<string> paths, CancellationToken token);
}

public sealed record CatalogRequest(GameType Game, CatalogAsset[] Assets, string CabMap, string SourceRoot)
{
    public string ExportLogDirectory { get; init; }
    public int ExportWorkers { get; init; } = 2;
    [Newtonsoft.Json.JsonIgnore]
    public IMeshCatalog MeshCatalog { get; init; }
}

public interface IStudioBridge
{
    Task LoadAsync(CatalogRequest request, CancellationToken token);
    Task<string> ExportAssetsAsync(CatalogRequest request, string folder, CancellationToken token);
    Task<string> ExportModelsAsync(CatalogRequest request, string folder, CancellationToken token);
    Task<DirectoryExportResult> ExportDirectoryAsync(DirectoryExportPlan plan, CatalogRequest context, string folder,
        Action<string> report, CancellationToken token);
}
