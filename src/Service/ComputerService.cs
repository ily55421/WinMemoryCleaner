using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace WinMemoryCleaner
{
    /// <summary>
    /// Computer Service
    /// </summary>
    public class ComputerService : IComputerService
    {
        #region Fields

        private static readonly HashSet<string> _unavailablePrivileges = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private Memory _memory = new Memory(new Structs.Windows.MemoryStatusEx());
        private OperatingSystem _operatingSystem;

        #endregion

        #region Properties

        /// <summary>
        /// Gets the memory info (RAM)
        /// </summary>
        public Memory Memory
        {
            get
            {
                try
                {
                    var memoryStatusEx = new Structs.Windows.MemoryStatusEx();

                    if (!NativeMethods.GlobalMemoryStatusEx(memoryStatusEx))
                        Logger.Error(new Win32Exception(Marshal.GetLastWin32Error()));

                    _memory = new Memory(memoryStatusEx);
                }
                catch (Exception e)
                {
                    Logger.Error(e);
                }

                return _memory;
            }
        }

        /// <summary>
        /// Gets the operating system info
        /// </summary>
        public OperatingSystem OperatingSystem
        {
            get
            {
                if (_operatingSystem == null)
                {
                    var operatingSystem = Environment.OSVersion;

                    _operatingSystem = new OperatingSystem
                    {
                        Is64Bit = Environment.Is64BitOperatingSystem,
                        IsWindows7OrGreater = (operatingSystem.Version.Major > 6) || (operatingSystem.Version.Major == 6 && operatingSystem.Version.Minor >= 1),
                        IsWindows8OrGreater = operatingSystem.Version.Major >= 6.2,
                        IsWindows81OrGreater = operatingSystem.Version.Major >= 6.3,
                        IsWindowsVistaOrGreater = operatingSystem.Version.Major >= 6,
                        IsWindowsXpOrGreater = operatingSystem.Version.Major >= 5.1
                    };
                }

                return _operatingSystem;
            }
        }

        #endregion

        #region Events

        /// <summary>
        /// Occurs when [optimize progress is update].
        /// </summary>
        public event Action<byte, string> OnOptimizeProgressUpdate;

        #endregion

        #region Methods (Computer)

        private static SafeFileHandle OpenVolumeHandle(string driveLetter)
        {
            if (string.IsNullOrWhiteSpace(driveLetter))
                return null;

            return NativeMethods.CreateFile
            (
                @"\\.\" + driveLetter.TrimEnd(':', '\\') + ":",
                FileAccess.ReadWrite,
                FileShare.Read | FileShare.Write,
                IntPtr.Zero,
                FileMode.Open,
                (int)FileAttributes.Normal | Constants.Windows.File.FlagsNoBuffering,
                IntPtr.Zero
            );
        }

        /// <summary>
        /// Increases the Privilege using a privilege name
        /// </summary>
        /// <param name="privilegeName">The name of the privilege that needs to be increased</param>
        /// <returns><c>true</c> if the privilege is enabled in the current process token; otherwise, <c>false</c></returns>
        private static bool SetIncreasePrivilege(string privilegeName)
        {
            var token = IntPtr.Zero;

            try
            {
                if (!NativeMethods.OpenProcessToken(NativeMethods.GetCurrentProcess(), (uint)(Constants.Windows.TokenAccess.AdjustPrivileges | Constants.Windows.TokenAccess.Query), out token))
                    return false;

                Structs.Windows.TokenPrivileges newState;
                newState.Count = 1;
                newState.Luid = 0L;
                newState.Attr = Constants.Windows.PrivilegeAttribute.Enabled;

                // Retrieves the uid used on a specified system to locally represent the specified privilege name
                if (!NativeMethods.LookupPrivilegeValue(null, privilegeName, ref newState.Luid))
                    return false;

                // Enables or disables privileges in a specified access token
                if (!NativeMethods.AdjustTokenPrivileges(token, false, ref newState, 0, IntPtr.Zero, IntPtr.Zero))
                    return false;

                // AdjustTokenPrivileges returns success even when the privilege was not assigned to the token
                return Marshal.GetLastWin32Error() == Constants.Windows.SystemErrorCode.ErrorSuccess;
            }
            finally
            {
                if (token != IntPtr.Zero)
                    NativeMethods.CloseHandle(token);
            }
        }

        /// <summary>
        /// Enables every privilege required by the memory areas. On machines where a privilege is
        /// removed by group policy or blocked by security software the optimization continues with
        /// the areas that do not depend on it (and the limitation is logged once).
        /// </summary>
        private static void EnablePrivileges()
        {
            var privileges = new[]
            {
                Constants.Windows.Privilege.SeProfSingleProcessName,
                Constants.Windows.Privilege.SeIncreaseQuotaName,
                Constants.Windows.Privilege.SeDebugName
            };

            foreach (var privilege in privileges)
            {
                if (SetIncreasePrivilege(privilege))
                    continue;

                if (_unavailablePrivileges.Add(privilege))
                {
                    Logger.Warning(string.Format(Localizer.Culture, "Privilege '{0}' is not available. It may have been removed by Windows security policy or blocked by a security product. Some memory areas may not be released.", privilege));
                }
            }
        }

        /// <summary>
        /// Converts an NTSTATUS returned by NtSetSystemInformation into a readable message.
        /// The Win32 error returned by Marshal.GetLastWin32Error is meaningless for NT native calls.
        /// </summary>
        /// <param name="status">The NTSTATUS code</param>
        /// <returns>The readable message</returns>
        internal static string GetNtStatusMessage(int status)
        {
            switch (unchecked((uint)status))
            {
                case 0xC0000022U:
                    return "STATUS_ACCESS_DENIED: access is denied";

                case 0xC0000061U:
                    return "STATUS_PRIVILEGE_NOT_HELD: the process does not hold the required Windows privilege";

                case 0xC000000D:
                    return "STATUS_INVALID_PARAMETER: the request is not supported by this Windows version";

                case 0xC0000002:
                    return "STATUS_NOT_IMPLEMENTED: the request is not supported by this Windows version";

                case 0xC0000004:
                    return "STATUS_INFO_LENGTH_MISMATCH: the request length does not match this Windows version";

                case 0xC000009A:
                    return "STATUS_INSUFFICIENT_RESOURCES: not enough resources to complete the request";

                default:
                    return string.Format(CultureInfo.InvariantCulture, "NTSTATUS 0x{0:X8}", unchecked((uint)status));
            }
        }

        /// <summary>
        /// Gets the available physical memory (in bytes)
        /// </summary>
        /// <returns>The available physical memory. Zero when the value cannot be read.</returns>
        private static long GetAvailablePhysicalMemory()
        {
            var memoryStatusEx = new Structs.Windows.MemoryStatusEx();

            return NativeMethods.GlobalMemoryStatusEx(memoryStatusEx) ? memoryStatusEx.AvailPhys : 0;
        }

        /// <summary>
        /// Creates an exception for a memory area that failed with the given NTSTATUS
        /// </summary>
        /// <param name="memoryArea">The memory area name</param>
        /// <param name="status">The NTSTATUS code</param>
        /// <returns>The exception</returns>
        private static Exception NewOptimizationException(string memoryArea, int status)
        {
            return new Exception(string.Format(Localizer.Culture, "{0}: {1}", memoryArea, GetNtStatusMessage(status)));
        }

        /// <summary>
        /// Sends a command to the system memory list
        /// </summary>
        /// <param name="command">The system memory list command</param>
        /// <returns>The NTSTATUS code</returns>
        private static int SetMemoryListCommand(int command)
        {
            var handle = GCHandle.Alloc(command, GCHandleType.Pinned);

            try
            {
                return NativeMethods.NtSetSystemInformation(Constants.Windows.SystemInformationClass.SystemMemoryListInformation, handle.AddrOfPinnedObject(), (uint)Marshal.SizeOf(command));
            }
            finally
            {
                try
                {
                    if (handle.IsAllocated)
                        handle.Free();
                }
                catch (InvalidOperationException)
                {
                    // ignored
                }
            }
        }

        /// <summary>
        /// Sets the system file cache limits (SIZE_T -1 flushes the cache)
        /// </summary>
        /// <param name="informationClass">The system information class</param>
        /// <returns>The NTSTATUS code</returns>
        private int SetSystemFileCacheInformation(int informationClass)
        {
            var handle = GCHandle.Alloc(0);

            try
            {
                object systemFileCacheInformation;

                if (OperatingSystem.Is64Bit)
                    systemFileCacheInformation = new Structs.Windows.SystemFileCacheInformation64 { MinimumWorkingSet = -1L, MaximumWorkingSet = -1L };
                else
                    systemFileCacheInformation = new Structs.Windows.SystemFileCacheInformation32 { MinimumWorkingSet = int.MaxValue, MaximumWorkingSet = int.MaxValue };

                handle = GCHandle.Alloc(systemFileCacheInformation, GCHandleType.Pinned);

                return NativeMethods.NtSetSystemInformation(informationClass, handle.AddrOfPinnedObject(), (uint)Marshal.SizeOf(systemFileCacheInformation));
            }
            finally
            {
                try
                {
                    if (handle.IsAllocated)
                        handle.Free();
                }
                catch (InvalidOperationException)
                {
                    // ignored
                }
            }
        }

        #endregion

        #region Methods (Memory)

        /// <summary>
        /// Gets the number of progress steps the optimizer reports for the specified memory areas.
        /// It must stay in sync with the progress events raised by <see cref="Optimize" />,
        /// otherwise the progress bar never reaches 100%.
        /// </summary>
        /// <param name="areas">Memory areas</param>
        /// <returns>The number of progress steps</returns>
        public static byte GetOptimizationStepCount(Enums.Memory.Areas areas)
        {
            var steps = 0;

            if ((areas & Enums.Memory.Areas.WorkingSet) != 0)
                steps++;

            if ((areas & Enums.Memory.Areas.SystemFileCache) != 0)
                steps++;

            if ((areas & Enums.Memory.Areas.ModifiedPageList) != 0)
                steps++;

            // The standby list is optimized as a single step, regardless of the priority variant
            if ((areas & (Enums.Memory.Areas.StandbyList | Enums.Memory.Areas.StandbyListLowPriority)) != 0)
                steps++;

            if ((areas & Enums.Memory.Areas.CombinedPageList) != 0)
                steps++;

            if ((areas & Enums.Memory.Areas.RegistryCache) != 0)
                steps++;

            if ((areas & Enums.Memory.Areas.ModifiedFileCache) != 0)
                steps++;

            // Garbage Collector
            steps++;

            return (byte)steps;
        }

        /// <summary>
        /// Optimize the computer
        /// </summary>
        /// <param name="reason">Optimization reason</param>
        /// <param name="areas">Memory areas</param>
        public void Optimize(Enums.Memory.Optimization.Reason reason, Enums.Memory.Areas areas)
        {
            if (areas == Enums.Memory.Areas.None)
                return;

            var availablePhysicalMemoryBefore = GetAvailablePhysicalMemory();
            var errorRuntime = new TimeSpan();
            var infoRuntime = new TimeSpan();
            var optimizationReason = reason.GetString();
            var stopwatch = new Stopwatch();
            var value = (byte)0;

            var error = new LogOptimizationData { Reason = optimizationReason };
            var info = new LogOptimizationData { Reason = optimizationReason };

            EnablePrivileges();

            // Optimize Working Set
            if ((areas & Enums.Memory.Areas.WorkingSet) != 0)
            {
                try
                {
                    if (OnOptimizeProgressUpdate != null)
                    {
                        value++;
                        OnOptimizeProgressUpdate(value, Localizer.String.WorkingSet);
                    }

                    stopwatch.Restart();

                    OptimizeWorkingSet();

                    info.MemoryAreas.Add(new LogOptimizationDataMemoryArea
                    {
                        Name = Localizer.String.WorkingSet,
                        Duration = string.Format(Localizer.Culture, "{0:0.0} {1}", stopwatch.Elapsed.TotalSeconds, Localizer.String.Seconds.ToLower(Localizer.Culture))
                    });

                    infoRuntime = infoRuntime.Add(stopwatch.Elapsed);
                }
                catch (Exception e)
                {
                    error.MemoryAreas.Add(new LogOptimizationDataMemoryArea
                    {
                        Name = Localizer.String.WorkingSet,
                        Duration = string.Format(Localizer.Culture, "{0:0.0} {1}", stopwatch.Elapsed.TotalSeconds, Localizer.String.Seconds.ToLower(Localizer.Culture)),
                        Error = e.GetMessage()
                    });

                    errorRuntime = errorRuntime.Add(stopwatch.Elapsed);
                }
            }

            // Optimize System File Cache
            if ((areas & Enums.Memory.Areas.SystemFileCache) != 0)
            {
                try
                {
                    if (OnOptimizeProgressUpdate != null)
                    {
                        value++;
                        OnOptimizeProgressUpdate(value, Localizer.String.SystemFileCache);
                    }

                    stopwatch.Restart();

                    OptimizeSystemFileCache();

                    info.MemoryAreas.Add(new LogOptimizationDataMemoryArea
                    {
                        Name = Localizer.String.SystemFileCache,
                        Duration = string.Format(Localizer.Culture, "{0:0.0} {1}", stopwatch.Elapsed.TotalSeconds, Localizer.String.Seconds.ToLower(Localizer.Culture))
                    });

                    infoRuntime = infoRuntime.Add(stopwatch.Elapsed);
                }
                catch (Exception e)
                {
                    error.MemoryAreas.Add(new LogOptimizationDataMemoryArea
                    {
                        Name = Localizer.String.SystemFileCache,
                        Duration = string.Format(Localizer.Culture, "{0:0.0} {1}", stopwatch.Elapsed.TotalSeconds, Localizer.String.Seconds.ToLower(Localizer.Culture)),
                        Error = e.GetMessage()
                    });

                    errorRuntime = errorRuntime.Add(stopwatch.Elapsed);
                }
            }

            // Optimize Modified Page List
            if ((areas & Enums.Memory.Areas.ModifiedPageList) != 0)
            {
                try
                {
                    if (OnOptimizeProgressUpdate != null)
                    {
                        value++;
                        OnOptimizeProgressUpdate(value, Localizer.String.ModifiedPageList);
                    }

                    stopwatch.Restart();

                    OptimizeModifiedPageList();

                    info.MemoryAreas.Add(new LogOptimizationDataMemoryArea
                    {
                        Name = Localizer.String.ModifiedPageList,
                        Duration = string.Format(Localizer.Culture, "{0:0.0} {1}", stopwatch.Elapsed.TotalSeconds, Localizer.String.Seconds.ToLower(Localizer.Culture))
                    });

                    infoRuntime = infoRuntime.Add(stopwatch.Elapsed);
                }
                catch (Exception e)
                {
                    error.MemoryAreas.Add(new LogOptimizationDataMemoryArea
                    {
                        Name = Localizer.String.ModifiedPageList,
                        Duration = string.Format(Localizer.Culture, "{0:0.0} {1}", stopwatch.Elapsed.TotalSeconds, Localizer.String.Seconds.ToLower(Localizer.Culture)),
                        Error = e.GetMessage()
                    });

                    errorRuntime = errorRuntime.Add(stopwatch.Elapsed);
                }
            }

            // Optimize Standby List
            if ((areas & (Enums.Memory.Areas.StandbyList | Enums.Memory.Areas.StandbyListLowPriority)) != 0)
            {
                var lowPriority = (areas & Enums.Memory.Areas.StandbyListLowPriority) != 0;
                var standbyList = lowPriority ? Localizer.String.StandbyListLowPriority : Localizer.String.StandbyList;

                try
                {
                    if (OnOptimizeProgressUpdate != null)
                    {
                        value++;
                        OnOptimizeProgressUpdate(value, standbyList);
                    }

                    stopwatch.Restart();

                    OptimizeStandbyList(lowPriority);

                    info.MemoryAreas.Add(new LogOptimizationDataMemoryArea
                    {
                        Name = standbyList,
                        Duration = string.Format(Localizer.Culture, "{0:0.0} {1}", stopwatch.Elapsed.TotalSeconds, Localizer.String.Seconds.ToLower(Localizer.Culture))
                    });

                    infoRuntime = infoRuntime.Add(stopwatch.Elapsed);
                }
                catch (Exception e)
                {
                    error.MemoryAreas.Add(new LogOptimizationDataMemoryArea
                    {
                        Name = standbyList,
                        Duration = string.Format(Localizer.Culture, "{0:0.0} {1}", stopwatch.Elapsed.TotalSeconds, Localizer.String.Seconds.ToLower(Localizer.Culture)),
                        Error = e.GetMessage()
                    });

                    errorRuntime = errorRuntime.Add(stopwatch.Elapsed);
                }
            }

            // Optimize Combined Page List
            if ((areas & Enums.Memory.Areas.CombinedPageList) != 0)
            {
                try
                {
                    if (OnOptimizeProgressUpdate != null)
                    {
                        value++;
                        OnOptimizeProgressUpdate(value, Localizer.String.CombinedPageList);
                    }

                    stopwatch.Restart();

                    OptimizeCombinedPageList();

                    info.MemoryAreas.Add(new LogOptimizationDataMemoryArea
                    {
                        Name = Localizer.String.CombinedPageList,
                        Duration = string.Format(Localizer.Culture, "{0:0.0} {1}", stopwatch.Elapsed.TotalSeconds, Localizer.String.Seconds.ToLower(Localizer.Culture))
                    });

                    infoRuntime = infoRuntime.Add(stopwatch.Elapsed);
                }
                catch (Exception e)
                {
                    error.MemoryAreas.Add(new LogOptimizationDataMemoryArea
                    {
                        Name = Localizer.String.CombinedPageList,
                        Duration = string.Format(Localizer.Culture, "{0:0.0} {1}", stopwatch.Elapsed.TotalSeconds, Localizer.String.Seconds.ToLower(Localizer.Culture)),
                        Error = e.GetMessage()
                    });

                    errorRuntime = errorRuntime.Add(stopwatch.Elapsed);
                }
            }

            // Optimize Registry Cache
            if ((areas & Enums.Memory.Areas.RegistryCache) != 0)
            {
                try
                {
                    if (OnOptimizeProgressUpdate != null)
                    {
                        value++;
                        OnOptimizeProgressUpdate(value, Localizer.String.RegistryCache);
                    }

                    stopwatch.Restart();

                    OptimizeRegistryCache();

                    info.MemoryAreas.Add(new LogOptimizationDataMemoryArea
                    {
                        Name = Localizer.String.RegistryCache,
                        Duration = string.Format(Localizer.Culture, "{0:0.0} {1}", stopwatch.Elapsed.TotalSeconds, Localizer.String.Seconds.ToLower(Localizer.Culture))
                    });

                    infoRuntime = infoRuntime.Add(stopwatch.Elapsed);
                }
                catch (Exception e)
                {
                    error.MemoryAreas.Add(new LogOptimizationDataMemoryArea
                    {
                        Name = Localizer.String.RegistryCache,
                        Duration = string.Format(Localizer.Culture, "{0:0.0} {1}", stopwatch.Elapsed.TotalSeconds, Localizer.String.Seconds.ToLower(Localizer.Culture)),
                        Error = e.GetMessage()
                    });

                    errorRuntime = errorRuntime.Add(stopwatch.Elapsed);
                }
            }

            // Optimize Modified File Cache
            if ((areas & Enums.Memory.Areas.ModifiedFileCache) != 0)
            {
                try
                {
                    if (OnOptimizeProgressUpdate != null)
                    {
                        value++;
                        OnOptimizeProgressUpdate(value, Localizer.String.ModifiedFileCache);
                    }

                    stopwatch.Restart();

                    OptimizeModifiedFileCache();

                    info.MemoryAreas.Add(new LogOptimizationDataMemoryArea
                    {
                        Name = Localizer.String.ModifiedFileCache,
                        Duration = string.Format(Localizer.Culture, "{0:0.0} {1}", stopwatch.Elapsed.TotalSeconds, Localizer.String.Seconds.ToLower(Localizer.Culture))
                    });

                    infoRuntime = infoRuntime.Add(stopwatch.Elapsed);
                }
                catch (Exception e)
                {
                    error.MemoryAreas.Add(new LogOptimizationDataMemoryArea
                    {
                        Name = Localizer.String.ModifiedFileCache,
                        Duration = string.Format(Localizer.Culture, "{0:0.0} {1}", stopwatch.Elapsed.TotalSeconds, Localizer.String.Seconds.ToLower(Localizer.Culture)),
                        Error = e.GetMessage()
                    });

                    errorRuntime = errorRuntime.Add(stopwatch.Elapsed);
                }
            }

            // Garbage Collector
            try
            {
                if (OnOptimizeProgressUpdate != null)
                {
                    value++;
                    OnOptimizeProgressUpdate(value, Localizer.String.GarbageCollector);
                }

                App.ReleaseMemory();
            }
            catch
            {
                // ignored
            }

            // Give the memory manager a moment to publish the released pages before measuring
            Thread.Sleep(300);

            var releasedMemory = Helper.GetReleasedBytes(availablePhysicalMemoryBefore, GetAvailablePhysicalMemory()).ToMemoryUnit();

            info.Released = string.Format(Localizer.Culture, "{0:0.#} {1}", releasedMemory.Key, releasedMemory.Value);

            // Log
            try
            {
                // Info
                if (info.MemoryAreas.Any())
                {
                    info.Duration = string.Format(Localizer.Culture, "{0:0.0} {1}", infoRuntime.TotalSeconds, Localizer.String.Seconds.ToLower(Localizer.Culture));

                    Logger.Log(new Log(Enums.Log.Levels.Information, Localizer.String.MemoryOptimized, info));
                }
                // Error
                if (error.MemoryAreas.Any())
                {
                    error.Duration = string.Format(Localizer.Culture, "{0:0.0} {1}", errorRuntime.TotalSeconds, Localizer.String.Seconds.ToLower(Localizer.Culture));

                    Logger.Log(new Log(Enums.Log.Levels.Error, Localizer.String.Invalid, error));
                }
            }
            catch
            {
                // ignored
            }
        }

        /// <summary>
        /// Optimize the combined page list.
        /// </summary>
        private void OptimizeCombinedPageList()
        {
            // Windows minimum version
            if (!OperatingSystem.HasCombinedPageList)
                throw new Exception(string.Format(Localizer.Culture, Localizer.String.ErrorMemoryAreaOptimizationNotSupported, Localizer.String.CombinedPageList));

            // Check privilege
            if (!SetIncreasePrivilege(Constants.Windows.Privilege.SeProfSingleProcessName))
                throw new Exception(string.Format(Localizer.Culture, Localizer.String.ErrorAdminPrivilegeRequired, Constants.Windows.Privilege.SeProfSingleProcessName));

            var handle = GCHandle.Alloc(0);

            try
            {
                var memoryCombineInformationEx = new Structs.Windows.MemoryCombineInformationEx();

                handle = GCHandle.Alloc(memoryCombineInformationEx, GCHandleType.Pinned);

                var status = NativeMethods.NtSetSystemInformation(Constants.Windows.SystemInformationClass.SystemCombinePhysicalMemoryInformation, handle.AddrOfPinnedObject(), (uint)Marshal.SizeOf(memoryCombineInformationEx));

                if (status != Constants.Windows.SystemErrorCode.ErrorSuccess)
                    throw NewOptimizationException(Localizer.String.CombinedPageList, status);
            }
            finally
            {
                try
                {
                    if (handle.IsAllocated)
                        handle.Free();
                }
                catch (InvalidOperationException)
                {
                    // ignored
                }
            }
        }

        /// <summary>
        /// Optimize the modified file cache.
        /// </summary>
        private void OptimizeModifiedFileCache()
        {
            // Windows minimum version
            if (!OperatingSystem.HasModifiedFileCache)
                throw new Exception(string.Format(Localizer.Culture, Localizer.String.ErrorMemoryAreaOptimizationNotSupported, Localizer.String.ModifiedFileCache));

            foreach (var drive in DriveInfo.GetDrives())
            {
                if (drive == null || drive.DriveType != DriveType.Fixed || string.IsNullOrWhiteSpace(drive.Name))
                    continue;

                using (var handle = OpenVolumeHandle(drive.Name))
                {
                    if (handle == null || handle.IsInvalid)
                        continue;

                    int bytesReturned;

                    if (OperatingSystem.IsWindows7OrGreater)
                    {
                        try
                        {
                            var buffer = Marshal.AllocHGlobal(1);

                            try
                            {
                                if (!NativeMethods.DeviceIoControl(
                                    handle,
                                    Constants.Windows.Drive.IoControlResetWriteOrder,
                                    buffer,
                                    1,
                                    IntPtr.Zero,
                                    0,
                                    out bytesReturned,
                                    IntPtr.Zero))
                                {
                                    throw new Win32Exception(Marshal.GetLastWin32Error());
                                }
                            }
                            finally
                            {
                                Marshal.FreeHGlobal(buffer);
                            }
                        }
                        catch
                        {
                            // ignored
                        }

                        if (OperatingSystem.IsWindows8OrGreater)
                        {
                            try
                            {
                                if (!NativeMethods.DeviceIoControl(
                                    handle,
                                    Constants.Windows.Drive.FsctlDiscardVolumeCache,
                                    IntPtr.Zero,
                                    0,
                                    IntPtr.Zero,
                                    0,
                                    out bytesReturned,
                                    IntPtr.Zero))
                                {
                                    throw new Win32Exception(Marshal.GetLastWin32Error());
                                }
                            }
                            catch
                            {
                                // ignored
                            }
                        }
                    }

                    if (!NativeMethods.FlushFileBuffers(handle))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }
        }

        /// <summary>
        /// Optimize the modified page list.
        /// </summary>
        private void OptimizeModifiedPageList()
        {
            // Windows minimum version
            if (!OperatingSystem.HasModifiedPageList)
                throw new Exception(string.Format(Localizer.Culture, Localizer.String.ErrorMemoryAreaOptimizationNotSupported, Localizer.String.ModifiedPageList));

            // Check privilege
            if (!SetIncreasePrivilege(Constants.Windows.Privilege.SeProfSingleProcessName))
                throw new Exception(string.Format(Localizer.Culture, Localizer.String.ErrorAdminPrivilegeRequired, Constants.Windows.Privilege.SeProfSingleProcessName));

            var status = SetMemoryListCommand(Constants.Windows.SystemMemoryListCommand.MemoryFlushModifiedList);

            if (status != Constants.Windows.SystemErrorCode.ErrorSuccess)
                throw NewOptimizationException(Localizer.String.ModifiedPageList, status);
        }

        /// <summary>
        /// Optimize the registry cache.
        /// </summary>
        private void OptimizeRegistryCache()
        {
            // Windows minimum version
            if (!OperatingSystem.HasRegistryHive)
                throw new Exception(string.Format(Localizer.Culture, Localizer.String.ErrorMemoryAreaOptimizationNotSupported, Localizer.String.RegistryCache));

            var status = NativeMethods.NtSetSystemInformation(Constants.Windows.SystemInformationClass.SystemRegistryReconciliationInformation, IntPtr.Zero, 0);

            if (status != Constants.Windows.SystemErrorCode.ErrorSuccess)
                throw NewOptimizationException(Localizer.String.RegistryCache, status);
        }

        /// <summary>
        /// Optimize the standby list.
        /// </summary>
        /// <param name="lowPriority">if set to <c>true</c> [low priority].</param>
        private void OptimizeStandbyList(bool lowPriority = false)
        {
            // Windows minimum version
            if (!OperatingSystem.HasStandbyList)
                throw new Exception(string.Format(Localizer.Culture, Localizer.String.ErrorMemoryAreaOptimizationNotSupported, Localizer.String.StandbyList));

            // Check privilege
            if (!SetIncreasePrivilege(Constants.Windows.Privilege.SeProfSingleProcessName))
                throw new Exception(string.Format(Localizer.Culture, Localizer.String.ErrorAdminPrivilegeRequired, Constants.Windows.Privilege.SeProfSingleProcessName));

            var status = SetMemoryListCommand(lowPriority ? Constants.Windows.SystemMemoryListCommand.MemoryPurgeLowPriorityStandbyList : Constants.Windows.SystemMemoryListCommand.MemoryPurgeStandbyList);

            // Some Windows builds and security products reject the full purge but still allow the low priority one
            if (status != Constants.Windows.SystemErrorCode.ErrorSuccess && !lowPriority)
                status = SetMemoryListCommand(Constants.Windows.SystemMemoryListCommand.MemoryPurgeLowPriorityStandbyList);

            if (status != Constants.Windows.SystemErrorCode.ErrorSuccess)
                throw NewOptimizationException(lowPriority ? Localizer.String.StandbyListLowPriority : Localizer.String.StandbyList, status);
        }

        /// <summary>
        /// Optimize the system file cache.
        /// </summary>
        private void OptimizeSystemFileCache()
        {
            // Windows minimum version
            if (!OperatingSystem.HasSystemFileCache)
                throw new Exception(string.Format(Localizer.Culture, Localizer.String.ErrorMemoryAreaOptimizationNotSupported, Localizer.String.SystemFileCache));

            // Check privilege
            if (!SetIncreasePrivilege(Constants.Windows.Privilege.SeIncreaseQuotaName))
                throw new Exception(string.Format(Localizer.Culture, Localizer.String.ErrorAdminPrivilegeRequired, Constants.Windows.Privilege.SeIncreaseQuotaName));

            var status = SetSystemFileCacheInformation(Constants.Windows.SystemInformationClass.SystemFileCacheInformation);

            // Windows 8+ exposes the same information through an extended class. Use it when the legacy one is rejected.
            if (status != Constants.Windows.SystemErrorCode.ErrorSuccess && OperatingSystem.IsWindows8OrGreater)
                status = SetSystemFileCacheInformation(Constants.Windows.SystemInformationClass.SystemFileCacheInformationEx);

            if (status != Constants.Windows.SystemErrorCode.ErrorSuccess)
                throw NewOptimizationException(Localizer.String.SystemFileCache, status);

            var fileCacheSize = IntPtr.Subtract(IntPtr.Zero, 1); // Flush

            if (!NativeMethods.SetSystemFileCacheSize(fileCacheSize, fileCacheSize, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        /// <summary>
        /// Optimize the working set.
        /// </summary>
        private void OptimizeWorkingSet()
        {
            // Windows minimum version
            if (!OperatingSystem.HasWorkingSet)
                throw new Exception(string.Format(Localizer.Culture, Localizer.String.ErrorMemoryAreaOptimizationNotSupported, Localizer.String.WorkingSet));

            // The global command is the fastest way to empty every working set, but it requires
            // SeProfileSingleProcessPrivilege. That privilege can be missing on machines hardened by
            // group policy or blocked by security products, so fall back to a process-by-process trim.
            if (!Settings.ProcessExclusionList.Any())
            {
                var status = SetMemoryListCommand(Constants.Windows.SystemMemoryListCommand.MemoryEmptyWorkingSets);

                if (status == Constants.Windows.SystemErrorCode.ErrorSuccess)
                    return;

                Logger.Warning(string.Format(Localizer.Culture, "Global working set optimization failed ({0}). Falling back to process-by-process optimization.", GetNtStatusMessage(status)));
            }
            else
            {
                // Check privilege
                if (!SetIncreasePrivilege(Constants.Windows.Privilege.SeDebugName))
                    throw new Exception(string.Format(Localizer.Culture, Localizer.String.ErrorAdminPrivilegeRequired, Constants.Windows.Privilege.SeDebugName));
            }

            OptimizeProcessWorkingSets();
        }

        /// <summary>
        /// Empties the working set of every process, except the ones in the exclusion list
        /// </summary>
        private void OptimizeProcessWorkingSets()
        {
            var attempted = 0;
            var errors = new StringBuilder();
            var trimmed = 0;

            // A process can exit between the enumeration and the access to its name, so the
            // name is resolved defensively and the process is skipped when it is gone.
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    string processName;

                    try
                    {
                        processName = process.ProcessName;
                    }
                    catch (InvalidOperationException)
                    {
                        // The process exited before its name could be read
                        continue;
                    }

                    if (Settings.ProcessExclusionList.Contains(processName, StringComparer.OrdinalIgnoreCase))
                        continue;

                    attempted++;

                    try
                    {
                        if (EmptyProcessWorkingSet(process))
                            trimmed++;
                        else
                            throw new Win32Exception(Marshal.GetLastWin32Error());
                    }
                    catch (InvalidOperationException)
                    {
                        // ignored
                    }
                    catch (Win32Exception e)
                    {
                        if (e.NativeErrorCode != Constants.Windows.SystemErrorCode.ErrorAccessDenied)
                            errors.Append(string.Format(Localizer.Culture, "{0}: {1} | ", processName, e.GetMessage()));
                    }
                }
            }

            if (errors.Length > 3)
                errors.Remove(errors.Length - 3, 3);

            if (errors.Length > 0)
                throw new Exception(errors.ToString());

            // Nothing could be trimmed. That happens when SeDebugPrivilege is not assigned to the
            // process token, because every other process handle is opened with access denied.
            if (attempted > 0 && trimmed == 0)
                throw new Exception(string.Format(Localizer.Culture, Localizer.String.ErrorAdminPrivilegeRequired, Constants.Windows.Privilege.SeDebugName));
        }

        /// <summary>
        /// Empties the working set of a process
        /// </summary>
        /// <param name="process">The process</param>
        /// <returns><c>true</c> if the working set was emptied; otherwise, <c>false</c></returns>
        private static bool EmptyProcessWorkingSet(Process process)
        {
            if (NativeMethods.EmptyWorkingSet(process.Handle))
                return true;

            // SetProcessWorkingSetSize(-1, -1) also empties the working set
            return NativeMethods.SetProcessWorkingSetSize(process.Handle, new IntPtr(-1), new IntPtr(-1));
        }

        #endregion
    }
}
