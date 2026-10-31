using System.Net;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Sonos2Mqtt.Client;

public record SonosTrack(string? Title, string? Artist, string? Album, string? AlbumArtUri, string? TrackUri, string? Duration);

/// <summary>
/// Turns the short URIs people put in commands ("spotify:playlist:...") into the URI and DIDL-Lite metadata a
/// speaker needs, and reads track details back out of the metadata a speaker reports.
/// </summary>
/// <remarks>
/// The guessing is a port of node-sonos-ts's MetadataHelper, which sonos2mqtt used, and produces the same output
/// byte for byte (see the tests). Anything it does not recognise is passed through with empty metadata, which is
/// what plain http(s) URLs need anyway.
/// </remarks>
public static class SonosMetadata
{
    private const string DidlOpen = "<DIDL-Lite xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:upnp=\"urn:schemas-upnp-org:metadata-1-0/upnp/\" xmlns:r=\"urn:schemas-rinconnetworks-com:metadata-1-0/\" xmlns=\"urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/\">";

    private static readonly XNamespace DidlNs = "urn:schemas-upnp-org:metadata-1-0/DIDL-Lite/";
    private static readonly XNamespace DcNs = "http://purl.org/dc/elements/1.1/";
    private static readonly XNamespace UpnpNs = "urn:schemas-upnp-org:metadata-1-0/upnp/";
    private static readonly XNamespace RinconNs = "urn:schemas-rinconnetworks-com:metadata-1-0/";

    private record GuessedTrack(string TrackUri, string ItemId, string UpnpClass, string CdUdn, string? Title = null, string? ParentId = null);

    public static (string TrackUri, string Metadata) Guess(string trackUri, string spotifyRegion)
    {
        var track = GuessTrack(trackUri, spotifyRegion);
        if (track == null)
        {
            return (trackUri, "");
        }

        return (WebUtility.HtmlDecode(Uri.UnescapeDataString(track.TrackUri)), ToDidl(track));
    }

    private static GuessedTrack? GuessTrack(string trackUri, string spotifyRegion)
    {
        if (trackUri.StartsWith("file:///jffs/settings/savedqueues.rsq#", StringComparison.Ordinal)
            || trackUri.StartsWith("sonos:playlist:", StringComparison.Ordinal))
        {
            var queueId = new string(trackUri.Where(char.IsAsciiDigit).ToArray());
            if (queueId.Length > 0)
            {
                return new GuessedTrack($"file:///jffs/settings/savedqueues.rsq#{queueId}", $"SQ:{queueId}", "object.container.playlistContainer", "RINCON_AssociatedZPUDN");
            }
        }

        var parts = trackUri.Split(':');
        if ((parts.Length == 3 || parts.Length == 5) && parts[0] == "spotify")
        {
            return GuessSpotifyTrack(trackUri, parts[1], spotifyRegion);
        }

        return null;
    }

