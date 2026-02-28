using SharpDX.DirectInput;
using System.Diagnostics;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace BRMascon;

class Program
{
    static async Task Main(string[] args)
    {
        Console.Title = "BR Mascon Adapter for Roblox";
        
        try
        {
            var adapter = new MasconAdapter();
            await adapter.RunAsync();
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"\n[FATAL ERROR] {ex.Message}");
            Console.ResetColor();
            Console.WriteLine("\nPress any key to exit...");
            Console.ReadKey();
        }
    }
}

// Enumerations
enum ControlMode
{
    CombinedHandle,  // W/S for power and brake
    TwoHandle        // W/S for power, A/D for brake
}

enum HandleState
{
    EmergencyBrake,
    Brake,
    Neutral,
    Power
}

enum PowerNotchConfig
{
    Class800,    // 4 Power Notches
    Class230,    // 6 Power Notches
    Class90,     // 7 Power Notches
    Class153,    // 7 Power Notches (notched brakes)
    Class231,    // 5 Power Positions (unnotched power/brake)
    Class745,    // 5 Power Positions (unnotched power/brake)
    Class755,    // 5 Power Positions (unnotched power/brake)
    Class756     // 5 Power Positions (unnotched power/brake)
}

// Handle Position Structure
struct HandlePosition
{
    public HandleState State { get; set; }
    public int Notch { get; set; }
    public int RawValue { get; set; }

    public override string ToString()
    {
        return State switch
        {
            HandleState.EmergencyBrake => "EB",
            HandleState.Brake => $"B{Notch}",
            HandleState.Neutral => "N",
            HandleState.Power => $"P{Notch}",
            _ => "UNKNOWN"
        };
    }
}

// Device Information Structure
class DeviceInfo
{
    public Guid InstanceGuid { get; set; }
    public string ProductName { get; set; } = "";
}

// Main Adapter Class
class MasconAdapter
{
    // Virtual Xbox 360 Controller
    private ViGEmClient? _vigemClient;
    private IXbox360Controller? _controller;
    
    private Joystick? _joystick;
    private HandlePosition _currentPosition;
    private HandlePosition _lastPosition;
    private ControlMode _controlMode = ControlMode.CombinedHandle;
    private PowerNotchConfig _powerNotches = PowerNotchConfig.Class800;
    private int _brakeNotches = 8;
    private bool _notchedBrakes = false;
    private bool _notchedPower = true;
    private bool _isRunning;
    private bool _ebActive;
    private DateTime _lastUpdateTime = DateTime.Now;
    private bool _showConfigMenu = false;
    private readonly object _configLock = new();
    private List<DeviceInfo> _availableDevices = new();
    private readonly Queue<QueuedAction> _actionQueue = new();
    private readonly object _actionLock = new();
    private DateTime _nextActionTime = DateTime.MinValue;

    private const int UnnotchedPowerFullRangeMs = 950; // it is actually 1200ms from 0 power to full power on 231, but there is delay in the response so 950ms is closer to the feel of the notched configs

    private const int UnnotchedBrakeFullRangeMs = 755;// it is actually 1000ms from 0 brake to full brake on majority of configs, but again there is delay in the response so 755ms is closer to the feel of the notched configs
    
    // Button states for tracking
    private bool _zrPressed = false;
    private bool _rPressed = false;
    private bool _zlPressed = false;
    private bool _lPressed = false;
    private bool _rStickPressed = false;
    private bool _button3Pressed = false;
    private bool _button4Pressed = false;
    private bool _dpadUp = false;
    private bool _dpadRight = false;
    private bool _dpadDown = false;
    private bool _dpadLeft = false;
    private int _powerCandidateNotch = -1;
    private DateTime _powerCandidateSince = DateTime.MinValue;
    private int _commandedPowerNotch = 0;
    private int _commandedBrakeNotch = 0;
    private int _pendingPowerTarget = -1;
    private bool _waitingForBrakeRelease = false;

    private struct QueuedAction
    {
        public string Button { get; set; }
        public bool Press { get; set; }
        public int DelayMsAfter { get; set; }
    }

