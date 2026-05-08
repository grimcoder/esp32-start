using BleClient;
using CoreBluetooth;
using CoreFoundation;
using Foundation;
using System.Text;

// ── NUS UUIDs (must match firmware) ─────────────────────────────────────────
var serviceUuid = CBUUID.FromString(Constants.NusServiceUuid);
var rxUuid      = CBUUID.FromString(Constants.NusRxUuid);
var txUuid      = CBUUID.FromString(Constants.NusTxUuid);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

await new BleSession(Constants.TargetName, serviceUuid, rxUuid, txUuid, cts).RunAsync();

// ── Session ───────────────────────────────────────────────────────────────────
sealed class BleSession
{
    private readonly string _name;
    private readonly CBUUID _svc, _rx, _tx;
    private readonly CancellationTokenSource _cts;

    private CBCentralManager? _central;
    private CBPeripheral?     _peripheral;
    private CBCharacteristic? _rxChar, _txChar;

    private readonly TaskCompletionSource<bool>         _btTcs   = new();
    private readonly TaskCompletionSource<CBPeripheral> _pTcs    = new();
    private readonly TaskCompletionSource<bool>         _conTcs  = new();
    private readonly TaskCompletionSource<bool>         _charTcs = new();

    public BleSession(string name, CBUUID svc, CBUUID rx, CBUUID tx, CancellationTokenSource cts)
    {
        _name = name; _svc = svc; _rx = rx; _tx = tx; _cts = cts;
    }

