using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;

namespace WinMemoryCleaner
{
    /// <summary>
    /// Logger
    /// </summary>
    public static class Logger
    {
        #region Fields

        private const long MaxLogFileSize = 1024 * 1024; // 1 MB

        private static SafeFileHandle _consoleHandle;
        private static FileStream _consoleStream;
        private static StreamWriter _consoleWriter;
        private static bool _fileDisabled;
        private static readonly object _fileLock = new object();
        private static Enums.Log.Levels _level = Enums.Log.Levels.Debug | Enums.Log.Levels.Information | Enums.Log.Levels.Warning | Enums.Log.Levels.Error;
        private static string _logFilePath;

        #endregion Fields

        #region Constructor

        /// <summary>
        /// Initializes the <see cref="Logger" /> class.
        /// </summary>
        static Logger()
        {
            try
            {
                Trace.AutoFlush = true;

                Trace.Listeners.Clear();
                Trace.Listeners.AddRange(new TraceListener[]
                {
                    new ConsoleTraceListener(false) { Name = Constants.App.Title },
                    new EventLogTraceListener(Constants.App.Title)
                });
            }
            catch
            {
                try
                {
                    if (Trace.Listeners.Count == 0)
                        Trace.Listeners.Add(new DefaultTraceListener() { Name = Constants.App.Title });
                }
                catch (Exception e)
                {
                    Event(e.GetMessage(), EventLogEntryType.Error);
                }
            }
        }

        /// <summary>
        /// Releases resources.
        /// Should be called during application shutdown if EnableConsoleOutput was called.
        /// </summary>
        internal static void Dispose()
        {
            if (_consoleWriter != null)
            {
                try
                {
                    _consoleWriter.Flush();
                }
                catch
                {
                    // ignored
                }

                try
                {
                    _consoleWriter.Close();
                }
                catch
                {
                    // ignored
                }

                _consoleWriter = null;
            }

            if (_consoleStream != null)
            {
                try
                {
                    _consoleStream.Close();
                }
                catch
                {
                    // ignored
                }

                _consoleStream = null;
            }

            if (_consoleHandle != null)
            {
                try
                {
                    _consoleHandle.Close();
                }
                catch
                {
                    // ignored
                }

                _consoleHandle = null;
            }
        }

        #endregion

        #region Properties

        /// <summary>
        /// Gets a value indicating whether console output is enabled.
        /// </summary>
        /// <value>
        ///   <c>true</c> if console output is enabled; otherwise, <c>false</c>.
        /// </value>
        private static bool IsConsoleEnabled { get { return _consoleWriter != null; } }

        private static bool IsDebugEnabled { get { return (_level & Enums.Log.Levels.Debug) != 0; } }

        private static bool IsErrorEnabled { get { return (_level & Enums.Log.Levels.Error) != 0; } }

        private static bool IsInformationEnabled { get { return (_level & Enums.Log.Levels.Information) != 0; } }

        private static bool IsWarningEnabled { get { return (_level & Enums.Log.Levels.Warning) != 0; } }

        /// <summary>
        /// Gets the log file path. Empty when the log file is not available.
        /// </summary>
        /// <value>
        /// The log file path.
        /// </value>
        public static string LogFilePath
        {
            get
            {
                lock (_fileLock)
                {
                    if (_logFilePath == null && !_fileDisabled)
                        _logFilePath = ResolveLogFilePath();

                    return _logFilePath ?? string.Empty;
                }
            }
        }

        /// <summary>
        /// Sets the log level.
        /// </summary>
        /// <value>
        /// The log level.
        /// </value>
        public static Enums.Log.Levels Level
        {
            get { return _level; }
            set
            {
                switch (value)
                {
                    case Enums.Log.Levels.Debug:
                        _level = Enums.Log.Levels.Debug | Enums.Log.Levels.Information | Enums.Log.Levels.Warning | Enums.Log.Levels.Error;
                        break;

                    case Enums.Log.Levels.Information:
                        _level = Enums.Log.Levels.Information | Enums.Log.Levels.Warning | Enums.Log.Levels.Error;
                        break;

                    case Enums.Log.Levels.Warning:
                        _level = Enums.Log.Levels.Warning | Enums.Log.Levels.Error;
                        break;

                    case Enums.Log.Levels.Error:
                        _level = Enums.Log.Levels.Error;
                        break;
                }
            }
        }

        #endregion Properties

        #region Methods

