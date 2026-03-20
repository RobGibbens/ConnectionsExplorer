using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.KeyVault;
using Azure.Security.KeyVault.Secrets;
using Spectre.Console;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using GitHub.Copilot.SDK;

var subscriptionArg = args.Length > 0 ? args[0] : null;
var devOpsOrgArg = args.Length > 1 ? args[1] : null;
var searchArg = args.Length > 2 ? args[2] : null;
var searchArgFromCli = searchArg is not null;

// Main menu: choose search mode
var searchMode = searchArgFromCli
    ? "Search for string"
    : AnsiConsole.Prompt(
        new SelectionPrompt<string>()
            .Title("Select a [green]search mode[/]:")
            .AddChoices("Search for string", "API endpoint search"));

if (searchMode == "API endpoint search")
{
    await RunApiEndpointSearchAsync();
    return;
}

if (searchArg is null)
{
    searchArg = AnsiConsole.Ask<string>("Enter the [green]partial string[/] to search for (comma-separated for multiple, or [grey]\"quit\"[/] to exit):");
    if (searchArg.Equals("quit", StringComparison.OrdinalIgnoreCase) ||
        searchArg.Equals("exit", StringComparison.OrdinalIgnoreCase))
    {
        AnsiConsole.MarkupLine("[grey]Goodbye![/]");
        return;
    }
}

var credential = new AzureCliCredential();
var armClient = new ArmClient(credential);
using var httpClient = new HttpClient();
var vaultCacheDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "ConnectionsExplorer");

// Discover and select a subscription
var subscriptions = await LoadSubscriptionsAsync();
if (subscriptions.Count == 0)
{
    AnsiConsole.MarkupLine("[yellow]No subscriptions found. Ensure you are logged in via 'az login'.[/]");
    return;
}

var selectedSubscription = SelectSubscription(subscriptions, subscriptionArg);
if (selectedSubscription is null)
    return;

// Discover Key Vaults in the selected subscription (with local cache)
var vaults = await LoadCachedVaultsAsync(selectedSubscription.Data.SubscriptionId);
if (vaults is null)
{
    vaults = await DiscoverKeyVaultsAsync(selectedSubscription);
    await SaveVaultsCacheAsync(selectedSubscription.Data.SubscriptionId, vaults);
}
else
{
    AnsiConsole.MarkupLine("[grey]Using cached Key Vault list (less than 1 hour old).[/]");
}
AnsiConsole.MarkupLine($"Found [green]{vaults.Count}[/] Key Vault(s).");

// Set up Azure DevOps
AnsiConsole.WriteLine();
var (devOpsOrg, searchDevOps) = ResolveDevOpsOrganization(devOpsOrgArg);
var devOpsRepos = new List<(string Project, string Repo, string DefaultBranch)>();
if (searchDevOps)
{
    (devOpsRepos, searchDevOps) = await DiscoverDevOpsReposAsync(devOpsOrg);
    if (searchDevOps)
        AnsiConsole.MarkupLine($"Found [green]{devOpsRepos.Count}[/] Azure DevOps repo(s) in organization [blue]{Markup.Escape(devOpsOrg)}[/].");
}

if (vaults.Count == 0 && !searchDevOps)
{
    AnsiConsole.MarkupLine("[yellow]No Key Vaults or DevOps repos available. Ensure you have access and are logged in via 'az login'.[/]");
    return;
}

// Pre-create SecretClients and cache secret names per vault
var secretClients = CreateSecretClients(vaults);
var (cachedSecretNames, deniedVaultNames) = await LoadSecretNamesAsync(vaults, secretClients);

// Remove vaults we don't have access to from the list and update the cache
if (deniedVaultNames.Count > 0)
{
    vaults.RemoveAll(v => deniedVaultNames.Contains(v.Name));
    foreach (var name in deniedVaultNames)
    {
        secretClients.Remove(name);
        cachedSecretNames.Remove(name);
    }
    await SaveVaultsCacheAsync(selectedSubscription.Data.SubscriptionId, vaults);
    AnsiConsole.MarkupLine($"[yellow]Removed {deniedVaultNames.Count} inaccessible vault(s) from cache.[/]");
}

