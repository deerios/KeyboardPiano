using DrunkDeer.Protocol;
using NAudio.Midi;
using Serilog;

namespace KeyboardPiano;

public class MidiAdapter : IDisposable
{
	private static readonly ILogger _log = Log.ForContext<MidiAdapter>();

	private const int SemitonesPerOctave = 12;

	private readonly KeyboardSession _keyboard;
	private readonly MidiOut _midiOut;

	private readonly int _deadzoneDepth = 3;
	private readonly int _actuationPoint;
	private readonly int _releasePoint;
	private readonly double _cooldownMs;
	private readonly double _velocityMinMs;
	private readonly double _velocityMaxMs;
	private readonly double _velocityGamma;
	private readonly double _velocityLogMinMs;
	private readonly double _velocityLogRangeMs;
	private readonly int _minVelocity;
	private readonly int _maxVelocity;
	private readonly double _actuationWindowMs;
	private readonly double _depthFactor;
	private readonly double _rateSaturation;
	private readonly Dictionary<int, (int Normal, int Shifted)> _keyMap;

	private readonly int _keyOctUp, _keyOctDown, _keyUp, _keyDown;
	private readonly int _keyShiftL, _keyShiftR;

	private volatile bool _isShiftHeld;
	private int _octOffset;
	private int _keyOffset;

	// Idle      - key at rest, no note
	// Pressing  - key rising, below actuation threshold
	// Actuating - crossed actuation; tracking peak depth for velocity measurement
	// Held      - note sounding, key above release threshold
	// Cooling   - post-NoteOff blackout to absorb mechanical spring-back
	private enum KeyState { Idle, Pressing, Actuating, Held, Cooling }

	private struct KeyTrackState
	{
		public KeyState State;
		public bool NoteOn;
		public int ActiveNote;
		public short PeakDepth;
		public float DepthRate;  // depth units/ms at the moment actuation threshold was crossed
	}

	private readonly KeyTrackState[] _keys = new KeyTrackState[127];
	private readonly long[] _prevPollMs = new long[127];
	private readonly short[] _prevPollDepth = new short[127];
	private readonly long[] _cooldownEndMs = new long[127];
	private readonly long[] _actuationEndMs = new long[127];

	public MidiAdapter(KeyboardSession keyboard, KeyboardMidiConfig config, MidiOut midiOut)
	{
		_keyboard = keyboard;
		_midiOut  = midiOut;

		(_releasePoint, _actuationPoint)                    = config.GetThresholds();
		(_minVelocity, _maxVelocity)                        = config.GetVelocityClampValues();
		(_velocityMinMs, _velocityMaxMs, _velocityGamma) = config.GetVelocityTimingMs();
		_velocityLogMinMs   = Math.Log(_velocityMinMs);
		_velocityLogRangeMs = Math.Log(_velocityMaxMs) - _velocityLogMinMs;
		_cooldownMs                                          = config.GetCooldownMs();
		_actuationWindowMs                                   = config.GetActuationWindowMs();
		_depthFactor                                         = config.GetDepthFactor();
		_rateSaturation                                      = config.GetRateSaturation();
		_keyMap                                              = config.BuildMidiKeyMap(keyboard);

		_keyOctUp   = config.GetKeyBindByName(keyboard, "OctUp");
		_keyOctDown = config.GetKeyBindByName(keyboard, "OctDown");
		_keyUp      = config.GetKeyBindByName(keyboard, "KeyUp");
		_keyDown    = config.GetKeyBindByName(keyboard, "KeyDown");
		_keyShiftL  = keyboard.GetKeyIndex(DDKey.LeftShift);
		_keyShiftR  = keyboard.GetKeyIndex(DDKey.RightShift);

		_keyboard.KeyHeightChanged += OnKeyHeightChanged;
		_keyboard.Polled           += OnPolled;
	}

	public void Run(CancellationToken ct = default)
	{
		_log.Information("MIDI adapter running.");

		// Put all keys in Cooling for the first 300ms so that keys already above the
		// deadzone at rest (elevated magnetic resting positions) don't fire stray notes.
		long startupEnd = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + 300;
		for (int i = 0; i < 127; i++)
		{
			_keys[i].State  = KeyState.Cooling;
			_cooldownEndMs[i] = startupEnd;
		}

		_keyboard.StartPolling(ct);
		ct.WaitHandle.WaitOne();

		_log.Information("MIDI adapter stopping.");
	}

