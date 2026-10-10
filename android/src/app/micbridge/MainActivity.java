package app.micbridge;

import android.Manifest;
import android.app.Activity;
import android.app.AlertDialog;
import android.content.Intent;
import android.content.SharedPreferences;
import android.content.pm.PackageManager;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.provider.Settings;
import android.view.View;
import android.view.WindowManager;
import android.widget.AdapterView;
import android.widget.ArrayAdapter;
import android.widget.Button;
import android.widget.ProgressBar;
import android.widget.Spinner;
import android.widget.Switch;
import android.widget.TextView;

import java.net.Inet4Address;
import java.net.InetAddress;
import java.net.NetworkInterface;
import java.net.SocketException;
import java.util.ArrayList;
import java.util.Enumeration;
import java.util.Locale;

/** The single screen: Start/Stop, status, this phone's address and a few switches. */
public final class MainActivity extends Activity {

    private static final int REQUEST_PERMISSIONS = 1;
    private static final int STOPPED = 0, WAITING = 1, STREAMING = 2;

    private final Handler handler = new Handler(Looper.getMainLooper());
    private final Runnable ticker = new Runnable() {
        @Override
        public void run() {
            refresh();
            handler.postDelayed(this, 100);
        }
    };

    private SharedPreferences prefs;
    private TextView status, detail, address;
    private Button toggle;
    private Switch mute, awake;
    private Spinner mode;
    private ProgressBar meter;

    private AlertDialog approval;
    private long approvalFor;
    private int shownState = -1, shownMeter = -1, ticks;
    private String shownStatus = "", shownDetail = "", shownAddress = "";
    private boolean shownError;
    private float level;

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        setContentView(R.layout.main);
        prefs = getSharedPreferences(MicService.PREFS, MODE_PRIVATE);

        status = findViewById(R.id.status);
        detail = findViewById(R.id.detail);
        address = findViewById(R.id.address);
        toggle = findViewById(R.id.toggle);
        mute = findViewById(R.id.mute);
        awake = findViewById(R.id.awake);
        mode = findViewById(R.id.mode);
        meter = findViewById(R.id.meter);

        toggle.setOnClickListener(v -> onToggle());

        MicService running = MicService.instance;
        mute.setChecked(running != null && running.core().isMuted());
        mute.setOnCheckedChangeListener((button, checked) -> {
            MicService svc = MicService.instance;
            if (svc != null) svc.core().setMuted(checked);
        });

        awake.setChecked(prefs.getBoolean("awake", false));
        awake.setOnCheckedChangeListener((button, checked) -> {
            prefs.edit().putBoolean("awake", checked).apply();
            applyKeepScreenOn();
        });

