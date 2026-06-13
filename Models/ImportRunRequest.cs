using System.ComponentModel.DataAnnotations;

namespace VirtualSportsImporter.Worker.Models;

public sealed class ImportRunRequest
{
    [Required]
    public string ClientCode { get; set; } = string.Empty;

    public DateOnly? BusinessDate { get; set; }

    public string? Period { get; set; }

    public DateOnly? FromDate { get; set; }

    public DateOnly? ToDate { get; set; }

    public bool DryRun { get; set; }
}
