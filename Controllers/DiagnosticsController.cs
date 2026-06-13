using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using VirtualSportsImporter.Worker.Options;
using VirtualSportsImporter.Worker.Security;

namespace VirtualSportsImporter.Worker.Controllers;

[ApiController]
public sealed class DiagnosticsController : ControllerBase
{
    private readonly IReadOnlyList<ClientImportApiOptions> _clients;
    private readonly IOptions<VirtualSportsOptions> _globalVirtualSportsOptions;

    public DiagnosticsController(
        IReadOnlyList<ClientImportApiOptions> clients,
        IOptions<VirtualSportsOptions> globalVirtualSportsOptions)
    {
        _clients = clients;
        _globalVirtualSportsOptions = globalVirtualSportsOptions;
    }

    [HttpGet("/health")]
    public IActionResult Health()
    {
        var clientCodes = _clients
            .Select(client => client.ClientCode)
            .Where(clientCode => !string.IsNullOrWhiteSpace(clientCode))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return Ok(new
        {
            status = "ok",
            service = "VirtualSportsImporter",
            clientsConfigured = clientCodes.Length,
            clientCodes
        });
    }

    [RequireWorkerApiKey]
    [HttpGet("/imports/virtualsports/config-check/{clientCode}")]
    public IActionResult ConfigCheck(string clientCode)
    {
        var client = _clients.FirstOrDefault(candidate =>
            string.Equals(candidate.ClientCode, clientCode, StringComparison.OrdinalIgnoreCase));

        if (client is null)
        {
            return NotFound(new
            {
                success = false,
                clientCode,
                errors = new[] { $"Unknown clientCode '{clientCode}'." }
            });
        }

        var virtualSports = BuildVirtualSportsOptions(client);
        var checks = BuildChecks(client, virtualSports);
        var missing = checks
            .Where(check => !check.Value)
            .Select(check => check.Key)
            .ToArray();

        return Ok(new
        {
            success = missing.Length == 0,
            clientCode = client.ClientCode,
            configured = checks,
            missing
        });
    }

