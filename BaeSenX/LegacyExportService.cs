using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace BaeSenX;

/// <summary>
/// Summary of a BSXScript_Tool-era DAT file parsed for read-only TXT export.
/// The physical data boundaries are derived from the next section and EOF,
/// because legacy BSXScript_Tool files may contain stale string-block sizes.
/// </summary>
public sealed class LegacyExportSummary
{
    public string Version { get; }
    public int CharacterCount { get; }
    public int MessageCount { get; }
    public long FileSize { get; }
    public int DeclaredCharacterBlockSize { get; }
    public int AvailableCharacterBlockSize { get; }
    public int DeclaredMessageBlockSize { get; }
    public int AvailableMessageBlockSize { get; }
    public int CharacterTrailingByteCount { get; }
    public int MessageTrailingByteCount { get; }

    public bool CharacterBlockSizeMatchesAvailable =>
        DeclaredCharacterBlockSize == AvailableCharacterBlockSize;

    public bool MessageBlockSizeMatchesAvailable =>
        DeclaredMessageBlockSize == AvailableMessageBlockSize;

    internal LegacyExportSummary(
        string version,
        int characterCount,
        int messageCount,
        long fileSize,
        int declaredCharacterBlockSize,
        int availableCharacterBlockSize,
        int declaredMessageBlockSize,
        int availableMessageBlockSize,
        int characterTrailingByteCount,
        int messageTrailingByteCount)
    {
        Version = version;
        CharacterCount = characterCount;
        MessageCount = messageCount;
        FileSize = fileSize;
        DeclaredCharacterBlockSize = declaredCharacterBlockSize;
        AvailableCharacterBlockSize = availableCharacterBlockSize;
        DeclaredMessageBlockSize = declaredMessageBlockSize;
        AvailableMessageBlockSize = availableMessageBlockSize;
        CharacterTrailingByteCount = characterTrailingByteCount;
        MessageTrailingByteCount = messageTrailingByteCount;
    }
}

/// <summary>
/// Parsed, read-only contents of a legacy BSXScript_Tool DAT file.
/// CharacterNames and Messages retain the index-table order from that file.
/// </summary>
public sealed class LegacyScriptDocument
{
    private readonly byte[] _characterTrailingData;
    private readonly byte[] _messageTrailingData;

    public string Version { get; }
    public IReadOnlyList<string> CharacterNames { get; }
    public IReadOnlyList<string> Messages { get; }
    public long FileSize { get; }
    public int CharacterBlockOffset { get; }
    public int DeclaredCharacterBlockSize { get; }
    public int AvailableCharacterBlockSize { get; }
    public int MessageIndexOffset { get; }
    public int MessageBlockOffset { get; }
    public int DeclaredMessageBlockSize { get; }
    public int AvailableMessageBlockSize { get; }
    public LegacyExportSummary Summary { get; }

    public byte[] GetCharacterTrailingData()
    {
        return (byte[])_characterTrailingData.Clone();
    }

    public byte[] GetMessageTrailingData()
    {
        return (byte[])_messageTrailingData.Clone();
    }

    internal LegacyScriptDocument(
        string version,
        string[] characterNames,
        string[] messages,
        long fileSize,
        int characterBlockOffset,
        int declaredCharacterBlockSize,
        int availableCharacterBlockSize,
        int messageIndexOffset,
        int messageBlockOffset,
        int declaredMessageBlockSize,
        int availableMessageBlockSize,
        byte[] characterTrailingData,
        byte[] messageTrailingData)
    {
        Version = version;
        CharacterNames = new ReadOnlyCollection<string>(characterNames);
        Messages = new ReadOnlyCollection<string>(messages);
        FileSize = fileSize;
        CharacterBlockOffset = characterBlockOffset;
        DeclaredCharacterBlockSize = declaredCharacterBlockSize;
        AvailableCharacterBlockSize = availableCharacterBlockSize;
        MessageIndexOffset = messageIndexOffset;
        MessageBlockOffset = messageBlockOffset;
        DeclaredMessageBlockSize = declaredMessageBlockSize;
        AvailableMessageBlockSize = availableMessageBlockSize;
        _characterTrailingData = (byte[])characterTrailingData.Clone();
        _messageTrailingData = (byte[])messageTrailingData.Clone();

        Summary = new LegacyExportSummary(
            version,
            characterNames.Length,
            messages.Length,
            fileSize,
            declaredCharacterBlockSize,
            availableCharacterBlockSize,
            declaredMessageBlockSize,
            availableMessageBlockSize,
            characterTrailingData.Length,
            messageTrailingData.Length);
    }
}

