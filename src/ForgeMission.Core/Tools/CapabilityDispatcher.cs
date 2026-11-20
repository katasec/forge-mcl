using System.Diagnostics.CodeAnalysis;

namespace ForgeMission.Core.Tools;

public sealed class CapabilityDispatcher(
    CapabilityRegistry capabilities,
    ICapabilityAuthorizer authorizer,
    ICapabilityAuditLog auditLog,
    ICapabilityConfirmationHandler? confirmationHandler = null) : ICapabilityDispatcher
{
    public async Task<ToolExecutionResult> DispatchAsync(
        string capabilityName,
        object request,
        CancellationToken ct)
    {
        var summary = request is ICapabilityRequest capabilityRequest
            ? capabilityRequest.Summary
            : request.ToString() ?? capabilityName;
        var outcome = await authorizer.AuthorizeAsync(capabilityName, request, ct);
        bool? userConfirmed = null;
        var providerInvoked = false;

        ToolExecutionResult result;
        if (outcome == AuthorizationOutcome.AutoDenied)
        {
            result = ToolExecutionResult.Error("Capability request denied by policy.");
        }
        else if (outcome == AuthorizationOutcome.RequiresUserConfirmation)
        {
            userConfirmed = confirmationHandler is not null && await confirmationHandler.ConfirmAsync(
                new CapabilityConfirmationRequest(capabilityName, summary), ct);
            result = userConfirmed == true
                ? await ExecuteAuthorizedAsync(capabilityName, request, ct, () => providerInvoked = true)
                : ToolExecutionResult.Error("Capability request denied by the user.");
        }
        else
        {
            result = await ExecuteAuthorizedAsync(capabilityName, request, ct, () => providerInvoked = true);
        }

        auditLog.Record(new CapabilityAuditRecord(
            capabilityName,
            summary,
            outcome,
            DateTimeOffset.UtcNow,
            userConfirmed,
            providerInvoked,
            !result.IsError));
        return result;
    }

    private Task<ToolExecutionResult> ExecuteAuthorizedAsync(
        string capabilityName,
        object request,
        CancellationToken ct,
        Action providerInvoked) =>
        (capabilityName, request) switch
        {
            (_, InvalidCapabilityRequest invalid) => Task.FromResult(ToolExecutionResult.Error(invalid.Error)),
            (CapabilityNames.File, ReadFileCapabilityRequest read) => ReadFileAsync(read, ct, providerInvoked),
            (CapabilityNames.File, EditFileCapabilityRequest edit) => EditFileAsync(edit, ct, providerInvoked),
            (CapabilityNames.File, WriteFileCapabilityRequest write) => WriteFileAsync(write, ct, providerInvoked),
            (CapabilityNames.Terminal, ExecuteTerminalCapabilityRequest terminal) => ExecuteTerminalAsync(terminal, ct, providerInvoked),
            _ => Task.FromResult(ToolExecutionResult.Error($"Unsupported capability request for {capabilityName}.")),
        };

    private async Task<ToolExecutionResult> ReadFileAsync(
        ReadFileCapabilityRequest request,
        CancellationToken ct,
        Action providerInvoked)
    {
        if (!TryResolveFile(request.FilePath, providerInvoked, out var files, out var resolved, out var error))
            return error;

        if (!await files.ExistsAsync(resolved, ct))
            return ToolExecutionResult.Error($"File not found: {request.FilePath}");

        try
        {
            var content = await files.ReadFileAsync(resolved, ct);
            var lines = ReadLines(content);
            var limit = request.Limit ?? lines.Count;
            return new ToolExecutionResult(string.Join('\n', lines.Skip(request.Offset).Take(limit)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ToolExecutionResult.Error($"Failed to read {request.FilePath}: {ex.Message}");
        }
    }

    private async Task<ToolExecutionResult> EditFileAsync(
        EditFileCapabilityRequest request,
        CancellationToken ct,
        Action providerInvoked)
    {
        // An empty old_string matches at every index, so counting it would never advance.
        if (string.IsNullOrEmpty(request.OldString))
            return ToolExecutionResult.Error("old_string must not be empty. Use Write to create or replace a whole file.");

        if (!TryResolveFile(request.FilePath, providerInvoked, out var files, out var resolved, out var error))
            return error;

        if (!await files.ExistsAsync(resolved, ct))
            return ToolExecutionResult.Error($"File not found: {request.FilePath}. Use Write to create a new file.");

        try
        {
            var content = await files.ReadFileAsync(resolved, ct);
            var occurrences = CountOccurrences(content, request.OldString);
            if (occurrences == 0)
                return ToolExecutionResult.Error("old_string not found in file");
            if (occurrences > 1 && !request.ReplaceAll)
                return ToolExecutionResult.Error(
                    $"old_string is not unique ({occurrences} matches) — pass replace_all: true, " +
                    "or include more surrounding context to make it unique");

            var updated = request.ReplaceAll
                ? content.Replace(request.OldString, request.NewString)
                : ReplaceFirst(content, request.OldString, request.NewString);
            await files.WriteFileAsync(resolved, updated, ct);
            return new ToolExecutionResult(request.ReplaceAll
                ? $"Edited {request.FilePath} ({occurrences} replacements)"
                : $"Edited {request.FilePath}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ToolExecutionResult.Error($"Failed to edit {request.FilePath}: {ex.Message}");
        }
    }

    private async Task<ToolExecutionResult> WriteFileAsync(
        WriteFileCapabilityRequest request,
        CancellationToken ct,
        Action providerInvoked)
    {
        if (!TryResolveFile(request.FilePath, providerInvoked, out var files, out var resolved, out var error))
            return error;

        try
        {
            await files.WriteFileAsync(resolved, request.Content, ct);
            return new ToolExecutionResult($"Wrote {request.FilePath}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ToolExecutionResult.Error($"Failed to write {request.FilePath}: {ex.Message}");
        }
    }

    // Shared file preamble: the file provider must be registered, and the path must resolve inside it.
    private bool TryResolveFile(
        string filePath,
        Action providerInvoked,
        [NotNullWhen(true)] out IFileProvider? files,
        out string resolved,
        [NotNullWhen(false)] out ToolExecutionResult? error)
    {
        resolved = string.Empty;
        error = null;
        if (!capabilities.TryGetProvider(out files))
        {
            error = ToolExecutionResult.Error("File capability is unavailable.");
            return false;
        }

        providerInvoked();
        if (files.TryResolvePath(filePath, out resolved, out var pathError))
            return true;

        error = ToolExecutionResult.Error(pathError!);
        return false;
    }

    private async Task<ToolExecutionResult> ExecuteTerminalAsync(
        ExecuteTerminalCapabilityRequest request,
        CancellationToken ct,
        Action providerInvoked)
    {
        if (!capabilities.TryGetProvider<ITerminalProvider>(out var terminal))
            return ToolExecutionResult.Error("Terminal capability is unavailable.");

        providerInvoked();
        return await terminal.ExecuteAsync(request.Command, workingDir: null, ct);
    }

    private static List<string> ReadLines(string content)
    {
        var lines = new List<string>();
        using var reader = new StringReader(content);
        while (reader.ReadLine() is { } line)
            lines.Add(line);
        return lines;
    }

    private static int CountOccurrences(string content, string target)
    {
        var count = 0;
        var index = 0;
        while ((index = content.IndexOf(target, index, StringComparison.Ordinal)) != -1)
        {
            count++;
            index += target.Length;
        }
        return count;
    }

    private static string ReplaceFirst(string content, string oldString, string newString)
    {
        var index = content.IndexOf(oldString, StringComparison.Ordinal);
        return content[..index] + newString + content[(index + oldString.Length)..];
    }
}
