namespace CanSensorHub.Core.Bootloader;

/// <summary>
/// Odpowiedź bootloadera na komendę HELLO.
///
/// Pola <see cref="NodeId"/> i <see cref="DeviceType"/> wprowadzono w wersji 1.1 bootloadera; zajmują
/// bajty pozostawione wolne w wersji 1.0. Bootloader 1.0 zwraca w nich zera, dlatego odczytuje się je
/// wyłącznie wtedy, gdy zgłoszona wersja to potwierdza — w przeciwnym razie zero zostałoby uznane za
/// prawidłowy typ urządzenia.
///
/// Wsparcie wersji 1.0 pozostaje wymogiem wiążącym: taki bootloader pracuje w module zalanym, którego
/// wymiana wymagałaby dostępu do pinu BOOT0.
/// </summary>
public sealed record BlHelloInfo(
    byte VersionMajor,
    byte VersionMinor,
    bool AppValid,
    ushort BlockSize,
    byte? NodeId = null,
    ushort? DeviceType = null)
{
    /// <summary>Bootloader zgłasza pola rozszerzone (wersja 1.1 lub nowsza).</summary>
    public bool HasExtendedFields => NodeId.HasValue;

    public string VersionText => $"{VersionMajor}.{VersionMinor}";
}

public sealed record BlFlashProgress(int BytesSent, int TotalBytes, string Message)
{
    public double Fraction => TotalBytes == 0 ? 0 : (double)BytesSent / TotalBytes;
}

/// <summary>
/// Drives the CAN bootloader protocol shared by MPCC_BL and MPSWP_BL: HELLO → ERASE →
/// (PROG_START, up to 64×PROG_DATA, PROG_END)×N over 512 B blocks → VERIFY → GO, over <see cref="BlCanLink"/>.
/// </summary>
public sealed class BootloaderFlasher(IBlLink link)
{
    private const int BlockSize = 512;
    private const int ChunkSize = 8;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan EraseTimeout = TimeSpan.FromSeconds(5);

    // The target's CAN receiver (bl_can.c) is pure polling with no interrupts and only a 3-deep hardware
    // RX FIFO. Sending all 64 PROG_DATA chunks of a block back-to-back (confirmed via a captured bus log:
    // ~3 ms host-side for the whole burst, ~0.2 ms gap before PROG_END) can outrun that FIFO — on real
    // hardware this reproduced as PROG_END never getting a reply, 3/3 attempts, with PROG_START always
    // acking instantly right before it. Pacing the burst gives the target's poll loop room to drain it.
    private static readonly TimeSpan InterChunkDelay = TimeSpan.FromMilliseconds(2);

    public async Task<BlHelloInfo> HelloAsync(CancellationToken ct)
    {
        var resp = await link.RequestAsync(BlOpcodes.Hello, 0, ReadOnlyMemory<byte>.Empty, DefaultTimeout, ct).ConfigureAwait(false)
            ?? throw new BootloaderException("Brak odpowiedzi HELLO — urządzenie nie jest w trybie bootloadera.");
        if (resp.Length < 6)
            throw new BootloaderException("Nieprawidłowa odpowiedź HELLO (za krótka).");

        byte verMajor = resp[0];
        byte verMinor = resp[1];
        bool appValid = resp[2] != 0;
        ushort blockSize = BitConverter.ToUInt16(resp, 4);

        // Bajty [3] oraz [6:7] niosą dane dopiero od wersji 1.1; w wersji 1.0 są zerami.
        if (verMinor >= 1 && resp.Length >= 8)
            return new BlHelloInfo(verMajor, verMinor, appValid, blockSize,
                NodeId: resp[3], DeviceType: BitConverter.ToUInt16(resp, 6));

        return new BlHelloInfo(verMajor, verMinor, appValid, blockSize);
    }

