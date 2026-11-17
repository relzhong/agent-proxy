using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using Microsoft.Win32;

namespace AgentProxy
{
    internal static class AntigravityLauncher
    {
        internal const string UpstreamDirectRuleName = "agentproxy-upstream-direct";

        private static readonly string[] RequiredProcesses = {
            "agy.exe",
            "language_server.exe",
            "language_server_windows",
            "Antigravity.exe",
            "node.exe"
        };

        internal static string NormalizeProtocol(string protocol)
        {
            if (String.IsNullOrWhiteSpace(protocol)) return "http";
            string value = protocol.Trim().ToLowerInvariant();
            if (value == "http" || value == "https") return "http";
            if (value == "socks5") return "socks5";
            throw new ArgumentException("Unsupported Antigravity proxy protocol: " + protocol);
        }

        internal static void ValidateEndpoint(string host, int port, string protocol)
        {
            if (String.IsNullOrWhiteSpace(host) || host.Length > 255
                || host.IndexOfAny(new[] { ' ', '\t', '\r', '\n', '\0', '/', '\\', ':' }) >= 0 && !IsLiteralIpv6(host))
                throw new ArgumentException("Invalid proxy host for Antigravity.");
            if (port < 1 || port > 65535)
                throw new ArgumentException("Invalid proxy port.");
            NormalizeProtocol(protocol);
        }

        private static bool IsLiteralIpv6(string host)
        {
            IPAddress parsed;
            return IPAddress.TryParse(host, out parsed)
                && parsed.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6;
        }

        internal static string ResolveExecutable(string overridePath = null)
        {
            if (!String.IsNullOrWhiteSpace(overridePath))
            {
                string full = Path.GetFullPath(overridePath);
                if (!File.Exists(full) || !Path.GetFileName(full).Equals("Antigravity.exe", StringComparison.OrdinalIgnoreCase))
                    throw new FileNotFoundException("Antigravity.exe was not found at the specified path.", full);
                return full;
            }

            var candidates = new List<string>
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Antigravity", "Antigravity.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Antigravity", "Antigravity.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Antigravity", "Antigravity.exe")
            };

            foreach (string path in candidates)
            {
                if (!String.IsNullOrEmpty(path) && File.Exists(path))
                    return Path.GetFullPath(path);
            }

            string fromRegistry = FindExecutableInUninstallRegistry();
            if (!String.IsNullOrEmpty(fromRegistry) && File.Exists(fromRegistry))
                return Path.GetFullPath(fromRegistry);

            throw new FileNotFoundException("Antigravity Desktop (Antigravity.exe) was not found.");
        }