	public void Dispose()
	{
		_keyboard.KeyHeightChanged -= OnKeyHeightChanged;
		_keyboard.Polled           -= OnPolled;
		_keyboard.StopPolling();
	}

	private void OnKeyHeightChanged(object? sender, KeyHeightChangedEventArgs e)
	{
		int index = e.Index;
		if ((uint)index >= 127) return;
		long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
		ProcessKey(index, e.Height, nowMs);
	}

	private void OnPolled(object? sender, PolledEventArgs e)
	{
		Console.Title = $"DrunkDeer MIDI Adapter  ( {e.Hz} Hz | Oct: {_octOffset} | Key: {_keyOffset} )";
	}

	private void ProcessKey(int keyIndex, short depth, long nowMs)
	{
		ref var key = ref _keys[keyIndex];

		bool repeat;
		do
		{
			repeat = false;
			switch (key.State)
			{
				case KeyState.Idle:
					if (depth > _deadzoneDepth)
					{
						if (keyIndex == _keyShiftL || keyIndex == _keyShiftR) _isShiftHeld = true;
						// Seed prev-poll to "4ms ago at deadzone" so depthRate is consistent
						// regardless of whether the press started during a prior cooldown period.
						_prevPollMs[keyIndex]    = nowMs - 4;
						_prevPollDepth[keyIndex] = (short)_deadzoneDepth;
						key.State = KeyState.Pressing;
						repeat = true;
					}
					break;

				case KeyState.Pressing:
					if (depth <= _deadzoneDepth)
					{
						// Light touch returned to rest before actuation.
						_cooldownEndMs[keyIndex] = nowMs + (long)_cooldownMs;
						key.State = KeyState.Cooling;
						break;
					}
					if (depth >= _actuationPoint)
					{
						if (keyIndex == _keyOctUp || keyIndex == _keyOctDown || keyIndex == _keyUp || keyIndex == _keyDown)
						{
							HandleControlActuation(keyIndex);
							key.State = KeyState.Held;
						}
						else
						{
							// Measure how fast the key was moving at the actuation crossing.
							// Using the slope over the last poll interval avoids the unreliable
							// press-start timestamp (which gets contaminated by elevated resting
							// positions and cooldown-expiry timing).
							long dtMs = Math.Max(nowMs - _prevPollMs[keyIndex], 1);
							double depthRate = (depth - _prevPollDepth[keyIndex]) / (double)dtMs;
							key.DepthRate      = (float)depthRate;
							key.PeakDepth      = depth;
							_prevPollMs[keyIndex]     = nowMs;   // seed for post-actuation rate tracking
							_prevPollDepth[keyIndex]  = depth;
							_actuationEndMs[keyIndex] = nowMs + (long)_actuationWindowMs;
							key.State          = KeyState.Actuating;
						}
					}
					else
					{
						_prevPollDepth[keyIndex] = depth;
						_prevPollMs[keyIndex]    = nowMs;
					}
					break;

				case KeyState.Actuating:
					if (depth > key.PeakDepth) key.PeakDepth = depth;
					bool windowExpired = nowMs >= _actuationEndMs[keyIndex];
					bool keyReleased   = depth < _releasePoint;
					if (windowExpired || keyReleased)
					{
						// Window-average rate from the actuation seed to now using peak depth;
						// smoother than instantaneous polling and captures post-actuation acceleration.
						long   windowDt   = Math.Max(nowMs - _prevPollMs[keyIndex], 1);
						double windowRate = (key.PeakDepth - _prevPollDepth[keyIndex]) / (double)windowDt;
						if (windowRate > key.DepthRate) key.DepthRate = (float)windowRate;
						FireActuatingNote(ref key, keyIndex, nowMs);
						key.State = KeyState.Held;
						if (keyReleased) repeat = true;
					}
					break;

				case KeyState.Held:
					if (depth < _releasePoint)
					{
						if (keyIndex == _keyShiftL || keyIndex == _keyShiftR) _isShiftHeld = false;
						EndNote(ref key, keyIndex);
						_cooldownEndMs[keyIndex] = nowMs + (long)_cooldownMs;
						key.State = KeyState.Cooling;
					}
					break;

				case KeyState.Cooling:
					if (nowMs >= _cooldownEndMs[keyIndex])
					{
						key.State = KeyState.Idle;
						repeat = true;
					}
					break;
			}
		} while (repeat);
	}

