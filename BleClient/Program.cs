using BleClient;
using CoreBluetooth;
using CoreFoundation;
using Foundation;
using System.Text;

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

await new BleShell(cts).RunAsync();

// ── Interactive BLE shell ─────────────────────────────────────────────────────
sealed class BleShell(CancellationTokenSource cts)
{
    private CBCentralManager _central = null!;
    private readonly TaskCompletionSource<bool> _btTcs = new();

    // Scan state
    private readonly List<CBPeripheral> _scanResults = new();
    private string?                               _activeScanTarget;
    private TaskCompletionSource<CBPeripheral>?   _activeScanTcs;

    // Connection state
    private CBPeripheral?      _peripheral;
    private CBCharacteristic?  _rxChar, _txChar;
    private bool               _connected;
    private TaskCompletionSource<bool>? _activeConTcs;
    private TaskCompletionSource<bool>? _activeCharTcs;

    // ── Entry point ───────────────────────────────────────────────────────────
    public async Task RunAsync()
    {
        _central = new CBCentralManager(new CentralDelegate(this), new DispatchQueue("ble", false));

        Console.WriteLine("Waiting for Bluetooth...");
        try   { await _btTcs.Task.WaitAsync(TimeSpan.FromSeconds(Constants.BtReadyTimeoutSeconds), cts.Token); }
        catch { Console.WriteLine("Bluetooth unavailable."); return; }
        Console.WriteLine("Bluetooth ready.\n");

        PrintHelp();

        while (!cts.IsCancellationRequested)
        {
            var prompt = _connected && _peripheral is not null
                ? $"[{_peripheral.Name ?? "device"}] > "
                : "> ";
            Console.Write(prompt);

            string? line;
            try   { line = await Task.Run(Console.ReadLine, cts.Token); }
            catch { break; }

            if (line is null) break;
            line = line.Trim();
            if (line.Length == 0) continue;

            var parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            var cmd   = parts[0].ToLowerInvariant();
            var arg   = parts.Length > 1 ? parts[1] : null;

            switch (cmd)
            {
                case "scan":
                    await ScanAsync(int.TryParse(arg, out var sec) ? sec : 5);
                    break;
                case "connect":
                    await ConnectAsync(arg);
                    break;
                case "disconnect":
                    Disconnect();
                    break;
                case "help":
                    PrintHelp();
                    break;
                case "exit":
                    Disconnect();
                    return;
                default:
                    if (_connected && _rxChar is not null && _peripheral is not null)
                        _peripheral.WriteValue(
                            NSData.FromArray(Encoding.UTF8.GetBytes(line)),
                            _rxChar, CBCharacteristicWriteType.WithoutResponse);
                    else
                        Console.WriteLine("Not connected. Use 'scan' or 'connect [name]' first.");
                    break;
            }
        }

        Disconnect();
    }

    // ── scan [seconds] ────────────────────────────────────────────────────────
    private async Task ScanAsync(int seconds)
    {
        if (_connected) { Console.WriteLine("Disconnect first."); return; }

        _scanResults.Clear();
        _activeScanTcs  = null;
        _activeScanTarget = null;

        Console.WriteLine($"Scanning for {seconds}s...");
        _central.ScanForPeripherals(Array.Empty<CBUUID>());

        using var scanCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        scanCts.CancelAfter(TimeSpan.FromSeconds(seconds));
        try   { await Task.Delay(Timeout.Infinite, scanCts.Token); }
        catch (OperationCanceledException) { }

        _central.StopScan();

        if (_scanResults.Count == 0)
        {
            Console.WriteLine("No devices found.");
            return;
        }

        Console.WriteLine($"\nFound {_scanResults.Count} device(s):");
        for (int i = 0; i < _scanResults.Count; i++)
        {
            var p = _scanResults[i];
            Console.WriteLine($"  [{i + 1}]  {p.Name ?? "<unnamed>"}   ({p.Identifier})");
        }
        Console.WriteLine();
    }

