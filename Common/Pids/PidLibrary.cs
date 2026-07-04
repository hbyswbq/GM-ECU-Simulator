using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Common.Pids;

/// <summary>
/// Reference catalogues of known PIDs for Modes $01, $1A, and $22, sourced
/// from the A2L-port CSVs under <c>tools/pid imports from a2l/</c>. The
/// editor presents these so the user can pick a PID by name and have its
/// size / units / scaling pre-filled; the dispatcher does not consult them.
/// </summary>
/// <remarks>
/// Storage form per mode: gzip(CSV-UTF8) -> AES-128-CBC (PKCS7), embedded
/// as <c>Common/Pids/Mode{01,1A,22}Library.bin</c> (plus the Ford set
/// <c>Mode22LibraryFord.bin</c>). The key + IV live in this
/// file so anyone with the shipped binary can recover the CSVs - this is
/// obfuscation, not real security. The point is to keep the source CSVs
/// out of repo and out of casual <c>strings</c> inspection. Regenerate the
/// blobs with <c>tools/pid_library_packer/pack_pid_libraries.py</c>; that
/// script's <c>AES_KEY</c> / <c>AES_IV</c> constants must stay in sync with
/// the values below. Loading is lazy: the first access to any of the four
/// dictionaries triggers a decrypt + decompress + parse pass for that mode
/// only.
/// </remarks>
public static class PidLibrary
{
    private static readonly byte[] Key = { 0x6F, 0x8B, 0x3D, 0xA2, 0x4C, 0x71, 0xE5, 0x5D, 0xB0, 0x88, 0x19, 0x2C, 0x7E, 0xAA, 0x04, 0xD3 };
    private static readonly byte[] Iv  = { 0x3A, 0x09, 0xC5, 0x7F, 0x91, 0x64, 0xB8, 0x22, 0x0E, 0xF5, 0x6D, 0xAB, 0x50, 0x1A, 0x47, 0xCC };

    private static readonly Lazy<IReadOnlyDictionary<ushort, PidLibraryEntry>> mode01 =
        new(() => LoadResource("Common.Pids.Mode01Library.bin"));
    private static readonly Lazy<IReadOnlyDictionary<ushort, PidLibraryEntry>> mode1A =
        new(() => LoadResource("Common.Pids.Mode1ALibrary.bin"));
    private static readonly Lazy<IReadOnlyDictionary<ushort, PidLibraryEntry>> mode22 =
        new(() => LoadResource("Common.Pids.Mode22Library.bin"));
    private static readonly Lazy<IReadOnlyDictionary<ushort, PidLibraryEntry>> mode22Ford =
        new(() => LoadResource("Common.Pids.Mode22LibraryFord.bin"));

    /// <summary>OBD-II Service $01 PID catalogue, keyed by 1-byte PID id.</summary>
    public static IReadOnlyDictionary<ushort, PidLibraryEntry> Mode01 => mode01.Value;

    /// <summary>GMW3110 Service $1A identifier catalogue, keyed by 1-byte DID.</summary>
    public static IReadOnlyDictionary<ushort, PidLibraryEntry> Mode1A => mode1A.Value;

    /// <summary>GMW3110 Service $22 PID catalogue, keyed by 2-byte PID id.</summary>
    public static IReadOnlyDictionary<ushort, PidLibraryEntry> Mode22 => mode22.Value;

    /// <summary>Ford UDS Service $22 PID catalogue (the SCP/J2190 DID dump),
    /// keyed by 2-byte PID id. Offered to the editor's Identifier picker when an
    /// ECU runs the Ford persona instead of the GM <see cref="Mode22"/> set.</summary>
    public static IReadOnlyDictionary<ushort, PidLibraryEntry> Mode22Ford => mode22Ford.Value;