// Interactive search loop
var allSearchSummaries = new List<string>();
var firstRun = true;
while (true)
{
    AnsiConsole.WriteLine();
    var searchTerms = PromptForSearchTerms(searchArg, firstRun);
    if (searchTerms is null)
        break;
    var wasCliSearch = firstRun && searchArgFromCli;
    firstRun = false;
    var searchDisplay = string.Join(", ", searchTerms);

    var (results, errors) = await ScanAllVaultsAsync(vaults, cachedSecretNames, searchTerms, secretClients);
    DisplayVaultResults(results, searchDisplay);
    DisplayErrors(errors);

    var devOpsResults = new List<(string Project, string Repo, string FilePath, string Branch)>();
    if (searchDevOps)
    {
        devOpsResults = await SearchDevOpsCodeAsync(devOpsOrg, searchTerms);
        DisplayDevOpsResults(devOpsResults, searchDisplay);
    }

    allSearchSummaries.Add(BuildSearchSummary(searchDisplay, results, errors, devOpsResults));

    if (wasCliSearch)
        break;
}

// Generate Copilot markdown report
if (allSearchSummaries.Count > 0)
    await GenerateCopilotReportAsync(allSearchSummaries, searchArg);

return;

// ─────────────────────────────── Subscription Discovery ───────────────────────────────

async Task<List<Azure.ResourceManager.Resources.SubscriptionResource>> LoadSubscriptionsAsync()
{
    var subs = new List<Azure.ResourceManager.Resources.SubscriptionResource>();
    await AnsiConsole.Status()
        .Spinner(Spinner.Known.Dots)
        .StartAsync("Loading subscriptions...", async ctx =>
        {
            await foreach (var sub in armClient.GetSubscriptions().GetAllAsync())
            {
                subs.Add(sub);
            }
        });
    return subs;
}

Azure.ResourceManager.Resources.SubscriptionResource? SelectSubscription(
    List<Azure.ResourceManager.Resources.SubscriptionResource> subscriptions,
    string? arg)
{
    if (arg is not null)
    {
        var match = subscriptions.FirstOrDefault(s =>
            s.Data.DisplayName.Contains(arg, StringComparison.OrdinalIgnoreCase)
            || s.Data.SubscriptionId.Equals(arg, StringComparison.OrdinalIgnoreCase));

        if (match is null)
        {
            AnsiConsole.MarkupLine($"[red]No subscription matching '{Markup.Escape(arg)}' was found.[/]");
            return null;
        }

        AnsiConsole.MarkupLine($"Using subscription: [green]{Markup.Escape(match.Data.DisplayName)}[/] ({match.Data.SubscriptionId})");
        return match;
    }

    return AnsiConsole.Prompt(
        new SelectionPrompt<Azure.ResourceManager.Resources.SubscriptionResource>()
            .Title("Select a [green]subscription[/] to scan:")
            .PageSize(15)
            .UseConverter(s => $"{s.Data.DisplayName} ({s.Data.SubscriptionId})")
            .AddChoices(subscriptions));
}

// ─────────────────────────────── Key Vault Cache ───────────────────────────────

async Task<List<(string Name, Uri VaultUri)>?> LoadCachedVaultsAsync(string subscriptionId)
{
    var cacheFile = Path.Combine(vaultCacheDir, $"vaults-{subscriptionId}.json");
    if (!File.Exists(cacheFile))
        return null;

    try
    {
        var json = await File.ReadAllTextAsync(cacheFile);
        var cache = JsonSerializer.Deserialize<VaultCacheEntry>(json);
        if (cache is null || DateTime.UtcNow - cache.Timestamp > TimeSpan.FromHours(1))
            return null;

        return cache.Vaults
            .Select(v => (v.Name, new Uri(v.VaultUri)))
            .ToList();
    }
    catch
    {
        return null;
    }
}

async Task SaveVaultsCacheAsync(string subscriptionId, List<(string Name, Uri VaultUri)> vaults)
{
    try
    {
        Directory.CreateDirectory(vaultCacheDir);
        var cacheFile = Path.Combine(vaultCacheDir, $"vaults-{subscriptionId}.json");
        var entry = new VaultCacheEntry
        {
            Timestamp = DateTime.UtcNow,
            Vaults = vaults.Select(v => new VaultCacheItem { Name = v.Name, VaultUri = v.VaultUri.ToString() }).ToList()
        };
        var json = JsonSerializer.Serialize(entry, new JsonSerializerOptions { WriteIndented = true });
        await File.WriteAllTextAsync(cacheFile, json);
    }
    catch
    {
        // Best-effort caching; don't fail the run.
    }
}

// ─────────────────────────────── Key Vault Discovery ───────────────────────────────

