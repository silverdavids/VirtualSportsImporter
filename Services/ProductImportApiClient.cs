using System.Net.Http.Json;
using VirtualSportsImporter.Worker.Models;
using VirtualSportsImporter.Worker.Options;

namespace VirtualSportsImporter.Worker.Services;

public sealed class ProductImportApiClient
{
    private readonly HttpClient _httpClient;
    private readonly IReadOnlyList<ClientImportApiOptions> _clients;
    private readonly ILogger<ProductImportApiClient> _logger;

    public ProductImportApiClient(
        HttpClient httpClient,
        IReadOnlyList<ClientImportApiOptions> clients,
        ILogger<ProductImportApiClient> logger)
    {
        _httpClient = httpClient;
        _clients = clients;
        _logger = logger;
    }

    public ClientImportApiOptions GetClientOrThrow(string clientCode)
    {
        var client = _clients.FirstOrDefault(client =>
            string.Equals(client.ClientCode, clientCode, StringComparison.OrdinalIgnoreCase));

        if (client is null)
        {
            throw new UnknownClientCodeException(clientCode);
        }

        return client;
    }

    public async Task PostBulkAsync(
        ClientImportApiOptions client,
        DateTime businessDate,
        IReadOnlyCollection<VirtualSportsImportRow> rows,
        CancellationToken cancellationToken)
    {
        ValidateOptions(client);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri(new Uri(client.BaseUrl), client.BulkImportPath));

        request.Headers.Add("X-Import-Api-Key", client.ApiKey);
        request.Content = JsonContent.Create(new
        {
            clientCode = client.ClientCode,
            clientId = client.ClientId,
            sourceSystem = "VirtualSports",
            businessDate = businessDate.ToString("yyyy-MM-dd"),
            rows = rows.Select(row => new
            {
                sourceSystem = row.SourceSystem,
                externalShopCode = row.ExternalShopCode,
                externalShopName = row.ExternalShopName,
                businessDate = row.BusinessDate.ToString("yyyy-MM-dd"),
                sales = row.Sales,
                payout = row.Payout,
                ticketCount = row.TicketCount
            })
        });

        using var response = await _httpClient.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var responseBody = await response.Content.ReadAsStringAsync(cancellationToken);

            _logger.LogError(
                "Product import failed for ClientCode={ClientCode} with status {StatusCode}: {ResponseBody}",
                client.ClientCode,
                response.StatusCode,
                responseBody);

            response.EnsureSuccessStatusCode();
        }
    }

    private static void ValidateOptions(ClientImportApiOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ClientCode))
        {
            throw new InvalidOperationException("Clients entries must include ClientCode.");
        }

        if (string.IsNullOrWhiteSpace(options.BaseUrl) || options.BaseUrl == "https://smartbet...")
        {
            throw new InvalidOperationException(
                $"Clients:{options.ClientCode}:BaseUrl must be configured before running imports.");
        }

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            throw new InvalidOperationException(
                $"Clients:{options.ClientCode}:ApiKey must be configured before running imports.");
        }

        if (string.IsNullOrWhiteSpace(options.BulkImportPath))
        {
            throw new InvalidOperationException(
                $"Clients:{options.ClientCode}:BulkImportPath must be configured before running imports.");
        }
    }
}

public sealed class UnknownClientCodeException : InvalidOperationException
{
    public UnknownClientCodeException(string clientCode)
        : base($"Unknown clientCode '{clientCode}'.")
    {
        ClientCode = clientCode;
    }

    public string ClientCode { get; }
}
