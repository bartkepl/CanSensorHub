using CanSensorHub.Core.Protocol;

namespace CanSensorHub.Core.Tests;

/// <summary>
/// Układ 29-bitowego identyfikatora jest wspólny bit-w-bit dla wszystkich węzłów na
/// magistrali. Każda zmiana tutaj rozjeżdża aplikację z firmware, więc układ jest
/// przybity testami do konkretnych wartości liczbowych, nie do samego kodu.
/// </summary>
public class CanIdTests
{
    [Theory]
    [InlineData(0x08, 0x10, 0x21, 0x00, 0x08102100u)]  // REQUEST do węzła 0x10, opcode 0x21
    [InlineData(0x09, 0x10, 0x21, 0x00, 0x09102100u)]  // RESPONSE z tego samego węzła
    [InlineData(0x10, 0x01, 0x03, 0x00, 0x10010300u)]  // TELEMETRY, kanał 3
    [InlineData(0x01, 0xFF, 0x02, 0x7B, 0x01FF027Bu)]  // EVENT rozgłoszeniowy, kod 123
    public void Pack_uklada_pola_w_wyznaczonych_bitach(byte func, byte node, byte obj, byte arg, uint expected)
    {
        Assert.Equal(expected, new CanId(func, node, obj, arg).Pack());
    }

    [Theory]
    [InlineData(0x08102100u, 0x08, 0x10, 0x21, 0x00)]
    [InlineData(0x0AFF01FFu, 0x0A, 0xFF, 0x01, 0xFF)]
    public void Unpack_rozklada_identyfikator_na_pola(uint id, byte func, byte node, byte obj, byte arg)
    {
        Assert.Equal(new CanId(func, node, obj, arg), CanId.Unpack(id));
    }

    [Fact]
    public void Pack_i_Unpack_sa_wzajemnie_odwrotne_dla_calego_zakresu_pol()
    {
        // Pełny przegląd byłby 2^29 kombinacji; wystarczy siatka pokrywająca
        // skrajne i typowe wartości każdego pola.
        byte[] funcs = [0x00, 0x01, 0x08, 0x09, 0x0A, 0x10, 0x1F];
        byte[] octets = [0x00, 0x01, 0x10, 0x7F, 0x80, 0xFE, 0xFF];

        foreach (var func in funcs)
            foreach (var node in octets)
                foreach (var obj in octets)
                    foreach (var arg in octets)
                    {
                        var id = new CanId(func, node, obj, arg);
                        Assert.Equal(id, CanId.Unpack(id.Pack()));
                    }
    }

    [Fact]
    public void Pack_przycina_FUNC_do_pieciu_bitow()
    {
        // FUNC zajmuje bity [28:24]. Wartość spoza zakresu nie może przelać się
        // na bity 29+ — ramka rozszerzona ma dokładnie 29 bitów i CanFrame
        // odrzuciłby większy identyfikator.
        var packed = new CanId(0xFF, 0x10, 0x00, 0x00).Pack();

        Assert.Equal(0x1F100000u, packed);
        Assert.True(packed <= 0x1FFFFFFFu);
    }

    [Fact]
    public void Unpack_ignoruje_bity_powyzej_29()
    {
        // Identyfikator odebrany z transportu może mieć ustawione wyższe bity
        // (np. flagę ramki rozszerzonej doklejoną przez sterownik) — FUNC musi
        // pozostać pięciobitowy.
        Assert.Equal(0x1F, CanId.Unpack(0xFFFFFFFFu).Func);
    }

    [Fact]
    public void Adresy_specjalne_maja_ustalone_wartosci()
    {
        Assert.Equal(0xFF, CanId.NodeBroadcast);
        Assert.Equal(0x00, CanId.NodeUnconfigured);
    }

    [Theory]
    [InlineData(CanFunc.Event, 0x01)]
    [InlineData(CanFunc.Request, 0x08)]
    [InlineData(CanFunc.Response, 0x09)]
    [InlineData(CanFunc.Stream, 0x0A)]
    [InlineData(CanFunc.Telemetry, 0x10)]
    public void Kody_FUNC_maja_wartosci_uzgodnione_z_firmware(CanFunc func, byte expected)
    {
        Assert.Equal(expected, (byte)func);
    }

    [Theory]
    [InlineData(StatusCode.Ok, 0x00)]
    [InlineData(StatusCode.ErrUnknownCmd, 0x01)]
    [InlineData(StatusCode.ErrBadParam, 0x02)]
    [InlineData(StatusCode.ErrOutOfRange, 0x03)]
    [InlineData(StatusCode.ErrSensorAbsent, 0x04)]
    [InlineData(StatusCode.ErrSensorFault, 0x05)]
    [InlineData(StatusCode.ErrBusy, 0x06)]
    [InlineData(StatusCode.ErrReadonly, 0x07)]
    [InlineData(StatusCode.ErrNotReady, 0x08)]
    [InlineData(StatusCode.ErrCrc, 0x09)]
    public void Kody_statusu_maja_wartosci_uzgodnione_z_firmware(StatusCode status, byte expected)
    {
        Assert.Equal(expected, (byte)status);
    }

    [Fact]
    public void Priorytet_na_magistrali_rosnie_wraz_ze_spadkiem_FUNC()
    {
        // Arbitraż CAN wygrywa niższy identyfikator. Zdarzenie musi przebić telemetrię.
        var evt = new CanId((byte)CanFunc.Event, 0x10, 0, 0).Pack();
        var telemetry = new CanId((byte)CanFunc.Telemetry, 0x01, 0, 0).Pack();

        Assert.True(evt < telemetry);
    }
}
