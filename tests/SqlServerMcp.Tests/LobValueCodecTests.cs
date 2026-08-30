using System.Text;
using SqlServerMcp.Infrastructure;
using SqlServerMcp.Sql;

namespace SqlServerMcp.Tests;

public sealed class LobValueCodecTests
{
    [Fact]
    public async Task ScanText_ReassemblesUnicodeWithoutSplittingSurrogatePairs()
    {
        const string value = "alpha😀beta漢字omega";
        var chunks = new List<string>();
        var offset = 0L;

        while (offset < value.Length)
        {
            var scan = await LobValueCodec.ScanTextAsync(
                new StringReader(value),
                offset,
                6,
                1_024,
                captureFullText: false);
            chunks.Add(scan.Chunk.Text);
            Assert.Null(scan.FullText);
            Assert.False(scan.Chunk.Text.Length > 0 && char.IsLowSurrogate(scan.Chunk.Text[0]));
            Assert.False(scan.Chunk.Text.Length > 0 && char.IsHighSurrogate(scan.Chunk.Text[^1]));
            offset = scan.Chunk.NextOffsetCharacters ?? value.Length;
        }

        Assert.Equal(value, string.Concat(chunks));
    }

    [Fact]
    public async Task ScanBinary_ReassemblesExactBytesAndHashesCompleteValue()
    {
        var value = Enumerable.Range(0, 1025).Select(index => (byte)(index % 251)).ToArray();
        var reconstructed = new List<byte>();
        var offset = 0L;

        while (offset < value.Length)
        {
            var scan = await LobValueCodec.ScanBinaryAsync(
                new MemoryStream(value, writable: false),
                offset,
                113,
                2_048);
            var chunk = Convert.FromBase64String(scan.Chunk.Base64);
            reconstructed.AddRange(chunk);
            Assert.Equal(LobValueCodec.ComputeSha256Hex(value), scan.Sha256);
            Assert.Equal(LobValueCodec.ComputeSha256Hex(chunk), scan.Chunk.ChunkSha256);
            offset = scan.Chunk.NextOffsetBytes ?? value.Length;
        }

        Assert.Equal(value, reconstructed);
    }

    [Fact]
    public void Cursor_RoundTripsCompleteValueIdentityAndRejectsStaleFingerprint()
    {
        var state = new LobCursorState(
            123,
            "fingerprint-a",
            "abc123",
            "utf-16le-code-units",
            "text",
            500,
            900);
        var cursor = LobValueCodec.EncodeCursor(state);

        Assert.Equal(state, LobValueCodec.DecodeCursor(cursor, "fingerprint-a"));
        var ex = Assert.Throws<SqlMcpException>(() => LobValueCodec.DecodeCursor(cursor, "fingerprint-b"));
        Assert.Equal(ErrorCodes.ConfigInvalid, ex.ErrorCode);
    }

    [Fact]
    public async Task Cursor_RejectsChangedTextSourceAfterCompleteRescan()
    {
        const string original = "alpha😀beta";
        const string changed = "alpha😀BETA";
        var first = await LobValueCodec.ScanTextAsync(new StringReader(original), 0, 5, 1_024, false);
        var cursor = new LobCursorState(
            first.Chunk.NextOffsetCharacters!.Value,
            "query",
            first.Sha256Utf16Le,
            "utf-16le-code-units",
            "text",
            first.TotalLengthCharacters,
            first.TotalLengthUtf8Bytes);
        var second = await LobValueCodec.ScanTextAsync(new StringReader(changed), cursor.Offset, 5, 1_024, false);

        var ex = Assert.Throws<SqlMcpException>(() => LobValueCodec.ValidateCursorSource(
            cursor,
            new LobValueIdentity(
                "text",
                second.TotalLengthCharacters,
                second.TotalLengthUtf8Bytes,
                second.Sha256Utf16Le,
                "utf-16le-code-units")));

        Assert.Equal(ErrorCodes.LobCursorExpired, ex.ErrorCode);
        Assert.Contains("source value changed", ex.Message, StringComparison.Ordinal);
        Assert.NotNull(ex.ErrorDetails);
    }

    [Fact]
    public async Task Cursor_ReportsSourceChangeBeforeBoundaryErrorWhenNewTextIsShorter()
    {
        const string original = "0123456789abcdef";
        const string changed = "short";
        var first = await LobValueCodec.ScanTextAsync(new StringReader(original), 0, 12, 1_024, false);
        var cursor = new LobCursorState(
            first.Chunk.NextOffsetCharacters!.Value,
            "query",
            first.Sha256Utf16Le,
            "utf-16le-code-units",
            "text",
            first.TotalLengthCharacters,
            first.TotalLengthUtf8Bytes);
        var second = await LobValueCodec.ScanTextAsync(
            new StringReader(changed),
            cursor.Offset,
            4,
            1_024,
            captureFullText: false,
            deferCursorBoundaryValidation: true);

        var ex = Assert.Throws<SqlMcpException>(() => LobValueCodec.ValidateCursorSource(
            cursor,
            new LobValueIdentity(
                "text",
                second.TotalLengthCharacters,
                second.TotalLengthUtf8Bytes,
                second.Sha256Utf16Le,
                "utf-16le-code-units")));

        Assert.Equal(ErrorCodes.LobCursorExpired, ex.ErrorCode);
        Assert.NotNull(second.BoundaryError);
    }

