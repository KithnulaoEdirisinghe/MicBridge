package app.micbridge;

import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.content.pm.ServiceInfo;
import android.net.wifi.WifiManager;
import android.os.Build;
import android.os.Handler;
import android.os.IBinder;
import android.os.Looper;
import android.os.PowerManager;
import android.provider.Settings;

import java.io.IOException;
import java.security.SecureRandom;
import java.util.HashSet;
import java.util.Locale;
import java.util.Set;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/**
 * Keeps the microphone stream alive while the screen is off or another app is in front.
 * It runs only between "Start" and "Stop"; the microphone itself is opened only while a PC is connected.
 */
public final class MicService extends Service implements StreamCore.Host {

    static final String ACTION_STOP = "app.micbridge.action.STOP";
    static final String PREFS = "micbridge";
    private static final String CHANNEL = "status";
    private static final int NOTIFICATION_ID = 1;

    /** The running service, or null. The activity reads its state from here. */
    static volatile MicService instance;
    /** Why the last start attempt failed, for the activity to show. */
    static volatile String lastError;

    private final Handler main = new Handler(Looper.getMainLooper());
    private ExecutorService worker;         // network calls are not allowed on the main thread
    private SharedPreferences prefs;
    private MicSource mic;
    private StreamCore core;
    private PowerManager.WakeLock wakeLock;
    private WifiManager.WifiLock wifiLock;
    private WifiManager.MulticastLock multicastLock;
    private boolean started;
    private String shownText;

    @Override
    public void onCreate() {
        super.onCreate();
        prefs = getSharedPreferences(PREFS, MODE_PRIVATE);
        long id = prefs.getLong("phoneId", 0);
        if (id == 0) {
            id = new SecureRandom().nextLong() | 1;
            prefs.edit().putLong("phoneId", id).apply();
        }
        mic = new MicSource(this, prefs.getInt("mode", MicSource.MODE_STANDARD));
        core = new StreamCore(mic, this, id, deviceName());
        worker = Executors.newSingleThreadExecutor();
        instance = this;
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        if (intent != null && ACTION_STOP.equals(intent.getAction())) {
            stopSelf();
            return START_NOT_STICKY;
        }
        if (started) return START_NOT_STICKY;

        createChannel();
        shownText = text();
        try {
            Notification notification = buildNotification(shownText);
            if (Build.VERSION.SDK_INT >= 29) {
                startForeground(NOTIFICATION_ID, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE);
            } else {
                startForeground(NOTIFICATION_ID, notification);
            }
        } catch (RuntimeException e) {
            // Android refuses this when the microphone permission is missing or the app is not in front.
            lastError = "Android did not allow the microphone service to start. Please tap Start again.";
            stopSelf();
            return START_NOT_STICKY;
        }
        started = true;
        lastError = null;
        acquireLocks();

        final StreamCore c = core;
        worker.execute(() -> {
            try {
                c.start();
            } catch (IOException e) {
                lastError = "Could not start listening on the network (port " + Protocol.PORT + " is in use).";
                main.post(this::stopSelf);
            }
        });
        return START_NOT_STICKY;            // never restart on its own: the user starts it from the app
    }

