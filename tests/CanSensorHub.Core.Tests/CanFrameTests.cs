using CanSensorHub.Core.Can;

namespace CanSensorHub.Core.Tests;

/// <summary>
/// Ograniczenia klasycznej ramki CAN 2.0. Kontrola jest tutaj, a nie w transporcie,
/// żeby błędny identyfikator albo zbyt długi ładunek nie dotarł do adaptera.
/// </summary>
public class CanFrameTests
{
    [Fact]
    public void Ramka_rozszerzona_przyjmuje_maksymalny_identyfikator_29_bitowy()
    {
        var frame = new CanFrame(CanFrame.MaxExtendedId, []);

        Assert.Equal(0x1FFFFFFFu, frame.Id);
        Assert.True(frame.IsExtended);
    }

    [Fact]
    public void Identyfikator_powyzej_29_bitow_jest_odrzucany()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CanFrame(0x20000000u, []));
    }

    [Fact]
    public void Ramka_standardowa_ograniczona_jest_do_11_bitow()
    {
        Assert.Equal(0x7FFu, new CanFrame(0x7FFu, [], isExtended: false).Id);
        Assert.Throws<ArgumentOutOfRangeException>(() => new CanFrame(0x800u, [], isExtended: false));
    }

    [Fact]
    public void Ladunek_dluzszy_niz_osiem_bajtow_jest_odrzucany()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CanFrame(0x100u, new byte[9]));
    }

    [Fact]
    public void Ladunek_osmiobajtowy_jest_dopuszczalny()
    {
        Assert.Equal(8, new CanFrame(0x100u, new byte[8]).Data.Length);
    }

    [Fact]
    public void Ramka_kopiuje_dane_zamiast_trzymac_referencji()
    {
        // Bufor odbiorczy transportu jest wielokrotnego użytku — ramka musi
        // przetrwać jego nadpisanie.
        var buffer = new byte[] { 0x01, 0x02 };
        var frame = new CanFrame(0x100u, buffer);

        buffer[0] = 0xFF;

        Assert.Equal(0x01, frame.Data[0]);
    }

    [Fact]
    public void ToString_pokazuje_identyfikator_i_bajty_szesnastkowo()
    {
        var text = new CanFrame(0x08102100u, [0xDE, 0xAD]).ToString();

        Assert.Equal("0x08102100 [2] DE AD", text);
    }
}