    private static IReadOnlyDictionary<ushort, PidLibraryEntry> LoadResource(string resourceName)
    {
        var asm = typeof(PidLibrary).Assembly;
        using var stream = asm.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{resourceName}' not found.");
        var ciphertext = new byte[stream.Length];
        stream.ReadExactly(ciphertext);

        using var aes = Aes.Create();
        aes.Key     = Key;
        aes.IV      = Iv;
        aes.Mode    = CipherMode.CBC;
        aes.Padding = PaddingMode.PKCS7;
        var compressed = aes.DecryptCbc(ciphertext, Iv);

        using var src = new MemoryStream(compressed, writable: false);
        using var gz  = new GZipStream(src, CompressionMode.Decompress);
        using var rdr = new StreamReader(gz, Encoding.UTF8);
        var csv = rdr.ReadToEnd();
        return ParseCsv(csv);
    }

    private static IReadOnlyDictionary<ushort, PidLibraryEntry> ParseCsv(string csv)
    {
        var rows = SplitCsv(csv);
        if (rows.Count == 0)
            throw new InvalidDataException("PID library CSV is empty.");

        // Header order is fixed by the packer; assert it once so a schema
        // drift between the packer and the loader trips loudly instead of
        // silently misaligning columns.
        var header = rows[0];
        string[] expected = { "did", "size", "flag", "a2l_kind", "friendly_name", "a2l_name", "a2l_desc", "datatype", "lower", "upper", "conv", "unit", "slope", "offset" };
        if (header.Count != expected.Length)
            throw new InvalidDataException($"PID library header has {header.Count} columns, expected {expected.Length}.");
        for (int i = 0; i < expected.Length; i++)
            if (!string.Equals(header[i], expected[i], StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"PID library header column {i} is '{header[i]}', expected '{expected[i]}'.");

        var dict = new Dictionary<ushort, PidLibraryEntry>(rows.Count);
        for (int r = 1; r < rows.Count; r++)
        {
            var row = rows[r];
            if (row.Count == 1 && row[0].Length == 0) continue; // blank trailing line
            if (row.Count != expected.Length)
                throw new InvalidDataException($"PID library row {r} has {row.Count} columns, expected {expected.Length}.");

            var did = ParseDid(row[0]);
            var entry = new PidLibraryEntry(
                Did:          did,
                Size:         int.Parse(row[1], CultureInfo.InvariantCulture),
                Flag:         int.Parse(row[2], CultureInfo.InvariantCulture),
                A2lKind:      row[3],
                FriendlyName: row[4],
                A2lName:      row[5],
                Description:  row[6],
                DataType:     row[7],
                Lower:        ParseNullableDouble(row[8]),
                Upper:        ParseNullableDouble(row[9]),
                Conversion:   row[10],
                Unit:         row[11],
                Slope:        ParseNullableDouble(row[12]),
                Offset:       ParseNullableDouble(row[13]));
            dict[did] = entry;
        }
        return dict;
    }

    private static ushort ParseDid(string s)
    {
        s = s.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return ushort.Parse(s.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return ushort.Parse(s, CultureInfo.InvariantCulture);
    }

    private static double? ParseNullableDouble(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        return double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    // Minimal RFC-4180-ish CSV reader: handles quoted fields with embedded
    // commas, quotes-as-double-quotes, CRLF or LF row terminators. The
    // packer emits straightforward CSV produced by Python's csv module, so
    // no need for a full parser dependency.
    private static List<List<string>> SplitCsv(string text)
    {
        var rows  = new List<List<string>>();
        var row   = new List<string>();
        var field = new StringBuilder();
        bool quoted = false;

        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { field.Append('"'); i++; }
                    else quoted = false;
                }
                else field.Append(c);
            }
            else
            {
                switch (c)
                {
                    case '"':
                        quoted = true;
                        break;
                    case ',':
                        row.Add(field.ToString()); field.Clear();
                        break;
                    case '\r':
                        break; // swallow; the \n drives row termination
                    case '\n':
                        row.Add(field.ToString()); field.Clear();
                        rows.Add(row); row = new List<string>();
                        break;
                    default:
                        field.Append(c);
                        break;
                }
            }
        }
        if (field.Length > 0 || row.Count > 0)
        {
            row.Add(field.ToString());
            rows.Add(row);
        }
        return rows;
    }
}
