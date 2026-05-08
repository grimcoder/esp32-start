#include <Arduino.h>
#include <WiFi.h>
#include <NimBLEDevice.h>
#include "secrets.h"

void setup() {
  Serial.begin(115200);

  // === WiFi ===
  WiFi.mode(WIFI_STA);
  WiFi.begin(WIFI_SSID, WIFI_PASSWORD);
  while (WiFi.status() != WL_CONNECTED) {
    delay(500);
    Serial.print(".");
  }
  Serial.println("\nWiFi Connected!");

  // === BLE ===
  NimBLEDevice::init("TARAS-ESP32");
  NimBLEServer* pServer = NimBLEDevice::createServer();
  // ... add services/characteristics as needed
  NimBLEAdvertising* pAdvertising = NimBLEDevice::getAdvertising();
  pAdvertising->start();

  Serial.println("Both WiFi + BLE are running!");
}

void loop() {
  delay(5000);
}