using BaiduBatchTransfer.Core.Abstractions;
using BaiduBatchTransfer.Infrastructure.Database;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace BaiduBatchTransfer.App.Tests;

public static class AccountManagementAcceptanceSeeder
{
    private static readonly Guid AccountA = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid AccountB = Guid.Parse("10000000-0000-0000-0000-000000000002");
    private static readonly Guid AccountC = Guid.Parse("10000000-0000-0000-0000-000000000003");
    private static readonly DateTimeOffset Now = new(2026, 8, 20, 10, 0, 0, TimeSpan.Zero);

    public static async Task SeedAsync(
        string acceptanceRoot,
        string scenario,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(acceptanceRoot);
        if (!IsSupportedScenario(scenario))
            throw new ArgumentOutOfRangeException(nameof(scenario), "Unsupported acceptance scenario.");
        if (!Path.IsPathFullyQualified(acceptanceRoot))
            throw new ArgumentException("The acceptance root must be absolute.", nameof(acceptanceRoot));

        var fullRoot = Path.GetFullPath(acceptanceRoot);
        var fullTemp = Path.GetFullPath(Path.GetTempPath())
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        if (!fullRoot.StartsWith(fullTemp, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                "The acceptance root must be a strict child of the system temp directory.",
                nameof(acceptanceRoot));
        if (Directory.Exists(fullRoot) || File.Exists(fullRoot))
            throw new InvalidOperationException("The acceptance root must not already exist.");

        Directory.CreateDirectory(fullRoot);
        try
        {
            using var host = AppBootstrap.CreateHost(fullRoot);
            await host.Services.GetRequiredService<DatabaseMigrator>()
                .MigrateAsync(cancellationToken);
            await SeedRepositoryGraphAsync(
                host.Services.GetRequiredService<IUnitOfWorkFactory>(),
                scenario,
                cancellationToken);
            await File.WriteAllTextAsync(
                Path.Combine(fullRoot, StartupDataRootSelector.AcceptanceMarkerFileName),
                StartupDataRootSelector.AcceptanceMarkerHeader + $"\nscenario={scenario}\n",
                cancellationToken);
        }
        catch
        {
            // Deliberately leave an unmarked root behind. The Debug app refuses it,
            // and the operator can inspect or delete the exact disposable path.
            throw;
        }
    }

    private static async Task SeedRepositoryGraphAsync(
        IUnitOfWorkFactory unitOfWorkFactory,
        string scenario,
        CancellationToken cancellationToken)
    {
        await using var unit = await unitOfWorkFactory.BeginAsync(cancellationToken);
        await unit.Accounts.UpsertAsync(Account(AccountA, "acceptance-a", "账号 A", 1), cancellationToken);
        await unit.Accounts.UpsertAsync(Account(AccountB, "acceptance-b", "账号 B", 2), cancellationToken);
        await unit.Accounts.UpsertAsync(Account(AccountC, "acceptance-c", "账号 C", 3), cancellationToken);

        switch (scenario)
        {
            case "keep-history":
                await AddTerminalBatchAsync(unit.Jobs, "保留历史-B", [AccountB], cancellationToken);
                break;
            case "cleanup-history":
                await AddTerminalBatchAsync(unit.Jobs, "独立历史-B", [AccountB], cancellationToken);
                await AddTerminalBatchAsync(unit.Jobs, "共享历史-A-B-C", [AccountA, AccountB, AccountC], cancellationToken);
                break;
            case "active-work":
                await AddActiveAllocationWithoutOperationAsync(unit.Jobs, cancellationToken);
                break;
        }

        await unit.CommitAsync(cancellationToken);
    }

    private static AccountRecord Account(Guid id, string baiduUserId, string displayName, int sortOrder) =>
        AccountRecord.Create(id, baiduUserId, displayName) with
        {
            EncryptedSession = [1, 2, 3],
            LastValidatedAt = Now,
            SortOrder = sortOrder,
        };

    private static async Task AddTerminalBatchAsync(
        IJobRepository jobs,
        string name,
        IReadOnlyList<Guid> accounts,
        CancellationToken cancellationToken)
    {
        var batchId = Guid.NewGuid();
        var completedAt = Now.AddMinutes(30);
        await jobs.AddAsync(new BatchJobRecord(
            batchId, name, "Text", "Distribution", "TaskCount", "ProportionalReduction",
            accounts.Count, accounts.Count, "/验收", "PerItem", "Skip", "Immediate", "Completed",
            Now, Now, completedAt, null, 1), cancellationToken);

        var resources = new List<ResourceItemRecord>(accounts.Count);
        var operations = new List<TransferOperationRecord>(accounts.Count);
        var allocations = new List<AccountAllocationRecord>(accounts.Count);
        for (var index = 0; index < accounts.Count; index++)
        {
            var accountId = accounts[index];
            var sequence = index + 1;
            var resourceId = Guid.NewGuid();
            var operationId = Guid.NewGuid();
            resources.Add(new ResourceItemRecord(
                resourceId, batchId, null, sequence, $"https://acceptance.invalid/{resourceId:N}",
                $"acceptance-{resourceId:N}", null, "acceptance", null, null, null, "Inherit",
                "Valid", accountId, "Expanded", null, null, Now, completedAt));
            operations.Add(new TransferOperationRecord(
                operationId, resourceId, batchId, sequence, sequence, "/验收", "/验收", string.Empty,
                accountId, "Fixed", "Succeeded", "NewTransfer", 1, $"acceptance-{operationId:N}",
                "{}", null, null, "ResultReceived", "Fulfilled", null, completedAt, null,
                null, null, null, Now, completedAt));
            allocations.Add(new AccountAllocationRecord(
                Guid.NewGuid(), batchId, accountId, sequence, "/验收", "/验收",
                1, 1, 1, 1, 1, 1, 0, 1, 0, 0, 1, 0, 0, 1));
        }

        await jobs.AddResourcesAsync(resources, cancellationToken);
        await jobs.AddOperationsAsync(operations, cancellationToken);
        await jobs.AddAllocationsAsync(allocations, cancellationToken);
    }

    private static async Task AddActiveAllocationWithoutOperationAsync(
        IJobRepository jobs,
        CancellationToken cancellationToken)
    {
        var batchId = Guid.NewGuid();
        await jobs.AddAsync(new BatchJobRecord(
            batchId, "活动分配-B", "Text", "Distribution", "TaskCount", "ProportionalReduction",
            0, 0, "/验收", "PerItem", "Skip", "Immediate", "Queued",
            Now, null, null, null, 1), cancellationToken);
        await jobs.AddAllocationsAsync(
            [new AccountAllocationRecord(
                Guid.NewGuid(), batchId, AccountB, 1, "/验收", "/验收",
                1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0)],
            cancellationToken);
    }

    private static bool IsSupportedScenario(string value) =>
        value is "keep-history" or "cleanup-history" or "active-work";
}

public sealed class AccountManagementAcceptanceSeedEntry(ITestOutputHelper output)
{
    [Fact]
    [Trait("Category", "ManualAcceptanceSeeder")]
    public async Task SeedAccountManagementAcceptanceDatabase()
    {
        var root = Environment.GetEnvironmentVariable("BBT_ACCEPTANCE_ROOT");
        var scenario = Environment.GetEnvironmentVariable("BBT_ACCEPTANCE_SCENARIO");
        if (root is null && scenario is null) return;
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(scenario))
            throw new InvalidOperationException(
                "BBT_ACCEPTANCE_ROOT and BBT_ACCEPTANCE_SCENARIO must both be set.");

        await AccountManagementAcceptanceSeeder.SeedAsync(root, scenario, CancellationToken.None);
        output.WriteLine(Path.GetFullPath(root));
        output.WriteLine(scenario);
    }
}
