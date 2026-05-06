using Serilog;

namespace DDSharp;

/// <summary>
/// Low-level HID packet helpers shared across all DrunkDeer command families.
/// Owns the write: read retry loop, write-with-ack, and all key-point packet encoding.
/// </summary>
internal sealed class KeyboardProtocol
{
	private static readonly ILogger _log = Log.ForContext<KeyboardProtocol>();

	// Firmware always addresses 126 keys in key-point commands (layout indices 0-125).
	private const int KeyCount = 126;

	private readonly HidInterface _hid;

	internal KeyboardProtocol(HidInterface hid) => _hid = hid;

	// ── Shared I/O primitives ─────────────────────────────────────────────────

	/// <summary>
	/// Writes <paramref name="request"/> then reads until <paramref name="count"/> packets
	/// passing <paramref name="filter"/> have been collected, discarding non-matching reads
	/// up to <paramref name="maxRetries"/> times.
	/// </summary>
	/// <param name="packets">
	/// Cleared on entry; receives the matched packets in arrival order (cloned).
	/// </param>
	/// <returns>
	/// <c>true</c> when exactly <paramref name="count"/> matching packets were collected.
	/// </returns>
	internal bool TryReadPackets(
		byte[] request,
		Func<byte[], bool> filter,
		int count,
		List<byte[]> packets,
		int maxRetries = 30)
	{
		packets.Clear();

		if (_hid.Write(request) < 0)
		{
			_log.Error("TryReadPackets: HID write failed (cmd=0x{Cmd:X2})",
				request.Length > 1 ? request[1] : 0);
			return false;
		}

		int retries = 0;
		while (packets.Count < count && retries < maxRetries)
		{
			byte[] buf = new byte[65];
			int read = _hid.Read(buf);
			if (read < 0) return false;
			if (read == 0) { retries++; continue; }

			if (filter(buf))
				packets.Add((byte[])buf.Clone());
			else
			{
				_log.Verbose("TryReadPackets: skipping packet cmd=0x{C:X2} sub=0x{S:X2}", buf[1], buf[2]);
				retries++;
			}
		}

		if (packets.Count < count)
			_log.Error("TryReadPackets: only {N}/{Total} matching packets received", packets.Count, count);

		return packets.Count == count;
	}

	/// <summary>
	/// Writes <paramref name="data"/> and reads back a single ack packet.
	/// </summary>
	/// <returns><c>true</c> if the response echoes the command byte at <c>buf[1]</c>.</returns>
	internal bool SendAndAck(byte[] data)
	{
		if (_hid.Write(data) < 0) return false;
		byte[] buf = new byte[65];
		int read = _hid.Read(buf);
		bool ack = read > 0 && buf[1] == data[1];
		if (!ack) _log.Warning("No ack for command 0x{Cmd:X2}", data[1]);
		return ack;
	}

	// ── Key-point write ───────────────────────────────────────────────────────

	/// <summary>
	/// Standard-precision write - command <c>0xB6</c>, 3 packets, 1 byte per key.
	/// Packet layout: pkt 0 = keys 0-58, pkt 1 = keys 59-117, pkt 2 = keys 118-125 (59+59+8 = 126).
	/// </summary>
	internal bool WriteB6(KeyPointType type, byte[] rawValues)
	{
		bool ok = true;
		for (byte pkt = 0; pkt < 3; pkt++)
		{
			int baseOffset = pkt switch { 0 => 0, 1 => 59, _ => 118 };
			int count = pkt == 2 ? 8 : 59;

			var data = new byte[64];
			data[0] = 0x04;
			data[1] = 0xB6;
			data[2] = (byte)type;
			// data[3] = 0x00 - padding, already zero
			data[4] = pkt;
			for (int x = 0; x < count; x++)
				data[5 + x] = rawValues[baseOffset + x];

			ok &= SendAndAck(data);
		}
		_log.Debug("WriteB6 {Type} complete (ok={Ok})", type, ok);
		return ok;
	}

	/// <summary>
	/// High-precision write - command <c>0xFD</c>, 5 sections, 2 bytes per key (LE, ×200 scale).
	/// Sections 0-3 = 30 keys each; section 4 = 6 keys (4×30 + 6 = 126 total).
	/// </summary>
	internal bool WriteFd(KeyPointType type, double[] mmValues)
	{
		bool ok = true;
		for (byte section = 0; section < 5; section++)
		{
			int baseOffset = section * 30;
			int count = section == 4 ? 6 : 30;

			var data = new byte[64];
			data[0] = 0x04;
			data[1] = 0xFD;
			data[2] = (byte)type;
			data[3] = section;
			for (int x = 0; x < count; x++)
			{
				ushort raw = (ushort)Math.Round(mmValues[baseOffset + x] * 200.0);
				data[4 + x * 2]     = (byte)(raw & 0xFF);
				data[4 + x * 2 + 1] = (byte)(raw >> 8);
			}

			ok &= SendAndAck(data);
		}
		_log.Debug("WriteFd {Type} complete (ok={Ok})", type, ok);
		return ok;
	}

	// ── Key-point pull ────────────────────────────────────────────────────────

	/// <summary>
	/// Sends a <c>0xFD 0x07</c> pull request and reads back 5 response packets,
	/// each carrying up to 30 key values as 16-bit LE integers.
	/// Packets are placed by their embedded section index, so out-of-order delivery is handled.
	/// </summary>
	/// <param name="dataType">
	/// Pull dataType byte - differs from the write subcommand for the same point type:
	/// actuation = <c>0x01</c>, downstroke = <c>0x03</c>, upstroke = <c>0x04</c>.
	/// </param>
	/// <param name="expectedSubcmd">
	/// Response subcommand to match:
	/// <c>0x08</c> (actuation), <c>0x0A</c> (downstroke), <c>0x0B</c> (upstroke).
	/// </param>
	/// <returns>
	/// Array of <c>126</c> mm values indexed by layout position, or <c>null</c> on failure.
	/// </returns>
	internal double[]? PullFd(byte dataType, byte expectedSubcmd)
	{
		const int totalSections = 5;
		const int keysPerSection = 30;

		var req = new byte[64];
		req[0] = 0x04;
		req[1] = 0xFD;
		req[2] = 0x07;
		req[3] = dataType;

		var packets = new List<byte[]>(totalSections);
		bool ok = TryReadPackets(
			req,
			buf => buf[1] == 0xFD && buf[2] == expectedSubcmd && buf[3] < totalSections,
			totalSections,
			packets);

		if (!ok)
		{
			_log.Error("PullFd: incomplete response for dataType=0x{DT:X2}", dataType);
			return null;
		}

		// Place each packet into the result by its embedded section index so that
		// out-of-order delivery (rare but possible) is handled correctly.
		var result = new double[KeyCount];
		foreach (var buf in packets)
		{
			byte section = buf[3];
			int baseOffset = section * keysPerSection;
			int count = section == 4 ? 6 : keysPerSection;

			for (int x = 0; x < count; x++)
			{
				int pos = 4 + x * 2;
				ushort raw = (ushort)(buf[pos] | (buf[pos + 1] << 8));
				result[baseOffset + x] = raw / 200.0;
			}
		}

		_log.Debug("PullFd: decoded {Total} sections (dataType=0x{DT:X2})", totalSections, dataType);
		return result;
	}
}
