using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace SuperPan.Licensing.Client;

public interface ILicenseStateProtector
{
    byte[] Protect(byte[] plaintext);
    byte[] Unprotect(byte[] ciphertext);
}

public interface ILicenseStateStore
{
    Task<LicenseStoreSnapshot> ReadAsync(CancellationToken cancellationToken);
    Task<StoredLicenseCredential> GetOrCreateCredentialAsync(string card, CancellationToken cancellationToken);
    Task SaveLeaseAsync(string card, string activationRequestId, StoredLicenseLease lease, CancellationToken cancellationToken);
    Task RemoveLeaseAsync(string card, string softwareId, string machineHash, CancellationToken cancellationToken);
}

public sealed class LicenseStoreException : Exception
{
    public LicenseStoreException() : base("The protected license state could not be accessed.") { }
}

public sealed record StoredLicenseLease(
    string CardId,
    string SoftwareId,
    string MachineHash,
    string PackageName,
    DateTimeOffset ExpiresAt,
    DateTimeOffset LeaseUntil,
    string SignedPayload)
{
    public override string ToString() =>
        $"StoredLicenseLease {{ Software = configured, Package = configured, ExpiresAt = {ExpiresAt:O}, LeaseUntil = {LeaseUntil:O} }}";
}

public sealed record StoredLicenseCredential
{
    public StoredLicenseCredential(string card, string activationRequestId, IReadOnlyList<StoredLicenseLease> leases)
    {
        Card = card;
        ActivationRequestId = activationRequestId;
        Leases = leases?.ToArray() ?? throw new ArgumentNullException(nameof(leases));
    }

    public string Card { get; }
    public string ActivationRequestId { get; }
    public IReadOnlyList<StoredLicenseLease> Leases { get; }
    public override string ToString() => $"StoredLicenseCredential {{ LeaseCount = {Leases.Count} }}";
}

public sealed record LicenseStoreSnapshot
{
    public LicenseStoreSnapshot(IReadOnlyList<StoredLicenseCredential> credentials) =>
        Credentials = credentials?.ToArray() ?? throw new ArgumentNullException(nameof(credentials));

    public IReadOnlyList<StoredLicenseCredential> Credentials { get; }
    public override string ToString() => $"LicenseStoreSnapshot {{ CredentialCount = {Credentials.Count} }}";
}

