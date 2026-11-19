using ForgeMission.Core.Tools;

namespace ForgeMission.Tests.Tools;

public sealed class CapabilityDispatcherFileTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("forge-file-").FullName;
    private readonly ICapabilityDispatcher _dispatcher;

    public CapabilityDispatcherFileTests()
    {
        var workspace = new LocalDiskWorkspace(_root);
        var capabilities = new CapabilityRegistry([new WorkspaceFileProvider(workspace)]);
        _dispatcher = new CapabilityDispatcher(capabilities,
            new PolicyCapabilityAuthorizer(CapabilityAuthorizationPolicy.Default), new InMemoryCapabilityAuditLog());
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Read_ReturnsFullFileContent()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "line1\nline2\nline3");

        var result = await Dispatch(new ReadFileCapabilityRequest("a.txt", 0, null));

        Assert.False(result.IsError);
        Assert.Equal("line1\nline2\nline3", result.Content);
    }

    [Fact]
    public async Task Read_OffsetAndLimit_SliceLines()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "l1\nl2\nl3\nl4\nl5");

        var result = await Dispatch(new ReadFileCapabilityRequest("a.txt", 1, 2));

        Assert.Equal("l2\nl3", result.Content);
    }

    [Fact]
    public async Task Read_MissingFile_ReturnsError_NotThrow()
    {
        var result = await Dispatch(new ReadFileCapabilityRequest("nope.txt", 0, null));

        Assert.True(result.IsError);
        Assert.Contains("not found", result.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Read_PathEscapingRoot_ReturnsError_NotThrow()
    {
        var result = await Dispatch(new ReadFileCapabilityRequest("../outside.txt", 0, null));

        Assert.True(result.IsError);
        Assert.Contains("outside the workspace roots", result.Content);
    }

    [Fact]
    public async Task Edit_UniqueMatch_Replaces()
    {
        var path = Path.Combine(_root, "a.txt");
        File.WriteAllText(path, "hello world");

        var result = await Dispatch(new EditFileCapabilityRequest("a.txt", "world", "there", ReplaceAll: false));

        Assert.False(result.IsError);
        Assert.Equal("hello there", File.ReadAllText(path));
    }

    [Fact]
    public async Task Edit_NonUniqueMatch_WithoutReplaceAll_FailsWithoutTouchingFile()
    {
        var path = Path.Combine(_root, "a.txt");
        File.WriteAllText(path, "foo bar foo");

        var result = await Dispatch(new EditFileCapabilityRequest("a.txt", "foo", "baz", ReplaceAll: false));

        Assert.True(result.IsError);
        Assert.Contains("not unique", result.Content);
        Assert.Equal("foo bar foo", File.ReadAllText(path));
    }

    [Fact]
    public async Task Edit_NonUniqueMatch_WithReplaceAll_ReplacesEvery()
    {
        var path = Path.Combine(_root, "a.txt");
        File.WriteAllText(path, "foo bar foo");

        var result = await Dispatch(new EditFileCapabilityRequest("a.txt", "foo", "baz", ReplaceAll: true));

        Assert.False(result.IsError);
        Assert.Equal("baz bar baz", File.ReadAllText(path));
    }

    [Fact]
    public async Task Edit_OldStringNotFound_ReturnsError()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "hello world");

        var result = await Dispatch(new EditFileCapabilityRequest("a.txt", "missing", "x", ReplaceAll: false));

        Assert.True(result.IsError);
        Assert.Contains("not found", result.Content);
    }

    [Fact]
    public async Task Edit_NonexistentFile_ReturnsError_PointsToWrite()
    {
        var result = await Dispatch(new EditFileCapabilityRequest("nope.txt", "a", "b", ReplaceAll: false));

        Assert.True(result.IsError);
        Assert.Contains("Write", result.Content);
    }

    [Fact]
    public async Task Edit_PathEscapingRoot_ReturnsError_NotThrow()
    {
        var result = await Dispatch(new EditFileCapabilityRequest("../outside.txt", "a", "b", ReplaceAll: false));

        Assert.True(result.IsError);
        Assert.Contains("outside the workspace roots", result.Content);
    }

    [Fact]
    public async Task Write_CreatesNewFile()
    {
        var result = await Dispatch(new WriteFileCapabilityRequest("new.txt", "hello"));

        Assert.False(result.IsError);
        Assert.Equal("hello", File.ReadAllText(Path.Combine(_root, "new.txt")));
    }

    [Fact]
    public async Task Write_OverwritesExistingFile()
    {
        var path = Path.Combine(_root, "a.txt");
        File.WriteAllText(path, "old");

        var result = await Dispatch(new WriteFileCapabilityRequest("a.txt", "new"));

        Assert.False(result.IsError);
        Assert.Equal("new", File.ReadAllText(path));
    }

    [Fact]
    public async Task Write_PathEscapingRoot_ReturnsError_NotThrow()
    {
        var result = await Dispatch(new WriteFileCapabilityRequest("../escape.txt", "x"));

        Assert.True(result.IsError);
        Assert.Contains("outside the workspace roots", result.Content);
        Assert.False(File.Exists(Path.Combine(Directory.GetParent(_root)!.FullName, "escape.txt")));
    }

    private Task<ToolExecutionResult> Dispatch(ICapabilityRequest request) =>
        _dispatcher.DispatchAsync("file", request, default);
}
