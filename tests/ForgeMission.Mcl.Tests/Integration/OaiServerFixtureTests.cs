using ForgeMission.Core.Adapters;
using ForgeMission.Tests.Runtime;

namespace ForgeMission.Tests.Integration;

public sealed class OaiServerFixtureTests
{
    [Fact]
    public async Task Fixture_StartsAndRespondsToModelsEndpoint()
    {
        var stub = new StubExpertRunner();
        var chatClient = BuildMissionChatClient(stub);

        await using var fixture = await OaiServerFixture.StartAsync(chatClient);

        using var http = new HttpClient();
        var response = await http.GetAsync($"{fixture.BaseUrl}/v1/models");

        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task NoopMission_LoadsCleanly()
    {
        var missionDir = Path.Combine(AppContext.BaseDirectory, "Missions", "noop");

        Assert.True(Directory.Exists(missionDir), $"Mission dir not found: {missionDir}");

        var source   = await File.ReadAllTextAsync(Path.Combine(missionDir, "mission.mcl"));
        var ast      = ForgeMission.Parser.MclParser.Parse(source);
        var lockFile = ForgeMission.Core.Resolution.LockFileIO.Read(
                           Path.Combine(missionDir, "mcl.lock"));
        var experts  = ForgeMission.Core.Experts.ExpertLoader
                           .LoadFromLockFile(lockFile, missionDir);

        Assert.NotNull(ast);
        Assert.True(experts.ContainsKey("PassThrough"));
    }

    private static ForgeMission.Core.Adapters.MissionChatClient BuildMissionChatClient(
        ForgeMission.Core.Runtime.IExpertRunner runner)
    {
        var missionDir = Path.Combine(AppContext.BaseDirectory, "Missions", "noop");
        var source     = File.ReadAllText(Path.Combine(missionDir, "mission.mcl"));
        var ast        = ForgeMission.Parser.MclParser.Parse(source);
        var lockFile   = ForgeMission.Core.Resolution.LockFileIO.Read(
                             Path.Combine(missionDir, "mcl.lock"));
        var experts    = ForgeMission.Core.Experts.ExpertLoader
                             .LoadFromLockFile(lockFile, missionDir);
        return new MissionChatClient(ast, experts, runner);
    }
}
