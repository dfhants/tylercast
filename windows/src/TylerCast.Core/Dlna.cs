namespace TylerCast.Core;

/// <summary>The audio formats the muxer can carry.</summary>
public enum AudioCodec
{
    /// <summary>ATSC AC-3 (stream type 0x81, private stream 1): what the test TV lists for H.264 TS.</summary>
    Ac3,
    /// <summary>AAC-LC in ADTS (stream type 0x0F, audio stream 0xC0).</summary>
    Aac,
}

/// <summary>What the test TV is known to accept (see the Mac README, tested 2026-11-15).</summary>
public static class Dlna
{
    public const string RendererST = "urn:schemas-upnp-org:device:MediaRenderer:1";
    public const string AVTransport = "urn:schemas-upnp-org:service:AVTransport:1";
    public const string ConnectionManager = "urn:schemas-upnp-org:service:ConnectionManager:1";

    /// <summary>DLNA.ORG_FLAGS: streaming transfer mode, background transfer, connection
    /// stall, DLNA 1.5 (bits 24, 22, 21, 20).</summary>
    public const string Flags = "01700000000000000000000000000000";
    /// <summary>The TV lists H.264 in 188-byte TS only with AC-3 audio, 50Hz family.</summary>
    public const string Profile = "AVC_TS_HD_50_AC3_ISO";
    public const string DefaultMime = "video/mpeg";
    public const string ServerHeader = "TylerCast/0.1 UPnP/1.0 DLNADOC/1.50";

    /// <summary>DLNA.ORG_PN for H.264 TS with each audio codec.</summary>
    public static string ProfileFor(AudioCodec codec) => codec == AudioCodec.Aac ? "AVC_TS_MP_HD_AAC_MULT5_ISO" : Profile;

    /// <summary><c>contentFeatures.dlna.org</c> value. OP=00: no seeking of any kind.</summary>
    public static string ContentFeatures(string? profile = Profile)
    {
        var pn = profile is null ? "" : $"DLNA.ORG_PN={profile};";
        return $"{pn}DLNA.ORG_OP=00;DLNA.ORG_CI=0;DLNA.ORG_FLAGS={Flags}";
    }

    public static string ProtocolInfo(string features, string mime = DefaultMime) => $"http-get:*:{mime}:{features}";
}
