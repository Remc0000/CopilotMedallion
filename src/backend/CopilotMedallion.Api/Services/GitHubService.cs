using Octokit;

namespace CopilotMedallion.Api.Services;

public class GitHubService
{
    private readonly string _owner;
    private readonly string _runsRepo;
    private readonly string _pat;
    private readonly ILogger<GitHubService> _log;

    public GitHubService(IConfiguration cfg, ILogger<GitHubService> log)
    {
        _owner = cfg["GitHub:Owner"] ?? "Remc0000";
        _runsRepo = cfg["GitHub:RunsRepo"] ?? "CopilotMedallion-runs";
        _pat = Environment.GetEnvironmentVariable("GITHUB_PAT") ?? cfg["GITHUB_PAT"] ?? "";
        _log = log;
    }

    private GitHubClient Client()
    {
        var c = new GitHubClient(new ProductHeaderValue("copilot-roesli"));
        if (!string.IsNullOrEmpty(_pat) && _pat != "__SET_ME__")
            c.Credentials = new Credentials(_pat);
        return c;
    }

    public bool Configured => !string.IsNullOrEmpty(_pat) && _pat != "__SET_ME__";

    public async Task<(string Branch, string FileUrl, string RawUrl)> PushSpecAsync(string runId, string specMarkdown)
    {
        if (!Configured) throw new InvalidOperationException("GITHUB_PAT not configured.");
        var client = Client();
        var branch = $"run/{runId}";
        var path = $"runs/{runId}/spec.md";

        // Ensure repo has at least one commit (Octokit's Reference.Get fails on empty repos).
        var repo = await client.Repository.Get(_owner, _runsRepo);
        Octokit.Reference defaultBranchRef;
        try
        {
            defaultBranchRef = await client.Git.Reference.Get(_owner, _runsRepo, $"heads/{repo.DefaultBranch}");
        }
        catch (ApiException ex) when (ex.HttpResponse != null && (int)ex.HttpResponse.StatusCode == 409)
        {
            await client.Repository.Content.CreateFile(_owner, _runsRepo, "README.md",
                new CreateFileRequest("chore: seed repo",
                    "# CopilotMedallion run specs\n\nAuto-populated by https://copilot.roesli.org.",
                    repo.DefaultBranch ?? "main"));
            defaultBranchRef = await client.Git.Reference.Get(_owner, _runsRepo, $"heads/{repo.DefaultBranch ?? "main"}");
        }

        try
        {
            await client.Git.Reference.Create(_owner, _runsRepo,
                new NewReference($"refs/heads/{branch}", defaultBranchRef.Object.Sha));
        }
        catch (ApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.UnprocessableEntity) { /* branch already exists */ }

        // Create-or-update the spec file on this branch.
        try
        {
            await client.Repository.Content.CreateFile(_owner, _runsRepo, path,
                new CreateFileRequest($"Add spec for run {runId}", specMarkdown, branch));
        }
        catch (ApiException ex) when (ex.StatusCode == System.Net.HttpStatusCode.UnprocessableEntity ||
                                       ex.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            // File already exists — update it.
            var existing = await client.Repository.Content.GetAllContentsByRef(_owner, _runsRepo, path, branch);
            var sha = existing.FirstOrDefault()?.Sha;
            if (sha is null) throw;
            await client.Repository.Content.UpdateFile(_owner, _runsRepo, path,
                new UpdateFileRequest($"Update spec for run {runId}", specMarkdown, sha, branch));
        }

        var blobUrl = $"https://github.com/{_owner}/{_runsRepo}/blob/{branch}/{path}";
        var rawUrl = $"https://raw.githubusercontent.com/{_owner}/{_runsRepo}/{branch}/{path}";
        return (branch, blobUrl, rawUrl);
    }
}
