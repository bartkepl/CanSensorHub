using CanSensorHub.Core.Bootloader;

namespace CanSensorHub.Core.Tests;

/// <summary>
/// Wczytywanie obrazu firmware. Błąd w dopełnianiu albo w parserze Intel HEX kończy
/// się wgraniem złego obrazu do węzła, więc testy sprawdzają dokładną zawartość
/// bufora, a nie tylko to, że plik się wczytał.
/// </summary>
public class FirmwareImageTests : IDisposable
{
    private readonly List<string> _tempFiles = [];

    private string WriteTemp(string extension, byte[] content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"cansensorhub-test-{Guid.NewGuid():N}{extension}");
        File.WriteAllBytes(path, content);
        _tempFiles.Add(path);
        return path;
    }

    private string WriteTemp(string extension, string content)
        => WriteTemp(extension, System.Text.Encoding.ASCII.GetBytes(content));

    public void Dispose()
    {
        foreach (var path in _tempFiles)
        {
            try { File.Delete(path); } catch { }
        }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void Bin_o_dlugosci_wielokrotnej_osmiu_nie_jest_dopelniany()
    {
        var path = WriteTemp(".bin", [1, 2, 3, 4, 5, 6, 7, 8]);

        var image = FirmwareImage.LoadFromFile(path);

        Assert.Equal(8, image.Data.Length);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, image.Data);
    }

    [Theory]
    [InlineData(1, 8)]
    [InlineData(7, 8)]
    [InlineData(9, 16)]
    [InlineData(15, 16)]
    [InlineData(17, 24)]
    public void Bin_jest_dopelniany_do_wielokrotnosci_osmiu(int inputLength, int expectedLength)
    {
        // Bootloader programuje jedno 64-bitowe słowo flash na ramkę PROG_DATA,
        // więc obraz musi być wielokrotnością 8 bajtów.
        var path = WriteTemp(".bin", new byte[inputLength]);

        var image = FirmwareImage.LoadFromFile(path);

        Assert.Equal(expectedLength, image.Data.Length);
    }

    [Fact]
    public void Dopelnienie_uzywa_0xFF_czyli_stanu_skasowanej_flash()
    {
        var path = WriteTemp(".bin", [0xAA, 0xBB, 0xCC]);

        var image = FirmwareImage.LoadFromFile(path);

        Assert.Equal(new byte[] { 0xAA, 0xBB, 0xCC, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }, image.Data);
    }

    [Fact]
    public void Crc_liczony_jest_z_obrazu_po_dopelnieniu()
    {
        // Węzeł przy VERIFY liczy sumę z tego, co faktycznie zapisał we flash —
        // czyli z bufora razem z dopełnieniem.
        var path = WriteTemp(".bin", [0x01, 0x02, 0x03]);

        var image = FirmwareImage.LoadFromFile(path);

        Assert.Equal(Crc32.Compute(image.Data), image.Crc32Value);
        Assert.NotEqual(Crc32.Compute([0x01, 0x02, 0x03]), image.Crc32Value);
    }

    [Fact]
    public void Bin_ma_zerowy_adres_ladowania()
    {
        // Surowy zrzut nie niesie informacji o adresie — ustala go bootloader.
        var path = WriteTemp(".bin", [0x00]);

        Assert.Equal(0u, FirmwareImage.LoadFromFile(path).LoadAddress);
    }

    [Fact]
    public void SourcePath_zapamietuje_sciezke_zrodlowa()
    {
        var path = WriteTemp(".bin", [0x00]);

        Assert.Equal(path, FirmwareImage.LoadFromFile(path).SourcePath);
    }

    [Fact]
    public void Hex_sklada_rekordy_danych_w_plaski_obraz()
    {
        // :LL AAAA 00 <dane> CC — osiem bajtów pod adresem 0x0000, potem EOF.
        var path = WriteTemp(".hex", ":080000000102030405060708D4\n:00000001FF\n");

        var image = FirmwareImage.LoadFromFile(path);

        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, image.Data);
        Assert.Equal(0u, image.LoadAddress);
    }

    [Fact]
    public void Hex_uwzglednia_rekord_rozszerzonego_adresu_liniowego()
    {
        // Rekord 04 ustawia górne 16 bitów adresu — dla Cortex-M obraz zwykle
        // ląduje pod 0x0800xxxx, nie pod zerem.
        var path = WriteTemp(".hex",
            ":020000040800F2\n" +
            ":080000000102030405060708D4\n" +
            ":00000001FF\n");

        var image = FirmwareImage.LoadFromFile(path);

        Assert.Equal(0x08000000u, image.LoadAddress);
        Assert.Equal(8, image.Data.Length);
    }

    [Fact]
    public void Hex_wypelnia_luki_miedzy_rekordami_bajtem_0xFF()
    {
        // Dwa rekordy po 4 bajty rozdzielone dziurą: 0x0000 i 0x0008.
        var path = WriteTemp(".hex",
            ":0400000001020304F2\n" +
            ":0400080005060708DA\n" +
            ":00000001FF\n");

        var image = FirmwareImage.LoadFromFile(path);

        // 12 bajtow po sklejeniu, wiec jeszcze 4 bajty dopelnienia do wielokrotnosci osmiu
        Assert.Equal(
            new byte[] { 1, 2, 3, 4, 0xFF, 0xFF, 0xFF, 0xFF, 5, 6, 7, 8, 0xFF, 0xFF, 0xFF, 0xFF },
            image.Data);
    }

    [Fact]
    public void Hex_liczy_adres_ladowania_od_najnizszego_rekordu()
    {
        var path = WriteTemp(".hex",
            ":020000040800F2\n" +
            ":0400100001020304E2\n" +
            ":00000001FF\n");

        var image = FirmwareImage.LoadFromFile(path);

        Assert.Equal(0x08000010u, image.LoadAddress);
    }

    [Fact]
    public void Hex_ignoruje_rekordy_po_EOF()
    {
        var path = WriteTemp(".hex",
            ":0400000001020304F2\n" +
            ":00000001FF\n" +
            ":04000400AABBCCDDEA\n");   // poprawny rekord, ale za znacznikiem konca pliku

        var image = FirmwareImage.LoadFromFile(path);

        Assert.Equal(new byte[] { 1, 2, 3, 4, 0xFF, 0xFF, 0xFF, 0xFF }, image.Data);
    }

    [Fact]
    public void Hex_pomija_puste_linie_i_smieci_bez_dwukropka()
    {
        var path = WriteTemp(".hex",
            "\n" +
            "komentarz spoza formatu\n" +
            "   :0400000001020304F2   \n" +
            "\n" +
            ":00000001FF\n");

        Assert.Equal(new byte[] { 1, 2, 3, 4, 0xFF, 0xFF, 0xFF, 0xFF },
            FirmwareImage.LoadFromFile(path).Data);
    }

    [Fact]
    public void Hex_bez_rekordow_danych_jest_odrzucany()
    {
        var path = WriteTemp(".hex", ":00000001FF\n");

        var ex = Assert.Throws<InvalidDataException>(() => FirmwareImage.LoadFromFile(path));
        Assert.Contains("nie zawiera", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rozszerzenie_rozpoznawane_jest_bez_wzgledu_na_wielkosc_liter()
    {
        var path = WriteTemp(".HEX", ":0400000001020304F2\n:00000001FF\n");

        Assert.Equal(new byte[] { 1, 2, 3, 4, 0xFF, 0xFF, 0xFF, 0xFF },
            FirmwareImage.LoadFromFile(path).Data);
    }

    [Fact]
    public void Nieznane_rozszerzenie_czytane_jest_jako_surowy_bin()
    {
        var path = WriteTemp(".fw", [0xDE, 0xAD]);

        var image = FirmwareImage.LoadFromFile(path);

        Assert.Equal(0xDE, image.Data[0]);
        Assert.Equal(0xAD, image.Data[1]);
        Assert.Equal(8, image.Data.Length);
    }
}
