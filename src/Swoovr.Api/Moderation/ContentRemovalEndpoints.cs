using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Swoovr.Api.Accounts;
using Swoovr.Api.Connections;
using Swoovr.Contracts;
using Swoovr.Domain;

namespace Swoovr.Api.Moderation;
public static class ContentRemovalEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/admin/cases/{id:guid}/content-sources",(Guid id,ClaimsPrincipal user,AccountResolver accounts,ContentRemovalService removals,HttpContext context,CancellationToken token)=>
            Staff(user,accounts,context,token,async actor=>Results.Ok(await removals.SourcesAsync(actor,id,token)))).RequireAuthorization();
        app.MapGet("/api/admin/cases/{id:guid}/content-removals/{removal:guid}",(Guid id,Guid removal,ClaimsPrincipal user,AccountResolver accounts,ContentRemovalService removals,HttpContext context,CancellationToken token)=>
            Staff(user,accounts,context,token,async actor=>Results.Ok(await removals.EvidenceAsync(actor,id,removal,token)))).RequireAuthorization();
        app.MapPost("/api/admin/cases/{id:guid}/content-removals",(Guid id,ContentRemovalRequest request,ClaimsPrincipal user,AccountResolver accounts,ContentRemovalService removals,TimeProvider clock,HttpContext context,IHubContext<MessagesHub> hub,CancellationToken token)=>
            Staff(user,accounts,context,token,async actor=>
            {
                var result=await removals.RemoveAsync(actor,id,request,clock.GetUtcNow(),token);
                // Issue 586 (D444): the place is gone and the plan called off; both members get the card again at once.
                if(!result.AlreadyApplied&&request.Field=="meetup-venue"&&request.MessageId is{}carrier
                    &&await accounts.Db!.Messages.AsNoTracking().Where(m=>m.Id==carrier).Select(m=>m.MeetupId).SingleOrDefaultAsync(token) is{}placePlan)
                {await MessagingEndpoints.SendMeetupCardAsync(accounts.Services,accounts.Db,placePlan,clock.GetUtcNow(),token);return Results.Ok(result);}
                if(!result.AlreadyApplied&&request.Field=="message"&&request.MessageId is{}messageId)
                {
                    var message=await accounts.Db!.Messages.AsNoTracking().SingleAsync(m=>m.Id==messageId,token);
                    // PR 583 review: a meet-up keeps its card and controls; only the note reads "Removed by Swoovr.".
                    if(message.Kind==MessageKind.Meetup&&message.MeetupId is{}plan){await MessagingEndpoints.SendMeetupCardAsync(accounts.Services,accounts.Db,plan,clock.GetUtcNow(),token);return Results.Ok(result);}
                    var conversation=await accounts.Db.Conversations.AsNoTracking().SingleAsync(c=>c.Id==message.ConversationId,token);
                    // Reuse the existing typed upsert event; never describe staff removal as a member unsend.
                    foreach(var member in new[]{conversation.PairLow,conversation.PairHigh})
                        await BetaMessageAudience.SendAsync(accounts.Services,member,MessagingEndpoints.View(message,member,clock.GetUtcNow()),token);
                }
                return Results.Ok(result);
            })).RequireAuthorization();
    }
    private static async Task<IResult> Staff(ClaimsPrincipal user,AccountResolver accounts,HttpContext context,CancellationToken token,Func<Guid,Task<IResult>> action)
    {
        context.Response.Headers.CacheControl="no-store";
        var(current,denied)=await AdminEndpoints.StaffAsync(user,accounts,AccountRole.Support,token);
        if(denied is not null)return denied;
        try{return await action(current!.Account!.Id);}
        catch(ContentRemovalRefusal refusal){return AccountEndpoints.Problem(refusal.Status,refusal.Code,"That content cannot be accessed through this review.");}
    }
}