        private static string FindExecutableInUninstallRegistry()
        {
            string[] roots = {
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall",
                @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"
            };
            foreach (RegistryKey hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
            {
                foreach (string sub in roots)
                {
                    try
                    {
                        using (RegistryKey key = hive.OpenSubKey(sub))
                        {
                            if (key == null) continue;
                            foreach (string name in key.GetSubKeyNames())
                            {
                                using (RegistryKey entry = key.OpenSubKey(name))
                                {
                                    if (entry == null) continue;
                                    string display = entry.GetValue("DisplayName") as string;
                                    if (String.IsNullOrEmpty(display) || display.IndexOf("Antigravity", StringComparison.OrdinalIgnoreCase) < 0)
                                        continue;
                                    if (display.IndexOf("IDE", StringComparison.OrdinalIgnoreCase) >= 0)
                                        continue;
                                    string loc = entry.GetValue("InstallLocation") as string;
                                    if (!String.IsNullOrEmpty(loc))
                                    {
                                        string exe = Path.Combine(loc.Trim('"'), "Antigravity.exe");
                                        if (File.Exists(exe)) return exe;
                                    }
                                    string icon = entry.GetValue("DisplayIcon") as string;
                                    if (!String.IsNullOrEmpty(icon))
                                    {
                                        string cleaned = icon.Split(',')[0].Trim().Trim('"');
                                        if (Path.GetFileName(cleaned).Equals("Antigravity.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(cleaned))
                                            return cleaned;
                                    }
                                }
                            }
                        }
                    }
                    catch { }
                }
            }
            return null;
        }

        internal static bool IsRunning(string executablePath)
        {
            if (String.IsNullOrWhiteSpace(executablePath)) return false;
            string fullExe = Path.GetFullPath(executablePath);
            string dir = Path.GetDirectoryName(fullExe);
            string langServer = Path.Combine(dir, "resources", "bin", "language_server.exe");

            if (IsExecutableProcessActive("Antigravity", fullExe)) return true;
            if (IsExecutableProcessActive("language_server", langServer)) return true;
            return false;
        }

        private static bool IsExecutableProcessActive(string processName, string expectedPath)
        {
            foreach (Process p in Process.GetProcessesByName(processName))
            {
                using (p)
                {
                    try
                    {
                        if (p.HasExited) continue;
                        string actual = Path.GetFullPath(p.MainModule.FileName);
                        if (actual.Equals(expectedPath, StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                    catch { }
                }
            }
            return false;
        }

        internal static string BuildConfigJson(string existingJson, string host, int port, string protocol)
        {
            ValidateEndpoint(host, port, protocol);
            string normalizedHost = host.Trim();
            string normalizedProtocol = NormalizeProtocol(protocol);
            var serializer = new JavaScriptSerializer { MaxJsonLength = 1048576 };

            Dictionary<string, object> root = null;
            if (!String.IsNullOrWhiteSpace(existingJson))
            {
                try
                {
                    root = serializer.Deserialize<Dictionary<string, object>>(existingJson);
                }
                catch
                {
                    root = null;
                }
            }
            if (root == null) root = new Dictionary<string, object>(StringComparer.Ordinal);

            if (!root.ContainsKey("_comment")) root["_comment"] = "Antigravity-Proxy configuration managed by AgentProxy";
            if (!root.ContainsKey("_version")) root["_version"] = "1.7";
            if (!root.ContainsKey("log_level")) root["log_level"] = "info";

            Dictionary<string, object> proxy = GetOrCreateObject(root, "proxy");
            proxy["host"] = normalizedHost;
            proxy["port"] = port;
            proxy["type"] = normalizedProtocol;
            if (!proxy.ContainsKey("username")) proxy["username"] = "";
            if (!proxy.ContainsKey("password")) proxy["password"] = "";

            if (!root.ContainsKey("fake_ip"))
            {
                root["fake_ip"] = new Dictionary<string, object> {
                    { "enabled", true },
                    { "cidr", "198.18.0.0/15" }
                };
            }

            if (!root.ContainsKey("timeout"))
            {
                root["timeout"] = new Dictionary<string, object> {
                    { "connect", 5000 },
                    { "send", 5000 },
                    { "recv", 5000 }
                };
            }

            Dictionary<string, object> ui = GetOrCreateObject(root, "ui");
            ui["load_notify"] = "none";

            Dictionary<string, object> updates = GetOrCreateObject(root, "updates");
            updates["enabled"] = false;

            if (!root.ContainsKey("traffic_logging")) root["traffic_logging"] = false;
            if (!root.ContainsKey("child_injection")) root["child_injection"] = true;
            if (!root.ContainsKey("child_injection_mode")) root["child_injection_mode"] = "filtered";

            var processes = new List<string>();
            object rawProcesses;
            if (root.TryGetValue("target_processes", out rawProcesses))
            {
                var arr = rawProcesses as System.Collections.IEnumerable;
                if (arr != null && !(rawProcesses is string))
                {
                    foreach (object item in arr)
                    {
                        string s = item as string;
                        if (!String.IsNullOrWhiteSpace(s) && !processes.Contains(s, StringComparer.OrdinalIgnoreCase))
                            processes.Add(s);
                    }
                }
            }
            foreach (string required in RequiredProcesses)
            {
                if (!processes.Contains(required, StringComparer.OrdinalIgnoreCase))
                    processes.Add(required);
            }
            root["target_processes"] = processes.ToArray();

            Dictionary<string, object> proxyRules = GetOrCreateObject(root, "proxy_rules");
            if (!proxyRules.ContainsKey("allowed_ports")) proxyRules["allowed_ports"] = new[] { 80, 443 };
            if (!proxyRules.ContainsKey("dns_mode")) proxyRules["dns_mode"] = "direct";
            if (!proxyRules.ContainsKey("ipv6_mode")) proxyRules["ipv6_mode"] = "proxy";
            if (!proxyRules.ContainsKey("udp_mode")) proxyRules["udp_mode"] = "auto";
            if (!proxyRules.ContainsKey("udp_fallback")) proxyRules["udp_fallback"] = "block";

            Dictionary<string, object> routing = GetOrCreateObject(proxyRules, "routing");
            routing["enabled"] = true;
            if (!routing.ContainsKey("priority_mode")) routing["priority_mode"] = "order";
            if (!routing.ContainsKey("default_action")) routing["default_action"] = "proxy";
            routing["use_default_private"] = true;

            var ruleList = new List<object>();
            object rawRules;
            if (routing.TryGetValue("rules", out rawRules))
            {
                var arr = rawRules as System.Collections.IEnumerable;
                if (arr != null && !(rawRules is string))
                {
                    foreach (object item in arr)
                    {
                        var dict = item as Dictionary<string, object>;
                        if (dict != null)
                        {
                            object nameObj;
                            if (dict.TryGetValue("name", out nameObj) && UpstreamDirectRuleName.Equals(nameObj as string, StringComparison.OrdinalIgnoreCase))
                                continue;
                        }
                        ruleList.Add(item);
                    }
                }
            }

            IPAddress upstreamIp;
            if (IPAddress.TryParse(normalizedHost, out upstreamIp)
                && upstreamIp.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                && !AgentProxy.ProcessProxy.ProxyPlan.IsLoopback(upstreamIp))
            {
                ruleList.Insert(0, new Dictionary<string, object> {
                    { "name", UpstreamDirectRuleName },
                    { "enabled", true },
                    { "action", "direct" },
                    { "priority", 0 },
                    { "ip_cidrs_v4", new[] { upstreamIp.ToString() + "/32" } },
                    { "ip_cidrs_v6", new string[0] },
                    { "domains", new string[0] },
                    { "ports", new string[0] },
                    { "protocols", new[] { "tcp", "udp" } }
                });
            }
            routing["rules"] = ruleList.ToArray();

            return serializer.Serialize(root);
        }

        private static Dictionary<string, object> GetOrCreateObject(Dictionary<string, object> parent, string key)
        {
            object existing;
            if (parent.TryGetValue(key, out existing))
            {
                var dict = existing as Dictionary<string, object>;
                if (dict != null) return dict;
            }
            var created = new Dictionary<string, object>(StringComparer.Ordinal);
            parent[key] = created;
            return created;
        }

        internal static string BundledDllPath(string baseDirectory = null)
        {
            string root = String.IsNullOrEmpty(baseDirectory) ? AppDomain.CurrentDomain.BaseDirectory : baseDirectory;
            return Path.Combine(root, "antigravity-proxy", "version.dll");
        }

        internal static void DeployAndWriteConfig(string executablePath, string host, int port, string protocol, string bundledDllPath = null)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(executablePath));
            string sourceDll = bundledDllPath ?? BundledDllPath();
            if (!File.Exists(sourceDll))
                throw new FileNotFoundException("Bundled Antigravity proxy component (version.dll) is missing.", sourceDll);

            string targetDll = Path.Combine(dir, "version.dll");
            string sourceHash = HashFile(sourceDll);
            if (!File.Exists(targetDll) || !HashFile(targetDll).Equals(sourceHash, StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(sourceDll, targetDll, true);
            }

            string configPath = Path.Combine(dir, "config.json");
            string existing = File.Exists(configPath) ? File.ReadAllText(configPath, new UTF8Encoding(false)) : null;
            string updated = BuildConfigJson(existing, host, port, protocol);
            File.WriteAllText(configPath, updated, new UTF8Encoding(false));
        }

        internal static void RemovePatch(string executablePath)
        {
            string dir = Path.GetDirectoryName(Path.GetFullPath(executablePath));
            string targetDll = Path.Combine(dir, "version.dll");
            if (File.Exists(targetDll))
            {
                File.Delete(targetDll);
            }
        }

        internal static bool IsPatched(string executablePath)
        {
            if (String.IsNullOrWhiteSpace(executablePath)) return false;
            string dir = Path.GetDirectoryName(Path.GetFullPath(executablePath));
            return File.Exists(Path.Combine(dir, "version.dll"));
        }

        internal static void Launch(string executablePath)
        {
            string full = Path.GetFullPath(executablePath);
            var info = new ProcessStartInfo(full)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(full)
            };
            Process.Start(info);
        }

        private static string HashFile(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var sha = SHA256.Create())
            {
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
            }
        }
    }
}
