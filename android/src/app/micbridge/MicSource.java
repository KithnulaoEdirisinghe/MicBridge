package app.micbridge;

import android.content.Context;
import android.media.AudioFormat;
import android.media.AudioManager;
import android.media.AudioRecord;
import android.media.MediaRecorder;
import android.os.Process;

import java.io.IOException;

/** The phone's microphone as 16-bit mono PCM. */
final class MicSource implements StreamCore.AudioSource {

    /** Plain microphone, as most recording apps use it. */
    static final int MODE_STANDARD = 0;
    /** Tuned for calls: the phone's echo cancellation and noise suppression are switched on. */
    static final int MODE_VOICE = 1;
    /** As little processing as the phone allows (best for music or when the PC does its own clean-up). */
    static final int MODE_RAW = 2;

    private static final int[] RATES = {48000, 44100};

    private final Context context;
    private final Object lock = new Object();
    private volatile int mode;
    private volatile AudioRecord record;

    MicSource(Context context, int mode) {
        this.context = context.getApplicationContext();
        this.mode = mode;
    }

    void setMode(int mode) {
        this.mode = mode;
    }

    @Override
    public int open() throws IOException {
        // We are on the capture thread: ask Android to schedule it like an audio thread.
        try {
            Process.setThreadPriority(Process.THREAD_PRIORITY_URGENT_AUDIO);
        } catch (RuntimeException ignored) {
            // Not allowed on this device: normal priority still works.
        }

        int source = audioSource();
        for (int rate : RATES) {
            int minimum = AudioRecord.getMinBufferSize(rate, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT);
            if (minimum <= 0) continue;
            int tenMs = rate / 100 * 2;
            int size = Math.max(minimum, tenMs * 4);   // small, so little audio waits inside the phone
            AudioRecord r = null;
            try {
                r = new AudioRecord(source, rate, AudioFormat.CHANNEL_IN_MONO, AudioFormat.ENCODING_PCM_16BIT, size);
                if (r.getState() != AudioRecord.STATE_INITIALIZED) throw new IllegalStateException("not initialised");
                r.startRecording();
                if (r.getRecordingState() != AudioRecord.RECORDSTATE_RECORDING) throw new IllegalStateException("not recording");
                synchronized (lock) {
                    record = r;
                }
                return rate;
            } catch (RuntimeException e) {
                // Includes SecurityException (permission revoked) and unsupported settings: try the next rate.
                if (r != null) r.release();
            }
        }
        throw new IOException("Could not open the microphone (is another app using it?)");
    }

    @Override
    public boolean read(byte[] buffer, int offset, int length) {
        AudioRecord r = record;
        if (r == null) return false;
        while (length > 0) {
            int n = r.read(buffer, offset, length);
            if (n <= 0) return false;
            offset += n;
            length -= n;
        }
        return true;
    }

    @Override
    public void close() {
        AudioRecord r;
        synchronized (lock) {
            r = record;
            record = null;
        }
        if (r == null) return;
        try {
            r.stop();
        } catch (IllegalStateException ignored) {
            // Was never started.
        }
        r.release();
    }

    private int audioSource() {
        switch (mode) {
            case MODE_VOICE:
                return MediaRecorder.AudioSource.VOICE_COMMUNICATION;
            case MODE_RAW: {
                AudioManager am = (AudioManager) context.getSystemService(Context.AUDIO_SERVICE);
                boolean unprocessed = am != null
                        && "true".equals(am.getProperty(AudioManager.PROPERTY_SUPPORT_AUDIO_SOURCE_UNPROCESSED));
                return unprocessed ? MediaRecorder.AudioSource.UNPROCESSED : MediaRecorder.AudioSource.VOICE_RECOGNITION;
            }
            default:
                return MediaRecorder.AudioSource.MIC;
        }
    }
}
