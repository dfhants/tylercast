import Foundation

/// What the test TV is known to accept (see README.md, tested 2026-11-15).
public enum DLNA {
    public static let rendererST = "urn:schemas-upnp-org:device:MediaRenderer:1"
    public static let avTransport = "urn:schemas-upnp-org:service:AVTransport:1"
    public static let connectionManager = "urn:schemas-upnp-org:service:ConnectionManager:1"

    /// DLNA.ORG_FLAGS: streaming transfer mode, background transfer, connection
    /// stall, DLNA 1.5 (bits 24, 22, 21, 20).
    public static let flags = "01700000000000000000000000000000"
    /// The TV lists H.264 in 188-byte TS only with AC-3 audio, 50Hz family.
    public static let profile = "AVC_TS_HD_50_AC3_ISO"
    /// DLNA.ORG_PN for H.264 TS with each audio codec (as mirror.py's --audio).
    public static func profile(for codec: AudioCodec) -> String {
        codec == .aac ? "AVC_TS_MP_HD_AAC_MULT5_ISO" : profile
    }
    public static let defaultMime = "video/mpeg"
    public static let serverHeader = "TylerCast/0.1 UPnP/1.0 DLNADOC/1.50"

    /// `contentFeatures.dlna.org` value. OP=00: no seeking of any kind.
    public static func contentFeatures(profile: String? = profile) -> String {
        let pn = profile.map { "DLNA.ORG_PN=\($0);" } ?? ""
        return "\(pn)DLNA.ORG_OP=00;DLNA.ORG_CI=0;DLNA.ORG_FLAGS=\(flags)"
    }

    public static func protocolInfo(mime: String = defaultMime, features: String) -> String {
        "http-get:*:\(mime):\(features)"
    }
}
