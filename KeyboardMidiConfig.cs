using DrunkDeer.Protocol;
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

	public (double MinMs, double MaxMs, double Curve) GetVelocityTimingMs()
	{
		var s = Settings;
		double min = s.TryGetProperty("VelocityMinMs", out var a) ? a.GetDouble() : 5.0;
		double max = s.TryGetProperty("VelocityMaxMs", out var b) ? b.GetDouble() : 80.0;
		double curve = s.TryGetProperty("VelocityCurve", out var c) ? c.GetDouble() : 3.5;
		return (min, max, curve);
	}

	public (int ReleasePoint, int ActuationPoint) GetThresholds()
	{
		var s = Settings;
		return (s.GetProperty("ReleasePoint").GetInt32(),
				s.GetProperty("ActuationPoint").GetInt32());
	}

	public double GetCooldownMs()
	{
		return Settings.TryGetProperty("CooldownMs", out var prop) ? prop.GetDouble() : 60.0;
	}

	public double GetActuationWindowMs()
	{
		return Settings.TryGetProperty("ActuationWindowMs", out var prop) ? prop.GetDouble() : 8.0;
	}

	public double GetDepthFactor()
	{
		return Settings.TryGetProperty("DepthFactor", out var prop) ? prop.GetDouble() : 0.04;
	}

	public double GetRateSaturation()
	{
		return Settings.TryGetProperty("RateSaturation", out var prop) ? prop.GetDouble() : 0.0;
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
