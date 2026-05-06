using Serilog;

namespace DDSharp;

/// <summary>
/// High-level interface for DrunkDeer magnetic-switch keyboards.
/// </summary>
/// <remarks>
/// All <em>millimetre</em> parameters refer to key-travel depth from the top of the switch.
/// Valid ranges:
/// <list type="bullet">
///   <item><description>Actuation point: <c>0.2 mm - 3.8 mm</c></description></item>
///   <item><description>Downstroke / Upstroke: <c>0.0 mm - 3.6 mm</c></description></item>
/// </list>
/// Dispose the instance when finished to release the USB handle.
/// </remarks>
/// <example>
/// <code>
/// using var kb = new DDKeyboardInterface();
/// Console.WriteLine(kb.Info?.FirmwareVersion);
///
/// kb.SetMaxSensitivity();                         // 0.2 mm for every key
/// kb.SetActuationPoint(1.5);                      // uniform 1.5 mm
/// kb.SetActuationPoints([1.5, 2.0, 1.0, ...]);   // per-key array (126 elements)
/// </code>
/// </example>
public class DDKeyboardInterface : IDisposable
{
	private static readonly ILogger _log = Log.ForContext<DDKeyboardInterface>();

	// ── Key-point limits (millimetres) ────────────────────────────────────────

	/// <summary>Minimum valid actuation-point depth (most sensitive). Value: <c>0.2 mm</c>.</summary>
	public const double ActuationMinMm = 0.2;

	/// <summary>Maximum valid actuation-point depth (least sensitive). Value: <c>3.8 mm</c>.</summary>
	public const double ActuationMaxMm = 3.8;

	/// <summary>Minimum valid downstroke / upstroke depth. Value: <c>0.0 mm</c>.</summary>
	public const double StrokeMinMm = 0.0;

	/// <summary>Maximum valid downstroke / upstroke depth. Value: <c>3.6 mm</c>.</summary>
	public const double StrokeMaxMm = 3.6;

	// ── Key-point limits (raw firmware units, 1 unit = 0.1 mm) ───────────────

	/// <summary>Minimum actuation-point raw unit. Equivalent to <c>0.2 mm</c>.</summary>
	public const byte ActuationMinRaw = 2;

	/// <summary>Maximum actuation-point raw unit. Equivalent to <c>3.8 mm</c>.</summary>
	public const byte ActuationMaxRaw = 38;

	/// <summary>Minimum downstroke / upstroke raw unit. Equivalent to <c>0.0 mm</c>.</summary>
	public const byte StrokeMinRaw = 0;

	/// <summary>Maximum downstroke / upstroke raw unit. Equivalent to <c>3.6 mm</c>.</summary>
	public const byte StrokeMaxRaw = 36;

	/// <summary>
	/// Total number of individually addressable keys in the profile-write protocol.
	/// </summary>
	public const int ProfileKeyCount = 126;
	// 0xA0 identity request - firmware replies with model bytes and firmware version regardless of payload.
	private static readonly byte[] CmdRequestId = [0x04, 0xa0, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00];

	// 0xB6/0x03/0x01 key-height snapshot request - firmware replies with three 0xB7 packets
	// carrying 59 signed-byte heights each (127 heights total, indices 0-126).
	private static readonly byte[] CmdRequestKeys = [0x04, 0xb6, 0x03, 0x01, 0x00, 0x00, 0x00, 0x00];

	/// <summary>Numeric model identifier returned by the keyboard (e.g. 75 = A75).</summary>
	public int KeyboardIdentifier { get; private set; }

	/// <summary>Human-readable keyboard model name (e.g. <c>"G75"</c>, <c>"A75 Pro"</c>).</summary>
	public string KeyboardName => Layouts.GetKeyboardNameFromId(KeyboardIdentifier);

	/// <summary>
	/// Key-point encoding mode for this keyboard model.
	/// High-precision models use command <c>0xFD</c> with 0.005 mm resolution;
	/// standard models use <c>0xB6</c> with 0.1 mm resolution.
	/// </summary>
	public PrecisionMode Precision { get; private set; }

	/// <summary>Key-name layout for this keyboard model, used to resolve key names to indices.</summary>
	public string[] Layout { get; private set; } = [];

	/// <summary>Firmware information read from the keyboard on connect. <c>null</c> if unavailable.</summary>
	public KeyboardInfo? Info { get; private set; }

