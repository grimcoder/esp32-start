#include <Arduino.h>
#include <WiFi.h>
#include <WebServer.h>
#include <NimBLEDevice.h>
#include <AccelStepper.h>
#include "secrets.h"

// ── Stepper motor (MKS APT / A4988 / DRV8825 etc.) ──────────────────────────
#define MOTOR_STEP_PIN  18
#define MOTOR_DIR_PIN   19
#define MOTOR_EN_PIN    21

// Pointer — AccelStepper is constructed in setup() after Arduino hardware init.
// A global object calls enableOutputs()/pinMode() before hardware is ready.
AccelStepper* stepper = nullptr;

// Nordic UART Service UUIDs
#define BLE_SERVICE_UUID  "6E400001-B5A3-F393-E0A9-E50E24DCCA9E"
#define BLE_RX_UUID       "6E400002-B5A3-F393-E0A9-E50E24DCCA9E"  // client writes here
#define BLE_TX_UUID       "6E400003-B5A3-F393-E0A9-E50E24DCCA9E"  // ESP32 notifies here

bool wifiEnabled = false;
bool bleEnabled = false;
bool webEnabled = false;
bool bleClientConnected = false;
bool motorEnabled = false;

WebServer server(80);
NimBLECharacteristic* pTxCharacteristic = nullptr;

// Forward declarations
void startWebServer();
void stopWebServer();
void processCommand(const String& cmd);

// ── BLE helpers ──────────────────────────────────────────────────────────────

void bleSend(const String& msg) {
  if (pTxCharacteristic && bleClientConnected) {
    pTxCharacteristic->setValue(msg.c_str());
    pTxCharacteristic->notify();
  }
}

class BLEServerCb : public NimBLEServerCallbacks {
  void onConnect(NimBLEServer*, NimBLEConnInfo&) override {
    bleClientConnected = true;
    Serial.println("BLE client connected");
    bleSend("ESP32 ready. Send commands: wifi on/off, ble on/off, web on/off, status, restart");
    NimBLEDevice::getAdvertising()->stop();
  }
  void onDisconnect(NimBLEServer*, NimBLEConnInfo&, int) override {
    bleClientConnected = false;
    Serial.println("BLE client disconnected");
    NimBLEDevice::getAdvertising()->start();
  }
};

class BLERxCb : public NimBLECharacteristicCallbacks {
  void onWrite(NimBLECharacteristic* pChr, NimBLEConnInfo&) override {
    String cmd = pChr->getValue().c_str();
    cmd.trim();
    cmd.toLowerCase();
    if (cmd.length() > 0) {
      Serial.println("[BLE] " + cmd);
      processCommand(cmd);
    }
  }
};

void startWiFi() {
  if (wifiEnabled) {
    Serial.println("WiFi is already on");
    return;
  }
  
  Serial.println("Starting WiFi...");
  WiFi.mode(WIFI_STA);
  WiFi.begin(WIFI_SSID, WIFI_PASSWORD);
  
  unsigned long startAttempt = millis();
  while (WiFi.status() != WL_CONNECTED && millis() - startAttempt < 15000) {
    delay(500);
    Serial.print(".");
  }

  if (WiFi.status() == WL_CONNECTED) {
    wifiEnabled = true;
    Serial.println("\n✅ WiFi Connected!");
    Serial.print("IP Address: ");
    Serial.println(WiFi.localIP());
    startWebServer();
  } else {
    Serial.println("\n❌ Failed to connect to WiFi");
  }
}

void stopWiFi() {
  if (!wifiEnabled) {
    Serial.println("WiFi is already off");
    return;
  }
  
  Serial.println("Stopping WiFi...");
  stopWebServer();
  WiFi.disconnect(true);   // Disconnect and turn off radio
  WiFi.mode(WIFI_OFF);
  wifiEnabled = false;
  Serial.println("✅ WiFi turned OFF");
}

void startWebServer() {
  if (webEnabled) {
    Serial.println("Web server is already on");
    return;
  }
  if (!wifiEnabled) {
    Serial.println("❌ WiFi must be on first");
    return;
  }

  server.on("/", []() {
    String html = "<!DOCTYPE html><html><head><title>ESP32</title></head><body>";
    html += "<h1>ESP32 Status</h1>";
    html += "<p>WiFi: " + String(WiFi.localIP().toString()) + "</p>";
    html += "<p>RSSI: " + String(WiFi.RSSI()) + " dBm</p>";
    html += "<p>BLE: " + String(bleEnabled ? "ON" : "OFF") + "</p>";
    html += "<p>Uptime: " + String(millis() / 1000) + "s</p>";
    html += "</body></html>";
    server.send(200, "text/html", html);
  });

  server.on("/status", []() {
    String json = "{";
    json += "\"wifi\":\"" + WiFi.localIP().toString() + "\",";
    json += "\"rssi\":" + String(WiFi.RSSI()) + ",";
    json += "\"ble\":" + String(bleEnabled ? "true" : "false") + ",";
    json += "\"uptime\":" + String(millis() / 1000);
    json += "}";
    server.send(200, "application/json", json);
  });

  server.begin();
  webEnabled = true;
  Serial.println("✅ Web server started on http://" + WiFi.localIP().toString());
}

