using DDSharp;
using System.Text.Json;

namespace KeyboardPiano;

public class KeyboardMidiConfig
{
	private readonly JsonDocument _doc;

	public KeyboardMidiConfig(string path = "config.json")
	{
		_doc = JsonDocument.Parse(File.ReadAllText(path));
	}

	public (int Min, int Max) GetVelocityClampValues()
	{
		var s = Settings;
		return (s.GetProperty("MinVelocity").GetInt32(),
				s.GetProperty("MaxVelocity").GetInt32());
	}

	public (double MinMs, double MaxMs, double Curve, double SamePollMs) GetVelocityTimingMs()
	{
		var s = Settings;
		double min = s.TryGetProperty("VelocityMinMs", out var a) ? a.GetDouble() : 5.0;
		double max = s.TryGetProperty("VelocityMaxMs", out var b) ? b.GetDouble() : 80.0;
		double curve = s.TryGetProperty("VelocityCurve", out var c) ? c.GetDouble() : 3.5;
		double samePollMs = s.TryGetProperty("SamePollMs", out var d) ? d.GetDouble() : min + 1.0;
		return (min, max, curve, samePollMs);
	}

	public (int ReleasePoint, int ActuationPoint) GetThresholds()
	{
		var s = Settings;
		return (s.GetProperty("ReleasePoint").GetInt32(),
				s.GetProperty("ActuationPoint").GetInt32());
	}

	public double GetGhostCancelMs()
	{
		return Settings.TryGetProperty("GhostCancelMs", out var prop) ? prop.GetDouble() : 30.0;
	}

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

	public record ClutchFeatherSettings(
		int MinVelocity, int MaxVelocity,
		double GhostCancelMs,
		double VelocityMinMs, double VelocityMaxMs, double VelocityCurve, double SamePollMs,
		int ReleasePoint, int ActuationPoint);

	public record ClutchOctaveDoubleSettings(int Semitones, double VelocityScale);

	public record ClutchTransposeSettings(int Semitones);

	public record ClutchFeatureConfig(
		bool Enabled,
		ClutchFeatherSettings? FeatherMode,
		ClutchOctaveDoubleSettings? OctaveDoubleMode,
		ClutchTransposeSettings? TransposeMode);

	public ClutchFeatureConfig GetClutchFeatureConfig()
	{
		var s = Settings;
		var none = new ClutchFeatureConfig(false, null, null, null);

		if (!s.TryGetProperty("ClutchFeature", out var cf))
			return none;

		bool enabled = cf.TryGetProperty("Enabled", out var en) && en.GetBoolean();

		ClutchFeatherSettings? featherMode = null;
		if (cf.TryGetProperty("FeatherMode", out var fm) &&
			fm.TryGetProperty("Enabled", out var fmEn) && fmEn.GetBoolean())
		{
			featherMode = new ClutchFeatherSettings(
				MinVelocity: fm.TryGetProperty("MinVelocity", out var a) ? a.GetInt32() : 5,
				MaxVelocity: fm.TryGetProperty("MaxVelocity", out var b) ? b.GetInt32() : 42,
				GhostCancelMs: fm.TryGetProperty("GhostCancelMs", out var c) ? c.GetDouble() : 20.0,
				VelocityMinMs: fm.TryGetProperty("VelocityMinMs", out var d) ? d.GetDouble() : 8.0,
				VelocityMaxMs: fm.TryGetProperty("VelocityMaxMs", out var e) ? e.GetDouble() : 120.0,
				VelocityCurve: fm.TryGetProperty("VelocityCurve", out var f) ? f.GetDouble() : 1.8,
				SamePollMs: fm.TryGetProperty("SamePollMs", out var g) ? g.GetDouble() : 10.0,
				ReleasePoint: fm.TryGetProperty("ReleasePoint", out var h) ? h.GetInt32() : 3,
				ActuationPoint: fm.TryGetProperty("ActuationPoint", out var i) ? i.GetInt32() : 28);
		}

		ClutchOctaveDoubleSettings? octaveDoubleMode = null;
		if (cf.TryGetProperty("OctaveDoubleMode", out var od) &&
			od.TryGetProperty("Enabled", out var odEn) && odEn.GetBoolean())
		{
			octaveDoubleMode = new ClutchOctaveDoubleSettings(
				Semitones: od.TryGetProperty("Semitones", out var a) ? a.GetInt32() : 12,
				VelocityScale: od.TryGetProperty("VelocityScale", out var b) ? b.GetDouble() : 0.6);
		}

		ClutchTransposeSettings? transposeMode = null;
		if (cf.TryGetProperty("TransposeMode", out var tp) &&
			tp.TryGetProperty("Enabled", out var tpEn) && tpEn.GetBoolean())
		{
			transposeMode = new ClutchTransposeSettings(
				Semitones: tp.TryGetProperty("Semitones", out var a) ? a.GetInt32() : -12);
		}

		return new ClutchFeatureConfig(enabled, featherMode, octaveDoubleMode, transposeMode);
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

	public int GetKeyBindByName(DDKeyboardInterface keyboard, string key)
	{
		string target = Settings.GetProperty(key).GetString()!;
		for (int i = 0; i < keyboard.Layout.Length; i++)
		{
			if (keyboard.Layout[i] == target)
				return i;
		}
		Console.Error.WriteLine($"Key binding '{key}' ({target}) not found in layout.");
		return -1;
	}

	public Dictionary<int, (int Normal, int Shifted)> BuildMidiKeyMap(DDKeyboardInterface keyboard)
	{
		var map = new Dictionary<int, (int Normal, int Shifted)>();
		var keymap = _doc.RootElement.GetProperty("Keymap");

		for (int i = 0; i < keyboard.Layout.Length; i++)
		{
			string key = keyboard.Layout[i];
			string shifted = ShiftedToken(key);

			int normal = 0, shift = 0;
			if (keymap.TryGetProperty(key.ToLower(), out var n)) normal = n.GetInt32();
			if (keymap.TryGetProperty(key.ToUpper(), out var s)) shift  = s.GetInt32();
			if (shift == 0 && keymap.TryGetProperty(shifted, out var sym)) shift = sym.GetInt32();

			if (normal != 0 || shift != 0)
				map[i] = (normal, shift != 0 ? shift : normal);
		}

		return map;
	}

	private JsonElement Settings => _doc.RootElement.GetProperty("Settings");

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
