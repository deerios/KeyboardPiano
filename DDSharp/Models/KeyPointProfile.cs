namespace DDSharp;

/// <summary>
/// Snapshot of the three key-point profiles read from the keyboard (all values in mm).
/// Each array contains exactly <see cref="DDKeyboardInterface.ProfileKeyCount"/> (126) elements
/// ordered by layout index.
/// </summary>
public record KeyPointProfile(
	/// <summary>Actuation depth per key (0.2-3.8 mm).</summary>
	double[] Actuation,
	/// <summary>Downstroke distance per key (0.0-3.6 mm).</summary>
	double[] Downstroke,
	/// <summary>Upstroke distance per key (0.0-3.6 mm).</summary>
	double[] Upstroke)
{
	/// <summary>
	/// Returns the actuation depth for the given key, or <see cref="double.NaN"/> if the
	/// key is not present on this keyboard model.
	/// </summary>
	public double GetActuation(DDKey key, DDKeyboardInterface kb)
	{
		int idx = kb.GetKeyIndex(key);
		return idx >= 0 && idx < Actuation.Length ? Actuation[idx] : double.NaN;
	}

	/// <summary>
	/// Returns the downstroke distance for the given key, or <see cref="double.NaN"/> if
	/// the key is not present on this keyboard model.
	/// </summary>
	public double GetDownstroke(DDKey key, DDKeyboardInterface kb)
	{
		int idx = kb.GetKeyIndex(key);
		return idx >= 0 && idx < Downstroke.Length ? Downstroke[idx] : double.NaN;
	}

	/// <summary>
	/// Returns the upstroke distance for the given key, or <see cref="double.NaN"/> if
	/// the key is not present on this keyboard model.
	/// </summary>
	public double GetUpstroke(DDKey key, DDKeyboardInterface kb)
	{
		int idx = kb.GetKeyIndex(key);
		return idx >= 0 && idx < Upstroke.Length ? Upstroke[idx] : double.NaN;
	}
}
