﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
#if CAN_FIND_CHILD_PROCESSES
using System.Management;
#endif
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using Octopus.Diagnostics;
using Octopus.Shared.Diagnostics;

namespace Octopus.Shared.Util
{
    public static class SilentProcessRunner
    {
        // ReSharper disable once InconsistentNaming
        const int CP_OEMCP = 1;
        static readonly Encoding oemEncoding;

        static SilentProcessRunner()
        {
            try
            {
                CPINFOEX info;
                if (GetCPInfoEx(CP_OEMCP, 0, out info))
                {
                    oemEncoding = Encoding.GetEncoding(info.CodePage);
                }
                else
                {
                    oemEncoding = Encoding.GetEncoding(850);
                }
            }
            catch (Exception ex)
            {
                Log.Octopus().Warn(ex, "Couldn't get default OEM encoding");
                oemEncoding = Encoding.UTF8;
            }
        }

        public static int ExecuteCommand(this CommandLineInvocation invocation, ILog log)
        {
            return ExecuteCommand(invocation, Environment.CurrentDirectory, log);
        }

        public static int ExecuteCommand(this CommandLineInvocation invocation, string workingDirectory, ILog log)
        {
            var arguments = (invocation.Arguments ?? "") + " " + (invocation.SystemArguments ?? "");

            var exitCode = ExecuteCommand(
                invocation.Executable,
                arguments,
                workingDirectory,
                log.Info,
                log.Error
                );

            return exitCode;
        }

        public static CmdResult ExecuteCommand(this CommandLineInvocation invocation)
        {
            return ExecuteCommand(invocation, Environment.CurrentDirectory);
        }

        public static CmdResult ExecuteCommand(this CommandLineInvocation invocation, string workingDirectory)
        {
            var arguments = (invocation.Arguments ?? "") + " " + (invocation.SystemArguments ?? "");
            var infos = new List<string>();
            var errors = new List<string>();

            var exitCode = ExecuteCommand(
                invocation.Executable,
                arguments,
                workingDirectory,
                infos.Add,
                errors.Add
                );

            return new CmdResult(exitCode, infos, errors);
        }

        public static int ExecuteCommand(
            string executable, 
            string arguments,
            string workingDirectory,
            Action<string> info,
            Action<string> error,
            NetworkCredential runAs = default(NetworkCredential),
            IDictionary<string, string> customEnvironmentVariables = null,
            CancellationToken cancel = default(CancellationToken))
        {
            return ExecuteCommand(executable, arguments, workingDirectory, Log.System().Info, info, error, runAs, customEnvironmentVariables, cancel);
        }

