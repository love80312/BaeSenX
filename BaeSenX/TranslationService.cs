using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace BaeSenX;

public sealed class ScriptSummary
{
    public string Version { get; }
    public int CharacterCount { get; }
    public int MessageCount { get; }
    public long FileSize { get; }

    public ScriptSummary(string version, int characterCount, int messageCount, long fileSize)
    {
        Version = version;
        CharacterCount = characterCount;
        MessageCount = messageCount;
        FileSize = fileSize;
    }
}

public sealed class ImportSummary
{
    public string Version { get; }
    public int UpdatedCharacterCount { get; }
    public int UpdatedMessageCount { get; }
    public int CharacterCount { get; }
    public int MessageCount { get; }
    public long OutputSize { get; }

    public ImportSummary(string version, int updatedCharacterCount, int updatedMessageCount,
        int characterCount, int messageCount, long outputSize)
    {
        Version = version;
        UpdatedCharacterCount = updatedCharacterCount;
        UpdatedMessageCount = updatedMessageCount;
        CharacterCount = characterCount;
        MessageCount = messageCount;
        OutputSize = outputSize;
    }
}

/// <summary>
/// Reads and updates only the UTF-16 character-name and message string tables
/// in a Bishop BSXScript 3.x file. The opcode region is deliberately preserved.
/// </summary>
public static class TranslationService
{
    private const int MinimumHeaderSize = 0xA8;
    private const int CharacterIndexOffsetField = 0x88;
    private const int CharacterIndexSizeField = 0x8C;
    private const int CharacterBlockOffsetField = 0x90;
    private const int CharacterBlockSizeField = 0x94;
    private const int MessageIndexOffsetField = 0x98;
    private const int MessageIndexSizeField = 0x9C;
    private const int MessageBlockOffsetField = 0xA0;
    private const int MessageBlockSizeField = 0xA4;
    private const int OpcodeOffsetField = 0x2C;
    private const int OpcodeSizeField = 0x30;
    private const int Alignment = 16;
    private const int MaximumStringCount = 1_000_000;
    private const int DiagnosticValueLimit = 120;

    private static readonly Encoding Utf16LittleEndian = new UnicodeEncoding(
        bigEndian: false,
        byteOrderMark: false,
        throwOnInvalidBytes: true);

    private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

    public static ScriptSummary Inspect(string scriptPath)
    {
        ValidatePath(scriptPath, nameof(scriptPath));
        var document = ScriptDocument.Load(File.ReadAllBytes(scriptPath));
        return new ScriptSummary(
            document.Version,
            document.CharacterNames.Length,
            document.Messages.Length,
            new FileInfo(scriptPath).Length);
    }

    /// <summary>
    /// Exports all entries in the character-name and message tables.
    /// B = shared message table (dialogue and choice labels).
    /// The text file format remains compatible with BSXScript_Tool markers.
    /// The output is written to a temporary file before replacing the target.
    /// </summary>
    public static ScriptSummary ExportText(string scriptPath, string outputTextPath)
    {
        ValidatePath(scriptPath, nameof(scriptPath));
        ValidatePath(outputTextPath, nameof(outputTextPath));

        string sourceFullPath = Path.GetFullPath(scriptPath);
        string outputFullPath = Path.GetFullPath(outputTextPath);
        if (string.Equals(sourceFullPath, outputFullPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "翻譯文字檔不能覆蓋原始 bsxx.dat。請使用不同的輸出檔名。");
        }

        var document = ScriptDocument.Load(File.ReadAllBytes(sourceFullPath));
        var builder = new StringBuilder(Math.Max(4096, document.Messages.Length * 80));

        builder.AppendLine("# BaeSenX Translation Text v1");
        builder.AppendLine("# Source: " + Path.GetFileName(scriptPath));
        builder.AppendLine("# BSXScript: " + document.Version);
        builder.AppendLine("# A = Character names");
        builder.AppendLine("# B = Message table (dialogue and choice labels)");
        builder.AppendLine("# Edit only ◆ lines. Escape sequences: \\n \\r \\t \\\\ \\uXXXX");
        builder.AppendLine();

        AppendTable(builder, 'A', document.CharacterNames, "角色名稱");
        AppendTable(builder, 'B', document.Messages, "對話／選項文字");

        WriteAtomically(outputFullPath, EncodeUtf8WithBom(builder.ToString()));

        return new ScriptSummary(
            document.Version,
            document.CharacterNames.Length,
            document.Messages.Length,
            new FileInfo(scriptPath).Length);
    }

