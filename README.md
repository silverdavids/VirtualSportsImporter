# VirtualSportsImporter

## Local configuration

`appsettings.json` is a safe template for source control. Do not put real portal credentials, API keys, client URLs, or client-specific selector values in it.

Create a local override file for real deployment/test values:

```bash
copy appsettings.Local.json.example appsettings.Local.json
```

Then fill `appsettings.Local.json` with the real worker API key, client API settings, VirtualSports credentials, and portal selectors. `appsettings.Local.json` is ignored by Git so secrets stay local.
