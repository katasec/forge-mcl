# ForgeMission.MissionRegistry

`ForgeMission.MissionRegistry` is the generic OCI retrieval boundary for Mission Control
Language. It parses Forge OCI locators, pulls immutable expert or mission content, and maintains
the local `~/.forge` cache used by those pull operations.

## Public surface

- `OciReference.Resolve` normalizes standard repository selectors against an explicit registry base; the existing `Parse` retains legacy `registry/name@reference` locators.
- `OciExpertPuller` retrieves one expert document into the Forge cache.
- `OciMissionPuller.PullVerifiedAsync` resolves every invocation through OciClient 0.5.0, verifies the layer and reads at most 32 MiB of regular tar entries into a fresh cache candidate. Absolute/traversal/duplicate paths, links, directories and devices are rejected. Existing content-keyed cache files must match the verified candidate.
- `ForgeCache` exposes the cache paths used by these operations.

The package does not own a mission catalog, baked fallback content, hosted execution, or user
interfaces. Consumers choose the OCI reference and invoke the pull operation. Registry tokens
are passed explicitly to the verified pull operations by the consumer. Legacy pull overloads retain their existing credential reader; this
package never publishes or transports credentials itself.

The package is Native-AOT compatible and targets `net10.0`.