void stopWebServer() {
  if (!webEnabled) {
    Serial.println("Web server is already off");
    return;
  }

  server.stop();
  webEnabled = false;
  Serial.println("✅ Web server stopped");
}

void startBLE() {
  if (bleEnabled) {
    Serial.println("BLE is already on");
    return;
  }

  Serial.println("Starting BLE...");
  NimBLEDevice::init("XXXX-ESP32-BLE");

  NimBLEServer* pServer = NimBLEDevice::createServer();
  pServer->setCallbacks(new BLEServerCb());

  NimBLEService* pService = pServer->createService(BLE_SERVICE_UUID);

  // TX characteristic – ESP32 → client (notify)
  pTxCharacteristic = pService->createCharacteristic(
    BLE_TX_UUID,
    NIMBLE_PROPERTY::NOTIFY
  );

  // RX characteristic – client → ESP32 (write)
  NimBLECharacteristic* pRxCharacteristic = pService->createCharacteristic(
    BLE_RX_UUID,
    NIMBLE_PROPERTY::WRITE | NIMBLE_PROPERTY::WRITE_NR
  );
  pRxCharacteristic->setCallbacks(new BLERxCb());

  pService->start();

  NimBLEAdvertising* pAdvertising = NimBLEDevice::getAdvertising();
  pAdvertising->addServiceUUID(BLE_SERVICE_UUID);
  pAdvertising->start();

  bleEnabled = true;
  Serial.println("✅ BLE advertising started (NUS service ready)");
}

void stopBLE() {
  if (!bleEnabled) {
    Serial.println("BLE is already off");
    return;
  }

  Serial.println("Stopping BLE...");
  NimBLEDevice::getAdvertising()->stop();
  NimBLEDevice::deinit(true);
  bleEnabled = false;
  Serial.println("✅ BLE turned OFF");
}

void printStatus() {
  Serial.println("\n--- ESP32 Status ---");
  Serial.print("WiFi: ");
  Serial.println(wifiEnabled ? "ON" : "OFF");
  
  if (wifiEnabled && WiFi.status() == WL_CONNECTED) {
    Serial.print("IP: ");
    Serial.println(WiFi.localIP());
    Serial.print("RSSI: ");
    Serial.print(WiFi.RSSI());
    Serial.println(" dBm");
  }
  
  Serial.print("BLE: ");
  Serial.println(bleEnabled ? "ON" : "OFF");
  Serial.print("Web: ");
  if (webEnabled)
    Serial.println("ON → http://" + WiFi.localIP().toString());
  else
    Serial.println("OFF");
  Serial.print("Motor: pos=");
  Serial.print(stepper ? stepper->currentPosition() : 0);
  Serial.print(" target=");
  Serial.print(stepper ? stepper->targetPosition() : 0);
  Serial.print(" running=");
  Serial.println((stepper && stepper->isRunning()) ? "YES" : "NO");
  Serial.println("--------------------");
}

// ── Command dispatcher (shared by serial and BLE) ────────────────────────────