        public static int ExecuteCommand(
            string executable,
            string arguments,
            string workingDirectory,
            Action<string> debug,
            Action<string> info,
            Action<string> error,
            NetworkCredential runAs = default(NetworkCredential),
            IDictionary<string, string> customEnvironmentVariables = null,
            CancellationToken cancel = default(CancellationToken))
        {
            try
            {
                // We need to be careful to make sure the message is accurate otherwise people could wrongly assume the exe is in the working directory when it could be somewhere completely different!
                var exeInSamePathAsWorkingDirectory = string.Equals(Path.GetDirectoryName(executable).TrimEnd('\\', '/'), workingDirectory.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
                var exeFileNameOrFullPath = exeInSamePathAsWorkingDirectory ? Path.GetFileName(executable) : executable;
                var runningAs = runAs == default(NetworkCredential) ? $@"{WindowsIdentity.GetCurrent().Name}" : $@"{runAs.Domain}\{runAs.UserName}";
                var customEnvironmentVars = runAs == default(NetworkCredential)
                    ? "the same environment variables as the launching process"
                    : customEnvironmentVariables == null
                        ? "that user's default environment variables"
                        : $"that user's environment variables plus {customEnvironmentVariables.Count} custom variable(s)";
                debug($"Starting {exeFileNameOrFullPath} in {workingDirectory} as {runningAs} with {customEnvironmentVars}");
                using (var process = new Process())
                {
                    process.StartInfo.FileName = executable;
                    process.StartInfo.Arguments = arguments;
                    process.StartInfo.WorkingDirectory = workingDirectory;
                    process.StartInfo.UseShellExecute = false;
                    process.StartInfo.CreateNoWindow = true;
                    if (runAs != default(NetworkCredential))
                    {
                        RunAsDifferentUser(process.StartInfo, runAs, customEnvironmentVariables);
                    }
                    process.StartInfo.RedirectStandardOutput = true;
                    process.StartInfo.RedirectStandardError = true;
                    process.StartInfo.StandardOutputEncoding = oemEncoding;
                    process.StartInfo.StandardErrorEncoding = oemEncoding;

                    using (var outputWaitHandle = new AutoResetEvent(false))
                    using (var errorWaitHandle = new AutoResetEvent(false))
                    {
                        process.OutputDataReceived += (sender, e) =>
                        {
                            try
                            {
                                if (e.Data == null)
                                    outputWaitHandle.Set();
                                else
                                    info(e.Data);
                            }
                            catch (Exception ex)
                            {
                                try
                                {
                                    error($"Error occured handling message: {ex.PrettyPrint()}");
                                }
                                catch
                                {
                                    // Ignore
                                }
                            }
                        };

                        process.ErrorDataReceived += (sender, e) =>
                        {
                            try
                            {
                                if (e.Data == null)
                                    errorWaitHandle.Set();
                                else
                                    error(e.Data);
                            }
                            catch (Exception ex)
                            {
                                try
                                {
                                    error($"Error occured handling message: {ex.PrettyPrint()}");
                                }
                                catch
                                {
                                    // Ignore
                                }
                            }
                        };

                        process.Start();

                        var running = true;

                        cancel.Register(() =>
                        {
                            if (!running)
                                return;
                            DoOurBestToCleanUp(process);
                        });

                        if (cancel.IsCancellationRequested)
                        {
                            DoOurBestToCleanUp(process);
                        }

                        process.BeginOutputReadLine();
                        process.BeginErrorReadLine();

                        process.WaitForExit();

                        debug($"Process {exeFileNameOrFullPath} in {workingDirectory} exited with code {process.ExitCode}");

                        running = false;

                        outputWaitHandle.WaitOne();
                        errorWaitHandle.WaitOne();

                        return process.ExitCode;
                    }
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Error when attempting to execute {executable}: {ex.Message}", ex);
            }
        }

        public static void ExecuteCommandWithoutWaiting(
            string executable,
            string arguments,
            string workingDirectory,
            NetworkCredential runAs = default(NetworkCredential),
            IDictionary<string, string> customEnvironmentVariables = null)
        {
            try
            {
                using (var process = new Process())
                {
                    process.StartInfo.FileName = executable;
                    process.StartInfo.Arguments = arguments;
                    process.StartInfo.WorkingDirectory = workingDirectory;
                    process.StartInfo.UseShellExecute = false;
                    process.StartInfo.CreateNoWindow = true;

                    if (runAs != default(NetworkCredential))
                    {
                        RunAsDifferentUser(process.StartInfo, runAs, customEnvironmentVariables);
                    }

                    process.Start();
                }
            }
            catch (Exception ex)
            {
                throw new Exception($"Error when attempting to execute {executable}: {ex.Message}", ex);
            }
        }

        private static void RunAsDifferentUser(ProcessStartInfo startInfo, NetworkCredential runAs, IDictionary<string, string> customEnvironmentVariables)
        {
            startInfo.Domain = runAs.Domain;
            startInfo.UserName = runAs.UserName;
            startInfo.Password = runAs.SecurePassword;
            startInfo.LoadUserProfile = true;

            WindowStationAndDesktopAccess.GrantAccessToWindowStationAndDesktop(runAs.UserName, runAs.Domain);

            if (customEnvironmentVariables != null && customEnvironmentVariables.Any())
            {
                SetEnvironmentVariablesForTargetUser(startInfo, runAs, customEnvironmentVariables);
            }
        }

        private static void SetEnvironmentVariablesForTargetUser(ProcessStartInfo startInfo, NetworkCredential runAs, IDictionary<string, string> customEnvironmentVariables)
        {
            // Double check before we go doing p/invoke gymnastics
            if (customEnvironmentVariables == null || !customEnvironmentVariables.Any()) return;

            // If ProcessStartInfo.enviromentVariables (field) is null, the new process will build its environment variables from scratch
            // This will be the system environment variables, plus the user's profile variables (if the user profile is loaded)
            // However, if the ProcessStartInfo.environmentVariables (field) is not null, these environment variables will be used instead
            // As soon as we touch ProcessStartInfo.EnvironmentVariables (property) it lazy loads the environment variables for the current process
            // which in turn means the launched process will get the environment variables for the wrong user profile!
 
            // See https://msdn.microsoft.com/en-us/library/windows/desktop/ms682425(v=vs.85).aspx (CreateProcess) used when ProcessStartInfo.Username is not set
            // See https://msdn.microsoft.com/en-us/library/windows/desktop/ms682431(v=vs.85).aspx (CreateProcessWithLogonW) used when ProcessStartInfo.Username is set

            // Start by getting the environment variables for the target user (as if they started a process themselves)
            // This will get the system environment variables along with the user's profile variables
            Dictionary<string, string> targetUserEnvironmentVariables;
            using (var token = AccessToken.Logon(runAs.UserName, runAs.Password, runAs.Domain))
            using (var userProfile = UserProfile.Load(token))
            {
                targetUserEnvironmentVariables = EnvironmentBlock.GetEnvironmentVariablesForUser(token, false);
                userProfile.Unload();
            }

            // Now copy in the extra environment variables we want to propagate from this process
            foreach (var variable in customEnvironmentVariables)
            {
                targetUserEnvironmentVariables[variable.Key] = variable.Value;
            }

            // Starting from a clean slate, copy the resulting environment variables into the ProcessStartInfo
            startInfo.EnvironmentVariables.Clear();
            foreach (var variable in targetUserEnvironmentVariables)
            {
                startInfo.EnvironmentVariables[variable.Key] = variable.Value;
            }
        }

        static void DoOurBestToCleanUp(Process process)
        {
            try
            {
                KillProcessAndChildren(process.Id);
            }
            catch (Exception)
            {
                try
                {
                    process.Kill();
                }
                catch (Exception)
                {
                }
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool GetCPInfoEx([MarshalAs(UnmanagedType.U4)] int CodePage, [MarshalAs(UnmanagedType.U4)] int dwFlags, out CPINFOEX lpCPInfoEx);

        const int MAX_DEFAULTCHAR = 2;
        const int MAX_LEADBYTES = 12;
        const int MAX_PATH = 260;

        [StructLayout(LayoutKind.Sequential)]
        struct CPINFOEX
        {
            [MarshalAs(UnmanagedType.U4)] public readonly int MaxCharSize;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = MAX_DEFAULTCHAR)] public readonly byte[] DefaultChar;

            [MarshalAs(UnmanagedType.ByValArray, SizeConst = MAX_LEADBYTES)] public readonly byte[] LeadBytes;

            public readonly char UnicodeDefaultChar;

            [MarshalAs(UnmanagedType.U4)] public readonly int CodePage;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = MAX_PATH)] public readonly string CodePageName;
        }

