#include <Arduino.h>
#include <WiFi.h>
#include <NimBLEDevice.h>
#include "secrets.h"

// const char* ssid = "YOUR_WIFI_SSID";
// const char* password = "YOUR_WIFI_PASSWORD";

bool wifiEnabled = false;

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
  WiFi.disconnect(true);   // Disconnect and turn off radio
  WiFi.mode(WIFI_OFF);
  wifiEnabled = false;
  Serial.println("✅ WiFi turned OFF");
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
  
  Serial.println("BLE: Advertising (always on)");
  Serial.println("--------------------");
}

void setup() {
  Serial.begin(115200);
  delay(1000);
  
  Serial.println("\n=== ESP32 Serial Command Control Ready ===");
  Serial.println("Available commands:");
  Serial.println("  wifi on     → Start WiFi");
  Serial.println("  wifi off    → Stop WiFi");
  Serial.println("  status      → Show current status");
  Serial.println("  restart     → Restart ESP32");
  
  // Start BLE (always on)
  NimBLEDevice::init("MyESP32_BLE");
  NimBLEAdvertising* pAdvertising = NimBLEDevice::getAdvertising();
  pAdvertising->start();
  Serial.println("✅ BLE Advertising started");
  
  printStatus();
}

void loop() {
  // Handle Serial commands
  if (Serial.available()) {
    String command = Serial.readStringUntil('\n');
    command.trim();
    command.toLowerCase();

    if (command == "wifi on") {
      startWiFi();
    }
    else if (command == "wifi off") {
      stopWiFi();
    }
    else if (command == "status") {
      printStatus();
    }
    else if (command == "restart") {
      Serial.println("Restarting...");
      delay(1000);
      ESP.restart();
    }
    else if (command.length() > 0) {
      Serial.println("Unknown command. Try: wifi on, wifi off, status, restart");
    }
  }

  delay(1000);   // Small delay to avoid CPU hogging
}