    private static GuessedTrack? GuessSpotifyTrack(string trackUri, string kind, string region)
    {
        var spotifyUri = trackUri.Replace(":", "%3a");
        var cdUdn = $"SA_RINCON{region}_X_#Svc{region}-0-Token";

        return kind switch
        {
            "album" => new GuessedTrack($"x-rincon-cpcontainer:1004206c{spotifyUri}?sid=9&flags=8300&sn=7", $"0004206c{spotifyUri}", "object.container.album.musicAlbum", cdUdn, Title: ""),
            "artistRadio" => new GuessedTrack($"x-sonosapi-radio:{spotifyUri}?sid=9&flags=8300&sn=7", $"100c206c{spotifyUri}", "object.item.audioItem.audioBroadcast.#artistRadio", cdUdn, Title: "Artist radio", ParentId: $"10052064{spotifyUri.Replace("artistRadio", "artist")}"),
            "artistTopTracks" => new GuessedTrack($"x-rincon-cpcontainer:100e206c{spotifyUri}?sid=9&flags=8300&sn=7", $"100e206c{spotifyUri}", "object.container.playlistContainer", cdUdn, Title: "", ParentId: $"10052064{spotifyUri.Replace("artistTopTracks", "artist")}"),
            "playlist" => new GuessedTrack($"x-rincon-cpcontainer:1006206c{spotifyUri}?sid=9&flags=8300&sn=7", $"1006206c{spotifyUri}", "object.container.playlistContainer", cdUdn, Title: "Spotify playlist", ParentId: "10fe2664playlists"),
            "track" => new GuessedTrack($"x-sonos-spotify:{spotifyUri}?sid=9&amp;flags=8224&amp;sn=7", $"00032020{spotifyUri}", "object.item.audioItem.musicTrack", cdUdn, Title: ""),
            "user" => new GuessedTrack($"x-rincon-cpcontainer:10062a6c{spotifyUri}?sid=9&flags=10860&sn=7", $"10062a6c{spotifyUri}", "object.container.playlistContainer", cdUdn, Title: "User's playlist", ParentId: "10082664playlists"),
            _ => null,
        };
    }

    private static string ToDidl(GuessedTrack track)
    {
        var builder = new StringBuilder(DidlOpen);
        builder.Append($"<item id=\"{track.ItemId}\" restricted=\"true\"");
        builder.Append(track.ParentId != null ? $" parentID=\"{track.ParentId}\">" : ">");
        if (track.Title != null)
        {
            builder.Append($"<dc:title>{track.Title}</dc:title>");
        }
        builder.Append($"<upnp:class>{track.UpnpClass}</upnp:class>");
        builder.Append($"<desc id=\"cdudn\" nameSpace=\"urn:schemas-rinconnetworks-com:metadata-1-0/\">{track.CdUdn}</desc>");
        builder.Append("</item></DIDL-Lite>");
        return builder.ToString();
    }

    /// <summary>
    /// Reads the current track out of GetPositionInfo. Radio streams carry the now-playing text in
    /// r:streamContent rather than in the title. Relative album art is made absolute against the speaker.
    /// </summary>
    public static SonosTrack? ParseTrack(PositionInfo position, Uri speakerBaseAddress)
    {
        var metadata = position.TrackMetaData;
        if (string.IsNullOrEmpty(metadata) || metadata == "NOT_IMPLEMENTED")
        {
            return string.IsNullOrEmpty(position.TrackUri)
                ? null
                : new SonosTrack(null, null, null, null, position.TrackUri, NullIfEmpty(position.TrackDuration));
        }

        XElement? item;
        try
        {
            item = XDocument.Parse(metadata).Root?.Element(DidlNs + "item");
        }
        catch (XmlException)
        {
            item = null;
        }

        var title = NullIfEmpty(item?.Element(DcNs + "title")?.Value);
        var streamContent = NullIfEmpty(item?.Element(RinconNs + "streamContent")?.Value);
        var albumArt = NullIfEmpty(item?.Element(UpnpNs + "albumArtURI")?.Value);
        if (albumArt != null && Uri.TryCreate(speakerBaseAddress, albumArt, out var absoluteArt))
        {
            albumArt = absoluteArt.ToString();
        }

        return new SonosTrack(
            streamContent ?? title,
            NullIfEmpty(item?.Element(DcNs + "creator")?.Value),
            NullIfEmpty(item?.Element(UpnpNs + "album")?.Value),
            albumArt,
            NullIfEmpty(position.TrackUri),
            NullIfEmpty(position.TrackDuration));
    }

    /// <summary>
    /// Whether a URI's metadata marks it as a live stream, which has no position to seek back to.
    /// </summary>
    public static bool IsBroadcast(string metadata) =>
        metadata.Contains("object.item.audioItem.audioBroadcast", StringComparison.Ordinal);

    private static string? NullIfEmpty(string? value) =>
        string.IsNullOrEmpty(value) || value == "NOT_IMPLEMENTED" ? null : value;
}
