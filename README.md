# VirtualSportsImporter

## Local configuration

`appsettings.json` is a safe template for source control. Do not put real portal credentials, API keys, client URLs, or client-specific selector values in it.

Create a local override file for real deployment/test values:

```bash
copy appsettings.Local.json.example appsettings.Local.json
```

Then fill `appsettings.Local.json` with the real worker API key, client API settings, VirtualSports credentials, report timezone, and portal selectors. `appsettings.Local.json` is ignored by Git so secrets stay local.

## Manual Import API / OpenAPI Examples

Trigger a dry-run import with:

```http
POST /imports/virtualsports/run
X-Worker-Api-Key: {worker-api-key}
Content-Type: application/json
```

Legacy business-date mode remains supported:

```json
{
  "clientCode": "EXAMPLE_CLIENT",
  "businessDate": "2026-06-12",
  "dryRun": true
}
```

Yesterday mode imports from yesterday at `00:00` to today at `00:00`:

```json
{
  "clientCode": "EXAMPLE_CLIENT",
  "period": "yesterday",
  "dryRun": true
}
```

Today mode imports from today at `00:00` in `VirtualSports:ReportTimeZone` to the current report-timezone hour rounded down:

```json
{
  "clientCode": "EXAMPLE_CLIENT",
  "period": "today",
  "dryRun": true
}
```

Custom mode imports from `fromDate 00:00` to `toDate 00:00`:

```json
{
  "clientCode": "EXAMPLE_CLIENT",
  "period": "custom",
  "fromDate": "2026-06-01",
  "toDate": "2026-06-13",
  "dryRun": true
}
```

The response includes the resolved `period`, `businessDate`, legacy `fromDateValue`/`toDateValue`, plus `requestedFromDateValue`, `requestedToDateValue`, `actualFromDateValue`, `actualToDateValue`, `portalAvailabilityMessage`, and `retriedWithAvailableRange`. If the portal reports that data is available only for a smaller range, the worker retries once with that available range and continues extraction.
