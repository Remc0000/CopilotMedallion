namespace CopilotMedallion.Api.Models;

public record SourceTable(string Name);

public record LakehouseInfo(string Id, string DisplayName, string WorkspaceId, string? Description);

public record GenerateSpecsRequest(
    string SourceLakehouseId,
    List<string> Tables,
    string? TargetLakehouseName,
    string? SpecMarkdown,
    string? ExistingRunId,
    string? TargetLakehouseId,
    string? TargetWorkspaceId);

public record PreviewSpecsRequest(string SourceLakehouseId, List<string> Tables, string? TargetLakehouseName, string? Model);

public record PreviewSpecsResponse(string Markdown, string RunId, string TargetLakehouseName);

public record GenerateSpecsResponse(string RunId, string Branch, string SpecUrl);

public record BuildRequest(string RunId, string? Model = null);
public record FixSpecRequest(string CurrentSpec, string ErrorTrace, string? Model = null, int? Iteration = null, string? FailedLayer = null);

public record RunInfo(
    string RunId,
    string Status,
    string? Branch,
    string? SpecUrl,
    string? WorkspaceId,
    string? SourceLakehouseId,
    string? TablesCsv,
    string? TargetLakehouseId,
    string? TargetLakehouseName,
    string? NotebookId,
    string? JobInstanceId,
    string? Message,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string? TargetWorkspaceId = null,
    string? SourceWorkspaceId = null,
    string? BronzeNotebookId = null,
    string? SilverNotebookId = null,
    string? GoldNotebookId = null,
    string? BronzeJobId = null,
    string? SilverJobId = null,
    string? GoldJobId = null,
    string? CurrentLayer = null,
    string? ReportingNotebookId = null,
    string? ReportingJobId = null,
    string? SpecMarkdown = null
);
