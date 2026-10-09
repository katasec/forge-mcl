# ForgeMission.MissionRegistry

`ForgeMission.MissionRegistry` is the generic OCI retrieval boundary for Mission Control
Language. It parses Forge OCI locators, pulls immutable expert or mission content, and maintains
the local `~/.forge` cache used by those pull operations.

## Public surface

- `OciReference` validates `registry/name@reference` locators.
- `OciExpertPuller` retrieves one expert document into the Forge cache.
- `OciMissionPuller` retrieves and unpacks a self-contained mission bundle.
- `ForgeCache` exposes the cache paths used by these operations.

The package does not own a mission catalog, baked fallback content, hosted execution, or user
interfaces. Consumers choose the OCI reference and invoke the pull operation. Registry tokens
come from `FORGE_REGISTRY_TOKEN` or the consumer-managed `~/.forge/credentials.json` file; this
package never publishes or transports credentials itself.

The package is Native-AOT compatible and targets `net10.0`.
