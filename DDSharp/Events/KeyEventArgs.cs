namespace DDSharp;

/// <summary>
/// Raised when a key is pressed past <see cref="DDKeyboardInterface.PressThreshold"/>
/// or released below <see cref="DDKeyboardInterface.ReleaseThreshold"/>.
/// </summary>
public sealed class KeyEventArgs : EventArgs
{
	/// <summary>Zero-based index of the key within the keyboard layout array.</summary>
	public int Index { get; }

	/// <summary>
	/// Human-readable key name from the keyboard layout (e.g. <c>"A"</c>, <c>"SPACE"</c>, <c>"ARR_UP"</c>).
	/// Empty string for unassigned positions.
	/// </summary>
	public string Name { get; }

	/// <summary>Raw travel depth at the moment this event fired. Higher = pressed deeper.</summary>
	public sbyte Height { get; }

	internal KeyEventArgs(int index, string name, sbyte height)
	{ Index = index; Name = name; Height = height; }
}
