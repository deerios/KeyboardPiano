using DrunkDeer.Protocol;
using KeyboardPiano;
using NAudio.Midi;
using Serilog;

Log.Logger = new LoggerConfiguration()
	.MinimumLevel.Debug()
	.WriteTo.Console(outputTemplate:
		"[{Timestamp:HH:mm:ss} {Level:u3}] [{SourceContext}] {Message:lj}{NewLine}{Exception}")
	.CreateLogger();

var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
	e.Cancel = true;
	Log.Information("Shutdown requested.");
	cts.Cancel();
};

Log.Information("DrunkDeer MIDI Adapter - press Ctrl+C to exit.");

using var keyboard = KeyboardSession.OpenFirst();
// Restore a sane firmware actuation point (min-actuation persists across sessions and
// makes keys feel hair-trigger; we don't need it since KeyHeightChanged fires for all depths).
keyboard.SetActuationPoint(2.0f);

var config = new KeyboardMidiConfig();

MidiOut? midiOut = null;
for (int i = 0; i < MidiOut.NumberOfDevices; i++)
{
	var info = MidiOut.DeviceInfo(i);
	Log.Debug("MIDI out [{Index}]: {Name}", i, info.ProductName);
	if (info.ProductName.Contains("DDMidiPort"))
		midiOut = new MidiOut(i);
}

if (midiOut == null)
{
	Log.Fatal("DDMidiPort not found. Create a virtual MIDI port named 'DDMidiPort' (e.g. with loopMIDI) and restart.");
	return;
}

using var adapter = new MidiAdapter(keyboard, config, midiOut);
adapter.Run(cts.Token);

Log.CloseAndFlush();
