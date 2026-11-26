using System.Text;
using System.Text.Json;
using Invest.Sign.Router.Core.Abstractions;
using Invest.Sign.Router.Core.Model;
using Invest.Sign.Router.Infrastructure.Stages;

namespace Invest.Sign.Router.IntegrationTests.Stages;

/// <summary>Тесты файловой системы; Docker не нужен. Каждый тест работает в собственном временном каталоге.</summary>
public sealed class CopyFileStageHandlerTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "sgn-copy-" + Guid.NewGuid().ToString("N"));
    private readonly string _source;
    private readonly string _target;
    private readonly CopyFileStageHandler _handler;

    public CopyFileStageHandlerTests()
    {
        _source = Path.Combine(_base, "in");
        _target = Path.Combine(_base, "out");
        Directory.CreateDirectory(_source);
        _handler = new CopyFileStageHandler(new CopyFileOptions { SourceRoot = _source, TargetRoot = _target });
    }

    public void Dispose() => Directory.Delete(_base, recursive: true);

    [Fact]
    public async Task Copies_file_into_target_folder_and_completes()
    {
        WriteSource("2026/contract.pdf", "content");

        var outcome = await _handler.StartAsync(Stage("""{"file_ref":"2026/contract.pdf"}"""), default);

        var done = Assert.IsType<StageOutcome.Done>(outcome);
        var copied = Path.Combine(_target, "manual", "doc-1_contract.pdf");
        Assert.Equal("content", await File.ReadAllTextAsync(copied));
        using var data = JsonDocument.Parse(done.Data!);
        Assert.Equal("2026/contract.pdf", data.RootElement.GetProperty("source").GetString());
        Assert.Equal("manual/doc-1_contract.pdf", data.RootElement.GetProperty("target").GetString());
        Assert.Single(Directory.GetFiles(Path.Combine(_target, "manual")));
    }

    [Fact]
    public async Task Repeated_start_overwrites_with_the_same_result()
    {
        WriteSource("contract.pdf", "v1");
        var stage = Stage("""{"file_ref":"contract.pdf"}""");
        await _handler.StartAsync(stage, default);
        WriteSource("contract.pdf", "v2");

        await _handler.StartAsync(stage, default);

        Assert.Equal("v2", await File.ReadAllTextAsync(Path.Combine(_target, "manual", "doc-1_contract.pdf")));
        Assert.Single(Directory.GetFiles(Path.Combine(_target, "manual")));
    }

    [Fact]
    public async Task Applies_file_name_template()
    {
        WriteSource("contract.pdf", "content");
        var stage = Stage("""{"file_ref":"contract.pdf"}""", new CopyFileStageSettings("manual", FileName: "{requestId}.pdf"));

        await _handler.StartAsync(stage, default);

        Assert.True(File.Exists(Path.Combine(_target, "manual", "req-1.pdf")));
    }

    [Fact]
    public async Task Missing_attribute_is_a_business_failure()
    {
        var outcome = await _handler.StartAsync(Stage("{}"), default);

        Assert.Contains("file_ref", Assert.IsType<StageOutcome.Failed>(outcome).Reason);
    }

    [Fact]
    public async Task Source_outside_root_is_a_business_failure()
    {
        var outcome = await _handler.StartAsync(Stage("""{"file_ref":"../secret.txt"}"""), default);

        Assert.Contains("outside the share root", Assert.IsType<StageOutcome.Failed>(outcome).Reason);
    }

    [Fact]
    public async Task Missing_source_file_is_a_technical_error_and_leaves_no_files()
    {
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            _handler.StartAsync(Stage("""{"file_ref":"absent.pdf"}"""), default));

        var folder = Path.Combine(_target, "manual");
        Assert.False(Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any());
    }

    [Theory]
    [InlineData("../elsewhere")]
    [InlineData("/absolute")]
    public void Rejects_target_folder_outside_root(string folder)
    {
        Assert.NotEmpty(_handler.ValidateSettings(new CopyFileStageSettings(folder)));
    }

    [Fact]
    public void Accepts_valid_settings_and_rejects_foreign_ones()
    {
        Assert.Empty(_handler.ValidateSettings(new CopyFileStageSettings("manual")));
        Assert.NotEmpty(_handler.ValidateSettings("manual"));
    }

    private static StageContext Stage(string attributes, CopyFileStageSettings? settings = null) =>
        new(1, 2, "manual-copy", "req-1",
            new DocumentContext("doc-1", "contract", attributes, null),
            settings ?? new CopyFileStageSettings("manual"),
            null);

    private void WriteSource(string relative, string content)
    {
        var path = Path.Combine(_source, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, Encoding.UTF8);
    }
}
