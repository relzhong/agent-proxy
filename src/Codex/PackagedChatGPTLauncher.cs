using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.IO;
using System.Text;
using Microsoft.Win32;

// Store apps must be activated by their registered AppUserModelId. Windows
// builds their environment from HKCU\Environment, not from this helper's own
// process. Keep the registry override inside a short, serialized launch window
// and restore the exact prior values even when activation fails.
internal static class PackagedChatGPTLauncher
{
    private const string BackupPath = @"Software\GeoD\AgentProxy\ProxyEnvBackup";
    private static readonly string[] ProxyKeys = {
        "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "WS_PROXY", "WSS_PROXY", "NO_PROXY", "NODE_USE_ENV_PROXY", BrowserReadLaunchPolicy.EnvironmentKey
    };

    [ComImport, Guid("2E941141-7F97-4756-BA1D-9DECDE894A3D"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IApplicationActivationManager
    {
        void ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId,
            [MarshalAs(UnmanagedType.LPWStr)] string arguments, uint options, out uint processId);
    }

    private static readonly Guid ActivationManagerClsid = new Guid("45BA127D-10A8-46EA-8AB7-56EA9078943C");

    [STAThread]
    private static int Main(string[] args)
    {
        Uri proxy;
        if (args.Length < 1 || args.Length > 3 || !Uri.TryCreate(args[0], UriKind.Absolute, out proxy)
            || (proxy.Scheme != Uri.UriSchemeHttp && !proxy.Scheme.Equals("socks5", StringComparison.OrdinalIgnoreCase)
                && !proxy.Scheme.Equals("direct", StringComparison.OrdinalIgnoreCase))
            || String.IsNullOrEmpty(proxy.Host) || proxy.Port <= 0
            || proxy.AbsolutePath != "/")
        {
            Console.Error.WriteLine("Usage: PackagedChatGPTLauncher.exe (http|socks5|direct)://host:port [cdp-port [expected-package-executable]]");
            return 2;
        }

        int cdpPort = 0;
        if (args.Length >= 2 && (!Int32.TryParse(args[1], out cdpPort) || cdpPort < (args.Length == 3 ? 0 : 1) || cdpPort > 65535))
        {
            Console.Error.WriteLine("Invalid CDP port.");
            return 2;
        }

        try
        {
            using (var mutex = new Mutex(false, @"Local\AgentProxyProxyEnv"))
            {
                bool ownsMutex;
                try { ownsMutex = mutex.WaitOne(TimeSpan.FromSeconds(20)); }
                catch (AbandonedMutexException) { ownsMutex = true; }
                if (!ownsMutex)
                    throw new TimeoutException("Another ChatGPT proxy launch is in progress.");
                try
                {
                    RestorePreviousEnvironment();
                    string installedExecutable;
                    string family = FindPackageFamily(args.Length == 3 ? args[2] : null, out installedExecutable);
                    if (family == null)
                        throw new InvalidOperationException("ChatGPT/Codex Store package is not installed.");

                    SaveEnvironment();
                    uint pid;
                    DateTime activationStarted=DateTime.UtcNow;
                    try
                    {
                        SetProxyEnvironment(args[0]);
                        pid = Activate(family, args[0], cdpPort);
                    }
                    finally
                    {
                        RestorePreviousEnvironment();
                    }
                    if (pid == 0)
                        throw new InvalidOperationException("Windows did not return an application process ID.");
                    // Store activation can return a short-lived forwarding process.
                    // Verify the resulting main window, not the departed broker PID.
                    int confirmed=ActivationProcessResolver.Wait(installedExecutable,activationStarted,ReadActivationCandidates,
                        ()=>Thread.Sleep(200),()=>DateTime.UtcNow,TimeSpan.FromSeconds(10));
                    Console.WriteLine(confirmed);
                }
                finally { mutex.ReleaseMutex(); }
            }
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error.Message + " (0x" + error.HResult.ToString("X8") + ")");
            return 1;
        }
    }

    private static string BuildBypassList(string proxyUrl, string separator, bool bracketIpv6)
    {
        string list = bracketIpv6 ? "localhost" + separator + "127.0.0.1" + separator + "[::1]"
                                  : "localhost" + separator + "127.0.0.1" + separator + "::1";
        Uri uri;
        System.Net.IPAddress ip;
        if (Uri.TryCreate(proxyUrl, UriKind.Absolute, out uri) && !String.IsNullOrEmpty(uri.Host)
            && System.Net.IPAddress.TryParse(uri.Host, out ip) && !System.Net.IPAddress.IsLoopback(ip))
        {
            list += separator + ip.ToString();
        }
        return list;
    }

