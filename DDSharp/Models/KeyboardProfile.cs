namespace DDSharp;

/// <summary>
/// A snapshot of the three key-point profiles (actuation, downstroke, upstroke) for a
/// connected keyboard, together with the keyboard identity and firmware metadata.
/// </summary>
/// <remarks>
/// Create an instance via <see cref="Load"/> immediately after connecting.
/// On <see cref="PrecisionMode.High"/> keyboards all values are read from firmware.
/// On <see cref="PrecisionMode.Standard"/> keyboards values reflect the last-written
/// in-memory state (the firmware exposes no pull command for <c>0xB6</c>).
/// Call <see cref="Refresh"/> to re-synchronise after writing new key-point values.
/// </remarks>
public sealed class KeyboardProfile
{
	private readonly DDKeyboardInterface _kb;
	private double[] _actuation;
	private double[] _downstroke;
	private double[] _upstroke;

	// ── Identity ──────────────────────────────────────────────────────────────

	/// <summary>Human-readable keyboard model name, e.g. <c>"G75"</c>.</summary>
	public string KeyboardName { get; }

	/// <summary>Firmware version string, e.g. <c>"0.09"</c>. <c>null</c> if unavailable.</summary>
	public string? Firmware { get; }

	/// <summary>Key-point encoding mode used by this keyboard.</summary>
	public PrecisionMode Precision { get; }

	/// <summary>UTC time at which the profile data was last read from the keyboard.</summary>
	public DateTime LoadedAt { get; private set; }

	// ── Factory ───────────────────────────────────────────────────────────────

	/// <summary>
	/// Reads the key-point profile from <paramref name="kb"/> and returns a new
	/// <see cref="KeyboardProfile"/>, or <c>null</c> if the firmware query fails.
	/// </summary>
	public static KeyboardProfile? Load(DDKeyboardInterface kb)
	{
		var raw = kb.ReadKeyPointProfile();
		return raw is null ? null : new KeyboardProfile(kb, raw);
	}

	private KeyboardProfile(DDKeyboardInterface kb, KeyPointProfile raw)
	{
		_kb          = kb;
		_actuation   = raw.Actuation;
		_downstroke  = raw.Downstroke;
		_upstroke    = raw.Upstroke;
		KeyboardName = kb.KeyboardName;
		Firmware     = kb.Info?.FirmwareVersion;
		Precision    = kb.Precision;
		LoadedAt     = DateTime.UtcNow;
	}

	// ── Refresh ───────────────────────────────────────────────────────────────

	/// <summary>
	/// Re-reads the key-point profile from the firmware and updates this instance.
	/// </summary>
	/// <returns><c>true</c> on success; <c>false</c> if the firmware query fails
	/// (the previous values are preserved).</returns>
	public bool Refresh()
	{
		var raw = _kb.ReadKeyPointProfile();
		if (raw is null) return false;
		_actuation  = raw.Actuation;
		_downstroke = raw.Downstroke;
		_upstroke   = raw.Upstroke;
		LoadedAt    = DateTime.UtcNow;
		return true;
	}

	// ── Per-key access - by DDKey ─────────────────────────────────────────────

	/// <summary>
	/// Returns the actuation depth for <paramref name="key"/> in mm,
	/// or <see cref="double.NaN"/> if the key is not present on this keyboard model.
	/// </summary>
	public double GetActuation(DDKey key)
	{
		int i = _kb.GetKeyIndex(key);
		return i >= 0 ? _actuation[i] : double.NaN;
	}

	/// <summary>
	/// Returns the downstroke distance for <paramref name="key"/> in mm,
	/// or <see cref="double.NaN"/> if the key is not present on this keyboard model.
	/// </summary>
	public double GetDownstroke(DDKey key)
	{
		int i = _kb.GetKeyIndex(key);
		return i >= 0 ? _downstroke[i] : double.NaN;
	}

	/// <summary>
	/// Returns the upstroke distance for <paramref name="key"/> in mm,
	/// or <see cref="double.NaN"/> if the key is not present on this keyboard model.
	/// </summary>
	public double GetUpstroke(DDKey key)
	{
		int i = _kb.GetKeyIndex(key);
		return i >= 0 ? _upstroke[i] : double.NaN;
	}

	// ── Per-key access - by layout index ─────────────────────────────────────

	/// <summary>
	/// Returns the actuation depth at <paramref name="index"/> in mm,
	/// or <see cref="double.NaN"/> if the index is out of range.
	/// </summary>
	public double GetActuation(int index) =>
		(uint)index < (uint)_actuation.Length ? _actuation[index] : double.NaN;

	/// <summary>
	/// Returns the downstroke distance at <paramref name="index"/> in mm,
	/// or <see cref="double.NaN"/> if the index is out of range.
	/// </summary>
	public double GetDownstroke(int index) =>
		(uint)index < (uint)_downstroke.Length ? _downstroke[index] : double.NaN;

	/// <summary>
	/// Returns the upstroke distance at <paramref name="index"/> in mm,
	/// or <see cref="double.NaN"/> if the index is out of range.
	/// </summary>
	public double GetUpstroke(int index) =>
		(uint)index < (uint)_upstroke.Length ? _upstroke[index] : double.NaN;

	// ── Enumeration ───────────────────────────────────────────────────────────

	/// <summary>
	/// Enumerates one <see cref="KeyProfileEntry"/> per occupied layout slot
	/// (slots with an empty token are physical gaps and are skipped).
	/// </summary>
	public IEnumerable<KeyProfileEntry> Keys
	{
		get
		{
			for (int i = 0; i < DDKeyboardInterface.ProfileKeyCount; i++)
			{
				string token = i < _kb.Layout.Length ? _kb.Layout[i] : string.Empty;
				if (string.IsNullOrEmpty(token)) continue;

				yield return new KeyProfileEntry(
					Index: i,
					Token: token,
					Key: _kb.GetKeyAtIndex(i),
					ActuationMm: Math.Round(_actuation[i], 3),
					DownstrokeMm: Math.Round(_downstroke[i], 3),
					UpstrokeMm: Math.Round(_upstroke[i], 3));
			}
		}
	}
}

/// <summary>
/// Immutable snapshot of all three key-point values for a single physical key position.
/// </summary>
/// <param name="Index">Zero-based layout index (0-125).</param>
/// <param name="Token">Raw layout token, e.g. <c>"SPACE"</c>, <c>"ARR_UP"</c>.</param>
/// <param name="Key">
/// Resolved <see cref="DDKey"/> enum value, or <c>null</c> for unnamed positions (<c>u*</c>).
/// </param>
/// <param name="ActuationMm">Actuation depth in millimetres.</param>
/// <param name="DownstrokeMm">Downstroke distance in millimetres.</param>
/// <param name="UpstrokeMm">Upstroke distance in millimetres.</param>
public record KeyProfileEntry(
	int Index,
	string Token,
	DDKey? Key,
	double ActuationMm,
	double DownstrokeMm,
	double UpstrokeMm);
