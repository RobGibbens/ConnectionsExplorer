Report saved to your session files. Here's the content:

---

# Connection Search Report

**Generated:** 2026-03-16
**Search Terms:** `qanafxencompassappsfunctions`, `nafxencompassappsfunctionsnew`

## Summary

No matching results were found in either Azure Key Vault Secrets or Azure DevOps repo code. One Key Vault secret could not be accessed due to a permissions error.

## Key Vault — Access Errors

| Vault | Secret | Error |
|---|---|---|
| NAF-LS-QA-KeyVault | EncompassFunctionsKey | Secrets permission denied |

## Recommendations

1. **Resolve Key Vault permissions** — Request access to read `EncompassFunctionsKey` in `NAF-LS-QA-KeyVault`.
2. **Expand search scope** — Try partial terms like `encompassapps` or `encompassfunctions`.
3. **Check additional sources** — App Service config, Function App settings, and API Management policies may hold references not in Key Vault or code.