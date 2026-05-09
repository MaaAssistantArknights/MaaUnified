using MAAUnified.Application.Models;

namespace MAAUnified.Application.Services;

public sealed class UiLogService
{
    private const int DefaultBufferCapacity = 2000;
    private readonly List<UiLogMessage> _buffer = [];
    private readonly Queue<UiLogMessage> _pendingDispatch = [];
    private readonly object _gate = new();
    private bool _verboseEnabled;
    private int _bufferCapacity = DefaultBufferCapacity;
    private int _dispatchBatchSize;
    private int _dispatchScheduled;

    public event Action<UiLogMessage>? LogReceived;

    public IReadOnlyList<UiLogMessage> Snapshot
    {
        get
        {
            lock (_gate)
            {
                return _buffer.ToArray();
            }
        }
    }

    public bool VerboseEnabled => _verboseEnabled;

    public int BufferCapacity
    {
        get
        {
            lock (_gate)
            {
                return _bufferCapacity;
            }
        }
        set
        {
            lock (_gate)
            {
                _bufferCapacity = Math.Max(50, value);
                TrimBufferUnderLock();
            }
        }
    }

    public int DispatchBatchSize
    {
        get
        {
            lock (_gate)
            {
                return _dispatchBatchSize;
            }
        }
        set
        {
            lock (_gate)
            {
                _dispatchBatchSize = Math.Max(0, value);
            }
        }
    }

    public void SetVerboseEnabled(bool enabled)
    {
        enabled = global::MAAUnified.Platform.MaaUnifiedBuildFlavor.ExposesDeveloperTools && enabled;
        if (_verboseEnabled == enabled)
        {
            return;
        }

        _verboseEnabled = enabled;
        Push("INFO", enabled ? "Developer mode enabled: verbose diagnostics active." : "Developer mode disabled: verbose diagnostics inactive.");
    }

    public void Info(string message) => Push("INFO", message);

    public void Debug(string message)
    {
        if (_verboseEnabled)
        {
            Push("DEBUG", message);
        }
    }

    public void Warn(string message) => Push("WARN", message);

    public void Error(string message) => Push("ERROR", message);

    private void Push(string level, string message)
    {
        var log = new UiLogMessage(DateTimeOffset.UtcNow, level, message);
        Action<UiLogMessage>? handlers;
        bool shouldScheduleBatchDispatch;
        var shouldDispatchImmediately = false;

        lock (_gate)
        {
            _buffer.Add(log);
            TrimBufferUnderLock();

            if (_dispatchBatchSize <= 0)
            {
                shouldDispatchImmediately = true;
                handlers = LogReceived;
                shouldScheduleBatchDispatch = false;
            }
            else
            {
                _pendingDispatch.Enqueue(log);
                handlers = null;
                shouldScheduleBatchDispatch = _dispatchScheduled == 0;
                if (shouldScheduleBatchDispatch)
                {
                    _dispatchScheduled = 1;
                }
            }
        }

        if (shouldDispatchImmediately)
        {
            handlers?.Invoke(log);
            return;
        }

        if (shouldScheduleBatchDispatch)
        {
            _ = Task.Run(DispatchBatchedLogsAsync);
        }
    }

    private Task DispatchBatchedLogsAsync()
    {
        var shouldScheduleAnother = false;
        try
        {
            while (true)
            {
                UiLogMessage[] batch;
                Action<UiLogMessage>? handlers;

                lock (_gate)
                {
                    if (_pendingDispatch.Count == 0)
                    {
                        _dispatchScheduled = 0;
                        break;
                    }

                    var count = _dispatchBatchSize <= 0
                        ? _pendingDispatch.Count
                        : Math.Min(_dispatchBatchSize, _pendingDispatch.Count);
                    batch = new UiLogMessage[count];
                    for (var i = 0; i < count; i++)
                    {
                        batch[i] = _pendingDispatch.Dequeue();
                    }

                    handlers = LogReceived;
                }

                if (handlers is null)
                {
                    continue;
                }

                foreach (var item in batch)
                {
                    handlers(item);
                }
            }
        }
        finally
        {
            lock (_gate)
            {
                shouldScheduleAnother = _dispatchScheduled != 0 && _pendingDispatch.Count > 0;
            }
        }

        if (shouldScheduleAnother)
        {
            _ = Task.Run(DispatchBatchedLogsAsync);
        }

        return Task.CompletedTask;
    }

    private void TrimBufferUnderLock()
    {
        if (_buffer.Count <= _bufferCapacity)
        {
            return;
        }

        var removeCount = _buffer.Count - _bufferCapacity;
        _buffer.RemoveRange(0, removeCount);
    }
}