void processCommand(const String& cmd) {
  String response;

  if (cmd == "wifi on") {
    startWiFi();
    response = wifiEnabled ? "WiFi ON, IP: " + WiFi.localIP().toString() : "WiFi failed";
  }
  else if (cmd == "wifi off") {
    stopWiFi();
    response = "WiFi OFF";
  }
  else if (cmd == "ble on") {
    startBLE();
    response = "BLE ON";
  }
  else if (cmd == "ble off") {
    bleSend("BLE stopping...");
    stopBLE();
    return; // can't notify after deinit
  }
  else if (cmd == "web on") {
    startWebServer();
    response = webEnabled ? "Web ON: http://" + WiFi.localIP().toString() : "Web failed (WiFi required)";
  }
  else if (cmd == "web off") {
    stopWebServer();
    response = "Web OFF";
  }
  else if (cmd == "status") {
    printStatus();
    response = "WiFi:" + String(wifiEnabled ? WiFi.localIP().toString() : "OFF")
             + " BLE:" + (bleEnabled ? "ON" : "OFF")
             + " Web:" + (webEnabled ? "ON" : "OFF")
             + " Motor:pos=" + String(stepper ? stepper->currentPosition() : 0);
  }
  // ── Motor commands ──────────────────────────────────────────────────────────
  else if (cmd.startsWith("motor move ")) {
    long steps = cmd.substring(11).toInt();
    if (stepper) stepper->moveTo(steps);
    response = "Moving to " + String(steps) + " steps";
    Serial.println(response);
  }
  else if (cmd.startsWith("motor step ")) {
    long delta = cmd.substring(11).toInt();
    if (stepper) stepper->move(delta);
    response = "Moving " + String(delta) + " steps relative";
    Serial.println(response);
  }
  else if (cmd == "motor stop") {
    if (stepper) stepper->stop();
    response = "Motor stopped at " + String(stepper ? stepper->currentPosition() : 0);
    Serial.println(response);
  }
  else if (cmd == "motor home") {
    if (stepper) stepper->moveTo(0);
    response = "Homing to 0";
    Serial.println(response);
  }
  else if (cmd == "motor zero") {
    if (stepper) stepper->setCurrentPosition(0);
    response = "Position zeroed";
    Serial.println(response);
  }
  else if (cmd.startsWith("motor speed ")) {
    float spd = cmd.substring(12).toFloat();
    if (stepper) stepper->setMaxSpeed(spd);
    response = "Max speed set to " + String(spd);
    Serial.println(response);
  }
  else if (cmd.startsWith("motor accel ")) {
    float acc = cmd.substring(12).toFloat();
    if (stepper) stepper->setAcceleration(acc);
    response = "Acceleration set to " + String(acc);
    Serial.println(response);
  }
  else if (cmd == "motor enable") {
    digitalWrite(MOTOR_EN_PIN, LOW);
    motorEnabled = true;
    response = "Motor enabled";
    Serial.println(response);
  }
  else if (cmd == "motor disable") {
    if (stepper) stepper->stop();
    digitalWrite(MOTOR_EN_PIN, HIGH);
    motorEnabled = false;
    response = "Motor disabled";
    Serial.println(response);
  }
  else if (cmd == "motor status") {
    response = "pos=" + String(stepper ? stepper->currentPosition() : 0)
             + " target=" + String(stepper ? stepper->targetPosition() : 0)
             + " running=" + String((stepper && stepper->isRunning()) ? "YES" : "NO")
             + " enabled=" + String(motorEnabled ? "YES" : "NO");
    Serial.println(response);
  }
  else if (cmd == "restart") {
    bleSend("Restarting...");
    Serial.println("Restarting...");
    delay(500);
    ESP.restart();
  }
  else {
    response = "Unknown: " + cmd;
    Serial.println("Unknown command: " + cmd);
  }

  if (response.length() > 0) {
    bleSend(response);
  }
}

void setup() {
  Serial.begin(115200);
  delay(1000);
  
  Serial.println("\n=== ESP32 Serial Command Control Ready ===");
  Serial.println("Available commands:");
  Serial.println("  wifi on     → Start WiFi");
  Serial.println("  wifi off    → Stop WiFi");
  Serial.println("  ble on      → Start BLE");
  Serial.println("  ble off     → Stop BLE");
  Serial.println("  web on      → Start web server (requires WiFi)");
  Serial.println("  web off     → Stop web server");
  Serial.println("  status      → Show current status");
  Serial.println("  restart     → Restart ESP32");
  Serial.println("  motor move <steps>   → Move to absolute step position");
  Serial.println("  motor step <delta>   → Move relative steps (+/-)");
  Serial.println("  motor stop           → Stop motor");
  Serial.println("  motor home           → Return to position 0");
  Serial.println("  motor zero           → Set current position as 0");
  Serial.println("  motor speed <pps>    → Set max speed (steps/sec)");
  Serial.println("  motor accel <pps2>   → Set acceleration");
  Serial.println("  motor enable/disable → Enable/disable driver");
  Serial.println("  motor status         → Motor position and state");

  // ── Motor init ──────────────────────────────────────────────────────────────
  // Construct on the heap here, after Arduino hardware is fully initialised.
  stepper = new AccelStepper(AccelStepper::DRIVER, MOTOR_STEP_PIN, MOTOR_DIR_PIN, 0, 0, false);
  pinMode(MOTOR_EN_PIN, OUTPUT);
  digitalWrite(MOTOR_EN_PIN, LOW);   // Active LOW — enable by default
  motorEnabled = true;
  stepper->enableOutputs();          // Set STEP/DIR pins as OUTPUT (safe here)
  stepper->setMaxSpeed(1000);
  stepper->setAcceleration(600);
  Serial.println("Motor ready (step=" + String(MOTOR_STEP_PIN) + ", dir=" + String(MOTOR_DIR_PIN) + ", en=" + String(MOTOR_EN_PIN) + ")");
  Serial.flush();

  startBLE();
  
  printStatus();
}

void loop() {
  // Handle Serial commands
  if (Serial.available()) {
    String command = Serial.readStringUntil('\n');
    command.trim();
    command.toLowerCase();
    if (command.length() > 0) {
      processCommand(command);
    }
  }

  if (webEnabled) {
    server.handleClient();
  }

  if (stepper) stepper->run();   // Must be called as often as possible
}