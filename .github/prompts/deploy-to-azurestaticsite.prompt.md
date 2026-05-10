---
description: "Deploy Everything Disk Usage to the Azure Static Site. Builds, packages, uploads ZIP to the private downloads container, and updates ONLY the Everything Disk Usage card in the shared index.html. Use when: deploy to static site, publish to azure, upload to download site, push release."
mode: "agent"
---

# Deploy to Azure Static Site

Run the publish script from the workspace root:

```powershell
& "$PSScriptRoot\..\..\scripts\publish-to-azure.ps1"
```

## Rules

- Do NOT modify the script before running it.
- The script handles everything: build, package, ZIP upload to the private `downloads` container, and targeted `index.html` update.
- It will only add or replace the Everything Disk Usage card in the shared `index.html` at `D:\installationSite\index.html`.
- Do not remove, reorder, or regenerate the existing app tiles in `index.html`.
- All projects sharing the download site use `D:\installationSite\index.html` as the single source of truth.
- **Never modify the auth gate**: the login overlay (`#login-overlay`), Google script tags, and auth JavaScript must remain intact.
- Binaries go to the private `downloads` container, NOT `$web`. The auth JavaScript generates SAS download URLs at runtime.
- Download links use `data-blob="filename"` attributes. Never hard-code direct blob URLs.
- Requires the `az` CLI to be authenticated with permission to list keys for storage account `installmonitordl` in resource group `rg-installmonitor-download`.
- The script uses explicit storage account key auth for blob operations and includes built-in retry handling for transient Azure upload/download failures.
- If it fails after the script's built-in retries, show the full error output to the user and do not keep rerunning blindly.
