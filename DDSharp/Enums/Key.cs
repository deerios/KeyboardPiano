namespace DDSharp;

/// <summary>
/// Logical key identifier, independent of keyboard model or physical layout index.
/// </summary>
/// <remarks>
/// Pass one or more values to the targeted overloads of
/// <see cref="DDKeyboardInterface.SetActuationPoint(double, DDKey[])"/>,
/// <see cref="DDKeyboardInterface.SetDownstrokePoint(double, DDKey[])"/>, and
/// <see cref="DDKeyboardInterface.SetUpstrokePoint(double, DDKey[])"/> to adjust
/// individual keys without affecting the rest of the profile.
/// </remarks>
/// <example>
/// <code>
/// // Set WASD to maximum sensitivity, leave everything else at 2.0 mm.
/// kb.SetActuationPoint(0.2, DDKey.W, DDKey.A, DDKey.S, DDKey.D);
/// </code>
/// </example>
public enum DDKey
{
	Escape,
	F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12,
	PrintScreen,

	Backtick,
	D1, D2, D3, D4, D5, D6, D7, D8, D9, D0,
	Minus, Equal, Backspace,

	Tab,
	Q, W, E, R, T, Y, U, I, O, P,
	LeftBracket, RightBracket,
	Backslash,

	CapsLock,
	A, S, D, F, G, H, J, K, L,
	Semicolon, Quote,
	IsoHash,
	Enter,

	LeftShift,
	IsoBackslash,
	Z, X, C, V, B, N, M,
	Comma, Period,
	Slash,
	RightShift,

	LeftCtrl, LeftWin, LeftAlt,
	Space,
	RightAlt, Fn1, Fn2,
	Menu,

	Insert, Delete, Home, End, PageUp, PageDown,

	ArrowUp, ArrowLeft, ArrowDown, ArrowRight,

	NumLock,
	Numpad0, Numpad1, Numpad2, Numpad3, Numpad4,
	Numpad5, Numpad6, Numpad7, Numpad8, Numpad9,
	NumpadDecimal,
}

/// <summary>
/// Maps each <see cref="DDKey"/> value to the token string used in keyboard layout arrays.
/// </summary>
internal static class KeyLayoutNames
{
	internal static readonly Dictionary<DDKey, string> Names = new()
	{
		[DDKey.Escape]        = "ESC",
		[DDKey.F1]            = "F1",
		[DDKey.F2]            = "F2",
		[DDKey.F3]            = "F3",
		[DDKey.F4]            = "F4",
		[DDKey.F5]            = "F5",
		[DDKey.F6]            = "F6",
		[DDKey.F7]            = "F7",
		[DDKey.F8]            = "F8",
		[DDKey.F9]            = "F9",
		[DDKey.F10]           = "F10",
		[DDKey.F11]           = "F11",
		[DDKey.F12]           = "F12",
		[DDKey.PrintScreen]   = "PRINT",

		[DDKey.Backtick]      = "SWUNG",
		[DDKey.D1]            = "1",
		[DDKey.D2]            = "2",
		[DDKey.D3]            = "3",
		[DDKey.D4]            = "4",
		[DDKey.D5]            = "5",
		[DDKey.D6]            = "6",
		[DDKey.D7]            = "7",
		[DDKey.D8]            = "8",
		[DDKey.D9]            = "9",
		[DDKey.D0]            = "0",
		[DDKey.Minus]         = "MINUS",
		[DDKey.Equal]         = "PLUS",
		[DDKey.Backspace]     = "BACK",

		[DDKey.Tab]           = "TAB",
		[DDKey.Q]             = "Q",
		[DDKey.W]             = "W",
		[DDKey.E]             = "E",
		[DDKey.R]             = "R",
		[DDKey.T]             = "T",
		[DDKey.Y]             = "Y",
		[DDKey.U]             = "U",
		[DDKey.I]             = "I",
		[DDKey.O]             = "O",
		[DDKey.P]             = "P",
		[DDKey.LeftBracket]   = "BRKTS_L",
		[DDKey.RightBracket]  = "BRKTS_R",
		[DDKey.Backslash]     = "SLASH_K29",

		[DDKey.CapsLock]      = "CAPS",
		[DDKey.A]             = "A",
		[DDKey.S]             = "S",
		[DDKey.D]             = "D",
		[DDKey.F]             = "F",
		[DDKey.G]             = "G",
		[DDKey.H]             = "H",
		[DDKey.J]             = "J",
		[DDKey.K]             = "K",
		[DDKey.L]             = "L",
		[DDKey.Semicolon]     = "COLON",
		[DDKey.Quote]         = "QOTATN",
		[DDKey.IsoHash]       = "EUR_K42",
		[DDKey.Enter]         = "RETURN",

		[DDKey.LeftShift]     = "SHF_L",
		[DDKey.IsoBackslash]  = "EUR_K45",
		[DDKey.Z]             = "Z",
		[DDKey.X]             = "X",
		[DDKey.C]             = "C",
		[DDKey.V]             = "V",
		[DDKey.B]             = "B",
		[DDKey.N]             = "N",
		[DDKey.M]             = "M",
		[DDKey.Comma]         = "COMMA",
		[DDKey.Period]        = "PERIOD",
		[DDKey.Slash]         = "VIRGUE",
		[DDKey.RightShift]    = "SHF_R",

		[DDKey.LeftCtrl]      = "CTRL_L",
		[DDKey.LeftWin]       = "WIN_L",
		[DDKey.LeftAlt]       = "ALT_L",
		[DDKey.Space]         = "SPACE",
		[DDKey.RightAlt]      = "ALT_R",
		[DDKey.Fn1]           = "FN1",
		[DDKey.Fn2]           = "FN2",
		[DDKey.Menu]          = "APP",

		[DDKey.Insert]        = "INSERT",
		[DDKey.Delete]        = "DELETE",
		[DDKey.Home]          = "HOME",
		[DDKey.End]           = "END",
		[DDKey.PageUp]        = "PAGEUP",
		[DDKey.PageDown]      = "PAGEDW",

		[DDKey.ArrowUp]       = "ARR_UP",
		[DDKey.ArrowLeft]     = "ARR_L",
		[DDKey.ArrowDown]     = "ARR_DW",
		[DDKey.ArrowRight]    = "ARR_R",

		[DDKey.NumLock]       = "NUMS",
		[DDKey.Numpad0]       = "KP0",
		[DDKey.Numpad1]       = "KP1",
		[DDKey.Numpad2]       = "KP2",
		[DDKey.Numpad3]       = "KP3",
		[DDKey.Numpad4]       = "KP4",
		[DDKey.Numpad5]       = "KP5",
		[DDKey.Numpad6]       = "KP6",
		[DDKey.Numpad7]       = "KP7",
		[DDKey.Numpad8]       = "KP8",
		[DDKey.Numpad9]       = "KP9",
		[DDKey.NumpadDecimal] = "KP_DEL",
	};
}
