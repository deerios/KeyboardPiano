using DrunkDeer.Protocol;
using System.Text.Json;

namespace KeyboardPiano;

/// <summary>
/// All tunables for the piano engine. Distances are in millimetres of key travel
/// (converted to raw sensor units per the connected model's precision mode),
/// speeds in mm/s, times in milliseconds.
/// </summary>
public sealed record PianoSettings(
	int MinVelocity,
	int MaxVelocity,
	double VelocityGamma,
	double SlowestMmPerSec,
	double FastestMmPerSec,
	bool SendReleaseVelocity,
	double FastReleaseMmPerSec,
	double DeadzoneMm,
	double ReleasePointMm,
	double ActuationPointMm,
	double VelocityMeasureStartMm,
	double ReleaseLiftMm,
	double RetriggerPressMm,
	double JitterMm,
	double CooldownMs,
	double ActuationWindowMs,
	double MinNoteMs,
	double StallGapMs);

public class KeyboardMidiConfig
{
	private readonly JsonDocument _doc;

	public KeyboardMidiConfig(string path = "config.json")
	{
		_doc = JsonDocument.Parse(File.ReadAllText(path));
	}

	public PianoSettings GetPianoSettings() => new(
		MinVelocity:            I("MinVelocity", 1),
		MaxVelocity:            I("MaxVelocity", 127),
		VelocityGamma:          D("VelocityGamma", 1.15),
		SlowestMmPerSec:        D("SlowestMmPerSec", 5.0),
		FastestMmPerSec:        D("FastestMmPerSec", 400.0),
		SendReleaseVelocity:    B("SendReleaseVelocity", true),
		FastReleaseMmPerSec:    D("FastReleaseMmPerSec", 60.0),
		DeadzoneMm:             D("DeadzoneMm", 0.05),
		ReleasePointMm:         D("ReleasePointMm", 0.45),
		ActuationPointMm:       D("ActuationPointMm", 1.10),
		VelocityMeasureStartMm: D("VelocityMeasureStartMm", 0.40),
		ReleaseLiftMm:          D("ReleaseLiftMm", 0.60),
		RetriggerPressMm:       D("RetriggerPressMm", 0.30),
		JitterMm:               D("JitterMm", 0.02),
		CooldownMs:             D("CooldownMs", 35.0),
		ActuationWindowMs:      D("ActuationWindowMs", 5.0),
		MinNoteMs:              D("MinNoteMs", 45.0),
		StallGapMs:             D("StallGapMs", 25.0));

	public (string Device, string Axis, int ThresholdPct, bool Inverted, bool ReverseSustain) GetBrakePedalConfig()
	{
		var s = Settings;
		return (
			s.GetProperty("BrakeDevice").GetString()       ?? "",
			s.GetProperty("BrakeAxis").GetString()         ?? "Z",
			s.GetProperty("BrakeThreshold").GetInt32(),
			s.GetProperty("BrakeInverted").GetBoolean(),
			s.GetProperty("BrakeReverseSustain").GetBoolean()
		);
	}

	public (string Device, string Axis, int ThresholdPct, bool Inverted) GetClutchPedalConfig()
	{
		var s = Settings;
		return (
			s.GetProperty("ClutchDevice").GetString()  ?? "",
			s.GetProperty("ClutchAxis").GetString()    ?? "Z",
			s.GetProperty("ClutchThreshold").GetInt32(),
			s.GetProperty("ClutchInverted").GetBoolean()
		);
	}

	// Config value must be a DDKey enum name (e.g. "ArrowUp", "Space", "LeftShift").
	public int GetKeyBindByName(KeyboardSession session, string configKey)
	{
		string target = Settings.GetProperty(configKey).GetString()!;
		if (!Enum.TryParse<DDKey>(target, ignoreCase: true, out var ddKey))
		{
			Console.Error.WriteLine($"Key binding '{configKey}' ({target}) is not a valid DDKey name.");
			return -1;
		}
		if (!session.TryGetKeyIndex(ddKey, out int index))
		{
			Console.Error.WriteLine($"Key binding '{configKey}' ({target}) not found on this keyboard model.");
			return -1;
		}
		return index;
	}

	public Dictionary<int, (int Normal, int Shifted)> BuildMidiKeyMap(KeyboardSession session)
	{
		var map = new Dictionary<int, (int Normal, int Shifted)>();
		var keymap = _doc.RootElement.GetProperty("Keymap");

		foreach (var ddKey in session.GetKeys())
		{
			int i = session.GetKeyIndex(ddKey);
			string token = DDKeyToToken(ddKey);
			string shifted = ShiftedToken(token);

			int normal = 0, shift = 0;
			if (keymap.TryGetProperty(token.ToLower(), out var n)) normal = n.GetInt32();
			if (keymap.TryGetProperty(token.ToUpper(), out var s)) shift  = s.GetInt32();
			if (shift == 0 && keymap.TryGetProperty(shifted, out var sym)) shift = sym.GetInt32();

			if (normal != 0 || shift != 0)
				map[i] = (normal, shift != 0 ? shift : normal);
		}

		return map;
	}

	private JsonElement Settings => _doc.RootElement.GetProperty("Settings");

	private double D(string name, double def) =>
		Settings.TryGetProperty(name, out var p) ? p.GetDouble() : def;

	private int I(string name, int def) =>
		Settings.TryGetProperty(name, out var p) ? p.GetInt32() : def;

	private bool B(string name, bool def) =>
		Settings.TryGetProperty(name, out var p) ? p.GetBoolean() : def;

	// DDKey.D0-D9 -> "0"-"9"; DDKey.Q -> "Q"; others pass through (won't match keymap, skipped).
	private static string DDKeyToToken(DDKey key)
	{
		string name = key.ToString();
		if (name.Length == 2 && name[0] == 'D' && char.IsDigit(name[1]))
			return name[1..];
		return name;
	}

	private static string ShiftedToken(string key) => key switch
	{
		"1" => "!",
		"2" => "@",
		"3" => "#",
		"4" => "$",
		"5" => "%",
		"6" => "^",
		"7" => "&",
		"8" => "*",
		"9" => "(",
		"0" => ")",
		_ => key.ToUpper()
	};
}
