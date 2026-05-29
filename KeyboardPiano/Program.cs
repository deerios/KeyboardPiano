using Melanchall.DryWetMidi.Multimedia;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Threading;
using static System.Net.Mime.MediaTypeNames;

namespace KeyboardPiano
{
	internal class Program
	{
		private static IInputDevice _inputDevice;

		[STAThread]
		static void Main(string[] args)
		{
			/*
			_inputDevice = InputDevice.GetByName("DDMidiPort");
			_inputDevice.EventReceived += OnEventReceived;
			_inputDevice.StartEventsListening();

			Console.WriteLine("Input device is listening for events. Press any key to exit...");
			Console.ReadKey();

			(_inputDevice as IDisposable)?.Dispose();*/

			// Create the Keyboard Hook
			KeyboardListener kh = new KeyboardListener();

			kh.KeyDown += (sender, e) =>
			{
				Console.WriteLine(e.Key.ToString());
			};

			Console.ReadLine();

		}

		private static void OnEventReceived(object sender, MidiEventReceivedEventArgs e)
		{
			var midiDevice = (MidiDevice)sender;
			Console.WriteLine($"Event received from '{midiDevice.Name}' at {DateTime.Now}: {e.Event}");
		}
	}

	/// <summary>
	/// Listens keyboard globally.
	/// 
	/// <remarks>Uses WH_KEYBOARD_LL.</remarks>
	/// </summary>
	public class KeyboardListener : IDisposable
	{
		/// <summary>
		/// Creates global keyboard listener.
		/// </summary>
		public KeyboardListener()
		{
			// Dispatcher thread handling the KeyDown/KeyUp events.
			this.dispatcher = Dispatcher.CurrentDispatcher;

			// We have to store the LowLevelKeyboardProc, so that it is not garbage collected runtime
			hookedLowLevelKeyboardProc = (InterceptKeys.LowLevelKeyboardProc)LowLevelKeyboardProc;

			// Set the hook
			hookId = InterceptKeys.SetHook(hookedLowLevelKeyboardProc);

			// Assign the asynchronous callback event
			hookedKeyboardCallbackAsync = new KeyboardCallbackAsync(KeyboardListener_KeyboardCallbackAsync);
		}

		private Dispatcher dispatcher;

		/// <summary>
		/// Destroys global keyboard listener.
		/// </summary>
		~KeyboardListener()
		{
			Dispose();
		}

		/// <summary>
		/// Fired when any of the keys is pressed down.
		/// </summary>
		public event RawKeyEventHandler KeyDown;

		/// <summary>
		/// Fired when any of the keys is released.
		/// </summary>
		public event RawKeyEventHandler KeyUp;

		#region Inner workings

		/// <summary>
		/// Hook ID
		/// </summary>
		private IntPtr hookId = IntPtr.Zero;

		/// <summary>
		/// Asynchronous callback hook.
		/// </summary>
		/// <param name="character">Character</param>
		/// <param name="keyEvent">Keyboard event</param>
		/// <param name="vkCode">VKCode</param>
		private delegate void KeyboardCallbackAsync(InterceptKeys.KeyEvent keyEvent, int vkCode, string character);

		/// <summary>
		/// Actual callback hook.
		/// 
		/// <remarks>Calls asynchronously the asyncCallback.</remarks>
		/// </summary>
		/// <param name="nCode"></param>
		/// <param name="wParam"></param>
		/// <param name="lParam"></param>
		/// <returns></returns>
		[MethodImpl(MethodImplOptions.NoInlining)]
		private IntPtr LowLevelKeyboardProc(int nCode, UIntPtr wParam, IntPtr lParam)
		{
			string chars = "";

			if (nCode >= 0)
				if (wParam.ToUInt32() == (int)InterceptKeys.KeyEvent.WM_KEYDOWN ||
					wParam.ToUInt32() == (int)InterceptKeys.KeyEvent.WM_KEYUP ||
					wParam.ToUInt32() == (int)InterceptKeys.KeyEvent.WM_SYSKEYDOWN ||
					wParam.ToUInt32() == (int)InterceptKeys.KeyEvent.WM_SYSKEYUP)
				{
					// Captures the character(s) pressed only on WM_KEYDOWN
					chars = InterceptKeys.VKCodeToString((uint)Marshal.ReadInt32(lParam),
						(wParam.ToUInt32() == (int)InterceptKeys.KeyEvent.WM_KEYDOWN ||
						wParam.ToUInt32() == (int)InterceptKeys.KeyEvent.WM_SYSKEYDOWN));

					hookedKeyboardCallbackAsync.BeginInvoke((InterceptKeys.KeyEvent)wParam.ToUInt32(), Marshal.ReadInt32(lParam), chars, null, null);
				}

			return InterceptKeys.CallNextHookEx(hookId, nCode, wParam, lParam);
		}