	/// <summary>
	/// Fired whenever any key's travel depth changes between two consecutive polls.
	/// This is the most granular event; use it to detect intermediate threshold crossings
	/// (e.g. for velocity calculation).
	/// </summary>
	public event EventHandler<KeyHeightChangedEventArgs>? KeyHeightChanged;

	/// <summary>
	/// Fired when a key's travel depth crosses <see cref="PressThreshold"/> on the way down.
	/// Guaranteed to fire before <see cref="KeyUp"/> for the same key.
	/// </summary>
	public event EventHandler<KeyEventArgs>? KeyDown;

	/// <summary>
	/// Fired when a key's travel depth drops below <see cref="ReleaseThreshold"/> after having been pressed.
	/// Guaranteed to fire after <see cref="KeyDown"/> for the same key.
	/// </summary>
	public event EventHandler<KeyEventArgs>? KeyUp;

	/// <summary>
	/// Fired after a complete press-release cycle: once <see cref="KeyDown"/> and then
	/// <see cref="KeyUp"/> have both fired for the same key.
	/// Fires at the same time as <see cref="KeyUp"/>, after it.
	/// </summary>
	public event EventHandler<KeyEventArgs>? KeyPressed;

	/// <summary>
	/// Fired once after every complete poll cycle, after all key events for that cycle have been raised.
	/// Use <see cref="PolledEventArgs.Hz"/> to monitor polling rate.
	/// </summary>
	public event EventHandler<PolledEventArgs>? Polled;

	/// <summary>
	/// Raw travel depth (1 unit = 0.1 mm) a key must reach to fire <see cref="KeyDown"/>.
	/// Default: <c>10</c> (1.0 mm). Must be greater than <see cref="ReleaseThreshold"/>.
	/// </summary>
	public int PressThreshold { get; set; } = 10;

	/// <summary>
	/// Raw travel depth (1 unit = 0.1 mm) a key must drop below to fire <see cref="KeyUp"/>.
	/// Default: <c>5</c> (0.5 mm). Must be less than <see cref="PressThreshold"/>.
	/// </summary>
	public int ReleaseThreshold { get; set; } = 5;

	private Task? _pollTask;
	private CancellationTokenSource? _pollCts;
	private readonly sbyte[] _pollHeights = new sbyte[127];
	private readonly bool[] _pollPressed = new bool[127];

	/// <summary>
	/// Starts a background polling loop that continuously reads key heights and raises
	/// <see cref="KeyHeightChanged"/>, <see cref="KeyDown"/>, <see cref="KeyUp"/>,
	/// <see cref="KeyPressed"/>, and <see cref="Polled"/> events.
	/// </summary>
	/// <param name="cancellationToken">
	/// Token that stops the loop when cancelled.
	/// If omitted, call <see cref="StopPolling"/> to stop.
	/// </param>
	/// <remarks>Calling this while polling is already running is a no-op.</remarks>
	public void StartPolling(CancellationToken cancellationToken = default)
	{
		if (_pollTask is { IsCompleted: false })
		{
			_log.Warning("Polling is already running; ignoring StartPolling call.");
			return;
		}

		_pollCts  = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
		_pollTask = Task.Run(() => PollLoop(_pollCts.Token), _pollCts.Token);
		_log.Information("Key polling started.");
	}

	/// <summary>
	/// Signals the polling loop to stop and waits up to 2 seconds for it to finish.
	/// </summary>
	public void StopPolling()
	{
		_pollCts?.Cancel();
		_pollTask?.Wait(TimeSpan.FromSeconds(2));
		_log.Information("Key polling stopped.");
	}

	// Each 0xB7 response packet: buf[4] = packet index (0/1/2), payload at buf[5].
	// pkt 0 = keys 0-58, pkt 1 = keys 59-117, pkt 2 = keys 118-126.
	private static readonly int[] B7PacketBase = [0, 59, 118];
	private static readonly int[] B7PacketCount = [59, 59, 9];

	private void PollLoop(CancellationToken ct)
	{
		PollLoopRequestResponse(ct);
	}

