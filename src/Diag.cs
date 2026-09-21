using System;
using System.Diagnostics;

namespace trutility
{
    /// <summary>
    /// Records failures that are deliberately swallowed.
    ///
    /// A single malformed policy root, an unreachable department share or a
    /// registry key denied by ACL must cost that one item rather than the whole
    /// tray menu, so these failures cannot be allowed to propagate. Recording
    /// them keeps that from being invisible: "my department folder is missing"
    /// is answerable from DebugView on the affected machine instead of needing a
    /// reproduction.
    ///
    /// Cost stays on the failure path. Nothing is formatted or written unless an
    /// exception has already been thrown, which is itself far more expensive than
    /// the trace write, and Trace.WriteLine is [Conditional("TRACE")] so both the
    /// call and its argument disappear entirely if the symbol is ever dropped.
    /// </summary>
    internal static class Diag
    {
        internal static void Swallowed(string what, Exception ex)
        {
            Trace.WriteLine("trutility: " + what + " failed: " + Describe(ex));
        }

        internal static void Swallowed(string what)
        {
            Trace.WriteLine("trutility: " + what + " failed");
        }

        /// Records something worth knowing that is not a failure.
        internal static void Note(string what)
        {
            Trace.WriteLine("trutility: " + what);
        }

        private static string Describe(Exception ex)
        {
            if (ex == null)
            {
                return "(no exception)";
            }
            return ex.GetType().Name + ": " + ex.Message;
        }
    }
}
