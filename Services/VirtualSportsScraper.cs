using System.Globalization;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using VirtualSportsImporter.Worker.Models;
using VirtualSportsImporter.Worker.Options;

namespace VirtualSportsImporter.Worker.Services;

public sealed class VirtualSportsScraper
{
    private static readonly CultureInfo AmountCulture = CultureInfo.InvariantCulture;

    private readonly IOptions<VirtualSportsOptions> _options;
    private readonly ILogger<VirtualSportsScraper> _logger;

    public VirtualSportsScraper(
        IOptions<VirtualSportsOptions> options,
        ILogger<VirtualSportsScraper> logger)
    {
        _options = options;
        _logger = logger;
    }

    public async Task<VirtualSportsScrapeResult> ExtractRowsAsync(
        string clientCode,
        DateTime businessDate,
        VirtualSportsOptions options,
        bool saveSuccessAudit,
        CancellationToken cancellationToken)
    {
        ValidateOptions(clientCode, options);
        Directory.CreateDirectory(options.AuditPath);

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = options.Headless,
            Timeout = options.TimeoutSeconds * 1000
        });

        var page = await browser.NewPageAsync(new BrowserNewPageOptions
        {
            ViewportSize = new ViewportSize { Width = 1440, Height = 1000 }
        });

        page.SetDefaultTimeout(options.TimeoutSeconds * 1000);
        AuditArtifactPaths? generatedReportArtifacts = null;
        DateRangeSelectionResult? dateRangeSelection = null;

        try
        {
            await LoginAsync(page, clientCode, options, cancellationToken);
            await OpenGeneralOverviewReportAsync(page, clientCode, options.Selectors, cancellationToken);
            dateRangeSelection = await SelectDateRangeAsync(
                page,
                clientCode,
                options.Selectors,
                businessDate,
                cancellationToken);

            if (saveSuccessAudit)
            {
                generatedReportArtifacts = await SaveAuditArtifactsAsync(
                    page,
                    options.AuditPath,
                    businessDate,
                    "generated-report",
                    cancellationToken);
            }

            await ExpandShopsNodeAsync(page, clientCode, options.Selectors, cancellationToken);

            var rows = await ExtractReportRowsAsync(page, clientCode, options.Selectors, businessDate);

            _logger.LogInformation(
                "VirtualSports scrape completed. ClientCode={ClientCode}, BusinessDate={BusinessDate:yyyy-MM-dd}, Rows={RowCount}, FromDateValue={FromDateValue}, ToDateValue={ToDateValue}, GeneratedReportScreenshotPath={GeneratedReportScreenshotPath}, GeneratedReportHtmlPath={GeneratedReportHtmlPath}.",
                clientCode,
                businessDate,
                rows.Count,
                dateRangeSelection.FromDateValue,
                dateRangeSelection.ToDateValue,
                generatedReportArtifacts?.ScreenshotPath,
                generatedReportArtifacts?.HtmlPath);

            return new VirtualSportsScrapeResult
            {
                Rows = rows,
                FromDateValue = dateRangeSelection.FromDateValue,
                ToDateValue = dateRangeSelection.ToDateValue,
                GeneratedReportScreenshotPath = generatedReportArtifacts?.ScreenshotPath ?? string.Empty,
                GeneratedReportHtmlPath = generatedReportArtifacts?.HtmlPath ?? string.Empty
            };
        }
        catch (Exception ex)
        {
            await SaveAuditArtifactsAsync(
                page,
                options.AuditPath,
                businessDate,
                "failure",
                cancellationToken);

            _logger.LogError(
                ex,
                "VirtualSports scrape failed. ClientCode={ClientCode}, BusinessDate={BusinessDate:yyyy-MM-dd}. Audit artifacts saved to {AuditPath}. GeneratedReportScreenshotPath={GeneratedReportScreenshotPath}, GeneratedReportHtmlPath={GeneratedReportHtmlPath}.",
                clientCode,
                businessDate,
                options.AuditPath,
                generatedReportArtifacts?.ScreenshotPath,
                generatedReportArtifacts?.HtmlPath);

            throw new VirtualSportsScrapeException(
                ex.Message,
                ex,
                dateRangeSelection?.FromDateValue ?? string.Empty,
                dateRangeSelection?.ToDateValue ?? string.Empty,
                generatedReportArtifacts?.ScreenshotPath ?? string.Empty,
                generatedReportArtifacts?.HtmlPath ?? string.Empty);
        }
    }

    public async Task<SelectorDiscoveryResult> DiscoverSelectorsAsync(
        string clientCode,
        VirtualSportsOptions options,
        CancellationToken cancellationToken)
    {
        ValidateLoginOptions(clientCode, options);
        Directory.CreateDirectory(options.AuditPath);

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = options.Headless,
            Timeout = options.TimeoutSeconds * 1000
        });

        var page = await browser.NewPageAsync(new BrowserNewPageOptions
        {
            ViewportSize = new ViewportSize { Width = 1440, Height = 1000 }
        });

        page.SetDefaultTimeout(options.TimeoutSeconds * 1000);

        try
        {
            await LoginAsync(page, clientCode, options, cancellationToken);

            var artifacts = await SaveAuditArtifactsAsync(
                page,
                options.AuditPath,
                DateTime.UtcNow.Date,
                "selector-discovery",
                cancellationToken);

            var result = new SelectorDiscoveryResult
            {
                Success = true,
                ClientCode = clientCode,
                PageTitle = await page.TitleAsync(),
                CurrentUrl = page.Url,
                ScreenshotPath = artifacts.ScreenshotPath,
                HtmlPath = artifacts.HtmlPath,
                Errors = []
            };

            _logger.LogInformation(
                "VirtualSports selector discovery completed. ClientCode={ClientCode}, PageTitle={PageTitle}, CurrentUrl={CurrentUrl}, ScreenshotPath={ScreenshotPath}, HtmlPath={HtmlPath}.",
                result.ClientCode,
                result.PageTitle,
                result.CurrentUrl,
                result.ScreenshotPath,
                result.HtmlPath);

            return result;
        }
        catch (Exception ex)
        {
            var artifacts = await SaveAuditArtifactsAsync(
                page,
                options.AuditPath,
                DateTime.UtcNow.Date,
                "selector-discovery-failure",
                cancellationToken);

            _logger.LogError(
                ex,
                "VirtualSports selector discovery failed. ClientCode={ClientCode}. Audit artifacts saved to {AuditPath}.",
                clientCode,
                options.AuditPath);

            return new SelectorDiscoveryResult
            {
                Success = false,
                ClientCode = clientCode,
                PageTitle = await page.TitleAsync(),
                CurrentUrl = page.Url,
                ScreenshotPath = artifacts.ScreenshotPath,
                HtmlPath = artifacts.HtmlPath,
                Errors = [ex.Message]
            };
        }
    }

    public async Task<LoginTestResult> TestLoginAsync(
        string clientCode,
        VirtualSportsOptions options,
        CancellationToken cancellationToken)
    {
        ValidateLoginOptions(clientCode, options);
        Directory.CreateDirectory(options.AuditPath);

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = options.Headless,
            Timeout = options.TimeoutSeconds * 1000
        });

        var page = await browser.NewPageAsync(new BrowserNewPageOptions
        {
            ViewportSize = new ViewportSize { Width = 1440, Height = 1000 }
        });

        page.SetDefaultTimeout(options.TimeoutSeconds * 1000);

        try
        {
            await LoginAsync(page, clientCode, options, cancellationToken);

            var errors = new List<string>();
            var success = await DetermineLoginSuccessAsync(page, options, errors);
            var artifacts = await SaveAuditArtifactsAsync(
                page,
                options.AuditPath,
                DateTime.UtcNow.Date,
                success ? "login-test-success" : "login-test",
                cancellationToken);

            var result = new LoginTestResult
            {
                Success = success,
                ClientCode = clientCode,
                PageTitle = await page.TitleAsync(),
                CurrentUrl = page.Url,
                ScreenshotPath = artifacts.ScreenshotPath,
                HtmlPath = artifacts.HtmlPath,
                Errors = errors
            };

            _logger.LogInformation(
                "VirtualSports login test completed. ClientCode={ClientCode}, Success={Success}, PageTitle={PageTitle}, CurrentUrl={CurrentUrl}, ScreenshotPath={ScreenshotPath}, HtmlPath={HtmlPath}, Errors={ErrorCount}.",
                result.ClientCode,
                result.Success,
                result.PageTitle,
                result.CurrentUrl,
                result.ScreenshotPath,
                result.HtmlPath,
                result.Errors.Count);

            return result;
        }
        catch (Exception ex)
        {
            var artifacts = await SaveAuditArtifactsAsync(
                page,
                options.AuditPath,
                DateTime.UtcNow.Date,
                "login-test-failure",
                cancellationToken);

            _logger.LogError(
                ex,
                "VirtualSports login test failed. ClientCode={ClientCode}. Audit artifacts saved to {AuditPath}.",
                clientCode,
                options.AuditPath);

            return new LoginTestResult
            {
                Success = false,
                ClientCode = clientCode,
                PageTitle = await page.TitleAsync(),
                CurrentUrl = page.Url,
                ScreenshotPath = artifacts.ScreenshotPath,
                HtmlPath = artifacts.HtmlPath,
                Errors = [ex.Message]
            };
        }
    }

    public async Task<SelectorDiscoveryResult> CaptureReportPageSnapshotAsync(
        string clientCode,
        VirtualSportsOptions options,
        CancellationToken cancellationToken)
    {
        ValidateReportPageSnapshotOptions(clientCode, options);
        Directory.CreateDirectory(options.AuditPath);

        using var playwright = await Playwright.CreateAsync();
        await using var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = options.Headless,
            Timeout = options.TimeoutSeconds * 1000
        });

        var page = await browser.NewPageAsync(new BrowserNewPageOptions
        {
            ViewportSize = new ViewportSize { Width = 1440, Height = 1000 }
        });

        page.SetDefaultTimeout(options.TimeoutSeconds * 1000);

        try
        {
            await LoginAsync(page, clientCode, options, cancellationToken);
            await OpenGeneralOverviewReportAsync(page, clientCode, options.Selectors, cancellationToken);

            var artifacts = await SaveAuditArtifactsAsync(
                page,
                options.AuditPath,
                DateTime.UtcNow.Date,
                "report-page-snapshot",
                cancellationToken);

            var result = new SelectorDiscoveryResult
            {
                Success = true,
                ClientCode = clientCode,
                PageTitle = await page.TitleAsync(),
                CurrentUrl = page.Url,
                ScreenshotPath = artifacts.ScreenshotPath,
                HtmlPath = artifacts.HtmlPath,
                Errors = []
            };

            _logger.LogInformation(
                "VirtualSports report page snapshot completed. ClientCode={ClientCode}, PageTitle={PageTitle}, CurrentUrl={CurrentUrl}, ScreenshotPath={ScreenshotPath}, HtmlPath={HtmlPath}.",
                result.ClientCode,
                result.PageTitle,
                result.CurrentUrl,
                result.ScreenshotPath,
                result.HtmlPath);

            return result;
        }
        catch (Exception ex)
        {
            var artifacts = await SaveAuditArtifactsAsync(
                page,
                options.AuditPath,
                DateTime.UtcNow.Date,
                "report-page-snapshot-failure",
                cancellationToken);

            _logger.LogError(
                ex,
                "VirtualSports report page snapshot failed. ClientCode={ClientCode}. Audit artifacts saved to {AuditPath}.",
                clientCode,
                options.AuditPath);

            return new SelectorDiscoveryResult
            {
                Success = false,
                ClientCode = clientCode,
                PageTitle = await page.TitleAsync(),
                CurrentUrl = page.Url,
                ScreenshotPath = artifacts.ScreenshotPath,
                HtmlPath = artifacts.HtmlPath,
                Errors = [ex.Message]
            };
        }
    }

    public async Task LoginAsync(IPage page, CancellationToken cancellationToken = default)
    {
        await LoginAsync(page, "GLOBAL", _options.Value, cancellationToken);
    }

    private async Task LoginAsync(
        IPage page,
        string clientCode,
        VirtualSportsOptions options,
        CancellationToken cancellationToken)
    {
        var selectors = options.Selectors;

        _logger.LogInformation(
            "VirtualSports navigation step started. ClientCode={ClientCode}, Step={Step}.",
            clientCode,
            "Login");

        await page.GotoAsync(options.LoginUrl, new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        await FillRequiredAsync(page, selectors.Username, options.Username, "Username", cancellationToken);
        await FillRequiredAsync(page, selectors.Password, options.Password, "Password", cancellationToken);
        await SelectLoginRoleAsync(page, options, cancellationToken);
        await ClickRequiredAsync(page, selectors.LoginButton, "LoginButton", cancellationToken);
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);

        _logger.LogInformation(
            "VirtualSports navigation step completed. ClientCode={ClientCode}, Step={Step}.",
            clientCode,
            "Login");

        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task OpenGeneralOverviewReportAsync(
        IPage page,
        string clientCode,
        VirtualSportsSelectorsOptions selectors,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "VirtualSports navigation step started. ClientCode={ClientCode}, Step={Step}.",
            clientCode,
            "Open General Overview");

        if (TryGetConfiguredUrl(selectors.GeneralOverviewMenu, out var configuredUrl))
        {
            await NavigateToConfiguredUrlAsync(page, configuredUrl);
        }
        else
        {
            await ClickRequiredAsync(page, selectors.GeneralOverviewMenu, "GeneralOverviewMenu", cancellationToken);
            await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        }

        _logger.LogInformation(
            "VirtualSports navigation step completed. ClientCode={ClientCode}, Step={Step}.",
            clientCode,
            "Open General Overview");

        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task<DateRangeSelectionResult> SelectDateRangeAsync(
        IPage page,
        string clientCode,
        VirtualSportsSelectorsOptions selectors,
        DateTime businessDate,
        CancellationToken cancellationToken)
    {
        var fromDate = businessDate.Date.ToString("yyyy-MM-dd 00:00", CultureInfo.InvariantCulture);
        var toDate = businessDate.Date.AddDays(1).ToString("yyyy-MM-dd 00:00", CultureInfo.InvariantCulture);

        _logger.LogInformation(
            "VirtualSports navigation step started. ClientCode={ClientCode}, Step={Step}, BusinessDate={BusinessDate:yyyy-MM-dd}, FromDate={FromDate}, ToDate={ToDate}.",
            clientCode,
            "Select Date Range",
            businessDate,
            fromDate,
            toDate);

        await FillDateAsync(
            page,
            selectors.FromDate,
            fromDate,
            "FromDate",
            selectors.DatePickerOkButton,
            cancellationToken);
        await FillDateAsync(
            page,
            selectors.ToDate,
            toDate,
            "ToDate",
            selectors.DatePickerOkButton,
            cancellationToken);
        await SetDateInputValueAsync(page, selectors.FromDate, fromDate, "FromDate");
        await SetDateInputValueAsync(page, selectors.ToDate, toDate, "ToDate");
        await page.Keyboard.PressAsync("Escape");
        await page.Keyboard.PressAsync("Escape");

        var selectedFromDate = await ReadInputValueAsync(page, selectors.FromDate, "FromDate");
        var selectedToDate = await ReadInputValueAsync(page, selectors.ToDate, "ToDate");

        _logger.LogInformation(
            "VirtualSports date inputs read back. ClientCode={ClientCode}, BusinessDate={BusinessDate:yyyy-MM-dd}, FromDateValue={FromDateValue}, ToDateValue={ToDateValue}.",
            clientCode,
            businessDate,
            selectedFromDate,
            selectedToDate);

        await ClickRequiredAsync(page, selectors.SearchButton, "SearchButton", cancellationToken, force: true);
        await page.WaitForLoadStateAsync(LoadState.NetworkIdle);
        await WaitForOptionalHiddenAsync(page, selectors.ReportLoadingIndicator);
        await page.WaitForTimeoutAsync(1000);

        _logger.LogInformation(
            "VirtualSports navigation step completed. ClientCode={ClientCode}, Step={Step}, BusinessDate={BusinessDate:yyyy-MM-dd}, FromDateValue={FromDateValue}, ToDateValue={ToDateValue}.",
            clientCode,
            "Select Date Range",
            businessDate,
            selectedFromDate,
            selectedToDate);

        cancellationToken.ThrowIfCancellationRequested();

        return new DateRangeSelectionResult
        {
            FromDateValue = selectedFromDate,
            ToDateValue = selectedToDate
        };
    }

    private async Task ExpandShopsNodeAsync(
        IPage page,
        string clientCode,
        VirtualSportsSelectorsOptions selectors,
        CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "VirtualSports navigation step started. ClientCode={ClientCode}, Step={Step}.",
            clientCode,
            "Expand Shops Node");

        await ClickRequiredAsync(
            page,
            selectors.ExpandShopsNodeSelector,
            "ExpandShopsNodeSelector",
            cancellationToken);

        _logger.LogInformation(
            "VirtualSports navigation step completed. ClientCode={ClientCode}, Step={Step}.",
            clientCode,
            "Expand Shops Node");
    }

    private async Task<IReadOnlyList<VirtualSportsImportRow>> ExtractReportRowsAsync(
        IPage page,
        string clientCode,
        VirtualSportsSelectorsOptions selectors,
        DateTime businessDate)
    {
        _logger.LogInformation(
            "VirtualSports navigation step started. ClientCode={ClientCode}, Step={Step}.",
            clientCode,
            "Extract Rows");

        await WaitForRequiredAsync(page, selectors.ReportTable, "ReportTable");
        var tableRows = await page.Locator(selectors.ReportRows).AllAsync();
        var rows = new List<VirtualSportsImportRow>(tableRows.Count);

        foreach (var tableRow in tableRows)
        {
            var shopCode = await GetCellTextAsync(tableRow, selectors.ShopCodeCell, "ShopCodeCell");
            var shopName = await GetCellTextAsync(tableRow, selectors.ShopNameCell, "ShopNameCell");

            rows.Add(new VirtualSportsImportRow
            {
                ExternalShopCode = shopCode,
                ExternalShopName = string.IsNullOrWhiteSpace(shopName) ? shopCode : shopName,
                BusinessDate = businessDate.Date,
                Sales = ParseDecimal(await GetCellTextAsync(tableRow, selectors.TotalInCell, "TotalInCell")),
                Payout = ParseDecimal(await GetCellTextAsync(tableRow, selectors.TotalOutCell, "TotalOutCell")),
                TicketCount = ParseInt(await GetCellTextAsync(tableRow, selectors.TicketCountCell, "TicketCountCell"))
            });
        }

        _logger.LogInformation(
            "VirtualSports navigation step completed. ClientCode={ClientCode}, Step={Step}, Rows={RowCount}.",
            clientCode,
            "Extract Rows",
            rows.Count);

        return rows;
    }

    private static async Task FillRequiredAsync(
        IPage page,
        string selector,
        string value,
        string selectorName,
        CancellationToken cancellationToken)
    {
        await WaitForRequiredAsync(page, selector, selectorName);
        await page.FillAsync(selector, value);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static async Task FillDateAsync(
        IPage page,
        string selector,
        string value,
        string selectorName,
        string datePickerOkButtonSelector,
        CancellationToken cancellationToken)
    {
        await WaitForRequiredAsync(page, selector, selectorName);

        var locator = page.Locator(selector).First;
        await locator.ClickAsync();
        await SetDateInputValueAsync(page, selector, value, selectorName);
        await ClickDatePickerDayAsync(page, value);
        await CommitDatePickerAsync(page, datePickerOkButtonSelector);
        await page.WaitForTimeoutAsync(250);

        cancellationToken.ThrowIfCancellationRequested();
    }

    private static async Task SetDateInputValueAsync(
        IPage page,
        string selector,
        string value,
        string selectorName)
    {
        await WaitForRequiredAsync(page, selector, selectorName);

        var locator = page.Locator(selector).First;
        await locator.EvaluateAsync(
            @"(element, value) => {
                const input = element;
                const valueSetter = Object.getOwnPropertyDescriptor(window.HTMLInputElement.prototype, 'value')?.set;

                if (valueSetter) {
                    valueSetter.call(input, value);
                } else {
                    input.value = value;
                }

                input.dispatchEvent(new Event('input', { bubbles: true }));
                input.dispatchEvent(new Event('change', { bubbles: true }));
                input.dispatchEvent(new KeyboardEvent('keyup', { bubbles: true, key: 'Enter' }));
                input.dispatchEvent(new Event('blur', { bubbles: false }));
            }",
            value);
    }

    private static async Task ClickDatePickerDayAsync(IPage page, string value)
    {
        if (!DateTime.TryParseExact(
                value,
                "yyyy-MM-dd HH:mm",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var date))
        {
            return;
        }

        var day = date.Day.ToString(CultureInfo.InvariantCulture);
        var dayCell = page.Locator($"td:visible:text-is(\"{day}\")").Last;

        try
        {
            await dayCell.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 2000
            });
            await dayCell.ClickAsync(new LocatorClickOptions
            {
                Force = true
            });
        }
        catch (PlaywrightException)
        {
            // The JavaScript assignment still gives us a useful audit trail if the picker UI changes.
        }
        catch (TimeoutException)
        {
            // The JavaScript assignment still gives us a useful audit trail if the picker UI changes.
        }
    }

    private static async Task CommitDatePickerAsync(IPage page, string datePickerOkButtonSelector)
    {
        if (string.IsNullOrWhiteSpace(datePickerOkButtonSelector))
        {
            await page.Keyboard.PressAsync("Escape");
            return;
        }

        var okButton = page.Locator(datePickerOkButtonSelector).Last;

        try
        {
            await okButton.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 2000
            });
            await okButton.ClickAsync(new LocatorClickOptions
            {
                Force = true
            });
        }
        catch (PlaywrightException)
        {
            await page.Keyboard.PressAsync("Escape");
        }
    }

    private static async Task WaitForOptionalHiddenAsync(IPage page, string selector)
    {
        if (string.IsNullOrWhiteSpace(selector))
        {
            return;
        }

        try
        {
            await page.Locator(selector).First.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Hidden,
                Timeout = 45000
            });
        }
        catch (TimeoutException)
        {
            // Keep the saved audit artifacts useful even when a portal request hangs.
        }
        catch (PlaywrightException)
        {
            // Optional selectors should not hide the real extraction/configuration error.
        }
    }

    private static async Task<string> ReadInputValueAsync(
        IPage page,
        string selector,
        string selectorName)
    {
        await WaitForRequiredAsync(page, selector, selectorName);
        return await page.Locator(selector).First.InputValueAsync();
    }

    private static async Task ClickRequiredAsync(
        IPage page,
        string selector,
        string selectorName,
        CancellationToken cancellationToken,
        bool force = false)
    {
        await WaitForRequiredAsync(page, selector, selectorName);
        await page.Locator(selector).First.ClickAsync(new LocatorClickOptions
        {
            Force = force
        });
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static async Task SelectLoginRoleAsync(
        IPage page,
        VirtualSportsOptions options,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(options.LoginRoleSelect) &&
            string.IsNullOrWhiteSpace(options.LoginRoleValue))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(options.LoginRoleSelect) ||
            string.IsNullOrWhiteSpace(options.LoginRoleValue))
        {
            throw new InvalidOperationException(
                "VirtualSports LoginRoleSelect and LoginRoleValue must both be configured when login role selection is used.");
        }

        await WaitForRequiredAsync(page, options.LoginRoleSelect, "LoginRoleSelect");
        await page.SelectOptionAsync(options.LoginRoleSelect, new SelectOptionValue
        {
            Label = options.LoginRoleValue
        });
        cancellationToken.ThrowIfCancellationRequested();
    }

    private static bool TryGetConfiguredUrl(string selectorOrUrl, out string configuredUrl)
    {
        const string prefix = "url:";

        if (selectorOrUrl.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            configuredUrl = selectorOrUrl[prefix.Length..];
            return true;
        }

        configuredUrl = string.Empty;
        return false;
    }

    private static async Task NavigateToConfiguredUrlAsync(IPage page, string configuredUrl)
    {
        var currentUrl = new Uri(page.Url);
        var targetUrl = configuredUrl.StartsWith("#", StringComparison.Ordinal)
            ? new Uri(currentUrl.GetLeftPart(UriPartial.Path) + configuredUrl)
            : new Uri(currentUrl, configuredUrl);

        await page.GotoAsync(targetUrl.ToString(), new PageGotoOptions
        {
            WaitUntil = WaitUntilState.NetworkIdle
        });
    }

    private static async Task WaitForRequiredAsync(IPage page, string selector, string selectorName)
    {
        if (string.IsNullOrWhiteSpace(selector))
        {
            throw new InvalidOperationException(
                $"VirtualSports selector '{selectorName}' must be configured before it can be used.");
        }

        try
        {
            await page.Locator(selector).First.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible
            });
        }
        catch (TimeoutException ex)
        {
            throw new InvalidOperationException(
                $"VirtualSports selector '{selectorName}' was not found or visible. Selector: {selector}",
                ex);
        }
        catch (PlaywrightException ex)
        {
            throw new InvalidOperationException(
                $"VirtualSports selector '{selectorName}' lookup failed. Selector: {selector}",
                ex);
        }
    }

    private static async Task<string> GetCellTextAsync(
        ILocator tableRow,
        string selector,
        string selectorName)
    {
        try
        {
            var locator = tableRow.Locator(selector).First;
            await locator.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Attached
            });

            return (await locator.InnerTextAsync()).Trim();
        }
        catch (TimeoutException ex)
        {
            throw new InvalidOperationException(
                $"VirtualSports selector '{selectorName}' was not found in a report row. Selector: {selector}",
                ex);
        }
        catch (PlaywrightException ex)
        {
            throw new InvalidOperationException(
                $"VirtualSports selector '{selectorName}' lookup failed in a report row. Selector: {selector}",
                ex);
        }
    }

    private static async Task<bool> DetermineLoginSuccessAsync(
        IPage page,
        VirtualSportsOptions options,
        List<string> errors)
    {
        if (!string.IsNullOrWhiteSpace(options.LoginFailureSelector) &&
            await IsSelectorPresentAsync(page, options.LoginFailureSelector))
        {
            errors.Add(
                $"Login failure selector was found. Selector: {options.LoginFailureSelector}");
            return false;
        }

        if (!string.IsNullOrWhiteSpace(options.LoginSuccessSelector) &&
            await IsSelectorPresentAsync(page, options.LoginSuccessSelector))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(options.LoginSuccessUrlContains) &&
            page.Url.Contains(options.LoginSuccessUrlContains, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        errors.Add(
            "Login result is uncertain. Configure LoginSuccessSelector, LoginSuccessUrlContains, or LoginFailureSelector to make this deterministic.");
        return false;
    }

    private static async Task<bool> IsSelectorPresentAsync(IPage page, string selector)
    {
        try
        {
            await page.Locator(selector).First.WaitForAsync(new LocatorWaitForOptions
            {
                State = WaitForSelectorState.Visible,
                Timeout = 1000
            });

            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (PlaywrightException)
        {
            return false;
        }
    }

    private static decimal ParseDecimal(string value)
    {
        var cleaned = new string(value
            .Where(character => char.IsDigit(character) || character is '.' or ',' or '-')
            .ToArray());

        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return 0;
        }

        var lastComma = cleaned.LastIndexOf(',');
        var lastPeriod = cleaned.LastIndexOf('.');

        if (lastComma >= 0 && lastPeriod >= 0)
        {
            cleaned = lastComma > lastPeriod
                ? cleaned.Replace(".", string.Empty).Replace(',', '.')
                : cleaned.Replace(",", string.Empty);
        }
        else if (lastComma >= 0)
        {
            var decimalDigits = cleaned.Length - lastComma - 1;
            cleaned = decimalDigits == 2
                ? cleaned.Replace(',', '.')
                : cleaned.Replace(",", string.Empty);
        }

        return decimal.Parse(cleaned, NumberStyles.Number, AmountCulture);
    }

    private static int ParseInt(string value)
    {
        var cleaned = value.Replace(",", string.Empty).Trim();
        return int.Parse(cleaned, NumberStyles.Integer, AmountCulture);
    }

    private static async Task<AuditArtifactPaths> SaveAuditArtifactsAsync(
        IPage page,
        string auditPath,
        DateTime businessDate,
        string reason,
        CancellationToken cancellationToken)
    {
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
        var prefix = Path.Combine(auditPath, $"virtualsports-{businessDate:yyyyMMdd}-{reason}-{timestamp}");

        await page.ScreenshotAsync(new PageScreenshotOptions
        {
            Path = $"{prefix}.png",
            FullPage = true
        });

        var html = await page.ContentAsync();
        await File.WriteAllTextAsync($"{prefix}.html", html, cancellationToken);

        return new AuditArtifactPaths
        {
            ScreenshotPath = $"{prefix}.png",
            HtmlPath = $"{prefix}.html"
        };
    }

    private static void ValidateOptions(string clientCode, VirtualSportsOptions options)
    {
        ValidateLoginOptions(clientCode, options);

        ValidateSelector(clientCode, nameof(options.Selectors.GeneralOverviewMenu), options.Selectors.GeneralOverviewMenu);
        ValidateSelector(clientCode, nameof(options.Selectors.FromDate), options.Selectors.FromDate);
        ValidateSelector(clientCode, nameof(options.Selectors.ToDate), options.Selectors.ToDate);
        ValidateSelector(clientCode, nameof(options.Selectors.SearchButton), options.Selectors.SearchButton);
    }

    private static void ValidateLoginOptions(string clientCode, VirtualSportsOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.LoginUrl) || options.LoginUrl == "https://...")
        {
            throw new InvalidOperationException(
                $"Clients:{clientCode}:VirtualSports:LoginUrl must be configured before running imports.");
        }

        if (string.IsNullOrWhiteSpace(options.Username))
        {
            throw new InvalidOperationException(
                $"Clients:{clientCode}:VirtualSports:Username must be configured before running imports.");
        }

        if (string.IsNullOrWhiteSpace(options.Password))
        {
            throw new InvalidOperationException(
                $"Clients:{clientCode}:VirtualSports:Password must be configured before running imports.");
        }

        ValidateSelector(clientCode, nameof(options.Selectors.Username), options.Selectors.Username);
        ValidateSelector(clientCode, nameof(options.Selectors.Password), options.Selectors.Password);
        ValidateSelector(clientCode, nameof(options.Selectors.LoginButton), options.Selectors.LoginButton);
    }

    private static void ValidateReportPageSnapshotOptions(string clientCode, VirtualSportsOptions options)
    {
        ValidateLoginOptions(clientCode, options);
        ValidateSelector(clientCode, nameof(options.Selectors.GeneralOverviewMenu), options.Selectors.GeneralOverviewMenu);
    }

    private static void ValidateSelector(string clientCode, string selectorName, string selector)
    {
        if (string.IsNullOrWhiteSpace(selector))
        {
            throw new InvalidOperationException(
                $"Clients:{clientCode}:VirtualSports:Selectors:{selectorName} must be configured before running imports.");
        }
    }
}

