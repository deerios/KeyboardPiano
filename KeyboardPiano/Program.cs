using DDSharp;
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

Log.Information("DrunkDeer MIDI Adapter - press Enter to connect, Ctrl+C to exit.");
DDKeyboardInterface.EnumerateInterfaces();
Console.ReadLine();

using var keyboard = new DDKeyboardInterface();
keyboard.SetMaxSensitivity();
await Task.Delay(500, cts.Token);

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

var (brakeDevice, brakeAxis, brakeThreshold, brakeInverted, brakeReverseSustain) = config.GetBrakePedalConfig();
var (clutchDevice, clutchAxis, clutchThreshold, clutchInverted) = config.GetClutchPedalConfig();

using var brakePedal = PedalMonitor.TryCreate(brakeDevice, brakeAxis, brakeThreshold, brakeInverted);
using var clutchPedal = PedalMonitor.TryCreate(clutchDevice, clutchAxis, clutchThreshold, clutchInverted);

brakePedal?.Start(cts.Token);
clutchPedal?.Start(cts.Token);

using var adapter = new MidiAdapter(keyboard, config, midiOut, brakePedal, clutchPedal, brakeReverseSustain);
adapter.Run(cts.Token);

Log.CloseAndFlush();
