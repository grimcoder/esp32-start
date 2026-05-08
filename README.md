# ESP32 Start

ESP32 firmware with WiFi, BLE (Nordic UART Service), and a simple web server — all controllable via serial or BLE commands.

---

## BLE Interface

The firmware exposes a **Nordic UART Service (NUS)** — a standard BLE profile for bidirectional text communication. Any BLE terminal app can connect and send commands.

### UUIDs

| Role | UUID |
|------|------|
| Service | `6E400001-B5A3-F393-E0A9-E50E24DCCA9E` |
| RX (client → ESP32) | `6E400002-B5A3-F393-E0A9-E50E24DCCA9E` |
| TX (ESP32 → client) | `6E400003-B5A3-F393-E0A9-E50E24DCCA9E` |

### Connecting

1. Power on the ESP32. It advertises as **`XXXX-ESP32-BLE`**.
2. Open a BLE terminal app on your phone or desktop:
   - **iOS/macOS**: [LightBlue](https://punchthrough.com/lightblue/) or [nRF Toolbox](https://www.nordicsemi.com/Products/Development-tools/nrf-toolbox)
   - **Android**: [Serial Bluetooth Terminal](https://play.google.com/store/apps/details?id=de.kai_morich.serial_bluetooth_terminal) (BLE mode) or nRF Toolbox
   - **Desktop**: [nRF Connect](https://www.nordicsemi.com/Products/Development-tools/nrf-connect-for-desktop)
3. Scan and connect to **`XXXX-ESP32-BLE`**.
4. **Subscribe (enable notifications) on the TX characteristic** (`6E400003…`) to receive responses.
5. Write commands as UTF-8 text to the **RX characteristic** (`6E400002…`).

On successful connection the ESP32 sends a welcome message over TX:
```
ESP32 ready. Send commands: wifi on/off, ble on/off, web on/off, status, restart
```

### Commands

| Command | Description | BLE response |
|---------|-------------|--------------|
| `wifi on` | Connect to WiFi (credentials from `secrets.h`) | `WiFi ON, IP: 192.168.x.x` or `WiFi failed` |
| `wifi off` | Disconnect WiFi and shut down web server | `WiFi OFF` |
| `ble on` | Start BLE (if stopped) | `BLE ON` |
| `ble off` | Stop BLE advertising and deinit | `BLE stopping...` (final message before shutdown) |
| `web on` | Start HTTP server (requires WiFi) | `Web ON: http://192.168.x.x` |
| `web off` | Stop HTTP server | `Web OFF` |
| `status` | Print current state to serial + return compact status | `WiFi:192.168.x.x BLE:ON Web:ON` |
| `restart` | Restart the ESP32 | `Restarting...` |

Commands are case-insensitive and whitespace-trimmed.

### Lifecycle

```
Power on
  └─► startBLE()
        ├─ Advertises as "XXXX-ESP32-BLE"
        └─ Waits for client

Client connects
  ├─ Advertising stops
  ├─ TX sends welcome message
  └─ RX listens for commands

Client writes command → RX characteristic
  └─► processCommand()
        ├─ Executes action (WiFi, web server, etc.)
        └─ Sends response string via TX notify

Client disconnects
  └─► Advertising resumes automatically
```

### Notes

- **WiFi and BLE coexist** on the ESP32 using the built-in coexistence mode.
- The `ble off` command is a one-way operation — once sent, BLE is torn down and a new `ble on` command must come from serial.
- Credentials (SSID/password) are stored in `include/secrets.h` (gitignored). Copy `include/secrets.h.example` and fill in your values.

---

## Serial Interface

The same commands work over the USB serial monitor at **115200 baud**. This is the primary way to control the device when no BLE client is connected.

---

## Web Server

When WiFi is connected, an HTTP server runs on port 80:

| Endpoint | Response |
|----------|----------|
| `GET /` | HTML status page |
| `GET /status` | JSON: `{"wifi":"...","rssi":...,"ble":true,"uptime":...}` |

---

## Credentials Setup

```bash
cp include/secrets.h.example include/secrets.h
# Edit include/secrets.h and set WIFI_SSID and WIFI_PASSWORD
```

`include/secrets.h` is listed in `.gitignore` and will never be committed.
