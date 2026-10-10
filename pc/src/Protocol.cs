namespace MicBridge
{
    // Wire format shared with the Android app (android/src/app/micbridge/Protocol.java).
    // Every datagram starts with 'M' 'B' <version> <type>; integers are little-endian.
    //
    //   DISCOVER   pc -> phone   (no payload)
    //   ANNOUNCE   phone -> pc   state:1  phoneId:8  nameLen:1  name
    //   CONNECT    pc -> phone   session:4  pcId:8  nameLen:1  name
    //   ACCEPT     phone -> pc   session:4  sampleRate:4  channels:1  phoneId:8  nameLen:1  name
    //   PENDING    phone -> pc   session:4            (waiting for "Allow" on the phone)
    //   REJECT     phone -> pc   session:4  reason:1
    //   AUDIO      phone -> pc   session:4  seq:4  flags:1  pad:3  pcm16le...
    //   KEEPALIVE  pc -> phone   session:4
    //   BYE        either way    session:4
    static class Proto
    {
        public const int Port = 28777;
        public const byte Magic0 = (byte)'M', Magic1 = (byte)'B', Version = 1;

        public const byte Discover = 1, Announce = 2, Connect = 3, Accept = 4, Pending = 5,
                          Reject = 6, Audio = 7, KeepAlive = 8, Bye = 9;

        public const int AudioHeader = 16;
        public const byte RejectDenied = 1, RejectBusy = 2, RejectMic = 3;
        public const byte FlagMuted = 1;
    }
}
