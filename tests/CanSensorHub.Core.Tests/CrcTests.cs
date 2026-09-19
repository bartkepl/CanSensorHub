using System.Text;
using CanSensorHub.Core.Bootloader;

namespace CanSensorHub.Core.Tests;

/// <summary>
/// Obie sumy kontrolne muszą zgadzać się co do bitu z implementacjami po drugiej
/// stronie: CRC32 liczy bootloader w firmware przy VERIFY, a CRC16 chroni ramki
/// protokołu. Testy używają wartości wzorcowych z katalogu CRC (ciąg "123456789"),
/// więc sprawdzają zgodność ze standardem, a nie samą powtarzalność kodu.
/// </summary>
public class CrcTests
{
    private static byte[] Check => Encoding.ASCII.GetBytes("123456789");

    [Fact]
    public void Crc32_zwraca_wartosc_wzorcowa_dla_123456789()
    {
        // Ten sam wynik daje zlib.crc32 w Pythonie, którym posługują się hosty pythonowe.
        Assert.Equal(0xCBF43926u, Crc32.Compute(Check));
    }

    [Fact]
    public void Crc32_pustego_wejscia_wynosi_zero()
    {
        Assert.Equal(0u, Crc32.Compute(ReadOnlySpan<byte>.Empty));
    }

    [Theory]
    [InlineData(new byte[] { 0x00 }, 0xD202EF8Du)]
    [InlineData(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, 0xFFFFFFFFu)]
    [InlineData(new byte[] { 0x00, 0x00, 0x00, 0x00 }, 0x2144DF1Cu)]
    public void Crc32_zgadza_sie_z_wartosciami_referencyjnymi(byte[] data, uint expected)
    {
        Assert.Equal(expected, Crc32.Compute(data));
    }

    [Fact]
    public void Crc32_wykrywa_przestawienie_bajtow()
    {
        // Suma wrażliwa na kolejność — inaczej zamiana dwóch doubleword w obrazie
        // firmware przeszłaby weryfikację.
        Assert.NotEqual(
            Crc32.Compute([0x01, 0x02, 0x03, 0x04]),
            Crc32.Compute([0x04, 0x03, 0x02, 0x01]));
    }

    [Fact]
    public void Crc16_zwraca_wartosc_wzorcowa_dla_123456789()
    {
        // CRC-16/CCITT-FALSE, wartość kontrolna z katalogu CRC.
        Assert.Equal((ushort)0x29B1, Crc16CcittFalse.Compute(Check));
    }

    [Fact]
    public void Crc16_pustego_wejscia_to_wartosc_poczatkowa()
    {
        Assert.Equal((ushort)0xFFFF, Crc16CcittFalse.Compute(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Crc16_wykrywa_przekrecenie_pojedynczego_bitu()
    {
        var original = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };
        var flipped = new byte[] { 0xDE, 0xAD, 0xBE, 0xEE };

        Assert.NotEqual(Crc16CcittFalse.Compute(original), Crc16CcittFalse.Compute(flipped));
    }

    [Fact]
    public void Crc32_wiodace_zera_zmieniaja_wynik()
    {
        // Dopełnienie obrazu z przodu nie może być niewidoczne dla sumy —
        // inaczej przesunięty obraz firmware zweryfikowałby się poprawnie.
        Assert.NotEqual(
            Crc32.Compute([0x01, 0x02]),
            Crc32.Compute([0x00, 0x01, 0x02]));
    }
}