		/// <summary>
		/// Event to be invoked asynchronously (BeginInvoke) each time key is pressed.
		/// </summary>
		private KeyboardCallbackAsync hookedKeyboardCallbackAsync;

		/// <summary>
		/// Contains the hooked callback in runtime.
		/// </summary>
		private InterceptKeys.LowLevelKeyboardProc hookedLowLevelKeyboardProc;

		/// <summary>
		/// HookCallbackAsync procedure that calls accordingly the KeyDown or KeyUp events.
		/// </summary>
		/// <param name="keyEvent">Keyboard event</param>
		/// <param name="vkCode">VKCode</param>
		/// <param name="character">Character as string.</param>
		void KeyboardListener_KeyboardCallbackAsync(InterceptKeys.KeyEvent keyEvent, int vkCode, string character)
		{
			switch (keyEvent)
			{
				// KeyDown events
				case InterceptKeys.KeyEvent.WM_KEYDOWN:
					if (KeyDown != null)
						dispatcher.BeginInvoke(new RawKeyEventHandler(KeyDown), this, new RawKeyEventArgs(vkCode, false, character));
					break;
				case InterceptKeys.KeyEvent.WM_SYSKEYDOWN:
					if (KeyDown != null)
						dispatcher.BeginInvoke(new RawKeyEventHandler(KeyDown), this, new RawKeyEventArgs(vkCode, true, character));
					break;

				// KeyUp events
				case InterceptKeys.KeyEvent.WM_KEYUP:
					if (KeyUp != null)
						dispatcher.BeginInvoke(new RawKeyEventHandler(KeyUp), this, new RawKeyEventArgs(vkCode, false, character));
					break;
				case InterceptKeys.KeyEvent.WM_SYSKEYUP:
					if (KeyUp != null)
						dispatcher.BeginInvoke(new RawKeyEventHandler(KeyUp), this, new RawKeyEventArgs(vkCode, true, character));
					break;

				default:
					break;
			}
		}

		#endregion

		#region IDisposable Members

		/// <summary>
		/// Disposes the hook.
		/// <remarks>This call is required as it calls the UnhookWindowsHookEx.</remarks>
		/// </summary>
		public void Dispose()
		{
			InterceptKeys.UnhookWindowsHookEx(hookId);
		}

		#endregion
	}


	/// <summary>
	/// 仮想キー
	/// </summary>
	public enum VirtualKeys
	{
		// http://www.pinvoke.net/default.aspx/Enums/VK.html

		/// <summary>Left mouse button</summary>
		LBUTTON = 0x01,

		/// <summary>Right mouse button</summary>
		RBUTTON = 0x02,

		/// <summary>Control-break processing</summary>
		CANCEL = 0x03,

		/// <summary>Middle mouse button (three-button mouse)</summary>
		MBUTTON = 0x04,

		/// <summary>Windows 2000/XP: X1 mouse button</summary>
		XBUTTON1 = 0x05,

		/// <summary>Windows 2000/XP: X2 mouse button</summary>
		XBUTTON2 = 0x06,

		// 0x07   Undefined

		/// <summary>BACKSPACE key</summary>
		BACK = 0x08,

		/// <summary>TAB key</summary>
		TAB = 0x09,

		// 0x0A-0x0B,  Reserved
		/// <summary>CLEAR key</summary>
		CLEAR = 0x0C,

