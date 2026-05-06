using DDSharp;
using NAudio.Midi;
using Serilog;
using System.Diagnostics;

namespace KeyboardPiano;

public class MidiAdapter : IDisposable
{
	private static readonly ILogger _log = Log.ForContext<MidiAdapter>();

	private readonly DDKeyboardInterface _keyboard;
	private readonly MidiOut _midiOut;

	private int _releasePoint, _actuationPoint;
	private int _minVelocity, _maxVelocity;
	private double _velocityCurve, _velocityMinMs, _velocityMaxMs, _samePollMs;
	private double _ghostCancelMs;
	private int _featherTapMinDepth;
	private readonly Dictionary<int, (int Normal, int Shifted)> _keyMap;

	private readonly int _baseReleasePoint, _baseActuationPoint;
	private readonly int _baseMinVelocity, _baseMaxVelocity;
	private readonly double _baseVelocityCurve, _baseVelocityMinMs, _baseVelocityMaxMs, _baseSamePollMs;
	private readonly double _baseGhostCancelMs;

	private readonly KeyboardMidiConfig.ClutchFeatureConfig _clutchFeature;
	private bool _clutchHeld;
	private int _clutchTransposeOffset;
	private readonly int[] _activeDoubleNotes = new int[127];

	private readonly int _keyPedal, _keyOctUp, _keyOctDown, _keyUp, _keyDown;
	private readonly int _keyShiftL, _keyShiftR;
	private readonly int _keySpace;

	private enum KeyState { Idle, Tracking, Active }

	private readonly KeyState[] _keyState = new KeyState[127];
	private readonly long[] _startTicks = new long[127];
	private readonly long[] _actuationTicks = new long[127];
	private readonly long[] _peakTicks = new long[127];
	private readonly sbyte[] _peakDepth = new sbyte[127];
	private readonly bool[] _noteOn = new bool[127];
	private readonly int[] _activeNotes = new int[127];

	private bool _isShiftHeld;
	private int _octOffset;
	private int _keyOffset;

	public MidiAdapter(DDKeyboardInterface keyboard, KeyboardMidiConfig config, MidiOut midiOut,
					   PedalMonitor? brakePedal = null, PedalMonitor? clutchPedal = null,
					   bool brakeReverseSustain = false)
	{
		_keyboard = keyboard;
		_midiOut  = midiOut;

		(_releasePoint, _actuationPoint)                               = config.GetThresholds();
		(_minVelocity, _maxVelocity)                                   = config.GetVelocityClampValues();
		(_velocityMinMs, _velocityMaxMs, _velocityCurve, _samePollMs)  = config.GetVelocityTimingMs();
		_ghostCancelMs                                                  = config.GetGhostCancelMs();
		_featherTapMinDepth                                             = config.GetFeatherTapMinDepth();
		_keyMap                                                         = config.BuildMidiKeyMap(keyboard);

		(_baseReleasePoint, _baseActuationPoint) = (_releasePoint, _actuationPoint);
		(_baseMinVelocity, _baseMaxVelocity)     = (_minVelocity, _maxVelocity);
		(_baseVelocityMinMs, _baseVelocityMaxMs, _baseVelocityCurve, _baseSamePollMs) = (_velocityMinMs, _velocityMaxMs, _velocityCurve, _samePollMs);
		_baseGhostCancelMs                       = _ghostCancelMs;

		_clutchFeature = config.GetClutchFeatureConfig();

		_keyPedal   = config.GetKeyBindByName(keyboard, "Pedal");
		_keyOctUp   = config.GetKeyBindByName(keyboard, "OctUp");
		_keyOctDown = config.GetKeyBindByName(keyboard, "OctDown");
		_keyUp      = config.GetKeyBindByName(keyboard, "KeyUp");
		_keyDown    = config.GetKeyBindByName(keyboard, "KeyDown");
		_keyShiftL  = keyboard.GetKeyIndex(DDKey.LeftShift);
		_keyShiftR  = keyboard.GetKeyIndex(DDKey.RightShift);
		_keySpace   = keyboard.GetKeyIndex(DDKey.Space);

		_keyboard.KeyHeightChanged += OnKeyHeightChanged;
		_keyboard.Polled           += OnPolled;

		if (brakePedal != null)
		{
			(int downVal, int upVal) = brakeReverseSustain ? (0, 127) : (127, 0);
			brakePedal.PedalDown += (_, _) => { Send(ControlChange(64, downVal)); _log.Information("Sustain: {V} (brake)", downVal); };
			brakePedal.PedalUp   += (_, _) => { Send(ControlChange(64, upVal)); _log.Information("Sustain: {V} (brake)", upVal); };
		}

		if (clutchPedal != null)
		{
			clutchPedal.PedalDown += (_, _) => OnClutchDown();
			clutchPedal.PedalUp   += (_, _) => OnClutchUp();
		}
	}

