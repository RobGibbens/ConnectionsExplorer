using Azure.Identity;
using Azure.ResourceManager;
using Azure.ResourceManager.KeyVault;
using Azure.Security.KeyVault.Secrets;
using Spectre.Console;
using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

var credential = new AzureCliCredential();
var armClient = new ArmClient(credential);
using var httpClient = new HttpClient();

// Discover all subscriptions
var subscriptions = new List<Azure.ResourceManager.Resources.SubscriptionResource>();
await AnsiConsole.Status()
    .Spinner(Spinner.Known.Dots)
    .StartAsync("Loading subscriptions...", async ctx =>
    {
        await foreach (var subscription in armClient.GetSubscriptions().GetAllAsync())
        {
            subscriptions.Add(subscription);
        }
    });

if (subscriptions.Count == 0)
{
    AnsiConsole.MarkupLine("[yellow]No subscriptions found. Ensure you are logged in via 'az login'.[/]");
    return;
}

var selectedSubscription = AnsiConsole.Prompt(
    new SelectionPrompt<Azure.ResourceManager.Resources.SubscriptionResource>()
        .Title("Select a [green]subscription[/] to scan:")
        .PageSize(15)
        .UseConverter(s => $"{s.Data.DisplayName} ({s.Data.SubscriptionId})")
        .AddChoices(subscriptions));

var vaults = new List<(string Name, Uri VaultUri)>();

await AnsiConsole.Status()
    .Spinner(Spinner.Known.Dots)
    .StartAsync($"Discovering Key Vaults in subscription [blue]{Markup.Escape(selectedSubscription.Data.DisplayName)}[/]...", async ctx =>
    {
        try
        {
            await foreach (var vault in selectedSubscription.GetKeyVaultsAsync())
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
                    vaults.Add((vault.Data.Name, vaultUri));
                    AnsiConsole.MarkupLine($"  Found vault: [green]{Markup.Escape(vault.Data.Name)}[/] (RG: {Markup.Escape(resourceGroupName)})");
                }
            }
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[yellow]Warning:[/] Could not enumerate vaults in subscription [blue]{Markup.Escape(selectedSubscription.Data.DisplayName)}[/]: {Markup.Escape(ex.Message)}");
        }
    });

AnsiConsole.MarkupLine($"Found [green]{vaults.Count}[/] Key Vault(s).");

// Azure DevOps setup
// Authentication: Uses the same Azure CLI credential (az login).
// A token is requested for the Azure DevOps resource ID (499b84ac-1321-427f-aa17-267ca6975798).
// Ensure your Azure AD / Entra ID account has access to the Azure DevOps organization.
AnsiConsole.WriteLine();
var devOpsOrg = AnsiConsole.Ask<string>("Enter your Azure DevOps [green]organization name[/] (or [grey]\"skip\"[/] to skip DevOps search):");
var searchDevOps = !devOpsOrg.Equals("skip", StringComparison.OrdinalIgnoreCase);
var devOpsRepos = new List<(string Project, string Repo, string DefaultBranch)>();

if (searchDevOps)
{
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

                var projectsUrl = $"https://dev.azure.com/{Uri.EscapeDataString(devOpsOrg)}/_apis/projects?api-version=7.1&$top=500";
                var projectsJson = await httpClient.GetFromJsonAsync<JsonObject>(projectsUrl);

                if (projectsJson?["value"] is JsonArray projects)
                {
                    foreach (var project in projects)
                    {
                        var projectName = project?["name"]?.GetValue<string>();
                        if (projectName is null) continue;

                        ctx.Status($"Listing repos in project [cyan]{Markup.Escape(projectName)}[/]...");
                        var reposUrl = $"https://dev.azure.com/{Uri.EscapeDataString(devOpsOrg)}/{Uri.EscapeDataString(projectName)}/_apis/git/repositories?api-version=7.1";
                        var reposJson = await httpClient.GetFromJsonAsync<JsonObject>(reposUrl);

                        if (reposJson?["value"] is JsonArray repos)
                        {
                            foreach (var repo in repos)
                            {
                                var repoName = repo?["name"]?.GetValue<string>();
                                var defaultBranch = repo?["defaultBranch"]?.GetValue<string>()?.Replace("refs/heads/", "");
                                if (repoName is not null && defaultBranch is not null)
                                {
                                    devOpsRepos.Add((projectName, repoName, defaultBranch));
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
                searchDevOps = false;
            }
        });

    if (searchDevOps)
        AnsiConsole.MarkupLine($"Found [green]{devOpsRepos.Count}[/] Azure DevOps repo(s) in organization [blue]{Markup.Escape(devOpsOrg)}[/].");
}

if (vaults.Count == 0 && !searchDevOps)
{
    AnsiConsole.MarkupLine("[yellow]No Key Vaults or DevOps repos available. Ensure you have access and are logged in via 'az login'.[/]");
    return;
}

// Pre-create SecretClients and cache secret names per vault (reused across searches)
var secretClients = new Dictionary<string, SecretClient>();
var cachedSecretNames = new Dictionary<string, List<string>>();

foreach (var vault in vaults)
{
    secretClients[vault.Name] = new SecretClient(vault.VaultUri, credential);
}

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
                await foreach (var secretProperties in secretClients[vault.Name].GetPropertiesOfSecretsAsync(cts.Token))
                {
                    names.Add(secretProperties.Name);
                }
            }
            catch (Azure.RequestFailedException ex) when (ex.Status == 403)
            {
                AnsiConsole.MarkupLine($"  [yellow]Skipping[/] [cyan]{Markup.Escape(vault.Name)}[/]: Secrets permission denied");
            }
            catch (OperationCanceledException)
            {
                AnsiConsole.MarkupLine($"  [yellow]Skipping[/] [cyan]{Markup.Escape(vault.Name)}[/]: Timed out");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"  [yellow]Skipping[/] [cyan]{Markup.Escape(vault.Name)}[/]: {Markup.Escape(ex.Message)}");
            }
            cachedSecretNames[vault.Name] = names;
            AnsiConsole.MarkupLine($"  [green]{Markup.Escape(vault.Name)}[/]: {names.Count} secret(s)");
        }
    });

