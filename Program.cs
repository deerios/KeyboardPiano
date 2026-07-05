using DrunkDeer.Protocol;
using KeyboardPiano;
using Commons.Music.Midi;
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

// Where to send MIDI: CLI arg > DDMIDI_PORT env > "MidiPort" in config.
//  - empty or "virtual"  -> create our own virtual source port (default). It shows up
//    as a MIDI *input* to everything else (synths, browsers/Web MIDI, DAWs); connect
//    whatever you like to it. This is the cross-platform equivalent of loopMIDI.
//  - anything else        -> connect straight to an existing output port whose name
//    contains that text (e.g. "FLUID Synth" to drive a running synth directly).
string? target = args.FirstOrDefault()
			  ?? Environment.GetEnvironmentVariable("DDMIDI_PORT");
if (string.IsNullOrWhiteSpace(target)) target = config.GetMidiPortName();
if (string.IsNullOrWhiteSpace(target)) target = null;

var access = MidiAccessManager.Default;
bool useVirtual = target is null || target.Equals("virtual", StringComparison.OrdinalIgnoreCase);

IMidiOutput output;
if (useVirtual)
{
	const string virtualPortName = "DrunkDeer";
	if (access is not IMidiAccess2 access2
		|| !access2.ExtensionManager.Supports<MidiPortCreatorExtension>())
	{
		Log.Fatal("This MIDI backend can't create virtual ports. Name an existing synth port "
				+ "instead (CLI arg, DDMIDI_PORT env, or \"MidiPort\" in config.json).");
		return;
	}
	var creator = access2.ExtensionManager.GetInstance<MidiPortCreatorExtension>();
	output = creator.CreateVirtualInputSender(new MidiPortCreatorExtension.PortCreatorContext
	{
		ApplicationName = "KeyboardPiano",
		PortName        = virtualPortName,
	});
	Log.Information("Created virtual MIDI port \"{Name}\". Connect a synth to it for sound "
				  + "(e.g. `aconnect KeyboardPiano '{Synth}'` or a patchbay like qpwgraph), and select "
				  + "it as the MIDI input on your site/DAW.", virtualPortName, "FLUID Synth");
}
else
{
	var allOutputs = access.Outputs.ToList();
	foreach (var d in allOutputs) Log.Debug("MIDI out: {Name}", d.Name);

	// The kernel's always-present "Midi Through" loopback makes no sound; ignore it.
	var candidates = allOutputs
		.Where(d => !d.Name.Contains("Midi Through", StringComparison.OrdinalIgnoreCase))
		.ToList();

	var port = candidates.FirstOrDefault(d => d.Name.Contains(target!, StringComparison.OrdinalIgnoreCase));
	if (port is null)
	{
		if (candidates.Count == 0)
			Log.Fatal("No MIDI output matching \"{Target}\" (no output ports at all). Start a synth first, "
					+ "or leave MidiPort empty to create a virtual port.", target);
		else
			Log.Fatal("No MIDI output matching \"{Target}\". Available: {Names}",
					  target, string.Join(", ", candidates.Select(d => d.Name)));
		return;
	}

	Log.Information("Sending MIDI to \"{Name}\".", port.Name);
	output = access.OpenOutputAsync(port.Id).GetAwaiter().GetResult();
}

using var midiOut = new MidiSink(output);
using var adapter = new MidiAdapter(keyboard, config, midiOut);
adapter.Run(cts.Token);

Log.CloseAndFlush();
