using System;
using System.Threading;

namespace AgentBridge.Desktop.Services;

public sealed class SingleInstanceGuard : IDisposable
{
    private const string MutexName = "Local\\AgentBridge.Desktop.Singleton.v1";
    private const string ActivationEventName = "Local\\AgentBridge.Desktop.Activate.v1";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activationEvent;
    private readonly RegisteredWaitHandle _activationRegistration;
    private bool _ownsMutex;

    private SingleInstanceGuard(Mutex mutex, bool ownsMutex, Action activationRequested)
    {
        _mutex = mutex;
        _ownsMutex = ownsMutex;
        _activationEvent = new EventWaitHandle(
            initialState: false,
            EventResetMode.AutoReset,
            ActivationEventName);
        _activationRegistration = ThreadPool.RegisterWaitForSingleObject(
            _activationEvent,
            static (state, timedOut) =>
            {
                if (!timedOut && state is Action callback)
                    callback();
            },
            activationRequested,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }

    public static bool TryAcquire(Action activationRequested, out SingleInstanceGuard? guard)
    {
        ArgumentNullException.ThrowIfNull(activationRequested);

        bool ownsMutex;
        var mutex = new Mutex(initiallyOwned: false, MutexName);
        try
        {
            ownsMutex = mutex.WaitOne(TimeSpan.FromMilliseconds(300), false);
        }
        catch (AbandonedMutexException)
        {
            ownsMutex = true;
        }

        if (!ownsMutex)
        {
            mutex.Dispose();
            SignalExistingInstance();
            guard = null;
            return false;
        }

        guard = new SingleInstanceGuard(mutex, ownsMutex: true, activationRequested);
        return true;
    }

    private static void SignalExistingInstance()
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                using var activationEvent = EventWaitHandle.OpenExisting(ActivationEventName);
                activationEvent.Set();
                return;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                Thread.Sleep(50);
            }
        }
    }

    public void Dispose()
    {
        _activationRegistration.Unregister(null);
        _activationEvent.Dispose();

        if (_ownsMutex)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
            }

            _ownsMutex = false;
        }

        _mutex.Dispose();
    }
}
