namespace SpiceClient.Protocol;

/// <summary>
/// Numeric constants for the SPICE protocol, ported from spice-html5 enums.js.
/// Only the values needed by this client are included.
/// </summary>
public static class SpiceConstants
{
    public const string Magic = "REDQ";
    public const uint VersionMajor = 2;
    public const uint VersionMinor = 2;

    // RSA pub key block length in the link reply: 1024/8 + 34 = 162 bytes (DER SubjectPublicKeyInfo).
    public const int TicketPubKeyBytes = 1024 / 8 + 34;
    public const int TicketKeyPairBytes = 1024 / 8; // 128 byte RSA ciphertext

    // Common capabilities
    public const int CAP_PROTOCOL_AUTH_SELECTION = 0;
    public const int CAP_AUTH_SPICE = 1;
    public const int CAP_AUTH_SASL = 2;
    public const int CAP_MINI_HEADER = 3;

    public const int MAIN_CAP_AGENT_CONNECTED_TOKENS = 2;

    // Link errors
    public const uint LINK_ERR_OK = 0;
    public const uint LINK_ERR_PERMISSION_DENIED = 7;

    // Channels
    public const byte CHANNEL_MAIN = 1;
    public const byte CHANNEL_DISPLAY = 2;
    public const byte CHANNEL_INPUTS = 3;
    public const byte CHANNEL_CURSOR = 4;
    public const byte CHANNEL_PLAYBACK = 5;
    public const byte CHANNEL_RECORD = 6;
    public const byte CHANNEL_TUNNEL = 7;
    public const byte CHANNEL_SMARTCARD = 8;
    public const byte CHANNEL_USBREDIR = 9;
    public const byte CHANNEL_PORT = 10;
    public const byte CHANNEL_WEBDAV = 11;

    // Common messages (server -> client)
    public const ushort MSG_MIGRATE = 1;
    public const ushort MSG_MIGRATE_DATA = 2;
    public const ushort MSG_SET_ACK = 3;
    public const ushort MSG_PING = 4;
    public const ushort MSG_WAIT_FOR_CHANNELS = 5;
    public const ushort MSG_DISCONNECTING = 6;
    public const ushort MSG_NOTIFY = 7;

    // Common messages (client -> server)
    public const ushort MSGC_ACK_SYNC = 1;
    public const ushort MSGC_ACK = 2;
    public const ushort MSGC_PONG = 3;

    // Main channel (server -> client)
    public const ushort MSG_MAIN_INIT = 103;
    public const ushort MSG_MAIN_CHANNELS_LIST = 104;
    public const ushort MSG_MAIN_MOUSE_MODE = 105;
    public const ushort MSG_MAIN_MULTI_MEDIA_TIME = 106;
    public const ushort MSG_MAIN_AGENT_CONNECTED = 107;
    public const ushort MSG_MAIN_AGENT_DISCONNECTED = 108;
    public const ushort MSG_MAIN_AGENT_DATA = 109;
    public const ushort MSG_MAIN_AGENT_TOKEN = 110;
    public const ushort MSG_MAIN_NAME = 113;
    public const ushort MSG_MAIN_UUID = 114;
    public const ushort MSG_MAIN_AGENT_CONNECTED_TOKENS = 115;

    // Main channel (client -> server)
    public const ushort MSGC_MAIN_ATTACH_CHANNELS = 104;
    public const ushort MSGC_MAIN_MOUSE_MODE_REQUEST = 105;
    public const ushort MSGC_MAIN_AGENT_START = 106;
    public const ushort MSGC_MAIN_AGENT_DATA = 107;
    public const ushort MSGC_MAIN_AGENT_TOKEN = 108;

    // Guest agent (vdagent): minimal subset for dynamic resize
    public const uint VD_AGENT_PROTOCOL = 1;
    public const int VD_AGENT_MAX_DATA_SIZE = 2048;
    public const uint VD_AGENT_MONITORS_CONFIG = 2;
    public const uint VD_AGENT_CLIPBOARD = 4;
    public const uint VD_AGENT_ANNOUNCE_CAPABILITIES = 6;
    public const uint VD_AGENT_CLIPBOARD_GRAB = 7;
    public const uint VD_AGENT_CLIPBOARD_REQUEST = 8;
    public const uint VD_AGENT_CLIPBOARD_RELEASE = 9;
    public const uint VD_AGENT_FILE_XFER_START = 10;
    public const uint VD_AGENT_FILE_XFER_STATUS = 11;
    public const uint VD_AGENT_FILE_XFER_DATA = 12;
    public const uint VD_AGENT_MAX_CLIPBOARD = 14;
    public const int VD_AGENT_CAP_MOUSE_STATE = 0;
    public const int VD_AGENT_CAP_MONITORS_CONFIG = 1;
    public const int VD_AGENT_CAP_REPLY = 2;
    public const int VD_AGENT_CAP_CLIPBOARD = 3;
    public const int VD_AGENT_CAP_CLIPBOARD_BY_DEMAND = 5;
    public const int VD_AGENT_CAP_CLIPBOARD_SELECTION = 6;
    public const int VD_AGENT_CAP_MAX_CLIPBOARD = 10;

