namespace DDSharp;

/// <summary>
/// Raised once per complete poll cycle. Useful for monitoring polling rate.
/// </summary>
public sealed class PolledEventArgs : EventArgs
{
	/// <summary>Wall-clock time taken for the last full poll cycle (read + event dispatch).</summary>
	public TimeSpan Elapsed { get; }

	/// <summary>Estimated polling rate derived from <see cref="Elapsed"/>. Capped at 9999 Hz.</summary>
	public int Hz => Elapsed.TotalMilliseconds >= 0.05 ? Math.Min((int)(1000.0 / Elapsed.TotalMilliseconds), 9999) : 9999;

	internal PolledEventArgs(TimeSpan elapsed) { Elapsed = elapsed; }
}