        /// <summary>
        /// Debug
        /// </summary>
        /// <param name="exception">Exception</param>
        /// <param name="message">Custom message about the Exception</param>
        /// <param name="method">Method</param>
        public static void Debug(Exception exception, string message = null, [CallerMemberName] string method = null)
        {
            Exception(Enums.Log.Levels.Debug, exception, message, method);
        }

        /// <summary>
        /// Debug
        /// </summary>
        /// <param name="message">Message</param>
        /// <param name="method">Method</param>
        public static void Debug(string message, [CallerMemberName] string method = null)
        {
            Log(new Log(Enums.Log.Levels.Debug, message, null, method));
        }

        /// <summary>
        /// Enables console output by attaching to the parent console process and redirecting standard output.
        /// This allows Console.WriteLine output to appear in the calling terminal (PowerShell/cmd).
        /// Typically called when the application is invoked with command-line arguments.
        /// This method is idempotent - calling it multiple times has no additional effect.
        /// </summary>
        public static void EnableConsoleOutput()
        {
            try
            {
                if (IsConsoleEnabled)
                    return;

                if (NativeMethods.AttachConsole(Constants.Windows.Console.AttachParentProcess))
                {
                    var stdHandle = NativeMethods.GetStdHandle(Constants.Windows.Console.StdOutputHandle);

                    if (stdHandle != IntPtr.Zero && stdHandle != new IntPtr(-1))
                    {
                        _consoleHandle = new SafeFileHandle(stdHandle, false);
                        _consoleStream = new FileStream(_consoleHandle, FileAccess.Write);
                        _consoleWriter = new StreamWriter(_consoleStream, Encoding.Default) { AutoFlush = true };

                        Console.SetOut(_consoleWriter);

                        Trace.Listeners.Clear();
                    }
                }
            }
            catch
            {
                // ignored
            }
        }

        /// <summary>
        /// Error
        /// </summary>
        /// <param name="exception">Exception</param>
        /// <param name="message">Custom message about the Exception</param>
        /// <param name="method">Method</param>
        public static void Error(Exception exception, string message = null, [CallerMemberName] string method = null)
        {
            Exception(Enums.Log.Levels.Error, exception, message, method);
        }

        /// <summary>
        /// Error
        /// </summary>
        /// <param name="message">Message</param>
        /// <param name="method">Method</param>
        public static void Error(string message, [CallerMemberName] string method = null)
        {
            Log(new Log(Enums.Log.Levels.Error, message, null, method));
        }

        /// <summary>
        /// Windows Event
        /// </summary>
        /// <param name="message">Message</param>
        /// <param name="type">Type</param>
        private static void Event(string message, EventLogEntryType type = EventLogEntryType.Information)
        {
            try
            {
                EventLog.WriteEntry(Constants.App.Title, message, type);
            }
            catch
            {
                // ignored
            }
        }

        /// <summary>
        /// Exception
        /// </summary>
        /// <param name="level">The level.</param>
        /// <param name="exception">The exception.</param>
        /// <param name="message">The message.</param>
        /// <param name="method">The method.</param>
        private static void Exception(Enums.Log.Levels level, Exception exception, string message = null, [CallerMemberName] string method = null)
        {
            if (IsDebugEnabled)
            {
                try
                {
                    var stackTrace = new StackTrace(exception, true);
                    if (stackTrace != null && stackTrace.FrameCount > 0)
                    {
                        var frame = stackTrace.GetFrame(stackTrace.FrameCount - 1);

                        if (frame != null)
                        {
                            var methodBase = frame.GetMethod();

                            if (methodBase != null && methodBase.DeclaringType != null)
                                method = string.Format(Localizer.Culture, "{0}.{1}", methodBase.DeclaringType.Name, methodBase.Name);
                        }
                    }
                }
                catch
                {
                    // ignored
                }
            }

            if (string.IsNullOrWhiteSpace(message) && exception != null)
                message = exception.GetMessage();

            Log(new Log(level, message, null, method));
        }

        /// <summary>
        /// Information
        /// </summary>
        /// <param name="message">Message</param>
        /// <param name="method">Method</param>
        public static void Information(string message, [CallerMemberName] string method = null)
        {
            Log(new Log(Enums.Log.Levels.Information, message, null, method));
        }

