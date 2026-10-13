using System.Threading.Channels;
using MouseMotionLab.Core.Domain;

namespace MouseMotionLab.Core.Data;

public sealed class DatasetWriteQueue : IAsyncDisposable
{
    private readonly DatasetRepository _repository;
    private readonly Channel<WriteCommand> _channel;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Task _worker;

    public DatasetWriteQueue(DatasetRepository repository, int capacity = 64)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 4);
        _repository = repository;
        _channel = Channel.CreateBounded<WriteCommand>(new BoundedChannelOptions(capacity)
        {
            SingleReader = true,
            SingleWriter = false,
            // TryWrite must return false when the queue is full. DropWrite reports
            // success even though it discards the new item, which would silently
            // lose a trial instead of letting the caller persist it as invalid.
            FullMode = BoundedChannelFullMode.Wait
        });
        _worker = Task.Run(ProcessAsync);
    }

    public event EventHandler<Exception>? WriteFailed;

    public bool TryEnqueue(TrialCapture trial) => _channel.Writer.TryWrite(new SaveTrial(trial));

    public bool TryExclude(Guid trialId, string reason = "excluded_by_user") =>
        _channel.Writer.TryWrite(new UpdateStatus(trialId, TrialStatus.Excluded, reason));

    public ValueTask EnqueueExcludeAsync(
        Guid trialId,
        string reason = "excluded_by_user",
        CancellationToken cancellationToken = default) =>
        _channel.Writer.WriteAsync(
            new UpdateStatus(trialId, TrialStatus.Excluded, reason),
            cancellationToken);

    public async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await _channel.Writer.WriteAsync(new Flush(completion), cancellationToken).ConfigureAwait(false);
        await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        _channel.Writer.TryComplete();
        try
        {
            await _worker.ConfigureAwait(false);
        }
        finally
        {
            _cancellation.Cancel();
            _cancellation.Dispose();
        }
    }

    private async Task ProcessAsync()
    {
        try
        {
            await foreach (var command in _channel.Reader.ReadAllAsync(_cancellation.Token).ConfigureAwait(false))
            {
                try
                {
                    switch (command)
                    {
                        case SaveTrial save:
                            await _repository.SaveTrialAsync(save.Trial, _cancellation.Token).ConfigureAwait(false);
                            break;
                        case Flush flush:
                            flush.Completion.SetResult();
                            break;
                        case UpdateStatus update:
                            await _repository.SetTrialStatusAsync(
                                    update.TrialId,
                                    update.Status,
                                    update.Reason,
                                    _cancellation.Token)
                                .ConfigureAwait(false);
                            break;
                    }
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    WriteFailed?.Invoke(this, exception);
                }
            }
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
            // Expected during disposal.
        }
    }

    private abstract record WriteCommand;

    private sealed record SaveTrial(TrialCapture Trial) : WriteCommand;

    private sealed record Flush(TaskCompletionSource Completion) : WriteCommand;

    private sealed record UpdateStatus(Guid TrialId, TrialStatus Status, string Reason) : WriteCommand;
}
