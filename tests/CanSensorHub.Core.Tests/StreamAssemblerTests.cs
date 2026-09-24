using CanSensorHub.Core.Protocol;

namespace CanSensorHub.Core.Tests;

/// <summary>
/// Składanie transferów segmentowanych. Najważniejszy przypadek to odbiór segmentów
/// w kolejności innej niż nadana (zaobserwowany na sprzęcie: 0, 1, 3, 2) — kontroler
/// CAN nadaje z kilku skrzynek, a przy jednakowych identyfikatorach o kolejności
/// decyduje numer skrzynki, nie moment zgłoszenia.
/// </summary>
public class StreamAssemblerTests
{
    private static StreamSegment Seg(int index, bool last, params byte[] payload)
        => new(Node: 0x01, Obj: 0x40, Arg: 0x00, Index: index, Last: last, Payload: payload);

    [Fact]
    public void Transfer_jednosegmentowy_konczy_sie_od_razu()
    {
        var assembler = new StreamAssembler();

        var result = assembler.Push(Seg(0, last: true, 0x11, 0x22));

        Assert.Equal(new byte[] { 0x11, 0x22 }, result);
    }

    [Fact]
    public void Segmenty_w_kolejnosci_sklejaja_sie_po_ostatnim()
    {
        var assembler = new StreamAssembler();

        Assert.Null(assembler.Push(Seg(0, false, 0x01, 0x02)));
        Assert.Null(assembler.Push(Seg(1, false, 0x03, 0x04)));
        var result = assembler.Push(Seg(2, last: true, 0x05));

        Assert.Equal(new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05 }, result);
    }

    [Fact]
    public void Segment_koncowy_przed_brakujacym_nie_konczy_transferu()
    {
        // Regresja: sekwencja 0, 1, 3, 2 z prawdziwej magistrali. Segment z bitem
        // "last" wyznacza tylko liczbę segmentów; dopóki brakuje któregoś ze
        // środka, transfer jest niekompletny.
        var assembler = new StreamAssembler();

        Assert.Null(assembler.Push(Seg(0, false, 0xA0)));
        Assert.Null(assembler.Push(Seg(1, false, 0xA1)));
        Assert.Null(assembler.Push(Seg(3, last: true, 0xA3)));   // koniec dotarł przed segmentem 2
        var result = assembler.Push(Seg(2, false, 0xA2));

        Assert.Equal(new byte[] { 0xA0, 0xA1, 0xA2, 0xA3 }, result);
    }

    [Fact]
    public void Segmenty_odebrane_w_odwrotnej_kolejnosci_skladaja_sie_poprawnie()
    {
        var assembler = new StreamAssembler();

        Assert.Null(assembler.Push(Seg(2, last: true, 0x03)));
        Assert.Null(assembler.Push(Seg(1, false, 0x02)));
        var result = assembler.Push(Seg(0, false, 0x01));

        Assert.Equal(new byte[] { 0x01, 0x02, 0x03 }, result);
    }

    [Fact]
    public void Transfery_roznych_wezlow_nie_mieszaja_sie()
    {
        // Dwa węzły mogą nadawać transfery równolegle — bufory są kluczowane
        // trójką (węzeł, obiekt, argument).
        var assembler = new StreamAssembler();

        Assert.Null(assembler.Push(new StreamSegment(0x01, 0x40, 0x00, 0, false, [0xAA])));
        Assert.Null(assembler.Push(new StreamSegment(0x10, 0x40, 0x00, 0, false, [0xBB])));

        var fromNode1 = assembler.Push(new StreamSegment(0x01, 0x40, 0x00, 1, true, [0xA1]));
        var fromNode16 = assembler.Push(new StreamSegment(0x10, 0x40, 0x00, 1, true, [0xB1]));

        Assert.Equal(new byte[] { 0xAA, 0xA1 }, fromNode1);
        Assert.Equal(new byte[] { 0xBB, 0xB1 }, fromNode16);
    }

    [Fact]
    public void Transfery_roznych_obiektow_tego_samego_wezla_nie_mieszaja_sie()
    {
        var assembler = new StreamAssembler();

        Assert.Null(assembler.Push(new StreamSegment(0x01, 0x40, 0x00, 0, false, [0x40])));
        Assert.Null(assembler.Push(new StreamSegment(0x01, 0x41, 0x00, 0, false, [0x41])));

        Assert.Equal(new byte[] { 0x40, 0x4F },
            assembler.Push(new StreamSegment(0x01, 0x40, 0x00, 1, true, [0x4F])));
        Assert.Equal(new byte[] { 0x41, 0x4E },
            assembler.Push(new StreamSegment(0x01, 0x41, 0x00, 1, true, [0x4E])));
    }

    [Fact]
    public void Ten_sam_transfer_moze_byc_zlozony_dwa_razy_pod_rzad()
    {
        // Po złożeniu bufor musi zostać zwolniony, inaczej kolejne odpytanie tego
        // samego obiektu zobaczyłoby pozostałości poprzedniego transferu.
        var assembler = new StreamAssembler();

        Assert.Equal(new byte[] { 0x01 }, assembler.Push(Seg(0, true, 0x01)));
        Assert.Equal(new byte[] { 0x02 }, assembler.Push(Seg(0, true, 0x02)));
    }

    [Fact]
    public void Expire_zwraca_indeksy_brakujacych_segmentow()
    {
        var assembler = new StreamAssembler();
        assembler.Push(Seg(0, false, 0x01));
        assembler.Push(Seg(3, last: true, 0x04));

        var missing = assembler.Expire(0x01, 0x40, 0x00);

        Assert.Equal(new[] { 1, 2 }, missing);
    }

    [Fact]
    public void Expire_bez_segmentu_koncowego_zglasza_braki_do_najwyzszego_indeksu()
    {
        // Bez bitu "last" nie wiadomo, ile segmentów miało przyjść — raportujemy
        // luki do najwyższego widzianego indeksu.
        var assembler = new StreamAssembler();
        assembler.Push(Seg(0, false, 0x01));
        assembler.Push(Seg(2, false, 0x03));

        Assert.Equal(new[] { 1 }, assembler.Expire(0x01, 0x40, 0x00));
    }

    [Fact]
    public void Expire_nieistniejacego_transferu_zwraca_null()
    {
        Assert.Null(new StreamAssembler().Expire(0x01, 0x40, 0x00));
    }

    [Fact]
    public void Expire_porzuca_bufor_i_nie_skleja_niekompletnych_danych()
    {
        // Sklejenie tego, co dotarło, byłoby groźniejsze niż brak danych: odbiorca
        // dostałby krótszy bufor bez żadnego sygnału błędu.
        var assembler = new StreamAssembler();
        assembler.Push(Seg(0, false, 0x01));
        assembler.Push(Seg(2, last: true, 0x03));

        assembler.Expire(0x01, 0x40, 0x00);

        // Po porzuceniu bufor jest pusty — spóźniony segment 1 nie może dokończyć
        // starego transferu, a drugie Expire nie ma już czego zgłosić.
        Assert.Null(assembler.Push(Seg(1, false, 0x02)));
        Assert.Equal(new[] { 0 }, assembler.Expire(0x01, 0x40, 0x00));
    }

    [Fact]
    public void Powtorzona_ramka_nie_psuje_transferu_ani_nie_jest_zglaszana()
    {
        // Kontroler CAN powtarza ramkę, której nadania nie potwierdził, choć odbiorca mógł ją
        // już przyjąć — ten sam indeks o tej samej treści jest więc zjawiskiem normalnym.
        var assembler = new StreamAssembler();
        var abandoned = new List<StreamTransferAbandoned>();
        assembler.TransferAbandoned += (_, a) => abandoned.Add(a);

        assembler.Push(Seg(0, false, 0x01));
        assembler.Push(Seg(0, false, 0x01));

        Assert.Equal(new byte[] { 0x01, 0x02 }, assembler.Push(Seg(1, last: true, 0x02)));
        Assert.Empty(abandoned);
    }

    [Fact]
    public void Zgubiony_segment_nie_przesuwa_skladania_kolejnych_transferow()
    {
        // Regresja: po utracie jednego segmentu niekompletny transfer był dopełniany segmentami
        // następnego odpytania. Każda kolejna odpowiedź łączyła wtedy świeże wartości (indeksy
        // do zgubionego włącznie) z wartościami sprzed jednego odpytania — na wykresie część
        // kanałów pokrywała się z telemetrią, a część była przesunięta o okres odpytywania.
        var time = new ManualTimeProvider();
        var assembler = new StreamAssembler(time);
        var abandoned = new List<StreamTransferAbandoned>();
        assembler.TransferAbandoned += (_, a) => abandoned.Add(a);

        // Odpytanie A: segment 3 zgubiony.
        foreach (var i in new[] { 0, 1, 2, 4, 5 }) Assert.Null(assembler.Push(Seg(i, false, (byte)(0xA0 + i))));
        Assert.Null(assembler.Push(Seg(6, last: true, 0xA6)));

        for (var transfer = 0; transfer < 3; transfer++)
        {
            time.Advance(TimeSpan.FromSeconds(2));
            var tag = (byte)(0xB0 + 0x10 * transfer);
            byte[]? result = null;
            for (var i = 0; i < 7; i++)
            {
                result = assembler.Push(Seg(i, i == 6, (byte)(tag + i)));
                if (i < 6) Assert.Null(result);
            }
            Assert.Equal(Enumerable.Range(0, 7).Select(i => (byte)(tag + i)).ToArray(), result);
        }

        var a = Assert.Single(abandoned);
        Assert.Equal(StreamAbandonReason.Timeout, a.Reason);
        Assert.Equal(new[] { 3 }, a.MissingIndices);
    }

    [Fact]
    public void Transfer_z_przerwami_w_granicach_limitu_sklada_sie()
    {
        var time = new ManualTimeProvider();
        var assembler = new StreamAssembler(time);

        Assert.Null(assembler.Push(Seg(0, false, 0x01)));
        time.Advance(StreamAssembler.DefaultSegmentGapTimeout);
        Assert.Null(assembler.Push(Seg(1, false, 0x02)));
        time.Advance(StreamAssembler.DefaultSegmentGapTimeout);

        Assert.Equal(new byte[] { 0x01, 0x02, 0x03 }, assembler.Push(Seg(2, last: true, 0x03)));
    }

    [Fact]
    public void Kolejny_transfer_przed_uplywem_limitu_zastepuje_niekompletny()
    {
        // Dwa odpytania tego samego obiektu tuż po sobie: segment o już zebranym indeksie, lecz
        // innej treści, otwiera nowy transfer zamiast dopełniać poprzedni.
        var assembler = new StreamAssembler(new ManualTimeProvider());
        var abandoned = new List<StreamTransferAbandoned>();
        assembler.TransferAbandoned += (_, a) => abandoned.Add(a);

        Assert.Null(assembler.Push(Seg(0, false, 0xA0)));
        Assert.Null(assembler.Push(Seg(2, last: true, 0xA2)));   // segment 1 zgubiony

        Assert.Null(assembler.Push(Seg(0, false, 0xB0)));
        Assert.Null(assembler.Push(Seg(1, false, 0xB1)));
        Assert.Equal(new byte[] { 0xB0, 0xB1, 0xB2 }, assembler.Push(Seg(2, last: true, 0xB2)));

        var a = Assert.Single(abandoned);
        Assert.Equal(StreamAbandonReason.Superseded, a.Reason);
        Assert.Equal(new[] { 1 }, a.MissingIndices);
    }

    /// <summary>Zegar sterowany z testu — limit przerwy między segmentami liczony jest względem niego.</summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _ticks;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan by) => _ticks += by.Ticks;
    }
}