async Task<List<(string Name, Uri VaultUri)>> DiscoverKeyVaultsAsync(
    Azure.ResourceManager.Resources.SubscriptionResource subscription)
{
    var found = new List<(string Name, Uri VaultUri)>();
    await AnsiConsole.Status()
        .Spinner(Spinner.Known.Dots)
        .StartAsync($"Discovering Key Vaults in subscription [blue]{Markup.Escape(subscription.Data.DisplayName)}[/]...", async ctx =>
        {
            try
            {
                await foreach (var vault in subscription.GetKeyVaultsAsync())
                {
                    var resourceGroupName = vault.Id.ResourceGroupName;
                    if (resourceGroupName is null ||
                        (!resourceGroupName.Contains("QA", StringComparison.OrdinalIgnoreCase) &&
                         !resourceGroupName.Contains("Staging", StringComparison.OrdinalIgnoreCase)))
                    {
                        AnsiConsole.MarkupLine($"  Skipping vault: [grey]{Markup.Escape(vault.Data.Name)}[/] (RG: {Markup.Escape(resourceGroupName ?? "unknown")})");
                        continue;
                    }

                    var vaultUri = vault.Data.Properties.VaultUri;
                    if (vaultUri is not null)
                    {
                        found.Add((vault.Data.Name, vaultUri));
                        AnsiConsole.MarkupLine($"  Found vault: [green]{Markup.Escape(vault.Data.Name)}[/] (RG: {Markup.Escape(resourceGroupName)})");
                    }
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]Warning:[/] Could not enumerate vaults in subscription [blue]{Markup.Escape(subscription.Data.DisplayName)}[/]: {Markup.Escape(ex.Message)}");
            }
        });
    return found;
}

Dictionary<string, SecretClient> CreateSecretClients(List<(string Name, Uri VaultUri)> vaults)
{
    var options = new SecretClientOptions { Retry = { MaxRetries = 0 } };
    var clients = new Dictionary<string, SecretClient>();
    foreach (var vault in vaults)
        clients[vault.Name] = new SecretClient(vault.VaultUri, credential, options);
    return clients;
}

async Task<(Dictionary<string, List<string>> SecretNames, List<string> DeniedVaults)> LoadSecretNamesAsync(
    List<(string Name, Uri VaultUri)> vaults,
    Dictionary<string, SecretClient> clients)
{
    var cached = new Dictionary<string, List<string>>();
    var denied = new List<string>();
    await AnsiConsole.Status()
        .Spinner(Spinner.Known.Dots)
        .StartAsync("Loading secret names from all vaults...", async ctx =>
        {
            foreach (var vault in vaults)
            {
                ctx.Status($"Loading secrets from [cyan]{Markup.Escape(vault.Name)}[/]...");
                var names = new List<string>();
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                try
                {
                    await foreach (var secretProperties in clients[vault.Name].GetPropertiesOfSecretsAsync(cts.Token))
                    {
                        names.Add(secretProperties.Name);
                    }
                }
                catch (Azure.RequestFailedException ex) when (ex.Status == 403)
                {
                    AnsiConsole.MarkupLine($"  [yellow]Skipping[/] [cyan]{Markup.Escape(vault.Name)}[/]: Secrets permission denied");
                    denied.Add(vault.Name);
                }
                catch (OperationCanceledException)
                {
                    AnsiConsole.MarkupLine($"  [yellow]Skipping[/] [cyan]{Markup.Escape(vault.Name)}[/]: Timed out");
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"  [yellow]Skipping[/] [cyan]{Markup.Escape(vault.Name)}[/]: {Markup.Escape(ex.Message)}");
                }
                cached[vault.Name] = names;
                AnsiConsole.MarkupLine($"  [green]{Markup.Escape(vault.Name)}[/]: {names.Count} secret(s)");
            }
        });
    return (cached, denied);
}

// ─────────────────────────────── Azure DevOps Discovery ───────────────────────────────

(string Org, bool ShouldSearch) ResolveDevOpsOrganization(string? arg)
{
    string org;
    if (arg is not null)
    {
        org = arg;
        AnsiConsole.MarkupLine($"Using Azure DevOps organization: [green]{Markup.Escape(org)}[/]");
    }
    else
    {
        org = AnsiConsole.Ask<string>("Enter your Azure DevOps [green]organization name[/] (or [grey]\"skip\"[/] to skip DevOps search):");
    }
    return (org, !org.Equals("skip", StringComparison.OrdinalIgnoreCase));
}

