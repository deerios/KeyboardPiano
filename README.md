# Keyboard Piano

Turns a DrunkDeer analog keyboard into a velocity-sensitive MIDI piano. Press speed is measured
from the analog key travel (two-crossing flight timing, like a real keybed) and mapped to MIDI
velocity 1–127; lift speed is sent as release velocity.

Requires a virtual MIDI port named `DDMidiPort` (e.g. created with loopMIDI).

## Configuration

All settings live in `config.json` under `Settings`. Distances are in **millimetres of key
travel**, speeds in **mm/s**, times in **milliseconds** — they are converted automatically to the
connected model's sensor resolution (Standard / Kun / HighPrecision). Every setting has a built-in
default, so a partial `Settings` block is fine.

| Setting | Default | Meaning |
|---|---|---|
| `MinVelocity` / `MaxVelocity` | 1 / 127 | MIDI velocity clamp |
| `SlowestMmPerSec` | 5 | press speed mapped to `MinVelocity` |
| `FastestMmPerSec` | 400 | press speed mapped to `MaxVelocity` |
| `VelocityGamma` | 1.15 | >1 softer mids, <1 louder mids |
| `ActuationPointMm` | 1.10 | depth at which a note fires |
| `ReleasePointMm` | 0.45 | depth below which a note always ends |
| `ReleaseLiftMm` | 0.60 | rise off the note's peak for a fast-lift cut |
| `FastReleaseMmPerSec` | 60 | upward speed required for a fast-lift cut |
| `RetriggerPressMm` | 0.30 | re-press distance for rapid repeats without full release |
| `MinNoteMs` | 45 | minimum sounding length of a note |
| `SendReleaseVelocity` | true | encode lift speed in NoteOff (64 = neutral when off) |
| `VelocityMeasureStartMm` | 0.40 | start "contact" of the velocity measurement |
| `DeadzoneMm` | 0.05 | rest-position noise floor |
| `CooldownMs` | 35 | post-NoteOff blackout (absorbs spring-back) |
| `ActuationWindowMs` | 5 | post-actuation window catching late acceleration |
| `JitterMm` / `StallGapMs` | 0.02 / 25 | measurement robustness; rarely need changing |

`OctUp` / `OctDown` / `KeyUp` / `KeyDown` bind octave/transpose controls to DDKey names.
`Keymap` maps key tokens to MIDI note numbers; the uppercase/symbol variant of a token is the
note played while Shift is held.

See `../REVIEW.md` for the full design analysis and a tuning guide.

This project currently only supports DrunkDeer keyboards. If you'd like to add support for your magnetic keyboard, please create a pull request.