    // Clipboard data types + selection. Text and images only: a file copy is not shared, because
    // the type that would carry it (FILE_LIST = 6) carries paths into a WebDAV share the client
    // would have to host, not bytes. Files go into the guest over VD_AGENT_FILE_XFER_* instead.
    //
    // PNG is the baseline every agent implements; BMP is the safe second (vdagent-win maps
    // CF_DIB to {PNG, BMP}, the Linux agent maps all four). TIFF and JPG are declared for
    // completeness: a guest may offer them, so the receive side accepts them, but this client
    // never advertises them.
    public const uint VD_AGENT_CLIPBOARD_NONE = 0;
    public const uint VD_AGENT_CLIPBOARD_UTF8_TEXT = 1;
    public const uint VD_AGENT_CLIPBOARD_IMAGE_PNG = 2;
    public const uint VD_AGENT_CLIPBOARD_IMAGE_BMP = 3;
    public const uint VD_AGENT_CLIPBOARD_IMAGE_TIFF = 4;
    public const uint VD_AGENT_CLIPBOARD_IMAGE_JPG = 5;
    public const byte VD_AGENT_CLIPBOARD_SELECTION_CLIPBOARD = 0;

    // File-transfer status results
    public const uint VD_AGENT_FILE_XFER_STATUS_CAN_SEND_DATA = 0;
    public const uint VD_AGENT_FILE_XFER_STATUS_CANCELLED = 1;
    public const uint VD_AGENT_FILE_XFER_STATUS_ERROR = 2;
    public const uint VD_AGENT_FILE_XFER_STATUS_SUCCESS = 3;

    // Display channel (server -> client)
    public const ushort MSG_DISPLAY_MODE = 101;
    public const ushort MSG_DISPLAY_MARK = 102;
    public const ushort MSG_DISPLAY_RESET = 103;
    public const ushort MSG_DISPLAY_COPY_BITS = 104;
    public const ushort MSG_DISPLAY_INVAL_LIST = 105;
    public const ushort MSG_DISPLAY_INVAL_ALL_PIXMAPS = 106;
    public const ushort MSG_DISPLAY_INVAL_PALETTE = 107;
    public const ushort MSG_DISPLAY_INVAL_ALL_PALETTES = 108;
    public const ushort MSG_DISPLAY_STREAM_CREATE = 122;
    public const ushort MSG_DISPLAY_STREAM_DATA = 123;
    public const ushort MSG_DISPLAY_STREAM_CLIP = 124;
    public const ushort MSG_DISPLAY_STREAM_DESTROY = 125;
    public const ushort MSG_DISPLAY_STREAM_DESTROY_ALL = 126;
    public const ushort MSG_DISPLAY_DRAW_FILL = 302;
    public const ushort MSG_DISPLAY_DRAW_OPAQUE = 303;
    public const ushort MSG_DISPLAY_DRAW_COPY = 304;
    public const ushort MSG_DISPLAY_DRAW_BLEND = 305;
    public const ushort MSG_DISPLAY_DRAW_ALPHA_BLEND = 313;
    public const ushort MSG_DISPLAY_SURFACE_CREATE = 314;
    public const ushort MSG_DISPLAY_SURFACE_DESTROY = 315;
    public const ushort MSG_DISPLAY_STREAM_DATA_SIZED = 316;
    public const ushort MSG_DISPLAY_MONITORS_CONFIG = 317;
    public const ushort MSG_DISPLAY_STREAM_ACTIVATE_REPORT = 319;

    // Display channel (client -> server)
    public const ushort MSGC_DISPLAY_INIT = 101;
    public const ushort MSGC_DISPLAY_STREAM_REPORT = 102;
    public const ushort MSGC_DISPLAY_PREFERRED_COMPRESSION = 103;

