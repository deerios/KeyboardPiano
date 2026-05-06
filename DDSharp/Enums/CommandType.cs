namespace DDSharp;

/// <summary>Built-in read commands supported by <see cref="DDKeyboardInterface.SendCommandSync"/>.</summary>
public enum CommandType
{
	/// <summary>Requests the keyboard model identifier and firmware info.</summary>
	RequestId = 0x01,

	/// <summary>Requests the current physical height of all keys.</summary>
	RequestKeys = 0x02,
}
