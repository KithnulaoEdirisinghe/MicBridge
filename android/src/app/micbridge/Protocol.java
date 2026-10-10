package app.micbridge;

/**
 * Wire format shared with the PC app (pc/src/Protocol.cs).
 * Every datagram starts with 'M' 'B' VERSION TYPE; integers are little-endian.
 *
 *   DISCOVER   pc -> phone   (no payload)
 *   ANNOUNCE   phone -> pc   state:1  phoneId:8  nameLen:1  name
 *   CONNECT    pc -> phone   session:4  pcId:8  nameLen:1  name
 *   ACCEPT     phone -> pc   session:4  sampleRate:4  channels:1  phoneId:8  nameLen:1  name
 *   PENDING    phone -> pc   session:4            (waiting for "Allow" on the phone)
 *   REJECT     phone -> pc   session:4  reason:1
 *   AUDIO      phone -> pc   session:4  seq:4  flags:1  pad:3  pcm16le...
 *   KEEPALIVE  pc -> phone   session:4
 *   BYE        either way    session:4
 */
final class Protocol {
    static final int PORT = 28777;
    static final byte MAGIC0 = 'M', MAGIC1 = 'B', VERSION = 1;

    static final byte DISCOVER = 1, ANNOUNCE = 2, CONNECT = 3, ACCEPT = 4, PENDING = 5,
            REJECT = 6, AUDIO = 7, KEEPALIVE = 8, BYE = 9;

    static final int AUDIO_HEADER = 16;
    static final byte REJECT_DENIED = 1, REJECT_BUSY = 2, REJECT_MIC = 3;
    static final byte FLAG_MUTED = 1;

    private Protocol() { }

    static void putInt(byte[] b, int offset, int value) {
        b[offset] = (byte) value;
        b[offset + 1] = (byte) (value >> 8);
        b[offset + 2] = (byte) (value >> 16);
        b[offset + 3] = (byte) (value >> 24);
    }

    static int getInt(byte[] b, int offset) {
        return (b[offset] & 0xFF) | (b[offset + 1] & 0xFF) << 8 | (b[offset + 2] & 0xFF) << 16 | (b[offset + 3] & 0xFF) << 24;
    }

    static void putLong(byte[] b, int offset, long value) {
        for (int i = 0; i < 8; i++) b[offset + i] = (byte) (value >> (8 * i));
    }

    static long getLong(byte[] b, int offset) {
        long value = 0;
        for (int i = 7; i >= 0; i--) value = (value << 8) | (b[offset + i] & 0xFF);
        return value;
    }
}
