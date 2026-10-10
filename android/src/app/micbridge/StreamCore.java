package app.micbridge;

import java.io.IOException;
import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.InetSocketAddress;
import java.net.SocketTimeoutException;
import java.nio.charset.StandardCharsets;
import java.util.Arrays;
import java.util.HashSet;

/**
 * The phone side of MicBridge: answers PCs that are searching, lets one PC connect and then sends it
 * the microphone in 10 ms packets. Plain Java on purpose (no Android classes), so the very same code
 * can be run and tested on a desktop JVM.
 *
 * Threads: one receiver thread for the small control packets, plus one capture thread that only lives
 * while a PC is connected. The capture loop reuses a single buffer and packet, so it creates no garbage.
 */
public final class StreamCore {

    /** Where the audio comes from (the microphone on a phone, a tone generator in tests). */
    public interface AudioSource {
        /** Opens the source on the calling (capture) thread and returns the sample rate in use. */
        int open() throws IOException;

        /** Blocks until {@code length} bytes of 16-bit little-endian mono PCM are available. False = failed. */
        boolean read(byte[] buffer, int offset, int length);

        void close();
    }

    public interface Host {
        /** True if the user already allowed this PC on an earlier occasion. */
        boolean isTrusted(long pcId);

        /** Something worth showing changed (called from worker threads). */
        void onChanged();
    }

    public static final class Peer {
        public final long id;
        public final String name;
        public final InetSocketAddress address;
        final int session;

        Peer(long id, String name, InetSocketAddress address, int session) {
            this.id = id;
            this.name = name;
            this.address = address;
            this.session = session;
        }
    }

    private static final int KEEPALIVE_TIMEOUT_MS = 6000, PENDING_TIMEOUT_MS = 5000;

    /** Network address to listen on; null (the default) means every interface. Tests use loopback. */
    java.net.InetAddress bindAddress;

    private final AudioSource source;
    private final Host host;
    private final long phoneId;
    private final byte[] name;
    private final HashSet<Long> denied = new HashSet<>();

    private DatagramSocket socket;
    private Thread rxThread, txThread;
    private volatile boolean running;
    private volatile Peer peer;             // the PC we are streaming to
    private volatile Peer pending;          // a PC waiting for the user to tap Allow
    private volatile int streamGen;         // changes whenever the current capture thread must end
    private volatile int sampleRate;        // 0 until the microphone is open
    private volatile int sourceEpoch;       // bumped when the microphone must be reopened (mode change)
    private volatile int level;
    private volatile long lastKeepAlive, pendingSeen;
    private volatile boolean muted;
    private volatile String error;

    public StreamCore(AudioSource source, Host host, long phoneId, String deviceName) {
        this.source = source;
        this.host = host;
        this.phoneId = phoneId;
        String n = deviceName == null || deviceName.trim().isEmpty() ? "Phone" : deviceName.trim();
        if (n.length() > 40) n = n.substring(0, 40);
        this.name = n.getBytes(StandardCharsets.UTF_8);
    }

    // ------------------------------------------------------------------ control (any background thread)

    public synchronized void start() throws IOException {
        if (running) return;
        DatagramSocket s = new DatagramSocket(null);
        try {
            s.setReuseAddress(true);
            s.bind(bindAddress == null ? new InetSocketAddress(Protocol.PORT) : new InetSocketAddress(bindAddress, Protocol.PORT));
            s.setBroadcast(true);
            s.setSoTimeout(500);
            try {
                s.setTrafficClass(0xB8);    // "expedited forwarding": Wi-Fi sends it in the voice queue
            } catch (IOException ignored) {
                // Purely an optimisation.
            }
        } catch (IOException e) {
            s.close();
            throw e;
        }
        socket = s;
        error = null;
        running = true;
        rxThread = new Thread(this::receiveLoop, "MicBridge-rx");
        rxThread.start();
        host.onChanged();
    }

