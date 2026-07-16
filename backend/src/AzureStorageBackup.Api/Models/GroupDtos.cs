namespace AzureStorageBackup.Api.Models;

public record GroupMemberDto(int AccountId, string ContainerName);

public record GroupRequest(string Name, List<GroupMemberDto> Members);

public record GroupResponse(int Id, string Name, List<GroupMemberDto> Members, DateTimeOffset CreatedAt)
{
    public static GroupResponse From(Group g) => new(
        g.Id,
        g.Name,
        g.Members.Select(m => new GroupMemberDto(m.AccountId, m.ContainerName)).ToList(),
        g.CreatedAt);
}
