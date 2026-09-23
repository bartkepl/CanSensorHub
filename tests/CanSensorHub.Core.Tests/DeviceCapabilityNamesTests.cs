using CanSensorHub.Core.Protocol;

namespace CanSensorHub.Core.Tests;

/// <summary>
/// Nazwy zdolności trafiają na ekran obok raportu testera zgodności, który wypisuje je w postaci
/// wspólnej warstwy. Rozjazd nazewnictwa zmusiłby do tłumaczenia jednej listy na drugą przy każdym
/// porównaniu, więc postać jest przybita testami — nie wynika wyłącznie z kodu przekształcenia.
/// </summary>
public class DeviceCapabilityNamesTests
{
    [Theory]
    [InlineData(DeviceCapability.Telemetry, "TELEMETRY")]
    [InlineData(DeviceCapability.ParamsNv, "PARAMS_NV")]
    [InlineData(DeviceCapability.Rtc, "RTC")]                              // skrót w całości wielkimi
    [InlineData(DeviceCapability.Gnss, "GNSS")]
    [InlineData(DeviceCapability.AddrConflictDetect, "ADDR_CONFLICT_DETECT")]
    [InlineData(DeviceCapability.SensorFusion, "SENSOR_FUSION")]
    public void Pojedyncza_zdolnosc_dostaje_nazwe_warstwy_wspolnej(DeviceCapability capability, string expected)
    {
        Assert.Equal(expected, DeviceCapabilityNames.Describe((uint)capability));
    }

    /// <summary>
    /// Maski rzeczywistych węzłów, wprost z ramek identyfikacyjnych odczytanych podczas badania
    /// zgodności. Kolejność odpowiada numerom bitów, czyli tej samej, w której wypisuje je raport.
    /// </summary>
    [Fact]
    public void Maska_MPCC_odpowiada_liscie_z_raportu_zgodnosci()
    {
        Assert.Equal(
            "TELEMETRY, SENSORS, PARAMS_NV, RTC, BOOTLOADER, TIME_SYNC, LIST_PARAMS, LIST_CHANNELS, " +
            "COUNTERS, SELF_TEST, CAL_LOCK, ADDR_CONFLICT_DETECT, STATUS_EXT, OUTPUTS, DISPLAY",
            DeviceCapabilityNames.Describe(0x0001B7FFu));
    }

    [Fact]
    public void Maska_MPSWP_odpowiada_liscie_z_raportu_zgodnosci()
    {
        Assert.Equal(
            "TELEMETRY, SENSORS, PARAMS_NV, RTC, BOOTLOADER, TIME_SYNC, LIST_PARAMS, LIST_CHANNELS, " +
            "COUNTERS, SELF_TEST, CAL_LOCK, RANGE_LIMITS, ADDR_CONFLICT_DETECT, STATUS_EXT, " +
            "SENSOR_FUSION, LIGHTNING",
            DeviceCapabilityNames.Describe(0x00087FFFu));
    }

    /// <summary>
    /// Węzeł nowszy od tej aplikacji zgłosi bit, którego ona nie zna. Pominięcie go sprawiłoby, że
    /// bitmapa wygląda na w pełni zrozumianą — a to jest gorsze niż jawne „nie wiem".
    /// </summary>
    [Fact]
    public void Bit_spoza_wyliczenia_jest_wypisany_nie_pominiety()
    {
        Assert.Equal("TELEMETRY, bit20", DeviceCapabilityNames.Describe(0x00100001u));
    }

    [Fact]
    public void Pusta_bitmapa_nie_daje_pustego_lancucha()
    {
        Assert.Equal("brak", DeviceCapabilityNames.Describe(0));
    }
}