    /// <summary>Polls HELLO until the bootloader answers (e.g. right after a reset / during its power-on "knock" window) or the timeout elapses.</summary>
    public async Task<BlHelloInfo> WaitForBootloaderAsync(TimeSpan timeout, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + timeout;
        Exception? last = null;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await HelloAsync(ct).ConfigureAwait(false);
            }
            catch (BootloaderException ex)
            {
                last = ex;
                await Task.Delay(150, ct).ConfigureAwait(false);
            }
        }
        throw new BootloaderException($"Bootloader nie odpowiada w {timeout.TotalSeconds:F0} s" + (last is null ? "." : $" ({last.Message})"));
    }

    public async Task EraseAsync(CancellationToken ct)
    {
        var resp = await link.RequestAsync(BlOpcodes.Erase, 0, ReadOnlyMemory<byte>.Empty, EraseTimeout, ct).ConfigureAwait(false)
            ?? throw new BootloaderException("Brak odpowiedzi ERASE.");
        CheckStatus(resp, "ERASE");
    }

    public async Task VerifyAsync(uint length, uint crc32, CancellationToken ct)
    {
        var payload = new byte[8];
        BitConverter.GetBytes(length).CopyTo(payload, 0);
        BitConverter.GetBytes(crc32).CopyTo(payload, 4);
        var resp = await link.RequestAsync(BlOpcodes.Verify, 0, payload, DefaultTimeout, ct).ConfigureAwait(false)
            ?? throw new BootloaderException("Brak odpowiedzi VERIFY.");
        CheckStatus(resp, "VERIFY");
    }

    public async Task GoAsync(CancellationToken ct)
    {
        var resp = await link.RequestAsync(BlOpcodes.Go, 0, new byte[] { 0xA5 }, DefaultTimeout, ct).ConfigureAwait(false)
            ?? throw new BootloaderException("Brak odpowiedzi GO.");
        CheckStatus(resp, "GO");
    }

    /// <summary>Full convenience sequence: HELLO → ERASE → program → VERIFY → GO, no optional steps.</summary>
    /// <param name="expectDeviceType">
    /// Typ urządzenia, dla którego przeznaczony jest obraz. Gdy bootloader zgłasza własny typ
    /// (wersja 1.1 lub nowsza) i wartości się różnią, wgrywanie jest przerywane przed skasowaniem
    /// pamięci. Obraz jednego typu węzła wgrany do innego wystartuje, lecz będzie sterował
    /// niewłaściwymi peryferiami, dlatego jest to odmowa, a nie ostrzeżenie.
    /// Bootloader 1.0 nie podaje typu i kontrola jest wtedy pomijana.
    /// </param>
    public async Task FlashAsync(FirmwareImage image, IProgress<BlFlashProgress>? progress,
        CancellationToken ct, ushort? expectDeviceType = null)
    {
        progress?.Report(new BlFlashProgress(0, image.Data.Length, "HELLO..."));
        var hello = await HelloAsync(ct).ConfigureAwait(false);

        if (hello.DeviceType is { } reported)
        {
            progress?.Report(new BlFlashProgress(0, image.Data.Length,
                $"Bootloader {hello.VersionText}, typ urządzenia 0x{reported:X4}"));

            if (expectDeviceType is { } expected && reported != expected)
                throw new BootloaderException(
                    $"Bootloader zgłasza typ urządzenia 0x{reported:X4}, a obraz jest przeznaczony " +
                    $"dla 0x{expected:X4}. Wgrywanie przerwane przed skasowaniem pamięci.");
        }

        progress?.Report(new BlFlashProgress(0, image.Data.Length, "ERASE..."));
        await EraseAsync(ct).ConfigureAwait(false);

        await ProgramAsync(image, hello.BlockSize, progress, ct).ConfigureAwait(false);

        progress?.Report(new BlFlashProgress(image.Data.Length, image.Data.Length, "VERIFY..."));
        await VerifyAsync((uint)image.Data.Length, image.Crc32Value, ct).ConfigureAwait(false);

        progress?.Report(new BlFlashProgress(image.Data.Length, image.Data.Length, "GO — restart do aplikacji"));
        await GoAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Just the block-programming loop (PROG_START/PROG_DATA×N/PROG_END per block) — the one step every flashing flow needs regardless of which optional steps (erase/go) surround it.</summary>
    public async Task ProgramAsync(FirmwareImage image, ushort blockSizeHint, IProgress<BlFlashProgress>? progress, CancellationToken ct)
    {
        var blockSize = blockSizeHint > 0 ? blockSizeHint : BlockSize;
        var data = image.Data;
        for (var offset = 0; offset < data.Length; offset += blockSize)
        {
            ct.ThrowIfCancellationRequested();
            var len = Math.Min(blockSize, data.Length - offset);
            await ProgramBlockAsync(data, offset, len, ct).ConfigureAwait(false);
            progress?.Report(new BlFlashProgress(offset + len, data.Length, $"Zapisano {offset + len}/{data.Length} B"));
        }
    }

    private async Task ProgramBlockAsync(byte[] data, int offset, int len, CancellationToken ct)
    {
        const int maxAttempts = 3;
        var startAt = 0; // chunk index (relative to block start) to (re)send from
        BootloaderException? lastTimeout = null;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                if (startAt == 0)
                {
                    var startPayload = new byte[6];
                    BitConverter.GetBytes((uint)offset).CopyTo(startPayload, 0);
                    BitConverter.GetBytes((ushort)len).CopyTo(startPayload, 4);
                    var startResp = await link.RequestAsync(BlOpcodes.ProgStart, 0, startPayload, DefaultTimeout, ct).ConfigureAwait(false)
                        ?? throw new BootloaderException("Brak odpowiedzi PROG_START.");
                    CheckStatus(startResp, "PROG_START");
                }

                for (var i = startAt * ChunkSize; i < len; i += ChunkSize)
                {
                    var n = Math.Min(ChunkSize, len - i);
                    var seq = (byte)(i / ChunkSize);
                    await link.SendNoResponseAsync(BlOpcodes.ProgData, seq, data.AsMemory(offset + i, n), ct).ConfigureAwait(false);
                    await Task.Delay(InterChunkDelay, ct).ConfigureAwait(false);
                }

                var crc = Crc16CcittFalse.Compute(data.AsSpan(offset, len));
                var endResp = await link.RequestAsync(BlOpcodes.ProgEnd, 0, BitConverter.GetBytes(crc), DefaultTimeout, ct).ConfigureAwait(false)
                    ?? throw new BootloaderException("Brak odpowiedzi PROG_END.");

                var status = (BlStatus)endResp[0];
                if (status == BlStatus.Ok) return;
                if (status == BlStatus.Seq && endResp.Length > 1)
                {
                    startAt = endResp[1]; // first_missing chunk index — resend from there, no new PROG_START
                    continue;
                }
                throw new BootloaderException($"PROG_END: błąd bootloadera ({status}).");
            }
            catch (BootloaderException ex) when (ex.Message is "Brak odpowiedzi PROG_START." or "Brak odpowiedzi PROG_END.")
            {
                // A single dropped request/response (e.g. the target's 3-deep, interrupt-free CAN RX
                // FIFO briefly overrun by the PROG_DATA burst) is transient — restart this block from a
                // fresh PROG_START, same as an explicit SEQ NAK, instead of aborting the whole flash.
                lastTimeout = ex;
                startAt = 0;
            }
        }

        throw lastTimeout ?? new BootloaderException("PROG_END: przekroczono liczbę prób ponowienia bloku.");
    }

    private static void CheckStatus(byte[] resp, string step)
    {
        if (resp.Length == 0 || (BlStatus)resp[0] != BlStatus.Ok)
            throw new BootloaderException($"{step}: błąd bootloadera ({(resp.Length > 0 ? (BlStatus)resp[0] : "brak danych")}).");
    }
}
