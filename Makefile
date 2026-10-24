SPECRIG := dotnet run --project src/Specrig.Tool --

PROTOCOL ?= ../protocol

build:
	dotnet build Specrig.slnx

generate:
	$(SPECRIG) surface samples/shop
	$(SPECRIG) agents samples/shop
	$(SPECRIG) surface samples/reminders
	$(SPECRIG) agents samples/reminders

test:
	dotnet test Specrig.slnx

# Without the .NET SDK, and in CI. The resources start their containers beside this
# one, through the host's Docker, and are reached at the ports it publishes them on.
DOCKER := docker run --rm -v $(CURDIR):/repo -v specrig-dotnet-nuget:/root/.nuget/packages \
	-v /var/run/docker.sock:/var/run/docker.sock -e TESTCONTAINERS_HOST_OVERRIDE=host.docker.internal \
	-w /repo mcr.microsoft.com/dotnet/sdk:10.0

generate-docker:
	$(DOCKER) sh -c '$(SPECRIG) surface samples/shop && $(SPECRIG) agents samples/shop && $(SPECRIG) surface samples/reminders && $(SPECRIG) agents samples/reminders'

test-docker:
	$(DOCKER) dotnet test Specrig.slnx

protocol:
	rm -rf protocol
	mkdir -p protocol/fixtures
	cp -R $(PROTOCOL)/README.md $(PROTOCOL)/records.md $(PROTOCOL)/guard.md $(PROTOCOL)/modes.yaml \
		$(PROTOCOL)/specrig-md.md $(PROTOCOL)/agents protocol/
	cp -R $(PROTOCOL)/fixtures/records $(PROTOCOL)/fixtures/agents $(PROTOCOL)/fixtures/markers protocol/fixtures/

.PHONY: build generate test generate-docker test-docker protocol