        ArrayAdapter<CharSequence> modes = ArrayAdapter.createFromResource(this, R.array.modes, android.R.layout.simple_spinner_item);
        modes.setDropDownViewResource(android.R.layout.simple_spinner_dropdown_item);
        mode.setAdapter(modes);
        int savedMode = prefs.getInt("mode", MicSource.MODE_STANDARD);
        mode.setSelection(savedMode >= 0 && savedMode < modes.getCount() ? savedMode : 0, false);
        mode.setOnItemSelectedListener(new AdapterView.OnItemSelectedListener() {
            @Override
            public void onItemSelected(AdapterView<?> parent, View view, int position, long id) {
                if (position == prefs.getInt("mode", MicSource.MODE_STANDARD)) return;
                prefs.edit().putInt("mode", position).apply();
                MicService svc = MicService.instance;
                if (svc != null) svc.setMode(position);
            }

            @Override
            public void onNothingSelected(AdapterView<?> parent) { }
        });
    }

    @Override
    protected void onResume() {
        super.onResume();
        ticks = 0;
        handler.removeCallbacks(ticker);
        handler.post(ticker);
    }

    @Override
    protected void onPause() {
        handler.removeCallbacks(ticker);
        super.onPause();
    }

    @Override
    protected void onDestroy() {
        if (approval != null) approval.dismiss();
        approval = null;
        super.onDestroy();
    }

    // ------------------------------------------------------------------ start / stop

    private void onToggle() {
        if (MicService.instance != null) {
            stopService(new Intent(this, MicService.class));
            return;
        }
        ArrayList<String> missing = new ArrayList<>();
        if (checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
            missing.add(Manifest.permission.RECORD_AUDIO);
        }
        // The status notification is optional, so only ask for it once.
        if (Build.VERSION.SDK_INT >= 33
                && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
                && !prefs.getBoolean("askedNotifications", false)) {
            missing.add(Manifest.permission.POST_NOTIFICATIONS);
            prefs.edit().putBoolean("askedNotifications", true).apply();
        }
        if (!missing.isEmpty()) {
            requestPermissions(missing.toArray(new String[0]), REQUEST_PERMISSIONS);
            return;
        }
        startMicService();
    }

    @Override
    public void onRequestPermissionsResult(int requestCode, String[] permissions, int[] results) {
        super.onRequestPermissionsResult(requestCode, permissions, results);
        if (requestCode != REQUEST_PERMISSIONS) return;
        if (checkSelfPermission(Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED) {
            startMicService();
            return;
        }
        // Without the microphone there is nothing this app can do: explain, and offer the settings page
        // when Android will no longer show its own question.
        AlertDialog.Builder b = new AlertDialog.Builder(this)
                .setTitle(R.string.perm_title)
                .setMessage(R.string.perm_message)
                .setNegativeButton(android.R.string.cancel, null);
        if (!shouldShowRequestPermissionRationale(Manifest.permission.RECORD_AUDIO)) {
            b.setPositiveButton(R.string.open_settings, (dialog, which) -> {
                Intent i = new Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.fromParts("package", getPackageName(), null));
                try {
                    startActivity(i);
                } catch (RuntimeException ignored) {
                    // No settings screen on this device.
                }
            });
        }
        b.show();
    }

    private void startMicService() {
        MicService.lastError = null;
        Intent i = new Intent(this, MicService.class);
        try {
            if (Build.VERSION.SDK_INT >= 26) startForegroundService(i); else startService(i);
        } catch (RuntimeException e) {
            MicService.lastError = getString(R.string.start_failed);
        }
    }

    // ------------------------------------------------------------------ screen refresh (10 times a second while visible)

    private void refresh() {
        MicService svc = MicService.instance;
        StreamCore core = svc == null ? null : svc.core();
        StreamCore.Peer peer = core == null ? null : core.peer();
        int state = svc == null ? STOPPED : peer != null ? STREAMING : WAITING;

        if (state != shownState) {
            shownState = state;
            toggle.setText(state == STOPPED ? R.string.start : R.string.stop);
            toggle.setBackgroundResource(state == STOPPED ? R.drawable.button_start : R.drawable.button_stop);
            mute.setEnabled(state != STOPPED);
            if (state == STOPPED) mute.setChecked(false);
            applyKeepScreenOn();
        }

        String problem = core != null ? core.error() : MicService.lastError;
        String statusText, detailText;
        if (state == STREAMING) {
            statusText = getString(R.string.status_streaming, peer.name);
            detailText = getString(core.isMuted() ? R.string.detail_muted : R.string.detail_streaming);
        } else if (state == WAITING) {
            statusText = getString(R.string.status_waiting);
            detailText = problem != null ? problem : getString(R.string.detail_waiting);
        } else {
            statusText = getString(R.string.status_stopped);
            detailText = problem != null ? problem : getString(R.string.detail_stopped);
        }
        boolean isError = state != STREAMING && problem != null;
        if (!statusText.equals(shownStatus)) {
            shownStatus = statusText;
            status.setText(statusText);
        }
        if (!detailText.equals(shownDetail) || isError != shownError) {
            shownDetail = detailText;
            shownError = isError;
            detail.setText(detailText);
            detail.setTextColor(getColor(isError ? R.color.danger : R.color.text2));
        }

        // Level meter: 60 dB scale with a smooth fall-back.
        float now = 0f;
        if (state == STREAMING) {
            int peak = core.takeLevel();
            if (peak > 32) now = (float) (1.0 + Math.log10(peak / 32768.0) / 3.0);
        }
        level = now > level ? now : Math.max(now, level - 0.06f);
        int progress = Math.round(level * 100);
        if (progress != shownMeter) {
            shownMeter = progress;
            meter.setProgress(progress);
        }

        if (ticks++ % 20 == 0) {
            String text = localAddresses();
            if (!text.equals(shownAddress)) {
                shownAddress = text;
                address.setText(text.isEmpty() ? getString(R.string.no_network) : text);
            }
        }

        // A PC that was never allowed before is asking.
        StreamCore.Peer asking = core == null ? null : core.pending();
        if (asking == null) {
            if (approval != null) {
                approval.dismiss();
                approval = null;
            }
        } else if (approval == null || approvalFor != asking.id) {
            if (approval != null) approval.dismiss();
            approvalFor = asking.id;
            approval = new AlertDialog.Builder(this)
                    .setTitle(R.string.allow_title)
                    .setMessage(getString(R.string.allow_message, asking.name, asking.address.getAddress().getHostAddress()))
                    .setCancelable(false)
                    .setPositiveButton(R.string.allow, (dialog, which) -> {
                        MicService s = MicService.instance;
                        if (s != null) s.allowPending();
                    })
                    .setNegativeButton(R.string.deny, (dialog, which) -> {
                        MicService s = MicService.instance;
                        if (s != null) s.denyPending();
                    })
                    .show();
        }
    }

    private void applyKeepScreenOn() {
        if (awake.isChecked() && shownState != STOPPED) {
            getWindow().addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        } else {
            getWindow().clearFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON);
        }
    }

    /** The phone's local network addresses (normally its Wi-Fi address), one per line. */
    private static String localAddresses() {
        StringBuilder sb = new StringBuilder();
        try {
            Enumeration<NetworkInterface> all = NetworkInterface.getNetworkInterfaces();
            while (all != null && all.hasMoreElements()) {
                NetworkInterface ni = all.nextElement();
                String n = ni.getName().toLowerCase(Locale.ROOT);
                // Skip loopback and mobile-data interfaces: a PC cannot reach the phone through those.
                if (!ni.isUp() || ni.isLoopback() || n.startsWith("rmnet") || n.startsWith("ccmni")
                        || n.startsWith("pdp") || n.startsWith("dummy") || n.startsWith("tun") || n.startsWith("v4-")) {
                    continue;
                }
                Enumeration<InetAddress> addresses = ni.getInetAddresses();
                while (addresses.hasMoreElements()) {
                    InetAddress a = addresses.nextElement();
                    if (a instanceof Inet4Address && a.isSiteLocalAddress()) {
                        if (sb.length() > 0) sb.append('\n');
                        sb.append(a.getHostAddress());
                    }
                }
            }
        } catch (SocketException | RuntimeException ignored) {
            // Shown as "not connected".
        }
        return sb.toString();
    }
}