    /// <summary>
    /// Imports translated A/B marker lines, rebuilds the two string tables,
    /// validates the generated script, and atomically writes a separate output file.
    /// </summary>
    public static ImportSummary ImportText(
        string scriptPath,
        string translationTextPath,
        string outputScriptPath)
    {
        ValidatePath(scriptPath, nameof(scriptPath));
        ValidatePath(translationTextPath, nameof(translationTextPath));
        ValidatePath(outputScriptPath, nameof(outputScriptPath));

        string sourceFullPath = Path.GetFullPath(scriptPath);
        string translationFullPath = Path.GetFullPath(translationTextPath);
        string outputFullPath = Path.GetFullPath(outputScriptPath);
        if (string.Equals(sourceFullPath, outputFullPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "輸出路徑不能與原始 bsxx.dat 相同。請另存新檔，避免覆蓋原始腳本。");
        }
        if (string.Equals(translationFullPath, outputFullPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "輸出腳本不能覆蓋翻譯文字檔。請選擇不同的輸出檔名。");
        }

        var document = ScriptDocument.Load(File.ReadAllBytes(sourceFullPath));
        var translation = ReadTranslationFile(translationTextPath, document);

        string[] translatedCharacters = ApplyTranslations(
            document.CharacterNames,
            translation.CharacterTranslations,
            out int updatedCharacters);

        string[] translatedMessages = ApplyTranslations(
            document.Messages,
            translation.MessageTranslations,
            out int updatedMessages);

        byte[] outputBytes = document.BuildTranslated(translatedCharacters, translatedMessages);

        // Re-open the generated file and verify every translated value.
        var verification = ScriptDocument.Load(outputBytes);
        if (!translatedCharacters.SequenceEqual(verification.CharacterNames, StringComparer.Ordinal) ||
            !translatedMessages.SequenceEqual(verification.Messages, StringComparer.Ordinal))
        {
            throw new InvalidDataException("輸出檔案的字串表驗證失敗，未寫入輸出檔案。");
        }

        // Translation must not modify the executable opcode region.
        if (!document.OpcodeBytes.SequenceEqual(verification.OpcodeBytes))
        {
            throw new InvalidDataException("輸出檔案的 Opcode 區段與原始檔不同，已中止寫入。");
        }

        WriteAtomically(outputFullPath, outputBytes);

        return new ImportSummary(
            document.Version,
            updatedCharacters,
            updatedMessages,
            document.CharacterNames.Length,
            document.Messages.Length,
            outputBytes.LongLength);
    }

    private static void ValidatePath(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("檔案路徑不可空白。", parameterName);
    }

    private static void AppendTable(StringBuilder builder, char type, IReadOnlyList<string> values, string title)
    {
        builder.AppendLine("# ===== " + title + " (" + type + ") =====");
        for (int index = 0; index < values.Count; index++)
        {
            string id = type + index.ToString("X7", CultureInfo.InvariantCulture);
            string escaped = EscapeText(values[index]);
            builder.Append('◇').Append(id).Append('◇').AppendLine(escaped);
            builder.Append('◆').Append(id).Append('◆').AppendLine(escaped);
            builder.AppendLine();
        }
    }

