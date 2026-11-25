using System;
using System.IO;
using System.Text;
using AgentProxy;

internal static class LauncherSettingsTests
{
    private static int checks;
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("FAIL: " + label);
        checks++;
    }

    private static int Main()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "agentproxy-settings-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            string path = Path.Combine(tempRoot, "nested", "settings.json");
            var store = new LauncherSettingsStore(path);
            Check(!store.Exists(), "settings initially missing");
            Check(store.Load() == null, "load returns null when file absent");

            var input = new LauncherSettings
            {
                Target = "antigravity",
                Host = "proxy.example.com",
                Port = "443",
                Protocol = "https",
                Enhanced = true,
                AllowInsecureTls = true,
                English = true,
                Username = "my-user",
                Password = "SuperSecret!Password#99"
            };
            store.Save(input);
            Check(store.Exists(), "settings file created");

            string rawJson = File.ReadAllText(path, new UTF8Encoding(false));
            Check(rawJson.Contains("proxy.example.com") && rawJson.Contains("443"), "host and port persisted");
            Check(rawJson.Contains("my-user"), "username persisted");
            Check(!rawJson.Contains("SuperSecret!Password#99"), "plaintext password NEVER written to disk");
            Check(rawJson.Contains("ProtectedPassword"), "DPAPI protected password stored");

            LauncherSettings loaded = store.Load();
            Check(loaded != null, "saved settings loaded");
            Check(loaded.Target == "antigravity", "target restored");
            Check(loaded.Host == "proxy.example.com", "host restored");
            Check(loaded.Port == "443", "port restored");
            Check(loaded.Protocol == "https", "protocol restored");
            Check(loaded.Enhanced, "enhanced flag restored");
            Check(loaded.AllowInsecureTls, "AllowInsecureTls flag restored");
            Check(loaded.English, "language flag restored");
            Check(loaded.Username == "my-user", "username restored");
            Check(loaded.Password == "SuperSecret!Password#99", "DPAPI password decrypted cleanly for current user");

            // Empty password clears protected blob cleanly
            store.Save(new LauncherSettings
            {
                Target = "codex",
                Host = "127.0.0.1",
                Port = "10808",
                Protocol = "http",
                Enhanced = false,
                English = false,
                Username = "",
                Password = ""
            });
            LauncherSettings cleared = store.Load();
            Check(cleared != null && cleared.Password == "" && cleared.Username == "", "empty credentials persisted cleanly");

            // Corrupted DPAPI payload degrades gracefully instead of crashing
            Check(LauncherSettingsStore.UnprotectPassword("not-valid-base64!!!") == "", "corrupted base64 handled gracefully");
        }
        finally
        {
            Directory.Delete(tempRoot, true);
        }

        Console.WriteLine("PASS " + checks + " LauncherSettings DPAPI assertions");
        return 0;
    }
}
