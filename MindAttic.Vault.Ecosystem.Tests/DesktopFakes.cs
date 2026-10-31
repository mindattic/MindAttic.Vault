using MindAttic.Vault.Credentials;
using MindAttic.Vault.Desktop.Services;

namespace MindAttic.Vault.Ecosystem.Tests;

/// <summary>The minimum Vault.Desktop needs to run its view models without a window: no dialogs, inline UI, temp health and audit.</summary>
public static class DesktopFakes
{
    public static DesktopContext Context(string localRoot) =>
        new(new SilentDialogs(), new InlineUi(), new KeyHealthStore(Path.Combine(localRoot, KeyAssignmentStore.Bucket)),
            TimeProvider.System, new SqliteAuditLog(Path.Combine(localRoot, KeyAssignmentStore.Bucket, SqliteAuditLog.FileName)));

    /// <summary>Answers every question "no" and records what it was shown.</summary>
    public sealed class SilentDialogs : IDialogs
    {
        public List<string> Shown { get; } = new();
        public bool Confirm(string message, string caption) { Shown.Add(caption); return false; }
        public void Error(string message, string caption) => Shown.Add(caption);
        public void Info(string message, string caption) => Shown.Add(caption);
        public void OpenUrl(string url) => Shown.Add("open url");
    }

    public sealed class InlineUi : IUiDispatcher
    {
        private readonly object gate = new();
        public void Post(Action action) { lock (gate) action(); }
    }

    /// <summary>Never calls a provider.</summary>
    public sealed class NoKeyTester : IKeyTester
    {
        public bool CanTest(string providerId) => false;
        public Task<KeyTestResult> TestAsync(string providerId, string key, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Ecosystem tests never call a provider.");
    }
}