    private static TranslationRecords ReadTranslationFile(string path, ScriptDocument document)
    {
        using var reader = new StreamReader(path, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: true);
        string text = reader.ReadToEnd();
        bool isBaeSenXFormat = text.StartsWith("# BaeSenX Translation Text v1", StringComparison.Ordinal);

        var originalCharacters = new Dictionary<int, string>();
        var originalMessages = new Dictionary<int, string>();
        var translatedCharacters = new Dictionary<int, string>();
        var translatedMessages = new Dictionary<int, string>();

        using var lineReader = new StringReader(text);
        string? line;
        int lineNumber = 0;
        int parsedTranslationCount = 0;

        while ((line = lineReader.ReadLine()) is not null)
        {
            lineNumber++;
            if (line.Length == 0 || (line[0] != '◇' && line[0] != '◆'))
                continue;

            ParsedRecord record = ParseRecord(line, lineNumber, isBaeSenXFormat);
            Dictionary<int, string> target = record.IsTranslation
                ? (record.Type == 'A' ? translatedCharacters : translatedMessages)
                : (record.Type == 'A' ? originalCharacters : originalMessages);

            if (target.TryGetValue(record.Index, out string? existing))
            {
                // Legacy BSXScript_Tool exports a name/message every time it is used.
                // Repeated identical records are harmless; conflicting duplicates are not.
                if (!string.Equals(existing, record.Value, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"第 {lineNumber} 行：同一索引出現內容不同的重複紀錄（{record.Type}{record.Index:X7}）。");
                }
            }
            else
            {
                target.Add(record.Index, record.Value);
            }

            if (record.IsTranslation)
                parsedTranslationCount++;
        }

        ValidateSourceRecords(originalCharacters, document.CharacterNames, 'A');
        ValidateSourceRecords(originalMessages, document.Messages, 'B');
        ValidateTranslationIndexes(translatedCharacters, document.CharacterNames.Length, 'A');
        ValidateTranslationIndexes(translatedMessages, document.Messages.Length, 'B');
        ValidateTranslationSources(originalCharacters, translatedCharacters, 'A');
        ValidateTranslationSources(originalMessages, translatedMessages, 'B');

        if (parsedTranslationCount == 0)
        {
            throw new InvalidDataException(
                "沒有找到任何 ◆A 或 ◆B 翻譯紀錄。請確認使用的是 BaeSenX／BSXScript_Tool 格式的文字檔。");
        }

        return new TranslationRecords(translatedCharacters, translatedMessages);
    }

    private static ParsedRecord ParseRecord(string line, int lineNumber, bool decodeEscapes)
    {
        char marker = line[0];
        int separator = line.IndexOf(marker, 1);
        if (separator < 0)
            throw new InvalidDataException($"第 {lineNumber} 行：找不到索引結尾標記。");

        string token = line.Substring(1, separator - 1);
        if (token.Length < 2 || (token[0] != 'A' && token[0] != 'B') ||
            !int.TryParse(token.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int index))
        {
            throw new InvalidDataException($"第 {lineNumber} 行：索引格式錯誤（{token}）。");
        }

        string value = line.Substring(separator + 1);
        if (decodeEscapes)
            value = UnescapeText(value, lineNumber);

        return new ParsedRecord(marker == '◆', token[0], index, value);
    }

    private static void ValidateSourceRecords(
        IReadOnlyDictionary<int, string> records,
        IReadOnlyList<string> actualValues,
        char type)
    {
        foreach (var pair in records)
        {
            if (pair.Key < 0 || pair.Key >= actualValues.Count)
                throw new InvalidDataException($"原文索引 {type}{pair.Key:X7} 超出目前腳本範圍。");

            string actualValue = actualValues[pair.Key];
            if (!string.Equals(pair.Value, actualValue, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"TXT 的原文與目前選擇的 DAT 不一致：索引 {type}{pair.Key:X7}。\n" +
                    $"TXT 的 ◇ 原文：{FormatDiagnosticValue(pair.Value)}\n" +
                    $"DAT 目前內容：{FormatDiagnosticValue(actualValue)}\n\n" +
                    "若要沿用最初匯出的 TXT，請選擇當初匯出 TXT 時使用的原版 bsxx.dat。\n" +
                    "若要以翻譯後 DAT 繼續修改，請先從該 DAT 重新匯出新版 TXT，再修改 ◆ 翻譯行。\n" +
                    "為避免翻譯套用錯位，已中止打包。");
            }
        }
    }

