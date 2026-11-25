using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace AgentProxy
{
    internal sealed class LauncherSettings
    {
        public string Target { get; set; }
        public string Host { get; set; }
        public string Port { get; set; }
        public string Protocol { get; set; }
        public bool Enhanced { get; set; }
        public bool AllowInsecureTls { get; set; }
        public bool English { get; set; }
        public string Username { get; set; }
        public string ProtectedPassword { get; set; }

        [ScriptIgnore]
        public string Password { get; set; }
    }

    internal sealed class LauncherSettingsStore
    {
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("AgentProxy.Settings.v1");
        private readonly string filePath;

        internal LauncherSettingsStore(string filePath = null)
        {
            this.filePath = String.IsNullOrWhiteSpace(filePath)
                ? DefaultFilePath()
                : Path.GetFullPath(filePath);
        }

        internal string FilePath { get { return filePath; } }

        internal static string DefaultFilePath()
        {
            string root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (String.IsNullOrWhiteSpace(root))
            {
                root = AppDomain.CurrentDomain.BaseDirectory;
            }
            return Path.Combine(root, "AgentProxy", "settings.json");
        }

        internal bool Exists()
        {
            return File.Exists(filePath);
        }

        internal LauncherSettings Load()
        {
            try
            {
                if (!File.Exists(filePath)) return null;
                string json = File.ReadAllText(filePath, new UTF8Encoding(false));
                if (String.IsNullOrWhiteSpace(json)) return null;
                var serializer = new JavaScriptSerializer { MaxJsonLength = 262144 };
                var settings = serializer.Deserialize<LauncherSettings>(json);
                if (settings == null) return null;
                settings.Password = UnprotectPassword(settings.ProtectedPassword);
                return settings;
            }
            catch
            {
                return null;
            }
        }

        internal void Save(LauncherSettings settings)
        {
            if (settings == null) return;
            try
            {
                string dir = Path.GetDirectoryName(filePath);
                if (!String.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

                var copy = new LauncherSettings
                {
                    Target = settings.Target ?? "codex",
                    Host = settings.Host ?? String.Empty,
                    Port = settings.Port ?? String.Empty,
                    Protocol = settings.Protocol ?? "http",
                    Enhanced = settings.Enhanced,
                    AllowInsecureTls = settings.AllowInsecureTls,
                    English = settings.English,
                    Username = settings.Username ?? String.Empty,
                    ProtectedPassword = ProtectPassword(settings.Password)
                };

                var serializer = new JavaScriptSerializer { MaxJsonLength = 262144 };
                string json = serializer.Serialize(copy);
                string tempPath = filePath + ".tmp";
                File.WriteAllText(tempPath, json, new UTF8Encoding(false));
                if (File.Exists(filePath))
                {
                    File.Replace(tempPath, filePath, null);
                }
                else
                {
                    File.Move(tempPath, filePath);
                }
            }
            catch
            {
                // Non-fatal: do not block app launch if settings persistence cannot write.
            }
        }

        internal static string ProtectPassword(string plaintext)
        {
            if (String.IsNullOrEmpty(plaintext)) return String.Empty;
            byte[] plainBytes = Encoding.UTF8.GetBytes(plaintext);
            try
            {
                byte[] protectedBytes = ProtectedData.Protect(plainBytes, Entropy, DataProtectionScope.CurrentUser);
                return Convert.ToBase64String(protectedBytes);
            }
            finally
            {
                Array.Clear(plainBytes, 0, plainBytes.Length);
            }
        }

        internal static string UnprotectPassword(string protectedBase64)
        {
            if (String.IsNullOrWhiteSpace(protectedBase64)) return String.Empty;
            try
            {
                byte[] protectedBytes = Convert.FromBase64String(protectedBase64);
                byte[] plainBytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
                try
                {
                    return Encoding.UTF8.GetString(plainBytes);
                }
                finally
                {
                    Array.Clear(plainBytes, 0, plainBytes.Length);
                }
            }
            catch
            {
                return String.Empty;
            }
        }
    }
}