        /// <summary>
        /// Log
        /// </summary>
        /// <param name="log">The log.</param>
        public static void Log(Log log)
        {
            try
            {
                if (log == null)
                    throw new ArgumentNullException("log");

                var message = log.Message;

                // Write plain message to console if enabled (user-friendly)
                if (IsConsoleEnabled && !string.IsNullOrEmpty(message))
                {
                    try
                    {
                        Console.WriteLine(message);
                    }
                    catch
                    {
                        // ignored
                    }
                }

                // Write structured JSON to Trace listeners (EventLog, etc.)
                message = Helper.Serialize(log);

                var enabled = false;

                switch (log.Level)
                {
                    case Enums.Log.Levels.Debug:
                        enabled = IsDebugEnabled;
                        break;

                    case Enums.Log.Levels.Information:
                        enabled = IsInformationEnabled;
                        break;

                    case Enums.Log.Levels.Warning:
                        enabled = IsWarningEnabled;
                        break;

                    case Enums.Log.Levels.Error:
                        enabled = IsErrorEnabled;
                        break;
                }

                if (!enabled)
                    return;

                switch (log.Level)
                {
                    case Enums.Log.Levels.Debug:
                        Trace.WriteLine(message);
                        break;

                    case Enums.Log.Levels.Information:
                        Trace.TraceInformation(message);
                        break;

                    case Enums.Log.Levels.Warning:
                        Trace.TraceWarning(message);
                        break;

                    case Enums.Log.Levels.Error:
                        Trace.TraceError(message);
                        break;
                }

                // One JSON object per line makes the file easy to copy and parse
                WriteLogFile(Helper.Serialize(log, true));
            }
            catch (Exception e)
            {
                Event(string.Format(Localizer.Culture, Localizer.String.ErrorCanNotSaveLog, log != null ? log.Message : "NULL", e.GetMessage()), EventLogEntryType.Error);
            }
        }

        /// <summary>
        /// Gets the log file candidates, in order of preference
        /// </summary>
        /// <returns>The log file candidates</returns>
        private static IEnumerable<string> GetLogFileCandidates()
        {
            var fileName = Constants.App.Name + ".log";
            string executableDirectory = null;

            try
            {
                executableDirectory = Path.GetDirectoryName(Helper.GetExecutablePath());
            }
            catch
            {
                // ignored
            }

            if (!string.IsNullOrWhiteSpace(executableDirectory))
                yield return Path.Combine(executableDirectory, fileName);

            var sharedDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), Constants.App.Name);

            yield return Path.Combine(sharedDirectory, fileName);
        }

        /// <summary>
        /// Resolves the log file path. The file is created next to the executable, or in the shared
        /// application data folder when the executable folder is read-only.
        /// </summary>
        /// <returns>The log file path. Null when no candidate is writable.</returns>
        private static string ResolveLogFilePath()
        {
            foreach (var path in GetLogFileCandidates())
            {
                try
                {
                    var directory = Path.GetDirectoryName(path);

                    if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
                        Directory.CreateDirectory(directory);

                    using (new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 1, FileOptions.None))
                    {
                        // Only checks that the file can be opened for writing
                    }

                    return path;
                }
                catch
                {
                    // Try the next candidate
                }
            }

            _fileDisabled = true;

            return null;
        }

        /// <summary>
        /// Renames the log file when it reached the size limit
        /// </summary>
        /// <param name="path">The log file path</param>
        private static void RotateLogFile(string path)
        {
            try
            {
                var file = new FileInfo(path);

                if (!file.Exists || file.Length < MaxLogFileSize)
                    return;

                var backupPath = path + ".old";

                if (File.Exists(backupPath))
                    File.Delete(backupPath);

                File.Move(path, backupPath);
            }
            catch
            {
                // ignored
            }
        }

        /// <summary>
        /// Writes a line to the log file. The file is opened and closed per entry so it can be
        /// copied while the app is running.
        /// </summary>
        /// <param name="line">The line</param>
        private static void WriteLogFile(string line)
        {
            try
            {
                lock (_fileLock)
                {
                    if (_fileDisabled)
                        return;

                    var path = _logFilePath ?? ResolveLogFilePath();

                    if (path == null)
                        return;

                    RotateLogFile(path);

                    using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite, 4096, FileOptions.WriteThrough))
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
                    {
                        writer.WriteLine(line);
                        writer.Flush();
                    }
                }
            }
            catch
            {
                _fileDisabled = true;
            }
        }

        /// <summary>
        /// Warning
        /// </summary>
        /// <param name="message">Message</param>
        /// <param name="method">Method</param>
        public static void Warning(string message, [CallerMemberName] string method = null)
        {
            Log(new Log(Enums.Log.Levels.Warning, message, null, method));
        }
    }

    #endregion Methods
}
