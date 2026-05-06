namespace DDSharp;

/// <summary>
/// Which point in the key travel to configure.
/// </summary>
/// <remarks>
/// These values are the **write subcommand** bytes used in <c>0xB6</c> and <c>0xFD</c> write
/// packets. They are <em>not</em> the same as the <c>dataType</c> bytes used in
/// <c>0xFD</c> pull requests - see <see cref="DDKeyboardInterface.ReadKeyPointProfile"/>.
/// </remarks>
public enum KeyPointType
{
	/// <summary>The depth at which a keypress is registered. Write subcommand: <c>0x01</c>.</summary>
	ActuationPoint = 0x01,

	/// <summary>The depth the key must reach on the way down before a chord can fire. Write subcommand: <c>0x04</c>.</summary>
	Downstroke = 0x04,

	/// <summary>The depth the key must rise to before it is considered released. Write subcommand: <c>0x05</c>.</summary>
	Upstroke = 0x05,
}
