using MindAttic.Vault.Credentials;
using MindAttic.Vault.Desktop.Profiles;

namespace MindAttic.Vault.Desktop.Services;

/// <summary>
/// Every change to the shared named keys, for the Keys tab and Get Started alike: writes through the Profile's
/// <see cref="IKeyBackend"/>, keeps key health following the key (rename moves it, a new value or a removal clears it),
/// audits by name (a value only as "changed"), and turns refusals into plain messages. Values pass through only as
/// method arguments, never into a property, log line or audit row.
/// </summary>
public sealed class KeyOperations(DesktopContext context, IKeyTester tester)
{
    /// <summary>Raised after any successful change, so tabs that show keys reload.</summary>
    public event EventHandler? Changed;

    public IKeyTester Tester => tester;

    public Task<bool> AddAsync(IProfile profile, string providerId, string name, string secret) =>
        RunAsync(profile, providerId, "add the key", async (keys, provider) =>
        {
            if (string.IsNullOrWhiteSpace(secret)) throw new ArgumentException("Paste the key's value first.");
            var existing = await keys.GetKeysAsync(provider).ConfigureAwait(true);
            var clean = NamedKeyStore.ValidateName(name, existing, except: null);
            await keys.AddAsync(provider, clean, secret.Trim()).ConfigureAwait(true);
            context.Health.Remove(provider, clean); // a reused name starts with no history
            context.Record(profile, provider, AuditActions.KeyAdd, new[] { clean }, "value set");
        });

    public Task<bool> SetValueAsync(IProfile profile, string providerId, string name, string secret) =>
        RunAsync(profile, providerId, "save the key", async (keys, provider) =>
        {
            if (string.IsNullOrWhiteSpace(secret)) throw new ArgumentException("A key can't be empty; remove it instead.");
            await keys.SetValueAsync(provider, name, secret.Trim()).ConfigureAwait(true);
            context.Health.Remove(provider, name); // a new value hasn't failed (or worked) yet
            context.Record(profile, provider, AuditActions.KeyValue, new[] { name }, "value changed");
        });

    public Task<bool> RenameAsync(IProfile profile, string providerId, string oldName, string newName) =>
        RunAsync(profile, providerId, "rename the key", async (keys, provider) =>
        {
            var existing = await keys.GetKeysAsync(provider).ConfigureAwait(true);
            var current = existing.FirstOrDefault(k => string.Equals(k.Name, oldName, StringComparison.OrdinalIgnoreCase))?.Name ?? oldName;
            var clean = NamedKeyStore.ValidateName(newName, existing, except: current);
            if (clean == current) return;
            await keys.RenameAsync(provider, current, clean).ConfigureAwait(true);
            context.Health.Rename(provider, current, clean);
            context.Record(profile, provider, AuditActions.KeyRename, new[] { current, clean });
        });

    public Task<bool> SetDisabledAsync(IProfile profile, string providerId, string name, bool disabled) =>
        RunAsync(profile, providerId, disabled ? "disable the key" : "enable the key", async (keys, provider) =>
        {
            await keys.SetDisabledAsync(provider, name, disabled).ConfigureAwait(true);
            context.Record(profile, provider, disabled ? AuditActions.KeyDisable : AuditActions.KeyEnable, new[] { name });
        });

    /// <summary>Removes a key after asking. A key an app still has assigned is refused with the apps' names.</summary>
    public async Task<bool> RemoveAsync(IProfile profile, string providerId, string name)
    {
        var display = ProviderCatalog.Get(providerId).DisplayName;
        if (!context.Dialogs.Confirm($"Remove the {display} key '{name}'? Apps that fall back to it will skip it.", "Remove key")) return false;
        return await RunAsync(profile, providerId, "remove the key", async (keys, provider) =>
        {
            await keys.RemoveAsync(provider, name).ConfigureAwait(true);
            context.Health.Remove(provider, name);
            context.Record(profile, provider, AuditActions.KeyRemove, new[] { name });
        }).ConfigureAwait(true);
    }

