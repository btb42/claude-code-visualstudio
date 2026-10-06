using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace ClaudeCode.VisualStudio.Services
{
    public sealed class StoredMessage
    {
        public string Role { get; set; }   // "user" | "assistant"
        public string Text { get; set; }
        public string Id { get; set; }     // short GUID, set for assistant messages only
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
        public int ForkWindowSize { get; set; } = 6;
        public bool ForkAllMessages { get; set; } = false;
        public string ClosedAt { get; set; }
        public List<StoredMessage> Messages { get; set; } = new List<StoredMessage>();
    }

    /// <summary>
    /// Wraps a list of tab sessions for a single working directory.
    /// </summary>
    public sealed class SessionBundle
    {
        public List<SessionRecord> Tabs { get; set; } = new List<SessionRecord>();
        public string ActiveTabId { get; set; }
        public List<SessionRecord> ClosedTabs { get; set; } = new List<SessionRecord>();
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

        // Per-file mutexes for in-process serialization; cross-process protection is handled
        // by the FileStream exclusive lock in WriteEncrypted/ReadDecrypted.
        private static readonly Dictionary<string, SemaphoreSlim> _fileLocks =
            new Dictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);
        private static readonly object _lockMapGuard = new object();

        private static SemaphoreSlim GetFileLock(string path)
        {
            lock (_lockMapGuard)
            {
                if (!_fileLocks.TryGetValue(path, out var sem))
                    _fileLocks[path] = sem = new SemaphoreSlim(1, 1);
                return sem;
            }
        }

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
            var path = FileFor(cwd);
            var sem = GetFileLock(path);
            sem.Wait();
            try
            {
                var bundle = LoadBundleNoLock(path) ?? new SessionBundle();
                var existing = bundle.Tabs.FindIndex(t => t.TabId == rec.TabId);
                if (existing >= 0) bundle.Tabs[existing] = rec;
                else bundle.Tabs.Add(rec);
                SaveBundleNoLock(path, bundle);
            }
            catch { }
            finally { sem.Release(); }
        }

        public static void Clear(string cwd)
        {
            try { var p = FileFor(cwd); if (File.Exists(p)) File.Delete(p); }
            catch { }
        }

        public static void ClearTab(string cwd, string tabId)
        {
            var path = FileFor(cwd);
            var sem = GetFileLock(path);
            sem.Wait();
            try
            {
                var bundle = LoadBundleNoLock(path);
                if (bundle == null) return;
                bundle.Tabs.RemoveAll(t => t.TabId == tabId);
                if (bundle.Tabs.Count == 0) { try { File.Delete(path); } catch { } }
                else SaveBundleNoLock(path, bundle);
            }
            catch { }
            finally { sem.Release(); }
        }

        public static SessionBundle LoadBundle(string cwd)
        {
            var path = FileFor(cwd);
            var sem = GetFileLock(path);
            sem.Wait();
            try { return LoadBundleNoLock(path); }
            finally { sem.Release(); }
        }

        public static void SaveBundle(string cwd, SessionBundle bundle)
        {
            if (bundle == null) return;
            var path = FileFor(cwd);
            var sem = GetFileLock(path);
            sem.Wait();
            try { SaveBundleNoLock(path, bundle); }
            catch { }
            finally { sem.Release(); }
        }

        private static SessionBundle LoadBundleNoLock(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                var json = ReadDecrypted(path);
                if (json == null) return null;
                SessionBundle bundle;
                if (json.TrimStart().StartsWith("{\"Tabs\"", StringComparison.OrdinalIgnoreCase) ||
                    json.TrimStart().StartsWith("{\"tabs\"", StringComparison.OrdinalIgnoreCase))
                {
                    bundle = JsonSerializer.Deserialize<SessionBundle>(json);
                }
                else
                {
                    var rec = JsonSerializer.Deserialize<SessionRecord>(json);
                    if (rec == null) return null;
                    if (rec.TabId == null) rec.TabId = "t1";
                    bundle = new SessionBundle { Tabs = new List<SessionRecord> { rec }, ActiveTabId = rec.TabId };
                }
                BackfillMessageIds(bundle);
                return bundle;
            }
            catch { return null; }
        }

        // Older sessions were stored before StoredMessage.Id existed; give every assistant
        // message a stable id so fork buttons resolve to the right message instead of the tail.
        private static void BackfillMessageIds(SessionBundle bundle)
        {
            if (bundle?.Tabs == null) return;
            foreach (var rec in bundle.Tabs)
            {
                if (rec?.Messages == null) continue;
                foreach (var m in rec.Messages)
                    if (m.Role == "assistant" && string.IsNullOrEmpty(m.Id))
                        m.Id = Guid.NewGuid().ToString("N").Substring(0, 8);
            }
        }

        private static void SaveBundleNoLock(string path, SessionBundle bundle)
        {
            Directory.CreateDirectory(Dir);
            foreach (var rec in bundle.Tabs)
                if (rec.Messages != null && rec.Messages.Count > MaxMessages)
                    rec.Messages.RemoveRange(0, rec.Messages.Count - MaxMessages);
            if (bundle.ClosedTabs != null)
                foreach (var rec in bundle.ClosedTabs)
                    if (rec.Messages != null && rec.Messages.Count > MaxMessages)
                        rec.Messages.RemoveRange(0, rec.Messages.Count - MaxMessages);
            WriteEncrypted(path, JsonSerializer.Serialize(bundle));
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

                byte[] buf;
                if (cipher != null)
                {
                    buf = new byte[Magic.Length + cipher.Length];
                    Buffer.BlockCopy(Magic, 0, buf, 0, Magic.Length);
                    Buffer.BlockCopy(cipher, 0, buf, Magic.Length, cipher.Length);
                }
                else
                {
                    // DPAPI unavailable or timed out — fall back to plaintext.
                    buf = Encoding.UTF8.GetBytes(json);
                }

                // Cross-process exclusive lock: open with FileShare.None so a second VS instance
                // cannot read a half-written file, then write atomically via a temp file + replace.
                var tmp = path + ".tmp";
                File.WriteAllBytes(tmp, buf);
                using (var fs = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None))
                {
                    fs.SetLength(0);
                    fs.Write(buf, 0, buf.Length);
                }
                // Clean up temp file (only used to prepare buf before the exclusive open).
                try { File.Delete(tmp); } catch { }
            }
            catch
            {
                // Last-resort plaintext so the conversation still persists.
                try { File.WriteAllText(path, json); } catch { }
            }
        }

        private static string ReadDecrypted(string path)
        {
            byte[] bytes;
            // Use FileShare.Read so concurrent readers are fine, but writers are excluded.
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                bytes = new byte[fs.Length];
                fs.Read(bytes, 0, bytes.Length);
            }
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