	private void OnClutchDown()
	{
		if (!_clutchFeature.Enabled) return;
		_clutchHeld = true;

		if (_clutchFeature.FeatherMode is { } fm)
		{
			_minVelocity    = fm.MinVelocity;
			_maxVelocity    = fm.MaxVelocity;
			_ghostCancelMs  = fm.GhostCancelMs;
			_velocityMinMs  = fm.VelocityMinMs;
			_velocityMaxMs  = fm.VelocityMaxMs;
			_velocityCurve  = fm.VelocityCurve;
			_samePollMs     = fm.SamePollMs;
			_releasePoint   = fm.ReleasePoint;
			_actuationPoint = fm.ActuationPoint;
			_log.Information("Clutch down: FeatherMode  vel={Min}-{Max}  curve={Curve}  msWindow={MinMs}-{MaxMs}  actuation={Act}",
				_minVelocity, _maxVelocity, _velocityCurve, _velocityMinMs, _velocityMaxMs, _actuationPoint);
		}

		if (_clutchFeature.TransposeMode is { } tp)
		{
			_clutchTransposeOffset = tp.Semitones;
			RetuneLiveNotes(tp.Semitones);
			_log.Information("Clutch down: TransposeMode +{S} semitones", tp.Semitones);
		}
	}

	private void OnClutchUp()
	{
		_clutchHeld = false;
		if (_clutchFeature.TransposeMode != null && _clutchTransposeOffset != 0)
		{
			RetuneLiveNotes(-_clutchTransposeOffset);
			_clutchTransposeOffset = 0;
			_log.Information("Clutch up: TransposeMode cleared");
		}

		_minVelocity    = _baseMinVelocity;
		_maxVelocity    = _baseMaxVelocity;
		_ghostCancelMs  = _baseGhostCancelMs;
		_velocityMinMs  = _baseVelocityMinMs;
		_velocityMaxMs  = _baseVelocityMaxMs;
		_velocityCurve  = _baseVelocityCurve;
		_samePollMs     = _baseSamePollMs;
		_releasePoint   = _baseReleasePoint;
		_actuationPoint = _baseActuationPoint;
		_log.Information("Clutch up: base settings restored  vel={Min}-{Max}  curve={Curve}  actuation={Act}",
			_minVelocity, _maxVelocity, _velocityCurve, _actuationPoint);
	}

	// Shifts all currently-held notes by deltaSemitones without retriggering.
	private void RetuneLiveNotes(int deltaSemitones)
	{
		for (int i = 0; i < 127; i++)
		{
			if (!_noteOn[i]) continue;
			int oldNote = _activeNotes[i];
			int newNote = Math.Clamp(oldNote + deltaSemitones, 0, 127);
			Send(NoteOff(oldNote));
			Send(NoteOn(newNote, 64));
			_activeNotes[i] = newNote;

			if (_activeDoubleNotes[i] != 0)
			{
				Send(NoteOff(_activeDoubleNotes[i]));
				int newDouble = Math.Clamp(_activeDoubleNotes[i] + deltaSemitones, 0, 127);
				Send(NoteOn(newDouble, 64));
				_activeDoubleNotes[i] = newDouble;
			}
		}
	}

