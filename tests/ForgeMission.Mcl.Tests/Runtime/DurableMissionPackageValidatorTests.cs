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
