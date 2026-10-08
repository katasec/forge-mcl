TERMINAL_EXTENSIONS_PROJECT := src/ForgeMission.Terminal.Extensions/ForgeMission.Terminal.Extensions.csproj
TERMINAL_EXTENSIONS_COMMIT := $(shell git rev-parse HEAD)
PARSER_PROJECT := src/ForgeMission.Parser/ForgeMission.Parser.csproj
PARSER_TEST_PROJECT := tests/ForgeMission.Mcl.Tests/ForgeMission.Mcl.Tests.csproj
PARSER_PACKAGE_DIR := artifacts/packages
PARSER_PACKAGE_VERSION := 0.1.0
PARSER_COMMIT := $(shell git rev-parse HEAD)
SCOUT_PROJECT := src/ForgeMission.Scout/ForgeMission.Scout.csproj
SCOUT_TEST_PROJECT := tests/ForgeMission.Mcl.Tests/ForgeMission.Mcl.Tests.csproj
SCOUT_PACKAGE_DIR := artifacts/packages
SCOUT_PACKAGE_VERSION := 0.1.0
SCOUT_COMMIT := $(shell git rev-parse HEAD)
CORE_PROJECT := src/ForgeMission.Core/ForgeMission.Core.csproj
CORE_TEST_PROJECT := tests/ForgeMission.Mcl.Tests/ForgeMission.Mcl.Tests.csproj
CORE_PACKAGE_DIR := artifacts/packages
CORE_PACKAGE_VERSION := 0.1.3
CORE_COMMIT := $(shell git rev-parse HEAD)
CHATCLIENTS_PROJECT := src/ForgeMission.ChatClients/ForgeMission.ChatClients.csproj
CHATCLIENTS_TEST_PROJECT := tests/ForgeMission.Mcl.Tests/ForgeMission.Mcl.Tests.csproj
CHATCLIENTS_PACKAGE_DIR := artifacts/packages
CHATCLIENTS_PACKAGE_VERSION := 0.1.2
CHATCLIENTS_COMMIT := $(shell git rev-parse HEAD)
MISSIONREGISTRY_PROJECT := src/ForgeMission.MissionRegistry/ForgeMission.MissionRegistry.csproj
MISSIONREGISTRY_TEST_PROJECT := tests/ForgeMission.Mcl.Tests/ForgeMission.Mcl.Tests.csproj
MISSIONREGISTRY_PACKAGE_DIR := artifacts/packages
MISSIONREGISTRY_PACKAGE_VERSION := 0.1.0
MISSIONREGISTRY_COMMIT := $(shell git rev-parse HEAD)
SERVE_PROJECT := src/ForgeMission.Serve/ForgeMission.Serve.csproj
SERVE_TEST_PROJECT := tests/ForgeMission.Mcl.Tests/ForgeMission.Mcl.Tests.csproj
SERVE_PACKAGE_DIR := artifacts/packages
SERVE_PACKAGE_VERSION := 0.1.0
SERVE_COMMIT := $(shell git rev-parse HEAD)
DOCKER_PROJECT := src/ForgeMission.Docker/ForgeMission.Docker.csproj
DOCKER_TEST_PROJECT := tests/ForgeMission.Mcl.Tests/ForgeMission.Mcl.Tests.csproj
DOCKER_PACKAGE_DIR := artifacts/packages
DOCKER_PACKAGE_VERSION := 0.1.0
DOCKER_COMMIT := $(shell git rev-parse HEAD)

.PHONY: build test test-parser pack-parser verify-parser-package test-scout pack-scout verify-scout-package test-core pack-core verify-core-package test-chatclients pack-chatclients verify-chatclients-package test-missionregistry pack-missionregistry verify-missionregistry-package test-serve pack-serve verify-serve-package test-docker pack-docker verify-docker-package install build-linux clean

build:
	pwsh -NoProfile -File scripts/build.ps1 -Action build

.PHONY: test-terminal-extensions pack-terminal-extensions verify-terminal-extensions-package

test-terminal-extensions:
	dotnet test tests/ForgeMission.Mcl.Tests/ForgeMission.Mcl.Tests.csproj -c Release --filter "FullyQualifiedName~ForgeMission.Tests.Terminal"

pack-terminal-extensions:
	dotnet pack $(TERMINAL_EXTENSIONS_PROJECT) -c Release --output artifacts/packages -p:ContinuousIntegrationBuild=true -p:RepositoryCommit=$(TERMINAL_EXTENSIONS_COMMIT)

verify-terminal-extensions-package: test-terminal-extensions pack-terminal-extensions
	bash ./eng/verify-terminal-extensions-package.sh artifacts/packages $(TERMINAL_EXTENSIONS_COMMIT)

test:
	pwsh -NoProfile -File scripts/build.ps1 -Action test

test-parser:
	dotnet test $(PARSER_TEST_PROJECT) -c Release --filter "FullyQualifiedName~Parser"

pack-parser:
	dotnet pack $(PARSER_PROJECT) -c Release --output $(PARSER_PACKAGE_DIR) -p:ContinuousIntegrationBuild=true -p:RepositoryCommit=$(PARSER_COMMIT)

