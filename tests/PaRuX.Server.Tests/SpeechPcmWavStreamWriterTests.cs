using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
using PaRuX.Contracts;
using PaRuX.Services;
using Xunit;

namespace PaRuX.Server.Tests;

public sealed class SpeechPcmWavStreamWriterTests
{
    [Fact]
    public async Task Writes_valid_wav_from_multiple_incremental_pcm_frames()
    {
        string directory = CreateTempDirectory();
        try
        {
            string path = Path.Combine(directory, "speech.wav");
            byte[] pcmA = { 0x01, 0x00, 0xFE, 0xFF };
            byte[] pcmB = { 0x10, 0x00, 0x20, 0x00 };
            byte[] allPcm = pcmA.Concat(pcmB).ToArray();
            long reportedBytes = 0;

            SpeechPcmWavStreamResult result = await SpeechPcmWavStreamWriter.WriteAsync(
                Frames(Start(), Audio(pcmA, 2), Audio(pcmB, 2), Done(0.5)),
                path,
                CancellationToken.None,
                new InlineProgress<long>(value => reportedBytes = value));

            byte[] wav = await File.ReadAllBytesAsync(path);
            Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
            Assert.Equal("WAVE", Encoding.ASCII.GetString(wav, 8, 4));
            Assert.Equal((uint)(wav.Length - 8), BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(4, 4)));
            Assert.Equal((uint)16, BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(16, 4)));
            Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(20, 2)));
            Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(22, 2)));
            Assert.Equal((uint)24_000, BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(24, 4)));
            Assert.Equal((uint)48_000, BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(28, 4)));
            Assert.Equal((ushort)2, BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(32, 2)));
            Assert.Equal((ushort)16, BinaryPrimitives.ReadUInt16LittleEndian(wav.AsSpan(34, 2)));
            Assert.Equal("data", Encoding.ASCII.GetString(wav, 36, 4));
            Assert.Equal((uint)allPcm.Length, BinaryPrimitives.ReadUInt32LittleEndian(wav.AsSpan(40, 4)));
            Assert.Equal(allPcm, wav.AsSpan(44).ToArray());
            Assert.Equal(allPcm.Length, reportedBytes);
            Assert.Equal(allPcm.Length, result.DataBytes);
            Assert.Equal(allPcm.Length + 44L, result.FileBytes);
            Assert.Equal(0.5, result.DurationSeconds);
        }
        finally { DeleteTempDirectory(directory); }
    }

    [Fact]
    public async Task Writes_audio_before_waiting_for_the_terminal_frame()
    {
        string directory = CreateTempDirectory();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var audioWritten = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowDone = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        string path = Path.Combine(directory, "speech.wav");

        async IAsyncEnumerable<SpeechStreamFrame> GatedFrames([EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return Start();
            yield return Audio(new byte[] { 0x01, 0x00 }, 1);
            await allowDone.Task.WaitAsync(ct);
            yield return Done(1d / 24_000);
        }

        try
        {
            Task<SpeechPcmWavStreamResult> write = SpeechPcmWavStreamWriter.WriteAsync(
                GatedFrames(cancellation.Token),
                path,
                cancellation.Token,
                new InlineProgress<long>(_ => audioWritten.TrySetResult(true)));

            await audioWritten.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.False(write.IsCompleted);
            allowDone.TrySetResult(true);
            await write.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(File.Exists(path));
        }
        finally
        {
            allowDone.TrySetResult(true);
            DeleteTempDirectory(directory);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Error_or_truncated_stream_never_exposes_a_partial_wav(bool emitError)
    {
        string directory = CreateTempDirectory();
        string path = Path.Combine(directory, "speech.wav");
        try
        {
            IAsyncEnumerable<SpeechStreamFrame> frames = emitError
                ? Frames(Start(), Audio(new byte[] { 0x01, 0x00 }, 1), Error("worker failed"))
                : Frames(Start(), Audio(new byte[] { 0x01, 0x00 }, 1));

            await Assert.ThrowsAsync<InvalidDataException>(() => SpeechPcmWavStreamWriter.WriteAsync(
                frames,
                path,
                CancellationToken.None));

            Assert.False(File.Exists(path));
            Assert.Empty(Directory.GetFiles(directory, "*.partial"));
        }
        finally { DeleteTempDirectory(directory); }
    }

    [Fact]
    public async Task Empty_success_frame_is_rejected_without_an_artifact()
    {
        string directory = CreateTempDirectory();
        string path = Path.Combine(directory, "speech.wav");
        try
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => SpeechPcmWavStreamWriter.WriteAsync(
                Frames(Start(), Done(0)),
                path,
                CancellationToken.None));

            Assert.False(File.Exists(path));
            Assert.Empty(Directory.GetFiles(directory, "*.partial"));
        }
        finally { DeleteTempDirectory(directory); }
    }

    [Fact]
    public async Task Cancellation_removes_partial_output_and_propagates()
    {
        string directory = CreateTempDirectory();
        string path = Path.Combine(directory, "speech.wav");
        using var cancellation = new CancellationTokenSource();
        var audioWritten = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        async IAsyncEnumerable<SpeechStreamFrame> NeverCompletes([EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return Start();
            yield return Audio(new byte[] { 0x01, 0x00 }, 1);
            audioWritten.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, ct);
        }

        try
        {
            Task<SpeechPcmWavStreamResult> write = SpeechPcmWavStreamWriter.WriteAsync(
                NeverCompletes(cancellation.Token),
                path,
                cancellation.Token);
            await audioWritten.Task.WaitAsync(TimeSpan.FromSeconds(3));
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => write);
            Assert.False(File.Exists(path));
            Assert.Empty(Directory.GetFiles(directory, "*.partial"));
        }
        finally { DeleteTempDirectory(directory); }
    }

    private static SpeechStreamFrame Start() => new() { Type = "start", RequestId = "test", Sequence = 0 };

    private static SpeechStreamFrame Audio(byte[] pcm, int samples) => new()
    {
        Type = "audio",
        RequestId = "test",
        Sequence = 1,
        Base64Data = Convert.ToBase64String(pcm),
        Samples = samples
    };

    private static SpeechStreamFrame Done(double duration) => new()
    {
        Type = "done",
        RequestId = "test",
        Sequence = 2,
        DurationSeconds = duration
    };

    private static SpeechStreamFrame Error(string detail) => new()
    {
        Type = "error",
        RequestId = "test",
        Sequence = 2,
        Error = detail
    };

    private static async IAsyncEnumerable<SpeechStreamFrame> Frames(params SpeechStreamFrame[] frames)
    {
        foreach (SpeechStreamFrame frame in frames) yield return frame;
        await Task.CompletedTask;
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "parux-speech-stream-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTempDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
