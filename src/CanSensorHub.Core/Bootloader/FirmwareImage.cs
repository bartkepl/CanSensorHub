using System.Globalization;

namespace CanSensorHub.Core.Bootloader;

/// <summary>
/// Loads a firmware image (.bin raw dump, or .hex Intel HEX) for the CAN/UART bootloader, padded to a
/// multiple of 8 bytes with 0xFF (the bootloader programs one 64-bit flash doubleword per PROG_DATA
/// frame) and pre-computes the whole-image CRC32 the VERIFY step checks against.
/// </summary>
public sealed class FirmwareImage
{
    public required byte[] Data { get; init; }
    public required uint Crc32Value { get; init; }
    public required uint LoadAddress { get; init; }
    public required string SourcePath { get; init; }

    public static FirmwareImage LoadFromFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var (raw, baseAddress) = ext == ".hex" ? ParseIntelHex(File.ReadAllLines(path)) : (File.ReadAllBytes(path), 0u);

        var padded = PadTo8(raw);
        return new FirmwareImage
        {
            Data = padded,
            Crc32Value = Bootloader.Crc32.Compute(padded),
            LoadAddress = baseAddress,
            SourcePath = path,
        };
    }

    private static byte[] PadTo8(byte[] data)
    {
        var rem = data.Length % 8;
        if (rem == 0) return data;
        var padded = new byte[data.Length + (8 - rem)];
        Array.Copy(data, padded, data.Length);
        Array.Fill(padded, (byte)0xFF, data.Length, padded.Length - data.Length);
        return padded;
    }

    /// <summary>Minimal Intel HEX (record types 00 data, 01 EOF, 04 extended linear address) reconstructing a flat image from the lowest address found.</summary>
    private static (byte[] Data, uint BaseAddress) ParseIntelHex(string[] lines)
    {
        var bytes = new SortedDictionary<uint, byte>();
        uint upperAddress = 0;

        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line[0] != ':') continue;

            var bin = new byte[(line.Length - 1) / 2];
            for (var i = 0; i < bin.Length; i++)
                bin[i] = byte.Parse(line.AsSpan(1 + i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);

            var len = bin[0];
            var addr = (uint)((bin[1] << 8) | bin[2]);
            var type = bin[3];
            // bin[^1] is the checksum byte — trusted here; a corrupt file will simply verify wrong later via CRC32.

            switch (type)
            {
                case 0x00: // data
                    for (var i = 0; i < len; i++)
                        bytes[upperAddress + addr + (uint)i] = bin[4 + i];
                    break;
                case 0x01: // EOF
                    goto done;
                case 0x04: // extended linear address
                    upperAddress = (uint)((bin[4] << 24) | (bin[5] << 16));
                    break;
                // 0x02/0x03/0x05 (segment address / start address) — not used by these Cortex-M images.
            }
        }
        done:

        if (bytes.Count == 0)
            throw new InvalidDataException("Plik .hex nie zawiera żadnych rekordów danych.");

        var baseAddress = bytes.Keys.First();
        var maxAddress = bytes.Keys.Last();
        var flat = new byte[maxAddress - baseAddress + 1];
        Array.Fill(flat, (byte)0xFF);
        foreach (var (addr, value) in bytes)
            flat[addr - baseAddress] = value;

        return (flat, baseAddress);
    }
}
