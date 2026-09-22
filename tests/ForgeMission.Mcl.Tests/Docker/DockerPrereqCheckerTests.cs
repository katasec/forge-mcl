using ForgeMission.Docker;

namespace ForgeMission.Tests.Docker;

public sealed class DockerPrereqCheckerTests
{
    [Fact]
    public void Evaluate_SkipsEveryCheckAfterTheFirstFailure()
    {
        var checks = new[]
        {
            new PrereqCheck("Docker", PrereqStatus.Pass, "ready"),
            new PrereqCheck("Port 8080", PrereqStatus.Fail, "in use"),
            new PrereqCheck("agent.yaml", PrereqStatus.Pass, "/tmp/agent.yaml"),
        };

        var results = DockerPrereqChecker.Evaluate(checks);

        Assert.Equal(PrereqStatus.Pass, results[0].Status);
        Assert.Equal(PrereqStatus.Fail, results[1].Status);
        Assert.Equal(PrereqStatus.Skipped, results[2].Status);
        Assert.Equal("–", results[2].Detail);
    }

    [Fact]
    public void Evaluate_LeavesAllChecksIntactWhenNoneFail()
    {
        var checks = new[]
        {
            new PrereqCheck("Docker", PrereqStatus.Pass, "ready"),
            new PrereqCheck("Port 8080", PrereqStatus.Pass, "available"),
        };

        var results = DockerPrereqChecker.Evaluate(checks);

        Assert.Equal(checks, results);
    }
}
