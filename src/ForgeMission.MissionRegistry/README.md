# ForgeMission.MissionRegistry

`ForgeMission.MissionRegistry` is the generic OCI retrieval boundary for Mission Control
Language. It parses Forge OCI locators, pulls immutable expert or mission content, and maintains
the local `~/.forge` cache used by those pull operations.

## Public surface

- `OciReference` resolves bare mission names below a caller-supplied registry base and validates fully qualified locators.
- `OciExpertPuller` retrieves one expert document into the Forge cache.
- `OciMissionPuller` retrieves and unpacks a self-contained mission bundle.
- `ForgeCache` exposes the cache paths used by these operations.

The package does not own a mission catalog, baked fallback content, hosted execution, user
interfaces, or credential persistence. Consumers choose the OCI reference, resolve any saved
credential in their composition boundary, and pass that one token to a pull operation.

The package is Native-AOT compatible and targets `net10.0`.
