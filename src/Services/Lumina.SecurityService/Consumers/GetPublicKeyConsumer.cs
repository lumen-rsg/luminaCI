using Lumina.SecurityService.Data;
using Lumina.Shared.Events;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Lumina.SecurityService.Consumers;

public sealed class GetPublicKeyConsumer : IConsumer<GetPublicKey>
{
    private readonly SecurityDbContext _db;

    public GetPublicKeyConsumer(SecurityDbContext db)
    {
        _db = db;
    }

    public async Task Consume(ConsumeContext<GetPublicKey> context)
    {
        var fingerprint = context.Message.Fingerprint.Trim().ToUpperInvariant();
        var publicKey = await _db.SecurityKeys
            .Where(k => k.KeyId == fingerprint)
            .Select(k => k.PublicKey)
            .SingleOrDefaultAsync();

        await context.RespondAsync(new PublicKeyByFingerprint(fingerprint, publicKey));
    }
}