		/// <summary>ENTER key</summary>
		RETURN = 0x0D,

		// 0x0E-0x0F, // Undefined

		/// <summary>SHIFT key</summary>
		SHIFT = 0x10,

		/// <summary>CTRL key</summary>
		CONTROL = 0x11,

		/// <summary>ALT key</summary>
		MENU = 0x12,
		/// <summary>PAUSE key</summary>
		PAUSE = 0x13,

		/// <summary>CAPS LOCK key</summary>
		CAPITAL = 0x14,

		/// <summary>Input Method Editor (IME) Kana mode</summary>
		KANA = 0x15,

		/// <summary>IME Hangul mode</summary>
		HANGUL = 0x15,

		// 0x16,  // Undefined

		/// <summary>IME Junja mode</summary>
		JUNJA = 0x17,

		/// <summary>IME final mode</summary>
		FINAL = 0x18,

		/// <summary>IME Hanja mode</summary>
		HANJA = 0x19,

		/// <summary>IME Kanji mode</summary>
		KANJI = 0x19,

		// 0x1A,  // Undefined

		/// <summary>ESC key</summary>
		ESCAPE = 0x1B,

		/// <summary>IME convert</summary>
		CONVERT = 0x1C,

		/// <summary>IME nonconvert</summary>
		NONCONVERT = 0x1D,

		/// <summary>IME accept</summary>
		ACCEPT = 0x1E,

		/// <summary>IME mode change request</summary>
		MODECHANGE = 0x1F,

		/// <summary>SPACEBAR</summary>
		SPACE = 0x20,

		/// <summary>PAGE UP key</summary>
		PRIOR = 0x21,

		/// <summary>PAGE DOWN key</summary>
		NEXT = 0x22,

		/// <summary>END key</summary>
		END = 0x23,

		/// <summary>HOME key</summary>
		HOME = 0x24,

		/// <summary>LEFT ARROW key</summary>
		LEFT = 0x25,

		/// <summary>UP ARROW key</summary>
		UP = 0x26,

		/// <summary>RIGHT ARROW key</summary>
		RIGHT = 0x27,

		/// <summary>DOWN ARROW key</summary>
		DOWN = 0x28,

		/// <summary>SELECT key</summary>
		SELECT = 0x29,

		/// <summary>PRINT key</summary>
		PRINT = 0x2A,

		/// <summary>EXECUTE key</summary>
		EXECUTE = 0x2B,

		/// <summary>PRINT SCREEN key</summary>
		SNAPSHOT = 0x2C,

		/// <summary>INS key</summary>
		INSERT = 0x2D,

		/// <summary>DEL key</summary>
		DELETE = 0x2E,

		/// <summary>HELP key</summary>
		HELP = 0x2F,

		/// <summary>0 key</summary>
		KEY_0 = 0x30,

		/// <summary>1 key</summary>
		KEY_1 = 0x31,

		/// <summary>2 key</summary>
		KEY_2 = 0x32,

		/// <summary>3 key</summary>
		KEY_3 = 0x33,

		/// <summary>4 key</summary>
		KEY_4 = 0x34,

		/// <summary>5 key</summary>
		KEY_5 = 0x35,

		/// <summary>6 key</summary>
		KEY_6 = 0x36,

		/// <summary>7 key</summary>
		KEY_7 = 0x37,

		/// <summary>8 key</summary>
		KEY_8 = 0x38,

		/// <summary>9 key</summary>
		KEY_9 = 0x39,

		// 0x3A-0x40, // Undefined

		/// <summary>A key</summary>
		KEY_A = 0x41,

		/// <summary>B key</summary>
		KEY_B = 0x42,

		/// <summary>C key</summary>
		KEY_C = 0x43,

		/// <summary>D key</summary>
		KEY_D = 0x44,

		/// <summary>E key</summary>
		KEY_E = 0x45,

		/// <summary>F key</summary>
		KEY_F = 0x46,

		/// <summary>G key</summary>
		KEY_G = 0x47,