    public void stop() {
        Thread rx, tx;
        DatagramSocket s;
        synchronized (this) {
            if (!running) return;
            Peer p = peer;
            if (p != null) {
                sendSession(Protocol.BYE, p.session, p.address);
                sendSession(Protocol.BYE, p.session, p.address);
            }
            running = false;
            peer = null;
            pending = null;
            streamGen++;
            rx = rxThread;
            tx = txThread;
            s = socket;
            rxThread = null;
            txThread = null;
        }
        join(tx, 700);
        source.close();                     // also frees a capture thread that is stuck in read()
        s.close();                          // wakes the receiver
        join(rx, 700);
        host.onChanged();
    }

    public boolean isRunning() { return running; }

    public Peer peer() { return peer; }

    public Peer pending() { return pending; }

    public String error() { return error; }

    public boolean isMuted() { return muted; }

    public void setMuted(boolean value) { muted = value; }

    /** Loudest sample (0..32767) since the previous call. */
    public int takeLevel() {
        int v = level;
        level = 0;
        return v;
    }

    /** Call after changing how the microphone should be opened; it is reopened without dropping the PC. */
    public void sourceChanged() { sourceEpoch++; }

    /** The user tapped Allow (the host must already report the PC as trusted). */
    public synchronized void approve() {
        Peer p = pending;
        pending = null;
        if (p != null && running && peer == null) startStreaming(p);
        host.onChanged();
    }

    /** The user tapped Deny: refuse this PC until the service is restarted. */
    public synchronized void deny() {
        Peer p = pending;
        pending = null;
        if (p != null) {
            denied.add(p.id);
            sendReject(p.session, Protocol.REJECT_DENIED, p.address);
        }
        host.onChanged();
    }

    // ------------------------------------------------------------------ receiver thread

    private void receiveLoop() {
        byte[] buffer = new byte[512];
        DatagramPacket packet = new DatagramPacket(buffer, buffer.length);
        DatagramSocket s = socket;
        while (running) {
            try {
                packet.setLength(buffer.length);
                s.receive(packet);
                handle(buffer, packet.getLength(), (InetSocketAddress) packet.getSocketAddress());
            } catch (SocketTimeoutException e) {
                // Nothing arrived: just do the housekeeping below.
            } catch (IOException e) {
                if (!running) break;
                pause(200);
            } catch (RuntimeException e) {
                // A malformed packet must never take the receiver down.
            }
            housekeeping();
        }
    }

    private void handle(byte[] b, int length, InetSocketAddress from) {
        if (length < 4 || b[0] != Protocol.MAGIC0 || b[1] != Protocol.MAGIC1 || b[2] != Protocol.VERSION) return;
        switch (b[3]) {
            case Protocol.DISCOVER:
                sendAnnounce(from);
                break;
            case Protocol.CONNECT:
                onConnect(b, length, from);
                break;
            case Protocol.KEEPALIVE: {
                Peer p = peer;
                if (p != null && length >= 8 && Protocol.getInt(b, 4) == p.session) lastKeepAlive = nowMs();
                break;
            }
            case Protocol.BYE: {
                Peer p = peer;
                if (p != null && length >= 8 && Protocol.getInt(b, 4) == p.session) stopStreaming(p, false, null);
                break;
            }
            default:
                break;
        }
    }

