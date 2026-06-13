using System.ComponentModel.DataAnnotations;

namespace VirtualSportsImporter.Worker.Models;

public sealed class ImportRunRequest
{
    [Required]
    public string ClientCode { get; set; } = string.Empty;

    [Required]
    public DateTime? BusinessDate { get; set; }

    public bool DryRun { get; set; }
}
