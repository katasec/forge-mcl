using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ForgeMission.Core.Runtime;

namespace ForgeMission.Tests.Runtime;

/// <summary>Resolved durable content is validated without local configuration or file access.</summary>
public class DurableMissionPackageValidatorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Generated_json_roundtrip_preserves_assets_and_canonical_hash(bool withAssets)
    {
        var original = Package("mission Root(message) = { Reader }", "Root", "message", "Reader");
        if (withAssets) original = Rehash(original with { Assets = [Asset("experts/Reader/read.py", [0, 1, 255], true)] });
        var json = JsonSerializer.Serialize(original, PackageTestJsonContext.Default.DurableMissionPackageInput);
        var restored = JsonSerializer.Deserialize(json, PackageTestJsonContext.Default.DurableMissionPackageInput)!;
        Assert.Equal(original.PackageHash, DurableMissionPackageValidator.ComputeHash(restored));
        Assert.True(DurableMissionPackageValidator.TryValidate(restored, out _, out var reason), reason);
        if (withAssets)
        {
            var asset = Assert.Single(restored.Assets!);
            Assert.Equal(new byte[] { 0, 1, 255 }, asset.Bytes);
            Assert.True(asset.Executable);
        }
        else
        {
            Assert.DoesNotContain("\"assets\"", json);
            Assert.Null(restored.Assets);
            var explicitNull = JsonSerializer.Deserialize(json[..^1] + ",\"assets\":null}", PackageTestJsonContext.Default.DurableMissionPackageInput)!;
            Assert.Equal(original.PackageHash, DurableMissionPackageValidator.ComputeHash(explicitNull));
            Assert.Equal(original.PackageHash, DurableMissionPackageValidator.ComputeHash(original with { Assets = [] }));
        }
    }

    [Fact]
    public void Construction_selects_parameterless_root_and_only_reachable_declared_names()
    {
        var reader = Resolved("Reader", "exec", "command: python3\ninputs: [source_file, mode, token_count, output_dir]\noutputKey: text");
        var unused = Resolved("Unused", "llm", "inputs: [unused]");
        var source = "let mode = \"text\"\nmission Root = { Child }\nmission Child = { Reader }";
        Assert.True(DurableMissionPackageValidator.TryCreate(source, [reader, unused], [], out var package, out var reason), reason);
        Assert.Equal("", package!.RootInputName);
        Assert.True(DurableMissionPackageValidator.TryValidate(package, out var validated, out reason), reason);
        Assert.Equal(["mode", "source_file", "token_count"], validated!.AdmittedInputNames);
        Assert.True(DurableMissionPackageValidator.TryValidateInputNames(validated, ["mode", "source_file"], out reason), reason);
        Assert.False(DurableMissionPackageValidator.TryValidateInputNames(validated, ["unused"], out _));
        Assert.False(DurableMissionPackageValidator.TryValidateInputNames(validated, ["mode", "mode"], out _));
    }

    [Theory]
    [InlineData("output")]
    [InlineData("apiKey")]
    [InlineData("FORGE_VALUE")]
    [InlineData("output_dir")]
    public void Reserved_named_inputs_are_rejected(string name)
    {
        var package = Package("mission Root(message) = { Reader }", "Root", "message", "Reader");
        Assert.True(DurableMissionPackageValidator.TryValidate(package, out var validated, out _));
        Assert.False(DurableMissionPackageValidator.TryValidateInputNames(validated!, ["message", name], out _));
        Assert.False(DurableMissionPackageValidator.TryValidateInputNames(validated!, [], out _));
    }

    [Theory]
    [InlineData("../script.py")]
    [InlineData("/script.py")]
    [InlineData("x\\script.py")]
    [InlineData("INPUTS/file")]
    [InlineData("outputs/file")]
    [InlineData("MISSION.MCL")]
    [InlineData("experts/reader/EXPERT.md")]
    [InlineData("experts")]
    public void Assets_cannot_escape_or_replace_staged_files(string path)
    {
        var package = Rehash(Package("mission Root(message) = { Reader }", "Root", "message", "Reader")
            with { Assets = [Asset(path, [1])] });
        Assert.False(DurableMissionPackageValidator.TryValidate(package, out _, out _));
    }

    [Fact]
    public void Asset_digest_execute_bit_and_case_collisions_are_checked()
    {
        var package = Rehash(Package("mission Root(message) = { Reader }", "Root", "message", "Reader")
            with { Assets = [Asset("read.py", [1])] });
        Assert.False(DurableMissionPackageValidator.TryValidate(package with { Assets = [Asset("read.py", [1], true)] }, out _, out _));
        Assert.False(DurableMissionPackageValidator.TryValidate(Rehash(package with { Assets = [package.Assets![0] with { Bytes = [2] }] }), out _, out _));
        Assert.False(DurableMissionPackageValidator.TryValidate(Rehash(package with { Assets = [Asset("read.py", [1]), Asset("READ.py", [1])] }), out _, out _));
    }

    [Theory]
    [InlineData("let value = env(\"PHASE76\")\nmission Root(message) = { Reader }")]
    [InlineData("mission Root(message) = { Reader with(value: env(\"PHASE76\")) }")]
    [InlineData("let provider = \"test\"\nmission Root(message) = { Reader }")]
    public void Authored_environment_and_reserved_bindings_are_rejected(string source)
    {
        var package = Package(source, "Root", "message", "Reader");
        Assert.False(DurableMissionPackageValidator.TryValidate(package, out _, out _));
    }

    [Fact]
    public void Actual_json_budget_includes_base64_and_escaping()
    {
        var package = Package("mission Root(message) = { Reader }", "Root", "message", "Reader");
        var bytes = new byte[3 * 1024 * 1024];
        Assert.False(DurableMissionPackageValidator.TryValidate(Rehash(package with { Assets = [Asset("large.bin", bytes)] }), out _, out var reason));
        Assert.Contains("serialized", reason);
        var markdown = package.ResolvedExperts[0].ExpertMarkdown + new string('<', 800_000);
        var entry = package.ResolvedExperts[0] with { ExpertMarkdown = markdown, LockHash = Hash(Encoding.UTF8.GetBytes(markdown)) };
        Assert.False(DurableMissionPackageValidator.TryValidate(Rehash(package with { ResolvedExperts = [entry] }), out _, out reason));
        Assert.Contains("serialized", reason);
    }

    [Fact]
    public void All_existing_kinds_more_than_two_experts_and_relative_model_assets_are_admitted()
    {
        var experts = new[] {
            Resolved("Llm"), Resolved("Rule", "rule", "check: true"),
            Resolved("Extract", "json_extract"), Resolved("Exec", "exec", "command: arbitrary-command\ninputs: [message]\noutputKey: text"),
            Resolved("Onnx", "onnx", "model: ../../models/identity.onnx\ninputs: [score]\noutputKey: result\nthreshold: 0.5"),
            Resolved("Http", "http", "endpoint: https://example.invalid/"), Resolved("Search", "search") };
        var source = "mission Root(message) = { Llm -> Rule -> Extract -> Exec -> Onnx -> Http -> Search }";
        Assert.True(DurableMissionPackageValidator.TryCreate(source, experts, [Asset("models/identity.onnx", [1])], out var package, out var reason), reason);
        Assert.False(DurableMissionPackageValidator.TryValidate(Rehash(package! with { Assets = [] }), out _, out _));
        Assert.False(DurableMissionPackageValidator.TryCreate("mission Root = { Unknown }", [Resolved("Unknown", "future")], [], out _, out _));
    }

    private static DurableMissionPackageInput Rehash(DurableMissionPackageInput package) => package with { PackageHash = DurableMissionPackageValidator.ComputeHash(package) };
    private static string Hash(byte[] bytes) => "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static DurableMissionAssetInput Asset(string path, byte[] bytes, bool executable = false) => new(path, "application/octet-stream", bytes, Hash(bytes), executable);
    private static DurableResolvedExpertInput Resolved(string name, string kind = "llm", string extra = "")
    {
        var markdown = $"---\nname: {name}\nkind: {kind}\ninput: text\noutput: text\n{extra}\n---\nAnswer.";
        return new(name, "experts", $"experts/{name}/expert.md", Hash(Encoding.UTF8.GetBytes(markdown)), markdown);
    }
    [Fact]
    public void A_step_using_a_listed_profile_is_accepted_and_reports_that_profile()
    {
        var package = Package("mission Chat(message) = {\n    Answerer using anthropic\n}\n", "Chat", "message", "Answerer");

        Assert.True(DurableMissionPackageValidator.TryValidate(package, out var validated, out var reason), reason);
        // Independently calculated from the pre-assets format-1 length-prefixed content.
        Assert.Equal("sha256:58e4db62aa532e40fb3a96a43be1968698f5a21e909b2d6613d3a5dff506e271", package.PackageHash);
        Assert.Equal(["anthropic"], validated!.ProviderProfileNames);
    }

    [Fact]
    public void Steps_without_using_run_on_the_default_profile()
    {
        var package = Package("mission Janus(task) = {\n    Proposer\n    -> Reviewer\n}\n", "Janus", "task", "Proposer", "Reviewer");

        Assert.True(DurableMissionPackageValidator.TryValidate(package, out var validated, out var reason), reason);
        Assert.Equal([DurableMissionPackageValidator.DefaultProviderProfile], validated!.ProviderProfileNames);
    }

    [Fact]
    public void A_step_using_an_arbitrary_profile_reports_its_name()
    {
        var package = Package("mission Chat(message) = {\n    Answerer using forbidden\n}\n", "Chat", "message", "Answerer");

        Assert.True(DurableMissionPackageValidator.TryValidate(package, out var validated, out var reason), reason);
        Assert.Equal(["forbidden"], validated!.ProviderProfileNames);
    }

    [Fact]
    public void Llm_steps_on_different_profiles_are_collected_in_order()
    {
        var package = Package("mission Janus(task) = {\n    Proposer using anthropic\n    -> Reviewer\n}\n", "Janus", "task", "Proposer", "Reviewer");

        Assert.True(DurableMissionPackageValidator.TryValidate(package, out var validated, out var reason), reason);
        Assert.Equal(["anthropic", "default"], validated!.ProviderProfileNames);
    }

    private static DurableMissionPackageInput Package(string source, string mission, string input, params string[] experts)
    {
        var resolved = experts.Select(name =>
        {
            var markdown = $"---\nname: {name}\nkind: llm\ninput: {input}\noutput: answer\n---\nAnswer.\n\n{{{{{input}}}}}\n";
            var hash = "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(markdown))).ToLowerInvariant();
            return new DurableResolvedExpertInput(name, "experts", $"experts/{name}/expert.md", hash, markdown);
        }).ToArray();
        var raw = new DurableMissionPackageInput(DurableMissionPackageValidator.CurrentFormatVersion, "", source, mission, input, resolved);
        return raw with { PackageHash = DurableMissionPackageValidator.ComputeHash(raw) };
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(DurableMissionPackageInput))]
internal partial class PackageTestJsonContext : JsonSerializerContext;
