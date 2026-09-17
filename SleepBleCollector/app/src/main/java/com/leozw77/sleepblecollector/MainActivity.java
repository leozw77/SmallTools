package com.leozw77.sleepblecollector;

import android.Manifest;
import android.app.Activity;
import android.bluetooth.BluetoothAdapter;
import android.bluetooth.BluetoothDevice;
import android.bluetooth.BluetoothGatt;
import android.bluetooth.BluetoothGattCallback;
import android.bluetooth.BluetoothGattCharacteristic;
import android.bluetooth.BluetoothGattDescriptor;
import android.bluetooth.BluetoothGattService;
import android.bluetooth.BluetoothManager;
import android.bluetooth.BluetoothProfile;
import android.bluetooth.le.BluetoothLeScanner;
import android.bluetooth.le.ScanCallback;
import android.bluetooth.le.ScanResult;
import android.bluetooth.le.ScanSettings;
import android.content.Context;
import android.content.pm.PackageManager;
import android.os.Build;
import android.os.Bundle;
import android.os.Environment;
import android.os.Handler;
import android.os.Looper;
import android.view.View;
import android.widget.Button;
import android.widget.LinearLayout;
import android.widget.ScrollView;
import android.widget.TextView;

import java.io.File;
import java.io.FileWriter;
import java.io.IOException;
import java.text.SimpleDateFormat;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.Date;
import java.util.HashSet;
import java.util.List;
import java.util.Locale;
import java.util.Set;
import java.util.UUID;

public class MainActivity extends Activity {
    private static final int REQUEST_BLE_PERMISSIONS = 1001;
    private static final long SCAN_MILLIS = 30000L;
    private static final UUID CCCD = UUID.fromString("00002902-0000-1000-8000-00805f9b34fb");

    private final Handler handler = new Handler(Looper.getMainLooper());
    private final ArrayDeque<BluetoothGattDescriptor> descriptorQueue = new ArrayDeque<>();
    private final ArrayDeque<BluetoothGattCharacteristic> readQueue = new ArrayDeque<>();
    private final Set<String> loggedAdvertisements = new HashSet<>();

    private TextView statusView;
    private TextView logView;
    private Button actionButton;
    private BluetoothAdapter adapter;
    private BluetoothLeScanner scanner;
    private BluetoothGatt gatt;
    private FileWriter capture;
    private boolean scanning;
    private boolean connecting;
    private String capturePath = "";

    @Override
    protected void onCreate(Bundle state) {
        super.onCreate(state);
        buildUi();
        openCapture();
        initBluetooth();
    }

    private void buildUi() {
        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setPadding(28, 24, 28, 16);

        TextView title = new TextView(this);
        title.setText("Sleep BLE Collector\n记录设备 BLE 原始数据");
        title.setTextSize(21);
        root.addView(title, new LinearLayout.LayoutParams(-1, -2));

        statusView = new TextView(this);
        statusView.setTextSize(15);
        statusView.setPadding(0, 18, 0, 12);
        root.addView(statusView, new LinearLayout.LayoutParams(-1, -2));

        actionButton = new Button(this);
        actionButton.setText("开始扫描");
        actionButton.setOnClickListener(v -> {
            if (scanning) {
                stopScan("用户停止");
            } else {
                ensurePermissionsAndScan();
            }
        });
        root.addView(actionButton, new LinearLayout.LayoutParams(-1, -2));

        TextView hint = new TextView(this);
        hint.setText("请保持睡眠监测带通电并靠近手机。日志保存在应用专属 Documents 目录。\n" +
                "只记录原始协议数据，不上传、不修改设备。");
        hint.setPadding(0, 12, 0, 12);
        root.addView(hint, new LinearLayout.LayoutParams(-1, -2));

        logView = new TextView(this);
        logView.setTextSize(12);
        logView.setTypeface(android.graphics.Typeface.MONOSPACE);
        ScrollView scroll = new ScrollView(this);
        scroll.addView(logView, new ScrollView.LayoutParams(-1, -2));
        root.addView(scroll, new LinearLayout.LayoutParams(-1, 0, 1));
        setContentView(root);
    }

    private void initBluetooth() {
        BluetoothManager manager = (BluetoothManager) getSystemService(Context.BLUETOOTH_SERVICE);
        adapter = manager == null ? null : manager.getAdapter();
        if (adapter == null) {
            setStatus("手机不支持蓝牙 LE");
            return;
        }
        setStatus("准备就绪：将自动扫描名称包含 midea 的 BLE 设备");
        ensurePermissionsAndScan();
    }