    // ── connect [name|index] ──────────────────────────────────────────────────
    private async Task ConnectAsync(string? nameOrIndex)
    {
        if (_connected) { Console.WriteLine("Already connected. Use 'disconnect' first."); return; }

        CBPeripheral? target = null;

        // Connect by scan result index
        if (int.TryParse(nameOrIndex, out var idx) && idx >= 1 && idx <= _scanResults.Count)
        {
            target = _scanResults[idx - 1];
        }
        else
        {
            // Scan by name (filter to NUS service)
            var name = nameOrIndex ?? Constants.TargetName;
            Console.WriteLine($"Scanning for '{name}'...");

            var found = new TaskCompletionSource<CBPeripheral>(TaskCreationOptions.RunContinuationsAsynchronously);
            _activeScanTarget = name;
            _activeScanTcs    = found;

            _central.ScanForPeripherals(new[] { CBUUID.FromString(Constants.NusServiceUuid) });

            using var scanCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            scanCts.CancelAfter(TimeSpan.FromSeconds(Constants.ScanTimeoutSeconds));
            try   { target = await found.Task.WaitAsync(scanCts.Token); }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            { _central.StopScan(); _activeScanTcs = null; Console.WriteLine("Cancelled."); return; }
            catch (OperationCanceledException)
            { _central.StopScan(); _activeScanTcs = null; Console.WriteLine($"'{name}' not found."); return; }

            _central.StopScan();
            _activeScanTcs = null;
        }

        // Connect
        Console.WriteLine($"Connecting to '{target.Name}'...");
        _peripheral    = target;
        _activeConTcs  = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _central.ConnectPeripheral(target);

        using var conCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        conCts.CancelAfter(TimeSpan.FromSeconds(Constants.ConnectTimeoutSeconds));
        try   { await _activeConTcs.Task.WaitAsync(conCts.Token); }
        catch { _central.CancelPeripheralConnection(target); _peripheral = null; Console.WriteLine("Connection failed."); return; }

        // Discover service + characteristics
        _activeCharTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        target.Delegate = new PeripheralDelegate(this);
        target.DiscoverServices(new[] { CBUUID.FromString(Constants.NusServiceUuid) });

        using var discCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
        discCts.CancelAfter(TimeSpan.FromSeconds(Constants.ConnectTimeoutSeconds));
        try   { await _activeCharTcs.Task.WaitAsync(discCts.Token); }
        catch { Disconnect(); Console.WriteLine("Service discovery failed."); return; }

        if (_rxChar is null || _txChar is null)
        { Disconnect(); Console.WriteLine("NUS characteristics not found."); return; }

        target.SetNotifyValue(true, _txChar);
        _connected = true;
        Console.WriteLine($"Connected to '{target.Name}'. Commands are forwarded to device.\n");
    }

    // ── disconnect ────────────────────────────────────────────────────────────
    private void Disconnect()
    {
        if (_peripheral is not null)
        {
            if (_txChar is not null)
                try { _peripheral.SetNotifyValue(false, _txChar); } catch { }
            _central?.CancelPeripheralConnection(_peripheral);
        }
        _connected  = false;
        _peripheral = null;
        _rxChar     = null;
        _txChar     = null;
    }

    // ── help ──────────────────────────────────────────────────────────────────
    private static void PrintHelp() => Console.WriteLine("""
        Commands:
          scan [seconds]        Scan for nearby BLE devices (default: 5s)
          connect [name|index]  Connect by name or scan result index
          disconnect            Disconnect from current device
          help                  Show this help
          exit                  Quit

        When connected, any other input is forwarded to the device:
          wifi on/off   ble on/off   web on/off   status   restart
        """);

    // ── Delegate callbacks ────────────────────────────────────────────────────
    internal void BtReady() => _btTcs.TrySetResult(true);
    internal void BtFailed(string msg) => _btTcs.TrySetException(new Exception(msg));

