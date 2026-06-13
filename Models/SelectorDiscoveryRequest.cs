using System.ComponentModel.DataAnnotations;

namespace VirtualSportsImporter.Worker.Models;

public sealed class SelectorDiscoveryRequest
{
    [Required]
    public string ClientCode { get; set; } = string.Empty;
}
