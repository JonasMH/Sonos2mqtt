build-image:
	docker build -t sonos2mqtt:latest -f src/Sonos2Mqtt/Dockerfile .

.PHONY: test
test:
	@dotnet test