    /// <summary>Moves a key one place up (-1) or down (+1) in the shared failover order.</summary>
    public Task<bool> MoveAsync(IProfile profile, string providerId, string name, int delta) =>
        RunAsync(profile, providerId, "reorder the keys", async (keys, provider) =>
        {
            var names = (await keys.GetKeysAsync(provider).ConfigureAwait(true)).Select(k => k.Name).ToList();
            var i = names.FindIndex(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
            var j = i + delta;
            if (i < 0 || j < 0 || j >= names.Count) return;
            (names[i], names[j]) = (names[j], names[i]);
            await keys.ReorderAsync(provider, names).ConfigureAwait(true);
            context.Record(profile, provider, AuditActions.KeyReorder, names);
        });

    /// <summary>Tests a stored key with one cheap call and records the outcome in key health.</summary>
    public async Task<KeyTestResult> TestAsync(IProfile profile, string providerId, string name)
    {
        var provider = ProviderCatalog.Normalize(providerId);
        NamedKey? key;
        try
        {
            key = (await profile.Keys.GetKeysAsync(provider).ConfigureAwait(true))
                .FirstOrDefault(k => string.Equals(k.Name, name, StringComparison.OrdinalIgnoreCase));
        }
        catch (ProfileAccessDeniedException ex)
        {
            return new KeyTestResult(KeyTestOutcome.Inconclusive, ex.Message);
        }
        if (key is null) return new KeyTestResult(KeyTestOutcome.Inconclusive, $"No key named '{name}'.");
        var result = await tester.TestAsync(provider, key.Value).ConfigureAwait(true);
        Apply(provider, key.Name, result);
        DesktopLog.For("Keys").Information("Tested {Provider} key {Name}: {Outcome}", provider, key.Name, result.Outcome);
        Changed?.Invoke(this, EventArgs.Empty);
        return result;
    }

    /// <summary>Records a test's outcome: success resets the key's health and marks it used; a refusal marks it failed or cooling down.</summary>
    public void Apply(string providerId, string keyName, KeyTestResult result)
    {
        var now = context.Time.GetUtcNow();
        switch (result.Outcome)
        {
            case KeyTestOutcome.Ok:
                context.Health.Reset(providerId, keyName);
                context.Health.RecordSuccess(providerId, keyName, now);
                break;
            case KeyTestOutcome.KeyFailed:
                context.Health.RecordFailure(providerId, keyName, result.FailureKind ?? KeyFailureKind.Auth, now, result.RetryAfter);
                break;
        }
    }

    private async Task<bool> RunAsync(IProfile profile, string providerId, string what, Func<IKeyBackend, string, Task> change)
    {
        var provider = ProviderCatalog.Normalize(providerId);
        var display = ProviderCatalog.Get(provider).DisplayName;
        try
        {
            await change(profile.Keys, provider).ConfigureAwait(true);
        }
        catch (KeyInUseException ex)
        {
            context.Dialogs.Error($"'{ex.KeyName}' is assigned to {string.Join(", ", ex.Apps)}. Choose another key on those apps' tabs first.",
                $"Can't {what}");
            return false;
        }
        catch (ProfileAccessDeniedException ex)
        {
            context.Dialogs.Error(ex.Message, "Access denied");
            return false;
        }
        catch (Exception ex) when (ex is ArgumentException or KeyNotFoundException or InvalidOperationException)
        {
            // These messages name keys and rules, never values (NamedKeyStore's own wording).
            context.Dialogs.Error(ex.Message, $"Can't {what}");
            return false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            DesktopLog.For("Keys").Warning("Couldn't {What} for {Provider}: {Type}", what, provider, ex.GetType().Name);
            context.Dialogs.Error($"Couldn't {what} for {display}: {ex.GetType().Name}.", $"Can't {what}");
            return false;
        }
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }
}
