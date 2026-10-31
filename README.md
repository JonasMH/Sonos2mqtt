# Sonos2Mqtt

Bridges Sonos speakers to MQTT. A drop-in replacement for [svrooij/sonos2mqtt](https://github.com/svrooij/sonos2mqtt), which is no longer maintained, written in .NET and published as a native AOT image.

It keeps sonos2mqtt's topics, state shape and command names, so automations built against sonos2mqtt keep working.

```sh
docker pull ghcr.io/jonasmh/sonos2mqtt:latest
```

## Features

### Survives speakers that come and go

The household topology is read from any speaker that answers and refreshed every 30 seconds. A speaker that is listed but does not answer (unplugged, off the network) is logged once, marked `offline`, and retried every 15 seconds, without holding up the others. sonos2mqtt crashed on startup in that situation.

### Events, with polling as the fallback

With `Sonos:CallbackAddress` set, the bridge subscribes to each speaker's UPnP events (AVTransport and RenderingControl). An event makes the bridge read that speaker's state straight away, so changes show up within a round trip. While events arrive, each speaker is still polled every 30 seconds in case one got lost.

Events count as working only once one has actually arrived. If a speaker refuses the subscription, or accepts it but its events never reach the bridge, it is logged and that speaker is polled every second instead. Without a callback address, every speaker is polled every second. Either way only changes are published.

In Kubernetes, expose the bridge with a `hostPort` and build the callback address from the node's IP, so the pod can run on any node:

```yaml
env:
  - name: HOST_IP
    valueFrom:
      fieldRef:
        fieldPath: status.hostIP
  - name: Sonos__CallbackAddress
    value: http://$(HOST_IP):31604
ports:
  - name: http
    containerPort: 8080
    hostPort: 31604
```

### Publish speaker state to MQTT

`sonos/<uuid>`, retained, on every change:

```json
{
  "uuid": "RINCON_542A1B5A6FC201400",
  "name": "Master Bad",
  "groupName": "Master Bad",
  "coordinatorUuid": "RINCON_542A1B5A6FC201400",
  "transportState": "PAUSED_PLAYBACK",
  "volume": { "Master": 71 },
  "mute": { "Master": false },
  "currentTrack": {
    "title": "Fall",
    "artist": "notsure",
    "album": "Whois",
    "albumArtUri": "http://192.168.0.252:1400/getaa?...",
    "trackUri": "x-sonos-spotify:spotify%3atrack%3a4kJixTlHNg0cRyay3KG7Ew?sid=9&flags=8232&sn=1",
    "duration": "0:04:31"
  },
  "ts": 1793447433458
}
```

`transportState` is one of `PLAYING`, `PAUSED_PLAYBACK`, `STOPPED` or `TRANSITIONING`.

`sonos/<uuid>/availability`, retained, is `online` while the speaker answers and `offline` once it stops or leaves the household. The state topic keeps the last state the speaker reported, so check this before trusting it.

`sonos/connected` is `2` while the bridge is connected to MQTT.

### Control speakers over MQTT

Publish to `sonos/<uuid>/control`, or `sonos/<room name>/control`:

```json
{ "command": "volume", "input": 42 }
```

| Command | Input | |
| --- | --- | --- |
| `play`, `pause`, `stop`, `toggle`, `next`, `previous` | | Sent to the group's coordinator |
| `seek` | `"0:01:30"` | Coordinator |
| `selecttrack` | track number | Coordinator |
| `playmode` | `NORMAL`, `REPEAT_ALL`, `SHUFFLE`, ... | Coordinator |
| `volume` | 0-100 | |
| `volumeup`, `volumedown` | step, default 4 | |
| `mute`, `unmute` | | |
| `setbass`, `settreble` | -10 to 10 | |
| `sleep` | minutes (1-60) or `"hh:mm:ss"` | |
| `setavtransporturi` | URI | Spotify URIs (`spotify:playlist:...`) get their metadata filled in |
| `queue` | URI, or `{"trackUri", "positionInQueue", "enqueueAsNext"}` | |
| `clearqueue`, `switchtoqueue`, `switchtoline`, `switchtotv` | | |
| `joingroup` | room name to join | |
| `leavegroup` | | |
| `notify` | see below | |
| `adv-command` | see below | |

Commands for a speaker run one at a time, in the order they arrived. A failed command is published to `sonos/<uuid>/error` as `{"command": ..., "error": ...}`.

#### Notifications

Plays a clip and then puts the speaker back the way it was: volume, source, track, position, and playing or not.

```json
{
  "command": "notify",
  "input": {
    "trackUri": "https://example.com/doorbell.mp3",
    "onlyWhenPlaying": false,
    "timeout": 10,
    "volume": 60,
    "delayMs": 700
  }
}
```

- `onlyWhenPlaying` skips the clip if the speaker is not playing already.
- `timeout` is the longest the clip may play, in seconds. The default is 60.
- `volume` (1-100) is the volume to play the clip at.
- `delayMs` (1-4000) is a pause after changing the volume and between restore steps, for speakers that need it.

#### Advanced commands

`adv-command` takes `{"cmd": ..., "val": ...}`. `cmd` is either a sonos2mqtt device method (`AddUriToQueue`, `SetAVTransportURI`, `SeekTrack`, ...) or a raw UPnP action as `Service.Action`, with `val` holding the action's arguments. `InstanceID` defaults to 0.

```json
{ "command": "adv-command", "input": { "cmd": "AVTransportService.RemoveAllTracksFromQueue" } }
{ "command": "adv-command", "input": { "cmd": "RenderingControlService.SetLoudness", "val": { "Channel": "Master", "DesiredLoudness": true } } }
```

Add `"replyTopic": "reply"` next to `command` to have the action's response published to `sonos/<uuid>/reply`.

## Configuration

Settings come from `appsettings.json` or environment variables (`Sonos__SeedHosts__0=192.168.0.9`).

| Setting | Default | |
| --- | --- | --- |
| `Sonos:SeedHosts` | required | Speakers to ask for the topology, as `host` or `host:port`. One reachable speaker is enough |
| `Sonos:CallbackAddress` | none | Where speakers can reach the bridge for events, e.g. `http://192.168.0.31:31604`. Leave out to poll only |
| `Sonos:PollInterval` | `00:00:01` | How often each speaker is polled without events |
| `Sonos:EventPollInterval` | `00:00:30` | How often each speaker is polled while events arrive |
| `Sonos:SubscriptionTimeout` | `00:30:00` | Event subscription length; renewed halfway |
| `Sonos:UnreachableRetryInterval` | `00:00:15` | How often a speaker that does not answer is tried again |
| `Sonos:TopologyInterval` | `00:00:30` | How often the topology is refreshed |
| `Sonos:RequestTimeout` | `00:00:05` | Timeout per request to a speaker |
| `Sonos:SpotifyRegion` | `2311` | Spotify account region: `2311` Europe, `3079` North America |
| `MqttConnection:Server` | `mosquitto` | |
| `MqttConnection:Port` | `8883` | |
| `MqttConnection:UseTls` | `true` | Client certificates are read from `/data/mosquitto-client-certs/` |
| `MqttConnection:NodeId` | `sonos` | Topic prefix |

Health is served on `:8080/health`, Prometheus metrics on `:8080/metrics`, and speaker events are received on `:8080/events/<uuid>`.

## Differences from sonos2mqtt

- Back-to-back notifications each restore the speaker before the next one plays. sonos2mqtt played queued notifications in a row and restored once.
- Not implemented: `speak`, `speaktwo`, `notifytwo`, `setnightmode`, `setledstate`, `setbuttonlockstate`, the `sonos/set/...` and `sonos/cmd/...` topics, the `distinct` per-field topics, and Home Assistant discovery.
- Metadata guessing covers Spotify and Sonos playlists. Other services' URIs are passed through as they are.

## Development

```sh
dotnet test
cd src/Sonos2Mqtt && dotnet run   # appsettings.Development.json: MQTT on localhost:1883, seed 192.168.0.9
make build-image
```

Releases are built by pushing a tag: `git tag v1.0.0 && git push origin v1.0.0` runs the tests, pushes `ghcr.io/jonasmh/sonos2mqtt:v1.0.0` and `:latest`, and creates a GitHub release with generated notes.