    private Dictionary<string, bool> BuildChecks(
        ClientImportApiOptions client,
        VirtualSportsOptions virtualSports)
    {
        return new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            ["LoginUrl"] = IsConfigured(virtualSports.LoginUrl) && virtualSports.LoginUrl != "https://...",
            ["Username"] = IsConfigured(virtualSports.Username),
            ["Password"] = IsConfigured(virtualSports.Password),
            ["LoginSelectors.Username"] = IsConfigured(virtualSports.Selectors.Username),
            ["LoginSelectors.Password"] = IsConfigured(virtualSports.Selectors.Password),
            ["LoginSelectors.LoginButton"] = IsConfigured(virtualSports.Selectors.LoginButton),
            ["LoginSuccessUrlContains"] = IsConfigured(virtualSports.LoginSuccessUrlContains),
            ["LoginSuccessSelector"] = IsConfigured(virtualSports.LoginSuccessSelector),
            ["LoginFailureSelector"] = IsConfigured(virtualSports.LoginFailureSelector),
            ["LoginRoleSelect"] = IsConfigured(virtualSports.LoginRoleSelect),
            ["LoginRoleValue"] = IsConfigured(virtualSports.LoginRoleValue),
            ["ReportSelectors.GeneralOverviewMenu"] = IsConfigured(virtualSports.Selectors.GeneralOverviewMenu),
            ["ReportSelectors.ExpandShopsNodeSelector"] = IsConfigured(virtualSports.Selectors.ExpandShopsNodeSelector),
            ["ReportSelectors.FromDate"] = IsConfigured(virtualSports.Selectors.FromDate),
            ["ReportSelectors.ToDate"] = IsConfigured(virtualSports.Selectors.ToDate),
            ["ReportSelectors.DatePickerOkButton"] = IsConfigured(virtualSports.Selectors.DatePickerOkButton),
            ["ReportSelectors.SearchButton"] = IsConfigured(virtualSports.Selectors.SearchButton),
            ["ReportSelectors.ReportLoadingIndicator"] = IsConfigured(virtualSports.Selectors.ReportLoadingIndicator),
            ["ReportSelectors.ReportTable"] = IsConfigured(virtualSports.Selectors.ReportTable),
            ["ReportSelectors.ReportRows"] = IsConfigured(virtualSports.Selectors.ReportRows),
            ["ReportSelectors.ShopCodeCell"] = IsConfigured(virtualSports.Selectors.ShopCodeCell),
            ["ReportSelectors.ShopNameCell"] = IsConfigured(virtualSports.Selectors.ShopNameCell),
            ["ReportSelectors.TicketCountCell"] = IsConfigured(virtualSports.Selectors.TicketCountCell),
            ["ReportSelectors.TotalInCell"] = IsConfigured(virtualSports.Selectors.TotalInCell),
            ["ReportSelectors.TotalOutCell"] = IsConfigured(virtualSports.Selectors.TotalOutCell),
            ["BaseUrl"] = IsConfigured(client.BaseUrl) && client.BaseUrl != "https://smartbet...",
            ["ApiKey"] = IsConfigured(client.ApiKey),
            ["BulkImportPath"] = IsConfigured(client.BulkImportPath)
        };
    }

    private VirtualSportsOptions BuildVirtualSportsOptions(ClientImportApiOptions client)
    {
        var global = _globalVirtualSportsOptions.Value;
        var clientVirtualSports = client.VirtualSports;

        if (clientVirtualSports is null)
        {
            return new VirtualSportsOptions
            {
                LoginUrl = global.LoginUrl,
                Username = global.Username,
                Password = global.Password,
                Headless = global.Headless,
                TimeoutSeconds = global.TimeoutSeconds,
                AuditPath = global.AuditPath,
                LoginSuccessUrlContains = global.LoginSuccessUrlContains,
                LoginSuccessSelector = global.LoginSuccessSelector,
                LoginFailureSelector = global.LoginFailureSelector,
                LoginRoleSelect = global.LoginRoleSelect,
                LoginRoleValue = global.LoginRoleValue,
                Selectors = CopySelectors(global.Selectors)
            };
        }

        return new VirtualSportsOptions
        {
            LoginUrl = FirstConfigured(clientVirtualSports.LoginUrl, global.LoginUrl),
            Username = FirstConfigured(clientVirtualSports.Username, global.Username),
            Password = FirstConfigured(clientVirtualSports.Password, global.Password),
            Headless = clientVirtualSports.Headless,
            TimeoutSeconds = clientVirtualSports.TimeoutSeconds > 0
                ? clientVirtualSports.TimeoutSeconds
                : global.TimeoutSeconds,
            AuditPath = FirstConfigured(clientVirtualSports.AuditPath, global.AuditPath),
            LoginSuccessUrlContains = FirstConfigured(
                clientVirtualSports.LoginSuccessUrlContains,
                global.LoginSuccessUrlContains),
            LoginSuccessSelector = FirstConfigured(
                clientVirtualSports.LoginSuccessSelector,
                global.LoginSuccessSelector),
            LoginFailureSelector = FirstConfigured(
                clientVirtualSports.LoginFailureSelector,
                global.LoginFailureSelector),
            LoginRoleSelect = FirstConfigured(
                clientVirtualSports.LoginRoleSelect,
                global.LoginRoleSelect),
            LoginRoleValue = FirstConfigured(
                clientVirtualSports.LoginRoleValue,
                global.LoginRoleValue),
            Selectors = MergeSelectors(clientVirtualSports.Selectors, global.Selectors)
        };
    }

    private static VirtualSportsSelectorsOptions MergeSelectors(
        VirtualSportsSelectorsOptions primary,
        VirtualSportsSelectorsOptions fallback)
    {
        return new VirtualSportsSelectorsOptions
        {
            Username = FirstConfigured(primary.Username, fallback.Username),
            Password = FirstConfigured(primary.Password, fallback.Password),
            LoginButton = FirstConfigured(primary.LoginButton, fallback.LoginButton),
            GeneralOverviewMenu = FirstConfigured(primary.GeneralOverviewMenu, fallback.GeneralOverviewMenu),
            ExpandShopsNodeSelector = FirstConfigured(
                primary.ExpandShopsNodeSelector,
                fallback.ExpandShopsNodeSelector),
            FromDate = FirstConfigured(primary.FromDate, fallback.FromDate),
            ToDate = FirstConfigured(primary.ToDate, fallback.ToDate),
            DatePickerOkButton = FirstConfigured(primary.DatePickerOkButton, fallback.DatePickerOkButton),
            SearchButton = FirstConfigured(primary.SearchButton, fallback.SearchButton),
            ReportLoadingIndicator = FirstConfigured(
                primary.ReportLoadingIndicator,
                fallback.ReportLoadingIndicator),
            ReportTable = FirstConfigured(primary.ReportTable, fallback.ReportTable),
            ReportRows = FirstConfigured(primary.ReportRows, fallback.ReportRows),
            ShopCodeCell = FirstConfigured(primary.ShopCodeCell, fallback.ShopCodeCell),
            ShopNameCell = FirstConfigured(primary.ShopNameCell, fallback.ShopNameCell),
            TicketCountCell = FirstConfigured(primary.TicketCountCell, fallback.TicketCountCell),
            TotalInCell = FirstConfigured(primary.TotalInCell, fallback.TotalInCell),
            TotalOutCell = FirstConfigured(primary.TotalOutCell, fallback.TotalOutCell)
        };
    }

    private static VirtualSportsSelectorsOptions CopySelectors(VirtualSportsSelectorsOptions selectors)
    {
        return MergeSelectors(selectors, new VirtualSportsSelectorsOptions());
    }

    private static string FirstConfigured(string primary, string fallback)
    {
        return string.IsNullOrWhiteSpace(primary) ? fallback : primary;
    }

    private static bool IsConfigured(string value)
    {
        return !string.IsNullOrWhiteSpace(value);
    }
}