	// Fallback: explicitly request a frame each iteration. Caps at ~333Hz due to 3 packets/frame.
	private void PollLoopRequestResponse(CancellationToken ct)
	{
		_log.Information("Polling mode: request-response.");
		_hid.SetReadTimeout(200);

		var packets = new byte[3][];
		bool[] gotPkt = new bool[3];
		long lastFrameTicks = System.Diagnostics.Stopwatch.GetTimestamp();

		while (!ct.IsCancellationRequested)
		{
			if (_hid.Write(CmdRequestKeys) < 0) break;

			gotPkt[0] = gotPkt[1] = gotPkt[2] = false;
			int retries = 0;

			while ((!gotPkt[0] || !gotPkt[1] || !gotPkt[2]) && retries < 10)
			{
				byte[] buf = new byte[65];
				int read = _hid.ReadCommand(buf);
				if (read <= 0) { retries++; continue; }
				if (buf[1] != 0xB7) { retries++; continue; }

				int idx = buf[4];
				if (idx < 0 || idx > 2) { retries++; continue; }

				packets[idx] = buf;
				gotPkt[idx]  = true;
			}

			if (!gotPkt[0] || !gotPkt[1] || !gotPkt[2]) continue;

			long frameTicks = System.Diagnostics.Stopwatch.GetTimestamp();
			var elapsed = TimeSpan.FromSeconds((double)(frameTicks - lastFrameTicks) / System.Diagnostics.Stopwatch.Frequency);
			lastFrameTicks = frameTicks;

			DispatchFrame(packets, elapsed);
		}

		_hid.SetReadTimeout(5000);
	}

	private void DispatchFrame(byte[][] packets, TimeSpan elapsed)
	{
		for (int pkt = 0; pkt < 3; pkt++)
		{
			int baseIdx = B7PacketBase[pkt];
			int count = B7PacketCount[pkt];
			byte[] pktBuf = packets[pkt];

			for (int x = 0; x < count; x++)
			{
				int i = baseIdx + x;
				if (i >= 127) break;

				sbyte h = (sbyte)pktBuf[5 + x];
				sbyte prev = _pollHeights[i];
				if (h == prev) continue;

				KeyHeightChanged?.Invoke(this, new KeyHeightChangedEventArgs(i, GetKeyName(i), prev, h));

				if (!_pollPressed[i] && h >= PressThreshold)
				{
					_pollPressed[i] = true;
					KeyDown?.Invoke(this, new KeyEventArgs(i, GetKeyName(i), h));
				}
				else if (_pollPressed[i] && h < ReleaseThreshold)
				{
					_pollPressed[i] = false;
					var args = new KeyEventArgs(i, GetKeyName(i), h);
					KeyUp?.Invoke(this, args);
					KeyPressed?.Invoke(this, args);
				}

				_pollHeights[i] = h;
			}
		}

		Polled?.Invoke(this, new PolledEventArgs(elapsed));
	}

	private string GetKeyName(int index) =>
		index < Layout.Length ? Layout[index] : string.Empty;

	/// <summary>
	/// Returns the zero-based layout index for a <see cref="DDKey"/>, or -1 if the key
	/// is not present in the current keyboard model's layout.
	/// </summary>
	public int GetKeyIndex(DDKey key)
	{
		if (!KeyLayoutNames.Names.TryGetValue(key, out var token))
			return -1;
		return Array.IndexOf(Layout, token);
	}

	/// <summary>
	/// Returns the <see cref="DDKey"/> at the given zero-based layout index, or <c>null</c>
	/// if the slot is empty, unnamed (<c>u*</c>), or out of range.
	/// </summary>
	public DDKey? GetKeyAtIndex(int index)
	{
		if (index < 0 || index >= Layout.Length) return null;
		string token = Layout[index];
		var map = KeyLayoutNames.Names.ToDictionary(kv => kv.Value, kv => kv.Key);
		return map.TryGetValue(token, out DDKey k) ? k : null;
	}

	// ── Construction / initialisation ────────────────────────────────────────

	private readonly HidInterface _hid = new();
	private readonly KeyboardProtocol _proto;

	/// <summary>
	/// Opens the first connected  keyboard and reads its identity.
	/// </summary>
	/// <exception cref="InvalidOperationException">
	/// Thrown when no supported keyboard is found or the identity query fails.
	/// </exception>
	public DDKeyboardInterface()
	{
		_proto = new KeyboardProtocol(_hid);
		if (!Initialize())
			throw new InvalidOperationException(
				"Could not connect to a DrunkDeer keyboard. " +
				"Ensure the keyboard is plugged in and no other application holds the HID interface.");
	}