    internal void OnPeripheralDiscovered(CBPeripheral p)
    {
        if (_activeScanTcs is not null && p.Name == _activeScanTarget)
        { _activeScanTcs.TrySetResult(p); return; }

        if (!_scanResults.Any(x => x.Identifier.ToString() == p.Identifier.ToString()))
            _scanResults.Add(p);
    }

    internal void OnConnected()      => _activeConTcs?.TrySetResult(true);
    internal void OnConnectFailed(string msg) => _activeConTcs?.TrySetException(new Exception(msg));

    internal void OnDisconnected(string? reason)
    {
        if (!_connected) return;
        Console.WriteLine($"\n[Disconnected: {reason ?? "remote"}]");
        _connected  = false;
        _peripheral = null;
        _rxChar     = null;
        _txChar     = null;
        Console.Write("> ");
    }

    internal void OnCharsReady(CBCharacteristic? rx, CBCharacteristic? tx)
    {
        _rxChar = rx; _txChar = tx;
        _activeCharTcs?.TrySetResult(true);
    }

    internal void OnNotification(byte[] value)
    {
        var msg    = Encoding.UTF8.GetString(value);
        var prompt = _connected && _peripheral is not null
            ? $"[{_peripheral.Name ?? "device"}] > " : "> ";
        Console.WriteLine($"\n[device] {msg}");
        Console.Write(prompt);
    }

    // ── CBCentralManagerDelegate ──────────────────────────────────────────────
    private sealed class CentralDelegate(BleShell s) : CBCentralManagerDelegate
    {
        public override void UpdatedState(CBCentralManager central)
        {
            if (central.State == CBManagerState.PoweredOn) s.BtReady();
            else s.BtFailed($"Bluetooth state: {central.State}");
        }

        public override void DiscoveredPeripheral(CBCentralManager central, CBPeripheral peripheral,
            NSDictionary advertisementData, NSNumber rssi)
            => s.OnPeripheralDiscovered(peripheral);

        public override void ConnectedPeripheral(CBCentralManager central, CBPeripheral peripheral)
            => s.OnConnected();

        public override void FailedToConnectPeripheral(CBCentralManager central, CBPeripheral peripheral, NSError? error)
            => s.OnConnectFailed(error?.LocalizedDescription ?? "unknown error");

        public override void DisconnectedPeripheral(CBCentralManager central, CBPeripheral peripheral, NSError? error)
            => s.OnDisconnected(error?.LocalizedDescription);
    }

    // ── CBPeripheralDelegate ──────────────────────────────────────────────────
    private sealed class PeripheralDelegate(BleShell s) : CBPeripheralDelegate
    {
        private static CBUUID SvcUuid => CBUUID.FromString(Constants.NusServiceUuid);
        private static CBUUID RxUuid  => CBUUID.FromString(Constants.NusRxUuid);
        private static CBUUID TxUuid  => CBUUID.FromString(Constants.NusTxUuid);

        public override void DiscoveredService(CBPeripheral peripheral, NSError? error)
        {
            if (error is not null) { s.OnCharsReady(null, null); return; }
            var svc = peripheral.Services?.FirstOrDefault(sv => sv.UUID == SvcUuid);
            if (svc is null) { s.OnCharsReady(null, null); return; }
            peripheral.DiscoverCharacteristics(new[] { RxUuid, TxUuid }, svc);
        }

        public override void DiscoveredCharacteristics(CBPeripheral peripheral, CBService service, NSError? error)
        {
            var chars = service.Characteristics;
            s.OnCharsReady(
                chars?.FirstOrDefault(c => c.UUID == RxUuid),
                chars?.FirstOrDefault(c => c.UUID == TxUuid));
        }

        public override void UpdatedCharacterteristicValue(CBPeripheral peripheral,
            CBCharacteristic characteristic, NSError? error)
        {
            if (characteristic.UUID == TxUuid && characteristic.Value is { } val)
                s.OnNotification(val.ToArray());
        }
    }
}