        static void KillProcessAndChildren(int pid)
        {
#if CAN_FIND_CHILD_PROCESSES
            using (var searcher = new ManagementObjectSearcher("Select * From Win32_Process Where ParentProcessID=" + pid))
            {
                using (var moc = searcher.Get())
                {
                    foreach (var mo in moc.OfType<ManagementObject>())
                    {
                        KillProcessAndChildren(Convert.ToInt32(mo["ProcessID"]));
                    }
                }
            }
#endif

            try
            {
                var proc = Process.GetProcessById(pid);
                proc.Kill();
            }
            catch (ArgumentException)
            {
                // Process already exited.
            }
        }

        internal class AccessToken : IDisposable
        {
            public string Username { get; }
            public SafeAccessTokenHandle Handle { get; }

            private AccessToken(string username, SafeAccessTokenHandle handle)
            {
                Username = username;
                Handle = handle;
            }

            public static AccessToken Logon(string username, string password, string domain = ".", LogonType logonType = LogonType.Network, LogonProvider logonProvider = LogonProvider.Default)
            {
                // See https://msdn.microsoft.com/en-us/library/windows/desktop/aa378184(v=vs.85).aspx
                var hToken = IntPtr.Zero;
                Win32Helper.Invoke(() => LogonUser(username, domain, password, LogonType.Network, LogonProvider.Default, out hToken),
                    $"Logon failed for the user '{username}'");

                return new AccessToken(username, new SafeAccessTokenHandle(hToken));
            }

            public void Dispose()
            {
                Handle?.Dispose();
            }

            public enum LogonType
            {
                Interactive = 2,
                Network = 3,
                Batch = 4,
                Service = 5,
                Unlock = 7,
                NetworkClearText = 8,
                NewCredentials = 9
            }

            public enum LogonProvider
            {
                Default = 0,
                WinNT40 = 2,
                WinNT50 = 3,
            }

            [DllImport("advapi32.dll", SetLastError = true)]
            private static extern bool LogonUser(string username, string domain, string password, LogonType logonType, LogonProvider logonProvider, out IntPtr hToken);
        }

        internal class UserProfile : IDisposable
        {
            readonly AccessToken token;
            readonly SafeRegistryHandle userProfile;

            private UserProfile(AccessToken token, SafeRegistryHandle userProfile)
            {
                this.token = token;
                this.userProfile = userProfile;
            }

            public static UserProfile Load(AccessToken token)
            {
                var userProfile = new PROFILEINFO
                {
                    lpUserName = token.Username
                };
                userProfile.dwSize = Marshal.SizeOf(userProfile);

                // See https://msdn.microsoft.com/en-us/library/windows/desktop/bb762281(v=vs.85).aspx
                Win32Helper.Invoke(() => LoadUserProfile(token.Handle, ref userProfile),
                    $"Failed to load user profile for user '{token.Username}'");

                return new UserProfile(token, new SafeRegistryHandle(userProfile.hProfile, false));
            }

