using System.Diagnostics;
using DrunkDeer.Protocol;
using Serilog;

namespace KeyboardPiano;

public class MidiAdapter : IDisposable
{
	private static readonly ILogger _log = Log.ForContext<MidiAdapter>();

	private const int SemitonesPerOctave = 12;
	private const int KeySlots = 127;

	private readonly KeyboardSession _keyboard;
	private readonly MidiSink _midiOut;

	// All config distances are given in millimetres and converted once to the raw
	// sensor units of the connected model (0.1 / 0.01 / 0.005 mm per unit).
	private readonly double _unitsPerMm;
	private readonly double _maxDepthU;
	private readonly double _deadzoneU;
	private readonly double _releaseU;
	private readonly double _actuationU;
	private readonly double _measureStartU;
	private readonly double _releaseLiftU;
	private readonly double _retriggerU;
	private readonly double _jitterU;

	private readonly double _cooldownMs;
	private readonly double _windowMs;
	private readonly double _minNoteMs;
	private readonly double _stallGapMs;

	private readonly int _minVelocity;
	private readonly int _maxVelocity;
	private readonly double _slowestMmPerSec;
	private readonly double _fastestMmPerSec;
	private readonly double _logSpeedRange;
	private readonly double _gamma;
	private readonly bool _sendReleaseVelocity;
	private readonly double _fastReleaseMmPerSec;

	private readonly Dictionary<int, (int Normal, int Shifted)> _keyMap;

	private readonly int _keyOctUp, _keyOctDown, _keyUp, _keyDown;
	private readonly int _keyShiftL, _keyShiftR;

	private bool _shiftLDown, _shiftRDown;
	private bool _ctlOctUp, _ctlOctDown, _ctlKeyUp, _ctlKeyDown;
	private bool _isShiftHeld;
	private int _octOffset;
	private int _keyOffset;

	// Duration of one poll frame; refined at runtime from PolledEventArgs.
	private double _frameMs = 4.0;

	// Idle      - key at rest, no note
	// Pressing  - key off rest; tracking the downstroke, below the fire threshold
	// Actuating - crossed the fire threshold; short window to observe peak/late acceleration
	// Held      - note sounding
	// Cooling   - post-NoteOff blackout to absorb mechanical spring-back
	private enum KeyState { Idle, Pressing, Actuating, Held, Cooling }

	private struct KeyTrack
	{
		public KeyState State;
		public bool NoteOn;
		public int ActiveNote;
		public double NoteOnT;

		// Last observed sample (events only fire when depth changes).
		public short PrevDepth;
		public double PrevT;

		// Start of the current downstroke: the most recent local depth minimum.
		public short AnchorDepth;
		public double AnchorT;

		// Interpolated time the stroke crossed the velocity-measurement start depth.
		public bool HasStartCross;
		public double StartCrossT;

		// Actuation window bookkeeping.
		public double FireDepth;
		public double ActCrossT;
		public double WindowEndT;
		public short PeakDepth;
		public double FlightSpeed;   // mm/s, measured from anchor/start-cross to the fire crossing

		// Release tracking.
		public short HeldPeak;
		public double UpSpeed;       // smoothed upward speed, mm/s

		public double CoolEndT;

		// NoteOff deferred by MinNoteMs so ultra-short taps still sound.
		public bool HasPendingOff;
		public int PendingOffNote;
		public int PendingOffVel;
		public double PendingOffDueT;
	}

	private readonly KeyTrack[] _keys = new KeyTrack[KeySlots];