	private bool Initialize()
	{
		if (!_hid.Open())
			return false;

		var result = SendCommandSync(CommandType.RequestId);
		if (!result.Success)
		{
			_log.Error("Failed to read keyboard identity: {Error}", result.Error);
			return false;
		}

		var data = result.Data;
		KeyboardIdentifier = Layouts.ParseIdentifierResult(data);
		_log.Information("Connected: {Name} (ID={Id})",
			Layouts.GetKeyboardNameFromId(KeyboardIdentifier), KeyboardIdentifier);

		if (data.Length >= 16)
		{
			Info = new KeyboardInfo(
				FirmwareVersion: $"0.{data[4]}{data[5]}",
				RapidTriggerEnabled: data[12] != 0,
				RapidTriggerPlusEnabled: data[14] != 0,
				TurboValue: data[11],
				LastWinValue: data[15]);
			_log.Information(
				"Firmware {Version} | RT={RT} | RT+={RTP} | Turbo={Turbo}",
				Info.FirmwareVersion, Info.RapidTriggerEnabled,
				Info.RapidTriggerPlusEnabled, Info.TurboValue);
		}

		Layout = KeyboardIdentifier switch
		{
			// A75 family - all share the A75 physical layout
			75 or 750 or 751 or 756 or 757 => Layouts.KeyboardLayoutA75,
			// G75 family
			754 or 755 => Layouts.KeyboardLayoutG75,
			// G65 family
			65 or 651 or 652 or 653 or 654 => Layouts.KeyboardLayoutG65,
			// G60 family (including X60 - compact 60 %)
			60 or 601 or 602 or 603 or 640 => Layouts.KeyboardLayoutG60,
			// Fallback
			_ => Layouts.KeyboardLayoutA75,
		};

		// G75 family and newer A75 variants use the high-precision 0xFD command set.
		Precision = KeyboardIdentifier switch
		{
			754 or 755 => PrecisionMode.High, // G75, G75 JP
			750 or 756 or 757 => PrecisionMode.High, // A75 Pro, Ultra, Master
			_ => PrecisionMode.Standard,
		};
		_log.Debug("Precision mode: {Mode}", Precision);

		return true;
	}

	// Tracks the last-written values for every key so that the params DDKey[]
	// overloads can send a complete 126-value packet with only the targeted
	// keys changed.  Initialised to sensible defaults matching typical factory
	// settings (2.0 mm actuation, 0.0 mm strokes).

	private readonly double[] _actuationProfile = Enumerable.Repeat(2.0, ProfileKeyCount).ToArray();
	private readonly double[] _downstrokeProfile = new double[ProfileKeyCount];
	private readonly double[] _upstrokeProfile = new double[ProfileKeyCount];

	/// <summary>
	/// Polls the current physical travel depth of every key.
	/// </summary>
	/// <returns>
	/// Array of 127 signed bytes. Higher values mean the key is pressed deeper.
	/// Index order matches <see cref="Layout"/>.
	/// </returns>
	public sbyte[] ReadKeyHeights()
	{
		var result = SendCommandSync(CommandType.RequestKeys);
		var heights = new sbyte[127];
		for (int i = 0; i < 127; i++)
			heights[i] = (sbyte)result.Data[i];
		return heights;
	}

	/// <summary>
	/// Sets the actuation point for every key to the same depth.
	/// </summary>
	/// <param name="mm">
	/// Depth in millimetres at which a keypress is registered.
	/// Valid range: <c>0.2 - 3.8 mm</c>. Use <c>0.2</c> for maximum sensitivity.
	/// </param>
	/// <exception cref="ArgumentOutOfRangeException">
	/// Thrown when <paramref name="mm"/> is outside <c>0.2 - 3.8 mm</c>.
	/// </exception>
	public bool SetActuationPoint(double mm)
	{
		ValidateActuation(mm);
		Array.Fill(_actuationProfile, mm);
		_log.Debug("SetActuationPoint({Mm:F1} mm)", mm);
		return DispatchKeyPointMm(KeyPointType.ActuationPoint, _actuationProfile);
	}

	/// <summary>
	/// Sets the actuation point for specific keys, leaving all other keys unchanged.
	/// The first call uses a default profile of <c>2.0 mm</c> for unspecified keys.
	/// </summary>
	/// <param name="mm">
	/// Depth in millimetres for the listed keys.
	/// Valid range: <c>0.2 - 3.8 mm</c>. Use <c>0.2</c> for maximum sensitivity.
	/// </param>
	/// <param name="keys">One or more keys to update.</param>
	/// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="mm"/> is outside <c>0.2 - 3.8 mm</c>.</exception>
	/// <exception cref="ArgumentException">Thrown when <paramref name="keys"/> is empty.</exception>
	/// <example>
	/// <code>
	/// kb.SetActuationPoint(0.2, DDKey.W, DDKey.A, DDKey.S, DDKey.D);  // WASD ultra-sensitive
	/// kb.SetActuationPoint(3.8, DDKey.CapsLock);                      // CapsLock hard to trigger
	/// </code>
	/// </example>
	public bool SetActuationPoint(double mm, params DDKey[] keys)
	{
		ValidateActuation(mm);
		if (keys.Length == 0)
			throw new ArgumentException("At least one key must be specified.", nameof(keys));
		foreach (var key in keys)
		{
			int idx = GetKeyIndex(key);
			if (idx >= 0) _actuationProfile[idx] = mm;
			else _log.Warning("Key {Key} is not present on this keyboard model; skipped.", key);
		}
		_log.Debug("SetActuationPoint({Mm:F1} mm): {Keys}", mm, string.Join(", ", keys));
		return DispatchKeyPointMm(KeyPointType.ActuationPoint, _actuationProfile);
	}

