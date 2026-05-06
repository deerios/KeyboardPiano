namespace DDSharp;

/// <summary>
/// Raised whenever a key's travel depth changes between two consecutive polls.
/// </summary>
public sealed class KeyHeightChangedEventArgs : EventArgs
{
	/// <summary>Zero-based index of the key within the keyboard layout array.</summary>
	public int Index { get; }

	/// <summary>Human-readable key name (e.g. <c>"A"</c>, <c>"SPACE"</c>). Empty for unassigned positions.</summary>
	public string Name { get; }

	/// <summary>Travel depth measured in the previous poll.</summary>
	public sbyte PreviousHeight { get; }

	/// <summary>Travel depth measured in the current poll.</summary>
	public sbyte Height { get; }

	internal KeyHeightChangedEventArgs(int index, string name, sbyte prev, sbyte height)
	{ Index = index; Name = name; PreviousHeight = prev; Height = height; }
}