    public async Task RunAsync()
    {
        var queue = new DispatchQueue("ble", false);
        _central = new CBCentralManager(new CentralDelegate(this), queue);

        // 1. Wait for Bluetooth ready
        Console.WriteLine("Waiting for Bluetooth...");
        try   { await _btTcs.Task.WaitAsync(TimeSpan.FromSeconds(Constants.BtReadyTimeoutSeconds), _cts.Token); }
        catch { Console.WriteLine("Bluetooth unavailable."); return; }

        // 2. Scan
        Console.WriteLine($"Scanning for '{_name}'... (Ctrl+C to cancel)");
        _central.ScanForPeripherals(new[] { _svc });

        try
        {
            using var scanCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            scanCts.CancelAfter(TimeSpan.FromSeconds(Constants.ScanTimeoutSeconds));
            _peripheral = await _pTcs.Task.WaitAsync(scanCts.Token);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        { _central.StopScan(); Console.WriteLine("Cancelled."); return; }
        catch (OperationCanceledException)
        { _central.StopScan(); Console.WriteLine($"'{_name}' not found within {Constants.ScanTimeoutSeconds} s."); return; }

        _central.StopScan();

        // 3. Connect
        Console.WriteLine($"Found '{_peripheral!.Name}'. Connecting...");
        _central.ConnectPeripheral(_peripheral);
        try   { await _conTcs.Task.WaitAsync(TimeSpan.FromSeconds(Constants.ConnectTimeoutSeconds), _cts.Token); }
        catch { Console.WriteLine("Connection failed."); return; }
        Console.WriteLine("Connected.");

        // 4. Discover service + characteristics
        _peripheral.Delegate = new PeripheralDelegate(this);
        _peripheral.DiscoverServices(new[] { _svc });
        try   { await _charTcs.Task.WaitAsync(TimeSpan.FromSeconds(10), _cts.Token); }
        catch { Console.WriteLine("Service discovery failed."); return; }

        if (_rxChar is null || _txChar is null)
        { Console.WriteLine("NUS characteristics not found."); return; }

        // 5. Subscribe to TX notifications (ESP32 → client)
        _peripheral.SetNotifyValue(true, _txChar);

        Console.WriteLine("\nAvailable commands: wifi on/off  ble on/off  web on/off  status  restart  exit\n");

        // 6. Command loop
        while (!_cts.IsCancellationRequested)
        {
            Console.Write("> ");
            string? line;
            try   { line = await Task.Run(Console.ReadLine, _cts.Token); }
            catch { break; }

            if (line is null || line.Trim().Equals("exit", StringComparison.OrdinalIgnoreCase)) break;
            if (string.IsNullOrWhiteSpace(line)) continue;

            _peripheral.WriteValue(
                NSData.FromArray(Encoding.UTF8.GetBytes(line.Trim())),
                _rxChar, CBCharacteristicWriteType.WithoutResponse);
        }

        // 7. Cleanup
        _peripheral.SetNotifyValue(false, _txChar);
        _central.CancelPeripheralConnection(_peripheral);
        Console.WriteLine("\nDisconnected.");
    }

    // ── Callbacks (called by delegates on the dispatch queue) ─────────────────
    internal void BtReady()                           => _btTcs.TrySetResult(true);
    internal void BtFailed(string msg)                => _btTcs.TrySetException(new Exception(msg));
    internal void Discovered(CBPeripheral p)          => _pTcs.TrySetResult(p);
    internal void Connected()                         => _conTcs.TrySetResult(true);
    internal void ConnFailed(string msg)              => _conTcs.TrySetException(new Exception(msg));
    internal void CharsReady(CBCharacteristic? rx, CBCharacteristic? tx)
    {
        _rxChar = rx; _txChar = tx; _charTcs.TrySetResult(true);
    }
    internal void Disconnected(string? reason)
    {
        Console.WriteLine($"\n[Disconnected: {reason ?? "clean"}]");
        _cts.Cancel();
    }
    internal void OnNotification(byte[] value)
    {
        Console.WriteLine($"\n[ESP32] {Encoding.UTF8.GetString(value)}");
        Console.Write("> ");
    }

    // ── CBCentralManagerDelegate ──────────────────────────────────────────────
    private sealed class CentralDelegate(BleSession s) : CBCentralManagerDelegate
    {
        public override void UpdatedState(CBCentralManager central)
        {
            if (central.State == CBManagerState.PoweredOn) s.BtReady();
            else s.BtFailed($"Bluetooth state: {central.State}");
        }

        public override void DiscoveredPeripheral(CBCentralManager central, CBPeripheral peripheral,
            NSDictionary advertisementData, NSNumber rssi)
        {
            if (peripheral.Name == s._name) s.Discovered(peripheral);
        }

        public override void ConnectedPeripheral(CBCentralManager central, CBPeripheral peripheral)
            => s.Connected();

        public override void FailedToConnectPeripheral(CBCentralManager central, CBPeripheral peripheral, NSError? error)
            => s.ConnFailed(error?.LocalizedDescription ?? "unknown error");

        public override void DisconnectedPeripheral(CBCentralManager central, CBPeripheral peripheral, NSError? error)
            => s.Disconnected(error?.LocalizedDescription);
    }

    // ── CBPeripheralDelegate ──────────────────────────────────────────────────
    private sealed class PeripheralDelegate(BleSession s) : CBPeripheralDelegate
    {
        public override void DiscoveredService(CBPeripheral peripheral, NSError? error)
        {
            if (error is not null) { s.CharsReady(null, null); return; }
            var svc = peripheral.Services?.FirstOrDefault(sv => sv.UUID == s._svc);
            if (svc is null) { s.CharsReady(null, null); return; }
            peripheral.DiscoverCharacteristics(new[] { s._rx, s._tx }, svc);
        }

        public override void DiscoveredCharacteristics(CBPeripheral peripheral, CBService service, NSError? error)
        {
            var chars = service.Characteristics;
            s.CharsReady(
                chars?.FirstOrDefault(c => c.UUID == s._rx),
                chars?.FirstOrDefault(c => c.UUID == s._tx));
        }

        public override void UpdatedCharacterteristicValue(CBPeripheral peripheral,
            CBCharacteristic characteristic, NSError? error)
        {
            if (characteristic.UUID == s._tx && characteristic.Value is { } val)
                s.OnNotification(val.ToArray());
        }
    }
}

