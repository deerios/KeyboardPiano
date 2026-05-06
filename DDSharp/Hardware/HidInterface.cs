using HidSharp;
using Serilog;

namespace DDSharp;

class HidInterface : IDisposable
{
	private static readonly ILogger _log = Log.ForContext<HidInterface>();

	private static readonly int[] KnownVendorIds = [0x352D, 0x5AC, 0x04D9, 0x1A85];
	private static readonly int[] KnownProductIds = [0x2383, 0x2382, 0x2384, 0x2386, 0x24F, 0x2391, 0x2A08, 0xFC4F];

	private HidStream? _stream;       // command interface (In=64 Out=64) - write + read responses
	private HidStream? _dataStream;   // data interface (In=64 Out=0)    - unsolicited 0xB7 stream
	private HidDevice? _device;

	public bool IsConnected { get; private set; } = true;
	public bool HasDataStream => _dataStream != null;

	/// <summary>
	/// Logs every HID interface found for known DrunkDeer VID/PID combinations.
	/// Use before connecting to identify all available interfaces and their report sizes.
	/// </summary>
	internal static void EnumerateInterfaces()
	{
		foreach (var device in DeviceList.Local.GetHidDevices())
		{
			if (!KnownVendorIds.Contains(device.VendorID)) continue;
			if (!KnownProductIds.Contains(device.ProductID)) continue;

			int outLen = device.GetMaxOutputReportLength();
			int inLen = device.GetMaxInputReportLength();
			int featureLen = device.GetMaxFeatureReportLength();
			string name = device.GetFriendlyName();
			byte[] desc = [];
			try { desc = device.GetRawReportDescriptor(); } catch { }

			_log.Information(
				"Interface  VID=0x{VID:X4} PID=0x{PID:X4}  In={In} Out={Out} Feature={F}  DescLen={D}  Name={Name}",
				device.VendorID, device.ProductID, inLen, outLen, featureLen, desc.Length, name);
		}
	}

	public bool Open()
	{
		int? targetVid = null, targetPid = null;

		// First pass: find and open the command interface (In=64, Out=64).
		foreach (var device in DeviceList.Local.GetHidDevices())
		{
			if (!KnownVendorIds.Contains(device.VendorID)) continue;
			if (!KnownProductIds.Contains(device.ProductID)) continue;

			int outLen = device.GetMaxOutputReportLength();
			int inLen = device.GetMaxInputReportLength();
			_log.Debug("Candidate VID=0x{VID:X4} PID=0x{PID:X4} OutReport={Out} InReport={In}",
				device.VendorID, device.ProductID, outLen, inLen);

			if (outLen < 64 || inLen < 64) continue;

			_log.Information("Opening command interface: VID=0x{VID:X4} PID=0x{PID:X4}", device.VendorID, device.ProductID);

			var options = new OpenConfiguration();
			options.SetOption(OpenOption.Interruptible, true);

			if (device.TryOpen(options, out _stream))
			{
				_device   = device;
				targetVid = device.VendorID;
				targetPid = device.ProductID;
				_stream.ReadTimeout  = 5000;
				_stream.WriteTimeout = 5000;
				break;
			}

			_log.Warning("Found command interface but could not open it (may be held by another process).");
		}

		if (_stream == null)
		{
			_log.Error("No supported DrunkDeer keyboard found. Is it plugged in?");
			return false;
		}

		// Second pass: find the read-only data interface (In=64, Out=0) on the same VID/PID.
		// The keyboard streams unsolicited 0xB7 height packets on this interface.
		foreach (var device in DeviceList.Local.GetHidDevices())
		{
			if (device.VendorID != targetVid || device.ProductID != targetPid) continue;
			if (device.GetMaxOutputReportLength() != 0) continue;
			if (device.GetMaxInputReportLength() < 64) continue;

			var options = new OpenConfiguration();
			options.SetOption(OpenOption.Interruptible, true);

			if (device.TryOpen(options, out _dataStream))
			{
				_dataStream.ReadTimeout = 200;
				_log.Information("Opened data stream interface (In=64 Out=0) for high-rate polling.");
				break;
			}
		}

		if (_dataStream == null)
			_log.Warning("Could not open data stream interface - falling back to request-response polling.");

		return true;
	}

	public int Write(byte[] data)
	{
		if (_stream == null || _device == null) return -1;

		int reportLen = _device.GetMaxOutputReportLength();
		byte[] buf = new byte[reportLen];
		Array.Copy(data, 0, buf, 0, Math.Min(data.Length, reportLen));

		try
		{
			_stream.Write(buf);
			return buf.Length;
		}
		catch (Exception ex)
		{
			IsConnected = false;
			_log.Error(ex, "HID write failed");
			return -1;
		}
	}

	// Read from the data stream if available, otherwise the command stream.
	public int Read(byte[] buffer) => ReadFrom(_dataStream ?? _stream, buffer);

	// Always read from the command stream (for request-response).
	public int ReadCommand(byte[] buffer) => ReadFrom(_stream, buffer);

	private int ReadFrom(HidStream? stream, byte[] buffer)
	{
		if (stream == null) return -1;

		try
		{
			return stream.Read(buffer, 0, buffer.Length);
		}
		catch (TimeoutException)
		{
			_log.Verbose("HID read timed out (no data)");
			return 0;
		}
		catch (Exception ex)
		{
			IsConnected = false;
			_log.Error(ex, "HID read failed");
			return -1;
		}
	}

	/// <summary>Drain any buffered input packets without blocking.</summary>
	public void FlushReadBuffer()
	{
		foreach (var stream in new[] { _stream, _dataStream })
		{
			if (stream == null) continue;
			int saved = stream.ReadTimeout;
			stream.ReadTimeout = 1;
			try
			{
				byte[] tmp = new byte[65];
				int attempts = 0;
				while (attempts++ < 32 && stream.Read(tmp, 0, tmp.Length) > 0) { }
			}
			catch { }
			finally { stream.ReadTimeout = saved; }
		}
	}

	public void SetReadTimeout(int ms)
	{
		if (_stream != null) _stream.ReadTimeout     = ms;
		if (_dataStream != null) _dataStream.ReadTimeout = ms;
	}

	public void SetDataReadTimeout(int ms)
	{
		if (_dataStream != null) _dataStream.ReadTimeout = ms;
	}

	public void Close()
	{
		_dataStream?.Close();
		_dataStream = null;
		_stream?.Close();
		_stream = null;
	}

	public void Dispose() => Close();
}
