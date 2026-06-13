namespace VirtualSportsImporter.Worker.Models;

public sealed class VirtualSportsImportRow
{
    public string SourceSystem { get; set; } = "VirtualSports";

    public string ExternalShopCode { get; set; } = string.Empty;

    public string ExternalShopName { get; set; } = string.Empty;

    public DateTime BusinessDate { get; set; }

    public decimal Sales { get; set; }

    public decimal Payout { get; set; }

    public int TicketCount { get; set; }
}