		/// <summary>H key</summary>
		KEY_H = 0x48,

		/// <summary>I key</summary>
		KEY_I = 0x49,

		/// <summary>J key</summary>
		KEY_J = 0x4A,

		/// <summary>K key</summary>
		KEY_K = 0x4B,

		/// <summary>L key</summary>
		KEY_L = 0x4C,

		/// <summary>M key</summary>
		KEY_M = 0x4D,

		/// <summary>N key</summary>
		KEY_N = 0x4E,

		/// <summary>O key</summary>
		KEY_O = 0x4F,

		/// <summary>P key</summary>
		KEY_P = 0x50,

		/// <summary>Q key</summary>
		KEY_Q = 0x51,

		/// <summary>R key</summary>
		KEY_R = 0x52,

		/// <summary>S key</summary>
		KEY_S = 0x53,

		/// <summary>T key</summary>
		KEY_T = 0x54,

		/// <summary>U key</summary>
		KEY_U = 0x55,

		/// <summary>V key</summary>
		KEY_V = 0x56,

		/// <summary>W key</summary>
		KEY_W = 0x57,

		/// <summary>X key</summary>
		KEY_X = 0x58,

		/// <summary>Y key</summary>
		KEY_Y = 0x59,

		/// <summary>Z key</summary>
		KEY_Z = 0x5A,

		/// <summary>Left Windows key (Microsoft Natural keyboard)</summary>
		LWIN = 0x5B,

		/// <summary>Right Windows key (Natural keyboard)</summary>
		RWIN = 0x5C,

		/// <summary>Applications key (Natural keyboard)</summary>
		APPS = 0x5D,

		// 0x5E, // Reserved

		/// <summary>Computer Sleep key</summary>
		SLEEP = 0x5F,

		/// <summary>Numeric keypad 0 key</summary>
		NUMPAD0 = 0x60,

		/// <summary>Numeric keypad 1 key</summary>
		NUMPAD1 = 0x61,

		/// <summary>Numeric keypad 2 key</summary>
		NUMPAD2 = 0x62,

		/// <summary>Numeric keypad 3 key</summary>
		NUMPAD3 = 0x63,

		/// <summary>Numeric keypad 4 key</summary>
		NUMPAD4 = 0x64,

		/// <summary>Numeric keypad 5 key</summary>
		NUMPAD5 = 0x65,

		/// <summary>Numeric keypad 6 key</summary>
		NUMPAD6 = 0x66,

		/// <summary>Numeric keypad 7 key</summary>
		NUMPAD7 = 0x67,

		/// <summary>Numeric keypad 8 key</summary>
		NUMPAD8 = 0x68,

		/// <summary>Numeric keypad 9 key</summary>
		NUMPAD9 = 0x69,

		/// <summary>Multiply key</summary>
		MULTIPLY = 0x6A,

		/// <summary>Add key</summary>
		ADD = 0x6B,

		/// <summary>Separator key</summary>
		SEPARATOR = 0x6C,

		/// <summary>Subtract key</summary>
		SUBTRACT = 0x6D,

		/// <summary>Decimal key</summary>
		DECIMAL = 0x6E,

		/// <summary>Divide key</summary>
		DIVIDE = 0x6F,

		/// <summary>F1 key</summary>
		F1 = 0x70,

		/// <summary>F2 key</summary>
		F2 = 0x71,

		/// <summary>F3 key</summary>
		F3 = 0x72,

		/// <summary>F4 key</summary>
		F4 = 0x73,

		/// <summary>F5 key</summary>
		F5 = 0x74,

		/// <summary>F6 key</summary>
		F6 = 0x75,

		/// <summary>F7 key</summary>
		F7 = 0x76,

		/// <summary>F8 key</summary>
		F8 = 0x77,

		/// <summary>F9 key</summary>
		F9 = 0x78,

		/// <summary>F10 key</summary>
		F10 = 0x79,

		/// <summary>F11 key</summary>
		F11 = 0x7A,

		/// <summary>F12 key</summary>
		F12 = 0x7B,

		/// <summary>F13 key</summary>
		F13 = 0x7C,

