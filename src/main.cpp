#include <Arduino.h>

#define LED 2   // Built-in LED on most ESP32 boards

void setup() {
  Serial.begin(115200);
  pinMode(LED, OUTPUT);
  Serial.println("ESP32 Ready!");
}

void loop() {
  // digitalWrite(LED, HIGH);
  // Serial.println("LED ON");
  // delay(1000);
  
  // digitalWrite(LED, LOW);
  // Serial.println("LED OFF");
  // delay(1000);
}