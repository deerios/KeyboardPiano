using Commons.Music.Midi;

namespace KeyboardPiano;

/// <summary>
/// Cross-platform MIDI output shim. <see cref="MidiAdapter"/> emits packed short
/// messages in the Win32 layout it was originally written against
/// (status | data1&lt;&lt;8 | data2&lt;&lt;16); this decodes them into raw MIDI bytes
/// and forwards them to a managed-midi output port (ALSA on Linux).
/// </summary>
public sealed class MidiSink : IDisposable
{
	private readonly IMidiOutput _output;

	public MidiSink(IMidiOutput output) => _output = output;

	public string Name => _output.Details.Name;

	public void Send(int shortMessage)
	{
		// A fresh 3-byte array per send keeps this safe if the engine ever raises
		// note events from more than one thread; MIDI event rates make it free.
		var msg = new byte[3];
		msg[0] = (byte)(shortMessage & 0xFF);          // status (command | channel)
		msg[1] = (byte)((shortMessage >> 8)  & 0x7F);  // data1 (note)
		msg[2] = (byte)((shortMessage >> 16) & 0x7F);  // data2 (velocity)
		_output.Send(msg, 0, msg.Length, 0);
	}

	public void Dispose()
	{
		// Best-effort close: ALSA's port disconnect can report "Operation not permitted"
		// on shutdown (notably for the Midi Through loopback). Never let that surface
		// from a using/finally and mask a clean exit.
		try { _output.CloseAsync().GetAwaiter().GetResult(); }
		catch { /* already tearing down */ }
	}
}