	/// <summary>
	/// Sets the actuation point individually for each key.
	/// </summary>
	/// <param name="mmValues">
	/// Array of exactly <see cref="ProfileKeyCount"/> (126) depth values in millimetres.
	/// Each value must be in the range <c>0.2 - 3.8 mm</c>.
	/// </param>
	/// <exception cref="ArgumentNullException">Thrown when <paramref name="mmValues"/> is null.</exception>
	/// <exception cref="ArgumentException">Thrown when the array does not contain exactly 126 elements.</exception>
	/// <exception cref="ArgumentOutOfRangeException">Thrown when any value is outside <c>0.2 - 3.8 mm</c>.</exception>
	public bool SetActuationPoints(double[] mmValues)
	{
		ArgumentNullException.ThrowIfNull(mmValues);
		ValidateLength(mmValues.Length);
		for (int i = 0; i < mmValues.Length; i++)
			ValidateActuation(mmValues[i], $"{nameof(mmValues)}[{i}]");
		mmValues.CopyTo(_actuationProfile, 0);
		_log.Debug("SetActuationPoints(126 values)");
		return DispatchKeyPointMm(KeyPointType.ActuationPoint, _actuationProfile);
	}

	/// <summary>
	/// Sets the downstroke point for every key to the same depth.
	/// </summary>
	/// <param name="mm">
	/// Depth in millimetres the key must reach on the way down.
	/// Valid range: <c>0.0 - 3.6 mm</c>.
	/// </param>
	/// <exception cref="ArgumentOutOfRangeException">
	/// Thrown when <paramref name="mm"/> is outside <c>0.0 - 3.6 mm</c>.
	/// </exception>
	public bool SetDownstrokePoint(double mm)
	{
		ValidateStroke(mm);
		Array.Fill(_downstrokeProfile, mm);
		_log.Debug("SetDownstrokePoint({Mm:F1} mm)", mm);
		return DispatchKeyPointMm(KeyPointType.Downstroke, _downstrokeProfile);
	}

	/// <summary>
	/// Sets the downstroke point for specific keys, leaving all other keys unchanged.
	/// </summary>
	/// <param name="mm">Depth in millimetres. Valid range: <c>0.0 - 3.6 mm</c>.</param>
	/// <param name="keys">One or more keys to update.</param>
	/// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="mm"/> is outside <c>0.0 - 3.6 mm</c>.</exception>
	/// <exception cref="ArgumentException">Thrown when <paramref name="keys"/> is empty.</exception>
	public bool SetDownstrokePoint(double mm, params DDKey[] keys)
	{
		ValidateStroke(mm);
		if (keys.Length == 0)
			throw new ArgumentException("At least one key must be specified.", nameof(keys));
		foreach (var key in keys)
		{
			int idx = GetKeyIndex(key);
			if (idx >= 0) _downstrokeProfile[idx] = mm;
			else _log.Warning("Key {Key} is not present on this keyboard model; skipped.", key);
		}
		_log.Debug("SetDownstrokePoint({Mm:F1} mm): {Keys}", mm, string.Join(", ", keys));
		return DispatchKeyPointMm(KeyPointType.Downstroke, _downstrokeProfile);
	}