            public void Unload()
            {
                // See https://msdn.microsoft.com/en-us/library/windows/desktop/bb762282(v=vs.85).aspx
                // This function closes the registry handle for the user profile too
                Win32Helper.Invoke(() => UnloadUserProfile(token.Handle, userProfile),
                    $"Failed to unload user profile for user '{token.Username}'");
            }

            public void Dispose()
            {

                if (userProfile != null && !userProfile.IsClosed)
                {
                    try
                    {
                        Unload();
                    }
                    catch
                    {
                        // Don't throw in dispose method
                    }

                    userProfile.Dispose();
                }
            }

            [DllImport("userenv.dll", SetLastError = true)]
            static extern bool LoadUserProfile(SafeAccessTokenHandle hToken, ref PROFILEINFO lpProfileInfo);

            [DllImport("userenv.dll", SetLastError = true)]
            static extern bool UnloadUserProfile(SafeAccessTokenHandle hToken, SafeRegistryHandle hProfile);

            [StructLayout(LayoutKind.Sequential)]
            struct PROFILEINFO
            {
                public int dwSize;
                public int dwFlags;
                public string lpUserName;
                public string lpProfilePath;
                public string lpDefaultPath;
                public string lpServerName;
                public string lpPolicyPath;
                public IntPtr hProfile;
            }
        }

        private class Win32Helper
        {
            public static bool Invoke(Func<bool> nativeMethod, string failureDescription)
            {
                try
                {
                    return nativeMethod() ? true : throw new Win32Exception();
                }
                catch (Win32Exception ex)
                {
                    throw new Exception($"{failureDescription}: {ex.Message}", ex);
                }
            }
        }

        internal class EnvironmentBlock
        {
            internal static Dictionary<string, string> GetEnvironmentVariablesForUser(AccessToken token, bool inheritFromCurrentProcess)
            {
                var env = IntPtr.Zero;

                // See https://msdn.microsoft.com/en-us/library/windows/desktop/bb762270(v=vs.85).aspx
                Win32Helper.Invoke(() => CreateEnvironmentBlock(out env, token.Handle, inheritFromCurrentProcess),
                    $"Failed to load the environment variables for the user '{token.Username}'");

                var userEnvironment = new Dictionary<string, string>();
                try
                {
                    var testData = new StringBuilder();
                    unsafe
                    {
                        // The environment block is an array of null-terminated Unicode strings.
                        // Key and Value are separated by =
                        // The list ends with two nulls (\0\0).
                        var start = (short*)env.ToPointer();
                        var done = false;
                        var current = start;
                        while (!done)
                        {
                            if (testData.Length > 0 && *current == 0 && current != start)
                            {
                                var data = testData.ToString();
                                var index = data.IndexOf('=');
                                if (index == -1)
                                {
                                    userEnvironment.Add(data, "");
                                }
                                else if (index == data.Length - 1)
                                {
                                    userEnvironment.Add(data.Substring(0, index), "");
                                }
                                else
                                {
                                    userEnvironment.Add(data.Substring(0, index), data.Substring(index + 1));
                                }
                                testData.Length = 0;
                            }
                            if (*current == 0 && current != start && *(current - 1) == 0)
                            {
                                done = true;
                            }
                            if (*current != 0)
                            {
                                testData.Append((char)*current);
                            }
                            current++;
                        }
                    }
                }
                finally
                {
                    // See https://msdn.microsoft.com/en-us/library/windows/desktop/bb762274(v=vs.85).aspx
                    Win32Helper.Invoke(() => DestroyEnvironmentBlock(env),
                        $"Failed to destroy the environment variables structure for user '{token.Username}'");
                }

                return userEnvironment;
            }

            [DllImport("userenv.dll", SetLastError = true)]
            private static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, SafeAccessTokenHandle hToken, bool inheritFromCurrentProcess);

            [DllImport("userenv.dll", SetLastError = true)]
            private static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);
        }

        internal sealed class SafeAccessTokenHandle : SafeHandle
        {
            // 0 is an Invalid Handle
            public SafeAccessTokenHandle(IntPtr handle) : base(handle, true) { }

            public static SafeAccessTokenHandle InvalidHandle => new SafeAccessTokenHandle(IntPtr.Zero);

            public override bool IsInvalid => handle == IntPtr.Zero || handle == new IntPtr(-1);

            protected override bool ReleaseHandle()
            {
                return CloseHandle(handle);
            }

            [DllImport("kernel32.dll", SetLastError = true)]
            static extern bool CloseHandle(IntPtr hHandle);
        }
    }
}