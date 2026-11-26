using Invest.Sign.Router.Core.Abstractions;
using Invest.Sign.Router.Core.Model;
using Invest.Sign.Router.Infrastructure.Stages;

namespace Invest.Sign.Router.IntegrationTests.Stages;

/// <summary>Тесты файловой системы; Docker не нужен. Каждый тест работает в собственном временном каталоге.</summary>
public sealed class FileSigningStageHandlerTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "sgn-filesign-" + Guid.NewGuid().ToString("N"));
    private readonly FileSigningOptions _options;
    private readonly FileSigningStageHandler _handler;

    public FileSigningStageHandlerTests()
    {
        _options = new FileSigningOptions
        {
            SourceDirectory = Path.Combine(_base, "source"),
            SigningDirectory = Path.Combine(_base, "signing"),
            SignedDirectory = Path.Combine(_base, "signed"),
            FinalDirectory = Path.Combine(_base, "final"),
        };
        Directory.CreateDirectory(_options.SourceDirectory);
        Directory.CreateDirectory(_options.SignedDirectory);
        _handler = new FileSigningStageHandler(_options);
    }

    public void Dispose() => Directory.Delete(_base, recursive: true);

    [Fact]
    public async Task Start_moves_file_from_source_to_signing_directory()
    {
        Write(_options.SourceDirectory, "doc.pdf", "content");

        var outcome = await _handler.StartAsync(Stage(), default);

        Assert.IsType<StageOutcome.Waiting>(outcome);
        Assert.False(File.Exists(Path.Combine(_options.SourceDirectory, "doc.pdf")));
        Assert.Equal("content", await File.ReadAllTextAsync(Path.Combine(_options.SigningDirectory, "doc.pdf")));
    }

    [Fact]
    public async Task Repeated_start_after_move_is_a_no_op()
    {
        Write(_options.SourceDirectory, "doc.pdf", "content");
        await _handler.StartAsync(Stage(), default);

        var outcome = await _handler.StartAsync(Stage(), default);

        Assert.IsType<StageOutcome.Waiting>(outcome);
        Assert.True(File.Exists(Path.Combine(_options.SigningDirectory, "doc.pdf")));
    }

    [Fact]
    public async Task Start_without_source_file_is_a_technical_error()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() => _handler.StartAsync(Stage(), default));
    }

    [Theory]
    [InlineData("""{"file_name":"../doc.pdf"}""")]
    [InlineData("""{"file_name":"sub/doc.pdf"}""")]
    [InlineData("""{"file_name":".."}""")]
    [InlineData("""{"other":"doc.pdf"}""")]
    public async Task Invalid_or_missing_file_name_fails_the_stage(string attributes)
    {
        var outcome = await _handler.StartAsync(Stage(attributes), default);

        Assert.IsType<StageOutcome.Failed>(outcome);
    }

    [Fact]
    public async Task Check_waits_until_both_file_and_signature_appear()
    {
        Write(_options.SignedDirectory, "doc.pdf", "signed");

        Assert.IsType<StageOutcome.Waiting>(await _handler.CheckAsync(Stage(), default));
        Assert.True(File.Exists(Path.Combine(_options.SignedDirectory, "doc.pdf")));
    }

    [Fact]
    public async Task Check_moves_file_and_signature_to_final_directory_and_returns_signature()
    {
        Write(_options.SignedDirectory, "doc.pdf", "signed");
        Write(_options.SignedDirectory, "doc.pdf.sig", "signature");

        var outcome = await _handler.CheckAsync(Stage(), default);

        var done = Assert.IsType<StageOutcome.Done>(outcome);
        Assert.Equal("doc.pdf.sig", done.File!.FileRef);
        Assert.Equal("signature"u8.ToArray(), done.File.Content);
        Assert.Empty(Directory.GetFiles(_options.SignedDirectory));
        Assert.Equal("signed", await File.ReadAllTextAsync(Path.Combine(_options.FinalDirectory, "doc.pdf")));
        Assert.True(File.Exists(Path.Combine(_options.FinalDirectory, "doc.pdf.sig")));
    }

    [Fact]
    public async Task Check_after_partial_move_completes_the_move()
    {
        Write(_options.FinalDirectory, "doc.pdf", "signed");
        Write(_options.SignedDirectory, "doc.pdf.sig", "signature");

        var done = Assert.IsType<StageOutcome.Done>(await _handler.CheckAsync(Stage(), default));

        Assert.Equal("signature"u8.ToArray(), done.File!.Content);
        Assert.Empty(Directory.GetFiles(_options.SignedDirectory));
    }

    [Fact]
    public async Task Check_does_not_touch_a_file_that_is_still_being_written()
    {
        Write(_options.SignedDirectory, "doc.pdf", "signed");
        await using var writer = new FileStream(
            Path.Combine(_options.SignedDirectory, "doc.pdf.sig"), FileMode.Create, FileAccess.Write, FileShare.None);

        Assert.IsType<StageOutcome.Waiting>(await _handler.CheckAsync(Stage(), default));
        Assert.True(File.Exists(Path.Combine(_options.SignedDirectory, "doc.pdf")));
    }

    [Fact]
    public void Options_require_distinct_absolute_directories()
    {
        var options = new FileSigningOptions
        {
            SourceDirectory = _options.SourceDirectory,
            SigningDirectory = _options.SourceDirectory,
            SignedDirectory = "relative",
            FinalDirectory = _options.FinalDirectory,
        };

        Assert.Contains(options.Validate(), e => e.StartsWith("SignedDirectory must be an absolute path"));
        options.SignedDirectory = _options.SignedDirectory;
        Assert.Contains("Source, signing, signed and final directories must be different.", options.Validate());
    }

    private static StageContext Stage(string attributes = """{"file_name":"doc.pdf"}""") =>
        new(1, 2, "sign", "req-1",
            new DocumentContext("doc-1", "document", attributes, null),
            new FileSigningStageSettings(),
            null);

    private static void Write(string directory, string name, string content)
    {
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, name), content);
    }
}