	/// <summary>
	/// Sets the downstroke point individually for each key.
	/// </summary>
	/// <param name="mmValues">
	/// Array of exactly <see cref="ProfileKeyCount"/> (126) depth values in millimetres.
	/// Each value must be in the range <c>0.0 - 3.6 mm</c>.
	/// </param>
	/// <exception cref="ArgumentNullException">Thrown when <paramref name="mmValues"/> is null.</exception>
	/// <exception cref="ArgumentException">Thrown when the array does not contain exactly 126 elements.</exception>
	/// <exception cref="ArgumentOutOfRangeException">Thrown when any value is outside <c>0.0 - 3.6 mm</c>.</exception>
	public bool SetDownstrokePoints(double[] mmValues)
	{
		ArgumentNullException.ThrowIfNull(mmValues);
		ValidateLength(mmValues.Length);
		for (int i = 0; i < mmValues.Length; i++)
			ValidateStroke(mmValues[i], $"{nameof(mmValues)}[{i}]");
		mmValues.CopyTo(_downstrokeProfile, 0);
		_log.Debug("SetDownstrokePoints(126 values)");
		return DispatchKeyPointMm(KeyPointType.Downstroke, _downstrokeProfile);
	}

	/// <summary>
	/// Sets the upstroke point for every key to the same depth.
	/// </summary>
	/// <param name="mm">
	/// Depth in millimetres the key must rise to before it is considered released.
	/// Valid range: <c>0.0 - 3.6 mm</c>.
	/// </param>
	/// <exception cref="ArgumentOutOfRangeException">
	/// Thrown when <paramref name="mm"/> is outside <c>0.0 - 3.6 mm</c>.
	/// </exception>
	public bool SetUpstrokePoint(double mm)
	{
		ValidateStroke(mm);
		Array.Fill(_upstrokeProfile, mm);
		_log.Debug("SetUpstrokePoint({Mm:F1} mm)", mm);
		return DispatchKeyPointMm(KeyPointType.Upstroke, _upstrokeProfile);
	}

	/// <summary>
	/// Sets the upstroke point for specific keys, leaving all other keys unchanged.
	/// </summary>
	/// <param name="mm">Depth in millimetres. Valid range: <c>0.0 - 3.6 mm</c>.</param>
	/// <param name="keys">One or more keys to update.</param>
	/// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="mm"/> is outside <c>0.0 - 3.6 mm</c>.</exception>
	/// <exception cref="ArgumentException">Thrown when <paramref name="keys"/> is empty.</exception>
	public bool SetUpstrokePoint(double mm, params DDKey[] keys)
	{
		ValidateStroke(mm);
		if (keys.Length == 0)
			throw new ArgumentException("At least one key must be specified.", nameof(keys));
		foreach (var key in keys)
		{
			int idx = GetKeyIndex(key);
			if (idx >= 0) _upstrokeProfile[idx] = mm;
			else _log.Warning("Key {Key} is not present on this keyboard model; skipped.", key);
		}
		_log.Debug("SetUpstrokePoint({Mm:F1} mm): {Keys}", mm, string.Join(", ", keys));
		return DispatchKeyPointMm(KeyPointType.Upstroke, _upstrokeProfile);
	}

	/// <summary>
	/// Sets the upstroke point individually for each key.
	/// </summary>
	/// <param name="mmValues">
	/// Array of exactly <see cref="ProfileKeyCount"/> (126) depth values in millimetres.
	/// Each value must be in the range <c>0.0 - 3.6 mm</c>.
	/// </param>
	/// <exception cref="ArgumentNullException">Thrown when <paramref name="mmValues"/> is null.</exception>
	/// <exception cref="ArgumentException">Thrown when the array does not contain exactly 126 elements.</exception>
	/// <exception cref="ArgumentOutOfRangeException">Thrown when any value is outside <c>0.0 - 3.6 mm</c>.</exception>
	public bool SetUpstrokePoints(double[] mmValues)
	{
		ArgumentNullException.ThrowIfNull(mmValues);
		ValidateLength(mmValues.Length);
		for (int i = 0; i < mmValues.Length; i++)
			ValidateStroke(mmValues[i], $"{nameof(mmValues)}[{i}]");
		mmValues.CopyTo(_upstrokeProfile, 0);
		_log.Debug("SetUpstrokePoints(126 values)");
		return DispatchKeyPointMm(KeyPointType.Upstroke, _upstrokeProfile);
	}

	/// <summary>
	/// Sets every key's actuation point to the minimum (<c>0.2 mm</c>),
	/// giving the most sensitive response possible.
	/// Also lowers <see cref="PressThreshold"/> and <see cref="ReleaseThreshold"/> to match.
	/// </summary>
	public bool SetMaxSensitivity()
	{
		PressThreshold   = ActuationMinRaw;     // 2 = 0.2 mm
		ReleaseThreshold = ActuationMinRaw - 1; // 1 = 0.1 mm
		Array.Fill(_actuationProfile, ActuationMinMm);
		_log.Information("Applying maximum sensitivity (actuation = {Min} mm)", ActuationMinMm);
		return DispatchKeyPointMm(KeyPointType.ActuationPoint, _actuationProfile);
	}

