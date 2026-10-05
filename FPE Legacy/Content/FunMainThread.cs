using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FPE_Legacy.Content
{
    internal static class FunMainThread
    {
        private sealed class Waiter { internal Func<bool> Poll; internal TaskCompletionSource<bool> Completion = new(); }
        private static readonly List<Waiter> Waiters = new();
        private static int _thread;
        internal static void Initialize() { _thread = Thread.CurrentThread.ManagedThreadId; }
        internal static void Require()
        {
            if (_thread == 0 || Thread.CurrentThread.ManagedThreadId != _thread)
                throw new InvalidOperationException("FunContent/FunNetwork must be called on Unity's main thread. Do not use Task.Run or ConfigureAwait(false).");
        }
        internal static Task Until(Func<bool> poll)
        {
            Require();
            if (poll()) return Task.CompletedTask;
            var waiter = new Waiter { Poll = poll }; Waiters.Add(waiter); return waiter.Completion.Task;
        }
        internal static Task NextFrame()
        {
            Require();
            var waiter = new Waiter { Poll = () => true }; Waiters.Add(waiter); return waiter.Completion.Task;
        }
        internal static void Tick()
        {
            foreach (var waiter in Waiters.ToArray())
            {
                try { if (!waiter.Poll()) continue; Waiters.Remove(waiter); waiter.Completion.TrySetResult(true); }
                catch (Exception e) { Waiters.Remove(waiter); waiter.Completion.TrySetException(e); }
            }
        }
    }
}