	public void Run(CancellationToken ct = default)
	{
		_log.Information("MIDI adapter running.");
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

	private int CalculateVelocityFromTime(double deltaMs)
	{
		if (deltaMs <= _velocityMinMs) return _maxVelocity;
		if (deltaMs >= _velocityMaxMs) return _minVelocity;
		double normalized = 1.0 - (deltaMs - _velocityMinMs) / (_velocityMaxMs - _velocityMinMs);
		double curved = 1.0 - Math.Pow(1.0 - normalized, 1.0 / _velocityCurve);
		int velocity = (int)Math.Round(_minVelocity + curved * (_maxVelocity - _minVelocity));
		_log.Debug("  time  deltaMs={D:F1}  norm={N:F2}  curved={C:F2}  vel={V}", deltaMs, normalized, curved, velocity);
		return Math.Clamp(velocity, _minVelocity, _maxVelocity);
	}

	private void OnKeyHeightChanged(object? sender, KeyHeightChangedEventArgs e)
	{
		int i = e.Index;
		if (i < 0 || i >= 127) return;

		long currentTicks = Stopwatch.GetTimestamp();

		switch (_keyState[i])
		{
			case KeyState.Idle:
				if (e.Height > 3 && e.PreviousHeight <= 3)
				{
					_startTicks[i] = currentTicks;
					_peakTicks[i]  = currentTicks;
					_peakDepth[i]  = e.Height;
					_keyState[i]   = KeyState.Tracking;
					HandleControlKey(i);

					_log.Debug("Key move  key={I}  h={H}", i, e.Height);

					if (e.Height >= _actuationPoint)
					{
						_actuationTicks[i] = currentTicks;
						int velocity = CalculateVelocityFromTime(_samePollMs);
						_log.Debug("Same-poll Actuation key={I}  h={H}  vel={V}", i, e.Height, velocity);
						SendNote(i, velocity);
						_keyState[i] = KeyState.Active;
					}
				}
				break;

			case KeyState.Tracking:
				if (e.Height < _releasePoint)
				{
					if (i == _keyShiftL || i == _keyShiftR) _isShiftHeld = false;
					if (_peakDepth[i] >= _releasePoint)
					{
						if (_peakDepth[i] < _featherTapMinDepth)
						{
							_log.Debug("Feather tap suppressed (wiggle)  key={I}  peak={Peak}  minDepth={Min}", i, _peakDepth[i], _featherTapMinDepth);
						}
						else
						{
							double deltaMs = (double)(_peakTicks[i] - _startTicks[i]) / Stopwatch.Frequency * 1000.0;
							if (deltaMs < _samePollMs) deltaMs = _samePollMs;
							double depthFraction = (double)_peakDepth[i] / _actuationPoint;
							double scaledDeltaMs = deltaMs / depthFraction;
							int velocity = CalculateVelocityFromTime(scaledDeltaMs);
							_log.Debug("Feather tap  key={I}  peak={Peak}  deltaMs={D:F1}  scaled={S:F1}  vel={V}", i, _peakDepth[i], deltaMs, scaledDeltaMs, velocity);
							SendNote(i, velocity);
							EndNote(i);
						}
						_keyState[i] = KeyState.Idle;
					}
					else
					{
						_log.Debug("Pre-Actuation release suppressed  key={I}", i);
						_keyState[i] = KeyState.Idle;
					}
				}
				else
				{
					if (e.Height > _peakDepth[i])
					{
						_peakDepth[i] = e.Height;
						_peakTicks[i] = currentTicks;
					}

					if (e.Height >= _actuationPoint && e.PreviousHeight < _actuationPoint)
					{
						_actuationTicks[i] = currentTicks;
						double deltaMs = (double)(currentTicks - _startTicks[i]) / Stopwatch.Frequency * 1000.0;
						int velocity = CalculateVelocityFromTime(deltaMs);
						_log.Debug("Actuation reached  key={I}  deltaMs={D:F1}  vel={V}", i, deltaMs, velocity);
						SendNote(i, velocity);
						_keyState[i] = KeyState.Active;
					}
				}
				break;

			case KeyState.Active:
				if (e.Height < _releasePoint && e.PreviousHeight >= _releasePoint)
				{
					if (i == _keyShiftL || i == _keyShiftR) _isShiftHeld = false;
					double heldMs = (double)(currentTicks - _actuationTicks[i]) / Stopwatch.Frequency * 1000.0;
					if (heldMs < _ghostCancelMs)
						_log.Debug("Ghost cancel  key={I}  held={Ms:F1}ms", i, heldMs);
					EndNote(i);
					_keyState[i] = KeyState.Idle;
				}
				break;
		}
	}

	private void OnPolled(object? sender, PolledEventArgs e)
	{
		Console.Title = $"DrunkDeer MIDI Adapter  ( {e.Hz} Hz | Oct: {_octOffset} | Key: {_keyOffset} )";
	}

	private void HandleControlKey(int i)
	{
		if (i == _keyShiftL || i == _keyShiftR) { _isShiftHeld = true; return; }
		if (i == _keyPedal) { Send(ControlChange(64, 127)); return; }
		if (i == _keyUp) { _keyOffset++; AllNotesOff(); _log.Debug("Key offset: {V}", _keyOffset); return; }
		if (i == _keyDown) { _keyOffset--; AllNotesOff(); _log.Debug("Key offset: {V}", _keyOffset); return; }
		if (i == _keyOctUp)
		{
			if (_octOffset >= 2) return;
			_octOffset++;
			AllNotesOff();
			_log.Debug("Octave offset: {V}", _octOffset);
			return;
		}
		if (i == _keyOctDown)
		{
			if (_octOffset <= -2) return;
			_octOffset--;
			AllNotesOff();
			_log.Debug("Octave offset: {V}", _octOffset);
		}
	}

	private void SendNote(int i, int velocity)
	{
		if (i == _keySpace || i == _keyPedal) return;
		if (!_keyMap.TryGetValue(i, out var notes)) return;

		int baseNote = _isShiftHeld ? notes.Shifted : notes.Normal;
		int finalNote = Math.Clamp(baseNote + (_octOffset * 12) + _keyOffset + _clutchTransposeOffset, 0, 127);

		if (_noteOn[i])
		{
			Send(NoteOff(_activeNotes[i]));
			_log.Debug("Note Off (retrigger cleanup)  note={Note}  key={Key}", _activeNotes[i], i);
		}

		_activeNotes[i] = finalNote;
		_noteOn[i]      = true;

		Send(NoteOn(finalNote, velocity));
		_log.Debug("Note On   note={Note}  vel={Vel}  key={Key}  shift={Shift}", finalNote, velocity, i, _isShiftHeld);

		if (_clutchHeld && _clutchFeature.OctaveDoubleMode is { } od)
		{
			int doubleNote = Math.Clamp(finalNote + od.Semitones, 0, 127);
			int doubleVel = Math.Clamp((int)(velocity * od.VelocityScale), 1, 127);
			_activeDoubleNotes[i] = doubleNote;
			Send(NoteOn(doubleNote, doubleVel));
			_log.Debug("OctDouble On  note={Note}  vel={Vel}", doubleNote, doubleVel);
		}
		else
		{
			_activeDoubleNotes[i] = 0;
		}
	}

	private void EndNote(int i)
	{
		if (i == _keySpace || i == _keyPedal) return;
		if (!_noteOn[i]) return;

		int finalNote = _activeNotes[i];
		_noteOn[i]    = false;

		Send(NoteOff(finalNote));
		_log.Debug("Note Off  note={Note}  key={Key}", finalNote, i);

		if (_activeDoubleNotes[i] != 0)
		{
			Send(NoteOff(_activeDoubleNotes[i]));
			_log.Debug("OctDouble Off  note={Note}", _activeDoubleNotes[i]);
			_activeDoubleNotes[i] = 0;
		}
	}

	private void AllNotesOff()
	{
		for (int n = 0; n <= 127; n++)
			Send(NoteOff(n));
		Array.Clear(_activeDoubleNotes);
	}

	private void Send(int message) => _midiOut.Send(message);

	private static int NoteOn(int note, int velocity) => 0x90 | (note << 8) | (velocity << 16);
	private static int NoteOff(int note) => 0x80 | (note << 8);
	private static int ControlChange(int cc, int val) => 0xB0 | (cc << 8) | (val << 16);
}