    // Zuiki Mascon Detection Points (from ZUIKI_to_JRE reference)
    private static readonly (int Threshold, HandleState State, int Notch)[] DetectionPoints =
    {
        (640, HandleState.EmergencyBrake, 9),
        (3072, HandleState.Brake, 8),
        (6528, HandleState.Brake, 7),
        (9984, HandleState.Brake, 6),
        (13568, HandleState.Brake, 5),
        (17023, HandleState.Brake, 4),
        (20479, HandleState.Brake, 3),
        (24063, HandleState.Brake, 2),
        (29311, HandleState.Brake, 1),
        (36668, HandleState.Neutral, 0),
        (43690, HandleState.Power, 1),
        (49801, HandleState.Power, 2),
        (55913, HandleState.Power, 3),
        (62284, HandleState.Power, 4),
        (65535, HandleState.Power, 5),
    };

    public async Task RunAsync()
    {
        ShowWelcomeBanner();
        
        if (!ScanDevices())
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("\n[ERROR] No joystick devices found!");
            Console.ResetColor();
            Console.WriteLine("\nPress any key to exit...");
            Console.ReadKey();
            return;
        }

        if (!SelectDevice())
        {
            Console.WriteLine("\nPress any key to exit...");
            Console.ReadKey();
            return;
        }

        ConfigureTrainSettings(isInitialSetup: true);
        
        if (!InitializeVirtualController())
        {
            Console.WriteLine("\nPress any key to exit...");
            Console.ReadKey();
            return;
        }
        
        Console.Clear();
        ShowDashboardHeader();
        
        _isRunning = true;
        
        // Setup cleanup on Ctrl+C
        Console.CancelKeyPress += (sender, e) =>
        {
            e.Cancel = true;
            _isRunning = false;
        };

        // Main loop
        var updateTask = Task.Run(UpdateLoop);
        var uiTask = Task.Run(UILoop);
        var inputTask = Task.Run(InputLoop);

        await Task.WhenAll(updateTask, uiTask, inputTask);

        // Cleanup
        ReleaseAllButtons();
        _controller?.Disconnect();
        _vigemClient?.Dispose();
        _joystick?.Unacquire();
        _joystick?.Dispose();
        
