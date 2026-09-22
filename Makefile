UNAME_S := $(shell uname -s)
UNAME_M := $(shell uname -m)

ifeq ($(UNAME_S),Darwin)
  ifeq ($(UNAME_M),arm64)
    RID := osx-arm64
  else
    RID := osx-x64
  endif
else ifeq ($(UNAME_S),Linux)
  ifeq ($(UNAME_M),aarch64)
    RID := linux-arm64
  else
    RID := linux-x64
  endif
endif

ifeq ($(OS),Windows_NT)
  RID := win-arm64
endif

INSTALL_DIR := $(HOME)/.local/bin
CLI := src/ForgeMission.Cli
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
CORE_PACKAGE_VERSION := 0.1.0
CORE_COMMIT := $(shell git rev-parse HEAD)
CHATCLIENTS_PROJECT := src/ForgeMission.ChatClients/ForgeMission.ChatClients.csproj
CHATCLIENTS_TEST_PROJECT := tests/ForgeMission.Mcl.Tests/ForgeMission.Mcl.Tests.csproj
CHATCLIENTS_PACKAGE_DIR := artifacts/packages
CHATCLIENTS_PACKAGE_VERSION := 0.1.0
CHATCLIENTS_COMMIT := $(shell git rev-parse HEAD)
MISSIONREGISTRY_PROJECT := src/ForgeMission.MissionRegistry/ForgeMission.MissionRegistry.csproj
MISSIONREGISTRY_TEST_PROJECT := tests/ForgeMission.Mcl.Tests/ForgeMission.Mcl.Tests.csproj
MISSIONREGISTRY_PACKAGE_DIR := artifacts/packages
MISSIONREGISTRY_PACKAGE_VERSION := 0.1.0
MISSIONREGISTRY_COMMIT := $(shell git rev-parse HEAD)

.PHONY: build test test-parser pack-parser verify-parser-package test-scout pack-scout verify-scout-package test-core pack-core verify-core-package test-chatclients pack-chatclients verify-chatclients-package test-missionregistry pack-missionregistry verify-missionregistry-package install build-linux clean

build:
	dotnet build ForgeMission.slnx

test:
	dotnet test ForgeMission.slnx

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

install:
	dotnet publish $(CLI) -c Release -r $(RID) -o $(INSTALL_DIR)

build-linux:
	dotnet publish $(CLI) -c Release -r linux-x64 -o . --self-contained

clean:
	dotnet clean ForgeMission.slnx
