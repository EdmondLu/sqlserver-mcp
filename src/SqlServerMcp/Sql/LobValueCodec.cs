using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SqlServerMcp.Infrastructure;

namespace SqlServerMcp.Sql;

internal static class LobValueCodec
{
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    public static async Task<TextLobScanResult> ScanTextAsync(
        TextReader reader,
        long offsetCharacters,
        int requestedSize,
        long maxUtf8Bytes,
        bool captureFullText,
        bool deferCursorBoundaryValidation = false,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(offsetCharacters, requestedSize);
        using var utf8Hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var utf16Hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var utf8Encoder = Utf8.GetEncoder();
        var charBuffer = new char[8_192];
        var utf8Buffer = new byte[Utf8.GetMaxByteCount(charBuffer.Length)];
        var utf16Buffer = new byte[charBuffer.Length * 2];
        var chunkBuffer = new StringBuilder(Math.Min(checked(requestedSize + 1), 1_048_576));
        var fullText = captureFullText ? new StringBuilder() : null;
        var totalCharacters = 0L;
        var totalUtf8Bytes = 0L;
        var isAscii = true;
        var cursorSplitsSurrogatePair = false;
        char? previousCharacter = null;

        while (true)
        {
            var read = await reader.ReadAsync(charBuffer.AsMemory(), cancellationToken);
            if (read == 0)
            {
                break;
            }

            fullText?.Append(charBuffer, 0, read);
            for (var index = 0; index < read; index++)
            {
                var character = charBuffer[index];
                var absoluteIndex = totalCharacters + index;
                if (absoluteIndex == offsetCharacters
                    && previousCharacter is char previous
                    && char.IsHighSurrogate(previous)
                    && char.IsLowSurrogate(character))
                {
                    cursorSplitsSurrogatePair = true;
                }

                if (absoluteIndex >= offsetCharacters && chunkBuffer.Length < requestedSize + 1)
                {
                    chunkBuffer.Append(character);
                }

                if (character > 0x7f)
                {
                    isAscii = false;
                }

                utf16Buffer[index * 2] = (byte)character;
                utf16Buffer[index * 2 + 1] = (byte)(character >> 8);
                previousCharacter = character;
            }

            utf16Hash.AppendData(utf16Buffer, 0, read * 2);
            var utf8Count = utf8Encoder.GetBytes(
                charBuffer.AsSpan(0, read),
                utf8Buffer.AsSpan(),
                flush: false);
            totalUtf8Bytes += utf8Count;
            EnsureWithinLimit(totalUtf8Bytes, maxUtf8Bytes);
            utf8Hash.AppendData(utf8Buffer, 0, utf8Count);
            totalCharacters += read;
        }

        var finalUtf8Count = utf8Encoder.GetBytes(
            ReadOnlySpan<char>.Empty,
            utf8Buffer.AsSpan(),
            flush: true);
        totalUtf8Bytes += finalUtf8Count;
        EnsureWithinLimit(totalUtf8Bytes, maxUtf8Bytes);
        utf8Hash.AppendData(utf8Buffer, 0, finalUtf8Count);

        var boundaryError = offsetCharacters > totalCharacters
            ? "LOB cursor offset is outside the value boundary."
            : cursorSplitsSurrogatePair
                ? "LOB cursor offset splits a UTF-16 surrogate pair."
                : null;
        if (boundaryError is not null && !deferCursorBoundaryValidation)
        {
            throw CursorBoundaryError(offsetCharacters, totalCharacters, boundaryError);
        }

        var bufferedChunk = chunkBuffer.ToString();
        var chunkLength = Math.Min(requestedSize, bufferedChunk.Length);
        if (chunkLength < bufferedChunk.Length
            && chunkLength > 0
            && char.IsHighSurrogate(bufferedChunk[chunkLength - 1])
            && char.IsLowSurrogate(bufferedChunk[chunkLength]))
        {
            chunkLength--;
        }

        if (chunkLength == 0
            && bufferedChunk.Length >= 2
            && char.IsSurrogatePair(bufferedChunk, 0))
        {
            chunkLength = 2;
        }

        var chunkText = bufferedChunk[..chunkLength];
        long? nextOffset = offsetCharacters + chunkLength < totalCharacters
            ? offsetCharacters + chunkLength
            : null;
        var chunkUtf8 = Utf8.GetBytes(chunkText);
        return new TextLobScanResult(
            TotalLengthCharacters: totalCharacters,
            TotalLengthUtf8Bytes: totalUtf8Bytes,
            Sha256Utf8: ToHex(utf8Hash.GetHashAndReset()),
            Sha256Utf16Le: ToHex(utf16Hash.GetHashAndReset()),
            IsAscii: isAscii,
            Chunk: new TextLobChunk(
                OffsetCharacters: offsetCharacters,
                LengthCharacters: chunkText.Length,
                Text: chunkText,
                ChunkLengthUtf8Bytes: chunkUtf8.Length,
                ChunkSha256: ComputeSha256Hex(chunkUtf8),
                NextOffsetCharacters: nextOffset),
            BoundaryError: boundaryError,
            FullText: fullText?.ToString());
    }