	/// <summary>
	/// Sets a key-point type using raw firmware units (1 unit = 0.1 mm) for all keys.
	/// Prefer the millimetre overloads (<see cref="SetActuationPoint"/> etc.) where possible.
	/// </summary>
	/// <param name="type">Which point in the key travel to set.</param>
	/// <param name="rawValue">
	/// Raw unit value. Valid ranges:
	/// <list type="bullet">
	///   <item><description>Actuation: <c>2 - 38</c> (<see cref="ActuationMinRaw"/> - <see cref="ActuationMaxRaw"/>)</description></item>
	///   <item><description>Downstroke / Upstroke: <c>0 - 36</c> (<see cref="StrokeMinRaw"/> - <see cref="StrokeMaxRaw"/>)</description></item>
	/// </list>
	/// </param>
	/// <exception cref="ArgumentOutOfRangeException">Thrown when the value is outside the valid range for the given type.</exception>
	public bool SetKeyPoints(KeyPointType type, byte rawValue)
	{
		ValidateRaw(type, rawValue);
		double mm = rawValue / 10.0;
		var mmValues = new double[ProfileKeyCount];
		Array.Fill(mmValues, mm);
		return DispatchKeyPointMm(type, mmValues);
	}

	/// <summary>
	/// Sets a key-point type using raw firmware units (1 unit = 0.1 mm), per key.
	/// Prefer the millimetre overloads (<see cref="SetActuationPoints"/> etc.) where possible.
	/// </summary>
	/// <param name="type">Which point in the key travel to set.</param>
	/// <param name="rawValues">
	/// Array of exactly <see cref="ProfileKeyCount"/> (126) raw unit values.
	/// See <see cref="SetKeyPoints(KeyPointType, byte)"/> for valid ranges.
	/// </param>
	/// <exception cref="ArgumentNullException">Thrown when <paramref name="rawValues"/> is null.</exception>
	/// <exception cref="ArgumentException">Thrown when the array does not have exactly 126 elements.</exception>
	/// <exception cref="ArgumentOutOfRangeException">Thrown when any value is outside the valid range for the given type.</exception>
	public bool SetKeyPoints(KeyPointType type, byte[] rawValues)
	{
		ArgumentNullException.ThrowIfNull(rawValues);
		ValidateLength(rawValues.Length);
		for (int i = 0; i < rawValues.Length; i++)
			ValidateRaw(type, rawValues[i], $"{nameof(rawValues)}[{i}]");
		return DispatchKeyPointMm(type, rawValues.Select(b => b / 10.0).ToArray());
	}

	/// <summary>
	/// Sends a built-in read command and returns the raw response data.
	/// Use <see cref="ReadKeyHeights"/> for key polling instead of calling this directly.
	/// </summary>
	public CallResult SendCommandSync(CommandType type)
	{
		bool isId = type == CommandType.RequestId;

		byte[] cmd = isId ? CmdRequestId : CmdRequestKeys;
		int count = isId ? 1 : 3;
		Func<byte[], bool> filter = isId
			? buf => buf[1] == 0xa0 && buf[2] == 0x02 && buf[3] == 0x00
			: buf => buf[1] == 0xb7;

		var packets = new List<byte[]>(count);
		if (!_proto.TryReadPackets(cmd, filter, count, packets, maxRetries: 20))
			return new CallResult(false, isId ? "Identity read failed" : "Key heights read failed", []);

		// Assemble flat result - identity yields 59 bytes; key heights yield 3×59 = 177 bytes.
		// Packets arrive in order; data payload begins at buf[5] (59 bytes per packet).
		byte[] result = new byte[count * 59];
		for (int i = 0; i < packets.Count; i++)
			Array.Copy(packets[i], 5, result, i * 59, 59);

		return new CallResult(true, "", result);
	}

	/// <summary>
	/// Routes to <see cref="KeyboardProtocol.WriteB6"/> or <see cref="KeyboardProtocol.WriteFd"/>
	/// based on <see cref="Precision"/>, preserving full sub-0.1 mm resolution on high-precision
	/// keyboards.
	/// </summary>
	private bool DispatchKeyPointMm(KeyPointType type, double[] mmValues) =>
		Precision == PrecisionMode.High
			? _proto.WriteFd(type, mmValues)
			: _proto.WriteB6(type, mmValues.Select(MmToRaw).ToArray());