		/// <summary>F14 key</summary>
		F14 = 0x7D,

		/// <summary>F15 key</summary>
		F15 = 0x7E,

		/// <summary>F16 key</summary>
		F16 = 0x7F,

		/// <summary>F17 key  </summary>
		F17 = 0x80,

		/// <summary>F18 key  </summary>
		F18 = 0x81,

		/// <summary>F19 key  </summary>
		F19 = 0x82,

		/// <summary>F20 key  </summary>
		F20 = 0x83,

		/// <summary>F21 key  </summary>
		F21 = 0x84,

		/// <summary>F22 key, (PPC only) Key used to lock device.</summary>
		F22 = 0x85,

		/// <summary>F23 key  </summary>
		F23 = 0x86,

		/// <summary>F24 key  </summary>
		F24 = 0x87,

		// 0x88-0X8F,  // Unassigned

		/// <summary>NUM LOCK key</summary>
		NUMLOCK = 0x90,

		/// <summary>SCROLL LOCK key</summary>
		SCROLL = 0x91,

		// 0x92-0x96,  // OEM specific
		// 0x97-0x9F,  // Unassigned

		/// <summary>Left SHIFT key</summary>
		LSHIFT = 0xA0,

		/// <summary>Right SHIFT key</summary>
		RSHIFT = 0xA1,

		/// <summary>Left CONTROL key</summary>
		LCONTROL = 0xA2,

		/// <summary>Right CONTROL key</summary>
		RCONTROL = 0xA3,

		/// <summary>Left MENU key</summary>
		LMENU = 0xA4,

		/// <summary>Right MENU key</summary>
		RMENU = 0xA5,

		/// <summary>// Windows 2000/XP: Browser Back key</summary>
		BROWSER_BACK = 0xA6,

		/// <summary>Windows 2000/XP: Browser Forward key</summary>
		BROWSER_FORWARD = 0xA7,

		/// <summary>Windows 2000/XP: Browser Refresh key</summary>
		BROWSER_REFRESH = 0xA8,

		/// <summary>Windows 2000/XP: Browser Stop key</summary>
		BROWSER_STOP = 0xA9,

		/// <summary>Windows 2000/XP: Browser Search key</summary>
		BROWSER_SEARCH = 0xAA,

		/// <summary>Windows 2000/XP: Browser Favorites key</summary>
		BROWSER_FAVORITES = 0xAB,

		/// <summary>Windows 2000/XP: Browser Start and Home key</summary>
		BROWSER_HOME = 0xAC,

		/// <summary>Windows 2000/XP: Volume Mute key</summary>
		VOLUME_MUTE = 0xAD,

		/// <summary>Windows 2000/XP: Volume Down key</summary>
		VOLUME_DOWN = 0xAE,

		/// <summary>Windows 2000/XP: Volume Up key</summary>
		VOLUME_UP = 0xAF,

		/// <summary>Windows 2000/XP: Next Track key</summary>
		MEDIA_NEXT_TRACK = 0xB0,

		/// <summary>Windows 2000/XP: Previous Track key</summary>
		MEDIA_PREV_TRACK = 0xB1,

		/// <summary>Windows 2000/XP: Stop Media key</summary>
		MEDIA_STOP = 0xB2,

		/// <summary>Windows 2000/XP: Play/Pause Media key</summary>
		MEDIA_PLAY_PAUSE = 0xB3,

		/// <summary>Windows 2000/XP: Start Mail key</summary>
		LAUNCH_MAIL = 0xB4,

		/// <summary>Windows 2000/XP: Select Media key</summary>
		LAUNCH_MEDIA_SELECT = 0xB5,

		/// <summary>Windows 2000/XP: Start Application 1 key</summary>
		LAUNCH_APP1 = 0xB6,

		/// <summary>Windows 2000/XP: Start Application 2 key</summary>
		LAUNCH_APP2 = 0xB7,

		// 0xB8-0xB9,  // Reserved

		/// <summary>Used for miscellaneous characters; it can vary by keyboard.</summary>
		OEM_1 = 0xBA,