	public MidiAdapter(KeyboardSession keyboard, KeyboardMidiConfig config, MidiSink midiOut)
	{
		_keyboard = keyboard;
		_midiOut  = midiOut;

		_unitsPerMm = keyboard.PrecisionMode switch
		{
			PrecisionMode.HighPrecision => 200.0,
			PrecisionMode.Kun           => 100.0,
			_                           => 10.0,
		};
		_maxDepthU = keyboard.MaxDepthMm * _unitsPerMm;

		var s = config.GetPianoSettings();

		if (s.ActuationPointMm >= keyboard.MaxDepthMm)
			_log.Warning("ActuationPointMm {A} >= keyboard max travel {M} mm; keys may never actuate.",
						 s.ActuationPointMm, keyboard.MaxDepthMm);

		_deadzoneU     = s.DeadzoneMm             * _unitsPerMm;
		_releaseU      = s.ReleasePointMm         * _unitsPerMm;
		_actuationU    = s.ActuationPointMm       * _unitsPerMm;
		_measureStartU = s.VelocityMeasureStartMm * _unitsPerMm;
		_releaseLiftU  = s.ReleaseLiftMm          * _unitsPerMm;
		_retriggerU    = s.RetriggerPressMm       * _unitsPerMm;
		_jitterU       = s.JitterMm               * _unitsPerMm;

		_cooldownMs = s.CooldownMs;
		_windowMs   = s.ActuationWindowMs;
		_minNoteMs  = s.MinNoteMs;
		_stallGapMs = s.StallGapMs;

		_minVelocity         = s.MinVelocity;
		_maxVelocity         = s.MaxVelocity;
		_slowestMmPerSec     = s.SlowestMmPerSec;
		_fastestMmPerSec     = s.FastestMmPerSec;
		_logSpeedRange       = Math.Log(_fastestMmPerSec / _slowestMmPerSec);
		_gamma               = s.VelocityGamma;
		_sendReleaseVelocity = s.SendReleaseVelocity;
		_fastReleaseMmPerSec = s.FastReleaseMmPerSec;

		_keyMap = config.BuildMidiKeyMap(keyboard);

		_keyOctUp   = config.GetKeyBindByName(keyboard, "OctUp");
		_keyOctDown = config.GetKeyBindByName(keyboard, "OctDown");
		_keyUp      = config.GetKeyBindByName(keyboard, "KeyUp");
		_keyDown    = config.GetKeyBindByName(keyboard, "KeyDown");
		_keyShiftL  = keyboard.GetKeyIndex(DDKey.LeftShift);
		_keyShiftR  = keyboard.GetKeyIndex(DDKey.RightShift);

		_log.Information("Precision={P} ({U} units/mm, max {Max:F0}u). Actuation={A:F0}u Release={R:F0}u Deadzone={D:F0}u",
						 keyboard.PrecisionMode, _unitsPerMm, _maxDepthU, _actuationU, _releaseU, _deadzoneU);

		_keyboard.KeyHeightChanged += OnKeyHeightChanged;
		_keyboard.Polled           += OnPolled;
	}

