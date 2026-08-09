using AzureStorageBackup.Api.Models;
using AzureStorageBackup.Api.Services;

namespace AzureStorageBackup.Api.Endpoints;

/// <summary>Backup group management endpoints (PRD 2.2).</summary>
public static class GroupEndpoints
{
    public static IEndpointRouteBuilder MapGroupEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/groups").WithTags("Groups");

        group.MapGet("/", async (IGroupService svc, CancellationToken ct) =>
            Results.Ok((await svc.ListAsync(ct)).Select(GroupResponse.From)));

        group.MapGet("/{id:int}", async (int id, IGroupService svc, CancellationToken ct) =>
        {
            var g = await svc.GetAsync(id, ct);
            return g is null ? Results.NotFound() : Results.Ok(GroupResponse.From(g));
        })
        .WithName("GetGroup");

        group.MapPost("/", async (GroupRequest req, IGroupService svc, CancellationToken ct) =>
        {
            if (req.Members is null || req.Members.Count == 0)
                return Results.BadRequest(new { error = "A group must contain at least one backup." });

            var created = await svc.CreateAsync(req.Name, ToMembers(req.Members), ct);
            return Results.CreatedAtRoute("GetGroup", new { id = created.Id }, GroupResponse.From(created));
        });

        group.MapPut("/{id:int}", async (int id, GroupRequest req, IGroupService svc, CancellationToken ct) =>
        {
            if (req.Members is null || req.Members.Count == 0)
                return Results.BadRequest(new { error = "A group must contain at least one backup." });

            var updated = await svc.UpdateAsync(id, req.Name, ToMembers(req.Members), ct);
            return updated is null ? Results.NotFound() : Results.Ok(GroupResponse.From(updated));
        });

        group.MapDelete("/{id:int}", async (int id, IGroupService svc, CancellationToken ct) =>
            await svc.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound());

        return app;
    }

    private static IEnumerable<GroupMember> ToMembers(IEnumerable<GroupMemberDto> dtos) =>
        dtos.Select(m => new GroupMember { AccountId = m.AccountId, ContainerName = m.ContainerName });
}