/// <summary>
/// Read-only converter for DAT files written by the older BSXScript_Tool.
/// This service is intentionally independent from TranslationService:
/// it never imports translations, rebuilds DAT files, compares files, or
/// requires the original game script.
/// </summary>
public static class LegacyExportService
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
    private const int MaximumStringCount = 1_000_000;

    private static readonly Encoding Utf16LittleEndian = new UnicodeEncoding(
        bigEndian: false,
        byteOrderMark: false,
        throwOnInvalidBytes: true);

    private static readonly Encoding Utf8WithBom = new UTF8Encoding(
        encoderShouldEmitUTF8Identifier: true,
        throwOnInvalidBytes: true);

    /// <summary>
    /// Reads and validates a legacy DAT file without modifying it.
    /// No original/untranslated DAT file is needed.
    /// </summary>
    public static LegacyScriptDocument Read(string legacyDatPath)
    {
        ValidatePath(legacyDatPath, nameof(legacyDatPath));
        string fullPath = Path.GetFullPath(legacyDatPath);
        byte[] data = File.ReadAllBytes(fullPath);
        return ReadDocument(data);
    }

    /// <summary>
    /// Validates a legacy DAT file and returns its table counts and physical ranges.
    /// </summary>
    public static LegacyExportSummary Inspect(string legacyDatPath)
    {
        return Read(legacyDatPath).Summary;
    }

    /// <summary>
    /// Exports character names and the shared dialogue/choice table to a
    /// UTF-8-BOM reference TXT. The source DAT is never changed.
    /// </summary>
    public static LegacyExportSummary ExportText(string legacyDatPath, string outputTextPath)
    {
        ValidatePath(legacyDatPath, nameof(legacyDatPath));
        ValidatePath(outputTextPath, nameof(outputTextPath));

        string sourceFullPath = Path.GetFullPath(legacyDatPath);
        string outputFullPath = Path.GetFullPath(outputTextPath);
        if (string.Equals(sourceFullPath, outputFullPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "TXT 輸出路徑不能與舊版 DAT 相同，已拒絕覆蓋來源檔案。");
        }

        LegacyScriptDocument document = Read(sourceFullPath);
        string content = BuildExportText(document, Path.GetFileName(sourceFullPath));
        byte[] preamble = Utf8WithBom.GetPreamble();
        byte[] body = Utf8WithBom.GetBytes(content);
        var outputBytes = new byte[preamble.Length + body.Length];
        Buffer.BlockCopy(preamble, 0, outputBytes, 0, preamble.Length);
        Buffer.BlockCopy(body, 0, outputBytes, preamble.Length, body.Length);
        WriteAtomically(outputFullPath, outputBytes);
        return document.Summary;
    }

    private static LegacyScriptDocument ReadDocument(byte[] data)
    {
        if (data == null)
            throw new ArgumentNullException(nameof(data));
        if (data.Length < MinimumHeaderSize)
            throw new InvalidDataException("舊版 DAT 太小，缺少完整的 BSXScript 標頭。");

        string signature = Encoding.ASCII.GetString(data, 0, 16).TrimEnd('\0');
        if (signature != "BSXScript 3.0" && signature != "BSXScript 3.1" &&
            signature != "BSXScript 3.2" && signature != "BSXScript 3.3")
        {
            throw new InvalidDataException(
                "不支援此舊版 DAT 格式。僅支援 BSXScript 3.0～3.3。");
        }

        int characterIndexOffset = ReadInt32(data, CharacterIndexOffsetField);
        int characterIndexSize = ReadInt32(data, CharacterIndexSizeField);
        int characterBlockOffset = ReadInt32(data, CharacterBlockOffsetField);
        int declaredCharacterBlockSize = ReadInt32(data, CharacterBlockSizeField);
        int messageIndexOffset = ReadInt32(data, MessageIndexOffsetField);
        int messageIndexSize = ReadInt32(data, MessageIndexSizeField);
        int messageBlockOffset = ReadInt32(data, MessageBlockOffsetField);
        int declaredMessageBlockSize = ReadInt32(data, MessageBlockSizeField);

        ValidateIndexTableRange(
            data.Length, characterIndexOffset, characterIndexSize, "角色名稱索引表");
        ValidateIndexTableRange(
            data.Length, messageIndexOffset, messageIndexSize, "訊息索引表");

        if (characterBlockOffset < MinimumHeaderSize || characterBlockOffset > data.Length)
            throw new InvalidDataException("角色名稱字串區起點超出檔案範圍。");
        if (messageIndexOffset < characterBlockOffset || messageIndexOffset > data.Length)
            throw new InvalidDataException("訊息索引表位置不在角色名稱字串區之後。");
        if (messageBlockOffset < MinimumHeaderSize || messageBlockOffset > data.Length)
            throw new InvalidDataException("訊息字串區起點超出檔案範圍。");
        if (declaredCharacterBlockSize < 0 || (declaredCharacterBlockSize % 2) != 0)
            throw new InvalidDataException("舊版標頭的角色名稱字串區大小為負值或非偶數。");
        if (declaredMessageBlockSize < 0 || (declaredMessageBlockSize % 2) != 0)
            throw new InvalidDataException("舊版標頭的訊息字串區大小為負值或非偶數。");

        long characterIndexEnd = (long)characterIndexOffset + characterIndexSize;
        long messageIndexEnd = (long)messageIndexOffset + messageIndexSize;
        if (characterIndexEnd > characterBlockOffset)
            throw new InvalidDataException("角色名稱索引表與字串區重疊。");
        if (messageIndexEnd > messageBlockOffset)
            throw new InvalidDataException("訊息索引表與字串區重疊。");

        // Legacy BSXScript_Tool may leave the old declared character block size
        // unchanged after shortening strings. The next section's offset is the
        // hard boundary; never read into the message index table.
        int availableCharacterBlockSize = checked(messageIndexOffset - characterBlockOffset);

        // Message data is the final data region in the known BSXScript 3.x
        // layout. The file's actual EOF is its hard boundary; the legacy header
        // size can be stale (the supplied sample declares more bytes than exist).
        int availableMessageBlockSize = checked(data.Length - messageBlockOffset);

        if ((availableCharacterBlockSize % 2) != 0)
            throw new InvalidDataException("角色名稱字串區的實際範圍不是 UTF-16LE 偶數位元組長度。");
        if ((availableMessageBlockSize % 2) != 0)
            throw new InvalidDataException("訊息字串區的實際範圍不是 UTF-16LE 偶數位元組長度。");

        string[] characterNames = ReadStringTable(
            data,
            characterIndexOffset,
            characterIndexSize,
            characterBlockOffset,
            availableCharacterBlockSize,
            "角色名稱",
            out int characterTailStart);

        string[] messages = ReadStringTable(
            data,
            messageIndexOffset,
            messageIndexSize,
            messageBlockOffset,
            availableMessageBlockSize,
            "訊息／選項",
            out int messageTailStart);

        byte[] characterTrailingData = CopyRange(
            data,
            checked(characterBlockOffset + characterTailStart),
            checked(availableCharacterBlockSize - characterTailStart));

        byte[] messageTrailingData = CopyRange(
            data,
            checked(messageBlockOffset + messageTailStart),
            checked(availableMessageBlockSize - messageTailStart));

        return new LegacyScriptDocument(
            signature,
            characterNames,
            messages,
            data.LongLength,
            characterBlockOffset,
            declaredCharacterBlockSize,
            availableCharacterBlockSize,
            messageIndexOffset,
            messageBlockOffset,
            declaredMessageBlockSize,
            availableMessageBlockSize,
            characterTrailingData,
            messageTrailingData);
    }

    private static string[] ReadStringTable(
        byte[] data,
        int indexOffset,
        int indexSize,
        int blockOffset,
        int blockSize,
        string tableName,
        out int trailingDataStart)
    {
        int count = indexSize / sizeof(int);
        if (count > MaximumStringCount)
            throw new InvalidDataException(tableName + "項目數量超過安全上限。");

        if (count > 0 && blockSize < 2)
            throw new InvalidDataException(tableName + "字串區太小，無法容納任何 UTF-16LE 字串。");

        var relativeOffsets = new int[count];
        for (int index = 0; index < count; index++)
        {
            int units = ReadInt32(data, checked(indexOffset + index * sizeof(int)));
            if (units < 0)
                throw new InvalidDataException(
                    tableName + "索引 " + index.ToString(CultureInfo.InvariantCulture) + " 是負數。");

            long relativeBytes = (long)units * 2L;
            if (relativeBytes < 0 || relativeBytes >= blockSize)
            {
                throw new InvalidDataException(
                    tableName + "索引 " + index.ToString(CultureInfo.InvariantCulture) +
                    " 指向實際字串區範圍之外（相對位址 " +
                    relativeBytes.ToString(CultureInfo.InvariantCulture) + " bytes）。");
            }

            relativeOffsets[index] = checked((int)relativeBytes);
        }

        int[] sortedOffsets = relativeOffsets.Distinct().OrderBy(value => value).ToArray();
        var values = new string[count];
        var decodedByOffset = new Dictionary<int, string>();
        int lastStringEnd = 0;

        for (int sortedIndex = 0; sortedIndex < sortedOffsets.Length; sortedIndex++)
        {
            int relativeStart = sortedOffsets[sortedIndex];
            int relativeLimit = sortedIndex + 1 < sortedOffsets.Length
                ? sortedOffsets[sortedIndex + 1]
                : blockSize;

            if (relativeLimit <= relativeStart)
                throw new InvalidDataException(tableName + "索引位址順序無效。");

            int absoluteStart = checked(blockOffset + relativeStart);
            int absoluteLimit = checked(blockOffset + relativeLimit);
            int terminator = FindUtf16Terminator(
                data, absoluteStart, absoluteLimit, tableName, relativeStart);
            int byteLength = checked(terminator - absoluteStart);

            string value;
            try
            {
                value = Utf16LittleEndian.GetString(data, absoluteStart, byteLength);
            }
            catch (DecoderFallbackException exception)
            {
                throw new InvalidDataException(
                    tableName + "字串區相對位址 " +
                    relativeStart.ToString(CultureInfo.InvariantCulture) +
                    " 含有無效 UTF-16LE 序列。", exception);
            }

            decodedByOffset.Add(relativeStart, value);
            if (sortedIndex == sortedOffsets.Length - 1)
                lastStringEnd = checked(relativeStart + byteLength + sizeof(char));
        }

        for (int index = 0; index < count; index++)
            values[index] = decodedByOffset[relativeOffsets[index]];

        // Bytes after the last string terminator are retained as explicit
        // trailing data and included in the exported TXT as Base64 comments.
        trailingDataStart = sortedOffsets.Length == 0 ? 0 : lastStringEnd;
        if (trailingDataStart < 0 || trailingDataStart > blockSize)
            throw new InvalidDataException(tableName + "尾端資料邊界計算錯誤。");

        return values;
    }

    private static int FindUtf16Terminator(
        byte[] data,
        int start,
        int limit,
        string tableName,
        int relativeStart)
    {
        for (int position = start; position + 1 < limit; position += 2)
        {
            if (data[position] == 0 && data[position + 1] == 0)
                return position;
        }

        throw new InvalidDataException(
            tableName + "字串（區內位址 " +
            relativeStart.ToString(CultureInfo.InvariantCulture) +
            "）在下一筆索引或區段邊界之前找不到 UTF-16LE NUL 結束字元。");
    }

    private static string BuildExportText(LegacyScriptDocument document, string sourceName)
    {
        var builder = new StringBuilder(
            Math.Max(4096, (document.CharacterNames.Count + document.Messages.Count) * 64));

        builder.AppendLine("# BSXScript_Tool Legacy DAT Export v1");
        builder.AppendLine("# Read-only reference export. This file is for manual comparison only.");
        builder.AppendLine("# Do not import this file with BaeSenX; use the NEW BaeSenX TXT for repacking.");
        builder.AppendLine("# Source: " + sourceName);
        builder.AppendLine("# BSXScript: " + document.Version);
        builder.AppendLine("# Character entries: " + document.CharacterNames.Count.ToString(CultureInfo.InvariantCulture));
        builder.AppendLine("# Message/dialogue/choice entries: " + document.Messages.Count.ToString(CultureInfo.InvariantCulture));
        builder.AppendLine(
            "# Character data: offset=0x" + document.CharacterBlockOffset.ToString("X8", CultureInfo.InvariantCulture) +
            "; declared=" + document.DeclaredCharacterBlockSize.ToString(CultureInfo.InvariantCulture) +
            "; available-to-message-index=" + document.AvailableCharacterBlockSize.ToString(CultureInfo.InvariantCulture));
        builder.AppendLine(
            "# Message data: offset=0x" + document.MessageBlockOffset.ToString("X8", CultureInfo.InvariantCulture) +
            "; declared=" + document.DeclaredMessageBlockSize.ToString(CultureInfo.InvariantCulture) +
            "; available-to-EOF=" + document.AvailableMessageBlockSize.ToString(CultureInfo.InvariantCulture));

        byte[] characterTail = document.GetCharacterTrailingData();
        byte[] messageTail = document.GetMessageTrailingData();
        AppendTailComment(builder, "Character", characterTail);
        AppendTailComment(builder, "Message", messageTail);

        builder.AppendLine("# Records: A = character name; B = message/dialogue/choice text.");
        builder.AppendLine("# For each index, ◇ and ◆ rows both contain the current text stored in this legacy DAT.");
        builder.AppendLine("# Indices are preserved in the order stored in the legacy DAT.");
        builder.AppendLine("# Escape sequences: \\n \\r \\t \\\\ \\uXXXX");
        builder.AppendLine();

        AppendTable(builder, 'A', document.CharacterNames);
        AppendTable(builder, 'B', document.Messages);
        return builder.ToString();
    }

    private static void AppendTailComment(StringBuilder builder, string name, byte[] data)
    {
        builder.AppendLine(
            "# " + name + " trailing bytes after last string terminator: " +
            data.Length.ToString(CultureInfo.InvariantCulture));
        builder.AppendLine(
            "# " + name + " trailing data Base64: " +
            (data.Length == 0 ? "(none)" : Convert.ToBase64String(data)));
    }

    private static void AppendTable(StringBuilder builder, char type, IReadOnlyList<string> values)
    {
        builder.AppendLine("# ===== " + (type == 'A' ? "角色名稱" : "對話／選項文字") + " (" + type + ") =====");
        for (int index = 0; index < values.Count; index++)
        {
            string id = type + index.ToString("X7", CultureInfo.InvariantCulture);
            string escaped = EscapeText(values[index]);
            builder.Append('◇')
                .Append(id)
                .Append('◇')
                .AppendLine(escaped);
            builder.Append('◆')
                .Append(id)
                .Append('◆')
                .AppendLine(escaped);
            builder.AppendLine();
        }
        builder.AppendLine();
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
                    {
                        builder.Append("\\u").Append(
                            ((int)character).ToString("X4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        builder.Append(character);
                    }
                    break;
            }
        }
        return builder.ToString();
    }

    private static void ValidateIndexTableRange(
        int totalLength,
        int offset,
        int size,
        string sectionName)
    {
        if (offset < MinimumHeaderSize || size < 0 || (size % sizeof(int)) != 0 ||
            (long)offset + size > totalLength)
        {
            throw new InvalidDataException(sectionName + "位址或大小超出檔案範圍，或索引表大小不是 4 bytes 的倍數。");
        }

        int count = size / sizeof(int);
        if (count > MaximumStringCount)
            throw new InvalidDataException(sectionName + "項目數量超過安全上限。");
    }

    private static int ReadInt32(byte[] data, int offset)
    {
        if (offset < 0 || offset > data.Length - sizeof(int))
            throw new InvalidDataException("BSXX 標頭或索引欄位超出檔案範圍。");

        return unchecked(
            data[offset] |
            (data[offset + 1] << 8) |
            (data[offset + 2] << 16) |
            (data[offset + 3] << 24));
    }

    private static byte[] CopyRange(byte[] data, int offset, int count)
    {
        if (offset < 0 || count < 0 || (long)offset + count > data.Length)
            throw new InvalidDataException("尾端資料範圍超出檔案範圍。");

        var result = new byte[count];
        if (count > 0)
            Buffer.BlockCopy(data, offset, result, 0, count);
        return result;
    }

    private static void ValidatePath(string path, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("檔案路徑不可空白。", parameterName);
    }

    private static void WriteAtomically(string outputPath, byte[] data)
    {
        string? directory = Path.GetDirectoryName(outputPath);
        if (string.IsNullOrEmpty(directory))
            throw new InvalidOperationException("無法判斷 TXT 輸出資料夾。");

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
}
