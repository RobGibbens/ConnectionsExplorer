# ConnectionsExplorer

A .NET 10 CLI tool that scans Azure Key Vault secrets and Azure DevOps repositories to help track down incoming connections to specified services. It searches secret names, secret values, and code in DevOps repos for user-supplied terms, then optionally generates a Markdown report using GitHub Copilot.

## Features

- **Subscription Discovery** — Lists all Azure subscriptions available to the logged-in user and allows selection by name or ID.
- **Key Vault Scanning** — Discovers Key Vaults in resource groups containing "QA" or "Staging", then searches secret names and values for the given terms.
- **Azure DevOps Code Search** — Searches `*.json` files across all repositories in an Azure DevOps organization for the same terms.
- **Copilot Report Generation** — Uses the GitHub Copilot SDK to produce a well-formatted Markdown summary of all search results.
- **Interactive Loop** — Supports repeated searches without re-fetching vault or repo metadata.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli) — used for authentication
- [GitHub Copilot CLI extension](#setting-up-github-copilot) — required for report generation

## Setting Up GitHub Copilot

The tool uses the [GitHub Copilot SDK](https://www.nuget.org/packages/GitHub.Copilot.SDK) (`GitHub.Copilot.SDK`) to generate Markdown reports. The SDK communicates with a local Copilot agent that must be running.

1. **Install the GitHub Copilot CLI extension**

   ```bash
   gh extension install github/gh-copilot
   ```

   > If you don't have the `gh` CLI, install it first: https://cli.github.com

2. **Authenticate with GitHub**

   ```bash
   gh auth login
   ```

   Ensure the account you log in with has an active GitHub Copilot subscription (Individual, Business, or Enterprise).

3. **Verify Copilot is available**

   ```bash
   gh copilot --version
   ```

If Copilot is not set up or not reachable at runtime, the tool will still complete all searches — the report generation step will simply be skipped with a warning.

## Authentication

All Azure access is performed through the **Azure CLI credential** (`AzureCliCredential`). Make sure you are logged in before running the tool:

```bash
az login
```

For Azure DevOps access the tool requests a token scoped to the Azure DevOps resource ID (`499b84ac-1321-427f-aa17-267ca6975798`). Your Azure AD / Entra ID account must have access to the target DevOps organization.

## Building

```bash
cd ConnectionsExplorer
dotnet build
```

## Running

```bash
dotnet run --project ConnectionsExplorer [subscription] [devOpsOrg] [searchTerms]
```

All three arguments are optional. When omitted, the tool will prompt interactively.

| Position | Argument | Description |
|----------|----------|-------------|
| 1 | `subscription` | Subscription name (partial match) or ID. If omitted, an interactive picker is shown. |
| 2 | `devOpsOrg` | Azure DevOps organization name. Pass `skip` to skip DevOps search. If omitted, prompted. |
| 3 | `searchTerms` | Comma-separated search terms used for the first search iteration. If omitted, prompted. |

### Examples

**Fully interactive** — prompted for subscription, DevOps org, and search terms:

```bash
dotnet run --project ConnectionsExplorer
```

**Specify subscription and DevOps org, prompt for search terms:**

```bash
dotnet run --project ConnectionsExplorer "My Subscription" "myorg"
```

**Fully non-interactive first search** — search for two terms across a subscription and DevOps org:

```bash
dotnet run --project ConnectionsExplorer "My Subscription" "myorg" "redis,sqlserver"
```

**Skip Azure DevOps search entirely:**

```bash
dotnet run --project ConnectionsExplorer "My Subscription" "skip" "redis"
```

After the first search completes, the tool enters an interactive loop where you can run additional searches or type `quit` to exit.

## Output

- **Console** — Results are displayed in formatted tables (Key Vault matches, DevOps code matches, and any errors/warnings).
- **Markdown Report** — When Copilot is available, a `.md` file is written to the current directory named `<searchTerms>-<timestamp>.md`.

## License

See [LICENSE](LICENSE) for details.
