using AzureStorageBackup.Api.Data;
using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace AzureStorageBackup.Api.Tests;

public class GroupServiceTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly AppDbContext _db;
    private readonly GroupService _sut;

    public GroupServiceTests()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        _db = new AppDbContext(options);
        _db.Database.EnsureCreated();
        _sut = new GroupService(_db);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
        GC.SuppressFinalize(this);
    }

    private static List<GroupMember> Members() =>
        [new GroupMember { AccountId = 1, ContainerName = "c1" }];

    [Fact]
    public async Task Create_With_Members_Persists()
    {
        var g = await _sut.CreateAsync("group1", Members());

        Assert.True(g.Id > 0);
        var fetched = await _sut.GetAsync(g.Id);
        Assert.Equal("group1", fetched!.Name);
        Assert.Single(fetched.Members);
        Assert.Equal("c1", fetched.Members[0].ContainerName);
    }

    [Fact]
    public async Task Create_Without_Members_Throws() =>
        await Assert.ThrowsAsync<ArgumentException>(() => _sut.CreateAsync("empty", []));

    [Fact]
    public async Task Update_Replaces_Members()
    {
        var g = await _sut.CreateAsync("g", Members());
        var newMembers = new List<GroupMember>
        {
            new() { AccountId = 2, ContainerName = "c2" },
            new() { AccountId = 2, ContainerName = "c3" },
        };

        var updated = await _sut.UpdateAsync(g.Id, "renamed", newMembers);

        Assert.NotNull(updated);
        var fetched = await _sut.GetAsync(g.Id);
        Assert.Equal("renamed", fetched!.Name);
        Assert.Equal(2, fetched.Members.Count);
        Assert.DoesNotContain(fetched.Members, m => m.ContainerName == "c1");
    }

    [Fact]
    public async Task Delete_Removes_Group()
    {
        var g = await _sut.CreateAsync("g", Members());

        Assert.True(await _sut.DeleteAsync(g.Id));
        Assert.Null(await _sut.GetAsync(g.Id));
    }

    [Fact]
    public async Task List_Returns_All()
    {
        await _sut.CreateAsync("a", Members());
        await _sut.CreateAsync("b", Members());

        var all = await _sut.ListAsync();

        Assert.Equal(2, all.Count);
    }

    [Fact]
    public async Task Group_Members_Keep_The_Order_They_Were_Given()
    {
        // Not (AccountId, ContainerName) order: the saved order is the run order, and it is the operator's.
        var arranged = new List<GroupMember>
        {
            new() { AccountId = 2, ContainerName = "c" },
            new() { AccountId = 1, ContainerName = "a" },
            new() { AccountId = 1, ContainerName = "b" },
        };
        var created = await _sut.CreateAsync("arranged", arranged);

        var g = await _sut.GetAsync(created.Id);
        Assert.Equal(new[] { "c", "a", "b" }, g!.Members.Select(m => m.ContainerName).ToArray());
        Assert.Equal(new[] { 0, 1, 2 }, g.Members.Select(m => m.Position).ToArray());

        // Rearranged on update: the new order replaces the old one wholesale.
        await _sut.UpdateAsync(created.Id, "arranged", new List<GroupMember>
        {
            new() { AccountId = 1, ContainerName = "b" },
            new() { AccountId = 2, ContainerName = "c" },
            new() { AccountId = 1, ContainerName = "a" },
        });
        var after = await _sut.GetAsync(created.Id);
        Assert.Equal(new[] { "b", "c", "a" }, after!.Members.Select(m => m.ContainerName).ToArray());
        Assert.Equal(new[] { 0, 1, 2 }, after.Members.Select(m => m.Position).ToArray());
    }

    [Fact]
    public async Task A_Backup_Named_Twice_Keeps_Its_First_Place()
    {
        var created = await _sut.CreateAsync("twice", new List<GroupMember>
        {
            new() { AccountId = 1, ContainerName = "a" },
            new() { AccountId = 1, ContainerName = "b" },
            new() { AccountId = 1, ContainerName = "a" },
        });

        var g = await _sut.GetAsync(created.Id);
        Assert.Equal(new[] { "a", "b" }, g!.Members.Select(m => m.ContainerName).ToArray());
    }
}
