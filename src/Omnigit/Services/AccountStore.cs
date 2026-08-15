using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Omnigit.HostProviders;

namespace Omnigit.Services;

/// <summary>Remembers which sites the user has signed in to.</summary>
public interface IAccountStore
{
    /// <summary>
    /// Loads saved accounts, fetching each token from the credential store. Accounts
    /// whose token has vanished (keyring cleared, revoked elsewhere) are dropped.
    /// </summary>
    Task<IReadOnlyList<HostAccount>> LoadAsync();

    Task SaveAsync(HostAccount account);
    Task RemoveAsync(HostAccount account);
}

/// <summary>
/// Splits an account in two: the harmless parts go in JSON next to the other settings,
/// and the token goes to <see cref="ICredentialStore"/>. Nothing here ever writes a
/// token to disk in plain text.
/// </summary>
public sealed class AccountStore(ICredentialStore credentials) : IAccountStore
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _file = AppPaths.In("accounts.json");

    public async Task<IReadOnlyList<HostAccount>> LoadAsync()
    {
        var records = Read();
        var accounts = new List<HostAccount>(records.Count);

        foreach (var record in records)
        {
            if (!Uri.TryCreate(record.BaseUrl, UriKind.Absolute, out var baseUrl))
                continue;

            var key = $"{record.ProviderId}|{baseUrl.Host}|{record.Login}";
            var token = await credentials.GetAsync(key);

            // No token means the sign-in is effectively gone; don't surface a
            // half-working account.
            if (string.IsNullOrEmpty(token))
                continue;

            accounts.Add(new HostAccount
            {
                ProviderId = record.ProviderId,
                BaseUrl = baseUrl,
                Login = record.Login,
                DisplayName = record.DisplayName,
                AvatarUrl = record.AvatarUrl,
                Token = token,
            });
        }

        return accounts;
    }

    public async Task SaveAsync(HostAccount account)
    {
        await credentials.SetAsync(account.Key, account.Token);

        var records = Read();
        records.RemoveAll(r => RecordKey(r) == account.Key);

        records.Add(new AccountRecord
        {
            ProviderId = account.ProviderId,
            BaseUrl = account.BaseUrl.ToString(),
            Login = account.Login,
            DisplayName = account.DisplayName,
            AvatarUrl = account.AvatarUrl,
        });

        Write(records);
    }

    public async Task RemoveAsync(HostAccount account)
    {
        await credentials.DeleteAsync(account.Key);

        var records = Read();
        records.RemoveAll(r => RecordKey(r) == account.Key);
        Write(records);
    }

    private static string RecordKey(AccountRecord r)
        => Uri.TryCreate(r.BaseUrl, UriKind.Absolute, out var uri)
            ? $"{r.ProviderId}|{uri.Host}|{r.Login}"
            : $"{r.ProviderId}||{r.Login}";

    private List<AccountRecord> Read()
    {
        try
        {
            if (!File.Exists(_file))
                return [];

            return JsonSerializer.Deserialize<List<AccountRecord>>(File.ReadAllText(_file), Json) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private void Write(List<AccountRecord> records)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
            File.WriteAllText(_file, JsonSerializer.Serialize(records, Json));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Losing the account list is recoverable by signing in again.
        }
    }

    /// <summary>The non-secret half of an account. Deliberately has no token field.</summary>
    public sealed class AccountRecord
    {
        public string ProviderId { get; set; } = string.Empty;
        public string BaseUrl { get; set; } = string.Empty;
        public string Login { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string? AvatarUrl { get; set; }
    }
}
