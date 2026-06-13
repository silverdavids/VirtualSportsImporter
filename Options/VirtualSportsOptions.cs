namespace VirtualSportsImporter.Worker.Options;

public sealed class VirtualSportsOptions
{
    public const string SectionName = "VirtualSports";

    public string LoginUrl { get; set; } = string.Empty;

    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public bool Headless { get; set; } = true;

    public int TimeoutSeconds { get; set; } = 60;

    public string AuditPath { get; set; } = @"C:\ProductImports\VirtualSports";

    public string LoginSuccessUrlContains { get; set; } = string.Empty;

    public string LoginSuccessSelector { get; set; } = string.Empty;

    public string LoginFailureSelector { get; set; } = string.Empty;

    public string LoginRoleSelect { get; set; } = string.Empty;

    public string LoginRoleValue { get; set; } = string.Empty;

    public VirtualSportsSelectorsOptions Selectors { get; set; } = new();
}

public sealed class VirtualSportsSelectorsOptions
{
    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    public string LoginButton { get; set; } = string.Empty;

    public string GeneralOverviewMenu { get; set; } = string.Empty;

    public string ExpandShopsNodeSelector { get; set; } = string.Empty;

    public string FromDate { get; set; } = string.Empty;

    public string ToDate { get; set; } = string.Empty;

    public string DatePickerOkButton { get; set; } = string.Empty;

    public string SearchButton { get; set; } = string.Empty;

    public string ReportLoadingIndicator { get; set; } = string.Empty;

    public string ReportTable { get; set; } = string.Empty;

    public string ReportRows { get; set; } = string.Empty;

    public string ShopCodeCell { get; set; } = string.Empty;

    public string ShopNameCell { get; set; } = string.Empty;

    public string TicketCountCell { get; set; } = string.Empty;

    public string TotalInCell { get; set; } = string.Empty;

    public string TotalOutCell { get; set; } = string.Empty;
}
