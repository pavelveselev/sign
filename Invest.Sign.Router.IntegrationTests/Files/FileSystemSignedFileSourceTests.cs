using System.Text;
using Invest.Sign.Router.Infrastructure.Files;

namespace Invest.Sign.Router.IntegrationTests.Files;

/// <summary>Тесты файловой системы; Docker не нужен. Каждый тест работает в собственном временном каталоге.</summary>
public sealed class FileSystemSignedFileSourceTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "sgn-files-" + Guid.NewGuid().ToString("N"));
    private readonly string _root;
    private readonly FileSystemSignedFileSource _source;

    public FileSystemSignedFileSourceTests()
    {
        _root = Path.Combine(_base, "root");
        Directory.CreateDirectory(_root);
        _source = new FileSystemSignedFileSource(new SignedFilesOptions { Root = _root });
    }

    public void Dispose() => Directory.Delete(_base, recursive: true);

    [Fact]
    public async Task Reads_file_by_relative_reference()
    {
        var content = Write("out/2026/a.pdf", "signed");

        Assert.Equal(content, await _source.ReadAsync("out/2026/a.pdf", default));
    }

    [Fact]
    public async Task Accepts_backslash_separators()
    {
        var content = Write("out/a.pdf", "signed");

        Assert.Equal(content, await _source.ReadAsync(@"out\a.pdf", default));
    }

    [Fact]
    public async Task Reads_large_file_completely()
    {
        var content = Enumerable.Range(0, 5_000_000).Select(i => (byte)i).ToArray();
        File.WriteAllBytes(Path.Combine(_root, "big.bin"), content);

        Assert.Equal(content, await _source.ReadAsync("big.bin", default));
    }

    [Fact]
    public async Task Dot_dot_segments_that_stay_inside_root_are_allowed()
    {
        var content = Write("b.pdf", "signed");

        Assert.Equal(content, await _source.ReadAsync("a/../b.pdf", default));
    }

    [Fact]
    public async Task Missing_file_throws_file_not_found()
    {
        Directory.CreateDirectory(Path.Combine(_root, "out"));

        await Assert.ThrowsAsync<FileNotFoundException>(() => _source.ReadAsync("out/missing.pdf", default));
    }

    [Fact]
    public async Task Missing_directory_throws_directory_not_found()
    {
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => _source.ReadAsync("no-such-dir/a.pdf", default));
    }

    [Fact]
    public async Task Unavailable_root_is_an_io_error_not_a_startup_error()
    {
        var source = new FileSystemSignedFileSource(new SignedFilesOptions { Root = Path.Combine(_base, "not-mounted") });

        await Assert.ThrowsAnyAsync<IOException>(() => source.ReadAsync("a.pdf", default));
    }

    [Fact]
    public async Task File_held_exclusively_by_writer_throws_io_exception()
    {
        Write("busy.pdf", "partial");
        await using var writer = new FileStream(
            Path.Combine(_root, "busy.pdf"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        await Assert.ThrowsAsync<IOException>(() => _source.ReadAsync("busy.pdf", default));
    }

    [Theory]
    [InlineData("../outside.pdf")]
    [InlineData("a/../../outside.pdf")]
    [InlineData(@"..\outside.pdf")]
    [InlineData("../root-evil/x.pdf")]    // соседний каталог с тем же префиксом имени, что и корень
    [InlineData("..")]
    [InlineData(".")]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Refuses_references_outside_root(string fileRef)
    {
        Write("../outside.pdf", "secret");
        Write("../root-evil/x.pdf", "secret");

        var ex = await Assert.ThrowsAsync<InvalidFileRefException>(() => _source.ReadAsync(fileRef, default));
        Assert.Equal(fileRef, ex.FileRef);
    }

    [Fact]
    public async Task Refuses_absolute_reference()
    {
        Write("../outside.pdf", "secret");
        var absolute = Path.Combine(_base, "outside.pdf");

        await Assert.ThrowsAsync<InvalidFileRefException>(() => _source.ReadAsync(absolute, default));
        await Assert.ThrowsAsync<InvalidFileRefException>(() => _source.ReadAsync("/outside.pdf", default));
    }

    [Fact]
    public async Task Refuses_absolute_reference_even_when_it_points_inside_root()
    {
        Write("a.pdf", "signed");

        await Assert.ThrowsAsync<InvalidFileRefException>(
            () => _source.ReadAsync(Path.Combine(_root, "a.pdf"), default));
    }

    [Fact]
    public async Task Slow_read_honouring_cancellation_times_out()
    {
        var source = new FileSystemSignedFileSource(
            new SignedFilesOptions { Root = _root, ReadTimeout = TimeSpan.FromMilliseconds(200) },
            async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return [];
            });

        await Assert.ThrowsAsync<TimeoutException>(() => source.ReadAsync("a.pdf", default));
    }

    [Fact]
    public async Task Hung_read_ignoring_cancellation_still_times_out()
    {
        var release = new TaskCompletionSource<byte[]>();
        var source = new FileSystemSignedFileSource(
            new SignedFilesOptions { Root = _root, ReadTimeout = TimeSpan.FromMilliseconds(200) },
            (_, _) => release.Task);

        await Assert.ThrowsAsync<TimeoutException>(() => source.ReadAsync("a.pdf", default));
        release.SetResult([]);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_reported_as_timeout()
    {
        using var cts = new CancellationTokenSource();
        var source = new FileSystemSignedFileSource(
            new SignedFilesOptions { Root = _root },
            async (_, ct) =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return [];
            });

        var read = source.ReadAsync("a.pdf", cts.Token);
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read);
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative/root")]
    public void Rejects_root_that_is_not_absolute(string root)
    {
        Assert.Throws<ArgumentException>(() => new FileSystemSignedFileSource(new SignedFilesOptions { Root = root }));
    }

    [Fact]
    public void Rejects_non_positive_timeout()
    {
        Assert.Throws<ArgumentException>(() => new FileSystemSignedFileSource(
            new SignedFilesOptions { Root = _root, ReadTimeout = TimeSpan.Zero }));
    }

    private byte[] Write(string relative, string text)
    {
        var path = Path.GetFullPath(Path.Combine(_root, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var content = Encoding.UTF8.GetBytes(text);
        File.WriteAllBytes(path, content);
        return content;
    }
}
