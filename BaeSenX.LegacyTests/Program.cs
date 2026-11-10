using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using BaeSenX;

namespace BaeSenX.LegacyTests;

internal static class Program
{
    private static int _assertionCount;

    private static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine(
                "Usage: BaeSenX.LegacyTests.exe <legacy-bsxx.dat> <work-directory>");
            return 2;
        }

        try
        {
            Run(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]));
            Console.WriteLine();
            Console.WriteLine(
                "PASS: all {0} legacy export assertions passed.", _assertionCount);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("FAIL: " + exception);
            return 1;
        }
    }

    private static void Run(string legacyPath, string workDirectory)
    {
        Assert(File.Exists(legacyPath), "legacy BSXScript_Tool DAT sample exists");
        Directory.CreateDirectory(workDirectory);

        string runDirectory = Path.Combine(
            workDirectory,
            DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture) + "_" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runDirectory);

        byte[] originalBytes = File.ReadAllBytes(legacyPath);
        byte[] originalHash = ComputeSha256(originalBytes);
        LegacyScriptDocument document = LegacyExportService.Read(legacyPath);
        LegacyExportSummary summary = document.Summary;

        Assert(summary.Version == "BSXScript 3.0" ||
               summary.Version == "BSXScript 3.1" ||
               summary.Version == "BSXScript 3.2" ||
               summary.Version == "BSXScript 3.3",
            "legacy script version is supported");
        Assert(summary.FileSize == originalBytes.LongLength,
            "reported legacy file size matches actual size");
        Assert(summary.CharacterCount == 57,
            "legacy sample has 57 character-name entries");
        Assert(summary.MessageCount == 36301,
            "legacy sample has 36,301 message/choice entries");
        Assert(summary.DeclaredCharacterBlockSize != summary.AvailableCharacterBlockSize,
            "legacy character block uses the physical next-section boundary instead of its stale size");
        Assert(summary.DeclaredMessageBlockSize > summary.AvailableMessageBlockSize,
            "legacy message block declared size is larger than bytes physically available to EOF");
        Assert(document.CharacterNames.Count == summary.CharacterCount,
            "all character records were decoded in original index order");
        Assert(document.Messages.Count == summary.MessageCount,
            "all message records were decoded in original index order");

        Console.WriteLine(
            "Input: {0}; version: {1}; characters: {2:N0}; messages/options: {3:N0}; size: {4:N0} bytes",
            legacyPath, summary.Version, summary.CharacterCount, summary.MessageCount, summary.FileSize);
        Console.WriteLine(
            "Character data declared/available: {0:N0}/{1:N0}; trailing bytes: {2:N0}",
            summary.DeclaredCharacterBlockSize,
            summary.AvailableCharacterBlockSize,
            summary.CharacterTrailingByteCount);
        Console.WriteLine(
            "Message data declared/available: {0:N0}/{1:N0}; trailing bytes: {2:N0}",
            summary.DeclaredMessageBlockSize,
            summary.AvailableMessageBlockSize,
            summary.MessageTrailingByteCount);

        string outputPath = Path.Combine(runDirectory, "cn_bsxx_legacy.txt");
        LegacyExportSummary exported = LegacyExportService.ExportText(legacyPath, outputPath);
        Assert(File.Exists(outputPath), "legacy exporter creates the reference TXT file");
        Assert(exported.CharacterCount == document.CharacterNames.Count,
            "exported character count matches the parsed legacy DAT");
        Assert(exported.MessageCount == document.Messages.Count,
            "exported message count matches the parsed legacy DAT");

        byte[] outputBytes = File.ReadAllBytes(outputPath);
        Assert(outputBytes.Length >= 3 && outputBytes[0] == 0xEF &&
               outputBytes[1] == 0xBB && outputBytes[2] == 0xBF,
            "reference TXT is written as UTF-8 with BOM");

        string outputText = ReadUtf8Text(outputPath);
        Assert(outputText.StartsWith("# BSXScript_Tool Legacy DAT Export v1", StringComparison.Ordinal),
            "reference TXT identifies the legacy-only export format");
        Assert(outputText.Contains("# Do not import this file with BaeSenX"),
            "reference TXT explicitly states that it is not a BaeSenX import file");
        Assert(outputText.Contains(
                "# Character trailing data Base64: " +
                EncodeBase64OrNone(document.GetCharacterTrailingData())),
            "character trailing bytes are explicitly represented in the TXT");
        Assert(outputText.Contains(
                "# Message trailing data Base64: " +
                EncodeBase64OrNone(document.GetMessageTrailingData())),
            "message trailing bytes are explicitly represented in the TXT");

        List<string> lines = outputText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).ToList();
        Dictionary<int, string> characterOriginalRecords = ParseRecords(lines, 'A', '◇');
        Dictionary<int, string> characterCurrentRecords = ParseRecords(lines, 'A', '◆');
        Dictionary<int, string> messageOriginalRecords = ParseRecords(lines, 'B', '◇');
        Dictionary<int, string> messageCurrentRecords = ParseRecords(lines, 'B', '◆');
        Assert(characterOriginalRecords.Count == document.CharacterNames.Count &&
               characterCurrentRecords.Count == document.CharacterNames.Count,
            "TXT contains one ◇ and one ◆ indexed row for every character name");
        Assert(messageOriginalRecords.Count == document.Messages.Count &&
               messageCurrentRecords.Count == document.Messages.Count,
            "TXT contains one ◇ and one ◆ indexed row for every message/choice entry");

        string? characterMismatch = FindMismatch(
            document.CharacterNames, characterOriginalRecords, characterCurrentRecords, 'A');
        Assert(characterMismatch == null,
            characterMismatch == null
                ? "all character ◇/◆ TXT values round-trip exactly in their original index order"
                : "character TXT value mismatch at " + characterMismatch);

        string? messageMismatch = FindMismatch(
            document.Messages, messageOriginalRecords, messageCurrentRecords, 'B');
        Assert(messageMismatch == null,
            messageMismatch == null
                ? "all message ◇/◆ TXT values round-trip exactly in their original index order"
                : "message TXT value mismatch at " + messageMismatch);

        Expect<InvalidOperationException>(
            () => LegacyExportService.ExportText(legacyPath, legacyPath),
            "legacy exporter refuses to overwrite the source DAT");
        Assert(ComputeSha256(File.ReadAllBytes(legacyPath)).SequenceEqual(originalHash),
            "source legacy DAT remains byte-identical after export");

        // A negative index must not be treated as a relative byte position.
        byte[] negativeIndex = (byte[])originalBytes.Clone();
        int characterIndexOffset = ReadInt32(negativeIndex, 0x88);
        WriteInt32(negativeIndex, characterIndexOffset, -1);
        ExpectInvalidDataFile(
            negativeIndex,
            Path.Combine(runDirectory, "negative-character-index.dat"),
            "negative character index is rejected");

        // An offset beyond the physical message region must be rejected even
        // though the old header declares a larger message block.
        byte[] outsideIndex = (byte[])originalBytes.Clone();
        int messageIndexOffset = ReadInt32(outsideIndex, 0x98);
        WriteInt32(outsideIndex, messageIndexOffset, int.MaxValue);
        ExpectInvalidDataFile(
            outsideIndex,
            Path.Combine(runDirectory, "outside-message-index.dat"),
            "message index outside the physical EOF boundary is rejected");

        // A lone high surrogate followed by NUL is an invalid UTF-16LE string.
        byte[] invalidUtf16 = (byte[])originalBytes.Clone();
        int messageBlockOffset = ReadInt32(invalidUtf16, 0xA0);
        int firstMessageUnits = ReadInt32(invalidUtf16, messageIndexOffset);
        int firstMessageAbsolute = checked(messageBlockOffset + firstMessageUnits * 2);
        invalidUtf16[firstMessageAbsolute] = 0x00;
        invalidUtf16[firstMessageAbsolute + 1] = 0xD8;
        invalidUtf16[firstMessageAbsolute + 2] = 0x00;
        invalidUtf16[firstMessageAbsolute + 3] = 0x00;
        ExpectInvalidDataFile(
            invalidUtf16,
            Path.Combine(runDirectory, "invalid-utf16.dat"),
            "invalid UTF-16LE surrogate is rejected");

        // Remove every possible UTF-16 NUL terminator before the next indexed
        // string to verify that a record cannot run into the next record.
        byte[] missingTerminator = (byte[])originalBytes.Clone();
        int messageCount = ReadInt32(missingTerminator, 0x9C) / sizeof(int);
        var uniqueOffsets = new SortedSet<int>();
        for (int index = 0; index < messageCount; index++)
        {
            int units = ReadInt32(missingTerminator, messageIndexOffset + index * sizeof(int));
            uniqueOffsets.Add(checked(units * 2));
        }
        Assert(uniqueOffsets.Count > 1,
            "legacy message table has distinct offsets for missing-terminator test");
        int firstRelative = uniqueOffsets.First();
        int secondRelative = uniqueOffsets.Skip(1).First();
        int firstAbsolute = checked(messageBlockOffset + firstRelative);
        int firstLength = secondRelative - firstRelative;
        for (int index = 0; index < firstLength; index++)
            missingTerminator[firstAbsolute + index] = 0x41;
        ExpectInvalidDataFile(
            missingTerminator,
            Path.Combine(runDirectory, "missing-terminator.dat"),
            "string without a terminator before the next index is rejected");

        Console.WriteLine("Legacy reference TXT: {0}", outputPath);
    }

    private static string? FindMismatch(
        IReadOnlyList<string> expected,
        IReadOnlyDictionary<int, string> originalRecords,
        IReadOnlyDictionary<int, string> currentRecords,
        char type)
    {
        for (int index = 0; index < expected.Count; index++)
        {
            if (!originalRecords.TryGetValue(index, out string? original) ||
                !currentRecords.TryGetValue(index, out string? current) ||
                !string.Equals(Unescape(original ?? string.Empty), expected[index], StringComparison.Ordinal) ||
                !string.Equals(Unescape(current ?? string.Empty), expected[index], StringComparison.Ordinal))
            {
                return type + index.ToString("X7", CultureInfo.InvariantCulture);
            }
        }
        return null;
    }

    private static Dictionary<int, string> ParseRecords(IEnumerable<string> lines, char type, char marker)
    {
        var records = new Dictionary<int, string>();
        foreach (string line in lines)
        {
            if (!line.StartsWith(marker + type.ToString(), StringComparison.Ordinal))
                continue;

            int separator = line.IndexOf(marker, 1);
            if (separator < 0)
                throw new InvalidDataException("TXT record has no closing index marker: " + line);

            string token = line.Substring(1, separator - 1);
            if (token.Length < 2 || token[0] != type ||
                !int.TryParse(token.Substring(1), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out int index))
            {
                throw new InvalidDataException("TXT record index is invalid: " + token);
            }

            string value = line.Substring(separator + 1);
            if (records.ContainsKey(index))
                throw new InvalidDataException("Duplicate TXT record index: " + token);
            records.Add(index, value);
        }
        return records;
    }

    private static string Unescape(string value)
    {
        var builder = new StringBuilder(value.Length);
        for (int index = 0; index < value.Length; index++)
        {
            char current = value[index];
            if (current != '\\' || index + 1 >= value.Length)
            {
                builder.Append(current);
                continue;
            }

            char escaped = value[++index];
            switch (escaped)
            {
                case '\\': builder.Append('\\'); break;
                case 'n': builder.Append('\n'); break;
                case 'r': builder.Append('\r'); break;
                case 't': builder.Append('\t'); break;
                case 'u':
                    if (index + 4 >= value.Length ||
                        !ushort.TryParse(value.Substring(index + 1, 4), NumberStyles.HexNumber,
                            CultureInfo.InvariantCulture, out ushort codeUnit))
                    {
                        throw new InvalidDataException("Invalid \\uXXXX escape in legacy TXT.");
                    }
                    builder.Append((char)codeUnit);
                    index += 4;
                    break;
                default:
                    builder.Append('\\').Append(escaped);
                    break;
            }
        }
        return builder.ToString();
    }

    private static string ReadUtf8Text(string path)
    {
        using (var reader = new StreamReader(path, new UTF8Encoding(false, true), true))
            return reader.ReadToEnd();
    }

    private static string EncodeBase64OrNone(byte[] data)
    {
        return data.Length == 0 ? "(none)" : Convert.ToBase64String(data);
    }

    private static void ExpectInvalidDataFile(byte[] data, string path, string description)
    {
        File.WriteAllBytes(path, data);
        Expect<InvalidDataException>(() => LegacyExportService.Read(path), description);
    }

    private static byte[] ComputeSha256(byte[] data)
    {
        using (SHA256 algorithm = SHA256.Create())
            return algorithm.ComputeHash(data);
    }

    private static int ReadInt32(byte[] data, int offset)
    {
        if (offset < 0 || offset > data.Length - sizeof(int))
            throw new InvalidDataException("Test read outside data.");
        return BitConverter.ToInt32(data, offset);
    }

    private static void WriteInt32(byte[] data, int offset, int value)
    {
        byte[] encoded = BitConverter.GetBytes(value);
        Buffer.BlockCopy(encoded, 0, data, offset, sizeof(int));
    }

    private static void Assert(bool condition, string description)
    {
        _assertionCount++;
        if (!condition)
            throw new InvalidOperationException("Assertion failed: " + description);
        Console.WriteLine("PASS: " + description);
    }

    private static void Expect<TException>(Action action, string description)
        where TException : Exception
    {
        _assertionCount++;
        try
        {
            action();
        }
        catch (TException)
        {
            Console.WriteLine("PASS: " + description);
            return;
        }

        throw new InvalidOperationException(
            "Assertion failed: expected " + typeof(TException).Name + " - " + description);
    }
}