	/// <summary>
	/// Reads the current key-point profile directly from the keyboard firmware.
	/// </summary>
	/// <remarks>
	/// On <see cref="PrecisionMode.Standard"/> keyboards the firmware does not expose
	/// a pull command, so the last-written in-memory values are returned instead.
	/// On <see cref="PrecisionMode.High"/> keyboards the method queries the firmware
	/// and updates the in-memory cache with the returned values.
	/// Any active polling loop is suspended for the duration of the query and restarted
	/// on return to avoid the poll task consuming the response packets.
	/// </remarks>
	/// <returns>
	/// A <see cref="KeyPointProfile"/> with actuation, downstroke and upstroke depths in mm
	/// for all <see cref="ProfileKeyCount"/> (126) key positions,
	/// or <c>null</c> if the firmware query failed.
	/// </returns>
	public KeyPointProfile? ReadKeyPointProfile()
	{
		if (Precision == PrecisionMode.Standard)
		{
			_log.Debug("ReadKeyPointProfile: standard precision - returning cached values.");
			return new KeyPointProfile(
				(double[])_actuationProfile.Clone(),
				(double[])_downstrokeProfile.Clone(),
				(double[])_upstrokeProfile.Clone());
		}

		bool wasPolling = _pollTask is { IsCompleted: false };
		if (wasPolling) StopPolling();

		try
		{
			double[]? actuation = _proto.PullFd(0x01, 0x08);
			double[]? downstroke = _proto.PullFd(0x03, 0x0A);
			double[]? upstroke = _proto.PullFd(0x04, 0x0B);

			if (actuation == null || downstroke == null || upstroke == null)
			{
				_log.Error("ReadKeyPointProfile: one or more pull commands failed.");
				return null;
			}

			actuation.CopyTo(_actuationProfile, 0);
			downstroke.CopyTo(_downstrokeProfile, 0);
			upstroke.CopyTo(_upstrokeProfile, 0);

			_log.Information("ReadKeyPointProfile: firmware profile loaded and cache updated.");
			return new KeyPointProfile(actuation, downstroke, upstroke);
		}
		finally
		{
			if (wasPolling) StartPolling();
		}
	}

	/// <summary>Discard any pending HID input packets. Call before a fuzz sweep to avoid stale data.</summary>
	public void FlushReadBuffer() => _hid.FlushReadBuffer();

	/// <summary><c>false</c> if the HID device has disconnected (e.g. keyboard rebooted).</summary>
	public bool IsConnected => _hid.IsConnected;

	/// <summary>
	/// Logs every HID interface found for known DrunkDeer VID/PID combinations.
	/// Use before connecting to identify all available interfaces and their report sizes.
	/// </summary>
	public static void EnumerateInterfaces() => HidInterface.EnumerateInterfaces();

	/// <summary>Converts millimetres to the raw firmware unit (×10, rounded).</summary>
	private static byte MmToRaw(double mm) => (byte)Math.Round(mm * 10);

	private static void ValidateActuation(double mm, string paramName = "mm")
	{
		if (mm < ActuationMinMm || mm > ActuationMaxMm)
			throw new ArgumentOutOfRangeException(paramName, mm,
				$"Actuation point must be between {ActuationMinMm} mm and {ActuationMaxMm} mm.");
	}

	private static void ValidateStroke(double mm, string paramName = "mm")
	{
		if (mm < StrokeMinMm || mm > StrokeMaxMm)
			throw new ArgumentOutOfRangeException(paramName, mm,
				$"Stroke point must be between {StrokeMinMm} mm and {StrokeMaxMm} mm.");
	}

	private static void ValidateRaw(KeyPointType type, byte value, string paramName = "rawValue")
	{
		(byte min, byte max) = type == KeyPointType.ActuationPoint
			? (ActuationMinRaw, ActuationMaxRaw)
			: (StrokeMinRaw, StrokeMaxRaw);

		if (value < min || value > max)
			throw new ArgumentOutOfRangeException(paramName, value,
				$"Raw value for {type} must be between {min} and {max} (= {min * 0.1:F1}-{max * 0.1:F1} mm).");
	}

	private static void ValidateLength(int length)
	{
		if (length != ProfileKeyCount)
			throw new ArgumentException(
				$"Array must contain exactly {ProfileKeyCount} elements, one per key. Got {length}.");
	}

	public void Dispose()
	{
		StopPolling();
		_hid.Dispose();
	}
}