    public static async Task<BinaryLobScanResult> ScanBinaryAsync(
        Stream stream,
        long offsetBytes,
        int requestedSize,
        long maxBytes,
        bool deferCursorBoundaryValidation = false,
        CancellationToken cancellationToken = default)
    {
        ValidateRequest(offsetBytes, requestedSize);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var chunkBuffer = new MemoryStream(Math.Min(requestedSize, 1_048_576));
        var buffer = new byte[81_920];
        var totalBytes = 0L;

        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(), cancellationToken);
            if (read == 0)
            {
                break;
            }

            var blockStart = totalBytes;
            totalBytes += read;
            EnsureWithinLimit(totalBytes, maxBytes);
            hash.AppendData(buffer, 0, read);

            var captureStart = Math.Max(offsetBytes, blockStart);
            var captureEnd = Math.Min(offsetBytes + requestedSize, totalBytes);
            if (captureStart < captureEnd)
            {
                var sourceOffset = checked((int)(captureStart - blockStart));
                var captureLength = checked((int)(captureEnd - captureStart));
                chunkBuffer.Write(buffer, sourceOffset, captureLength);
            }
        }

        var boundaryError = offsetBytes > totalBytes
            ? "LOB cursor offset is outside the value boundary."
            : null;
        if (boundaryError is not null && !deferCursorBoundaryValidation)
        {
            throw CursorBoundaryError(offsetBytes, totalBytes, boundaryError);
        }

        var chunkBytes = chunkBuffer.ToArray();
        long? nextOffset = offsetBytes + chunkBytes.LongLength < totalBytes
            ? offsetBytes + chunkBytes.LongLength
            : null;
        return new BinaryLobScanResult(
            TotalLengthBytes: totalBytes,
            Sha256: ToHex(hash.GetHashAndReset()),
            Chunk: new BinaryLobChunk(
                OffsetBytes: offsetBytes,
                LengthBytes: chunkBytes.Length,
                Base64: Convert.ToBase64String(chunkBytes),
                ChunkSha256: ComputeSha256Hex(chunkBytes),
                NextOffsetBytes: nextOffset),
            BoundaryError: boundaryError);
    }

    public static string EncodeCursor(LobCursorState value)
    {
        var json = JsonSerializer.Serialize(value, JsonResponse.Options);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    public static LobCursorState? DecodeCursor(string? cursor, string fingerprint)
    {
        if (string.IsNullOrWhiteSpace(cursor))
        {
            return null;
        }

        try
        {
            var base64 = cursor.Trim().Replace('-', '+').Replace('_', '/');
            base64 = base64.PadRight(base64.Length + ((4 - base64.Length % 4) % 4), '=');
            var value = JsonSerializer.Deserialize<LobCursorState>(
                Encoding.UTF8.GetString(Convert.FromBase64String(base64)),
                JsonResponse.Options);
            if (value is null
                || value.Offset < 0
                || value.TotalLengthUnits < 0
                || value.TotalLengthBytes < 0
                || string.IsNullOrWhiteSpace(value.IdentitySha256)
                || value.IdentityEncoding is not ("utf-16le-code-units" or "raw-bytes")
                || value.Kind is not ("text" or "binary")
                || !string.Equals(value.Fingerprint, fingerprint, StringComparison.Ordinal))
            {
                throw new FormatException();
            }

            return value;
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            throw new SqlMcpException(
                ErrorCodes.ConfigInvalid,
                "Invalid or stale LOB cursor.",
                null,
                "Restart read_lob without cursor.");
        }
    }

    public static void ValidateCursorSource(LobCursorState? cursor, LobValueIdentity actual)
    {
        if (cursor is null)
        {
            return;
        }

        if (string.Equals(cursor.IdentitySha256, actual.IdentitySha256, StringComparison.Ordinal)
            && string.Equals(cursor.IdentityEncoding, actual.IdentityEncoding, StringComparison.Ordinal)
            && string.Equals(cursor.Kind, actual.Kind, StringComparison.Ordinal)
            && cursor.TotalLengthUnits == actual.TotalLengthUnits
            && cursor.TotalLengthBytes == actual.TotalLengthBytes)
        {
            return;
        }

        throw new SqlMcpException(
            ErrorCodes.LobCursorExpired,
            "LOB cursor expired because the source value changed.",
            "The complete value identity no longer matches the prior chunk.",
            "Discard all chunks from this read and restart read_lob without cursor.",
            errorDetails: new
            {
                reason = "source_value_changed",
                expected = new
                {
                    cursor.Kind,
                    cursor.TotalLengthUnits,
                    cursor.TotalLengthBytes,
                    sha256 = cursor.IdentitySha256,
                    identityEncoding = cursor.IdentityEncoding
                },
                actual = new
                {
                    actual.Kind,
                    actual.TotalLengthUnits,
                    actual.TotalLengthBytes,
                    sha256 = actual.IdentitySha256,
                    identityEncoding = actual.IdentityEncoding
                }
            });
    }

    public static void ValidateDeferredCursorBoundary(string? boundaryError, long offset, long totalLength)
    {
        if (boundaryError is not null)
        {
            throw CursorBoundaryError(offset, totalLength, boundaryError);
        }
    }

    public static SqlMcpException CreateExpiredCursorForMissingValue(LobCursorState cursor, string reason)
    {
        return new SqlMcpException(
            ErrorCodes.LobCursorExpired,
            "LOB cursor expired because the source value is no longer available.",
            $"reason={reason}",
            "Discard all chunks from this read and restart read_lob without cursor.",
            errorDetails: new
            {
                reason,
                expected = new
                {
                    cursor.Kind,
                    cursor.TotalLengthUnits,
                    cursor.TotalLengthBytes,
                    sha256 = cursor.IdentitySha256,
                    identityEncoding = cursor.IdentityEncoding
                }
            });
    }

    public static string ComputeSha256Hex(ReadOnlySpan<byte> value)
    {
        return ToHex(SHA256.HashData(value));
    }

    private static string ToHex(ReadOnlySpan<byte> value)
    {
        return Convert.ToHexString(value).ToLowerInvariant();
    }

    private static void ValidateRequest(long offset, int requestedSize)
    {
        if (offset < 0)
        {
            throw CursorBoundaryError(offset, null, "LOB cursor offset cannot be negative.");
        }

        if (requestedSize <= 0)
        {
            throw new SqlMcpException(ErrorCodes.ConfigInvalid, "LOB chunk size must be positive.");
        }

        if (offset > long.MaxValue - requestedSize - 1L)
        {
            throw CursorBoundaryError(offset, null, "LOB cursor offset is too large.");
        }
    }

    private static void EnsureWithinLimit(long actualBytes, long maxBytes)
    {
        if (actualBytes <= maxBytes)
        {
            return;
        }

        throw new SqlMcpException(
            ErrorCodes.ResultTooLarge,
            "LOB value exceeded the configured byte limit.",
            $"actualUtf8OrBinaryBytes={actualBytes}; maxBytes={maxBytes}",
            "Raise limits.maxLobMb only after reviewing memory impact, or narrow the SQL expression server-side.",
            errorDetails: new { actualBytes, maxBytes, limitUnit = "bytes" });
    }

    private static SqlMcpException CursorBoundaryError(long offset, long? totalLength, string message)
    {
        return new SqlMcpException(
            ErrorCodes.ConfigInvalid,
            message,
            totalLength is null ? $"offset={offset}" : $"offset={offset}; totalLength={totalLength}",
            "Restart read_lob without cursor.");
    }
}

internal sealed record LobCursorState(
    long Offset,
    string Fingerprint,
    string IdentitySha256,
    string IdentityEncoding,
    string Kind,
    long TotalLengthUnits,
    long TotalLengthBytes);

internal sealed record LobValueIdentity(
    string Kind,
    long TotalLengthUnits,
    long TotalLengthBytes,
    string IdentitySha256,
    string IdentityEncoding);

internal sealed record TextLobScanResult(
    long TotalLengthCharacters,
    long TotalLengthUtf8Bytes,
    string Sha256Utf8,
    string Sha256Utf16Le,
    bool IsAscii,
    TextLobChunk Chunk,
    string? BoundaryError,
    string? FullText);

internal sealed record BinaryLobScanResult(
    long TotalLengthBytes,
    string Sha256,
    BinaryLobChunk Chunk,
    string? BoundaryError);

internal sealed record TextLobChunk(
    long OffsetCharacters,
    int LengthCharacters,
    string Text,
    int ChunkLengthUtf8Bytes,
    string ChunkSha256,
    long? NextOffsetCharacters);

internal sealed record BinaryLobChunk(
    long OffsetBytes,
    int LengthBytes,
    string Base64,
    string ChunkSha256,
    long? NextOffsetBytes);
