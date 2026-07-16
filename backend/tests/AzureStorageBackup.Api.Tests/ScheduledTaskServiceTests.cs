using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Tests;

public class ScheduledTaskServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly ScheduledTaskService _sut;

    public ScheduledTaskServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var encryption = new EncryptionService(new EphemeralDataProtectionProvider());
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        _db = new AppDbContext(options, encryption);
        _db.Database.EnsureCreated();
        _sut = new ScheduledTaskService(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private static ScheduledTask BackupTask() => new()
    {
        TargetKind = TaskTargetKind.Backup,
        AccountId = 1,
        ContainerName = "c1",
        TaskType = ScheduledTaskType.Backup,
        CronExpression = "0 2 * * *",
        Enabled = true
    };

    private static ScheduledTask GroupTask() => new()
    {
        TargetKind = TaskTargetKind.Group,
        GroupId = 5,
        TaskType = ScheduledTaskType.Check,
        CronExpression = "0 3 * * 0",
        Enabled = true
    };

    [Fact]
    public async Task Create_Backup_Task_Persists()
    {
        var t = await _sut.CreateAsync(BackupTask());

        Assert.True(t.Id > 0);
        var fetched = await _sut.GetAsync(t.Id);
        Assert.Equal(TaskTargetKind.Backup, fetched!.TargetKind);
        Assert.Equal("c1", fetched.ContainerName);
        Assert.Equal("0 2 * * *", fetched.CronExpression);
    }

    [Fact]
    public async Task Create_Group_Task_Persists()
    {
        var t = await _sut.CreateAsync(GroupTask());

        var fetched = await _sut.GetAsync(t.Id);
        Assert.Equal(TaskTargetKind.Group, fetched!.TargetKind);
        Assert.Equal(5, fetched.GroupId);
    }

    [Fact]
    public async Task Create_Backup_Task_Missing_Target_Throws()
    {
        var t = BackupTask();
        t.ContainerName = null;
        await Assert.ThrowsAsync<ArgumentException>(() => _sut.CreateAsync(t));
    }

    [Fact]
    public async Task Create_Group_Task_Missing_GroupId_Throws()
    {
        var t = GroupTask();
        t.GroupId = null;
        await Assert.ThrowsAsync<ArgumentException>(() => _sut.CreateAsync(t));
    }

    [Fact]
    public async Task Create_Missing_Cron_Throws()
    {
        var t = BackupTask();
        t.CronExpression = "";
        await Assert.ThrowsAsync<ArgumentException>(() => _sut.CreateAsync(t));
    }

    [Fact]
    public async Task Update_Modifies_Cron_And_Enabled()
    {
        var t = await _sut.CreateAsync(BackupTask());

        var update = BackupTask();
        update.CronExpression = "30 4 * * *";
        update.Enabled = false;
        var result = await _sut.UpdateAsync(t.Id, update);

        Assert.NotNull(result);
        var fetched = await _sut.GetAsync(t.Id);
        Assert.Equal("30 4 * * *", fetched!.CronExpression);
        Assert.False(fetched.Enabled);
    }

    [Fact]
    public async Task Delete_Removes_Task()
    {
        var t = await _sut.CreateAsync(BackupTask());

        Assert.True(await _sut.DeleteAsync(t.Id));
        Assert.Null(await _sut.GetAsync(t.Id));
    }

    [Fact]
    public async Task List_Returns_All()
    {
        await _sut.CreateAsync(BackupTask());
        await _sut.CreateAsync(GroupTask());

        var all = await _sut.ListAsync();

        Assert.Equal(2, all.Count);
    }
}