		// Windows 2000/XP: For the US standard keyboard, the ';:' key

		/// <summary>Windows 2000/XP: For any country/region, the '+' key</summary>
		OEM_PLUS = 0xBB,


		/// <summary>Windows 2000/XP: For any country/region, the ',' key</summary>
		OEM_COMMA = 0xBC,

		/// <summary>Windows 2000/XP: For any country/region, the '-' key</summary>
		OEM_MINUS = 0xBD,

		/// <summary>Windows 2000/XP: For any country/region, the '.' key</summary>
		OEM_PERIOD = 0xBE,

		/// <summary>Used for miscellaneous characters; it can vary by keyboard.</summary>
		OEM_2 = 0xBF,

		// Windows 2000/XP: For the US standard keyboard, the '/?' key

		/// <summary>Used for miscellaneous characters; it can vary by keyboard.</summary>
		OEM_3 = 0xC0,

		// Windows 2000/XP: For the US standard keyboard, the '`~' key
		// 0xC1-0xD7,  // Reserved
		// 0xD8-0xDA,  // Unassigned

		/// <summary>Used for miscellaneous characters; it can vary by keyboard.</summary>
		OEM_4 = 0xDB,

		// Windows 2000/XP: For the US standard keyboard, the '[{' key

		/// <summary>Used for miscellaneous characters; it can vary by keyboard.</summary>
		OEM_5 = 0xDC,

		// Windows 2000/XP: For the US standard keyboard, the '\|' key

		/// <summary>Used for miscellaneous characters; it can vary by keyboard.</summary>
		OEM_6 = 0xDD,

		// Windows 2000/XP: For the US standard keyboard, the ']}' key

		/// <summary>Used for miscellaneous characters; it can vary by keyboard.</summary>
		OEM_7 = 0xDE,

		// Windows 2000/XP: For the US standard keyboard, the 'single-quote/double-quote' key

		/// <summary>Used for miscellaneous characters; it can vary by keyboard.</summary>
		OEM_8 = 0xDF,

		// 0xE0,  // Reserved
		// 0xE1,  // OEM specific

		/// <summary>Windows 2000/XP: Either the angle bracket key or the backslash key on the RT 102-key keyboard</summary>
		OEM_102 = 0xE2,

		// 0xE3-E4,  // OEM specific

		/// <summary>Windows 95/98/Me, Windows NT 4.0, Windows 2000/XP: IME PROCESS key</summary>
		PROCESSKEY = 0xE5,

		// 0xE6,  // OEM specific

		/// <summary>Windows 2000/XP: Used to pass Unicode characters as if they were keystrokes. The VK_PACKET key is the low word of a 32-bit Virtual Key value used for non-keyboard input methods. For more information, see Remark in KEYBDINPUT, SendInput, WM_KEYDOWN, and WM_KEYUP</summary>
		PACKET = 0xE7,

		// 0xE8,  // Unassigned
		// 0xE9-F5,  // OEM specific

		/// <summary>Attn key</summary>
		ATTN = 0xF6,

		/// <summary>CrSel key</summary>
		CRSEL = 0xF7,

		/// <summary>ExSel key</summary>
		EXSEL = 0xF8,

		/// <summary>Erase EOF key</summary>
		EREOF = 0xF9,

		/// <summary>Play key</summary>
		PLAY = 0xFA,

		/// <summary>Zoom key</summary>
		ZOOM = 0xFB,

		/// <summary>Reserved</summary>
		NONAME = 0xFC,

		/// <summary>PA1 key</summary>
		PA1 = 0xFD,

		/// <summary>Clear key</summary>
		OEM_CLEAR = 0xFE
	}

	/// <summary>
	/// Raw KeyEvent arguments.
	/// </summary>
	public class RawKeyEventArgs : EventArgs
	{
		/// <summary>
		/// VKCode of the key.
		/// </summary>
		public int VKCode;

		/// <summary>
		/// WPF Key of the key.
		/// </summary>
		public Key Key;

		/// <summary>
		/// Is the hitted key system key.
		/// </summary>
		public bool IsSysKey;

