using RedigiAI.Domain.Essays;

namespace RedigiAI.UnitTests.Essays;

public class TranscriptionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 15, 0, 0, TimeSpan.Zero);

    private static Transcription Read(params string[] lines) =>
        Transcription.Create(
            Guid.CreateVersion7(),
            sheetLines: 30,
            [.. lines.Select((text, index) => new ReadLine(index + 1, text, Indent: false))],
            identificationFound: false,
            "test-model",
            Now);

    [Fact]
    public void Each_marker_becomes_an_illegible_word_with_line_and_position_computed_by_the_backend()
    {
        var transcription = Read("Os [?] decidem o que vemos", "sem palavra ilegivel", "[?] isso [?] o debate");

        Assert.Equal(3, transcription.IllegibleCount);
        Assert.Equal(
            [(1, 3, 6), (3, 0, 3), (3, 9, 12)],
            transcription.Words.Select(w => (w.LineNumber, w.CharStart, w.CharEnd)));
        // A transcrição guarda o texto como veio, com o marcador.
        Assert.Equal("Os [?] decidem o que vemos", transcription.Lines[0].Text);
    }

    [Fact]
    public void Line_without_marker_has_no_illegible_word()
    {
        var transcription = Read("Texto inteiro legivel.", "");

        Assert.Equal(0, transcription.IllegibleCount);
        Assert.Empty(transcription.Words);
    }

    [Theory]
    [InlineData(10, false)]
    [InlineData(11, true)]
    public void More_than_10_markers_make_the_photo_unreadable(int markers, bool unreadable)
    {
        // Espalhados em duas linhas: a regra conta os marcadores da folha inteira.
        var transcription = Read(
            string.Join(' ', Enumerable.Repeat(Transcription.IllegibleMarker, markers - 4)),
            string.Join(' ', Enumerable.Repeat(Transcription.IllegibleMarker, 4)));

        Assert.Equal(markers, transcription.IllegibleCount);
        Assert.Equal(unreadable, transcription.IsUnreadable);
    }
}