    [Fact]
    public async Task Cursor_RejectsChangedBinarySourceEvenWhenLengthIsUnchanged()
    {
        var original = Enumerable.Range(0, 512).Select(index => (byte)index).ToArray();
        var changed = original.ToArray();
        changed[400] ^= 0xff;
        var first = await LobValueCodec.ScanBinaryAsync(new MemoryStream(original), 0, 64, 1_024);
        var cursor = new LobCursorState(
            first.Chunk.NextOffsetBytes!.Value,
            "query",
            first.Sha256,
            "raw-bytes",
            "binary",
            first.TotalLengthBytes,
            first.TotalLengthBytes);
        var second = await LobValueCodec.ScanBinaryAsync(new MemoryStream(changed), cursor.Offset, 64, 1_024);

        var ex = Assert.Throws<SqlMcpException>(() => LobValueCodec.ValidateCursorSource(
            cursor,
            new LobValueIdentity(
                "binary",
                second.TotalLengthBytes,
                second.TotalLengthBytes,
                second.Sha256,
                "raw-bytes")));

        Assert.Equal(ErrorCodes.LobCursorExpired, ex.ErrorCode);
    }

    [Fact]
    public async Task Cursor_UsesExactUtf16CodeUnitsWhenInvalidSurrogatesShareUtf8Hash()
    {
        const string original = "\ud800A";
        var first = await LobValueCodec.ScanTextAsync(new StringReader(original), 0, 1, 1_024, false);
        foreach (var changed in new[] { "\udc00A", "\ud801A" })
        {
            var second = await LobValueCodec.ScanTextAsync(new StringReader(changed), 1, 1, 1_024, false);

            Assert.Equal(first.TotalLengthCharacters, second.TotalLengthCharacters);
            Assert.Equal(first.TotalLengthUtf8Bytes, second.TotalLengthUtf8Bytes);
            Assert.Equal(first.Sha256Utf8, second.Sha256Utf8);
            Assert.NotEqual(first.Sha256Utf16Le, second.Sha256Utf16Le);

            var cursor = new LobCursorState(
                1,
                "query",
                first.Sha256Utf16Le,
                "utf-16le-code-units",
                "text",
                first.TotalLengthCharacters,
                first.TotalLengthUtf8Bytes);
            var ex = Assert.Throws<SqlMcpException>(() => LobValueCodec.ValidateCursorSource(
                cursor,
                new LobValueIdentity(
                    "text",
                    second.TotalLengthCharacters,
                    second.TotalLengthUtf8Bytes,
                    second.Sha256Utf16Le,
                    "utf-16le-code-units")));

            Assert.Equal(ErrorCodes.LobCursorExpired, ex.ErrorCode);
            Assert.Contains("source value changed", ex.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ScanText_RejectsCursorThatSplitsSurrogatePair()
    {
        const string value = "a😀b";

        var ex = await Assert.ThrowsAsync<SqlMcpException>(() => LobValueCodec.ScanTextAsync(
            new StringReader(value),
            2,
            1,
            100,
            false));

        Assert.Equal(ErrorCodes.ConfigInvalid, ex.ErrorCode);
        Assert.Contains("surrogate pair", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScanText_EnforcesMaxLobInUtf8BytesForMultibyteUnicodeWithoutFullCapture()
    {
        const string value = "漢字漢字"; // 12 UTF-8 bytes, 4 UTF-16 code units

        var ex = await Assert.ThrowsAsync<SqlMcpException>(() => LobValueCodec.ScanTextAsync(
            new StringReader(value),
            0,
            1,
            10,
            captureFullText: false));

        Assert.Equal(ErrorCodes.ResultTooLarge, ex.ErrorCode);
        Assert.Contains("maxBytes=10", ex.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScanText_HashesUtf8AndUtf16AndOnlyCapturesFullTextWhenRequested()
    {
        const string value = "BOM:\ufeff XML:<?xml?>";

        var scan = await LobValueCodec.ScanTextAsync(
            new StringReader(value),
            0,
            4,
            1_024,
            captureFullText: true);

        Assert.Equal(value, scan.FullText);
        Assert.Equal(LobValueCodec.ComputeSha256Hex(Encoding.UTF8.GetBytes(value)), scan.Sha256Utf8);
        Assert.Equal(LobValueCodec.ComputeSha256Hex(Encoding.Unicode.GetBytes(value)), scan.Sha256Utf16Le);
    }
}