        Console.Clear();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("\n[SHUTDOWN] Virtual controller disconnected. Mascon disconnected safely.");
        Console.ResetColor();
    }

    private void ShowWelcomeBanner()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("╔════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║          BR Mascon Adapter for Roblox                      ║");
        Console.WriteLine("║          Zuiki One-Handle Mascon Interface                 ║");
        Console.WriteLine("╚════════════════════════════════════════════════════════════╝");
        Console.ResetColor();
        Console.WriteLine();
    }

    private bool ScanDevices()
    {
        Console.WriteLine("[SCAN] Detecting DirectInput devices...");
        Console.WriteLine("─────────────────────────────────────────────────────────────");
        
        _availableDevices.Clear();
        var directInput = new DirectInput();
        var devices = directInput.GetDevices();
        
        int index = 1;
        foreach (var device in devices)
        {
            var deviceInfo = new DeviceInfo
            {
                InstanceGuid = device.InstanceGuid,
                ProductName = device.ProductName
            };
            
            _availableDevices.Add(deviceInfo);
            
            // Check if this is the Zuiki Mascon
            bool isZuikiMascon = device.ProductName.Contains("33DD") || 
                                 device.ProductName.Contains("0001") ||
                                 device.ProductName.ToLower().Contains("mascon");
            
            if (isZuikiMascon)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.Write($"  [{index}] {device.ProductName}");
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine(" ★ (Zuiki Mascon Detected)");
                Console.ResetColor();
            }
            else
            {
                Console.WriteLine($"  [{index}] {device.ProductName}");
            }
            
            index++;
        }
        
        if (_availableDevices.Count == 0)
        {
            Console.WriteLine("\n  No devices found.");
            return false;
        }
        
        Console.WriteLine($"\n[INFO] Found {_availableDevices.Count} device(s)");
        return true;
    }

    private bool SelectDevice()
    {
        Console.WriteLine("\n[SELECT] Choose your device:");
        Console.Write($"Enter device number [1-{_availableDevices.Count}]: ");
        
        string? input = Console.ReadLine();
        
        if (string.IsNullOrEmpty(input))
        {
            input = _availableDevices.Count.ToString();
            foreach (var device in _availableDevices)
            {
                if (device.ProductName.Contains("33DD") || 
                    device.ProductName.Contains("0001") ||
                    device.ProductName.ToLower().Contains("mascon"))
                {
                    input = (_availableDevices.IndexOf(device) + 1).ToString();
                    break;
                }
            }
        }
        
        if (int.TryParse(input, out int selection) && 
            selection >= 1 && 
            selection <= _availableDevices.Count)
        {
            var selectedDevice = _availableDevices[selection - 1];
            
            try
            {
                var directInput = new DirectInput();
                _joystick = new Joystick(directInput, selectedDevice.InstanceGuid);
                _joystick.Acquire();
                
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"\n[CONNECTED] {selectedDevice.ProductName}");
                Console.ResetColor();
                
                var state = _joystick.GetCurrentState();
                Console.WriteLine($"[TEST] Y-Axis reading: {state.Y}");
                
                return true;
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"\n[ERROR] Failed to connect to device: {ex.Message}");
                Console.ResetColor();
                return false;
            }
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("\n[ERROR] Invalid selection!");
            Console.ResetColor();
            return false;
        }
    }

    private void ConfigureTrainSettings(bool isInitialSetup = true)
    {
        lock (_configLock)
        {
            if (!isInitialSetup)
            {
                _showConfigMenu = true;
            }
        }

        if (!isInitialSetup)
        {
            Console.Clear();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("╔════════════════════════════════════════════════════════════╗");
            Console.WriteLine("║               TRAIN CONFIGURATION MENU                     ║");
            Console.WriteLine("╚════════════════════════════════════════════════════════════╝");
            Console.ResetColor();
            Console.WriteLine();
            Console.WriteLine("Current Setup:");
            Console.WriteLine($"  Class: {GetConfigName(_powerNotches)}");
            Console.WriteLine($"  Control Mode: {_controlMode}");
            Console.WriteLine();
        }
        else
        {
            Console.WriteLine("\n[SETUP] Configuration");
            Console.WriteLine("─────────────────────────────────────────────────────────────");
        }

        Console.WriteLine("Select Control Mode:");
        Console.WriteLine("  [1] Combined Handle (W/S for Power & Brake)");
        Console.WriteLine("  [2] Two-Handle (W/S for Power, A/D for Brake)");
        if (!isInitialSetup)
        {
            Console.WriteLine("  [0] Keep current");
        }
        Console.Write("\nEnter choice: ");

        string? modeInput = Console.ReadLine();
        ControlMode newControlMode = _controlMode;
        if (modeInput == "1")
        {
            newControlMode = ControlMode.CombinedHandle;
        }
        else if (modeInput == "2")
        {
            newControlMode = ControlMode.TwoHandle;
        }

        Console.WriteLine("\nSelect Train Class:");
        Console.WriteLine("  [1] Class 800 (4 power notches)");
        Console.WriteLine("  [2] Class 230 (6 power notches)");
        Console.WriteLine("  [3] Class 90  (7 power notches)");
        Console.WriteLine("  [4] Class 142/153 (P7 / B3 notched)");
        Console.WriteLine("  [5] Class 231/745/755/756 (unnotched power/brake)");
        Console.Write("\nEnter choice: ");

        string? input = Console.ReadLine();

        if (!isInitialSetup && input == "0")
        {
            lock (_configLock)
            {
                _showConfigMenu = false;
            }
            RestoreDashboard();
            return;
        }

        var (newConfig, newNotchedBrakes, newNotchedPower) = ParseTrainClass(input);

        ApplyConfigChange(newConfig, newNotchedBrakes, newNotchedPower, newControlMode);

        if (!isInitialSetup)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"\n[SUCCESS] Configuration changed to {GetConfigName(newConfig)}");
            Console.ResetColor();
            Console.WriteLine("Press any key to continue...");
            Console.ReadKey();

            lock (_configLock)
            {
                _showConfigMenu = false;
            }
            RestoreDashboard();
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"\n[CONFIG] Mode: {newControlMode} | Power Notches: {GetPowerNotchCount()} | Power: {(newNotchedPower ? "Notched" : "Unnotched")}");
            Console.ResetColor();
        }
    }

    private (PowerNotchConfig, bool notchedBrakes, bool notchedPower) ParseTrainClass(string? input)
    {
        return input switch
        {
            "1" => (PowerNotchConfig.Class800, false, true),
            "2" => (PowerNotchConfig.Class230, false, true),
            "3" => (PowerNotchConfig.Class90, false, true),
            "4" => (PowerNotchConfig.Class153, true, true),
            "5" => (PowerNotchConfig.Class231, false, false),
            _ => (_powerNotches, _notchedBrakes, _notchedPower) // keep current
        };
    }
    
    private bool InitializeVirtualController()
    {
        Console.WriteLine("\n[VIGEM] Initializing Virtual Xbox 360 Controller...");
        Console.WriteLine("─────────────────────────────────────────────────────────────");
        
        try
        {
            _vigemClient = new ViGEmClient();
            _controller = _vigemClient.CreateXbox360Controller();
            _controller.Connect();
            
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("[SUCCESS] Virtual Xbox 360 Controller created!");
            Console.WriteLine("           Roblox should detect: 'Xbox 360 Controller'");
            Console.ResetColor();
            Console.WriteLine("\n[MAPPING] Controller Button Layout:");
            Console.WriteLine("  • ZR (RT - Right Trigger)  → Throttle Up   (W)");
            Console.WriteLine("  • R  (RB - Right Bumper)   → Throttle Down (S)");
            Console.WriteLine("  • ZL (LT - Left Trigger)   → Brake Apply   (A)");
            Console.WriteLine("  • L  (LB - Left Bumper)    → Brake Release (D)");
            Console.WriteLine("  • R3 (Right Stick Click)   → Emergency Brake (1s hold)");
            Console.WriteLine("  • Button 3                → B Button");
            Console.WriteLine("  • Button 4                → X Button");
            Console.WriteLine("  • D-Pad                   → D-Pad (Up/Down/Left/Right)");
            Console.WriteLine("\nPress any key to start...");
            Console.ReadKey();
            
            return true;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[ERROR] Failed to initialize ViGEm: {ex.Message}");
            Console.WriteLine("\n[SOLUTION] Install ViGEmBus driver:");
            Console.WriteLine("           https://github.com/ViGEm/ViGEmBus/releases");
            Console.WriteLine("           Download and run 'ViGEmBus_Setup_x64.msi'");
            Console.ResetColor();
            return false;
        }
    }

    private void ShowDashboardHeader()
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("╔════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║                    LIVE DASHBOARD                          ║");
        Console.WriteLine("╚════════════════════════════════════════════════════════════╝");
        Console.ResetColor();
        Console.WriteLine($"\nMode: {_controlMode} | Power Notches: {GetPowerNotchCount()}");
        Console.WriteLine("Press Ctrl+C to stop\n");
        Console.WriteLine("─────────────────────────────────────────────────────────────");
    }

    private async Task UpdateLoop()
    {
        while (_isRunning)
        {
            try
            {
                if (IsConfigMenuActive())
                {
                    await Task.Delay(50);
                    continue;
                }

                if (_joystick == null) break;
                
                var state = _joystick.GetCurrentState();
                var newPosition = DetectPosition(state.Y);
                newPosition = FilterPowerNotch(newPosition);
                
                if (newPosition.State != _currentPosition.State || newPosition.Notch != _currentPosition.Notch)
                {
                    _lastPosition = _currentPosition;
                    _currentPosition = newPosition;
                    _lastUpdateTime = DateTime.Now;
                    
                    ProcessStateChange();
                }
                
                ApplyAuxButtonMappings(state);
                ProcessActionQueue();
                CheckPendingPowerTarget();
                
                await Task.Delay(16); // ~60Hz polling
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\n[ERROR] Update loop: {ex.Message}");
                _isRunning = false;
            }
        }
    }

    private void ApplyAuxButtonMappings(JoystickState state)
    {
        if (_controller == null) return;

        bool changed = false;
        var buttons = state.Buttons;
        bool button3 = buttons != null && buttons.Length > 2 && buttons[2];
        bool button4 = buttons != null && buttons.Length > 3 && buttons[3];

        if (button3 != _button3Pressed)
        {
            _controller.SetButtonState(Xbox360Button.B, button3);
            _button3Pressed = button3;
            changed = true;
        }

        if (button4 != _button4Pressed)
        {
            _controller.SetButtonState(Xbox360Button.X, button4);
            _button4Pressed = button4;
            changed = true;
        }

        int angle = -1;
        var pov = state.PointOfViewControllers;
        if (pov != null && pov.Length > 0)
        {
            angle = pov[0];
        }

        bool up = false;
        bool right = false;
        bool down = false;
        bool left = false;

        if (angle >= 0)
        {
            switch (angle)
            {
                case 0:
                    up = true;
                    break;
                case 4500:
                    up = true;
                    right = true;
                    break;
                case 9000:
                    right = true;
                    break;
                case 13500:
                    right = true;
                    down = true;
                    break;
                case 18000:
                    down = true;
                    break;
                case 22500:
                    down = true;
                    left = true;
                    break;
                case 27000:
                    left = true;
                    break;
                case 31500:
                    up = true;
                    left = true;
                    break;
            }
        }

        if (up != _dpadUp)
        {
            _controller.SetButtonState(Xbox360Button.Up, up);
            _dpadUp = up;
            changed = true;
        }

        if (right != _dpadRight)
        {
            _controller.SetButtonState(Xbox360Button.Right, right);
            _dpadRight = right;
            changed = true;
        }

        if (down != _dpadDown)
        {
            _controller.SetButtonState(Xbox360Button.Down, down);
            _dpadDown = down;
            changed = true;
        }

        if (left != _dpadLeft)
        {
            _controller.SetButtonState(Xbox360Button.Left, left);
            _dpadLeft = left;
            changed = true;
        }

        if (changed)
        {
            _controller.SubmitReport();
        }
    }

    private HandlePosition DetectPosition(int yAxisValue)
    {
        foreach (var (threshold, state, notch) in DetectionPoints)
        {
            if (yAxisValue < threshold)
            {
                return new HandlePosition
                {
                    State = state,
                    Notch = notch,
                    RawValue = yAxisValue
                };
            }
        }
        
        return new HandlePosition
        {
            State = HandleState.Power,
            Notch = 5,
            RawValue = yAxisValue
        };
    }

    private HandlePosition FilterPowerNotch(HandlePosition newPosition)
    {
        if (newPosition.State != HandleState.Power || _powerNotches != PowerNotchConfig.Class153)
        {
            _powerCandidateNotch = -1;
            return newPosition;
        }

        if (_currentPosition.State != HandleState.Power)
        {
            _powerCandidateNotch = -1;
            return newPosition;
        }

        if (newPosition.Notch == _currentPosition.Notch)
        {
            _powerCandidateNotch = -1;
            return newPosition;
        }

        if (_powerCandidateNotch != newPosition.Notch)
        {
            _powerCandidateNotch = newPosition.Notch;
            _powerCandidateSince = DateTime.Now;
            return _currentPosition;
        }

        if ((DateTime.Now - _powerCandidateSince).TotalMilliseconds < 120)
        {
            return _currentPosition;
        }

        _powerCandidateNotch = -1;
        return newPosition;
    }

    private void ProcessStateChange()
    {
        // Emergency Brake handling
        if (_currentPosition.State == HandleState.EmergencyBrake)
        {
            if (!_ebActive)
            {
                // Don't clear queue - let queued actions continue to process while EB is held
                _commandedPowerNotch = 0;
                _commandedBrakeNotch = _brakeNotches;
                // PressButton("ZL");
                // PressButton("ZR");
                PressButton("RStick");
                _ebActive = true;
            }
            return;
        }
        else if (_ebActive)
        {
            ClearActionQueue();
            // ReleaseButton("ZL");
            // ReleaseButton("ZR");
            ReleaseButton("RStick");
            // EnqueueHold("ZR", 200, 0);
            _ebActive = false;
            _commandedBrakeNotch = 0;
            _commandedPowerNotch = 0;
        }

        int targetPowerNotch = 0;
        int targetBrakeNotch = 0;

        if (_currentPosition.State == HandleState.Power)
        {
            targetPowerNotch = GetScaledPowerNotch(_currentPosition.Notch);
        }
        else if (_currentPosition.State == HandleState.Brake)
        {
            targetBrakeNotch = _notchedBrakes
                ? GetScaledBrakeNotch(_currentPosition.Notch)
                : _currentPosition.Notch;
        }

        // When transitioning to power, ensure all previous actions (like brake release) are finished
        if (_currentPosition.State == HandleState.Power)
        {
            ApplyBrakeTarget(targetBrakeNotch); // Should be 0, ensures all brake release actions are queued

            bool queueHasItems;
            lock (_actionLock)
            {
                queueHasItems = _actionQueue.Count > 0;
            }
            
            // Once we detect power is requested, keep power pending until brakes are fully released
            // Update the pending target to the latest requested power notch
            if (queueHasItems || DateTime.Now < _nextActionTime || _waitingForBrakeRelease)
            {
                _pendingPowerTarget = targetPowerNotch;
                _waitingForBrakeRelease = true;
            }
            else
            {
                // Queue is empty AND no timing delays, safe to apply power
                ApplyPowerTarget(targetPowerNotch);
                _pendingPowerTarget = -1;
                _waitingForBrakeRelease = false;
            }
        }
        else
        {
            // If we move back to Neutral or Brake while waiting for power, cancel the pending power
            if (_waitingForBrakeRelease)
            {
                _pendingPowerTarget = -1;
                _waitingForBrakeRelease = false;
            }
            
            ApplyPowerTarget(targetPowerNotch); // Should be 0
            ApplyBrakeTarget(targetBrakeNotch);
        }
    }

    private void ApplyPowerTarget(int targetNotch)
    {
        int powerNotches = GetPowerNotchCount();
        targetNotch = Math.Clamp(targetNotch, 0, powerNotches);
        int delta = targetNotch - _commandedPowerNotch;

        if (delta > 0)
        {
            if (_notchedPower)
            {
                for (int i = 0; i < delta; i++)
                {
                    EnqueueTap("ZR", 50, 50);
                }
            }
            else
            {
                int holdMs = (int)((Math.Abs(delta) / (double)Math.Max(1, powerNotches)) * UnnotchedPowerFullRangeMs);
                EnqueueHold("ZR", Math.Max(1, holdMs), 100);
            }
        }
        else if (delta < 0)
        {
            if (_notchedPower)
            {
                for (int i = 0; i < Math.Abs(delta); i++)
                {
                    EnqueueTap("R", 50, 50);
                }
            }
            else
            {
                int holdMs = (int)((Math.Abs(delta) / (double)Math.Max(1, powerNotches)) * UnnotchedPowerFullRangeMs);
                EnqueueHold("R", Math.Max(1, holdMs), 0);
            }
        }

        _commandedPowerNotch = targetNotch;
    }

    private void ApplyBrakeTarget(int targetNotch)
    {
        int maxBrake = _notchedBrakes ? _brakeNotches : 8;
        targetNotch = Math.Clamp(targetNotch, 0, maxBrake);
        int delta = targetNotch - _commandedBrakeNotch;

        if (delta > 0)
        {
            var button = _controlMode == ControlMode.CombinedHandle ? "R" : "ZL";
            if (_notchedBrakes)
            {
                for (int i = 0; i < delta; i++)
                {
                    EnqueueTap(button, 50, 50);
                }
            }
            else
            {
                var holdMs = (int)((Math.Abs(delta) / 8.0) * UnnotchedBrakeFullRangeMs);
                EnqueueHold(button, holdMs, 100);
            }
        }
        else if (delta < 0)
        {
            var button = _controlMode == ControlMode.CombinedHandle ? "ZR" : "L";
            if (_notchedBrakes)
            {
                for (int i = 0; i < Math.Abs(delta); i++)
                {
                    EnqueueTap(button, 50, 50);
                }
            }
            else
            {
                var holdMs = (int)((Math.Abs(delta) / 8.0) * UnnotchedBrakeFullRangeMs);
                EnqueueHold(button, holdMs, 0);
            }
        }

        _commandedBrakeNotch = targetNotch;
    }

    private int GetScaledPowerNotch(int rawNotch)
    {
        if (rawNotch <= 0) return 0;

        int maxNotches = GetPowerNotchCount();
        int cappedRaw = Math.Min(rawNotch, 5);
        if (maxNotches <= 5)
        {
            return Math.Min(cappedRaw, maxNotches);
        }

        double scaled = cappedRaw * (maxNotches / 5.0);
        int result = (int)Math.Round(scaled, MidpointRounding.AwayFromZero);
        return Math.Clamp(result, 0, maxNotches);
    }

    private int GetScaledBrakeNotch(int rawNotch)
    {
        if (rawNotch <= 0) return 0;

        int cappedRaw = Math.Min(rawNotch, 8);
        double scaled = cappedRaw * (_brakeNotches / 8.0);
        int result = (int)Math.Round(scaled, MidpointRounding.AwayFromZero);
        return Math.Clamp(result, 0, _brakeNotches);
    }

    private void EnqueueTap(string button, int pressMs, int gapMs)
    {
        EnqueueAction(button, true, pressMs);
        EnqueueAction(button, false, gapMs);
    }

    private void EnqueueHold(string button, int holdMs, int gapMs)
    {
        EnqueueAction(button, true, holdMs);
        EnqueueAction(button, false, gapMs);
    }

    private void EnqueueAction(string button, bool press, int delayMsAfter)
    {
        lock (_actionLock)
        {
            _actionQueue.Enqueue(new QueuedAction
            {
                Button = button,
                Press = press,
                DelayMsAfter = delayMsAfter
            });
        }
    }

    private void ClearActionQueue()
    {
        lock (_actionLock)
        {
            _actionQueue.Clear();
            _nextActionTime = DateTime.MinValue;
        }
    }

    private void CheckPendingPowerTarget()
    {
        // After brakes are fully released, apply the pending power target
        if (_waitingForBrakeRelease)
        {
            // If the handle is no longer in Power, cancel the pending power
            if (_currentPosition.State != HandleState.Power)
            {
                _pendingPowerTarget = -1;
                _waitingForBrakeRelease = false;
                return;
            }

            bool queueEmpty;
            lock (_actionLock)
            {
                queueEmpty = _actionQueue.Count == 0;
            }
            
            if (queueEmpty && DateTime.Now >= _nextActionTime)
            {
                ApplyPowerTarget(_pendingPowerTarget);
                _pendingPowerTarget = -1;
                _waitingForBrakeRelease = false;
            }
        }
    }

    private void ProcessActionQueue()
    {
        if (_controller == null) return;
        int processed = 0;

        while (processed < 6 && DateTime.Now >= _nextActionTime)
        {
            QueuedAction action;
            lock (_actionLock)
            {
                if (_actionQueue.Count == 0) return;
                action = _actionQueue.Dequeue();
            }

            if (action.Press)
            {
                PressButton(action.Button);
            }
            else
            {
                ReleaseButton(action.Button);
            }

            _nextActionTime = DateTime.Now.AddMilliseconds(Math.Max(0, action.DelayMsAfter));
            processed++;
        }
    }

    private async Task UILoop()
    {
        int lastCursorTop = Console.CursorTop;
        
        while (_isRunning)
        {
            try
            {
                if (IsConfigMenuActive())
                {
                    await Task.Delay(50);
                    continue;
                }

                Console.SetCursorPosition(0, lastCursorTop);
                
                Console.ForegroundColor = GetStateColor(_currentPosition.State);
                Console.Write($"Handle Position: {_currentPosition,-6}");
                Console.ResetColor();
                Console.WriteLine($"  Raw: {_currentPosition.RawValue,5}   ");
                
                if (_joystick != null)
                {
                    var state = _joystick.GetCurrentState();
                    Console.WriteLine($"Y-Axis Value:    {state.Y,5}                    ");
                }
                
                Console.Write("Active Buttons:  ");
                if (_ebActive)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.Write("[EB Engaged] ");
                }
                if (_zrPressed)
                {
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.Write("[ZR] ");
                }
                if (_rPressed)
                {
                    Console.ForegroundColor = ConsoleColor.Yellow;
                    Console.Write("[R] ");
                }
                if (_zlPressed)
                {
                    Console.ForegroundColor = ConsoleColor.Magenta;
                    Console.Write("[ZL] ");
                }
                if (_lPressed)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.Write("[L] ");
                }
                if (_rStickPressed)
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.Write("[R3] ");
                }
                Console.ResetColor();
                Console.WriteLine("                ");
                
                Console.WriteLine($"Last Update:     {_lastUpdateTime:HH:mm:ss.fff}          ");
                
                Console.Write("Controller:      ");
                if (_controller != null)
                {
                    Console.ForegroundColor = ConsoleColor.Green;
                    Console.WriteLine("✓ Xbox 360 Virtual Controller     ");
                }
                else
                {
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine("✗ Disconnected                    ");
                }
                Console.ResetColor();
                
                Console.WriteLine("─────────────────────────────────────────────────────────────");
                
                await Task.Delay(50);
            }
            catch
            {
                // Ignore UI errors
            }
        }
    }

    private async Task InputLoop()
    {
        while (_isRunning)
        {
            try
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(true);
                    if (key.Key == ConsoleKey.Escape)
                    {
                        ConfigureTrainSettings(isInitialSetup: false);
                    }
                }
                await Task.Delay(50);
            }
            catch
            {
                // Ignore input errors
            }
        }
    }

    private bool IsConfigMenuActive()
    {
        lock (_configLock)
        {
            return _showConfigMenu;
        }
    }



    private void ApplyConfigChange(PowerNotchConfig newConfig, bool newNotchedBrakes, bool newNotchedPower, ControlMode newControlMode)
    {
        lock (_configLock)
        {
            _powerNotches = newConfig;
            _notchedBrakes = newNotchedBrakes;
            _notchedPower = newNotchedPower;
            _brakeNotches = newNotchedBrakes ? 3 : 8;
            _controlMode = newControlMode;

            ClearActionQueue();
            _commandedPowerNotch = 0;
            _commandedBrakeNotch = 0;
        }
    }

    private string GetConfigName(PowerNotchConfig config)
    {
        return config switch
        {
            PowerNotchConfig.Class800 => "Class 800",
            PowerNotchConfig.Class230 => "Class 230",
            PowerNotchConfig.Class90 => "Class 90",
            PowerNotchConfig.Class153 => "Class 142/153",
            PowerNotchConfig.Class231 => "Class 231/745/755/756",

            _ => "Unknown"
        };
    }

    private int GetPowerNotchCount()
    {
        return _powerNotches switch
        {
            PowerNotchConfig.Class800 => 4,
            PowerNotchConfig.Class230 => 6,
            PowerNotchConfig.Class90 => 7,
            PowerNotchConfig.Class153 => 7,
            PowerNotchConfig.Class231 => 5,
            _ => 5
        };
    }

    private void RestoreDashboard()
    {
        Console.Clear();
        ShowDashboardHeader();
    }

    private ConsoleColor GetStateColor(HandleState state)
    {
        return state switch
        {
            HandleState.EmergencyBrake => ConsoleColor.Red,
            HandleState.Brake => ConsoleColor.Yellow,
            HandleState.Neutral => ConsoleColor.Green,
            HandleState.Power => ConsoleColor.Cyan,
            _ => ConsoleColor.White
        };
    }

    private void ReleaseAllButtons()
    {
        ReleaseButton("ZR");
        ReleaseButton("R");
        ReleaseButton("ZL");
        ReleaseButton("L");
        ReleaseButton("RStick");
    }
    
    private void PressButton(string button)
    {
        if (_controller == null) return;
        
        switch (button)
        {
            case "ZR": // Right Trigger - slider only
                _controller.SetSliderValue(Xbox360Slider.RightTrigger, 255);
                _zrPressed = true;
                break;
            case "R": // Right Bumper - button only
                _controller.SetButtonState(Xbox360Button.RightShoulder, true);
                _rPressed = true;
                break;
            case "ZL": // Left Trigger - slider only
                _controller.SetSliderValue(Xbox360Slider.LeftTrigger, 255);
                _zlPressed = true;
                break;
            case "L": // Left Bumper - button only
                _controller.SetButtonState(Xbox360Button.LeftShoulder, true);
                _lPressed = true;
                break;
            case "RStick": // Right Stick Click (R3)
                _controller.SetButtonState(Xbox360Button.RightThumb, true);
                _rStickPressed = true;
                break;
        }
        
        _controller.SubmitReport();
    }
    
    private void ReleaseButton(string button)
    {
        if (_controller == null) return;
        
        switch (button)
        {
            case "ZR": // Right Trigger - slider only
                _controller.SetSliderValue(Xbox360Slider.RightTrigger, 0);
                _zrPressed = false;
                break;
            case "R": // Right Bumper - button only
                _controller.SetButtonState(Xbox360Button.RightShoulder, false);
                _rPressed = false;
                break;
            case "ZL": // Left Trigger - slider only
                _controller.SetSliderValue(Xbox360Slider.LeftTrigger, 0);
                _zlPressed = false;
                break;
            case "L": // Left Bumper - button only
                _controller.SetButtonState(Xbox360Button.LeftShoulder, false);
                _lPressed = false;
                break;
            case "RStick": // Right Stick Click (R3)
                _controller.SetButtonState(Xbox360Button.RightThumb, false);
                _rStickPressed = false;
                break;
        }
        
        _controller.SubmitReport();
    }
}