    private void ensurePermissionsAndScan() {
        List<String> missing = new ArrayList<>();
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            if (checkSelfPermission(Manifest.permission.BLUETOOTH_SCAN) != PackageManager.PERMISSION_GRANTED) {
                missing.add(Manifest.permission.BLUETOOTH_SCAN);
            }
            if (checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) != PackageManager.PERMISSION_GRANTED) {
                missing.add(Manifest.permission.BLUETOOTH_CONNECT);
            }
        } else if (checkSelfPermission(Manifest.permission.ACCESS_FINE_LOCATION) != PackageManager.PERMISSION_GRANTED) {
            missing.add(Manifest.permission.ACCESS_FINE_LOCATION);
        }
        if (!missing.isEmpty()) {
            requestPermissions(missing.toArray(new String[0]), REQUEST_BLE_PERMISSIONS);
            setStatus("等待蓝牙权限");
            return;
        }
        startScan();
    }

    @Override
    public void onRequestPermissionsResult(int requestCode, String[] permissions, int[] results) {
        super.onRequestPermissionsResult(requestCode, permissions, results);
        if (requestCode == REQUEST_BLE_PERMISSIONS) {
            boolean granted = results.length > 0;
            for (int result : results) granted &= result == PackageManager.PERMISSION_GRANTED;
            if (granted) startScan(); else setStatus("蓝牙权限未授予，无法采集");
        }
    }

    private void startScan() {
        if (adapter == null) return;
        if (!adapter.isEnabled()) {
            setStatus("请先打开手机蓝牙");
            return;
        }
        scanner = adapter.getBluetoothLeScanner();
        if (scanner == null) {
            setStatus("BLE 扫描器不可用");
            return;
        }
        loggedAdvertisements.clear();
        connecting = false;
        scanning = true;
        actionButton.setText("停止扫描");
        writeLog("SCAN_START\twindow_ms=" + SCAN_MILLIS);
        try {
            ScanSettings settings = new ScanSettings.Builder()
                    .setScanMode(ScanSettings.SCAN_MODE_LOW_LATENCY)
                    .build();
            scanner.startScan(null, settings, scanCallback);
            handler.postDelayed(() -> {
                if (scanning) stopScan("30 秒扫描窗口结束");
            }, SCAN_MILLIS);
            setStatus("正在扫描：等待 midea 广播");
        } catch (SecurityException e) {
            setStatus("扫描权限异常：" + e.getMessage());
            writeLog("SCAN_ERROR\t" + e);
        }
    }

    private void stopScan(String reason) {
        if (!scanning) return;
        scanning = false;
        actionButton.setText("重新扫描");
        try {
            if (scanner != null) scanner.stopScan(scanCallback);
        } catch (SecurityException e) {
            writeLog("SCAN_STOP_ERROR\t" + e);
        }
        writeLog("SCAN_STOP\treason=" + reason);
        if (!connecting) setStatus("扫描结束：未自动连接到 midea");
    }

    private final ScanCallback scanCallback = new ScanCallback() {
        @Override
        public void onScanResult(int callbackType, ScanResult result) {
            BluetoothDevice device = result.getDevice();
            String name = result.getScanRecord() == null ? null : result.getScanRecord().getDeviceName();
            if (name == null) {
                try { name = device.getName(); } catch (SecurityException ignored) { }
            }
            String address = safeAddress(device);
            byte[] adv = result.getScanRecord() == null ? null : result.getScanRecord().getBytes();
            String key = address + "|" + name + "|" + result.getRssi() + "|" + hex(adv);
            if (loggedAdvertisements.add(key)) {
                writeLog("ADV\tname=" + safe(name) + "\taddress=" + address + "\trssi=" + result.getRssi() +
                        "\ttx_power=" + (result.getTxPower() == Integer.MIN_VALUE ? "unknown" : result.getTxPower()) +
                        "\traw=" + hex(adv));
                appendUi("ADV " + safe(name) + " " + maskAddress(address) + " RSSI=" + result.getRssi());
            }
            if (!connecting && name != null && name.toLowerCase(Locale.ROOT).contains("midea")) {
                connecting = true;
                stopScan("发现目标 " + safe(name));
                connect(device);
            }
        }

        @Override
        public void onScanFailed(int errorCode) {
            scanning = false;
            actionButton.setText("重新扫描");
            setStatus("扫描失败：" + errorCode);
            writeLog("SCAN_FAILED\tcode=" + errorCode);
        }
    };

    private void connect(BluetoothDevice device) {
        String address = safeAddress(device);
        setStatus("正在连接 midea：" + maskAddress(address));
        writeLog("CONNECT_START\tname=" + safe(device.getName()) + "\taddress=" + address);
        try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
                gatt = device.connectGatt(this, false, gattCallback, BluetoothDevice.TRANSPORT_LE);
            } else {
                gatt = device.connectGatt(this, false, gattCallback);
            }
        } catch (SecurityException e) {
            writeLog("CONNECT_ERROR\t" + e);
            setStatus("连接权限异常");
            connecting = false;
        }
    }

    private final BluetoothGattCallback gattCallback = new BluetoothGattCallback() {
        @Override
        public void onConnectionStateChange(BluetoothGatt connectedGatt, int status, int newState) {
            writeLog("CONNECTION\tstatus=" + status + "\tstate=" + (newState == BluetoothProfile.STATE_CONNECTED ? "CONNECTED" : "DISCONNECTED"));
            if (newState == BluetoothProfile.STATE_CONNECTED) {
                setStatus("已连接，正在枚举 GATT 服务");
                try { connectedGatt.discoverServices(); } catch (SecurityException e) { writeLog("DISCOVER_ERROR\t" + e); }
            } else {
                setStatus("设备已断开；已保存当前日志");
                connecting = false;
                closeGatt();
            }
        }

        @Override
        public void onServicesDiscovered(BluetoothGatt connectedGatt, int status) {
            writeLog("SERVICES_DISCOVERED\tstatus=" + status);
            if (status != BluetoothGatt.GATT_SUCCESS) return;
            descriptorQueue.clear();
            readQueue.clear();
            for (BluetoothGattService service : connectedGatt.getServices()) {
                writeLog("SERVICE\tuuid=" + service.getUuid());
                for (BluetoothGattCharacteristic characteristic : service.getCharacteristics()) {
                    int properties = characteristic.getProperties();
                    writeLog("CHARACTERISTIC\tservice=" + service.getUuid() + "\tuuid=" + characteristic.getUuid() +
                            "\tproperties=" + propertyNames(properties));
                    if ((properties & (BluetoothGattCharacteristic.PROPERTY_NOTIFY | BluetoothGattCharacteristic.PROPERTY_INDICATE)) != 0) {
                        for (BluetoothGattDescriptor descriptor : characteristic.getDescriptors()) {
                            if (CCCD.equals(descriptor.getUuid())) descriptorQueue.add(descriptor);
                        }
                    }
                    if ((properties & BluetoothGattCharacteristic.PROPERTY_READ) != 0) readQueue.add(characteristic);
                }
            }
            configureNextDescriptor();
        }

        @Override
        public void onDescriptorWrite(BluetoothGatt connectedGatt, BluetoothGattDescriptor descriptor, int status) {
            writeLog("DESCRIPTOR_WRITE\tuuid=" + descriptor.getUuid() + "\tstatus=" + status);
            configureNextDescriptor();
        }

        @Override
        public void onCharacteristicRead(BluetoothGatt connectedGatt, BluetoothGattCharacteristic characteristic, int status) {
            logCharacteristic("READ", characteristic.getService().getUuid(), characteristic.getUuid(), characteristic.getValue(), status);
            requestNextRead();
        }

        @Override
        public void onCharacteristicRead(BluetoothGatt connectedGatt, BluetoothGattCharacteristic characteristic, byte[] value, int status) {
            logCharacteristic("READ", characteristic.getService().getUuid(), characteristic.getUuid(), value, status);
            requestNextRead();
        }

        @Override
        public void onCharacteristicChanged(BluetoothGatt connectedGatt, BluetoothGattCharacteristic characteristic) {
            logCharacteristic("NOTIFY", characteristic.getService().getUuid(), characteristic.getUuid(), characteristic.getValue(), BluetoothGatt.GATT_SUCCESS);
        }

        @Override
        public void onCharacteristicChanged(BluetoothGatt connectedGatt, BluetoothGattCharacteristic characteristic, byte[] value) {
            logCharacteristic("NOTIFY", characteristic.getService().getUuid(), characteristic.getUuid(), value, BluetoothGatt.GATT_SUCCESS);
        }
    };

    private void configureNextDescriptor() {
        if (gatt == null) return;
        BluetoothGattDescriptor descriptor = descriptorQueue.poll();
        if (descriptor == null) {
            requestNextRead();
            return;
        }
        BluetoothGattCharacteristic characteristic = descriptor.getCharacteristic();
        byte[] value = (characteristic.getProperties() & BluetoothGattCharacteristic.PROPERTY_INDICATE) != 0
                ? BluetoothGattDescriptor.ENABLE_INDICATION_VALUE
                : BluetoothGattDescriptor.ENABLE_NOTIFICATION_VALUE;
        try {
            gatt.setCharacteristicNotification(characteristic, true);
            if (Build.VERSION.SDK_INT >= 33) {
                gatt.writeDescriptor(descriptor, value);
            } else {
                descriptor.setValue(value);
                gatt.writeDescriptor(descriptor);
            }
        } catch (SecurityException e) {
            writeLog("DESCRIPTOR_WRITE_ERROR\tuuid=" + descriptor.getUuid() + "\t" + e);
            configureNextDescriptor();
        }
    }

    private void requestNextRead() {
        if (gatt == null) return;
        BluetoothGattCharacteristic characteristic = readQueue.poll();
        if (characteristic == null) {
            setStatus("采集进行中：通知已订阅，等待设备上报");
            writeLog("READY_FOR_NOTIFICATIONS");
            return;
        }
        try {
            if (!gatt.readCharacteristic(characteristic)) {
                writeLog("READ_START_FAILED\tuuid=" + characteristic.getUuid());
                requestNextRead();
            }
        } catch (SecurityException e) {
            writeLog("READ_ERROR\tuuid=" + characteristic.getUuid() + "\t" + e);
            requestNextRead();
        }
    }

    private void logCharacteristic(String kind, UUID service, UUID characteristic, byte[] value, int status) {
        writeLog(kind + "\tservice=" + service + "\tcharacteristic=" + characteristic + "\tstatus=" + status + "\tvalue_hex=" + hex(value));
        appendUi(kind + " " + characteristic + " bytes=" + (value == null ? 0 : value.length));
    }

    private void openCapture() {
        File base = getExternalFilesDir(Environment.DIRECTORY_DOCUMENTS);
        if (base == null) base = getFilesDir();
        File dir = new File(base, "sleep_ble_captures");
        if (!dir.exists() && !dir.mkdirs()) {
            setStatus("无法创建日志目录");
            return;
        }
        File file = new File(dir, "ble_capture_" + new SimpleDateFormat("yyyyMMdd_HHmmss", Locale.US).format(new Date()) + ".tsv");
        capturePath = file.getAbsolutePath();
        try {
            capture = new FileWriter(file, true);
            writeLog("CAPTURE_FILE\tpath=" + capturePath);
        } catch (IOException e) {
            setStatus("无法打开日志文件");
        }
    }

    private void writeLog(String message) {
        String line = new SimpleDateFormat("yyyy-MM-dd'T'HH:mm:ss.SSSXXX", Locale.US).format(new Date()) + "\t" + message + "\n";
        try {
            if (capture != null) { capture.write(line); capture.flush(); }
        } catch (IOException ignored) { }
        appendUi(message);
    }

    private void appendUi(String message) {
        runOnUiThread(() -> {
            if (logView == null) return;
            String old = logView.getText().toString();
            String next = old + message + "\n";
            if (next.length() > 12000) next = next.substring(next.length() - 12000);
            logView.setText(next);
        });
    }

    private void setStatus(String text) {
        runOnUiThread(() -> { if (statusView != null) statusView.setText(text); });
    }

    private void closeGatt() {
        if (gatt == null) return;
        try { gatt.close(); } catch (SecurityException ignored) { }
        gatt = null;
    }

    private String safeAddress(BluetoothDevice device) {
        try { return device.getAddress(); } catch (SecurityException e) { return "permission_denied"; }
    }

    private String maskAddress(String value) {
        if (value == null || value.length() < 5) return value;
        return "••:••:••:" + value.substring(value.length() - 8);
    }

    private String safe(String value) { return value == null ? "<unknown>" : value.replace('\t', '_').replace('\n', '_'); }

    private String hex(byte[] bytes) {
        if (bytes == null || bytes.length == 0) return "";
        StringBuilder out = new StringBuilder(bytes.length * 2);
        for (byte value : bytes) out.append(String.format(Locale.US, "%02X", value & 0xFF));
        return out.toString();
    }

    private String propertyNames(int properties) {
        List<String> names = new ArrayList<>();
        if ((properties & BluetoothGattCharacteristic.PROPERTY_READ) != 0) names.add("READ");
        if ((properties & BluetoothGattCharacteristic.PROPERTY_WRITE) != 0) names.add("WRITE");
        if ((properties & BluetoothGattCharacteristic.PROPERTY_WRITE_NO_RESPONSE) != 0) names.add("WRITE_NO_RESPONSE");
        if ((properties & BluetoothGattCharacteristic.PROPERTY_NOTIFY) != 0) names.add("NOTIFY");
        if ((properties & BluetoothGattCharacteristic.PROPERTY_INDICATE) != 0) names.add("INDICATE");
        return names.toString();
    }

    @Override
    protected void onDestroy() {
        stopScan("应用关闭");
        closeGatt();
        try { if (capture != null) capture.close(); } catch (IOException ignored) { }
        super.onDestroy();
    }
}