    private synchronized void onConnect(byte[] b, int length, InetSocketAddress from) {
        if (!running || length < 17) return;
        int session = Protocol.getInt(b, 4);
        long pcId = Protocol.getLong(b, 8);
        int nameLength = b[16] & 0xFF;
        if (17 + nameLength > length) return;
        String pcName = new String(b, 17, nameLength, StandardCharsets.UTF_8).trim();
        if (pcName.isEmpty()) pcName = "PC";

        Peer current = peer;
        if (current != null) {
            if (current.id != pcId) {
                sendReject(session, Protocol.REJECT_BUSY, from);
                return;
            }
            // Same PC again: it retried, restarted or changed network port. Keep streaming to wherever it is now.
            if (current.session != session || !current.address.equals(from)) {
                peer = new Peer(pcId, pcName, from, session);
                host.onChanged();
            }
            lastKeepAlive = nowMs();
            if (sampleRate != 0) sendAccept(session, from);
            return;
        }

        if (denied.contains(pcId)) {
            sendReject(session, Protocol.REJECT_DENIED, from);
            return;
        }
        if (!host.isTrusted(pcId)) {
            Peer p = pending;
            pendingSeen = nowMs();
            if (p == null || p.id != pcId || p.session != session || !p.address.equals(from)) {
                pending = new Peer(pcId, pcName, from, session);
                host.onChanged();
            }
            sendSession(Protocol.PENDING, session, from);
            return;
        }
        pending = null;
        startStreaming(new Peer(pcId, pcName, from, session));
    }

    private void housekeeping() {
        long now = nowMs();
        Peer p = peer;
        if (p != null && now - lastKeepAlive > KEEPALIVE_TIMEOUT_MS) stopStreaming(p, true, null);
        if (pending != null && now - pendingSeen > PENDING_TIMEOUT_MS) {
            synchronized (this) {
                if (pending != null && nowMs() - pendingSeen > PENDING_TIMEOUT_MS) pending = null;   // the PC gave up
            }
            host.onChanged();
        }
    }

    // ------------------------------------------------------------------ streaming

    /** Caller holds the lock. */
    private void startStreaming(Peer p) {
        peer = p;
        sampleRate = 0;
        error = null;
        lastKeepAlive = nowMs();
        final int generation = ++streamGen;
        final Thread previous = txThread;
        Thread t = new Thread(() -> captureLoop(generation, previous), "MicBridge-tx");
        t.setPriority(Thread.MAX_PRIORITY);
        txThread = t;
        t.start();
        host.onChanged();
    }

    /** Ends the stream to {@code expected} (if it is still the current one). */
    private synchronized void stopStreaming(Peer expected, boolean sayBye, String why) {
        Peer p = peer;
        if (p == null || (expected != null && p != expected && p.session != expected.session)) return;
        if (sayBye) {
            sendSession(Protocol.BYE, p.session, p.address);
            sendSession(Protocol.BYE, p.session, p.address);
        }
        peer = null;
        streamGen++;
        if (why != null) error = why;
        host.onChanged();
    }

    private void captureLoop(int generation, Thread previous) {
        join(previous, 1500);               // never open the microphone while the last session is still closing it
        if (!alive(generation)) return;

        int rate;
        try {
            rate = source.open();
        } catch (IOException | RuntimeException e) {
            source.close();
            failStream(generation, e.getMessage() != null ? e.getMessage() : "Could not open the microphone");
            return;
        }

        final int frameBytes = rate / 100 * 2;          // 10 ms of 16-bit mono
        final byte[] data = new byte[Protocol.AUDIO_HEADER + frameBytes];
        data[0] = Protocol.MAGIC0;
        data[1] = Protocol.MAGIC1;
        data[2] = Protocol.VERSION;
        data[3] = Protocol.AUDIO;
        final DatagramPacket packet = new DatagramPacket(data, data.length);
        final DatagramSocket s = socket;

        sampleRate = rate;
        Peer p = peer;
        if (p != null && alive(generation)) sendAccept(p.session, p.address);

        int sequence = 0, epoch = sourceEpoch, sendFailures = 0;
        String failure = null;
        while (alive(generation)) {
            if (sourceEpoch != epoch) {
                // The user picked another microphone mode: reopen it in place.
                epoch = sourceEpoch;
                source.close();
                try {
                    if (source.open() != rate) break;   // different rate: let the PC reconnect cleanly
                } catch (IOException | RuntimeException e) {
                    failure = e.getMessage() != null ? e.getMessage() : "Could not open the microphone";
                    break;
                }
            }
            if (!source.read(data, Protocol.AUDIO_HEADER, frameBytes)) {
                if (alive(generation)) failure = "The microphone stopped (is another app using it?)";
                break;
            }
            p = peer;
            if (p == null) break;

            boolean silent = muted;
            if (silent) {
                Arrays.fill(data, Protocol.AUDIO_HEADER, data.length, (byte) 0);
            } else {
                int peak = 0;
                for (int i = Protocol.AUDIO_HEADER; i < data.length; i += 2) {
                    int v = (short) ((data[i] & 0xFF) | (data[i + 1] << 8));
                    if (v < 0) v = -v;
                    if (v > peak) peak = v;
                }
                if (peak > level) level = peak;
            }

            Protocol.putInt(data, 4, p.session);
            Protocol.putInt(data, 8, sequence++);
            data[12] = silent ? Protocol.FLAG_MUTED : 0;
            packet.setSocketAddress(p.address);
            try {
                s.send(packet);
                sendFailures = 0;
            } catch (IOException e) {
                // Wi-Fi hiccup: keep going for a few seconds, the PC side copes with missing packets.
                if (++sendFailures > 500) {
                    failure = "The network connection was lost";
                    break;
                }
            }
        }
        source.close();
        level = 0;
        if (alive(generation)) failStream(generation, failure);
    }