		/// <summary>
		/// Convert to string.
		/// </summary>
		/// <returns>Returns string representation of this key, if not possible empty string is returned.</returns>
		public override string ToString()
		{
			return Character;
		}

		/// <summary>
		/// Unicode character of key pressed.
		/// </summary>
		public string Character;

		/// <summary>
		/// Create raw keyevent arguments.
		/// </summary>
		/// <param name="VKCode"></param>
		/// <param name="isSysKey"></param>
		/// <param name="Character">Character</param>
		public RawKeyEventArgs(int VKCode, bool isSysKey, string Character)
		{
			this.VKCode = VKCode;
			this.IsSysKey = isSysKey;
			this.Character = Character;
			this.Key = System.Windows.Input.KeyInterop.KeyFromVirtualKey(VKCode);
		}

	}

	/// <summary>
	/// Raw keyevent handler.
	/// </summary>
	/// <param name="sender">sender</param>
	/// <param name="args">raw keyevent arguments</param>
	public delegate void RawKeyEventHandler(object sender, RawKeyEventArgs args);

	#region WINAPI Helper class
	/// <summary>
	/// Winapi Key interception helper class.
	/// </summary>
	internal static class InterceptKeys
	{
		public delegate IntPtr LowLevelKeyboardProc(int nCode, UIntPtr wParam, IntPtr lParam);
		public static int WH_KEYBOARD_LL = 13;

		/// <summary>
		/// Key event
		/// </summary>
		public enum KeyEvent : int
		{
			/// <summary>
			/// Key down
			/// </summary>
			WM_KEYDOWN = 256,

			/// <summary>
			/// Key up
			/// </summary>
			WM_KEYUP = 257,

			/// <summary>
			/// System key up
			/// </summary>
			WM_SYSKEYUP = 261,

			/// <summary>
			/// System key down
			/// </summary>
			WM_SYSKEYDOWN = 260
		}

		public static IntPtr SetHook(LowLevelKeyboardProc proc)
		{
			using (Process curProcess = Process.GetCurrentProcess())
			using (ProcessModule curModule = curProcess.MainModule)
			{
				return SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(curModule.ModuleName), 0);
			}
		}