    // Display channel capabilities
    public const int DISPLAY_CAP_SIZED_STREAM = 0;
    public const int DISPLAY_CAP_STREAM_REPORT = 4;
    public const int DISPLAY_CAP_PREF_COMPRESSION = 6;
    public const int DISPLAY_CAP_MULTI_CODEC = 8;
    public const int DISPLAY_CAP_CODEC_MJPEG = 9;

    // Image-compression modes (payload of MSGC_DISPLAY_PREFERRED_COMPRESSION).
    // Full set: INVALID=0, OFF=1, AUTO_GLZ=2, AUTO_LZ=3, QUIC=4, GLZ=5, LZ=6, LZ4=7.
    public const byte IMAGE_COMPRESSION_OFF = 1;     // raw bitmaps
    public const byte IMAGE_COMPRESSION_AUTO_GLZ = 2; // server default (GLZ + QUIC), not decodable here
    public const byte IMAGE_COMPRESSION_LZ = 6;      // LZ only (no QUIC/GLZ), the safe default for this client

    // Video stream codecs
    public const byte VIDEO_CODEC_TYPE_MJPEG = 1;
    public const byte VIDEO_CODEC_TYPE_VP8 = 2;

    // Inputs channel (server -> client)
    public const ushort MSG_INPUTS_INIT = 101;
    public const ushort MSG_INPUTS_KEY_MODIFIERS = 102;
    public const ushort MSG_INPUTS_MOUSE_MOTION_ACK = 111;

    // Inputs channel (client -> server)
    public const ushort MSGC_INPUTS_KEY_DOWN = 101;
    public const ushort MSGC_INPUTS_KEY_UP = 102;
    public const ushort MSGC_INPUTS_MOUSE_MOTION = 111;
    public const ushort MSGC_INPUTS_MOUSE_POSITION = 112;
    public const ushort MSGC_INPUTS_MOUSE_PRESS = 113;
    public const ushort MSGC_INPUTS_MOUSE_RELEASE = 114;

    public const int INPUT_MOTION_ACK_BUNCH = 4;

    // Cursor channel (server -> client)
    public const ushort MSG_CURSOR_INIT = 101;
    public const ushort MSG_CURSOR_RESET = 102;
    public const ushort MSG_CURSOR_SET = 103;
    public const ushort MSG_CURSOR_MOVE = 104;
    public const ushort MSG_CURSOR_HIDE = 105;
    public const ushort MSG_CURSOR_TRAIL = 106;
    public const ushort MSG_CURSOR_INVAL_ONE = 107;
    public const ushort MSG_CURSOR_INVAL_ALL = 108;

    // SpiceVMC channels (usbredir / smartcard / port): opaque byte-stream tunnel.
    // The usbredir channel carries the raw usbredir wire protocol in these messages.
    // We never advertise compression, so the server sends plain DATA (not COMPRESSED_DATA=102).
    public const ushort MSG_SPICEVMC_DATA = 101;   // server -> client
    public const ushort MSGC_SPICEVMC_DATA = 101;  // client -> server

    // Playback channel (server -> client). Speakers only; the client sends nothing here.
    public const ushort MSG_PLAYBACK_DATA = 101;     // u32 time + raw PCM bytes
    public const ushort MSG_PLAYBACK_MODE = 102;     // u32 time, u16 mode
    public const ushort MSG_PLAYBACK_START = 103;    // u32 channels, u16 format, u32 frequency, u32 time
    public const ushort MSG_PLAYBACK_STOP = 104;     // empty
    public const ushort MSG_PLAYBACK_VOLUME = 105;   // unused (VOLUME cap not advertised)
    public const ushort MSG_PLAYBACK_MUTE = 106;     // unused
    public const ushort MSG_PLAYBACK_LATENCY = 107;  // unused (LATENCY cap not advertised)

    // Audio data modes (SpiceMsgPlaybackMode.mode). We advertise no codec caps, so the
    // server falls back to RAW signed-16-bit PCM (CELT/OPUS would need a decoder we don't have).
    public const ushort AUDIO_DATA_MODE_INVALID = 0;
    public const ushort AUDIO_DATA_MODE_RAW = 1;
    public const ushort AUDIO_DATA_MODE_CELT_0_5_1 = 2;
    public const ushort AUDIO_DATA_MODE_OPUS = 3;

    // Audio sample formats (SpiceMsgPlaybackStart.format)
    public const ushort AUDIO_FMT_INVALID = 0;
    public const ushort AUDIO_FMT_S16 = 1;

