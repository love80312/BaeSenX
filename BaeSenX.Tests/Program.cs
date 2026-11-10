using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using BaeSenX;

namespace BaeSenX.Tests;

internal static class Program
{
    private static int _assertionCount;

    private static int Main(string[] args)
    {
        if (args.Length != 2)
        {
            Console.Error.WriteLine("Usage: BaeSenX.Tests.exe <path-to-bsxx.dat> <work-directory>");
            return 2;
        }

        try
        {
            Run(Path.GetFullPath(args[0]), Path.GetFullPath(args[1]));
            Console.WriteLine();
            Console.WriteLine("PASS: all {0} regression assertions passed.", _assertionCount);
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("FAIL: " + exception);
            return 1;
        }
    }

    private static void Run(string sourcePath, string workDirectory)
    {
        Assert(File.Exists(sourcePath), "real BSXX test file exists");
        Directory.CreateDirectory(workDirectory);

        string runDirectory = Path.Combine(
            workDirectory,
            DateTime.UtcNow.ToString("yyyyMMdd_HHmmss_fff", CultureInfo.InvariantCulture) + "_" +
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(runDirectory);

        byte[] sourceBytes = File.ReadAllBytes(sourcePath);
        byte[] sourceHash = ComputeSha256(sourceBytes);
        ScriptSummary inspected = TranslationService.Inspect(sourcePath);

        Assert(inspected.Version == "BSXScript 3.0" ||
               inspected.Version == "BSXScript 3.1" ||
               inspected.Version == "BSXScript 3.2" ||
               inspected.Version == "BSXScript 3.3",
            "test script version is supported");
        Assert(inspected.CharacterCount > 0, "character-name table is not empty");
        Assert(inspected.MessageCount > 0, "message table is not empty");
        Assert(inspected.FileSize == sourceBytes.LongLength, "reported source file size matches actual size");
        Console.WriteLine("Input: {0}", sourcePath);
        Console.WriteLine("Version: {0}; characters: {1:N0}; messages/options: {2:N0}; size: {3:N0} bytes",
            inspected.Version, inspected.CharacterCount, inspected.MessageCount, inspected.FileSize);

        string exportedTextPath = Path.Combine(runDirectory, "source.translation.txt");
        ScriptSummary exported = TranslationService.ExportText(sourcePath, exportedTextPath);
        Assert(File.Exists(exportedTextPath), "export creates translation text file");
        Assert(exported.CharacterCount == inspected.CharacterCount, "export character count matches inspection");
        Assert(exported.MessageCount == inspected.MessageCount, "export message count matches inspection");

        List<string> exportedLines = ReadUtf8Lines(exportedTextPath);
        Assert(exportedLines.Any(line => line.StartsWith("◇A", StringComparison.Ordinal)),
            "export includes original character-name records");
        Assert(exportedLines.Any(line => line.StartsWith("◆A", StringComparison.Ordinal)),
            "export includes translatable character-name records");
        Assert(exportedLines.Any(line => line.StartsWith("◇B", StringComparison.Ordinal)),
            "export includes original message-table records");
        Assert(exportedLines.Any(line => line.StartsWith("◆B", StringComparison.Ordinal)),
            "export includes translatable message-table records");

        // Test 1: import without changing any translation. This exercises the real
        // sample file's complete string tables and the offset-table rebuild.
        string roundTripPath = Path.Combine(runDirectory, "roundtrip.dat");
        ImportSummary roundTrip = TranslationService.ImportText(sourcePath, exportedTextPath, roundTripPath);
        Assert(File.Exists(roundTripPath), "unchanged import creates a new BSXX file");
        Assert(roundTrip.UpdatedCharacterCount == 0, "unchanged import does not report changed character names");
        Assert(roundTrip.UpdatedMessageCount == 0, "unchanged import does not report changed messages");
        Assert(roundTrip.CharacterCount == inspected.CharacterCount, "round-trip character count is preserved");
        Assert(roundTrip.MessageCount == inspected.MessageCount, "round-trip message count is preserved");
        VerifyBinaryPreservation(sourceBytes, File.ReadAllBytes(roundTripPath));

        // Exercise replacement of an already existing output file. The service
        // writes a temporary file and atomically replaces the previous result.
        ImportSummary replacement = TranslationService.ImportText(sourcePath, exportedTextPath, roundTripPath);
        Assert(replacement.OutputSize == new FileInfo(roundTripPath).Length,
            "rebuilding an existing output file succeeds");

        // Locate a message-table entry and replace it with Traditional Chinese,
        // Japanese, line breaks, a tab, marker glyphs and a supplementary-plane
        // Unicode character. This checks UTF-8 text parsing and UTF-16LE output.
        int messageLineIndex = exportedLines.FindIndex(line => line.StartsWith("◆B", StringComparison.Ordinal));
        Assert(messageLineIndex >= 0, "a message translation record exists");
        string messageToken = GetRecordToken(exportedLines[messageLineIndex]);
        string expectedMessage = "BaeSenX 測試：繁體中文\n第二行\t日文テスト ◆◇ 😀";
        string serializedMessage = EscapeForExpectedText(expectedMessage);
        exportedLines[messageLineIndex] = GetRecordPrefix(exportedLines[messageLineIndex]) + serializedMessage;

        string editedTextPath = Path.Combine(runDirectory, "edited.translation.txt");
        WriteUtf8Lines(editedTextPath, exportedLines, emitBom: true);
        string editedScriptPath = Path.Combine(runDirectory, "translated.dat");
        ImportSummary edited = TranslationService.ImportText(sourcePath, editedTextPath, editedScriptPath);
        Assert(edited.UpdatedMessageCount == 1, "one edited message is counted as updated");
        Assert(edited.UpdatedCharacterCount == 0, "message-only edit does not change character names");
        VerifyBinaryPreservation(sourceBytes, File.ReadAllBytes(editedScriptPath));

        string editedVerifyTextPath = Path.Combine(runDirectory, "translated.verify.txt");
        TranslationService.ExportText(editedScriptPath, editedVerifyTextPath);
        List<string> editedVerifyLines = ReadUtf8Lines(editedVerifyTextPath);
        string expectedPrefix = "◆" + messageToken + "◆";
        string? actualEditedLine = editedVerifyLines.FirstOrDefault(
            line => line.StartsWith(expectedPrefix, StringComparison.Ordinal));
        Assert(actualEditedLine != null, "edited message can be read from rebuilt script");
        Assert(actualEditedLine == expectedPrefix + serializedMessage,
            "Traditional Chinese, Japanese, line breaks, tabs, marker glyphs and emoji round-trip");

        // Test 2: import a minimal legacy BSXScript_Tool marker file.
        // Use a character-name row because names are normally single-line;
        // decode the BaeSenX escape format before writing the legacy text file.
        string? legacyOriginalLine = exportedLines.FirstOrDefault(line =>
            line.StartsWith("◇A", StringComparison.Ordinal) &&
            UnescapeExportText(GetRecordValue(line)).IndexOfAny(new[] { '\r', '\n' }) < 0);
        Assert(legacyOriginalLine != null, "a single-line character-name record exists for legacy import");
        string legacyToken = GetRecordToken(legacyOriginalLine!);
        string legacyOriginalValue = UnescapeExportText(GetRecordValue(legacyOriginalLine!));
        string legacyTranslation = "舊版格式相容測試／繁體中文";
        string legacyTextPath = Path.Combine(runDirectory, "legacy.translation.txt");
        WriteUtf8Lines(legacyTextPath, new[]
        {
            "◇" + legacyToken + "◇" + legacyOriginalValue,
            "◆" + legacyToken + "◆" + legacyTranslation
        }, emitBom: false);

        string legacyScriptPath = Path.Combine(runDirectory, "legacy-import.dat");
        ImportSummary legacy = TranslationService.ImportText(sourcePath, legacyTextPath, legacyScriptPath);
        Assert(legacy.UpdatedCharacterCount == 1, "legacy BSXScript_Tool format updates one character name");

        string legacyVerifyPath = Path.Combine(runDirectory, "legacy.verify.txt");
        TranslationService.ExportText(legacyScriptPath, legacyVerifyPath);
        List<string> legacyVerifyLines = ReadUtf8Lines(legacyVerifyPath);
        string legacyPrefix = "◆" + legacyToken + "◆";
        Assert(legacyVerifyLines.Any(line => line == legacyPrefix + legacyTranslation),
            "legacy translation is present in rebuilt BSXX file");

        // Test 3: mismatched source text must be rejected before writing output.
        List<string> mismatchedLines = ReadUtf8Lines(exportedTextPath);
        int originalMessageLineIndex = mismatchedLines.FindIndex(
            line => line.StartsWith("◇B", StringComparison.Ordinal));
        Assert(originalMessageLineIndex >= 0, "an original message record exists for mismatch test");
        string originalMessagePrefix = GetRecordPrefix(mismatchedLines[originalMessageLineIndex]);
        mismatchedLines[originalMessageLineIndex] = originalMessagePrefix + "故意錯誤的原文";
        string mismatchedTextPath = Path.Combine(runDirectory, "mismatched.translation.txt");
        WriteUtf8Lines(mismatchedTextPath, mismatchedLines, emitBom: true);
        string rejectedOutputPath = Path.Combine(runDirectory, "must-not-exist.dat");
        Expect<InvalidDataException>(() => TranslationService.ImportText(
            sourcePath, mismatchedTextPath, rejectedOutputPath),
            "mismatched original text is rejected");
        Assert(!File.Exists(rejectedOutputPath), "rejected import does not create an output file");

        // Test 4: NUL must not be written into a UTF-16 string table.
        List<string> nulLines = ReadUtf8Lines(exportedTextPath);
        int nulLineIndex = nulLines.FindIndex(line => line.StartsWith("◆B", StringComparison.Ordinal));
        string nulPrefix = GetRecordPrefix(nulLines[nulLineIndex]);
        nulLines[nulLineIndex] = nulPrefix + "\\u0000";
        string nulTextPath = Path.Combine(runDirectory, "nul.translation.txt");
        WriteUtf8Lines(nulTextPath, nulLines, emitBom: true);
        string nulOutputPath = Path.Combine(runDirectory, "nul-must-not-exist.dat");
        Expect<InvalidDataException>(() => TranslationService.ImportText(
            sourcePath, nulTextPath, nulOutputPath),
            "NUL character is rejected");
        Assert(!File.Exists(nulOutputPath), "NUL rejection does not create an output file");

        // The tool must never overwrite its source file.
        Expect<InvalidOperationException>(() => TranslationService.ImportText(
            sourcePath, exportedTextPath, sourcePath),
            "source file overwrite is rejected");
        Assert(ComputeSha256(File.ReadAllBytes(sourcePath)).SequenceEqual(sourceHash),
            "original test/bsxx.dat remains byte-identical");

        Console.WriteLine("Regression work files: {0}", runDirectory);
    }

    private static void VerifyBinaryPreservation(byte[] original, byte[] rebuilt)
    {
        Assert(rebuilt.Length >= 0xA8, "rebuilt file contains complete BSXX header");
        Assert(ReadInt32(rebuilt, 0x2C) == ReadInt32(original, 0x2C),
            "Opcode offset remains unchanged");
        Assert(ReadInt32(rebuilt, 0x30) == ReadInt32(original, 0x30),
            "Opcode size remains unchanged");

        int opcodeOffset = ReadInt32(original, 0x2C);
        int opcodeSize = ReadInt32(original, 0x30);
        AssertRange(original, opcodeOffset, opcodeSize, "original Opcode");
        AssertRange(rebuilt, opcodeOffset, opcodeSize, "rebuilt Opcode");
        Assert(Slice(original, opcodeOffset, opcodeSize).SequenceEqual(
                Slice(rebuilt, opcodeOffset, opcodeSize)),
            "Opcode region is byte-identical");

        int originalCharacterIndexOffset = ReadInt32(original, 0x88);
        int rebuiltCharacterIndexOffset = ReadInt32(rebuilt, 0x88);
        Assert(originalCharacterIndexOffset == rebuiltCharacterIndexOffset,
            "data before the character index table retains its original boundary");
        Assert(Slice(original, 0, originalCharacterIndexOffset).SequenceEqual(
                Slice(rebuilt, 0, rebuiltCharacterIndexOffset)),
            "all data before the character index table is byte-identical");

        int originalTailOffset = checked(ReadInt32(original, 0xA0) + ReadInt32(original, 0xA4));
        int rebuiltTailOffset = checked(ReadInt32(rebuilt, 0xA0) + ReadInt32(rebuilt, 0xA4));
        AssertRange(original, originalTailOffset, original.Length - originalTailOffset, "original trailing data");
        AssertRange(rebuilt, rebuiltTailOffset, rebuilt.Length - rebuiltTailOffset, "rebuilt trailing data");
        Assert(Slice(original, originalTailOffset, original.Length - originalTailOffset).SequenceEqual(
                Slice(rebuilt, rebuiltTailOffset, rebuilt.Length - rebuiltTailOffset)),
            "data after the message string block is byte-identical");

        int characterBlockOffset = ReadInt32(rebuilt, 0x90);
        int messageIndexOffset = ReadInt32(rebuilt, 0x98);
        int messageBlockOffset = ReadInt32(rebuilt, 0xA0);
        Assert(characterBlockOffset % 16 == 0, "character string block is 16-byte aligned");
        Assert(messageIndexOffset % 16 == 0, "message index table is 16-byte aligned");
        Assert(messageBlockOffset % 16 == 0, "message string block is 16-byte aligned");
    }

    private static List<string> ReadUtf8Lines(string path)
    {
        return File.ReadAllLines(path, new UTF8Encoding(false, true)).ToList();
    }

    private static void WriteUtf8Lines(string path, IEnumerable<string> lines, bool emitBom)
    {
        File.WriteAllLines(path, lines, new UTF8Encoding(emitBom, true));
    }

    private static string GetRecordToken(string line)
    {
        if (line.Length == 0 || (line[0] != '◇' && line[0] != '◆'))
            throw new InvalidDataException("Not a BaeSenX marker line: " + line);

        int separator = line.IndexOf(line[0], 1);
        if (separator < 0)
            throw new InvalidDataException("Marker line has no closing marker: " + line);
        return line.Substring(1, separator - 1);
    }

    private static string GetRecordPrefix(string line)
    {
        char marker = line[0];
        int separator = line.IndexOf(marker, 1);
        if (separator < 0)
            throw new InvalidDataException("Marker line has no closing marker: " + line);
        return line.Substring(0, separator + 1);
    }

    private static string GetRecordValue(string line)
    {
        string prefix = GetRecordPrefix(line);
        return line.Substring(prefix.Length);
    }

    private static string UnescapeExportText(string value)
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
                        throw new InvalidDataException("Invalid escape sequence in exported text.");
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

    private static string EscapeForExpectedText(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (char character in value)
        {
            switch (character)
            {
                case '\\': builder.Append("\\\\"); break;
                case '\r': builder.Append("\\r"); break;
                case '\n': builder.Append("\\n"); break;
                case '\t': builder.Append("\\t"); break;
                default:
                    if (char.IsControl(character))
                        builder.Append("\\u").Append(((int)character).ToString("X4", CultureInfo.InvariantCulture));
                    else
                        builder.Append(character);
                    break;
            }
        }
        return builder.ToString();
    }

    private static byte[] ComputeSha256(byte[] data)
    {
        using (SHA256 algorithm = SHA256.Create())
            return algorithm.ComputeHash(data);
    }

    private static int ReadInt32(byte[] data, int offset)
    {
        if (offset < 0 || offset > data.Length - sizeof(int))
            throw new InvalidDataException("Header offset is outside the file.");
        return BitConverter.ToInt32(data, offset);
    }

    private static byte[] Slice(byte[] data, int offset, int count)
    {
        var result = new byte[count];
        Buffer.BlockCopy(data, offset, result, 0, count);
        return result;
    }

    private static void AssertRange(byte[] data, int offset, int count, string name)
    {
        if (offset < 0 || count < 0 || (long)offset + count > data.Length)
            throw new InvalidDataException(name + " range is outside the file.");
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

        throw new InvalidOperationException("Assertion failed: expected " + typeof(TException).Name + " - " + description);
    }
}
