using Sonos2Mqtt.Client;

namespace Sonos2Mqtt.Test;

/// <summary>
/// Expected values are what sonos2mqtt 3.2.0's MetadataHelper.GuessMetaDataAndTrackUri produced for the same
/// input, taken from the svrooij/sonos2mqtt:3.2.0 image.
/// </summary>
public class SonosMetadataTests
{
    private const string DidlOpen = "<DIDL-Lite xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:upnp=\"urn:schemas-upnp-org:metadata-1-0/upnp/\" xmlns:r=\"urn:schemas-rinconnetworks-com:metadata-1-0/\" xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\">";
    private const string EuropeCdUdn = "<desc id=\"cdudn\" nameSpace=\"urn:schemas-rinconnetworks-com:metadata-1-0/\">SA_RINCON2311_X_#Svc2311-0-Token</desc>";

    [Fact]
    public void SpotifyPlaylistMatchesSonos2Mqtt()
    {
        var (uri, metadata) = SonosMetadata.Guess("spotify:playlist:37i9dQZF1DX5622XLsm5RS", "2311");

        Assert.Equal("x-rincon-cpcontainer:1006206cspotify:playlist:37i9dQZF1DX5622XLsm5RS?sid=9&flags=8300&sn=7", uri);
        Assert.Equal(
            DidlOpen
            + "<item id=\"1006206cspotify%3aplaylist%3a37i9dQZF1DX5622XLsm5RS\" restricted=\"true\" parentID=\"10fe2664playlists\">"
            + "<dc:title>Spotify playlist</dc:title><upnp:class>object.container.playlistContainer</upnp:class>"
            + EuropeCdUdn + "</item></DIDL-Lite>",
            metadata);
    }

    [Fact]
    public void SpotifyAlbumMatchesSonos2Mqtt()
    {
        var (uri, metadata) = SonosMetadata.Guess("spotify:album:4uLU6hMCjMI75M1A2tKUQC", "2311");

        Assert.Equal("x-rincon-cpcontainer:1004206cspotify:album:4uLU6hMCjMI75M1A2tKUQC?sid=9&flags=8300&sn=7", uri);
        Assert.Equal(
            DidlOpen
            + "<item id=\"0004206cspotify%3aalbum%3a4uLU6hMCjMI75M1A2tKUQC\" restricted=\"true\">"
            + "<dc:title></dc:title><upnp:class>object.container.album.musicAlbum</upnp:class>"
            + EuropeCdUdn + "</item></DIDL-Lite>",
            metadata);
    }

    [Fact]
    public void SpotifyTrackMatchesSonos2Mqtt()
    {
        var (uri, metadata) = SonosMetadata.Guess("spotify:track:2TpxZ7JUBn3uw46aR7qd6V", "2311");

        Assert.Equal("x-sonos-spotify:spotify:track:2TpxZ7JUBn3uw46aR7qd6V?sid=9&flags=8224&sn=7", uri);
        Assert.Equal(
            DidlOpen
            + "<item id=\"00032020spotify%3atrack%3a2TpxZ7JUBn3uw46aR7qd6V\" restricted=\"true\">"
            + "<dc:title></dc:title><upnp:class>object.item.audioItem.musicTrack</upnp:class>"
            + EuropeCdUdn + "</item></DIDL-Lite>",
            metadata);
    }

    [Fact]
    public void SpotifyRegionGoesIntoTheCdUdn()
    {
        var (_, metadata) = SonosMetadata.Guess("spotify:playlist:37i9dQZF1DX5622XLsm5RS", "3079");

        Assert.Contains("SA_RINCON3079_X_#Svc3079-0-Token", metadata);
    }

    [Fact]
    public void HttpUrlIsPassedThroughWithoutMetadata()
    {
        var (uri, metadata) = SonosMetadata.Guess("https://ha.home.jonasmhansen.com/local/ringbell01.mp3", "2311");

        Assert.Equal("https://ha.home.jonasmhansen.com/local/ringbell01.mp3", uri);
        Assert.Equal("", metadata);
    }

    [Fact]
    public void SonosPlaylistBecomesSavedQueue()
    {
        var (uri, metadata) = SonosMetadata.Guess("sonos:playlist:12", "2311");

        Assert.Equal("file:///jffs/settings/savedqueues.rsq#12", uri);
        Assert.Contains("<item id=\"SQ:12\" restricted=\"true\">", metadata);
        Assert.Contains("RINCON_AssociatedZPUDN", metadata);
    }

    [Fact]
    public void ParseTrackReadsDidl()
    {
        var position = new PositionInfo(3, "0:03:21",
            "<DIDL-Lite xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:upnp=\"urn:schemas-upnp-org:metadata-1-0/upnp/\" xmlns:r=\"urn:schemas-rinconnetworks-com:metadata-1-0/\" xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\">"
            + "<item id=\"-1\" parentID=\"-1\" restricted=\"true\"><upnp:albumArtURI>/getaa?s=1&amp;u=x</upnp:albumArtURI>"
            + "<dc:title>Song</dc:title><dc:creator>Artist</dc:creator><upnp:album>Album</upnp:album></item></DIDL-Lite>",
            "x-sonos-spotify:spotify%3atrack%3a123", "0:01:00");

        var track = SonosMetadata.ParseTrack(position, new Uri("http://192.168.0.36:1400"));

        Assert.NotNull(track);
        Assert.Equal("Song", track.Title);
        Assert.Equal("Artist", track.Artist);
        Assert.Equal("Album", track.Album);
        Assert.Equal("http://192.168.0.36:1400/getaa?s=1&u=x", track.AlbumArtUri);
        Assert.Equal("0:03:21", track.Duration);
    }

    [Fact]
    public void ParseTrackPrefersStreamContentForRadio()
    {
        var position = new PositionInfo(1, "0:00:00",
            "<DIDL-Lite xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:r=\"urn:schemas-rinconnetworks-com:metadata-1-0/\" xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\">"
            + "<item id=\"-1\"><dc:title>stream.mp3</dc:title><r:streamContent>Now playing - Band</r:streamContent></item></DIDL-Lite>",
            "x-rincon-mp3radio://example/stream.mp3", "0:12:00");

        var track = SonosMetadata.ParseTrack(position, new Uri("http://192.168.0.36:1400"));

        Assert.Equal("Now playing - Band", track?.Title);
    }

    [Fact]
    public void ParseTrackIsNullWhenNothingIsLoaded()
    {
        Assert.Null(SonosMetadata.ParseTrack(new PositionInfo(0, "NOT_IMPLEMENTED", "NOT_IMPLEMENTED", "", "NOT_IMPLEMENTED"), new Uri("http://192.168.0.36:1400")));
    }
}
