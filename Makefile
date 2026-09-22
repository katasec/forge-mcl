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

.PHONY: build test install build-linux clean

build:
	dotnet build ForgeMission.slnx

test:
	dotnet test ForgeMission.slnx

install:
	dotnet publish $(CLI) -c Release -r $(RID) -o $(INSTALL_DIR)

build-linux:
	dotnet publish $(CLI) -c Release -r linux-x64 -o . --self-contained

clean:
	dotnet clean ForgeMission.slnx