public sealed class DpapiLicenseStore : ILicenseStateStore
{
    public const string ProtectionPurpose = "SuperPan.Licensing.State.v1";
    public const int MaximumFileBytes = 1024 * 1024;
    public const int MaximumCredentialCount = 32;
    public const int MaximumLeaseCountPerCredential = 32;
    private const int MaximumPlaintextBytes = 768 * 1024;
    private const int MaximumCardBytes = 256;
    private const int MaximumPayloadBytes = 128 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        MaxDepth = 16,
    };
    private readonly string path;
    private readonly ILicenseStateProtector protector;
    private readonly string mutexName;

    public DpapiLicenseStore(string? path = null, ILicenseStateProtector? protector = null)
    {
        this.path = Path.GetFullPath(path ?? GetDefaultPath());
        this.protector = protector ?? DpapiCurrentUserProtector.Instance;
        mutexName = @"Local\SuperPan.Licensing.State.v1." +
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(this.path.ToUpperInvariant()))).ToLowerInvariant();
    }

    public static string GetDefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SuperPan", "Licensing", "v1", "licenses.dat");

    public Task<LicenseStoreSnapshot> ReadAsync(CancellationToken cancellationToken) =>
        ExecuteAsync(() => LoadUnsafe(), cancellationToken);

    public Task<StoredLicenseCredential> GetOrCreateCredentialAsync(string card, CancellationToken cancellationToken) =>
        ExecuteAsync(() =>
        {
            ValidateCard(card);
            var snapshot = LoadUnsafe();
            var found = snapshot.Credentials.FirstOrDefault(item => string.Equals(item.Card, card, StringComparison.Ordinal));
            if (found is not null)
            {
                return found;
            }
            if (snapshot.Credentials.Count >= MaximumCredentialCount)
            {
                throw new LicenseStoreException();
            }
            var credential = new StoredLicenseCredential(card, Guid.NewGuid().ToString("D"), []);
            SaveUnsafe(new LicenseStoreSnapshot(snapshot.Credentials.Append(credential).ToArray()));
            return credential;
        }, cancellationToken);

    public Task SaveLeaseAsync(string card, string activationRequestId, StoredLicenseLease lease, CancellationToken cancellationToken) =>
        ExecuteAsync(() =>
        {
            ValidateCard(card);
            ValidateActivationId(activationRequestId);
            ValidateLease(lease);
            var snapshot = LoadUnsafe();
            var found = false;
            var credentials = snapshot.Credentials.Select(credential =>
            {
                if (!string.Equals(credential.Card, card, StringComparison.Ordinal)) return credential;
                found = true;
                if (!string.Equals(credential.ActivationRequestId, activationRequestId, StringComparison.Ordinal))
                    throw new LicenseStoreException();
                var leases = credential.Leases
                    .Where(item => !string.Equals(item.SoftwareId, lease.SoftwareId, StringComparison.Ordinal) ||
                                   !string.Equals(item.MachineHash, lease.MachineHash, StringComparison.Ordinal))
                    .Append(lease)
                    .ToArray();
                if (leases.Length > MaximumLeaseCountPerCredential) throw new LicenseStoreException();
                return new StoredLicenseCredential(card, activationRequestId, leases);
            }).ToArray();
            if (!found) throw new LicenseStoreException();
            SaveUnsafe(new LicenseStoreSnapshot(credentials));
            return true;
        }, cancellationToken);

    public Task RemoveLeaseAsync(string card, string softwareId, string machineHash, CancellationToken cancellationToken) =>
        ExecuteAsync(() =>
        {
            ValidateCard(card);
            ValidateIdentifier(softwareId);
            ValidateIdentifier(machineHash);
            var snapshot = LoadUnsafe();
            var credentials = snapshot.Credentials.Select(credential =>
                !string.Equals(credential.Card, card, StringComparison.Ordinal)
                    ? credential
                    : new StoredLicenseCredential(
                        credential.Card,
                        credential.ActivationRequestId,
                        credential.Leases.Where(lease =>
                            !string.Equals(lease.SoftwareId, softwareId, StringComparison.Ordinal) ||
                            !string.Equals(lease.MachineHash, machineHash, StringComparison.Ordinal)).ToArray())).ToArray();
            SaveUnsafe(new LicenseStoreSnapshot(credentials));
            return true;
        }, cancellationToken);

    public static bool HasRestrictiveAcl(string path)
    {
        if (!OperatingSystem.IsWindows()) return false;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var sid = identity.User;
            if (sid is null) return false;
            var security = new FileInfo(path).GetAccessControl(AccessControlSections.Access);
            if (!security.AreAccessRulesProtected) return false;
            var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier));
            return rules.Cast<FileSystemAccessRule>().All(rule => sid.Equals(rule.IdentityReference));
        }
        catch
        {
            return false;
        }
    }

    public override string ToString() => nameof(DpapiLicenseStore);

    private Task<T> ExecuteAsync<T>(Func<T> action, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var mutex = new Mutex(false, mutexName);
            var acquired = false;
            try
            {
                while (!acquired)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        acquired = mutex.WaitOne(TimeSpan.FromMilliseconds(100));
                    }
                    catch (AbandonedMutexException)
                    {
                        acquired = true;
                    }
                }
                return Task.FromResult(action());
            }
            finally
            {
                if (acquired) mutex.ReleaseMutex();
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (LicenseStoreException)
        {
            throw;
        }
        catch
        {
            throw new LicenseStoreException();
        }
    }

    private LicenseStoreSnapshot LoadUnsafe()
    {
        if (!File.Exists(path)) return new LicenseStoreSnapshot([]);
        var info = new FileInfo(path);
        if (info.Length is < 1 or > MaximumFileBytes) throw new LicenseStoreException();
        var ciphertext = File.ReadAllBytes(path);
        var plaintext = protector.Unprotect(ciphertext);
        if (plaintext.Length is < 2 or > MaximumPlaintextBytes) throw new LicenseStoreException();
        try
        {
            var document = JsonSerializer.Deserialize<StateDocument>(plaintext, JsonOptions) ?? throw new JsonException();
            if (document.Version != 1 || document.Credentials is null || document.Credentials.Count > MaximumCredentialCount)
                throw new LicenseStoreException();
            var credentials = document.Credentials.Select(FromDto).ToArray();
            return new LicenseStoreSnapshot(credentials);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private void SaveUnsafe(LicenseStoreSnapshot snapshot)
    {
        ValidateSnapshot(snapshot);
        var document = new StateDocument(1, snapshot.Credentials.Select(ToDto).ToList());
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(document, JsonOptions);
        try
        {
            if (plaintext.Length > MaximumPlaintextBytes) throw new LicenseStoreException();
            var ciphertext = protector.Protect(plaintext);
            if (ciphertext.Length is < 1 or > MaximumFileBytes) throw new LicenseStoreException();
            var directory = Path.GetDirectoryName(path) ?? throw new LicenseStoreException();
            Directory.CreateDirectory(directory);
            RestrictDirectoryAcl(directory);
            var temporary = Path.Combine(directory, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllBytes(temporary, ciphertext);
                RestrictFileAcl(temporary);
                if (File.Exists(path))
                {
                    File.Replace(temporary, path, null, ignoreMetadataErrors: false);
                }
                else
                {
                    File.Move(temporary, path);
                }
                RestrictFileAcl(path);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
                CryptographicOperations.ZeroMemory(ciphertext);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static void RestrictDirectoryAcl(string directory)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User ?? throw new LicenseStoreException();
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(sid);
        security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(directory).SetAccessControl(security);
    }

    private static void RestrictFileAcl(string file)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User ?? throw new LicenseStoreException();
        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(sid);
        security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(file).SetAccessControl(security);
    }

    private static StoredLicenseCredential FromDto(CredentialDto dto)
    {
        ValidateCard(dto.Card);
        ValidateActivationId(dto.ActivationRequestId);
        if (dto.Leases is null || dto.Leases.Count > MaximumLeaseCountPerCredential) throw new LicenseStoreException();
        var leases = dto.Leases.Select(item => new StoredLicenseLease(item.CardId, item.SoftwareId, item.MachineHash,
            item.PackageName, item.ExpiresAt, item.LeaseUntil, item.SignedPayload)).ToArray();
        foreach (var lease in leases) ValidateLease(lease);
        return new StoredLicenseCredential(dto.Card, dto.ActivationRequestId, leases);
    }

    private static CredentialDto ToDto(StoredLicenseCredential credential) => new(
        credential.Card,
        credential.ActivationRequestId,
        credential.Leases.Select(lease => new LeaseDto(lease.CardId, lease.SoftwareId, lease.MachineHash,
            lease.PackageName, lease.ExpiresAt, lease.LeaseUntil, lease.SignedPayload)).ToList());

    private static void ValidateSnapshot(LicenseStoreSnapshot snapshot)
    {
        if (snapshot.Credentials.Count > MaximumCredentialCount) throw new LicenseStoreException();
        foreach (var credential in snapshot.Credentials)
        {
            ValidateCard(credential.Card);
            ValidateActivationId(credential.ActivationRequestId);
            if (credential.Leases.Count > MaximumLeaseCountPerCredential) throw new LicenseStoreException();
            foreach (var lease in credential.Leases) ValidateLease(lease);
        }
    }

    private static void ValidateCard(string card)
    {
        if (string.IsNullOrEmpty(card) || card != card.Trim() || Encoding.UTF8.GetByteCount(card) > MaximumCardBytes)
            throw new LicenseStoreException();
    }

    private static void ValidateActivationId(string value)
    {
        if (!Guid.TryParseExact(value, "D", out _)) throw new LicenseStoreException();
    }

    private static void ValidateLease(StoredLicenseLease lease)
    {
        ArgumentNullException.ThrowIfNull(lease);
        ValidateIdentifier(lease.CardId);
        ValidateIdentifier(lease.SoftwareId);
        ValidateIdentifier(lease.MachineHash);
        ValidateIdentifier(lease.PackageName);
        if (lease.ExpiresAt <= DateTimeOffset.UnixEpoch || lease.LeaseUntil <= DateTimeOffset.UnixEpoch ||
            lease.LeaseUntil > lease.ExpiresAt || string.IsNullOrEmpty(lease.SignedPayload) ||
            Encoding.UTF8.GetByteCount(lease.SignedPayload) > MaximumPayloadBytes)
            throw new LicenseStoreException();
    }

    private static void ValidateIdentifier(string value)
    {
        if (string.IsNullOrEmpty(value) || value != value.Trim() || Encoding.UTF8.GetByteCount(value) > LicenseProtocol.MaxIdentifierLength)
            throw new LicenseStoreException();
    }

    private sealed record StateDocument(int Version, List<CredentialDto> Credentials);
    private sealed record CredentialDto(string Card, string ActivationRequestId, List<LeaseDto> Leases);
    private sealed record LeaseDto(string CardId, string SoftwareId, string MachineHash, string PackageName,
        DateTimeOffset ExpiresAt, DateTimeOffset LeaseUntil, string SignedPayload);

    private sealed class DpapiCurrentUserProtector : ILicenseStateProtector
    {
        public static readonly DpapiCurrentUserProtector Instance = new();
        private static readonly byte[] Entropy = SHA256.HashData(Encoding.UTF8.GetBytes(ProtectionPurpose));

        public byte[] Protect(byte[] plaintext)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            return ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
        }

        public byte[] Unprotect(byte[] ciphertext)
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            return ProtectedData.Unprotect(ciphertext, Entropy, DataProtectionScope.CurrentUser);
        }
    }
}
