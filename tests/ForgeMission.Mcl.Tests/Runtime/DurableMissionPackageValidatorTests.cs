using System.Security.Cryptography;
using System.Text;
using ForgeMission.Core.Runtime;

namespace ForgeMission.Tests.Runtime;

/// <summary>Phase 53.4: a durable step may select only a Core-listed provider profile, and a
/// package runs every llm step on one profile so its settlement names the model actually used.</summary>
public class DurableMissionPackageValidatorTests
{
    [Fact]
    public void A_step_using_a_listed_profile_is_accepted_and_reports_that_profile()
    {
        var package = Package("mission Chat(message) = {\n    Answerer using anthropic\n}\n", "Chat", "message", "Answerer");

        Assert.True(DurableMissionPackageValidator.TryValidate(package, out var validated, out var reason), reason);
        Assert.Equal("anthropic", validated!.ProviderProfile);
    }

    [Fact]
    public void Steps_without_using_run_on_the_default_profile()
    {
        var package = Package("mission Janus(task) = {\n    Proposer\n    -> Reviewer\n}\n", "Janus", "task", "Proposer", "Reviewer");

        Assert.True(DurableMissionPackageValidator.TryValidate(package, out var validated, out var reason), reason);
        Assert.Equal(DurableMissionPackageValidator.DefaultProviderProfile, validated!.ProviderProfile);
    }

    [Fact]
    public void A_step_using_an_unlisted_profile_is_rejected()
    {
        var package = Package("mission Chat(message) = {\n    Answerer using forbidden\n}\n", "Chat", "message", "Answerer");

        Assert.False(DurableMissionPackageValidator.TryValidate(package, out var validated, out var reason));
        Assert.Null(validated);
        Assert.Contains("'forbidden'", reason);
    }

    [Fact]
    public void Llm_steps_on_different_profiles_are_rejected()
    {
        var package = Package("mission Janus(task) = {\n    Proposer using anthropic\n    -> Reviewer\n}\n", "Janus", "task", "Proposer", "Reviewer");

        Assert.False(DurableMissionPackageValidator.TryValidate(package, out _, out var reason));
        Assert.Contains("one provider profile", reason);
    }

    [Fact]
    public void Canonical_package_bytes_round_trip_through_the_AOT_serializer()
    {
        var package = Package("mission Chat(message) = {\n    Answerer\n}\n", "Chat", "message", "Answerer");

        var bytes = DurableMissionPackageValidator.Serialize(package);
        var accepted = DurableMissionPackageValidator.TryDeserialize(bytes, out var restored, out var reason);

        Assert.True(accepted, reason);
        Assert.NotNull(restored);
        Assert.Equal(package.FormatVersion, restored.FormatVersion);
        Assert.Equal(package.PackageHash, restored.PackageHash);
        Assert.Equal(package.MissionSource, restored.MissionSource);
        Assert.Equal(package.RootMissionName, restored.RootMissionName);
        Assert.Equal(package.RootInputName, restored.RootInputName);
        Assert.Equal(package.ResolvedExperts, restored.ResolvedExperts);
    }

    [Fact]
    public void Canonical_package_bytes_sort_resolved_experts()
    {
        var package = Package("mission Chat(message) = {\n    Alpha\n    -> Beta\n}\n", "Chat", "message", "Alpha", "Beta");
        var reordered = package with { ResolvedExperts = package.ResolvedExperts.Reverse().ToArray() };

        Assert.Equal(DurableMissionPackageValidator.Serialize(package), DurableMissionPackageValidator.Serialize(reordered));
    }

    [Fact]
    public void Canonical_package_bytes_reject_a_tampered_hash()
    {
        var validPackage = Package("mission Chat(message) = {\n    Answerer\n}\n", "Chat", "message", "Answerer");
        var package = validPackage with
        {
            PackageHash = "sha256:0000000000000000000000000000000000000000000000000000000000000000",
        };
        var original = DurableMissionPackageValidator.Serialize(validPackage);
        var text = Encoding.UTF8.GetString(original).Replace(
            validPackage.PackageHash,
            package.PackageHash,
            StringComparison.Ordinal);
        var bytes = Encoding.UTF8.GetBytes(text);

        var accepted = DurableMissionPackageValidator.TryDeserialize(bytes, out _, out var reason);

        Assert.False(accepted);
        Assert.Equal("The durable package hash does not match its canonical content.", reason);
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
