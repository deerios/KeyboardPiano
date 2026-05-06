using Serilog;
using SharpDX.DirectInput;

namespace KeyboardPiano;

public sealed class PedalMonitor : IDisposable
{
	private static readonly ILogger _log = Log.ForContext<PedalMonitor>();

	public event EventHandler? PedalDown;
	public event EventHandler? PedalUp;

	private readonly Joystick _joystick;
	private readonly Func<JoystickState, int> _axisReader;
	private readonly int _threshold;
	private readonly bool _inverted;

	private PedalMonitor(Joystick joystick, Func<JoystickState, int> axisReader, int restValue, int thresholdPct, bool inverted)
	{
		_joystick   = joystick;
		_axisReader = axisReader;
		_inverted   = inverted;

		// Threshold is a fraction of the travel from rest to the extreme end.
		// inverted=true : pedal travels rest->0,      fire when value drops below rest by thresholdPct% of rest
		// inverted=false: pedal travels rest->65535,  fire when value rises above rest by thresholdPct% of remaining range
		double pct = thresholdPct / 100.0;
		_threshold = inverted
			? restValue - (int)(restValue * pct)
			: restValue + (int)((65535 - restValue) * pct);
	}

	/// <summary>
	/// Attempts to find and open the pedal device. Always logs available devices so you can
	/// identify the correct name and axis. Returns null if the device is not found.
	/// </summary>
	/// <param name="deviceNameHint">Case-insensitive substring of the DirectInput device name.</param>
	/// <param name="axisName">Axis: X, Y, Z, Rx, Ry, Rz, Slider0, Slider1.</param>
	/// <param name="thresholdPct">0-100 - axis percentage above/below which pedal is considered pressed.</param>
	/// <param name="inverted">True when the axis reads high at rest and low when pressed.</param>
	public static PedalMonitor? TryCreate(string deviceNameHint, string axisName, int thresholdPct, bool inverted)
	{
		var di = new DirectInput();
		var devices = di.GetDevices(DeviceClass.GameControl, DeviceEnumerationFlags.AllDevices).ToList();

		if (devices.Count == 0)
		{
			_log.Warning("No DirectInput GameControl devices found.");
			return null;
		}

		_log.Information("Available DirectInput devices ({Count}):", devices.Count);
		foreach (var d in devices)
			_log.Information("  '{Name}'  (type={Type})", d.InstanceName, d.Type);

		if (string.IsNullOrWhiteSpace(deviceNameHint))
		{
			_log.Information("PedalDevice is not configured. Set it to a substring of a device name above.");
			return null;
		}

		var info = devices.FirstOrDefault(d =>
			d.InstanceName.Contains(deviceNameHint, StringComparison.OrdinalIgnoreCase));

		if (info == null)
		{
			_log.Warning("Pedal device not found (hint: '{Hint}'). Sustain pedal disabled.", deviceNameHint);
			return null;
		}

		var joystick = new Joystick(di, info.InstanceGuid);
		joystick.Acquire();

		joystick.Poll();
		var rest = joystick.GetCurrentState();
		_log.Information("Axis values at rest - X={X}  Y={Y}  Z={Z}  Rx={Rx}  Ry={Ry}  Rz={Rz}  Slider0={S0}  Slider1={S1}",
			rest.X, rest.Y, rest.Z, rest.RotationX, rest.RotationY, rest.RotationZ, rest.Sliders[0], rest.Sliders[1]);

		if (axisName.Equals("discover", StringComparison.OrdinalIgnoreCase))
		{
			_log.Information("Discovery mode: press each pedal over the next 10 seconds - any axis that moves will be logged.");
			Task.Run(() => RunDiscovery(joystick, rest));
			return null;
		}

		Func<JoystickState, int> axisReader = axisName.ToUpperInvariant() switch
		{
			"X" => s => s.X,
			"Y" => s => s.Y,
			"Z" => s => s.Z,
			"RX" => s => s.RotationX,
			"RY" => s => s.RotationY,
			"RZ" => s => s.RotationZ,
			"SLIDER0" => s => s.Sliders[0],
			"SLIDER1" => s => s.Sliders[1],
			_ => throw new ArgumentException($"Unknown pedal axis '{axisName}'. Use X, Y, Z, Rx, Ry, Rz, Slider0, Slider1, or 'discover'.")
		};

		int restValue = axisReader(rest);
		double pct = thresholdPct / 100.0;
		int absoluteThreshold = inverted
			? restValue - (int)(restValue * pct)
			: restValue + (int)((65535 - restValue) * pct);
		_log.Information("Pedal device ready - axis={Axis}  rest={Rest}/65535  threshold={Threshold}/65535 ({Pct}% of travel)  fires when axis {Dir} {Threshold}",
			axisName, restValue, absoluteThreshold, thresholdPct, inverted ? "<" : ">", absoluteThreshold);
		return new PedalMonitor(joystick, axisReader, restValue, thresholdPct, inverted);
	}

	private static void RunDiscovery(Joystick joystick, JoystickState baseline)
	{
		const int deltaThreshold = 2000;
		var deadline = DateTime.UtcNow.AddSeconds(10);

		while (DateTime.UtcNow < deadline)
		{
			joystick.Poll();
			var cur = joystick.GetCurrentState();

			(string Name, int Prev, int Cur)[] axes =
			[
				("X",       baseline.X,            cur.X),
				("Y",       baseline.Y,            cur.Y),
				("Z",       baseline.Z,            cur.Z),
				("Rx",      baseline.RotationX,    cur.RotationX),
				("Ry",      baseline.RotationY,    cur.RotationY),
				("Rz",      baseline.RotationZ,    cur.RotationZ),
				("Slider0", baseline.Sliders[0],   cur.Sliders[0]),
				("Slider1", baseline.Sliders[1],   cur.Sliders[1]),
			];

			foreach (var (name, prev, curVal) in axes)
			{
				if (Math.Abs(curVal - prev) > deltaThreshold)
					_log.Information("  [{Axis}] changed  {Prev}: {Cur}", name, prev, curVal);
			}

			baseline = cur;
			Thread.Sleep(50);
		}

		_log.Information("Discovery complete. Set PedalAxis in config.json to the axis name(s) above.");
	}

	public void Start(CancellationToken ct) => Task.Run(() => PollLoop(ct), ct);

	private void PollLoop(CancellationToken ct)
	{
		bool active = false;
		while (!ct.IsCancellationRequested)
		{
			try
			{
				_joystick.Poll();
				int value = _axisReader(_joystick.GetCurrentState());
				bool pressed = _inverted ? value < _threshold : value > _threshold;

				if (pressed && !active)
				{
					active = true;
					_log.Information("Pedal down  (axis={V}/65535)", value);
					PedalDown?.Invoke(this, EventArgs.Empty);
				}
				else if (!pressed && active)
				{
					active = false;
					_log.Information("Pedal up    (axis={V}/65535)", value);
					PedalUp?.Invoke(this, EventArgs.Empty);
				}
			}
			catch (SharpDX.SharpDXException ex) when (!ct.IsCancellationRequested)
			{
				_log.Warning(ex, "Pedal poll error; retrying.");
				Thread.Sleep(500);
			}

			Thread.Sleep(5);
		}
	}

	public void Dispose() => _joystick.Dispose();
}
