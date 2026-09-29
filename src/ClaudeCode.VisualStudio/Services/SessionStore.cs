using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ClaudeCode.VisualStudio.Services
{
    public sealed class StoredMessage
    {
        public string Role { get; set; }   // "user" | "assistant"
        public string Text { get; set; }
    }

    public sealed class SessionRecord
    {
        public string TabId { get; set; }      // which tab this record belongs to
        public string TabTitle { get; set; }   // user-visible tab label
        public string SessionId { get; set; }
        public string Model { get; set; } = "default";
        public string Mode { get; set; } = "default";
        public string Effort { get; set; } = "none";
        public bool ShowThinking { get; set; } = true;
        public List<StoredMessage> Messages { get; set; } = new List<StoredMessage>();
    }

    /// <summary>
    /// Wraps a list of tab sessions for a single working directory.
    /// </summary>
    public sealed class SessionBundle
    {
        public List<SessionRecord> Tabs { get; set; } = new List<SessionRecord>();
        public string ActiveTabId { get; set; }
    }

    /// <summary>
    /// Persists a chat session (id + options + transcript) per working directory so the
    /// conversation can be restored when the tool window or Visual Studio is reopened.
    /// Stored under %LOCALAPPDATA%\ClaudeCodeVS\sessions, one file per cwd.
    /// </summary>
    public static class SessionStore
    {
        private static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ClaudeCodeVS", "sessions");

        private const int MaxMessages = 200;

        private static string FileFor(string cwd)
        {
            var key = (cwd ?? string.Empty).Trim().ToLowerInvariant();
            using (var sha = SHA1.Create())
            {
                var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
                var sb = new StringBuilder();
                foreach (var b in hash) sb.Append(b.ToString("x2"));
                return Path.Combine(Dir, sb.ToString() + ".json");
            }
        }

        public static SessionRecord Load(string cwd)
        {
            try
            {
                var bundle = LoadBundle(cwd);
                return bundle?.Tabs?.Count > 0 ? bundle.Tabs[0] : null;
            }
            catch { return null; }
        }

        public static void Save(string cwd, SessionRecord rec)
        {
            if (rec == null) return;
            try
            {
                var bundle = LoadBundle(cwd) ?? new SessionBundle();
                var existing = bundle.Tabs.FindIndex(t => t.TabId == rec.TabId);
                if (existing >= 0) bundle.Tabs[existing] = rec;
                else bundle.Tabs.Add(rec);
                SaveBundle(cwd, bundle);
            }
            catch { }
        }

        public static void Clear(string cwd)
        {
            try { var p = FileFor(cwd); if (File.Exists(p)) File.Delete(p); }
            catch { }
        }

        public static void ClearTab(string cwd, string tabId)
        {
            try
            {
                var bundle = LoadBundle(cwd);
                if (bundle == null) return;
                bundle.Tabs.RemoveAll(t => t.TabId == tabId);
                if (bundle.Tabs.Count == 0) Clear(cwd);
                else SaveBundle(cwd, bundle);
            }
            catch { }
        }

        public static SessionBundle LoadBundle(string cwd)
        {
            try
            {
                var path = FileFor(cwd);
                if (!File.Exists(path)) return null;
                var json = ReadDecrypted(path);
                if (json == null) return null;
                // Try new bundle format first, fall back to legacy single-record format.
                if (json.TrimStart().StartsWith("{\"Tabs\"", StringComparison.OrdinalIgnoreCase) ||
                    json.TrimStart().StartsWith("{\"tabs\"", StringComparison.OrdinalIgnoreCase))
                    return JsonSerializer.Deserialize<SessionBundle>(json);
                // Legacy: single SessionRecord — wrap it in a bundle.
                var rec = JsonSerializer.Deserialize<SessionRecord>(json);
                if (rec == null) return null;
                if (rec.TabId == null) rec.TabId = "t1";
                return new SessionBundle { Tabs = new List<SessionRecord> { rec }, ActiveTabId = rec.TabId };
            }
            catch { return null; }
        }

        public static void SaveBundle(string cwd, SessionBundle bundle)
        {
            try
            {
                if (bundle == null) return;
                Directory.CreateDirectory(Dir);
                foreach (var rec in bundle.Tabs)
                    if (rec.Messages != null && rec.Messages.Count > MaxMessages)
                        rec.Messages.RemoveRange(0, rec.Messages.Count - MaxMessages);
                WriteEncrypted(FileFor(cwd), JsonSerializer.Serialize(bundle));
            }
            catch { }
        }

        // The transcript can contain anything discussed in chat (incl. secrets), so it is encrypted
        // at rest with DPAPI — per-user, machine-bound, no key management. A short magic prefix marks
        // an encrypted file; a file without it is read as legacy plaintext (written before encryption
        // was added) and silently re-encrypted on the next Save.
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("CCVS1\n");

        private static void WriteEncrypted(string path, string json)
        {
            try
            {
                // CryptProtectData (DPAPI) can block indefinitely on corporate machines when the
                // domain service is unreachable. Run it on a background thread with a timeout so
                // the caller is never permanently stuck. Fall back to plaintext on timeout.
                byte[] plain = Encoding.UTF8.GetBytes(json);
                byte[] cipher = null;
                try
                {
                    var t = System.Threading.Tasks.Task.Run(
                        () => ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser));
                    if (t.Wait(TimeSpan.FromSeconds(4)))
                        cipher = t.Result;
                }
                catch { }

                if (cipher != null)
                {
                    var buf = new byte[Magic.Length + cipher.Length];
                    Buffer.BlockCopy(Magic, 0, buf, 0, Magic.Length);
                    Buffer.BlockCopy(cipher, 0, buf, Magic.Length, cipher.Length);
                    File.WriteAllBytes(path, buf);
                }
                else
                {
                    // DPAPI unavailable or timed out — fall back to plaintext.
                    File.WriteAllText(path, json);
                }
            }
            catch
            {
                // Last-resort plaintext so the conversation still persists.
                try { File.WriteAllText(path, json); } catch { }
            }
        }

        private static string ReadDecrypted(string path)
        {
            var bytes = File.ReadAllBytes(path);
            if (HasMagic(bytes))
            {
                var cipher = new byte[bytes.Length - Magic.Length];
                Buffer.BlockCopy(bytes, Magic.Length, cipher, 0, cipher.Length);
                var plain = ProtectedData.Unprotect(cipher, null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(plain);
            }
            // Legacy plaintext JSON (pre-encryption). Read as-is; the next Save upgrades it.
            return Encoding.UTF8.GetString(bytes);
        }

        private static bool HasMagic(byte[] b)
        {
            if (b.Length < Magic.Length) return false;
            for (int i = 0; i < Magic.Length; i++) if (b[i] != Magic[i]) return false;
            return true;
        }
    }
}
