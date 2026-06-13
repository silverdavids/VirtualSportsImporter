namespace VirtualSportsImporter.Worker.Options;

public sealed class ClientImportApiOptions
{
    public const string SectionName = "Clients";

    public string ClientCode { get; set; } = string.Empty;

    public string BaseUrl { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;

    public string BulkImportPath { get; set; } = "/api/management/product-imports/virtualsports/bulk";

    public VirtualSportsOptions? VirtualSports { get; set; }
}