	public void Run(CancellationToken ct = default)
	{
		_log.Information("MIDI adapter running.");

		// Put all keys in Cooling for the first 300ms so that keys already above the
		// deadzone at rest (elevated magnetic resting positions) don't fire stray notes.
		double startupEnd = NowMs() + 300;
		for (int i = 0; i < KeySlots; i++)
		{
			_keys[i].State    = KeyState.Cooling;
			_keys[i].CoolEndT = startupEnd;
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
		AllNotesOff();
	}

	private static double NowMs() => Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;

	private void OnKeyHeightChanged(object? sender, KeyHeightChangedEventArgs e)
	{
		int index = e.Index;
		if ((uint)index >= KeySlots) return;

		// Shift and the octave/transpose keys never make sound; give them a simple
		// hysteresis latch instead of the note state machine, so they can't get stuck
		// half-engaged. Shift engages on a light touch (sharps need to be quick to reach);
		// the octave/transpose keys require a deliberate full actuation.
		if (index == _keyShiftL) { Latch(ref _shiftLDown, e.Height, _releaseU, _deadzoneU); _isShiftHeld = _shiftLDown || _shiftRDown; return; }
		if (index == _keyShiftR) { Latch(ref _shiftRDown, e.Height, _releaseU, _deadzoneU); _isShiftHeld = _shiftLDown || _shiftRDown; return; }
		if (index == _keyOctUp)   { if (Latch(ref _ctlOctUp,   e.Height, _actuationU, _releaseU)) HandleControlActuation(index); return; }
		if (index == _keyOctDown) { if (Latch(ref _ctlOctDown, e.Height, _actuationU, _releaseU)) HandleControlActuation(index); return; }
		if (index == _keyUp)      { if (Latch(ref _ctlKeyUp,   e.Height, _actuationU, _releaseU)) HandleControlActuation(index); return; }
		if (index == _keyDown)    { if (Latch(ref _ctlKeyDown, e.Height, _actuationU, _releaseU)) HandleControlActuation(index); return; }

		ProcessKey(index, e.Height, NowMs());
	}

	// Returns true on the engage edge.
	private static bool Latch(ref bool down, short depth, double engageU, double releaseU)
	{
		if (!down && depth >= engageU) { down = true; return true; }
		if (down && depth < releaseU) down = false;
		return false;
	}

	private void OnPolled(object? sender, PolledEventArgs e)
	{
		double now = NowMs();

		double ms = e.Elapsed.TotalMilliseconds;
		if (ms is > 0.2 and < 50) _frameMs = 0.7 * _frameMs + 0.3 * ms;

		// Time-based transitions must not depend on further key movement: a key that
		// bottoms out and holds perfectly still stops producing height events.
		for (int i = 0; i < KeySlots; i++)
		{
			ref var key = ref _keys[i];
			if (key.State == KeyState.Actuating && now >= key.WindowEndT)
				FireNote(ref key, i, now);
			if (key.HasPendingOff && now >= key.PendingOffDueT)
			{
				Send(NoteOff(key.PendingOffNote, key.PendingOffVel));
				_log.Debug("Note Off (deferred)  note={Note}  relVel={V}  key={Key}", key.PendingOffNote, key.PendingOffVel, i);
				key.HasPendingOff = false;
			}
		}

		Console.Title = $"DrunkDeer MIDI Adapter  ( {e.Hz} Hz | Oct: {_octOffset} | Key: {_keyOffset} )";
	}

	private void ProcessKey(int keyIndex, short depth, double now)
	{
		ref var key = ref _keys[keyIndex];

		// If the key sat still, height events stopped flowing, so the previous sample's
		// timestamp is stale. Motion can only have resumed within the last poll frame.
		double prevT = key.PrevT;
		if (now - prevT > _stallGapMs) prevT = now - _frameMs;

		bool repeat;
		do
		{
			repeat = false;
			switch (key.State)
			{
				case KeyState.Idle:
					if (depth > _deadzoneU)
					{
						key.AnchorDepth   = key.PrevDepth;
						key.AnchorT       = prevT;
						key.HasStartCross = false;
						key.State         = KeyState.Pressing;
						repeat = true;
					}
					break;

				case KeyState.Pressing:
				{
					if (depth <= _deadzoneU)
					{
						key.State = KeyState.Idle;
						break;
					}
					if (depth < key.PrevDepth - _jitterU)
					{
						// Genuine retreat: the next downstroke starts from here.
						key.AnchorDepth = depth;
						key.AnchorT     = now;
						if (depth < _measureStartU) key.HasStartCross = false;
						break;
					}
					if (depth <= key.PrevDepth) break; // jitter-scale dip; ignore

					if (!key.HasStartCross && depth >= _measureStartU && key.PrevDepth < _measureStartU)
					{
						key.StartCrossT   = InterpolateCrossing(key.PrevDepth, prevT, depth, now, _measureStartU);
						key.HasStartCross = true;
					}

					// Fire at the actuation point, or - when the stroke started deep
					// (fast repeat without a full release) - a fixed re-press distance
					// below the local minimum, rapid-trigger style.
					double fireU = Math.Max(_actuationU, key.AnchorDepth + _retriggerU);
					fireU = Math.Min(fireU, _maxDepthU - 2);
					if (depth >= fireU)
					{
						double actCrossT = InterpolateCrossing(key.PrevDepth, prevT, depth, now, fireU);

						// Measure average stroke speed over the longest clean span we have:
						// from the start-depth crossing when the stroke began above it,
						// otherwise from the downstroke anchor.
						double fromDepth, fromT;
						if (key.HasStartCross && key.AnchorDepth < _measureStartU)
						{
							fromDepth = _measureStartU;
							fromT     = key.StartCrossT;
						}
						else
						{
							fromDepth = key.AnchorDepth;
							fromT     = key.AnchorT;
						}

						double distMm = (fireU - fromDepth) / _unitsPerMm;
						double dtSec  = (actCrossT - fromT) / 1000.0;
						key.FlightSpeed = distMm > 0 && dtSec > 0.0005 ? distMm / dtSec : 0;

						key.FireDepth  = fireU;
						key.ActCrossT  = actCrossT;
						key.WindowEndT = now + _windowMs;
						key.PeakDepth  = depth;
						key.State      = KeyState.Actuating;
					}
					break;
				}

				case KeyState.Actuating:
					if (depth > key.PeakDepth) key.PeakDepth = depth;
					if (now >= key.WindowEndT || depth < _releaseU)
					{
						FireNote(ref key, keyIndex, now);
						repeat = depth < _releaseU; // let Held see the release immediately
					}
					break;

				case KeyState.Held:
				{
					if (depth > key.HeldPeak) key.HeldPeak = depth;

					if (depth < key.PrevDepth)
					{
						double instUp = (key.PrevDepth - depth) / _unitsPerMm
									  / Math.Max(now - prevT, 0.25) * 1000.0;
						key.UpSpeed = key.UpSpeed <= 0 ? instUp : 0.5 * key.UpSpeed + 0.5 * instUp;
					}
					else if (depth > key.PrevDepth)
					{
						key.UpSpeed = 0;
					}

					// Two ways to end a note:
					//  - absolute: the key is nearly back at rest (slow drift included);
					//  - fast lift: a deliberate, quick rise off the note's peak. The speed
					//    gate keeps slow finger relaxation from chopping sustained notes.
					bool absoluteRelease = depth < _releaseU;
					bool fastLift = (key.HeldPeak - depth) >= _releaseLiftU
								 && key.UpSpeed >= _fastReleaseMmPerSec;
					if (absoluteRelease || fastLift)
					{
						EndNote(ref key, keyIndex, now);
						key.AnchorDepth = depth;
						key.AnchorT     = now;
						key.CoolEndT    = now + _cooldownMs;
						key.State       = KeyState.Cooling;
					}
					break;
				}

				case KeyState.Cooling:
					// Keep trailing the local minimum so a re-press that begins during
					// the cooldown still gets a correct velocity measurement.
					if (depth < key.AnchorDepth)
					{
						key.AnchorDepth = depth;
						key.AnchorT     = now;
					}
					if (now >= key.CoolEndT)
					{
						key.HasStartCross = false;
						key.State = depth > _deadzoneU ? KeyState.Pressing : KeyState.Idle;
						repeat = key.State == KeyState.Pressing;
					}
					break;
			}
		} while (repeat);

		key.PrevDepth = depth;
		key.PrevT     = now;
	}

	private static double InterpolateCrossing(double d0, double t0, double d1, double t1, double threshold)
	{
		if (d1 <= d0) return t1;
		double f = Math.Clamp((threshold - d0) / (d1 - d0), 0.0, 1.0);
		return t0 + f * (t1 - t0);
	}

	private void HandleControlActuation(int keyIndex)
	{
		if      (keyIndex == _keyOctUp   && _octOffset < 2)  { _octOffset++; AllNotesOff(); _log.Debug("Oct: {V}", _octOffset); }
		else if (keyIndex == _keyOctDown && _octOffset > -2) { _octOffset--; AllNotesOff(); _log.Debug("Oct: {V}", _octOffset); }
		else if (keyIndex == _keyUp)   { _keyOffset++; AllNotesOff(); _log.Debug("Key: {V}", _keyOffset); }
		else if (keyIndex == _keyDown) { _keyOffset--; AllNotesOff(); _log.Debug("Key: {V}", _keyOffset); }
	}

	private void FireNote(ref KeyTrack key, int keyIndex, double now)
	{
		// A hard hit keeps accelerating after the crossing and slams into the bottom;
		// the window speed catches that (and covers strokes with no usable flight span).
		double windowDt    = now - key.ActCrossT;
		double windowSpeed = 0;
		if (windowDt >= 1.5 && key.PeakDepth > key.FireDepth)
			windowSpeed = (key.PeakDepth - key.FireDepth) / _unitsPerMm / (windowDt / 1000.0);

		double speed   = Math.Max(key.FlightSpeed, windowSpeed);
		int   velocity = MapSpeedToVelocity(speed);

		_log.Debug("Actuation  key={I}  peak={D}  flight={F:F0}mm/s  window={W:F0}mm/s  vel={V}",
				   keyIndex, key.PeakDepth, key.FlightSpeed, windowSpeed, velocity);

		SendNote(ref key, keyIndex, velocity);
		key.NoteOnT  = now;
		key.HeldPeak = key.PeakDepth;
		key.UpSpeed  = 0;
		key.State    = KeyState.Held;
	}

	// Log-domain speed mapping: equal ratios of press speed give equal velocity steps,
	// which is how loudness is perceived. Gamma > 1 softens the low-mid range.
	private int MapSpeedToVelocity(double mmPerSec)
	{
		if (mmPerSec <= _slowestMmPerSec) return _minVelocity;
		if (mmPerSec >= _fastestMmPerSec) return _maxVelocity;
		double t      = Math.Log(mmPerSec / _slowestMmPerSec) / _logSpeedRange;
		double curved = Math.Pow(t, _gamma);
		return Math.Clamp(
			(int)Math.Round(_minVelocity + curved * (_maxVelocity - _minVelocity)),
			_minVelocity, _maxVelocity);
	}

	private void SendNote(ref KeyTrack key, int i, int velocity)
	{
		if (!_keyMap.TryGetValue(i, out var notes)) return;

		int baseNote  = _isShiftHeld ? notes.Shifted : notes.Normal;
		int finalNote = Math.Clamp(baseNote + (_octOffset * SemitonesPerOctave) + _keyOffset, 0, 127);

		if (key.HasPendingOff)
		{
			Send(NoteOff(key.PendingOffNote, key.PendingOffVel));
			key.HasPendingOff = false;
		}
		if (key.NoteOn)
		{
			Send(NoteOff(key.ActiveNote, 64));
			_log.Debug("Note Off (retrigger)  note={Note}  key={Key}", key.ActiveNote, i);
		}

		key.ActiveNote = finalNote;
		key.NoteOn     = true;

		Send(NoteOn(finalNote, velocity));
		_log.Debug("Note On  note={Note}  vel={Vel}  key={Key}", finalNote, velocity, i);
	}

	private void EndNote(ref KeyTrack key, int keyIndex, double now)
	{
		if (!key.NoteOn) return;

		// Release velocity mirrors the lift speed so synths that honour it can shape
		// the damper: slow lift = gentle long release, snap lift = fast cut. 64 = neutral.
		int relVel = _sendReleaseVelocity && key.UpSpeed > 0 ? MapSpeedToVelocity(key.UpSpeed) : 64;

		double due = key.NoteOnT + _minNoteMs;
		if (now < due)
		{
			key.HasPendingOff  = true;
			key.PendingOffNote = key.ActiveNote;
			key.PendingOffVel  = relVel;
			key.PendingOffDueT = due;
			_log.Debug("Note Off (scheduled +{Ms:F0}ms)  note={Note}  key={Key}", due - now, key.ActiveNote, keyIndex);
		}
		else
		{
			Send(NoteOff(key.ActiveNote, relVel));
			_log.Debug("Note Off  note={Note}  relVel={V}  key={Key}", key.ActiveNote, relVel, keyIndex);
		}
		key.NoteOn = false;
	}

	private void AllNotesOff()
	{
		for (int i = 0; i < KeySlots; i++)
		{
			ref var key = ref _keys[i];
			if (key.HasPendingOff)
			{
				Send(NoteOff(key.PendingOffNote, key.PendingOffVel));
				key.HasPendingOff = false;
			}
			if (!key.NoteOn) continue;
			Send(NoteOff(key.ActiveNote, 64));
			key.NoteOn = false;
		}
	}

	private void Send(int message) => _midiOut.Send(message);

	private static int NoteOn(int note, int velocity)  => 0x90 | (note << 8) | (velocity << 16);
	private static int NoteOff(int note, int velocity) => 0x80 | (note << 8) | (velocity << 16);
}
