namespace DDSharp;

/// <summary>
/// Key-point command and encoding mode for this keyboard model.
/// </summary>
public enum PrecisionMode
{
	/// <summary>
	/// Command <c>0xB6</c> - 1 byte per key, 0.1 mm resolution.
	/// Used by A75, G65, G60, and most original models.
	/// </summary>
	Standard,

	/// <summary>
	/// Command <c>0xFD</c> - 2 bytes per key (little-endian, ×200 scale), 0.005 mm resolution.
	/// Used by G75, A75 Pro, A75 Ultra, A75 Master, and newer high-precision variants.
	/// </summary>
	High,
}