    private static string FormatDiagnosticValue(string value)
    {
        string escaped = EscapeText(value);
        if (escaped.Length > DiagnosticValueLimit)
        {
            int displayLength = DiagnosticValueLimit;
            if (displayLength < escaped.Length && char.IsHighSurrogate(escaped[displayLength - 1]) &&
                char.IsLowSurrogate(escaped[displayLength]))
            {
                displayLength--;
            }
            escaped = escaped.Substring(0, displayLength) + "…";
        }
        return "「" + escaped + "」";
    }

    private static void ValidateTranslationSources(
        IReadOnlyDictionary<int, string> originalRecords,
        IReadOnlyDictionary<int, string> translationRecords,
        char type)
    {
        foreach (var pair in translationRecords)
        {
            if (!originalRecords.TryGetValue(pair.Key, out string? originalValue))
            {
                throw new InvalidDataException(
                    $"翻譯索引 {type}{pair.Key:X7} 缺少對應的 ◇ 原文行。請保留匯出檔中的原文行，避免將翻譯套用到錯誤版本。 ");
            }

            if (originalValue == null)
                throw new InvalidDataException($"翻譯索引 {type}{pair.Key:X7} 的原文紀錄無效。");
        }
    }

    private static void ValidateTranslationIndexes(
        IReadOnlyDictionary<int, string> records,
        int actualCount,
        char type)
    {
        foreach (int index in records.Keys)
        {
            if (index < 0 || index >= actualCount)
                throw new InvalidDataException($"翻譯索引 {type}{index:X7} 超出目前腳本範圍。");
        }
    }

    private static string[] ApplyTranslations(
        IReadOnlyList<string> originals,
        IReadOnlyDictionary<int, string> translations,
        out int updatedCount)
    {
        string[] result = originals.ToArray();
        updatedCount = 0;

        foreach (var pair in translations)
        {
            if (pair.Value.IndexOf('\0') >= 0)
            {
                throw new InvalidDataException(
                    $"索引 {pair.Key:X7} 的翻譯含有 NUL 字元，無法安全寫入 UTF-16 字串表。");
            }

            if (!string.Equals(result[pair.Key], pair.Value, StringComparison.Ordinal))
            {
                result[pair.Key] = pair.Value;
                updatedCount++;
            }
        }

        return result;
    }

    private static string EscapeText(string value)
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