// Search loop — reuses cached clients and secret names
while (true)
{
    AnsiConsole.WriteLine();
    var searchTerm = AnsiConsole.Ask<string>("Enter the [green]partial string[/] to search for (or [grey]\"quit\"[/] to exit):");
    if (searchTerm.Equals("quit", StringComparison.OrdinalIgnoreCase) ||
        searchTerm.Equals("exit", StringComparison.OrdinalIgnoreCase))
    {
        AnsiConsole.MarkupLine("[grey]Goodbye![/]");
        break;
    }

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
                var secrets = cachedSecretNames[vault.Name];
                var progressTask = ctx.AddTask($"[cyan]{Markup.Escape(vault.Name)}[/]", maxValue: Math.Max(secrets.Count, 1));
                await ScanVaultAsync(vault, secrets, searchTerm, progressTask, results, errors);
            }
        });

    // Display results
    AnsiConsole.WriteLine();

    if (results.IsEmpty)
    {
        AnsiConsole.MarkupLine($"[yellow]No secrets matched the search term[/] [red]{Markup.Escape(searchTerm)}[/].");
    }
    else
    {
        var table = new Table()
            .Border(TableBorder.Rounded)
            .Title($"[bold green]Search Results for \"{Markup.Escape(searchTerm)}\"[/]")
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

    if (!errors.IsEmpty)
    {
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

    // Azure DevOps code search
    if (searchDevOps)
    {
        var devOpsResults = new List<(string Project, string Repo, string FilePath, string Branch)>();

        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Searching Azure DevOps repos for *.json files...", async ctx =>
            {
                try
                {
                    var token = await credential.GetTokenAsync(
                        new Azure.Core.TokenRequestContext(["499b84ac-1321-427f-aa17-267ca6975798/.default"]));
                    httpClient.DefaultRequestHeaders.Authorization =
                        new AuthenticationHeaderValue("Bearer", token.Token);

                    var searchRequest = new JsonObject
                    {
                        ["searchText"] = $"{searchTerm} ext:json",
                        ["$skip"] = 0,
                        ["$top"] = 1000,
                        ["filters"] = new JsonObject(),
                        ["includeFacets"] = false
                    };

                    var response = await httpClient.PostAsJsonAsync(
                        $"https://almsearch.dev.azure.com/{Uri.EscapeDataString(devOpsOrg)}/_apis/search/codesearchresults?api-version=7.1-preview.1",
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
                        AnsiConsole.MarkupLine($"[yellow]Azure DevOps search failed:[/] {Markup.Escape(response.StatusCode.ToString())}");
                        AnsiConsole.MarkupLine($"[grey]{Markup.Escape(errorBody)}[/]");
                    }
                }
                catch (Exception ex)
                {
                    AnsiConsole.MarkupLine($"[yellow]Azure DevOps search error:[/] {Markup.Escape(ex.Message)}");
                }
            });

        AnsiConsole.WriteLine();
        if (devOpsResults.Count > 0)
        {
            var devOpsTable = new Table()
                .Border(TableBorder.Rounded)
                .Title($"[bold blue]Azure DevOps Results for \"{Markup.Escape(searchTerm)}\" (*.json files)[/]")
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
            AnsiConsole.MarkupLine($"[yellow]No Azure DevOps matches found for[/] [red]{Markup.Escape(searchTerm)}[/] [yellow]in *.json files.[/]");
        }
    }
}

async Task ScanVaultAsync(
    (string Name, Uri VaultUri) vault,
    List<string> secrets,
    string searchTerm,
    ProgressTask progressTask,
    ConcurrentBag<(string VaultName, string SecretName, string MatchedOn)> results,
    ConcurrentBag<(string VaultName, string SecretName, string Message)> errors)
{
    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
    try
    {
        var client = secretClients[vault.Name];

        foreach (var secretName in secrets)
        {
            try
            {
                if (secretName.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add((vault.Name, secretName, "Name"));
                }

                var secret = await client.GetSecretAsync(secretName, cancellationToken: cts.Token);
                if (secret.Value.Value is not null &&
                    secret.Value.Value.Contains(searchTerm, StringComparison.OrdinalIgnoreCase))
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
