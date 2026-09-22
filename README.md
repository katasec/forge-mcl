# forge-mcl

`forge-mcl` owns the Mission Control Language runtime: parsing, provider-neutral execution,
provider adapters, web search, generic OCI registry pulls, HTTP wire mapping, Docker helpers, and
the Native AOT `forge` CLI.

It deliberately does not own a mission catalog, baked mission fallback, hosted Runner, Desktop,
conversations, Rooms, platform accounts, or billing. Those remain consumer-owned concerns.

## Build

```sh
dotnet build ForgeMission.slnx
dotnet test ForgeMission.slnx
dotnet run --project src/ForgeMission.Cli -- --help
```

Use `make install` to publish the current-platform Native AOT CLI locally.
