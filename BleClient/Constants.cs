namespace BleClient;

internal static class Constants
{
    // Target device name (matches NimBLEDevice::init() in firmware)
    public const string TargetName = "XXXX-ESP32-BLE";

    // Nordic UART Service (NUS) — de-facto standard BLE serial profile
    // https://developer.nordicsemi.com/nRF_Connect_SDK/doc/latest/nrf/libraries/bluetooth_services/services/nus.html
    public const string NusServiceUuid = "6e400001-b5a3-f393-e0a9-e50e24dcca9e";
    public const string NusRxUuid      = "6e400002-b5a3-f393-e0a9-e50e24dcca9e"; // client → device
    public const string NusTxUuid      = "6e400003-b5a3-f393-e0a9-e50e24dcca9e"; // device → client (notify)

    // Scan / connection timeouts
    public const int ScanTimeoutSeconds    = 20;
    public const int ConnectTimeoutSeconds = 10;
    public const int BtReadyTimeoutSeconds = 5;
}