	private void HandleControlActuation(int keyIndex)
	{
		if (keyIndex == _keyOctUp   && _octOffset < 2) { _octOffset++; AllNotesOff(); _log.Debug("Oct: {V}", _octOffset); }
		else if (keyIndex == _keyOctDown && _octOffset > -2) { _octOffset--; AllNotesOff(); _log.Debug("Oct: {V}", _octOffset); }
		else if (keyIndex == _keyUp) { _keyOffset++; AllNotesOff(); _log.Debug("Key: {V}", _keyOffset); }
		else if (keyIndex == _keyDown) { _keyOffset--; AllNotesOff(); _log.Debug("Key: {V}", _keyOffset); }
	}

	private void FireActuatingNote(ref KeyTrackState key, int keyIndex, long nowMs)
	{
		double depthRate = Math.Max(key.DepthRate, 0.1);
		if (_rateSaturation > 0)
			depthRate = _rateSaturation * Math.Tanh(depthRate / _rateSaturation);
		double effectiveMs = _actuationPoint / depthRate
						   + (255.0 - key.PeakDepth) * _depthFactor;
		int velocity = CalcVelocity(effectiveMs);
		_log.Debug("Actuation  key={I}  peak={D}  rate={R:F1}  effMs={E:F1}  vel={V}",
				   keyIndex, key.PeakDepth, key.DepthRate, effectiveMs, velocity);
		SendNote(ref key, keyIndex, velocity);
	}

	private void SendNote(ref KeyTrackState key, int i, int velocity)
	{
		if (!_keyMap.TryGetValue(i, out var notes)) return;

		int baseNote = _isShiftHeld ? notes.Shifted : notes.Normal;
		int finalNote = Math.Clamp(baseNote + (_octOffset * SemitonesPerOctave) + _keyOffset, 0, 127);

		if (key.NoteOn)
		{
			Send(NoteOff(key.ActiveNote));
			_log.Debug("Note Off (retrigger)  note={Note}  key={Key}", key.ActiveNote, i);
		}

		key.ActiveNote = finalNote;
		key.NoteOn     = true;

		Send(NoteOn(finalNote, velocity));
		_log.Debug("Note On  note={Note}  vel={Vel}  key={Key}", finalNote, velocity, i);
	}

	private void EndNote(ref KeyTrackState key, int keyIndex)
	{
		if (!key.NoteOn) return;
		Send(NoteOff(key.ActiveNote));
		_log.Debug("Note Off  note={Note}  key={Key}", key.ActiveNote, keyIndex);
		key.NoteOn = false;
	}

	private void AllNotesOff()
	{
		for (int i = 0; i < 127; i++)
		{
			ref var key = ref _keys[i];
			if (!key.NoteOn) continue;
			Send(NoteOff(key.ActiveNote));
			key.NoteOn = false;
		}
	}

	private int CalcVelocity(double deltaMs)
	{
		if (deltaMs <= _velocityMinMs) return _maxVelocity;
		if (deltaMs >= _velocityMaxMs) return _minVelocity;
		// Log scale so equal perceived press-speed ratios span equal t intervals;
		// (1-t)^gamma is steep at fast end (velocity range for hard hits) and
		// flat at slow end (many soft levels cluster near MinVelocity).
		double t = (Math.Log(deltaMs) - _velocityLogMinMs) / _velocityLogRangeMs; // 0=fast, 1=slow
		double curved = Math.Pow(1.0 - t, _velocityGamma);
		return Math.Clamp(
			(int)Math.Round(_minVelocity + curved * (_maxVelocity - _minVelocity)),
			_minVelocity, _maxVelocity);
	}

	private void Send(int message) => _midiOut.Send(message);

	private static int NoteOn(int note, int velocity) => 0x90 | (note << 8) | (velocity << 16);
	private static int NoteOff(int note) => 0x80 | (note << 8);
}
