namespace CanSensorHub.Core.Protocol;

/// <summary>
/// Generacja protokołu, którą posługuje się węzeł. Na jednej magistrali mogą pracować węzły
/// w różnych profilach: moduł zalany aktualizowany jest przez CAN, a egzemplarze rozwojowe
/// bywają przywracane z archiwalnych obrazów, więc obsługa starszych profili pozostaje wymagana.
/// </summary>
public enum DeviceProfile : byte
{
    /// <summary>Ramka identyfikacyjna 4-bajtowa: PROTO_VERSION, FW_MAJOR, FW_MINOR, NODE.</summary>
    Legacy4B = 0,

    /// <summary>Ramka 21-bajtowa: powyższe oraz rewizja sprzętu, licznik budowy, flagi i UID.</summary>
    Legacy21B = 1,

    /// <summary>Ramka 28-bajtowa: powyższe oraz DEVICE_TYPE, PROFILE_VERSION i CAPABILITIES.</summary>
    Common = 2,
}

/// <summary>
/// Zdekodowana ramka identyfikacyjna (odpowiedź GET_INFO), wzorowana na komendzie 0 protokołu HART.
///
/// Układ pól [0:20] jest zamrożony i zgodny wstecz, dlatego profil rozpoznaje się po DŁUGOŚCI
/// odpowiedzi, a nie po jej zawartości. Węzeł profilu 2 podaje numer profilu wprost — wtedy
/// pierwszeństwo ma deklaracja węzła, a długość służy jako kontrola.
///
/// Pola spoza profilu węzła pozostają puste. Prezentacja wartości domyślnej zamiast pustej
/// oznaczałaby zgadywanie: zero jest poprawną wartością DEVICE_TYPE w rejestrze rezerwowym.
/// </summary>
public sealed record DeviceIdentity(
    byte ProtocolVersion,
    byte FwMajor,
    byte FwMinor,
    byte Node,
    byte? HwMajor = null,
    byte? HwMinor = null,
    ushort? BuildRevision = null,
    byte? BuildFlags = null,
    byte[]? Uid = null,
    ushort? DeviceType = null,
    byte? ProfileVersion = null,
    uint? Capabilities = null)
{
    public const int Length4B = 4;
    public const int Length21B = 21;
    public const int Length28B = 28;

    // Przesunięcia pól — układ info_layout warstwy wspólnej.
    public const int OffProtocolVersion = 0;
    public const int OffFwMajor = 1;
    public const int OffFwMinor = 2;
    public const int OffNode = 3;
    public const int OffHwMajor = 4;
    public const int OffHwMinor = 5;
    public const int OffBuildRevision = 6;
    public const int OffBuildFlags = 8;
    public const int OffUid = 9;
    public const int UidLength = 12;
    public const int OffDeviceType = 21;
    public const int OffProfileVersion = 23;
    public const int OffCapabilities = 24;

    /// <summary>Bit 0 bajtu BUILD_FLAGS — obraz zbudowany w konfiguracji Debug.</summary>
    public bool IsDebugBuild => BuildFlags is { } f && (f & 0x01) != 0;

    public bool HasExtendedInfo => HwMajor.HasValue;

    public string? UidHex => Uid is null ? null : Convert.ToHexString(Uid);

    public DeviceProfile Profile => ProfileVersion switch
    {
        >= (byte)DeviceProfile.Common => DeviceProfile.Common,
        _ when HwMajor.HasValue => DeviceProfile.Legacy21B,
        _ => DeviceProfile.Legacy4B,
    };

    public bool Supports(DeviceCapability capability) =>
        Capabilities is { } caps && (caps & (uint)capability) != 0;

    /// <summary>
    /// Dekoduje odpowiedź GET_INFO dowolnej generacji. Krótsza odpowiedź nie jest błędem — oznacza
    /// starszy profil węzła.
    /// </summary>
    public static DeviceIdentity Parse(ReadOnlySpan<byte> data)
    {
        if (data.Length < Length4B)
            throw new ArgumentException(
                $"Ramka identyfikacyjna ma {data.Length} B; minimum to {Length4B} B.", nameof(data));

        byte proto = data[OffProtocolVersion];
        byte fwMajor = data[OffFwMajor];
        byte fwMinor = data[OffFwMinor];
        byte node = data[OffNode];

        if (data.Length < Length21B)
            return new DeviceIdentity(proto, fwMajor, fwMinor, node);

        byte hwMajor = data[OffHwMajor];
        byte hwMinor = data[OffHwMinor];
        ushort buildRev = BitConverter.ToUInt16(data.Slice(OffBuildRevision, 2));
        byte buildFlags = data[OffBuildFlags];
        byte[] uid = data.Slice(OffUid, UidLength).ToArray();

        if (data.Length < Length28B)
            return new DeviceIdentity(proto, fwMajor, fwMinor, node,
                hwMajor, hwMinor, buildRev, buildFlags, uid);

        return new DeviceIdentity(proto, fwMajor, fwMinor, node,
            hwMajor, hwMinor, buildRev, buildFlags, uid,
            DeviceType: BitConverter.ToUInt16(data.Slice(OffDeviceType, 2)),
            ProfileVersion: data[OffProfileVersion],
            Capabilities: BitConverter.ToUInt32(data.Slice(OffCapabilities, 4)));
    }
}

/// <summary>
/// Bitmapa zdolności węzła, zgłaszana w GET_INFO i przez komendę GET_CAPABILITIES. Pozwala hostowi
/// dostosować interfejs do tego, co węzeł faktycznie obsługuje, bez wbudowanej wiedzy o jego typie.
/// Wartości odpowiadają sekcji <c>capabilities</c> warstwy wspólnej.
/// </summary>
[Flags]
public enum DeviceCapability : uint
{
    None = 0,
    Telemetry = 1u << 0,
    Sensors = 1u << 1,
    ParamsNv = 1u << 2,
    Rtc = 1u << 3,
    Bootloader = 1u << 4,
    TimeSync = 1u << 5,
    ListParams = 1u << 6,
    ListChannels = 1u << 7,
    Counters = 1u << 8,
    SelfTest = 1u << 9,
    CalLock = 1u << 10,
    RangeLimits = 1u << 11,
    AddrConflictDetect = 1u << 12,
    StatusExt = 1u << 13,
    SensorFusion = 1u << 14,
    Outputs = 1u << 15,
    Display = 1u << 16,
    Gnss = 1u << 17,
    Radiometry = 1u << 18,
    Lightning = 1u << 19,
}

/// <summary>
/// Bajty zabezpieczające komend nieodwracalnych, wspólne dla wszystkich węzłów. Ramka bez właściwej
/// wartości jest odrzucana kodem ERR_BAD_PARAM, co chroni przed wykonaniem komendy w następstwie
/// zakłócenia na magistrali.
/// </summary>
public static class CommandGuard
{
    /// <summary>LOAD_DEFAULTS — komenda kasuje konfigurację wraz z kalibracją.</summary>
    public const byte LoadDefaults = 0x5A;

    /// <summary>RESET.</summary>
    public const byte Reset = 0xA5;

    /// <summary>ENTER_BOOTLOADER.</summary>
    public const byte EnterBootloader = 0xB0;
}