    @Override
    public void onDestroy() {
        instance = null;
        final StreamCore c = core;
        worker.execute(c::stop);
        worker.shutdown();
        releaseLocks();
        if (started) stopForeground(STOP_FOREGROUND_REMOVE);
        started = false;
        super.onDestroy();
    }

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }

    // ------------------------------------------------------------------ used by the activity (main thread)

    StreamCore core() {
        return core;
    }

    void allowPending() {
        StreamCore.Peer p = core.pending();
        if (p == null) return;
        Set<String> trusted = new HashSet<>(prefs.getStringSet("trusted", new HashSet<String>()));
        trusted.add(Long.toString(p.id));
        prefs.edit().putStringSet("trusted", trusted).apply();
        worker.execute(core::approve);
    }

    void denyPending() {
        worker.execute(core::deny);
    }

    void setMode(int mode) {
        mic.setMode(mode);
        core.sourceChanged();
    }

    // ------------------------------------------------------------------ StreamCore.Host (worker threads)

    @Override
    public boolean isTrusted(long pcId) {
        Set<String> trusted = prefs.getStringSet("trusted", null);
        return trusted != null && trusted.contains(Long.toString(pcId));
    }

    @Override
    public void onChanged() {
        main.post(this::refreshNotification);
    }

    // ------------------------------------------------------------------ notification

    private void refreshNotification() {
        if (!started || instance != this) return;
        String text = text();
        if (text.equals(shownText)) return;
        shownText = text;
        NotificationManager nm = (NotificationManager) getSystemService(NOTIFICATION_SERVICE);
        if (nm != null) nm.notify(NOTIFICATION_ID, buildNotification(text));
    }

    private String text() {
        StreamCore.Peer peer = core.peer();
        if (peer != null) return getString(R.string.notif_streaming, peer.name);
        if (core.pending() != null) return getString(R.string.notif_pending);
        return getString(R.string.notif_waiting);
    }

    private void createChannel() {
        if (Build.VERSION.SDK_INT < 26) return;
        NotificationManager nm = (NotificationManager) getSystemService(NOTIFICATION_SERVICE);
        if (nm == null) return;
        NotificationChannel channel = new NotificationChannel(CHANNEL, getString(R.string.channel_name), NotificationManager.IMPORTANCE_LOW);
        channel.setShowBadge(false);
        nm.createNotificationChannel(channel);
    }

    @SuppressWarnings("deprecation")
    private Notification buildNotification(String text) {
        Intent open = new Intent(this, MainActivity.class).addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP);
        PendingIntent openIntent = PendingIntent.getActivity(this, 0, open,
                PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);
        Intent stop = new Intent(this, MicService.class).setAction(ACTION_STOP);
        PendingIntent stopIntent = PendingIntent.getService(this, 1, stop,
                PendingIntent.FLAG_UPDATE_CURRENT | PendingIntent.FLAG_IMMUTABLE);

        Notification.Builder b = Build.VERSION.SDK_INT >= 26
                ? new Notification.Builder(this, CHANNEL)
                : new Notification.Builder(this).setPriority(Notification.PRIORITY_LOW);
        b.setSmallIcon(R.drawable.ic_stat_mic)
                .setContentTitle(getString(R.string.app_name))
                .setContentText(text)
                .setContentIntent(openIntent)
                .setOngoing(true)
                .setOnlyAlertOnce(true)
                .setShowWhen(false)
                .setColor(getColor(R.color.accent))
                .addAction(new Notification.Action.Builder(null, getString(R.string.stop), stopIntent).build());
        if (Build.VERSION.SDK_INT >= 31) b.setForegroundServiceBehavior(Notification.FOREGROUND_SERVICE_IMMEDIATE);
        return b.build();
    }

    // ------------------------------------------------------------------ keeping the phone awake

    private void acquireLocks() {
        try {
            PowerManager pm = (PowerManager) getSystemService(POWER_SERVICE);
            if (pm != null) {
                wakeLock = pm.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "MicBridge:stream");
                wakeLock.setReferenceCounted(false);
                wakeLock.acquire();
            }
            WifiManager wm = (WifiManager) getApplicationContext().getSystemService(Context.WIFI_SERVICE);
            if (wm != null) {
                // Stops Wi-Fi from dozing between packets, which would add delay and drop-outs.
                int mode = Build.VERSION.SDK_INT >= 29 ? WifiManager.WIFI_MODE_FULL_LOW_LATENCY : wifiHighPerf();
                wifiLock = wm.createWifiLock(mode, "MicBridge:wifi");
                wifiLock.setReferenceCounted(false);
                wifiLock.acquire();
                // Lets the PC's "who is there?" broadcasts through while the screen is off.
                multicastLock = wm.createMulticastLock("MicBridge:discovery");
                multicastLock.setReferenceCounted(false);
                multicastLock.acquire();
            }
        } catch (RuntimeException ignored) {
            // Locks are an optimisation; streaming still works without them.
        }
    }

    @SuppressWarnings("deprecation")
    private static int wifiHighPerf() {
        return WifiManager.WIFI_MODE_FULL_HIGH_PERF;
    }

    private void releaseLocks() {
        try {
            if (multicastLock != null && multicastLock.isHeld()) multicastLock.release();
            if (wifiLock != null && wifiLock.isHeld()) wifiLock.release();
            if (wakeLock != null && wakeLock.isHeld()) wakeLock.release();
        } catch (RuntimeException ignored) {
            // Already released.
        }
        multicastLock = null;
        wifiLock = null;
        wakeLock = null;
    }

    private String deviceName() {
        String name = null;
        try {
            name = Settings.Global.getString(getContentResolver(), "device_name");
        } catch (RuntimeException ignored) {
            // Not readable on this device.
        }
        if (name == null || name.trim().isEmpty()) {
            String maker = Build.MANUFACTURER == null ? "" : Build.MANUFACTURER.trim();
            String model = Build.MODEL == null ? "" : Build.MODEL.trim();
            if (!maker.isEmpty() && !model.toLowerCase(Locale.ROOT).startsWith(maker.toLowerCase(Locale.ROOT))) {
                model = maker.substring(0, 1).toUpperCase(Locale.ROOT) + maker.substring(1) + " " + model;
            }
            name = model;
        }
        name = name.trim();
        return name.isEmpty() ? "Android phone" : name;
    }
}
