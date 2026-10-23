using System;
using System.Collections.Generic;
using System.Diagnostics;
using OpenAxis.Navigation;

namespace Rotatrix.Unity.Editor
{
    internal sealed class EditorScheduler : INavigationScheduler, IDisposable
    {
        private readonly object gate = new object();
        private readonly List<(double deadline, Action action)> pending = new List<(double, Action)>();
        private bool disposed;
        internal static double Now => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

        public void Post(Action callback) => PostAt(0, callback);
        public void PostAt(double deadline, Action callback)
        {
            lock (gate) if (!disposed) pending.Add((deadline, callback));
        }

        // Called only from EditorApplication.update. Snapshotting guarantees that
        // callbacks posted by a callback are never executed inline or recursively.
        internal void Drain()
        {
            var ready = new List<Action>();
            lock (gate)
            {
                var now = Now;
                for (var i = 0; i < pending.Count && ready.Count < 64;)
                {
                    if (pending[i].deadline > now) { i++; continue; }
                    ready.Add(pending[i].action);
                    pending.RemoveAt(i);
                }
            }
            foreach (var action in ready) action();
        }

        public void Dispose() { lock (gate) { disposed = true; pending.Clear(); } }
    }
}
