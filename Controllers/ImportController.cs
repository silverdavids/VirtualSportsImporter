using Microsoft.AspNetCore.Mvc;
using VirtualSportsImporter.Worker.Models;
using VirtualSportsImporter.Worker.Security;
using VirtualSportsImporter.Worker.Services;

namespace VirtualSportsImporter.Worker.Controllers;

[ApiController]
public sealed class ImportController : ControllerBase
{
    private readonly ImportJobRunner _importJobRunner;

    public ImportController(ImportJobRunner importJobRunner)
    {
        _importJobRunner = importJobRunner;
    }

    [RequireWorkerApiKey]
    [HttpPost("/imports/virtualsports/run")]
    public async Task<IActionResult> RunVirtualSportsImport(
        [FromBody] ImportRunRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ClientCode))
        {
            return BadRequest(new
            {
                success = false,
                errors = new[] { "clientCode is required." }
            });
        }

        if (request.BusinessDate is null)
        {
            return BadRequest(new
            {
                success = false,
                errors = new[] { "businessDate is required." }
            });
        }

        var result = await _importJobRunner.RunAsync(
            request.ClientCode,
            request.BusinessDate.Value.Date,
            request.DryRun,
            cancellationToken);

        var response = new
        {
            success = result.Success,
            clientCode = result.ClientCode,
            dryRun = result.DryRun,
            businessDate = result.BusinessDate.ToString("yyyy-MM-dd"),
            rowCount = result.RowCount,
            rowsImported = result.RowsImported,
            totalSales = result.TotalSales,
            totalPayout = result.TotalPayout,
            totalTickets = result.TotalTickets,
            fromDateValue = result.FromDateValue,
            toDateValue = result.ToDateValue,
            generatedReportScreenshotPath = result.GeneratedReportScreenshotPath,
            generatedReportHtmlPath = result.GeneratedReportHtmlPath,
            rows = result.DryRun ? result.Rows.Select(row => new
            {
                sourceSystem = row.SourceSystem,
                externalShopCode = row.ExternalShopCode,
                externalShopName = row.ExternalShopName,
                businessDate = row.BusinessDate.ToString("yyyy-MM-dd"),
                sales = row.Sales,
                payout = row.Payout,
                ticketCount = row.TicketCount
            }).Cast<object>().ToArray() : Array.Empty<object>(),
            errors = result.Errors
        };

        if (result.Success)
        {
            return Ok(response);
        }

        return result.Errors.Any(error => error.StartsWith("Unknown clientCode", StringComparison.OrdinalIgnoreCase))
            ? BadRequest(response)
            : StatusCode(StatusCodes.Status500InternalServerError, response);
    }

    [RequireWorkerApiKey]
    [HttpPost("/imports/virtualsports/discover-selectors")]
    public async Task<IActionResult> DiscoverVirtualSportsSelectors(
        [FromBody] SelectorDiscoveryRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ClientCode))
        {
            return BadRequest(new
            {
                success = false,
                errors = new[] { "clientCode is required." }
            });
        }

        var result = await _importJobRunner.DiscoverSelectorsAsync(
            request.ClientCode,
            cancellationToken);

        var response = new
        {
            success = result.Success,
            clientCode = result.ClientCode,
            pageTitle = result.PageTitle,
            currentUrl = result.CurrentUrl,
            screenshotPath = result.ScreenshotPath,
            htmlPath = result.HtmlPath,
            errors = result.Errors
        };

        if (result.Success)
        {
            return Ok(response);
        }

        return result.Errors.Any(error => error.StartsWith("Unknown clientCode", StringComparison.OrdinalIgnoreCase))
            ? BadRequest(response)
            : StatusCode(StatusCodes.Status500InternalServerError, response);
    }

    [RequireWorkerApiKey]
    [HttpPost("/imports/virtualsports/test-login")]
    public async Task<IActionResult> TestVirtualSportsLogin(
        [FromBody] SelectorDiscoveryRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ClientCode))
        {
            return BadRequest(new
            {
                success = false,
                errors = new[] { "clientCode is required." }
            });
        }

        var result = await _importJobRunner.TestLoginAsync(
            request.ClientCode,
            cancellationToken);

        var response = new
        {
            success = result.Success,
            clientCode = result.ClientCode,
            pageTitle = result.PageTitle,
            currentUrl = result.CurrentUrl,
            screenshotPath = result.ScreenshotPath,
            htmlPath = result.HtmlPath,
            errors = result.Errors
        };

        if (result.Success)
        {
            return Ok(response);
        }

        return result.Errors.Any(error => error.StartsWith("Unknown clientCode", StringComparison.OrdinalIgnoreCase))
            ? BadRequest(response)
            : StatusCode(StatusCodes.Status500InternalServerError, response);
    }

    [RequireWorkerApiKey]
    [HttpPost("/imports/virtualsports/report-page-snapshot")]
    public async Task<IActionResult> CaptureVirtualSportsReportPageSnapshot(
        [FromBody] SelectorDiscoveryRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.ClientCode))
        {
            return BadRequest(new
            {
                success = false,
                errors = new[] { "clientCode is required." }
            });
        }

        var result = await _importJobRunner.CaptureReportPageSnapshotAsync(
            request.ClientCode,
            cancellationToken);

        var response = new
        {
            success = result.Success,
            clientCode = result.ClientCode,
            pageTitle = result.PageTitle,
            currentUrl = result.CurrentUrl,
            screenshotPath = result.ScreenshotPath,
            htmlPath = result.HtmlPath,
            errors = result.Errors
        };

        if (result.Success)
        {
            return Ok(response);
        }

        return result.Errors.Any(error => error.StartsWith("Unknown clientCode", StringComparison.OrdinalIgnoreCase))
            ? BadRequest(response)
            : StatusCode(StatusCodes.Status500InternalServerError, response);
    }
}
