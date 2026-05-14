namespace CopilotMedallion.Api.Models;

public record SourceTable(string Name, string Path);

public record LakehouseInfo(string Id, string DisplayName, string WorkspaceId, string? Description);

public record GenerateSpecsRequest(string SourceLakehouseId, List<string> Tables, string? TargetLakehouseName, string? SpecMarkdown, string? ExistingRunId);

public record PreviewSpecsRequest(string SourceLakehouseId, List<string> Tables, string? TargetLakehouseName);

public record PreviewSpecsResponse(string Markdown, string RunId, string TargetLakehouseName);

public record GenerateSpecsResponse(string RunId, string Branch, string SpecUrl, string SpecRawUrl);

public record BuildRequest(string RunId);

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
    DateTime UpdatedAt
);