    private boolean alive(int generation) {
        return running && streamGen == generation;
    }

    /** The capture thread could not go on: tell the PC and return to waiting. */
    private synchronized void failStream(int generation, String why) {
        if (streamGen != generation) return;
        Peer p = peer;
        if (p != null && why != null) sendReject(p.session, Protocol.REJECT_MIC, p.address);
        peer = null;
        streamGen++;
        if (why != null) error = why;
        host.onChanged();
    }

    // ------------------------------------------------------------------ small packets

    private void sendAnnounce(InetSocketAddress to) {
        byte[] b = header(Protocol.ANNOUNCE, 14 + name.length);
        b[4] = (byte) (peer != null ? 1 : 0);
        Protocol.putLong(b, 5, phoneId);
        b[13] = (byte) name.length;
        System.arraycopy(name, 0, b, 14, name.length);
        send(b, to);
    }

    private void sendAccept(int session, InetSocketAddress to) {
        byte[] b = header(Protocol.ACCEPT, 22 + name.length);
        Protocol.putInt(b, 4, session);
        Protocol.putInt(b, 8, sampleRate);
        b[12] = 1;
        Protocol.putLong(b, 13, phoneId);
        b[21] = (byte) name.length;
        System.arraycopy(name, 0, b, 22, name.length);
        send(b, to);
    }

    private void sendReject(int session, byte reason, InetSocketAddress to) {
        byte[] b = header(Protocol.REJECT, 9);
        Protocol.putInt(b, 4, session);
        b[8] = reason;
        send(b, to);
    }

    private void sendSession(byte type, int session, InetSocketAddress to) {
        byte[] b = header(type, 8);
        Protocol.putInt(b, 4, session);
        send(b, to);
    }

    private static byte[] header(byte type, int size) {
        byte[] b = new byte[size];
        b[0] = Protocol.MAGIC0;
        b[1] = Protocol.MAGIC1;
        b[2] = Protocol.VERSION;
        b[3] = type;
        return b;
    }

    private void send(byte[] data, InetSocketAddress to) {
        DatagramSocket s = socket;
        if (s == null) return;
        try {
            s.send(new DatagramPacket(data, data.length, to));
        } catch (IOException ignored) {
            // The PC repeats its requests, so a lost reply sorts itself out.
        }
    }

    // ------------------------------------------------------------------ helpers

    private static long nowMs() {
        return System.nanoTime() / 1_000_000L;
    }

    private static void join(Thread t, int ms) {
        if (t == null || t == Thread.currentThread()) return;
        try {
            t.join(ms);
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
        }
    }

    private static void pause(int ms) {
        try {
            Thread.sleep(ms);
        } catch (InterruptedException e) {
            Thread.currentThread().interrupt();
        }
    }
}