public sealed class SelectorDiscoveryResult
{
    public bool Success { get; init; }

    public string ClientCode { get; init; } = string.Empty;

    public string PageTitle { get; init; } = string.Empty;

    public string CurrentUrl { get; init; } = string.Empty;

    public string ScreenshotPath { get; init; } = string.Empty;

    public string HtmlPath { get; init; } = string.Empty;

    public IReadOnlyList<string> Errors { get; init; } = [];
}

public sealed class LoginTestResult
{
    public bool Success { get; init; }

    public string ClientCode { get; init; } = string.Empty;

    public string PageTitle { get; init; } = string.Empty;

    public string CurrentUrl { get; init; } = string.Empty;

    public string ScreenshotPath { get; init; } = string.Empty;

    public string HtmlPath { get; init; } = string.Empty;

    public IReadOnlyList<string> Errors { get; init; } = [];
}

public sealed class VirtualSportsScrapeResult
{
    public IReadOnlyCollection<VirtualSportsImportRow> Rows { get; init; } = [];

    public string FromDateValue { get; init; } = string.Empty;

    public string ToDateValue { get; init; } = string.Empty;

    public string GeneratedReportScreenshotPath { get; init; } = string.Empty;

    public string GeneratedReportHtmlPath { get; init; } = string.Empty;
}

public sealed class VirtualSportsScrapeException : Exception
{
    public VirtualSportsScrapeException(
        string message,
        Exception innerException,
        string fromDateValue,
        string toDateValue,
        string generatedReportScreenshotPath,
        string generatedReportHtmlPath)
        : base(message, innerException)
    {
        FromDateValue = fromDateValue;
        ToDateValue = toDateValue;
        GeneratedReportScreenshotPath = generatedReportScreenshotPath;
        GeneratedReportHtmlPath = generatedReportHtmlPath;
    }

    public string FromDateValue { get; }

    public string ToDateValue { get; }

    public string GeneratedReportScreenshotPath { get; }

    public string GeneratedReportHtmlPath { get; }
}

public sealed class DateRangeSelectionResult
{
    public string FromDateValue { get; init; } = string.Empty;

    public string ToDateValue { get; init; } = string.Empty;
}

public sealed class AuditArtifactPaths
{
    public string ScreenshotPath { get; init; } = string.Empty;

    public string HtmlPath { get; init; } = string.Empty;
}