    private static string UnescapeText(string value, int lineNumber)
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
                            CultureInfo.InvariantCulture, out ushort codePoint))
                    {
                        throw new InvalidDataException($"第 {lineNumber} 行：\\uXXXX 跳脫序列格式錯誤。");
                    }
                    builder.Append((char)codePoint);
                    index += 4;
                    break;
                default:
                    // Keep unknown sequences unchanged rather than silently losing a backslash.
                    builder.Append('\\').Append(escaped);
                    break;
            }
        }
        return builder.ToString();
    }

    private static byte[] EncodeUtf8WithBom(string text)
    {
        byte[] preamble = Utf8WithBom.GetPreamble();
        byte[] body = Utf8WithBom.GetBytes(text);
        byte[] result = new byte[preamble.Length + body.Length];
        Buffer.BlockCopy(preamble, 0, result, 0, preamble.Length);
        Buffer.BlockCopy(body, 0, result, preamble.Length, body.Length);
        return result;
    }

    private static void WriteAtomically(string outputPath, byte[] data)
    {
        string? directory = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrEmpty(directory))
            throw new InvalidOperationException("無法判斷輸出資料夾。");

        Directory.CreateDirectory(directory);
        string temporaryPath = outputPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.WriteAllBytes(temporaryPath, data);
            if (File.Exists(outputPath))
                File.Replace(temporaryPath, outputPath, null);
            else
                File.Move(temporaryPath, outputPath);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private sealed class TranslationRecords
    {
        public Dictionary<int, string> CharacterTranslations { get; }
        public Dictionary<int, string> MessageTranslations { get; }

        public TranslationRecords(Dictionary<int, string> characterTranslations,
            Dictionary<int, string> messageTranslations)
        {
            CharacterTranslations = characterTranslations;
            MessageTranslations = messageTranslations;
        }
    }

    private sealed class ParsedRecord
    {
        public bool IsTranslation { get; }
        public char Type { get; }
        public int Index { get; }
        public string Value { get; }

        public ParsedRecord(bool isTranslation, char type, int index, string value)
        {
            IsTranslation = isTranslation;
            Type = type;
            Index = index;
            Value = value;
        }
    }

    private sealed class ScriptDocument
    {
        private readonly byte[] _source;
        private readonly int _characterIndexOffset;
        private readonly int _messageBlockOffset;
        private readonly int _messageBlockSize;
        private readonly int _opcodeOffset;
        private readonly int _opcodeSize;

        public string Version { get; }
        public string[] CharacterNames { get; }
        public string[] Messages { get; }
        public byte[] OpcodeBytes { get; }

        private ScriptDocument(
            byte[] source,
            string version,
            int characterIndexOffset,
            int messageBlockOffset,
            int messageBlockSize,
            int opcodeOffset,
            int opcodeSize,
            string[] characterNames,
            string[] messages)
        {
            _source = source;
            Version = version;
            _characterIndexOffset = characterIndexOffset;
            _messageBlockOffset = messageBlockOffset;
            _messageBlockSize = messageBlockSize;
            _opcodeOffset = opcodeOffset;
            _opcodeSize = opcodeSize;
            CharacterNames = characterNames;
            Messages = messages;
            OpcodeBytes = new byte[opcodeSize];
            Buffer.BlockCopy(source, opcodeOffset, OpcodeBytes, 0, opcodeSize);
        }

        public static ScriptDocument Load(byte[] data)
        {
            if (data == null)
                throw new ArgumentNullException(nameof(data));
            if (data.Length < MinimumHeaderSize)
                throw new InvalidDataException("檔案太小，不是有效的 BSXScript 3.x 檔案。");

            string signature = Encoding.ASCII.GetString(data, 0, 16).TrimEnd('\0');
            if (signature != "BSXScript 3.0" && signature != "BSXScript 3.1" &&
                signature != "BSXScript 3.2" && signature != "BSXScript 3.3")
                throw new InvalidDataException("不支援此檔案格式。僅支援 BSXScript 3.0～3.3。");

            int characterIndexOffset = ReadInt32(data, CharacterIndexOffsetField);
            int characterIndexSize = ReadInt32(data, CharacterIndexSizeField);
            int characterBlockOffset = ReadInt32(data, CharacterBlockOffsetField);
            int characterBlockSize = ReadInt32(data, CharacterBlockSizeField);
            int messageIndexOffset = ReadInt32(data, MessageIndexOffsetField);
            int messageIndexSize = ReadInt32(data, MessageIndexSizeField);
            int messageBlockOffset = ReadInt32(data, MessageBlockOffsetField);
            int messageBlockSize = ReadInt32(data, MessageBlockSizeField);
            int opcodeOffset = ReadInt32(data, OpcodeOffsetField);
            int opcodeSize = ReadInt32(data, OpcodeSizeField);

            ValidateRange(data.Length, characterIndexOffset, characterIndexSize, "角色名稱索引表");
            ValidateRange(data.Length, characterBlockOffset, characterBlockSize, "角色名稱字串區");
            ValidateRange(data.Length, messageIndexOffset, messageIndexSize, "訊息索引表");
            ValidateRange(data.Length, messageBlockOffset, messageBlockSize, "訊息字串區");
            ValidateRange(data.Length, opcodeOffset, opcodeSize, "Opcode 區段");

            if (characterIndexOffset < MinimumHeaderSize || characterIndexSize % sizeof(int) != 0 ||
                characterBlockSize % 2 != 0 || messageIndexSize % sizeof(int) != 0 || messageBlockSize % 2 != 0)
            {
                throw new InvalidDataException("BSXX 字串表的位址或大小不合理，已中止讀取。");
            }

            long characterIndexEnd = (long)characterIndexOffset + characterIndexSize;
            long characterBlockEnd = (long)characterBlockOffset + characterBlockSize;
            long messageIndexEnd = (long)messageIndexOffset + messageIndexSize;
            long messageBlockEnd = (long)messageBlockOffset + messageBlockSize;

            if (characterIndexEnd > characterBlockOffset ||
                characterBlockEnd > messageIndexOffset ||
                messageIndexEnd > messageBlockOffset ||
                messageBlockEnd > data.Length)
            {
                throw new InvalidDataException("BSXX 區塊排列重疊或超出檔案範圍，可能不是支援的檔案版本。");
            }

            if ((long)opcodeOffset + opcodeSize > characterIndexOffset)
                throw new InvalidDataException("Opcode 區段超出目前支援的 BSXX 資料排列範圍。");

            string[] characterNames = ReadStringTable(
                data, characterIndexOffset, characterIndexSize, characterBlockOffset, characterBlockSize, "角色名稱");
            string[] messages = ReadStringTable(
                data, messageIndexOffset, messageIndexSize, messageBlockOffset, messageBlockSize, "訊息");

            return new ScriptDocument(
                data,
                signature,
                characterIndexOffset,
                messageBlockOffset,
                messageBlockSize,
                opcodeOffset,
                opcodeSize,
                characterNames,
                messages);
        }

        public byte[] BuildTranslated(string[] characterNames, string[] messages)
        {
            if (characterNames.Length != CharacterNames.Length || messages.Length != Messages.Length)
                throw new InvalidOperationException("翻譯項目數量與原始腳本不同，無法安全重建。");

            StringTableBuild characterTable = BuildStringTable(characterNames, "角色名稱");
            StringTableBuild messageTable = BuildStringTable(messages, "訊息");

            using var output = new MemoryStream(capacity: Math.Max(_source.Length, 4096));
            output.Write(_source, 0, _characterIndexOffset);

            int newCharacterIndexOffset = checked((int)output.Position);
            WriteInt32Array(output, characterTable.Offsets);
            Align(output);

            int newCharacterBlockOffset = checked((int)output.Position);
            output.Write(characterTable.Bytes, 0, characterTable.Bytes.Length);
            Align(output);

            int newMessageIndexOffset = checked((int)output.Position);
            WriteInt32Array(output, messageTable.Offsets);
            Align(output);

            int newMessageBlockOffset = checked((int)output.Position);
            output.Write(messageTable.Bytes, 0, messageTable.Bytes.Length);

            // Preserve any trailing data that was outside the declared message block.
            int oldTailOffset = checked(_messageBlockOffset + _messageBlockSize);
            if (oldTailOffset < _source.Length)
                output.Write(_source, oldTailOffset, _source.Length - oldTailOffset);

            byte[] result = output.ToArray();
            WriteInt32(result, CharacterIndexOffsetField, newCharacterIndexOffset);
            WriteInt32(result, CharacterIndexSizeField, checked(characterTable.Offsets.Length * sizeof(int)));
            WriteInt32(result, CharacterBlockOffsetField, newCharacterBlockOffset);
            WriteInt32(result, CharacterBlockSizeField, characterTable.Bytes.Length);
            WriteInt32(result, MessageIndexOffsetField, newMessageIndexOffset);
            WriteInt32(result, MessageIndexSizeField, checked(messageTable.Offsets.Length * sizeof(int)));
            WriteInt32(result, MessageBlockOffsetField, newMessageBlockOffset);
            WriteInt32(result, MessageBlockSizeField, messageTable.Bytes.Length);

            // The script opcodes are not part of localization and must remain byte-identical.
            var rebuiltOpcode = new byte[_opcodeSize];
            Buffer.BlockCopy(result, _opcodeOffset, rebuiltOpcode, 0, _opcodeSize);
            if (!rebuiltOpcode.SequenceEqual(OpcodeBytes))
                throw new InvalidDataException("重建時 Opcode 區段發生變化，已中止輸出。");

            return result;
        }
    }

    private sealed class StringTableBuild
    {
        public int[] Offsets { get; }
        public byte[] Bytes { get; }

        public StringTableBuild(int[] offsets, byte[] bytes)
        {
            Offsets = offsets;
            Bytes = bytes;
        }
    }

    private static string[] ReadStringTable(
        byte[] data,
        int indexOffset,
        int indexSize,
        int blockOffset,
        int blockSize,
        string tableName)
    {
        int count = indexSize / sizeof(int);
        if (count > MaximumStringCount)
            throw new InvalidDataException($"{tableName}項目數量超過安全上限。");

        var relativeOffsets = new int[count];
        for (int index = 0; index < count; index++)
        {
            int units = ReadInt32(data, checked(indexOffset + index * sizeof(int)));
            long relativeBytes = (long)units * 2;
            if (units < 0 || relativeBytes >= blockSize)
                throw new InvalidDataException($"{tableName}索引 {index} 指向字串區範圍之外。");
            relativeOffsets[index] = checked((int)relativeBytes);
        }

        int[] sortedOffsets = relativeOffsets.Distinct().OrderBy(value => value).ToArray();
        var result = new string[count];

        for (int index = 0; index < count; index++)
        {
            int relativeStart = relativeOffsets[index];
            int sortedIndex = Array.BinarySearch(sortedOffsets, relativeStart);
            int relativeLimit = sortedIndex >= 0 && sortedIndex + 1 < sortedOffsets.Length
                ? sortedOffsets[sortedIndex + 1]
                : blockSize;

            int absoluteStart = checked(blockOffset + relativeStart);
            int absoluteLimit = checked(blockOffset + relativeLimit);
            int terminator = FindUtf16Terminator(data, absoluteStart, absoluteLimit, tableName, index);
            int byteLength = terminator - absoluteStart;

            try
            {
                result[index] = Utf16LittleEndian.GetString(data, absoluteStart, byteLength);
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException($"{tableName}索引 {index} 不是有效的 UTF-16LE 字串。", exception);
            }
        }

        return result;
    }

    private static int FindUtf16Terminator(
        byte[] data,
        int start,
        int limit,
        string tableName,
        int index)
    {
        for (int position = start; position + 1 < limit; position += 2)
        {
            if (data[position] == 0 && data[position + 1] == 0)
                return position;
        }

        throw new InvalidDataException($"{tableName}索引 {index} 找不到 UTF-16 結尾字元，檔案可能損毀。");
    }

    private static StringTableBuild BuildStringTable(IReadOnlyList<string> values, string tableName)
    {
        if (values.Count > MaximumStringCount)
            throw new InvalidDataException($"{tableName}項目數量超過安全上限。");

        var offsets = new int[values.Count];
        using var block = new MemoryStream();

        for (int index = 0; index < values.Count; index++)
        {
            string value = values[index] ?? string.Empty;
            if (value.IndexOf('\0') >= 0)
                throw new InvalidDataException($"{tableName}索引 {index} 含有 NUL 字元，不能寫入字串表。");

            offsets[index] = checked((int)(block.Position / 2));
            byte[] encoded;
            try
            {
                encoded = Utf16LittleEndian.GetBytes(value);
            }
            catch (EncoderFallbackException exception)
            {
                throw new InvalidDataException($"{tableName}索引 {index} 含有無效的 UTF-16 字元。", exception);
            }

            block.Write(encoded, 0, encoded.Length);
            block.WriteByte(0);
            block.WriteByte(0);
        }

        return new StringTableBuild(offsets, block.ToArray());
    }

    private static void WriteInt32Array(Stream stream, IReadOnlyList<int> values)
    {
        foreach (int value in values)
        {
            byte[] buffer = BitConverter.GetBytes(value);
            stream.Write(buffer, 0, buffer.Length);
        }
    }

    private static void Align(Stream stream)
    {
        int padding = (Alignment - (int)(stream.Position % Alignment)) % Alignment;
        for (int index = 0; index < padding; index++)
            stream.WriteByte(0);
    }

    private static int ReadInt32(byte[] data, int offset)
    {
        if (offset < 0 || offset > data.Length - sizeof(int))
            throw new InvalidDataException("BSXX 標頭欄位超出檔案範圍。");
        return BitConverter.ToInt32(data, offset);
    }

    private static void WriteInt32(byte[] data, int offset, int value)
    {
        byte[] bytes = BitConverter.GetBytes(value);
        Buffer.BlockCopy(bytes, 0, data, offset, sizeof(int));
    }

    private static void ValidateRange(int totalLength, int offset, int size, string sectionName)
    {
        if (offset < 0 || size < 0 || (long)offset + size > totalLength)
            throw new InvalidDataException($"{sectionName}位址或大小超出檔案範圍。");
    }
}
