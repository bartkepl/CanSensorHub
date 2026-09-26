using System.Runtime.InteropServices;

namespace CanSensorHub.Metrology.Visa;

/// <summary>
/// Wywołania biblioteki VISA (specyfikacja VPP-4.3, implementacja współdzielona IVI Foundation —
/// NI-VISA, Keysight IO Libraries). Uchwyty sesji są typu <c>ViUInt32</c> niezależnie od bitowości
/// procesu, a <c>ViAttrState</c> ma szerokość wskaźnika — stąd <c>uint</c> i <c>nuint</c>.
/// </summary>
internal static class VisaNative
{
    private const string DllName = "visa64.dll";

    public const uint AttrTimeoutMs = 0x3FFF001A;

    public const int StatusTimeout = unchecked((int)0xBFFF0015);
    public const int StatusResourceNotFound = unchecked((int)0xBFFF0011);

    [DllImport(DllName, EntryPoint = "viOpenDefaultRM")]
    public static extern int OpenDefaultRM(out uint session);

    [DllImport(DllName, EntryPoint = "viFindRsrc", CharSet = CharSet.Ansi, BestFitMapping = false)]
    public static extern int FindRsrc(uint session, string expression, out uint findList, out uint count,
        [Out] byte[] description);

    [DllImport(DllName, EntryPoint = "viFindNext")]
    public static extern int FindNext(uint findList, [Out] byte[] description);

    [DllImport(DllName, EntryPoint = "viOpen", CharSet = CharSet.Ansi, BestFitMapping = false)]
    public static extern int Open(uint session, string resourceName, uint accessMode, uint openTimeoutMs, out uint vi);

    [DllImport(DllName, EntryPoint = "viClose")]
    public static extern int Close(uint vi);

    [DllImport(DllName, EntryPoint = "viClear")]
    public static extern int Clear(uint vi);

    [DllImport(DllName, EntryPoint = "viWrite")]
    public static extern int Write(uint vi, byte[] buffer, uint count, out uint returnCount);

    [DllImport(DllName, EntryPoint = "viRead")]
    public static extern int Read(uint vi, [Out] byte[] buffer, uint count, out uint returnCount);

    [DllImport(DllName, EntryPoint = "viSetAttribute")]
    public static extern int SetAttribute(uint vi, uint attribute, nuint value);

    [DllImport(DllName, EntryPoint = "viStatusDesc")]
    public static extern int StatusDesc(uint vi, int status, [Out] byte[] description);

    /// <summary>Bufor opisu w VISA ma stałą długość 256 znaków (<c>ViChar[256]</c>).</summary>
    public static byte[] DescriptionBuffer() => new byte[256];

    public static string FromBuffer(byte[] buffer)
    {
        var end = Array.IndexOf(buffer, (byte)0);
        return System.Text.Encoding.ASCII.GetString(buffer, 0, end < 0 ? buffer.Length : end).Trim();
    }

    public static string Describe(uint vi, int status)
    {
        try
        {
            var buf = DescriptionBuffer();
            if (StatusDesc(vi, status, buf) >= 0)
                return $"{FromBuffer(buf)} (0x{status:X8})";
        }
        catch
        {
            // Opis jest tylko udogodnieniem; kod statusu wystarcza.
        }
        return $"status VISA 0x{status:X8}";
    }
}
