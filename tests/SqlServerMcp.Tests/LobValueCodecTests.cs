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

    [Fact]
    public async Task SearchText_CountsAllMatchesAndBoundsReturnedContexts()
    {
        var scan = await LobValueCodec.ScanTextAsync(new StringReader("Alpha xx alpha yy ALPHA"),
            0, 1, 1000, false, searchTerms: ["alpha"], searchContextCharacters: 3, maxMatchesPerTerm: 2);
        var result = scan.Search!;

        Assert.True(result.AnyMatch);
        Assert.True(result.Truncated);
        Assert.Equal(3, result.TotalMatchCount);
        Assert.Equal(2, result.ReturnedMatchCount);
        var term = Assert.Single(result.Terms);
        Assert.Equal(3, term.MatchCount);
        Assert.Equal(2, term.ReturnedCount);
        Assert.True(term.Truncated);
        Assert.Equal("Alpha xx", term.Matches[0].Snippet);
        Assert.Equal(0, term.Matches[0].StartCharacter);
    }

    [Fact]
    public async Task SearchText_DoesNotSplitSurrogatePairsAtSnippetBoundaries()
    {
        var scan = await LobValueCodec.ScanTextAsync(new StringReader("😀needle😀"),
            0, 1, 1000, false, searchTerms: ["needle"], searchContextCharacters: 1, maxMatchesPerTerm: 1);
        var result = scan.Search!;

        var match = Assert.Single(Assert.Single(result.Terms).Matches);
        Assert.Equal("😀needle😀", match.Snippet);
        Assert.Equal(0, match.SnippetStartCharacter);
        Assert.Equal(10, match.SnippetEndCharacterExclusive);
    }

    [Fact]
    public void NormalizeSearchTerms_DeduplicatesAndRejectsTooManyTerms()
    {
        Assert.Equal(["alpha", "beta", " alpha "], LobValueCodec.NormalizeSearchTerms(["alpha", "ALPHA", "beta", " alpha ", "", null!]));

        var ex = Assert.Throws<SqlMcpException>(() => LobValueCodec.NormalizeSearchTerms(
            Enumerable.Range(1, 11).Select(index => $"term-{index}").ToArray()));

        Assert.Equal(ErrorCodes.ConfigInvalid, ex.ErrorCode);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(1, 1)]
    [InlineData(7, 500)]
    [InlineData(8192, 1)]
    [InlineData(8192, 500)]
    public async Task StreamingSearch_MatchesIndependentWholeStringReferenceAcrossReadBoundaries(int readSize, int context)
    {
        var text = new string('x', 8190) + "😀Alpha😀" + new string('a', 17000)
            + "\ud801\udc00\ud801\udc28" + new string('z', 520) + "end😀";
        string[] terms = ["ALPHA", "aa", "😀", "\ud801\udc28", "missing", new string('z', 512), "end"];
        using var reader = new ShortReadReader(text, readSize);
        var scan = await LobValueCodec.ScanTextAsync(reader, 8190, 2, 100_000, false,
            searchTerms: terms, searchContextCharacters: context, maxMatchesPerTerm: 3);

        Assert.Null(scan.FullText);
        Assert.Equal(text.Length, reader.CharactersRead);
        Assert.Equal(LobValueCodec.ComputeSha256Hex(Encoding.UTF8.GetBytes(text)), scan.Sha256Utf8);
        Assert.Equal(LobValueCodec.ComputeSha256Hex(Encoding.Unicode.GetBytes(text)), scan.Sha256Utf16Le);
        foreach (var result in scan.Search!.Terms)
        {
            var offsets = new List<int>();
            for (var next = 0; next <= text.Length - result.Term.Length;)
            {
                var match = text.IndexOf(result.Term, next, StringComparison.OrdinalIgnoreCase);
                if (match < 0) break;
                offsets.Add(match);
                next = match + result.Term.Length;
            }

            Assert.Equal(offsets.Count, result.MatchCount);
            Assert.Equal(offsets.Take(3).Select(value => (long)value), result.Matches.Select(value => value.StartCharacter));
            foreach (var match in result.Matches)
            {
                var start = Math.Max(0, (int)match.StartCharacter - context);
                var end = Math.Min(text.Length, (int)match.EndCharacterExclusive + context);
                if (start > 0 && char.IsSurrogatePair(text, start - 1)) start--;
                if (end > 0 && end < text.Length && char.IsSurrogatePair(text, end - 1)) end++;
                Assert.Equal(text[start..end], match.Snippet);
                Assert.Equal(start > 0, match.BeforeTruncated);
                Assert.Equal(end < text.Length, match.AfterTruncated);
            }
        }
    }

    [Fact]
    public async Task StreamingSearch_BudgetBoundsEscapedContextsWithoutLosingCountsOrIdentity()
    {
        var text = string.Concat(Enumerable.Repeat(new string('\u0001', 500) + "needle", 1000));
        var scan = await LobValueCodec.ScanTextAsync(new StringReader(text), 0, 1, 1_000_000, false,
            searchTerms: ["needle"], searchContextCharacters: 500, maxMatchesPerTerm: 20,
            searchResultByteBudget: 50_000);
        Assert.Equal(1000, scan.Search!.TotalMatchCount);
        Assert.True(scan.Search.Truncated);
        Assert.InRange(scan.Search.ReturnedMatchCount, 1, 19);
        Assert.True(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(scan.Search, JsonResponse.Options).Length < 50_000);
        Assert.Null(scan.FullText);
        Assert.Equal(LobValueCodec.ComputeSha256Hex(Encoding.UTF8.GetBytes(text)), scan.Sha256Utf8);
    }

    [Fact]
    public async Task StreamingSearch_EmptyNoHitsLimitsCancellationAndMalformedIdentity()
    {
        var empty = await LobValueCodec.ScanTextAsync(new StringReader(""), 0, 1, 100, false, searchTerms: ["x"]);
        Assert.False(empty.Search!.AnyMatch);
        Assert.False(empty.Search.Truncated);
        Assert.Empty(Assert.Single(empty.Search.Terms).Matches);
        var noSearch = await LobValueCodec.ScanTextAsync(new StringReader("x"), 0, 1, 100, false, searchTerms: []);
        Assert.Null(noSearch.Search);
        var tooLong = Assert.Throws<SqlMcpException>(() => LobValueCodec.NormalizeSearchTerms([new string('x', 513)]));
        Assert.Equal(ErrorCodes.ConfigInvalid, tooLong.ErrorCode);
        var limit = await Assert.ThrowsAsync<SqlMcpException>(() => LobValueCodec.ScanTextAsync(
            new StringReader("漢字"), 0, 1, 5, false, searchTerms: ["漢"]));
        Assert.Equal(ErrorCodes.ResultTooLarge, limit.ErrorCode);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LobValueCodec.ScanTextAsync(
            new ShortReadReader("abc", 1), 0, 1, 100, false, cancellationToken: cancelled.Token, searchTerms: ["a"]));
        var first = await LobValueCodec.ScanTextAsync(new StringReader("\ud800A"), 0, 1, 100, false, searchTerms: ["A"]);
        var second = await LobValueCodec.ScanTextAsync(new StringReader("\udc00A"), 1, 1, 100, false, searchTerms: ["A"]);
        Assert.Equal(first.Sha256Utf8, second.Sha256Utf8);
        Assert.NotEqual(first.Sha256Utf16Le, second.Sha256Utf16Le);
    }

    private sealed class ShortReadReader(string text, int readSize) : TextReader
    {
        public int CharactersRead { get; private set; }

        public override ValueTask<int> ReadAsync(Memory<char> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(Math.Min(buffer.Length, readSize), text.Length - CharactersRead);
            text.AsMemory(CharactersRead, count).CopyTo(buffer);
            CharactersRead += count;
            return ValueTask.FromResult(count);
        }
    }

    [Fact]
    public void ResponseLimit_CountsEscapingAndConnectionMetadataWithoutLeakingContents()
    {
        var options = new SqlServerMcp.Configuration.SqlServerMcpOptions
        {
            Server = "localhost",
            Database = "SampleDb",
            CredentialTarget = "test",
            Limits = new() { MaxResultMb = 1 }
        };
        SqlMetadataService.EnsureLobResponseWithinLimit(new { chunk = new string('x', 200_000) }, options);
        var error = Assert.Throws<SqlMcpException>(() => SqlMetadataService.EnsureLobResponseWithinLimit(
            new { chunk = new string('\u0001', 200_000), search = new { snippet = "sensitive-value" } }, options));
        Assert.Equal(ErrorCodes.ResultTooLarge, error.ErrorCode);
        Assert.DoesNotContain("sensitive-value", error.ToString());
    }

    [Fact]
    public async Task Search_ClampsLimitsAndPreservesLiteralWhitespace()
    {
        var value = string.Concat(Enumerable.Repeat(" a ", 30));
        var scan = await LobValueCodec.ScanTextAsync(new StringReader(value), 0, 1, 1000, false,
            searchTerms: [" a ", " A ", ""], searchContextCharacters: 999, maxMatchesPerTerm: 999);
        Assert.Equal(500, scan.Search!.ContextCharacters);
        Assert.Equal(20, scan.Search.ReturnedMatchCount);
        Assert.Equal(30, scan.Search.TotalMatchCount);
        Assert.Equal(" a ", Assert.Single(scan.Search.Terms).Term);
        var minimum = await LobValueCodec.ScanTextAsync(new StringReader(value), 0, 1, 1000, false,
            searchTerms: ["a"], searchContextCharacters: -1, maxMatchesPerTerm: 0);
        Assert.Equal(0, minimum.Search!.ContextCharacters);
        Assert.Equal(1, minimum.Search.ReturnedMatchCount);
        Assert.Equal("a", minimum.Search.Terms[0].Matches[0].Snippet);
    }
}
