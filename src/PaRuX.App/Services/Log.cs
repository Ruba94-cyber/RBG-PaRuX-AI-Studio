using System;
using System.IO;

namespace PaRuX.Services
{
    /// <summary>Deliberately tiny append-only diagnostic log.</summary>
    public static class Log
    {
        private static readonly object Gate = new object();
        private const long MaximumBytes = 5L * 1024L * 1024L;
        private const int RetainedFiles = 5;

        public static void Write(string message)
        {
            try
            {
                AppPaths.EnsureCreated();
                lock (Gate)
                {
                    RotateIfNeeded();
                    File.AppendAllText(
                        AppPaths.LogFile,
                        DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + message + Environment.NewLine);
                }
            }
            catch
            {
                // Logging must never take the app down.
            }
        }

        private static void RotateIfNeeded()
        {
            if (!File.Exists(AppPaths.LogFile) || new FileInfo(AppPaths.LogFile).Length < MaximumBytes) return;
            string oldest = AppPaths.LogFile + "." + RetainedFiles;
            if (File.Exists(oldest)) File.Delete(oldest);
            for (int i = RetainedFiles - 1; i >= 1; i--)
            {
                string source = AppPaths.LogFile + "." + i;
                if (File.Exists(source)) File.Move(source, AppPaths.LogFile + "." + (i + 1));
            }
            File.Move(AppPaths.LogFile, AppPaths.LogFile + ".1");
        }
    }
}