    private static uint Activate(string family, string proxy, int cdpPort)
    {
        var manager = (IApplicationActivationManager)Activator.CreateInstance(Type.GetTypeFromCLSID(ActivationManagerClsid));
        try
        {
            Uri uri;
            bool isDirect = Uri.TryCreate(proxy, UriKind.Absolute, out uri)
                && uri.Scheme.Equals("direct", StringComparison.OrdinalIgnoreCase);
            string arguments = isDirect
                ? String.Empty
                : "--proxy-server=" + proxy + " --proxy-bypass-list=" + BuildBypassList(proxy, ";", true);
            if (cdpPort > 0) arguments += (arguments.Length > 0 ? " " : String.Empty) + "--remote-debugging-port=" + cdpPort;
            uint pid;
            manager.ActivateApplication(family + "!App", arguments, 0, out pid);
            return pid;
        }
        finally { Marshal.ReleaseComObject(manager); }
    }

    private static string FindPackageFamily(string expectedExecutable, out string installedExecutable)
    {
        installedExecutable=null;
        const string script = "$p=@(Get-AppxPackage -Name 'OpenAI.Codex';Get-AppxPackage -Name 'OpenAI.ChatGPT-Desktop')|Sort-Object Version -Descending|Select-Object -First 1;if($p){[Console]::Out.Write($p.PackageFamilyName+'|'+$p.InstallLocation)}";
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"), "-NoLogo -NoProfile -NonInteractive -Command \"" + script + "\"")
        {
            UseShellExecute = false, RedirectStandardOutput = true,
            RedirectStandardError = true, CreateNoWindow = true
        };
        using (Process process = Process.Start(start))
        {
            var outputRead=process.StandardOutput.ReadToEndAsync();
            var errorRead=process.StandardError.ReadToEndAsync();
            if(!process.WaitForExit(15000)) {
                // Only this owned, read-only package lookup is stopped. No client
                // process or proxy environment has been changed at this point.
                try {process.Kill();process.WaitForExit(2000);} catch { }
                throw new TimeoutException("Windows package discovery did not finish within fifteen seconds. The client was not started.");
            }
            string output = outputRead.GetAwaiter().GetResult().Trim();
            string errors = errorRead.GetAwaiter().GetResult();
            if (process.ExitCode != 0)
                throw new InvalidOperationException("Cannot query ChatGPT package: " + errors.Trim());
            if (output.Length == 0) return null;
            string[] parts = output.Split('|');
            if (parts.Length != 2) throw new InvalidOperationException("Invalid package discovery result.");
            if (expectedExecutable != null && !Path.GetFullPath(Path.GetDirectoryName(Path.GetDirectoryName(expectedExecutable))).Equals(Path.GetFullPath(parts[1]), StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Package version changed before activation. Restart Launch Deck; enhanced rules were not broadened.");
            foreach(string name in new[]{"ChatGPT.exe","Codex.exe"}) {
                string candidate=Path.Combine(parts[1],"app",name);
                if(File.Exists(candidate)){installedExecutable=Path.GetFullPath(candidate);break;}
            }
            if(installedExecutable==null || (expectedExecutable!=null && !installedExecutable.Equals(expectedExecutable,StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("Registered package executable could not be verified.");
            return parts[0];
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref uint count);
    private static void VerifyActivatedExecutable(Process process, string expected)
    {
        var image = new StringBuilder(32768); uint count = (uint)image.Capacity;
        if (!QueryFullProcessImageName(process.Handle, 0, image, ref count)
            || !Path.GetFullPath(image.ToString()).Equals(Path.GetFullPath(expected), StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Activated package identity changed or could not be verified. Enhanced proxy must stop; the client was not terminated.");
    }

    private static System.Collections.Generic.IEnumerable<ActivationCandidate> ReadActivationCandidates(string executable)
    {
        var result=new System.Collections.Generic.List<ActivationCandidate>();
        foreach(var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(executable))) {
            using(process) try {
                if(process.HasExited || process.MainWindowHandle==IntPtr.Zero) continue;
                VerifyActivatedExecutable(process,executable);
                result.Add(new ActivationCandidate { Id=process.Id,Executable=executable,StartedUtc=process.StartTime.ToUniversalTime(),HasWindow=true });
            } catch(InvalidOperationException) { } catch(System.ComponentModel.Win32Exception) { }
        }
        return result;
    }

    private static void SaveEnvironment()
    {
        using (RegistryKey environment = Registry.CurrentUser.CreateSubKey("Environment"))
        using (RegistryKey backup = Registry.CurrentUser.CreateSubKey(BackupPath))
        {
            foreach (string key in ProxyKeys)
            {
                object previous = environment.GetValue(key, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                backup.SetValue(key + ".present", previous == null ? 0 : 1, RegistryValueKind.DWord);
                if (previous != null)
                    backup.SetValue(key, previous, environment.GetValueKind(key));
            }
            backup.SetValue("complete", 1, RegistryValueKind.DWord);
            backup.Flush();
        }
    }

    private static void SetProxyEnvironment(string proxy)
    {
        Uri uri;
        bool isDirect = Uri.TryCreate(proxy, UriKind.Absolute, out uri)
            && uri.Scheme.Equals("direct", StringComparison.OrdinalIgnoreCase);
        bool isSocks5 = uri != null && uri.Scheme.Equals("socks5", StringComparison.OrdinalIgnoreCase);
        using (RegistryKey environment = Registry.CurrentUser.CreateSubKey("Environment"))
        {
            if (isDirect)
            {
                // Enhanced mode handles authenticated upstreams via WinDivert without exposing credentials to env/Chromium.
                foreach (string key in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "WS_PROXY", "WSS_PROXY", "NO_PROXY", "NODE_USE_ENV_PROXY" })
                    environment.DeleteValue(key, false);
            }
            else if (isSocks5)
            {
                // Node.js NODE_USE_ENV_PROXY rejects socks5:// schemes; rely on WinDivert enhanced proxy for Node/TCP443.
                foreach (string key in new[] { "HTTP_PROXY", "HTTPS_PROXY", "WS_PROXY", "WSS_PROXY", "NODE_USE_ENV_PROXY" })
                    environment.DeleteValue(key, false);
                environment.SetValue("ALL_PROXY", "socks5h://" + uri.Host + ":" + uri.Port, RegistryValueKind.String);
                environment.SetValue("NO_PROXY", BuildBypassList(proxy, ",", false), RegistryValueKind.String);
            }
            else
            {
                environment.SetValue("HTTP_PROXY", proxy, RegistryValueKind.String);
                environment.SetValue("HTTPS_PROXY", proxy, RegistryValueKind.String);
                environment.SetValue("ALL_PROXY", proxy, RegistryValueKind.String);
                environment.SetValue("WS_PROXY", proxy, RegistryValueKind.String);
                environment.SetValue("WSS_PROXY", proxy, RegistryValueKind.String);
                environment.SetValue("NODE_USE_ENV_PROXY", "1", RegistryValueKind.String);
                environment.SetValue("NO_PROXY", BuildBypassList(proxy, ",", false), RegistryValueKind.String);
            }
            string existing = environment.GetValue(BrowserReadLaunchPolicy.EnvironmentKey, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
            string inherited = Environment.GetEnvironmentVariable(BrowserReadLaunchPolicy.EnvironmentKey);
            environment.SetValue(BrowserReadLaunchPolicy.EnvironmentKey, BrowserReadLaunchPolicy.Merge(existing, inherited), RegistryValueKind.String);
            environment.Flush();
        }
    }

    private static void RestorePreviousEnvironment()
    {
        using (RegistryKey backup = Registry.CurrentUser.OpenSubKey(BackupPath))
        {
            if (backup == null) return;
            if (Convert.ToInt32(backup.GetValue("complete", 0)) == 1)
            {
                using (RegistryKey environment = Registry.CurrentUser.CreateSubKey("Environment"))
                {
                    foreach (string key in ProxyKeys)
                    {
                        object present = backup.GetValue(key + ".present", null);
                        // Older helpers did not back up ALL_PROXY; leave untracked values intact.
                        if (present == null) continue;
                        if (Convert.ToInt32(present) == 1)
                            environment.SetValue(key, backup.GetValue(key, null, RegistryValueOptions.DoNotExpandEnvironmentNames), backup.GetValueKind(key));
                        else
                            environment.DeleteValue(key, false);
                    }
                    environment.Flush();
                }
            }
        }
        Registry.CurrentUser.DeleteSubKeyTree(BackupPath, false);
    }
}