		[DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
		public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

		[DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
		[return: MarshalAs(UnmanagedType.Bool)]
		public static extern bool UnhookWindowsHookEx(IntPtr hhk);

		[DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
		public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, UIntPtr wParam, IntPtr lParam);

		[DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
		public static extern IntPtr GetModuleHandle(string lpModuleName);

		#region Convert VKCode to string
		// Note: Sometimes single VKCode represents multiple chars, thus string. 
		// E.g. typing "^1" (notice that when pressing 1 the both characters appear, 
		// because of this behavior, "^" is called dead key)

		[DllImport("user32.dll")]
		private static extern int ToUnicodeEx(uint wVirtKey, uint wScanCode, byte[] lpKeyState, [Out, MarshalAs(UnmanagedType.LPWStr)] System.Text.StringBuilder pwszBuff, int cchBuff, uint wFlags, IntPtr dwhkl);

		[DllImport("user32.dll")]
		private static extern bool GetKeyboardState(byte[] lpKeyState);

		[DllImport("user32.dll")]
		private static extern uint MapVirtualKeyEx(uint uCode, uint uMapType, IntPtr dwhkl);

		[DllImport("user32.dll", CharSet = CharSet.Auto, ExactSpelling = true)]
		private static extern IntPtr GetKeyboardLayout(uint dwLayout);

		[DllImport("User32.dll")]
		private static extern IntPtr GetForegroundWindow();

		[DllImport("User32.dll")]
		private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

		[DllImport("user32.dll")]
		private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

		[DllImport("kernel32.dll")]
		private static extern uint GetCurrentThreadId();

		private static uint lastVKCode = 0;
		private static uint lastScanCode = 0;
		private static byte[] lastKeyState = new byte[255];
		private static bool lastIsDead = false;

		/// <summary>
		/// Convert VKCode to Unicode.
		/// <remarks>isKeyDown is required for because of keyboard state inconsistencies!</remarks>
		/// </summary>
		/// <param name="VKCode">VKCode</param>
		/// <param name="isKeyDown">Is the key down event?</param>
		/// <returns>String representing single unicode character.</returns>
		public static string VKCodeToString(uint VKCode, bool isKeyDown)
		{
			// ToUnicodeEx needs StringBuilder, it populates that during execution.
			System.Text.StringBuilder sbString = new System.Text.StringBuilder(5);

			byte[] bKeyState = new byte[255];
			bool bKeyStateStatus;
			bool isDead = false;

			// Gets the current windows window handle, threadID, processID
			IntPtr currentHWnd = GetForegroundWindow();
			uint currentProcessID;
			uint currentWindowThreadID = GetWindowThreadProcessId(currentHWnd, out currentProcessID);

			// This programs Thread ID
			uint thisProgramThreadId = GetCurrentThreadId();

			// Attach to active thread so we can get that keyboard state
			if (AttachThreadInput(thisProgramThreadId, currentWindowThreadID, true))
			{
				// Current state of the modifiers in keyboard
				bKeyStateStatus = GetKeyboardState(bKeyState);

				// Detach
				AttachThreadInput(thisProgramThreadId, currentWindowThreadID, false);
			}
			else
			{
				// Could not attach, perhaps it is this process?
				bKeyStateStatus = GetKeyboardState(bKeyState);
			}

			// On failure we return empty string.
			if (!bKeyStateStatus)
				return "";

			// Gets the layout of keyboard
			IntPtr HKL = GetKeyboardLayout(currentWindowThreadID);

			// Maps the virtual keycode
			uint lScanCode = MapVirtualKeyEx(VKCode, 0, HKL);

			// Keyboard state goes inconsistent if this is not in place. In other words, we need to call above commands in UP events also.
			if (!isKeyDown)
				return "";

			// Converts the VKCode to unicode
			int relevantKeyCountInBuffer = ToUnicodeEx(VKCode, lScanCode, bKeyState, sbString, sbString.Capacity, (uint)0, HKL);

			string ret = "";

			switch (relevantKeyCountInBuffer)
			{
				// Dead keys (^,`...)
				case -1:
					isDead = true;

					// We must clear the buffer because ToUnicodeEx messed it up, see below.
					ClearKeyboardBuffer(VKCode, lScanCode, HKL);
					break;

				case 0:
					break;

				// Single character in buffer
				case 1:
					ret = sbString[0].ToString();
					break;

				// Two or more (only two of them is relevant)
				case 2:
				default:
					ret = sbString.ToString().Substring(0, 2);
					break;
			}

			// We inject the last dead key back, since ToUnicodeEx removed it.
			// More about this peculiar behavior see e.g: 
			//   http://www.experts-exchange.com/Programming/System/Windows__Programming/Q_23453780.html
			//   http://blogs.msdn.com/michkap/archive/2005/01/19/355870.aspx
			//   http://blogs.msdn.com/michkap/archive/2007/10/27/5717859.aspx
			if (lastVKCode != 0 && lastIsDead)
			{
				System.Text.StringBuilder sbTemp = new System.Text.StringBuilder(5);
				ToUnicodeEx(lastVKCode, lastScanCode, lastKeyState, sbTemp, sbTemp.Capacity, (uint)0, HKL);
				lastVKCode = 0;

				return ret;
			}

			// Save these
			lastScanCode = lScanCode;
			lastVKCode = VKCode;
			lastIsDead = isDead;
			lastKeyState = (byte[])bKeyState.Clone();

			return ret;
		}

		private static void ClearKeyboardBuffer(uint vk, uint sc, IntPtr hkl)
		{
			System.Text.StringBuilder sb = new System.Text.StringBuilder(10);

			int rc;
			do
			{
				byte[] lpKeyStateNull = new Byte[255];
				rc = ToUnicodeEx(vk, sc, lpKeyStateNull, sb, sb.Capacity, 0, hkl);
			} while (rc < 0);
		}
		#endregion
	}
	#endregion
}
