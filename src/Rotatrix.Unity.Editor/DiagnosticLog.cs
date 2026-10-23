using System;
using System.Collections.Generic;
using System.IO;
using OpenAxis.Diagnostics;

namespace Rotatrix.Unity.Editor
{
    // UI history is local; the SDK owns file naming, rotation and retention.
    internal sealed class DiagnosticLog : IDisposable
    {
        private readonly object gate = new object();
        private readonly Queue<string> recent = new Queue<string>();
        private readonly OpenAxis.Diagnostics.DiagnosticLog fileLog;
        internal string FilePath => fileLog.FilePath ?? "";
        internal string? Error => fileLog.Error;
        internal DiagnosticLog(string? path = null, long maxBytes = 5 * 1024 * 1024, string? clientVersion = null)
        {
            fileLog = path == null ? OpenAxis.Diagnostics.DiagnosticLog.Configure("unity", clientVersion: clientVersion)
                : new OpenAxis.Diagnostics.DiagnosticLog("unity", Path.GetDirectoryName(path), maxBytes, clientVersion: clientVersion);
            fileLog.Message += Remember;
        }
        private void Remember(string level, string message)
        {
            var line = DiagnosticFormatter.FormatLogLine(message, level == "error"
                ? DiagnosticLevel.Error : level == "warning" ? DiagnosticLevel.Warning : DiagnosticLevel.Info);
            lock (gate) { recent.Enqueue(line); while (recent.Count > 200) recent.Dequeue(); }
        }
        internal string Text { get { lock (gate) return string.Join("\n", recent); } }
        internal void Write(string level, string message) => fileLog.Write(level, message);
        public void Dispose() { fileLog.Message -= Remember; fileLog.Dispose(); }
    }
}