async Task<(List<(string Project, string Repo, string DefaultBranch)> Repos, bool Success)> DiscoverDevOpsReposAsync(string org)
{
    var repos = new List<(string Project, string Repo, string DefaultBranch)>();
    var success = true;

    await AnsiConsole.Status()
        .Spinner(Spinner.Known.Dots)
        .StartAsync("Discovering Azure DevOps repositories...", async ctx =>
        {
            try
            {
                var token = await credential.GetTokenAsync(
                    new Azure.Core.TokenRequestContext(["499b84ac-1321-427f-aa17-267ca6975798/.default"]));
                httpClient.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", token.Token);

                var projectsUrl = $"https://dev.azure.com/{Uri.EscapeDataString(org)}/_apis/projects?api-version=7.1&$top=500";
                var projectsJson = await httpClient.GetFromJsonAsync<JsonObject>(projectsUrl);

                if (projectsJson?["value"] is JsonArray projects)
                {
                    foreach (var project in projects)
                    {
                        var projectName = project?["name"]?.GetValue<string>();
                        if (projectName is null) continue;

                        ctx.Status($"Listing repos in project [cyan]{Markup.Escape(projectName)}[/]...");
                        var reposUrl = $"https://dev.azure.com/{Uri.EscapeDataString(org)}/{Uri.EscapeDataString(projectName)}/_apis/git/repositories?api-version=7.1";
                        var reposJson = await httpClient.GetFromJsonAsync<JsonObject>(reposUrl);

                        if (reposJson?["value"] is JsonArray repoArray)
                        {
                            foreach (var repo in repoArray)
                            {
                                var repoName = repo?["name"]?.GetValue<string>();
                                var defaultBranch = repo?["defaultBranch"]?.GetValue<string>()?.Replace("refs/heads/", "");
                                if (repoName is not null && defaultBranch is not null)
                                {
                                    repos.Add((projectName, repoName, defaultBranch));
                                    AnsiConsole.MarkupLine($"  Found repo: [green]{Markup.Escape(projectName)}/{Markup.Escape(repoName)}[/] (branch: {Markup.Escape(defaultBranch)})");
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]Warning:[/] Could not connect to Azure DevOps: {Markup.Escape(ex.Message)}");
                success = false;
            }
        });

    return (repos, success);
}

async Task<List<(string Project, string Repo, string FilePath, string Branch)>> SearchDevOpsCodeAsync(
    string org,
    List<string> searchTerms)
{
    var devOpsResults = new List<(string Project, string Repo, string FilePath, string Branch)>();

    var extensions = new[] { "json", "ts", "config", "cs", "js" };

    await AnsiConsole.Status()
        .Spinner(Spinner.Known.Dots)
        .StartAsync("Searching Azure DevOps repos for *.json, *.ts, *.config, *.cs files...", async ctx =>
        {
            try
            {
                var token = await credential.GetTokenAsync(
                    new Azure.Core.TokenRequestContext(["499b84ac-1321-427f-aa17-267ca6975798/.default"]));
                httpClient.DefaultRequestHeaders.Authorization =
                    new AuthenticationHeaderValue("Bearer", token.Token);

                foreach (var ext in extensions)
                {
                    ctx.Status($"Searching Azure DevOps repos for [cyan]*.{ext}[/] files...");

                    var searchText = searchTerms.Count == 1
                        ? $"{searchTerms[0]} ext:{ext}"
                        : $"({string.Join(" OR ", searchTerms)}) ext:{ext}";

                    var searchRequest = new JsonObject
                    {
                        ["searchText"] = searchText,
                        ["$skip"] = 0,
                        ["$top"] = 1000,
                        ["filters"] = new JsonObject(),
                        ["includeFacets"] = false
                    };

                    var response = await httpClient.PostAsJsonAsync(
                        $"https://almsearch.dev.azure.com/{Uri.EscapeDataString(org)}/_apis/search/codesearchresults?api-version=7.1-preview.1",
                        searchRequest);

                    if (response.IsSuccessStatusCode)
                    {
                        var result = await response.Content.ReadFromJsonAsync<JsonObject>();
                        if (result?["results"] is JsonArray hits)
                        {
                            foreach (var hit in hits)
                            {
                                var filePath = hit?["path"]?.GetValue<string>() ?? "";
                                var project = hit?["project"]?["name"]?.GetValue<string>() ?? "";
                                var repo = hit?["repository"]?["name"]?.GetValue<string>() ?? "";
                                var branch = hit?["versions"] is JsonArray versions && versions.Count > 0
                                    ? versions[0]?["branchName"]?.GetValue<string>() ?? ""
                                    : "";
                                devOpsResults.Add((project, repo, filePath, branch));
                            }
                        }
                    }
                    else
                    {
                        var errorBody = await response.Content.ReadAsStringAsync();
                        AnsiConsole.MarkupLine($"[yellow]Azure DevOps search failed for *.{ext}:[/] {Markup.Escape(response.StatusCode.ToString())}");
                        AnsiConsole.MarkupLine($"[grey]{Markup.Escape(errorBody)}[/]");
                    }
                }
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[yellow]Azure DevOps search error:[/] {Markup.Escape(ex.Message)}");
            }
        });

    return devOpsResults;
}

// ─────────────────────────────── Vault Scanning ───────────────────────────────

List<string>? PromptForSearchTerms(string? searchArg, bool firstRun)
{
    if (firstRun && searchArg is not null)
    {
        var terms = searchArg.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        AnsiConsole.MarkupLine($"Searching for: [green]{Markup.Escape(string.Join(", ", terms))}[/]");
        return terms;
    }

    var input = AnsiConsole.Ask<string>("Enter the [green]partial string[/] to search for (comma-separated for multiple, or [grey]\"quit\"[/] to exit):");
    if (input.Equals("quit", StringComparison.OrdinalIgnoreCase) ||
        input.Equals("exit", StringComparison.OrdinalIgnoreCase))
    {
        AnsiConsole.MarkupLine("[grey]Goodbye![/]");
        return null;
    }
    return input.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}

async Task<(ConcurrentBag<(string VaultName, string SecretName, string MatchedOn)> Results,
            ConcurrentBag<(string VaultName, string SecretName, string Message)> Errors)>
    ScanAllVaultsAsync(
        List<(string Name, Uri VaultUri)> vaults,
        Dictionary<string, List<string>> cachedNames,
        List<string> searchTerms,
        Dictionary<string, SecretClient> clients)
{
    var results = new ConcurrentBag<(string VaultName, string SecretName, string MatchedOn)>();
    var errors = new ConcurrentBag<(string VaultName, string SecretName, string Message)>();

    await AnsiConsole.Progress()
        .AutoClear(false)
        .HideCompleted(false)
        .Columns(
            new TaskDescriptionColumn(),
            new ProgressBarColumn(),
            new PercentageColumn(),
            new SpinnerColumn())
        .StartAsync(async ctx =>
        {
            foreach (var vault in vaults)
            {
                var secrets = cachedNames[vault.Name];
                var progressTask = ctx.AddTask($"[cyan]{Markup.Escape(vault.Name)}[/]", maxValue: Math.Max(secrets.Count, 1));
                await ScanSingleVaultAsync(vault, secrets, searchTerms, progressTask, results, errors, clients);
            }
        });

    return (results, errors);
}

async Task ScanSingleVaultAsync(
    (string Name, Uri VaultUri) vault,
    List<string> secrets,
    List<string> searchTerms,
    ProgressTask progressTask,
    ConcurrentBag<(string VaultName, string SecretName, string MatchedOn)> results,
    ConcurrentBag<(string VaultName, string SecretName, string Message)> errors,
    Dictionary<string, SecretClient> clients)
{
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    try
    {
        var client = clients[vault.Name];

        foreach (var secretName in secrets)
        {
            try
            {
                if (searchTerms.Any(t => secretName.Contains(t, StringComparison.OrdinalIgnoreCase)))
                {
                    results.Add((vault.Name, secretName, "Name"));
                }

                var secret = await client.GetSecretAsync(secretName, cancellationToken: cts.Token);
                if (secret.Value.Value is not null &&
                    searchTerms.Any(t => secret.Value.Value.Contains(t, StringComparison.OrdinalIgnoreCase)))
                {
                    results.Add((vault.Name, secretName, "Value"));
                }
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 403)
            {
                errors.Add((vault.Name, secretName, "Secrets permission denied"));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                errors.Add((vault.Name, secretName, ex.Message));
            }

            progressTask.Increment(1);
        }

        if (secrets.Count == 0)
        {
            progressTask.Increment(1);
        }

        progressTask.StopTask();
    }
    catch (OperationCanceledException)
    {
        errors.Add((vault.Name, "", "Timed out"));
        progressTask.Increment(progressTask.MaxValue);
        progressTask.StopTask();
    }
    catch (Exception ex)
    {
        errors.Add((vault.Name, "", ex.Message));
        progressTask.Increment(progressTask.MaxValue);
        progressTask.StopTask();
    }
}

// ─────────────────────────────── Display Results ───────────────────────────────

void DisplayVaultResults(
    ConcurrentBag<(string VaultName, string SecretName, string MatchedOn)> results,
    string searchDisplay)
{
    AnsiConsole.WriteLine();

    if (results.IsEmpty)
    {
        AnsiConsole.MarkupLine($"[yellow]No secrets matched the search term(s)[/] [red]{Markup.Escape(searchDisplay)}[/].");
        return;
    }

    var table = new Table()
        .Border(TableBorder.Rounded)
        .Title($"[bold green]Search Results for \"{Markup.Escape(searchDisplay)}\"[/]")
        .AddColumn(new TableColumn("[bold]Key Vault[/]").Centered())
        .AddColumn(new TableColumn("[bold]Secret Name[/]").Centered())
        .AddColumn(new TableColumn("[bold]Matched On[/]").Centered());

    foreach (var result in results.OrderBy(r => r.VaultName).ThenBy(r => r.SecretName))
    {
        var matchColor = result.MatchedOn == "Value" ? "red" : "green";
        table.AddRow(
            Markup.Escape(result.VaultName),
            Markup.Escape(result.SecretName),
            $"[{matchColor}]{result.MatchedOn}[/]");
    }

    AnsiConsole.Write(table);
    AnsiConsole.MarkupLine($"\n[bold]Total matches:[/] [green]{results.Count}[/]");
}

void DisplayErrors(
    ConcurrentBag<(string VaultName, string SecretName, string Message)> errors)
{
    if (errors.IsEmpty)
        return;

    AnsiConsole.WriteLine();
    var errorTable = new Table()
        .Border(TableBorder.Rounded)
        .Title("[bold yellow]Errors & Warnings[/]")
        .AddColumn(new TableColumn("[bold]Key Vault[/]").Centered())
        .AddColumn(new TableColumn("[bold]Secret[/]").Centered())
        .AddColumn(new TableColumn("[bold]Error[/]").Centered());

    foreach (var error in errors.OrderBy(e => e.VaultName).ThenBy(e => e.SecretName))
    {
        errorTable.AddRow(
            Markup.Escape(error.VaultName),
            string.IsNullOrEmpty(error.SecretName) ? "-" : Markup.Escape(error.SecretName),
            $"[yellow]{Markup.Escape(error.Message)}[/]");
    }

    AnsiConsole.Write(errorTable);
    AnsiConsole.MarkupLine($"\n[bold]Total errors:[/] [yellow]{errors.Count}[/]");
}

void DisplayDevOpsResults(
    List<(string Project, string Repo, string FilePath, string Branch)> devOpsResults,
    string searchDisplay)
{
    AnsiConsole.WriteLine();
    if (devOpsResults.Count > 0)
    {
        var devOpsTable = new Table()
            .Border(TableBorder.Rounded)
            .Title($"[bold blue]Azure DevOps Results for \"{Markup.Escape(searchDisplay)}\" (*.json, *.ts, *.config, *.cs files)[/]")
            .AddColumn(new TableColumn("[bold]Project[/]").Centered())
            .AddColumn(new TableColumn("[bold]Repository[/]").Centered())
            .AddColumn(new TableColumn("[bold]File Path[/]").Centered())
            .AddColumn(new TableColumn("[bold]Branch[/]").Centered());

        foreach (var r in devOpsResults.OrderBy(r => r.Project).ThenBy(r => r.Repo).ThenBy(r => r.FilePath))
        {
            devOpsTable.AddRow(
                Markup.Escape(r.Project),
                Markup.Escape(r.Repo),
                Markup.Escape(r.FilePath),
                Markup.Escape(r.Branch));
        }

        AnsiConsole.Write(devOpsTable);
        AnsiConsole.MarkupLine($"\n[bold]Total DevOps matches:[/] [blue]{devOpsResults.Count}[/]");
    }
    else
    {
        AnsiConsole.MarkupLine($"[yellow]No Azure DevOps matches found for[/] [red]{Markup.Escape(searchDisplay)}[/] [yellow]in *.json, *.ts, *.config, *.cs files.[/]");
    }
}

// ─────────────────────────────── Reporting ───────────────────────────────

string BuildSearchSummary(
    string searchDisplay,
    ConcurrentBag<(string VaultName, string SecretName, string MatchedOn)> results,
    ConcurrentBag<(string VaultName, string SecretName, string Message)> errors,
    List<(string Project, string Repo, string FilePath, string Branch)> devOpsResults)
{
    var sb = new StringBuilder();
    sb.AppendLine($"Search Terms: \"{searchDisplay}\"");
    sb.AppendLine();
    if (!results.IsEmpty)
    {
        sb.AppendLine("Key Vault Matches:");
        foreach (var r in results.OrderBy(r => r.VaultName).ThenBy(r => r.SecretName))
            sb.AppendLine($"  Vault: {r.VaultName}, Secret: {r.SecretName}, Matched On: {r.MatchedOn}");
        sb.AppendLine($"Total Key Vault matches: {results.Count}");
    }
    else
    {
        sb.AppendLine("No Key Vault secrets matched.");
    }
    if (!errors.IsEmpty)
    {
        sb.AppendLine("Errors:");
        foreach (var e in errors.OrderBy(e => e.VaultName).ThenBy(e => e.SecretName))
            sb.AppendLine($"  Vault: {e.VaultName}, Secret: {(string.IsNullOrEmpty(e.SecretName) ? "-" : e.SecretName)}, Error: {e.Message}");
    }
    if (devOpsResults.Count > 0)
    {
        sb.AppendLine("Azure DevOps Matches:");
        foreach (var r in devOpsResults.OrderBy(r => r.Project).ThenBy(r => r.Repo).ThenBy(r => r.FilePath))
            sb.AppendLine($"  Project: {r.Project}, Repo: {r.Repo}, File: {r.FilePath}, Branch: {r.Branch}");
        sb.AppendLine($"Total DevOps matches: {devOpsResults.Count}");
    }
    return sb.ToString();
}

async Task GenerateCopilotReportAsync(List<string> summaries, string? searchArg)
{
    AnsiConsole.WriteLine();
    AnsiConsole.MarkupLine("[bold blue]Generating Markdown report with Copilot...[/]");

    try
    {
        await using var copilotClient = new CopilotClient();
        await copilotClient.StartAsync();

        await using var session = await copilotClient.CreateSessionAsync(new SessionConfig
        {
            Streaming = true,
			Model = "claude-opus-4.6",
			OnPermissionRequest = PermissionHandler.ApproveAll,
            SystemMessage = new SystemMessageConfig
            {
                Mode = SystemMessageMode.Append,
                Content = "You are a report generator. Given raw search results data, produce a clean, well-structured Markdown document with a title, summary, and tables. Output only the raw markdown content with no wrapping code fences."
            }
        });

        var markdownContent = string.Empty;
        var done = new TaskCompletionSource();

        session.On(evt =>
        {
            switch (evt)
            {
                case AssistantMessageDeltaEvent delta:
                    AnsiConsole.Write(delta.Data.DeltaContent);
                    break;
                case AssistantMessageEvent msg:
                    markdownContent = msg.Data.Content;
                    break;
                case SessionErrorEvent error:
                    AnsiConsole.MarkupLine($"\n[red]Copilot error: {Markup.Escape(error.Data.Message)}[/]");
                    done.TrySetResult();
                    break;
                case SessionIdleEvent:
                    done.TrySetResult();
                    break;
            }
        });

        var prompt = $"""
            Attached are the search results across Azure DevOps, \
            search the secrets of any accessible Azure KeyVault Secrets as well as any results in Azure DevOps repo code searches. \
            This will be used to help track down any incoming connections to specified services. \
            Create a well-formatted Markdown report for the following search results.
            Include a title, summary section, and use tables where appropriate.
            Output only the raw markdown content. Be sure to include all relevant information from the search results in the report. \
            The name of the report file should be "Inbound-Direct-Connections-{searchArg}-{DateTime.Now:yyyyMMdd-HHmmss}.md". \

            {string.Join("\n---\n", summaries)}
            """;

        await session.SendAsync(new MessageOptions { Prompt = prompt });
        await done.Task;

        if (!string.IsNullOrWhiteSpace(markdownContent))
        {
            // Delete any previous reports for these search terms
            var prefix = $"{searchArg}-";
            foreach (var oldFile in Directory.EnumerateFiles(Environment.CurrentDirectory, $"{prefix}*.md"))
            {
                try { File.Delete(oldFile); }
                catch { /* best-effort cleanup */ }
            }

            var outputPath = Path.Combine(Environment.CurrentDirectory, $"{prefix}{DateTime.Now:yyyyMMdd-HHmmss}.md");
            await File.WriteAllTextAsync(outputPath, markdownContent);
            AnsiConsole.WriteLine();
            AnsiConsole.MarkupLine($"[green]Markdown report saved to:[/] {Markup.Escape(outputPath)}");
        }
    }
    catch (Exception ex)
    {
        AnsiConsole.MarkupLine($"[yellow]Could not generate Copilot report:[/] {Markup.Escape(ex.Message)}");
    }
}

// ─────────────────────────────── API Endpoint Search ───────────────────────────────

async Task RunApiEndpointSearchAsync()
{
    var rootPath = @"C:\dev";

    if (!Directory.Exists(rootPath))
    {
        AnsiConsole.MarkupLine($"[red]Directory not found:[/] {Markup.Escape(rootPath)}");
        return;
    }

    var gitRepos = Directory.GetDirectories(rootPath)
        .Where(d => Directory.Exists(Path.Combine(d, ".git")))
        .OrderBy(d => d)
        .ToList();

    if (gitRepos.Count == 0)
    {
        AnsiConsole.MarkupLine($"[yellow]No git repositories found in[/] {Markup.Escape(rootPath)}");
        return;
    }

    AnsiConsole.MarkupLine($"Found [green]{gitRepos.Count}[/] git repository(ies) in [blue]{Markup.Escape(rootPath)}[/]:");
    foreach (var repo in gitRepos)
    {
        AnsiConsole.MarkupLine($"  [cyan]{Markup.Escape(Path.GetFileName(repo))}[/]");
    }
    AnsiConsole.WriteLine();

    var endpointPrompt = """
        This folder contains a .NET solution. I want you to find all of the HTTP endpoints in the solution \
        (GET, POST, PUT, PATCH, DELETE). Once you have the complete list and are sure that you have not \
        missed any, output a .json file named 'endpoints.json' containing all of the endpoint information \
        as described. This file should list the filename, the endpoint, the http verb, the url/route, and \
        the route name for each endpoint. The route name is the last segment of the url. For example, \
        '/api/account/profile' would have a route name of 'profile'. Make sure to include all endpoints in the solution, even if \
        they are not directly referenced in the code.
        """;

    var originalDirectory = Environment.CurrentDirectory;

    foreach (var repoPath in gitRepos)
    {
        var repoName = Path.GetFileName(repoPath);

        if (!AnsiConsole.Confirm($"Search [cyan]{Markup.Escape(repoName)}[/]?", defaultValue: true))
        {
            AnsiConsole.MarkupLine($"[grey]Skipping {Markup.Escape(repoName)}[/]");
            continue;
        }

        AnsiConsole.MarkupLine($"\n[bold blue]Processing:[/] [cyan]{Markup.Escape(repoName)}[/]");

        try
        {
            Environment.CurrentDirectory = repoPath;

            await using var copilotClient = new CopilotClient();
            await copilotClient.StartAsync();

            await using var session = await copilotClient.CreateSessionAsync(new SessionConfig
            {
                Streaming = true,
                Model = "claude-opus-4.6",
                OnPermissionRequest = PermissionHandler.ApproveAll,
                SystemMessage = new SystemMessageConfig
                {
                    Mode = SystemMessageMode.Append,
                    Content = $"You are analyzing the .NET solution located at: {repoPath}. " +
                              "Find all HTTP endpoints and output the results as endpoints.json in that directory."
                }
            });

            var done = new TaskCompletionSource();

            session.On(evt =>
            {
                switch (evt)
                {
                    case AssistantMessageDeltaEvent delta:
                        AnsiConsole.Write(new Text(delta.Data.DeltaContent));
                        break;
                    case AssistantMessageEvent:
                        break;
                    case SessionErrorEvent error:
                        AnsiConsole.MarkupLine($"\n[red]Copilot error for {Markup.Escape(repoName)}: {Markup.Escape(error.Data.Message)}[/]");
                        done.TrySetResult();
                        break;
                    case SessionIdleEvent:
                        done.TrySetResult();
                        break;
                }
            });

            await session.SendAsync(new MessageOptions { Prompt = endpointPrompt });
            await done.Task;

            var endpointsFile = Path.Combine(repoPath, "endpoints.json");
            if (File.Exists(endpointsFile))
            {
                AnsiConsole.MarkupLine($"\n[green]\u2713 endpoints.json created for {Markup.Escape(repoName)}[/]");
            }
            else
            {
                AnsiConsole.MarkupLine($"\n[yellow]\u26a0 endpoints.json was not created for {Markup.Escape(repoName)}[/]");
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]Error processing {Markup.Escape(repoName)}:[/] {Markup.Escape(ex.Message)}");
        }
    }

    Environment.CurrentDirectory = originalDirectory;
    AnsiConsole.MarkupLine("\n[bold green]API endpoint search complete.[/]");
}

// ─────────────────────────────── Cache Types ───────────────────────────────

class VaultCacheEntry
{
    public DateTime Timestamp { get; set; }
    public List<VaultCacheItem> Vaults { get; set; } = [];
}

class VaultCacheItem
{
    public string Name { get; set; } = string.Empty;
    public string VaultUri { get; set; } = string.Empty;
}
