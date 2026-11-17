using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;
using AgentProxy;

internal static class AntigravityLauncherTests
{
    private static int checks;
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("FAIL: " + label);
        checks++;
    }
    private static void Reject(Action action, string label)
    {
        bool failed = false;
        try { action(); } catch { failed = true; }
        Check(failed, label);
    }

    private static int Main()
    {
        Check(AntigravityLauncher.NormalizeProtocol("http") == "http", "http normalized");
        Check(AntigravityLauncher.NormalizeProtocol("HTTP") == "http", "HTTP normalized");
        Check(AntigravityLauncher.NormalizeProtocol("https") == "http", "https normalized");
        Check(AntigravityLauncher.NormalizeProtocol("SOCKS5") == "socks5", "socks5 normalized");
        Reject(() => AntigravityLauncher.NormalizeProtocol("ftp"), "unsupported protocol rejected");

        AntigravityLauncher.ValidateEndpoint("127.0.0.1", 7890, "socks5");
        AntigravityLauncher.ValidateEndpoint("192.168.1.50", 10808, "http");
        AntigravityLauncher.ValidateEndpoint("localhost", 1080, "http");
        AntigravityLauncher.ValidateEndpoint("::1", 1080, "socks5");
        Reject(() => AntigravityLauncher.ValidateEndpoint("", 7890, "http"), "empty host rejected");
        Reject(() => AntigravityLauncher.ValidateEndpoint("127.0.0.1:7890", 7890, "http"), "host with port rejected");
        Reject(() => AntigravityLauncher.ValidateEndpoint("127.0.0.1", 0, "http"), "port 0 rejected");
        Reject(() => AntigravityLauncher.ValidateEndpoint("127.0.0.1", 70000, "http"), "port > 65535 rejected");

        var serializer = new JavaScriptSerializer();
        string freshJson = AntigravityLauncher.BuildConfigJson(null, "192.168.1.50", 7897, "socks5");
        var fresh = serializer.Deserialize<Dictionary<string, object>>(freshJson);
        var proxy = (Dictionary<string, object>)fresh["proxy"];
        Check((string)proxy["host"] == "192.168.1.50" && (int)proxy["port"] == 7897 && (string)proxy["type"] == "socks5", "fresh proxy config");
        var ui = (Dictionary<string, object>)fresh["ui"];
        Check((string)ui["load_notify"] == "none", "silent load notify");
        var updates = (Dictionary<string, object>)fresh["updates"];
        Check((bool)updates["enabled"] == false, "updates disabled");
        var targets = ((ArrayList)fresh["target_processes"]).Cast<string>().ToArray();
        Check(targets.Contains("Antigravity.exe") && targets.Contains("language_server.exe"), "required target processes");
        var rules = (Dictionary<string, object>)fresh["proxy_rules"];
        var routing = (Dictionary<string, object>)rules["routing"];
        var ruleItems = ((ArrayList)routing["rules"]).Cast<Dictionary<string, object>>().ToArray();
        Check(ruleItems.Length == 1 && (string)ruleItems[0]["name"] == AntigravityLauncher.UpstreamDirectRuleName, "non-loopback direct rule added");
        Check(((ArrayList)ruleItems[0]["ip_cidrs_v4"]).Cast<string>().First() == "192.168.1.50/32", "non-loopback CIDR exact");

        // Preserve user rule and replace stale upstream rule when switching back to loopback
        string existingWithCustom = freshJson.Replace("\"rules\":[", "\"rules\":[{\"name\":\"custom-rule\",\"enabled\":true,\"action\":\"direct\"},");
        string updatedJson = AntigravityLauncher.BuildConfigJson(existingWithCustom, "127.0.0.1", 10808, "http");
        var updated = serializer.Deserialize<Dictionary<string, object>>(updatedJson);
        var updatedProxy = (Dictionary<string, object>)updated["proxy"];
        Check((string)updatedProxy["host"] == "127.0.0.1" && (int)updatedProxy["port"] == 10808 && (string)updatedProxy["type"] == "http", "updated proxy config");
        var updatedRuleItems = ((ArrayList)((Dictionary<string, object>)((Dictionary<string, object>)updated["proxy_rules"])["routing"])["rules"]).Cast<Dictionary<string, object>>().ToArray();
        Check(updatedRuleItems.Length == 1 && (string)updatedRuleItems[0]["name"] == "custom-rule", "stale upstream rule removed while custom rule preserved");

        // File deployment, tamper repair, and removal
        string tempRoot = Path.Combine(Path.GetTempPath(), "ag-launcher-test-" + Guid.NewGuid().ToString("N"));
        string appDir = Path.Combine(tempRoot, "app");
        string srcDir = Path.Combine(tempRoot, "src");
        Directory.CreateDirectory(appDir);
        Directory.CreateDirectory(srcDir);
        try
        {
            string fakeExe = Path.Combine(appDir, "Antigravity.exe");
            string fakeDll = Path.Combine(srcDir, "version.dll");
            File.WriteAllText(fakeExe, "exe");
            File.WriteAllText(fakeDll, "valid-version-dll");

            Check(!AntigravityLauncher.IsPatched(fakeExe), "initially unpatched");
            AntigravityLauncher.DeployAndWriteConfig(fakeExe, "192.168.1.50", 7890, "socks5", fakeDll);
            string deployedDll = Path.Combine(appDir, "version.dll");
            string configPath = Path.Combine(appDir, "config.json");
            Check(AntigravityLauncher.IsPatched(fakeExe) && File.ReadAllText(deployedDll) == "valid-version-dll", "dll deployed");
            byte[] cfgBytes = File.ReadAllBytes(configPath);
            Check(!(cfgBytes.Length >= 3 && cfgBytes[0] == 0xEF && cfgBytes[1] == 0xBB && cfgBytes[2] == 0xBF), "config written without UTF-8 BOM");

            // Corrupt deployed dll and ensure re-deploy repairs it
            File.WriteAllText(deployedDll, "corrupted");
            AntigravityLauncher.DeployAndWriteConfig(fakeExe, "192.168.1.50", 7890, "socks5", fakeDll);
            Check(File.ReadAllText(deployedDll) == "valid-version-dll", "corrupted dll repaired");

            AntigravityLauncher.RemovePatch(fakeExe);
            Check(!AntigravityLauncher.IsPatched(fakeExe), "patch removed");
        }
        finally
        {
            Directory.Delete(tempRoot, true);
        }

        Console.WriteLine("PASS " + checks + " AntigravityLauncher assertions");
        return 0;
    }
}