    // Playback channel capabilities (bit positions). We advertise NONE → server uses RAW S16.
    public const int PLAYBACK_CAP_CELT_0_5_1 = 0;
    public const int PLAYBACK_CAP_VOLUME = 1;
    public const int PLAYBACK_CAP_LATENCY = 2;
    public const int PLAYBACK_CAP_OPUS = 3;

    // Mouse modes
    public const int MOUSE_MODE_SERVER = 1 << 0;
    public const int MOUSE_MODE_CLIENT = 1 << 1;

    // Mouse buttons
    public const byte MOUSE_BUTTON_LEFT = 1;
    public const byte MOUSE_BUTTON_MIDDLE = 2;
    public const byte MOUSE_BUTTON_RIGHT = 3;
    public const byte MOUSE_BUTTON_UP = 4;
    public const byte MOUSE_BUTTON_DOWN = 5;

    public const ushort MOUSE_BUTTON_MASK_LEFT = 1 << 0;
    public const ushort MOUSE_BUTTON_MASK_MIDDLE = 1 << 1;
    public const ushort MOUSE_BUTTON_MASK_RIGHT = 1 << 2;

    // Clip types
    public const byte CLIP_TYPE_NONE = 0;
    public const byte CLIP_TYPE_RECTS = 1;

    // Brush types
    public const byte BRUSH_TYPE_NONE = 0;
    public const byte BRUSH_TYPE_SOLID = 1;
    public const byte BRUSH_TYPE_PATTERN = 2;

    // Image types
    public const byte IMAGE_TYPE_BITMAP = 0;
    public const byte IMAGE_TYPE_QUIC = 1;
    public const byte IMAGE_TYPE_LZ_PLT = 100;
    public const byte IMAGE_TYPE_LZ_RGB = 101;
    public const byte IMAGE_TYPE_GLZ_RGB = 102;
    public const byte IMAGE_TYPE_FROM_CACHE = 103;
    public const byte IMAGE_TYPE_SURFACE = 104;
    public const byte IMAGE_TYPE_JPEG = 105;
    public const byte IMAGE_TYPE_FROM_CACHE_LOSSLESS = 106;
    public const byte IMAGE_TYPE_ZLIB_GLZ_RGB = 107;
    public const byte IMAGE_TYPE_JPEG_ALPHA = 108;

    public const byte IMAGE_FLAGS_CACHE_ME = 1 << 0;

    // Bitmap formats
    public const byte BITMAP_FMT_8BIT = 5;
    public const byte BITMAP_FMT_16BIT = 6;
    public const byte BITMAP_FMT_24BIT = 7;
    public const byte BITMAP_FMT_32BIT = 8;
    public const byte BITMAP_FMT_RGBA = 9;

    public const byte BITMAP_FLAGS_PAL_CACHE_ME = 1 << 0;
    public const byte BITMAP_FLAGS_PAL_FROM_CACHE = 1 << 1;
    public const byte BITMAP_FLAGS_TOP_DOWN = 1 << 2;

    // LZ image types
    public const uint LZ_IMAGE_TYPE_RGB16 = 6;
    public const uint LZ_IMAGE_TYPE_RGB24 = 7;
    public const uint LZ_IMAGE_TYPE_RGB32 = 8;
    public const uint LZ_IMAGE_TYPE_RGBA = 9;
    public const uint LZ_IMAGE_TYPE_XXXA = 10;

    // Surface
    public const uint SURFACE_FLAGS_PRIMARY = 1 << 0;
    public const uint SURFACE_FMT_16_555 = 16;
    public const uint SURFACE_FMT_32_xRGB = 32;
    public const uint SURFACE_FMT_16_565 = 80;
    public const uint SURFACE_FMT_32_ARGB = 96;

    // Cursor flags
    public const ushort CURSOR_FLAGS_NONE = 1 << 0;
    public const ushort CURSOR_FLAGS_CACHE_ME = 1 << 1;
    public const ushort CURSOR_FLAGS_FROM_CACHE = 1 << 2;

    // Cursor types
    public const byte CURSOR_TYPE_ALPHA = 0;
    public const byte CURSOR_TYPE_MONO = 1;
    public const byte CURSOR_TYPE_COLOR32 = 6;

    // Notify severity
    public const uint NOTIFY_SEVERITY_INFO = 0;
    public const uint NOTIFY_SEVERITY_WARN = 1;
    public const uint NOTIFY_SEVERITY_ERROR = 2;
}
