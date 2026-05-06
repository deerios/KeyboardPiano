namespace DDSharp;

/// <summary>Firmware and feature-flag snapshot read from the keyboard on connect.</summary>
public record KeyboardInfo(
	/// <summary>Firmware version string, e.g. "0.09".</summary>
	string FirmwareVersion,

	/// <summary>Whether Rapid Trigger is currently enabled on the keyboard.</summary>
	bool RapidTriggerEnabled,

	/// <summary>Whether Rapid Trigger Plus is currently enabled on the keyboard.</summary>
	bool RapidTriggerPlusEnabled,

	/// <summary>Current turbo repeat value reported by the firmware.</summary>
	int TurboValue,

	/// <summary>Current last-win value reported by the firmware.</summary>
	int LastWinValue);