verify-parser-package: test-parser pack-parser
	bash ./eng/verify-parser-package.sh $(PARSER_PACKAGE_DIR) $(PARSER_COMMIT)

test-scout:
	dotnet test $(SCOUT_TEST_PROJECT) -c Release --filter "FullyQualifiedName~Scout"

pack-scout:
	dotnet pack $(SCOUT_PROJECT) -c Release --output $(SCOUT_PACKAGE_DIR) -p:ContinuousIntegrationBuild=true -p:RepositoryCommit=$(SCOUT_COMMIT)

verify-scout-package: test-scout pack-scout
	bash ./eng/verify-scout-package.sh $(SCOUT_PACKAGE_DIR) $(SCOUT_COMMIT)

test-core:
	env -u MCL_API_KEY dotnet test $(CORE_TEST_PROJECT) -c Release --filter "FullyQualifiedName~ForgeMission.Tests.Adapters|FullyQualifiedName~ForgeMission.Tests.Experts|FullyQualifiedName~ForgeMission.Tests.Manifest|FullyQualifiedName~ForgeMission.Tests.Resolution|FullyQualifiedName~ForgeMission.Tests.Rules|FullyQualifiedName~ForgeMission.Tests.Runtime|FullyQualifiedName~ForgeMission.Tests.Tools"

pack-core:
	dotnet pack $(CORE_PROJECT) -c Release --output $(CORE_PACKAGE_DIR) -p:ContinuousIntegrationBuild=true -p:RepositoryCommit=$(CORE_COMMIT)

verify-core-package: test-core pack-core
	bash ./eng/verify-core-package.sh $(CORE_PACKAGE_DIR) $(CORE_COMMIT)

test-chatclients:
	dotnet test $(CHATCLIENTS_TEST_PROJECT) -c Release --filter "FullyQualifiedName~ForgeMission.Tests.ChatClients"

pack-chatclients:
	dotnet pack $(CHATCLIENTS_PROJECT) -c Release --output $(CHATCLIENTS_PACKAGE_DIR) -p:ContinuousIntegrationBuild=true -p:RepositoryCommit=$(CHATCLIENTS_COMMIT)

verify-chatclients-package: test-chatclients pack-chatclients
	bash ./eng/verify-chatclients-package.sh $(CHATCLIENTS_PACKAGE_DIR) $(CHATCLIENTS_COMMIT)

test-missionregistry:
	dotnet test $(MISSIONREGISTRY_TEST_PROJECT) -c Release --filter "FullyQualifiedName~ForgeMission.Tests.Registry"

pack-missionregistry:
	dotnet pack $(MISSIONREGISTRY_PROJECT) -c Release --output $(MISSIONREGISTRY_PACKAGE_DIR) -p:ContinuousIntegrationBuild=true -p:RepositoryCommit=$(MISSIONREGISTRY_COMMIT)

verify-missionregistry-package: test-missionregistry pack-missionregistry
	bash ./eng/verify-missionregistry-package.sh $(MISSIONREGISTRY_PACKAGE_DIR) $(MISSIONREGISTRY_COMMIT)

test-serve:
	dotnet test $(SERVE_TEST_PROJECT) -c Release --filter "FullyQualifiedName~ForgeMission.Tests.Integration.ConvergedServeTests"

pack-serve:
	dotnet pack $(SERVE_PROJECT) -c Release --output $(SERVE_PACKAGE_DIR) -p:ContinuousIntegrationBuild=true -p:RepositoryCommit=$(SERVE_COMMIT)

verify-serve-package: test-serve pack-serve
	bash ./eng/verify-serve-package.sh $(SERVE_PACKAGE_DIR) $(SERVE_COMMIT)

test-docker:
	dotnet test $(DOCKER_TEST_PROJECT) -c Release --filter "FullyQualifiedName~ForgeMission.Tests.Docker"

pack-docker:
	dotnet pack $(DOCKER_PROJECT) -c Release --output $(DOCKER_PACKAGE_DIR) -p:ContinuousIntegrationBuild=true -p:RepositoryCommit=$(DOCKER_COMMIT)

verify-docker-package: test-docker pack-docker
	bash ./eng/verify-docker-package.sh $(DOCKER_PACKAGE_DIR) $(DOCKER_COMMIT)

install:
	pwsh -NoProfile -File scripts/build.ps1 -Action install

build-linux:
	pwsh -NoProfile -File scripts/build.ps1 -Action publish -Rid linux-x64

clean:
	pwsh -NoProfile -File scripts/build.ps1 -Action clean

.PHONY: cli-verify cli-package cli-script-test release-prepare release-publish

cli-verify:
	pwsh -NoProfile -File scripts/build.ps1 -Action verify

cli-package:
	pwsh -NoProfile -File scripts/build.ps1 -Action package

cli-script-test:
	pwsh -NoProfile -File scripts/tests.ps1

release-prepare:
	pwsh -NoProfile -File scripts/release.ps1 -Action prepare

release-publish:
	pwsh -NoProfile -File scripts/release.ps1 -Action publish
